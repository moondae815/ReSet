using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 이미 쌓인 명세서에서 정산 업무 인수인계 문서를 만든다.
    ///
    /// [왜 DB가 선택인가] 근거는 Spec.md 하나이고 상수 추출 보조로 쓰는 DDL 사본도
    /// raw/metadata.json에 있다. DB는 코드값의 우변(업무 의미)에만 쓰이며, 없으면
    /// 「의미 미상」으로 표기된 채 문서가 완주한다. 그 덕에 파이프라인 전체가
    /// DB 없이 테스트된다.
    ///
    /// [왜 L2가 없는가] PrdDerivationService와 같다 - 수렴하지 않는 루프를 새로 만드는
    /// 대신 단계마다 한 번 되돌리고, 남은 결함은 배너에 박아 사람 검토로 넘긴다.
    /// </summary>
    public sealed class SettlementPolicyService : ISettlementPolicyService
    {
        public const string RosterFileName = "settlement-process.md";
        public const string PolicyDirectoryName = "Policy";
        public const string PolicyFileName = "SettlementPolicy.md";
        public const string CodebookFileName = "settlement-codebook.json";

        /// <summary>
        /// 한 단계에 실리는 명세서 합계의 경고선.
        ///
        /// 코퍼스 14편의 합계가 421,121자다. 한 단계가 그 3할에 가까워지면 단계로
        /// 나눈 이득이 사라지므로 사람에게 알린다. 자동 분할은 하지 않는다 - 목차는
        /// 사람이 명부에 정한 것이고, 도구가 그것을 바꾸면 단계 완전성 검사의 기준이
        /// 흔들린다.
        /// </summary>
        public const int StageSpecCharWarningThreshold = 120_000;

        private readonly IAiService _aiService;

        public SettlementPolicyService(IAiService aiService) =>
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));

        public async Task<PolicyDerivationOutcome> GenerateAsync(
            string outputRoot,
            ICodeTableProfiler? profiler,
            string? effort,
            CancellationToken cancellationToken = default)
        {
            // 1. 재료 적재
            var targets = PolicyTargetDiscovery.Find(outputRoot);
            var sources = targets
                .Select(PolicyCorpusLoader.Load)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            if (sources.Count == 0)
            {
                throw new InvalidOperationException(
                    "명세서가 있는 분석 산출물이 없습니다. 개별 SP 분석을 먼저 수행하십시오.");
            }

            // 2. 명부 - 없으면 초안을 쓰고 멈춘다(덮어쓰지 않는다)
            var rosterPath = Path.Combine(outputRoot, RosterFileName);
            if (!File.Exists(rosterPath))
            {
                await File.WriteAllTextAsync(
                    rosterPath, SettlementProcessRosterDraft.Build(sources), cancellationToken);
                throw new PolicyRosterBlockedException(
                    new[]
                    {
                        new RosterDefect(
                            RosterDefectType.PlaceholderTitleRemaining, RosterFileName,
                            "명부 초안을 만들었습니다. 단계 이름과 순서를 채운 뒤 다시 실행하십시오."),
                    },
                    rosterPath);
            }

            var roster = SettlementProcessRosterParser.Parse(
                await File.ReadAllTextAsync(rosterPath, cancellationToken));

            // 명부 결함은 심각도를 가리지 않는다 - 어떤 결함이든 하나라도 있으면 중단한다.
            // 특히 StageTitleDuplicated를 대충 넘기면 MarkdownSectionLocator가 같은
            // 제목의 첫 헤딩만 찾아 뒤 단계의 규칙 표 전체가 조용히 증발한다.
            var rosterDefects = SettlementRosterReconciler.Reconcile(
                roster, sources.Select(s => s.Label).ToList());
            if (rosterDefects.Count > 0)
            {
                throw new PolicyRosterBlockedException(rosterDefects, rosterPath);
            }

            // 3. 코드값 사전
            var staged = roster.AllStagedProcedures().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stagedSources = sources.Where(s => staged.Contains(s.Label)).ToList();
            var codebook = SettlementCodebookBuilder.BuildLeftSide(stagedSources);

            var profilingRan = false;
            if (profiler is not null)
            {
                var dependencies = stagedSources.SelectMany(s => s.Dependencies).ToList();
                var tables = await profiler.ProfileAsync(dependencies, cancellationToken);
                codebook = SettlementCodebookBuilder.ApplyMatches(codebook, tables);
                profilingRan = true;
            }

            // 4. 단계별 서술 - 실패하면 그 단계만 교정 재호출 1회
            //
            // [단계별 근거 선별은 이 서비스의 책임이다] GeneratePolicyStageAsync는 넘겨받은
            // sources만 돌 뿐, 어느 SP가 어느 단계에 속하는지 모른다. 여기서 명부의
            // stage.Procedures로 걸러내지 않으면 모든 단계가 전체 코퍼스를 프롬프트에
            // 실어, 단계로 나눈 의미(§9 참조)가 사라지고 단계 2를 쓰면서 단계 1의
            // 명세서까지 인용할 여지가 생긴다.
            var specsByLabel = stagedSources.ToDictionary(
                s => s.Label, s => s.SpecMarkdown, StringComparer.OrdinalIgnoreCase);

            // [stageHeadings는 명부 제목을 변형 없이 이어 붙인 것이어야 한다]
            // PolicyDocumentParser·PolicyAttributionValidator 둘 다 MarkdownSectionLocator의
            // 정확 일치(exact: true) 경로로 이 헤딩을 찾는다. 대소문자를 고치거나
            // 추가로 Trim/정규화하면 두 검증기가 그 절을 못 찾아 단계 전체가 검사에서
            // 빠진다 - roster.Stages[i].Title은 파서가 "## " 접두만 벗기고 Trim한
            // 값이므로 여기서 다시 손대지 않는다.
            var stageHeadings = roster.Stages.Select(s => "## " + s.Title).ToList();
            var stageBodies = new List<string>();

            for (var i = 0; i < roster.Stages.Count; i++)
            {
                var stage = roster.Stages[i];
                var stageNumber = PolicySectionContract.EffectiveStageNumber(stage.Title, i);

                var stageSources = stage.Procedures
                    .Select(p => stagedSources.First(s =>
                        string.Equals(s.Label, p, StringComparison.OrdinalIgnoreCase)))
                    .Select(s => (s.Label, s.SpecMarkdown))
                    .ToList();

                var stageCodeValues = codebook.Entries
                    .Where(e => e.Procedures.Any(p => stage.Procedures.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    .ToList();

                var stageChars = stageSources.Sum(s => s.SpecMarkdown.Length);
                if (stageChars > StageSpecCharWarningThreshold)
                {
                    Log.Warning(
                        "정책 단계가 큽니다 - '{Stage}'의 명세서 합계 {Chars:N0}자 (경고선 {Threshold:N0}자). "
                        + "명부에서 이 단계를 더 잘게 나누면 서술 품질이 올라갑니다.",
                        stage.Title, stageChars, StageSpecCharWarningThreshold);
                }

                // 단일 헤딩만 넘기면(과거 시도) 유령 StageMissing은 사라지지만, 그
                // 대가로 PolicyDocumentParser.Parse 안의 stageIndex가 항상 0이 되어
                // 번호 없는 제목의 실효 단계 번호가 EffectiveStageNumber(heading, 0)
                // 으로 잘못 계산된다 - 명부의 두 번째 이후 단계가 번호를 안 붙이면
                // 늘 1로 계산되어, 옳게 S2-01을 낸 초안이 IdPrefixMismatch로 고발되고
                // 무조건 교정 재호출을 탄다(2026-09-06 Fix Round 1 리뷰로 재현).
                // 그래서 전체 stageHeadings로 검증해 이 단계가 자기 진짜 위치의
                // 인덱스를 받게 하고, 돌아온 결함에서 "이 단계가 아닌 다른 헤딩이
                // 없다"는 유령 StageMissing만 걸러낸다 - 위치 정보와 유령 제거를
                // 함께 얻는다.
                var body = await GenerateStageWithOneRepairAsync(
                    stageNumber, stage, stageSources, stageCodeValues,
                    stageHeadings, stageHeadings[i], specsByLabel,
                    effort, cancellationToken);

                stageBodies.Add(body);
                await WriteStagePartAsync(outputRoot, stageNumber, stage.Title, body, cancellationToken);
            }

            // 5. 개요와 조립
            var assembledStages = string.Join("\n\n", stageBodies);
            var overview = (await _aiService.GeneratePolicyOverviewAsync(
                roster.Stages.Select(s => s.Title).ToList(), assembledStages, effort, cancellationToken))
                .Content ?? "## 정산 업무 개요\n";

            var documentBody = PolicyDocumentAssembler.Assemble(overview, stageBodies, codebook, roster);

            // 6. 최종 검사와 배너
            var rules = PolicyDocumentParser.Parse(documentBody, stageHeadings);
            var defects = PolicyAttributionValidator
                .Validate(documentBody, stageHeadings, specsByLabel).Defects
                .Concat(PolicyDocumentChecks.CheckCodeValues(rules, codebook))
                .Concat(PolicyDocumentChecks.CheckProcedureCitationCoverage(rules, roster))
                .ToList();

            var translated = codebook.Entries.Count(e => e.Matches.Count > 0);
            var skippedShort = codebook.Entries.Count(e => !e.MatchEligible);
            var unmatched = codebook.Entries.Count - translated - skippedShort;

            var banner = PolicyReportBanner.Build(defects, translated, unmatched, skippedShort, profilingRan);

            var sourceStatusCounts = CountSourceStatuses(stagedSources);
            var document = VerificationDocumentFormatter.FormatUnverifiedDocument(
                banner + documentBody, sourceStatusCounts,
                _aiService.ProviderName, _aiService.ModelName, effort, DateTime.Now);

            // 7. 저장
            var policyDir = Path.Combine(outputRoot, PolicyDirectoryName);
            Directory.CreateDirectory(policyDir);

            var policyPath = Path.Combine(policyDir, PolicyFileName);
            await File.WriteAllTextAsync(policyPath, document, cancellationToken);

            var codebookPath = Path.Combine(policyDir, CodebookFileName);
            await File.WriteAllTextAsync(
                codebookPath,
                JsonSerializer.Serialize(codebook, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                cancellationToken);

            Log.Information(
                "정산 정책서 생성 완료 - 단계 {Stages}개, 결함 {Defects}건, 코드값 번역 {Translated}건",
                roster.Stages.Count, defects.Count, translated);

            return new PolicyDerivationOutcome(
                policyPath, codebookPath, defects, translated, unmatched, skippedShort, profilingRan);
        }

        /// <summary>
        /// 단계 하나를 생성하고, 귀속 결함이 있으면 한 번만 교정 재호출한다.
        /// 결함이 늘면 첫 초안을 지킨다 - 결함 수가 유일하게 비교 가능한 척도다
        /// (PrdDerivationService와 같은 규칙).
        ///
        /// [왜 전체 stageHeadings를 넘기고 걸러내는가] 단일 헤딩만 넘기면(과거 시도)
        /// PolicyDocumentParser.Parse 안의 stageIndex가 항상 0이 되어 번호 없는
        /// 제목의 실효 단계 번호가 틀어진다(EffectiveStageNumber(heading, 0)).
        /// 전체 목록으로 검증하면 이 단계가 진짜 인덱스를 받아 번호가 옳게 나오고,
        /// 대신 아직 조립되지 않은 형제 단계의 StageMissing이 함께 딸려 온다 -
        /// 그 유령만 <see cref="RelevantDefects"/>로 걸러낸다.
        /// </summary>
        private async Task<string> GenerateStageWithOneRepairAsync(
            int stageNumber,
            PolicyStage stage,
            IReadOnlyList<(string Label, string SpecMarkdown)> stageSources,
            IReadOnlyList<CodebookEntry> stageCodeValues,
            IReadOnlyList<string> stageHeadings,
            string currentHeading,
            IReadOnlyDictionary<string, string> specsByLabel,
            string? effort,
            CancellationToken cancellationToken)
        {
            var draft = (await _aiService.GeneratePolicyStageAsync(
                stageNumber, stage.Title, stageSources, stageCodeValues, null, effort, cancellationToken))
                .Content ?? string.Empty;

            var defects = RelevantDefects(draft, stageHeadings, currentHeading, specsByLabel);
            if (defects.Count == 0)
            {
                return draft;
            }

            Log.Information(
                "정책 단계 귀속 검사 미통과 - 단계 {Stage}, 결함 {Count}건. 교정 재호출 1회를 시도합니다.",
                stage.Title, defects.Count);

            var feedback = string.Join("\n", defects.Select(d => $"- [{d.RuleId}] {d.Message}"));
            var retry = (await _aiService.GeneratePolicyStageAsync(
                stageNumber, stage.Title, stageSources, stageCodeValues, feedback, effort, cancellationToken))
                .Content ?? string.Empty;

            var retryDefects = RelevantDefects(retry, stageHeadings, currentHeading, specsByLabel);
            return retryDefects.Count <= defects.Count ? retry : draft;
        }

        /// <summary>
        /// 단계 하나의 초안을 전체 stageHeadings로 검증하되, 다른(아직 조립되지 않은)
        /// 형제 단계의 StageMissing은 유령이므로 걸러낸다. 초안은 자기 단계의 H2 하나만
        /// 담고 있어 형제 헤딩은 찾아질 수가 없다 - 그 결여를 결함으로 세면 모든 단계가
        /// 무조건 교정 재호출을 타게 된다. 반면 이 단계 자신의 헤딩이 정말 빠졌다면
        /// (currentHeading에 대한 StageMissing) 그것은 진짜 결함이므로 남긴다.
        /// </summary>
        private static IReadOnlyList<PolicyDefect> RelevantDefects(
            string draft,
            IReadOnlyList<string> stageHeadings,
            string currentHeading,
            IReadOnlyDictionary<string, string> specsByLabel)
        {
            var validation = PolicyAttributionValidator.Validate(draft, stageHeadings, specsByLabel);
            return validation.Defects
                .Where(d => d.Type != PolicyDefectType.StageMissing || d.Subject == currentHeading)
                .ToList();
        }

        private static async Task WriteStagePartAsync(
            string outputRoot, int stageNumber, string title, string body, CancellationToken cancellationToken)
        {
            var stepsDir = Path.Combine(outputRoot, PolicyDirectoryName, "steps");
            Directory.CreateDirectory(stepsDir);

            var slug = string.Concat(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
            var path = Path.Combine(stepsDir, $"{stageNumber:D2}-{slug}.md");
            await File.WriteAllTextAsync(path, body, cancellationToken);
        }

        /// <summary>근거 명세서의 종료 상태를 센다. 상태 표기가 없으면 「알 수 없음」으로 센다.</summary>
        private static IReadOnlyDictionary<string, int> CountSourceStatuses(IReadOnlyList<PolicySource> sources)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var status = SpecHeaderReader.Read(source.SpecMarkdown).VerificationStatus ?? "알 수 없음";
                counts[status] = counts.TryGetValue(status, out var n) ? n + 1 : 1;
            }

            return counts;
        }
    }
}
