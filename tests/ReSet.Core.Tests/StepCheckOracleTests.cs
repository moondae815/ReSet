using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 고정 오라클 두 판에 검사를 걸어 <b>판정이 갈리는지</b> 잠근다.
    ///
    /// [왜 발화 수가 아니라 두 판인가] 발화 수와 통과 수는 활동이지 효력이 아니다. 결함이
    /// 있다고 감사가 판정한 판에서 발화하고, 현행 판에서 침묵해야 비로소 그 검사가
    /// 무언가를 가른다고 말할 수 있다.
    ///
    /// [왜 조용히 통과시키지 않는가] `if (없으면) return;`으로 두면 코퍼스가 없는
    /// 워크트리에서 단언이 한 줄도 안 돌면서 초록이 된다 - <see cref="CorpusSkip"/>가
    /// 기록한 2026-08-23 사고가 정확히 그것이고, 다른 세션의 parallel-sdd 실행이
    /// 그 통과를 믿었다. 그래서 Skip 으로 드러낸다. 완료 기준이 「건너뜀 0」이므로
    /// 심링크를 빠뜨리면 기준이 자동으로 실패한다.
    ///
    /// [왜 번들 합계가 아니라 S07.md 를 직접 잠그는가 - 회귀 2026-09-05 라운드 1]
    /// 최초 판은 `defectiveHits > 0`(번들 14개 파일의 발화 합계)만 잠갔다. 이 조건은
    /// 너무 느슨해서 실제 회귀를 가렸다 - 리뷰어가 파일별로 재보니 S07.md(감사가
    /// 🔴로 매긴 그 자리, `ConsistencyReport.md:138`)는 **0건**이었는데도 S08.md가
    /// 1건을 내 합계가 0보다 커서 초록이 됐다. 그래서 대표 자리(S07.md)를 직접
    /// 못박는다 - 다른 파일의 발화가 이 자리의 회귀를 가리지 못하게 한다.
    ///
    /// [왜 `> 0`이 아니라 `== 10`인가 - 라운드 2]
    /// 라운드 1은 S07 에서 17건을 냈지만, 리뷰어가 하나씩 갈라보니 **7건이 무관한
    /// 오탐**이었다(U1·U2·U3~U6·U12·U13·U17·U18 - 실제로는 자기 자리에 진짜 DML 이
    /// 있는 정상 완료 구간). 진짜 결함 10건(갱신 4·5·6·7·8·9·10·11·14·15)이
    /// `ConsistencyReport.md:138`의 "18개 갱신 중 10개의 상수·계수·부호·반올림
    /// 자릿수와 UDF 인자가 지시서에 없다"와 **정확히 일치한다** - 이 10은 우리가
    /// 만든 수가 아니라 감사가 독립적으로 센 수다. `> 0`은 S08 하나만으로도
    /// 통과하므로 S07 자신의 회귀(예: 10건 중 일부가 다시 죽는 것)를 잡지 못한다 -
    /// 그래서 정확한 수를 못박는다. 이 수가 흔들리면 코드가 아니라
    /// `ConsistencyReport.md:138`(감사)을 먼저 재확인해야 한다는 뜻이다.
    ///
    /// 실측(2026-09-05, 이 파일이 가진 알고리즘 그대로 <c>OmissionCommentScanner.Scan</c>을
    /// 번들의 각 `*.md`와 현행 계획서에 파일 단위로 직접 돌려 잰 값. S08 은 감사가
    /// 별도로 낸 기준값이 없어 `> 0`만 둔다 - 총합 수치도 코드에 잠그지 않는다.
    /// 스캐너를 넓히면 늘 수 있고 그것은 개선이다):
    ///
    ///   S01~S06, S09~S16 → 0
    ///   S07               → 10   (감사 🔴 · ConsistencyReport.md:138의 「18개 중 10개」와 정확히 대응)
    ///   S08               → 11   (기준값 없음 - U1·U2·U4·U5·U7·U8·U9·U10·U13·U14·U15)
    ///   번들 합계          → 21
    ///   현행 판            → 0
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_FiresExactlyOnAuditedDefectCount_S07()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var s07Path = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps", "S07.md");
            Skip.If(!File.Exists(s07Path), CorpusSkip.Reason);

            var s07Hits = OmissionCommentScanner.Scan(File.ReadAllText(s07Path)).Count;

            // 10은 우리가 만든 수가 아니라 감사가 독립적으로 센 수다
            // (ConsistencyReport.md:138, "18개 갱신 중 10개"). 이 값이 어긋나면
            // 먼저 그 감사 기록을 다시 확인하라 - 코드를 임의로 조정하지 마라.
            Assert.Equal(10, s07Hits);
        }

        [SkippableFact]
        public void OmissionScanner_IsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var planPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(planPath), CorpusSkip.Reason);

            var currentHits = OmissionCommentScanner.Scan(File.ReadAllText(planPath)).Count;

            Assert.Equal(0, currentHits);
        }

        [SkippableFact]
        public void OmissionScanner_FiresAcrossDefectiveBundle()
        {
            // 번들 전체 합계는 대표 자리(S07)를 대신하지 못한다(위 클래스 주석 참고) -
            // 그래도 "검사가 결함 판 전체에서 무엇도 못 찾는" 완전 실효는 이 시험이
            // 잡는다.
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var bundleDir = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps");

            var defectiveHits = Directory.GetFiles(bundleDir, "*.md")
                .Sum(f => OmissionCommentScanner.Scan(File.ReadAllText(f)).Count);

            Assert.True(defectiveHits > 0,
                $"결함 판 전체에서 발화하지 않았다 - 검사가 무엇도 가르지 못한다 (발화 {defectiveHits})");
        }

        /// <summary>
        /// Fix Round 1 Critical(2026-09-05) 회귀 잠금 - 리뷰어가 실물 코퍼스에서 확인한
        /// 오탐 넷 중 <c>POQSettleProc1/S04</c>를 제외한 나머지 둘.
        ///
        ///   POQSettleProc3/agent/steps/S04.md:185-186
        ///   POQSettleProc9/agent/steps/S06.md:131-132
        ///
        /// 둘 다 `UP_UTIL_SETTLE_EXCEPTION_PROC` 갱신 1(`A.TxAmt - B.OrgDiscountAmt`)을
        /// 별칭만 `S`/`P`로 바꿔 정확히 구현했다. `POQSettleProc1/S04`는 별도 단위 시험
        /// (<c>SetExpressionCheck_StaysSilentWhenGeneratedAliasDiffersFromSpec_POQSettleProc1S04</c>)이
        /// 이미 잠갔으므로 여기서는 나머지 둘만 확인한다 - 넷째
        /// (<c>POQSettleProc1/S04.md:408-409</c>, 갱신 12 - PLCard 원천 DiscountFlag/Amt)도
        /// 같은 파일·같은 프로시저이므로 저 시험이 함께 잠근다(오류 0건이 그 갱신도
        /// 포함한 전체 SET 산식 오류 수이기 때문).
        /// </summary>
        [SkippableTheory]
        [InlineData("POQSettleProc3", "S04")]
        [InlineData("POQSettleProc9", "S06")]
        public void SetExpressionCheck_StaysSilentWhenGeneratedAliasDiffersFromSpec_AdditionalSites(
            string job, string code)
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var stepPath = Path.Combine(root, "output", "Jobs", job, "agent", "steps", $"{code}.md");
            var planStructurePath = Path.Combine(root, "output", "Jobs", job, "raw", "PlanStructure.md");
            var specPath = Path.Combine(
                root, "output", "Procedures", "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "docs", "Spec.md");
            Skip.If(!File.Exists(stepPath) || !File.Exists(planStructurePath) || !File.Exists(specPath),
                CorpusSkip.Reason);

            var steps = BatchStepPlanParser.TryParse(File.ReadAllText(planStructurePath));
            Skip.If(steps == null, "목차 JSON을 못 읽어 건너뜀 - raw/PlanStructure.md 형식을 확인하라");

            var step = steps!.FirstOrDefault(s => s.Code == code);
            Skip.If(step == null, $"{code} 단계가 목차에 없어 건너뜀");

            var facts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", File.ReadAllText(specPath)) });

            var markdown = File.ReadAllText(stepPath);
            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step!, System.Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(),
                stepInterfaces: null, runRowOwnedTables: null,
                statementFactsByProcedure: facts, allSteps: steps);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SET 산식") && e.Contains("갱신 1("));
        }

        /// <summary>
        /// A-2(2026-09-05) 오라클 - <c>CheckSpecSetExpressions</c>가 결함 판에서 발화하고
        /// 현행 판에서 침묵하는지 잠근다.
        ///
        /// [왜 파일 하나가 아니라 프로덕션 진입점을 다시 부르는가] <c>OmissionCommentScanner.Scan</c>
        /// 오라클과 달리 이 검사는 명세서 재료(<c>SpecStatementFacts.SetTargets</c>)와 단계
        /// 목차(<c>BatchStepPlan.LegacyProcedures</c>)를 함께 받아야 판정이 선다. 그래서
        /// <c>BatchStepPlanParser.TryParse</c>·<c>SpecStatementFactsExtractor.Extract</c>·
        /// <c>MechanicalValidator.ValidateBatchStep</c>을 그대로 불러 <c>SweepCommand</c>가
        /// 하는 조립을 이 시험 안에서 다시 한다 - 자체 토큰 채점기를 새로 짜면 그 채점기가
        /// 재는 것은 프로덕션 판정이 아니라 이 시험이 상상한 판정이 된다.
        ///
        /// [왜 명세서 재료가 판 공용인가] 결함 판(08-24 번들)·현행 판(09-04 문서) 둘 다
        /// `output/Procedures/*/docs/Spec.md`(현재 명세서)를 재료로 쓴다. 계획서 Task 2의
        /// 파이썬 하네스와 같은 설계다 - Jobs 트리만 두 판으로 가르고, SP 재생성의 영향을
        /// 받지 않는 Procedures 트리는 공용이다.
        ///
        /// [실측치의 출처] 판독 문서(2026-09-05-set-expression-token-readout-b1.md)의
        /// 결함판 10·현행판 0은 계획서의 파이썬 하네스(정규식으로 명세서 표를 직접 파싱)가
        /// 낸 값이다. 이 시험은 <b>같은 값을 C# 프로덕션 코드 경로로 독립적으로 재서
        /// 확인한다</b> - 실측(2026-09-05, 이 시험이 가진 알고리즘 그대로):
        /// 결함판 10 · 현행판 0. 두 하네스가 같은 값을 냈다(파이썬 대 C# 조기 대사가 갈리면
        /// 먼저 어느 쪽이 실제 파이프라인 규약(BareObjectName 조회·분할-SP 면제 등)을
        /// 놓쳤는지부터 의심하라 - 값을 맞추려고 코드를 바꾸지 마라).
        /// </summary>
        [SkippableFact]
        public void SetExpressionCheck_FiresOnDefectiveBundle_AndIsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var currentPlanPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(currentPlanPath), CorpusSkip.Reason);

            var procedureRoot = Path.Combine(root, "output", "Procedures");
            Skip.If(!Directory.Exists(procedureRoot), CorpusSkip.Reason);

            var specs = Directory.GetDirectories(procedureRoot)
                .Select(dir => (FileName: Path.GetFileName(dir), SpecPath: Path.Combine(dir, "docs", "Spec.md")))
                .Where(x => File.Exists(x.SpecPath))
                .Select(x => (x.FileName, Content: File.ReadAllText(x.SpecPath)))
                .ToList();
            Skip.If(specs.Count == 0, CorpusSkip.Reason);

            var facts = SpecStatementFactsExtractor.Extract(specs);

            var defectivePlanStructurePath = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "raw", "PlanStructure.md");
            var currentPlanStructurePath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "raw", "PlanStructure.md");
            Skip.If(!File.Exists(defectivePlanStructurePath) || !File.Exists(currentPlanStructurePath),
                CorpusSkip.Reason);

            var defectiveSteps = BatchStepPlanParser.TryParse(File.ReadAllText(defectivePlanStructurePath));
            var currentSteps = BatchStepPlanParser.TryParse(File.ReadAllText(currentPlanStructurePath));
            Skip.If(defectiveSteps == null || currentSteps == null,
                "목차 JSON을 못 읽어 건너뜀 - raw/PlanStructure.md 형식을 확인하라");

            var defectiveStepsDir = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps");
            var defectiveHits = defectiveSteps!.Sum(step =>
            {
                var stepPath = Path.Combine(defectiveStepsDir, $"{step.Code}.md");
                var markdown = File.Exists(stepPath) ? File.ReadAllText(stepPath) : null;
                return CountSetExpressionHits(markdown, step, defectiveSteps, facts);
            });

            var currentSections = SplitCombinedPlanIntoStepSections(File.ReadAllText(currentPlanPath));
            var currentHits = currentSteps!.Sum(step =>
            {
                currentSections.TryGetValue(step.Code, out var markdown);
                return CountSetExpressionHits(markdown, step, currentSteps, facts);
            });

            // 10·0 은 우리가 만든 수가 아니라 판독 문서(위 클래스 주석)가 독립 하네스로
            // 잰 수다. 이 값이 어긋나면 먼저 그 판독 문서를 다시 확인하라.
            Assert.Equal(10, defectiveHits);
            Assert.Equal(0, currentHits);
        }

        /// <summary>
        /// Fix Round 1 Critical(2026-09-05) 회귀 잠금. 리뷰어가 실물 코퍼스에서 확인한
        /// 오탐 넷 중 하나 - <c>POQSettleProc1/agent/steps/S04.md:122-123</c>.
        ///
        /// `UP_UTIL_SETTLE_EXCEPTION_PROC`의 명세서 갱신 1 산식은
        /// `A.TxAmt - B.OrgDiscountAmt`(별칭 A/B)인데, 생성본은 별칭을 `S`/`P`로 골라
        /// `S.TxAmt - P.OrgDiscountAmt`로 <b>정확히 구현했다.</b> 별칭 문자 차이만으로
        /// "SET 산식을 담지 않았습니다"가 발화하면 정확한 코드가 결함으로 고발된다 -
        /// <see cref="MechanicalValidator.ContainsSetExpressionToken"/>이 이 자리를
        /// 침묵시켜야 한다(그 메서드는 private이라 여기서 직접 부르지 않고, 이 시험은
        /// 실제 코퍼스 파일로 <c>ValidateBatchStep</c>을 불러 결과로 확인한다).
        /// </summary>
        [SkippableFact]
        public void SetExpressionCheck_StaysSilentWhenGeneratedAliasDiffersFromSpec_POQSettleProc1S04()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var stepPath = Path.Combine(root, "output", "Jobs", "POQSettleProc1", "agent", "steps", "S04.md");
            var planStructurePath = Path.Combine(root, "output", "Jobs", "POQSettleProc1", "raw", "PlanStructure.md");
            var specPath = Path.Combine(
                root, "output", "Procedures", "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "docs", "Spec.md");
            Skip.If(!File.Exists(stepPath) || !File.Exists(planStructurePath) || !File.Exists(specPath),
                CorpusSkip.Reason);

            var steps = BatchStepPlanParser.TryParse(File.ReadAllText(planStructurePath));
            Skip.If(steps == null, "목차 JSON을 못 읽어 건너뜀 - raw/PlanStructure.md 형식을 확인하라");

            var step = steps!.FirstOrDefault(s => s.Code == "S04");
            Skip.If(step == null, "S04 단계가 목차에 없어 건너뜀");

            var facts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", File.ReadAllText(specPath)) });

            var markdown = File.ReadAllText(stepPath);
            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step!, System.Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(),
                stepInterfaces: null, runRowOwnedTables: null,
                statementFactsByProcedure: facts, allSteps: steps);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SET 산식") && e.Contains("갱신 1("));
        }

        private static int CountSetExpressionHits(
            string? stepMarkdown,
            BatchStepPlan step,
            IReadOnlyList<BatchStepPlan> allSteps,
            IReadOnlyDictionary<string, SpecStatementFacts> facts)
        {
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return 0;

            var result = new MechanicalValidator().ValidateBatchStep(
                stepMarkdown, step, System.Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(),
                stepInterfaces: null, runRowOwnedTables: null,
                statementFactsByProcedure: facts, allSteps: allSteps);

            return result.Errors.Count(e => e.Contains("SET 산식"));
        }

        /// <summary>
        /// 현행 판(<c>BatchMigrationPlan.md</c>)은 단계별 파일이 아니라 한 문서에
        /// <c>### S01</c> 식 헤딩으로 이어 붙어 있다. <c>step_bodies_current</c>
        /// (계획서 파이썬 하네스, measure-set-expression-tokens.py)와 같은 방식으로
        /// 헤딩 경계로 자른다.
        /// </summary>
        private static Dictionary<string, string> SplitCombinedPlanIntoStepSections(string combinedMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(combinedMarkdown);
            var headings = new List<(int Index, string Code)>();
            for (var i = 0; i < lines.Count; i++)
            {
                var m = Regex.Match(lines[i], @"^###\s+(S\d\d)\b");
                if (m.Success)
                {
                    headings.Add((i, m.Groups[1].Value));
                }
            }

            var sections = new Dictionary<string, string>();
            for (var k = 0; k < headings.Count; k++)
            {
                var start = headings[k].Index;
                var end = k + 1 < headings.Count ? headings[k + 1].Index : lines.Count;
                sections[headings[k].Code] = string.Join("\n", lines.Skip(start).Take(end - start));
            }

            return sections;
        }
    }
}
