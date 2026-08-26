using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepServiceTests
    {
        private const string DdlWithTwoCodes = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT
AS
BEGIN
    DECLARE @v_err INT = 0;

    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;

    UPDATE dbo.TSettleMiss SET UseState = 2 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -14;
END";

        [Fact]
        public void SimulatedMapPairsEachCodeWithItsStatement()
        {
            var map = StepSweepService.BuildSimulatedErrorCodeMap(DdlWithTwoCodes, "@pi_strYMD");

            Assert.Equal(("UPDATE", 1), map["-13"]);
            Assert.Equal(("UPDATE", 2), map["-14"]);
        }

        // 제품 규칙(SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299)과 같아야 한다 -
        // 같은 코드가 두 문장에 붙으면 귀속할 수 없으므로 덮어쓰지 않고 아예 뺀다.
        [Fact]
        public void DuplicateCodeIsDroppedNotOverwritten()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.UP_DUP @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TA SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
    UPDATE dbo.TB SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
END";

            Assert.False(
                StepSweepService.BuildSimulatedErrorCodeMap(ddl, "@pi_strYMD").ContainsKey("-13"));
        }

        [Fact]
        public void EmptyOrUnparsableDdlYieldsEmptyMap()
        {
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap(null, "@pi_strYMD"));
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap("NOT SQL (((", "@pi_strYMD"));
        }

        // 왕복이 진짜 리더를 지나는지 확인한다. 헤딩이 어긋나면 리더가 표를 못 찾아
        // 빈 사전을 돌려준다 - 조용히 틀린 사전을 쓰는 대신 눈에 띄는 0이 된다.
        [Fact]
        public void RenderedTableUsesTheHeadingTheRealReaderLooksFor()
        {
            var rendered = StepSweepService.RenderErrorCodeTable(
                DmlScopeExtractor.ExtractErrorCodes(DdlWithTwoCodes, "@pi_strYMD"));

            Assert.Contains(DmlScopeExtractor.ErrorCodeTableHeading, rendered);

            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", rendered) });
            Assert.Equal(2, facts["UP_TEST"].ErrorCodeToOrdinal.Count);

            var broken = rendered.Replace(DmlScopeExtractor.ErrorCodeTableHeading, "### 오류 코드");
            var brokenFacts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", broken) });
            Assert.Empty(brokenFacts["UP_TEST"].ErrorCodeToOrdinal);
        }

        // 명세서: UPDATE 1은 TSettleMst를 YMD·PGNAME으로 필터한다고 확정한다.
        private const string SpecWithOneUpdateRow = @"
### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 | 술어 컬럼 | 조인 키 | GROUP BY | ORDER BY |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 1 | 10 | TSettleMst | YMD, PGNAME | — | — | — |
";

        // 단계 SQL: 코드 라벨(-13)은 있고 U-앵커는 없다. PGNAME 필터가 빠져 있다.
        private const string StepMarkdownMissingPgName = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
```
";

        private const string DdlOneUpdateWithCode = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD AND PGNAME = 'X';
    IF @@ERROR <> 0 SET @v_err = -13;
END";

        private static SweepInput OneJobInput() => new(
            new List<SweepJob>
            {
                new(
                    "TestJob",
                    new List<BatchStepPlan>
                    {
                        new("S01", "정산 마스터 갱신",
                            new List<string> { "dbo.UP_TEST" },
                            new List<string> { "TSettleMst" },
                            new List<string> { "-13" },
                            false,
                            new List<string>()),
                    },
                    new Dictionary<string, string> { ["S01"] = StepMarkdownMissingPgName },
                    new List<(string, string)> { ("dbo.UP_TEST", SpecWithOneUpdateRow) },
                    new Dictionary<string, string> { ["dbo.UP_TEST"] = DdlOneUpdateWithCode },
                    new Dictionary<string, string> { ["dbo.UP_TEST"] = "@pi_strYMD" }),
            },
            new List<string>(),
            0);

        // [이 테스트가 이 계획에서 가장 중요하다]
        // 조건 (B) 주입이 통째로 죽어도 "(A)와 (B)가 같다"는 그럴듯한 결과로 통과한다.
        // 코드 앵커만 있고 U-앵커가 없는 단계에서 (A)는 침묵하고 (B)는 발화해야 한다.
        [Fact]
        public void ConditionBFiresWhereConditionAIsSilent()
        {
            var report = StepSweepService.Sweep(OneJobInput());

            var asIs = report.Findings
                .Where(f => f.Condition == SweepCondition.AsIs && f.Check == SweepCheck.B);
            var simulated = report.Findings
                .Where(f => f.Condition == SweepCondition.SimulatedCache17 && f.Check == SweepCheck.B);

            Assert.Empty(asIs);
            Assert.Single(simulated);
            Assert.Equal("TestJob", simulated.Single().JobName);
            Assert.Equal("S01", simulated.Single().StepCode);
        }

        [Fact]
        public void GapsRecordMeasuredPairsAndNullInputs()
        {
            var gaps = StepSweepService.Sweep(OneJobInput()).Gaps;

            Assert.Equal(1, gaps.MeasuredPairs);
            Assert.Equal(1, gaps.MeasuredJobs);
            Assert.True(gaps.StepInterfacesWereNull);
            Assert.True(gaps.RunRowOwnedTablesWereNull);
        }

        [Fact]
        public void ParseFailedJobsAndMissingStepFilesSurviveIntoTheReport()
        {
            var input = new SweepInput(
                new List<SweepJob>(),
                new List<string> { "POQSettleProc4", "POQSettleProc7" },
                51);

            var gaps = StepSweepService.Sweep(input).Gaps;

            Assert.Equal(new[] { "POQSettleProc4", "POQSettleProc7" }, gaps.PlanParseFailedJobs);
            Assert.Equal(51, gaps.MissingStepFiles);
            Assert.Equal(0, gaps.MeasuredPairs);
        }

        // 목차에 있는데 마크다운이 없는 단계는 세지 않는다 - 빈 문자열을 넘기면
        // "섹션 내용이 비어있습니다"가 발화해 결손이 결함으로 둔갑한다.
        [Fact]
        public void StepWithoutMarkdownIsNotMeasured()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>(),
            };
            var input = new SweepInput(new List<SweepJob> { job }, new List<string>(), 1);

            var report = StepSweepService.Sweep(input);

            Assert.Equal(0, report.Gaps.MeasuredPairs);
            Assert.Empty(report.Findings);
        }
    }
}
