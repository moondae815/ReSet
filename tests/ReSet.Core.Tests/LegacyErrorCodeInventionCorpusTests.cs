using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `CheckLegacyStepErrorCodeInvention`이 실물 코퍼스에서 무엇을 잡고 어디서
    /// 침묵하는지 잰다.
    ///
    /// [왜 필요한가] L1 실패는 보고가 아니라 되돌림이다
    /// (`VerificationPipelineOrchestrator.ComposeAfterL1Failure`) - 오탐 하나가 곧바로
    /// 재시도 소진이 된다. 그래서 새 검사는 켜기 전에 코퍼스에서 발화량을 재야 한다.
    /// `LocalVariableTableCorpusTests`가 같은 이유로 있는 자다.
    ///
    /// [침묵 분모를 함께 세는 이유] 이 검사는 재료를 못 얻으면 조용히 지나간다 -
    /// 명세에서 리터럴 코드가 안 나오는 SP가 실재하기 때문이다(오류를 음수 코드가
    /// 아니라 `ERROR_NUMBER()`로 내는 원본). 그 침묵은 옳지만, <b>발화 0을 깨끗함으로
    /// 읽는 것</b>이 이 저장소가 반복해서 데인 실패 양식이다
    /// (`rule-enforcement-census.md` §3, 검사 D가 그렇게 꺼졌다). 그래서 「재료를 얻어
    /// 판정한 단계」와 「재료가 없어 지나간 단계」를 나눠 찍는다.
    ///
    /// [왜 하한만 단언하는가] 숫자로 못박으면 코퍼스가 한 편 늘 때마다 빨개지고,
    /// 다음 사람이 관측을 읽는 대신 기대값을 고친다 - 이 저장소의 다른 코퍼스
    /// 테스트와 같은 근거다. 잡는 것은 「추출기가 조용히 망가져 전부 비는」 회귀다.
    ///
    /// [2026-08-30 기준선]
    /// <code>
    /// output              판정 194 · 재료 없어 지나감 1 · 발화 10  (오탐 0, 대입 지점 전량 확인)
    ///   -9    7건  원본이 정의한 적 없는 코드를 문서 전역 관례로 승격한 것
    ///   1000  3건  UP_UTIL_SETTLE_SUMMARY_ETC(0·1001·1002)에 없는 값. csharp 펜스 안이다
    /// </code>
    /// `1000`이 `csharp` 펜스에 있다는 것이 <c>CleanedCodeFencesExcludingDiagrams</c>가
    /// 필요한 이유의 실물이다 - <c>CleanedSqlFences</c>였다면 못 봤다.
    ///
    /// [알려진 오탐 계열 - 아직 안 닫았다] <c>RESET_SWEEP_ROOT</c>로 4단계 3차 통제군
    /// 트리를 재면 `POQSettleBatch3/S12`가 `-5`~`-8`을 발명으로 고발하는데 <b>오탐이다</b>.
    /// 그 회차의 `dbo.UP_Util_Settle_Summary` 명세는 그 넷을 「<c>`-5`를 반환합니다</c>」로
    /// 적고 <c>@po_intRetVal = -5</c> 철자를 쓰지 않아 <see cref="SpecReturnCodeExtractor"/>가
    /// 못 뽑는다(그 명세엔 기계 확정 표도 없어 두 번째 오라클도 못 덮는다).
    /// 현행 `output/`에도 같은 형태가 하나 남아 있다 - `UP_UTIL_SETTLE_COMM_UPD`의 `-15`.
    /// <b>[2026-08-30 정정 — 추출기를 넓히지 않기로 했다]</b> 앞 문단이 「서술형 패턴을
    /// 조이면 잡음이 0」이라고 적었는데 <b>틀렸다.</b> 그 패턴이 잡는 둘은 문법이 같아
    /// 가를 수 없다:
    /// <code>
    /// 진짜  「오류 시 롤백하고 `-5`를 반환합니다」            ← 활성 분기
    /// 죽은  「활성화될 경우 오류 코드 `-15`를 반환하도록」    ← 주석 처리된 블록
    /// </code>
    /// `UP_UTIL_SETTLE_COMM_UPD` 명세가 스스로 적는다 - 「본 프로시저의 활성 코드에는
    /// `-15` 오류 코드가 존재하지 않습니다」(`Spec.md:103`, `:609`, `:622`).
    ///
    /// 폭발 반경을 재니 대가가 그대로 나온다. <see cref="PlanStructureEnricher"/>는
    /// <c>ErrorCodes</c>를 <b>합집합</b>으로 채우고(`:247`), 하한 검사가 그 코드를 단계
    /// 본문에서 요구하며(`MechanicalValidator:422`), 그 실패는 되돌림이다.
    /// <code>
    /// output/              레거시 단계 195 · 더해질 코드 16 · 새 L1 실패 15  ← 전부 죽은 `-15`
    /// output.bak-…batch4   레거시 단계  38 · 더해질 코드 22 · 새 L1 실패  8  ← S12/S13 귀속 문제
    /// </code>
    /// 그리고 <b>현행 명세 생성분에는 이 구멍이 없다</b> - 14편의 서술형 전용 코드는
    /// `-15`(죽음)와 `0`뿐이고, 진짜 서술형 코드(`-5`~`-8`)는 옛 생성분에만 있다.
    /// 같은 SP를 현행 명세는 `@po_intRetVal = -5`로 적는다.
    ///
    /// <b>그러므로 닫지 않는다.</b> 남는 노출은 「다음 명세 생성분이 다시 서술형으로
    /// 쓰는 경우」이고, 그때는 이 테스트가 찍는 <b>침묵 분모</b>가 아니라 <b>오탐 발화</b>로
    /// 나타난다 - 발화 목록의 코드가 원본 명세에 산문으로 있는지 한 번 열어 보면 갈린다.
    ///
    /// [RESET_SWEEP_ROOT] 다른 산출물 트리에 같은 자를 대 보는 창구다. 기본값 `output`.
    /// 예: <c>RESET_SWEEP_ROOT=output.bak-stage4-control-20260828 dotnet test --filter …</c>
    /// </summary>
    public class LegacyErrorCodeInventionCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public LegacyErrorCodeInventionCorpusTests(ITestOutputHelper output) => _output = output;

        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, SpecConditions> NoConditions =
            new Dictionary<string, SpecConditions>();

        [SkippableFact]
        public void CorpusFiringAndSilenceAreBothMeasured()
        {
            var root = RepoPaths.FindRepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root!, Environment.GetEnvironmentVariable("RESET_SWEEP_ROOT") ?? "output");
            var jobsDir = Path.Combine(outputRoot, "Jobs");
            var proceduresDir = Path.Combine(outputRoot, "Procedures");
            Skip.IfNot(Directory.Exists(jobsDir) && Directory.Exists(proceduresDir), CorpusSkip.Reason);

            // 재료는 프로덕션과 같은 손으로 만든다 - 여기서 사전을 직접 지으면 키 규약이
            // 갈라져도 테스트가 초록으로 남는다.
            var specs = Directory
                .GetDirectories(proceduresDir)
                .Select(d => (FileName: Path.GetFileName(d), Path: Path.Combine(d, "docs", "Spec.md")))
                .Where(s => File.Exists(s.Path))
                .Select(s => (s.FileName, Content: File.ReadAllText(s.Path)))
                .ToList();
            Skip.IfNot(specs.Count > 0, CorpusSkip.Reason);

            var codesByProcedure = SpecReturnCodeExtractor.Extract(specs);

            var validator = new MechanicalValidator();
            var judged = 0;      // 레거시 출신 + 재료 있음 = 실제로 판정된 단계
            var silent = 0;      // 레거시 출신인데 재료가 없어 지나간 단계
            var firings = new List<string>();

            foreach (var jobDir in Directory.GetDirectories(jobsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var planPath = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var docPath = Path.Combine(jobDir, "docs", "BatchMigrationPlan.md");
                if (!File.Exists(planPath) || !File.Exists(docPath)) continue;

                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(planPath));
                if (steps == null) continue;

                var sections = SplitStepSections(File.ReadAllText(docPath));

                foreach (var step in steps.Where(s => s.LegacyProcedures.Count > 0))
                {
                    if (!sections.TryGetValue(step.Code, out var body)) continue;

                    var hasMaterial = step.LegacyProcedures.Any(p =>
                        !string.IsNullOrWhiteSpace(p) &&
                        codesByProcedure.ContainsKey(StepSweepService.BareProcedureName(p)));

                    if (!hasMaterial) { silent++; continue; }
                    judged++;

                    var result = validator.ValidateBatchStep(
                        body, step, Array.Empty<string>(), NoConditions,
                        codesByProcedure: codesByProcedure);

                    foreach (var error in result.Errors.Where(e => e.Contains("원본에 없는 코드")))
                    {
                        firings.Add($"{Path.GetFileName(jobDir)}/{step.Code}: {error}");
                    }
                }
            }

            _output.WriteLine($"판정한 단계 {judged} · 재료가 없어 지나간 단계 {silent} · 발화 {firings.Count}");
            foreach (var f in firings) _output.WriteLine("  " + f);

            // 추출기가 통째로 비면(회귀) judged가 0이 된다. 그 하나만 못박는다.
            Assert.True(judged > 0,
                $"레거시 출신 단계 중 재료를 얻은 것이 하나도 없다 - 명세 추출이 꺼졌을 수 있다 " +
                $"(재료 없이 지나간 단계 {silent}).");
        }

        /// <summary>
        /// `### S01 …`부터 다음 단계 머리글까지를 그 단계의 본문으로 본다. 같은 코드가
        /// 두 번 나오면(재작성 이력) 이어 붙인다 - 어느 쪽이 최종본인지 이 자리에서
        /// 판정하지 않는다.
        /// </summary>
        private static Dictionary<string, string> SplitStepSections(string markdown)
        {
            var heads = System.Text.RegularExpressions.Regex
                .Matches(markdown, @"^#{3,4}\s*(?<code>S\d{2})\b",
                    System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => (Index: m.Index, Code: m.Groups["code"].Value))
                .ToList();

            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < heads.Count; i++)
            {
                var end = i + 1 < heads.Count ? heads[i + 1].Index : markdown.Length;
                var body = markdown[heads[i].Index..end];
                sections[heads[i].Code] = sections.TryGetValue(heads[i].Code, out var prior)
                    ? prior + body
                    : body;
            }

            return sections;
        }
    }
}
