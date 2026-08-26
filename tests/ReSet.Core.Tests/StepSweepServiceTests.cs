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

        // 단계 SQL: 코드 라벨이 둘(-13, -99)이다. SP 표(DdlOneUpdateWithCode)에는
        // -13 하나뿐이므로 -99는 단계 쪽에만 있는 여분 코드다 - 방향성 테스트 전용.
        private const string StepMarkdownWithExtraCode = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
SET @v_currentStepId = -99;
UPDATE dbo.TSettleMiss SET UseState = 2 WHERE YMD = @pi_strYMD;
```
";

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

        [Fact]
        public void MultiProcedureStepsAreCounted()
        {
            var job = OneJobInput().Jobs[0] with
            {
                Steps = new List<BatchStepPlan>
                {
                    new("S01", "둘", new List<string> { "dbo.UP_A", "dbo.UP_B" },
                        new List<string>(), new List<string>(), false, new List<string>()),
                    new("S02", "하나", new List<string> { "dbo.UP_A" },
                        new List<string>(), new List<string>(), false, new List<string>()),
                },
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownMissingPgName,
                    ["S02"] = StepMarkdownMissingPgName,
                },
            };

            var report = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0));

            Assert.Equal(1, report.Indicators.MultiProcedureSteps);
        }

        // UP_UTIL_SETTLE_COMM_UPD의 -9 소실 모양이다. SP 표에는 -13이 있는데
        // 단계 SQL은 -14만 단다 - 라벨이 밀렸다는 신호다.
        [Fact]
        public void CodeSetMismatchIsCountedInBothDirections()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownMissingPgName.Replace("-13", "-14"),
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(1, indicators.StepsMissingSpecCodes);   // 표의 -13이 단계에 없다
            Assert.Equal(1, indicators.StepsWithUnknownCodes);   // 단계의 -14가 표에 없다
        }

        // 리뷰 발견 Critical: step.LegacyProcedures는 원문이고 코퍼스의 43%(314개 중
        // 134개)가 스키마 접두사 없이 실린다. 반면 DdlByProcedure·DateParameterByProcedure는
        // 항상 디렉터리 이름("dbo.UP_TEST")으로 키잉된다(SweepCommand.cs:97-98). 조회가
        // 원문 그대로 TryGetValue를 쓰면 접두사 없는 단계에서 매번 빗나가 specCodes가
        // 조용히 빈 집합이 된다. 이 테스트는 그 상태를 재현한다 - LegacyProcedures는
        // 맨이름("UP_TEST")인데 DdlByProcedure의 키는 접두사가 있다("dbo.UP_TEST").
        //
        // SP 표에는 -13뿐이고 단계 SQL에는 -14뿐이다(라벨이 밀린 모양). 조회가
        // 정규화 없이 원문을 쓰면 specCodes가 항상 비어 StepsMissingSpecCodes가
        // 0으로 과소 집계된다(정답은 1) - 수정 전 코드에서 이 단언이 죽는 것을
        // 먼저 확인했다.
        [Fact]
        public void MissingSpecCodesFiresEvenWhenLegacyProcedureLacksSchemaPrefix()
        {
            var job = OneJobInput().Jobs[0] with
            {
                Steps = new List<BatchStepPlan>
                {
                    new("S01", "정산 마스터 갱신",
                        new List<string> { "UP_TEST" }, // 접두사 없음 - 코퍼스의 43% 모양
                        new List<string> { "TSettleMst" },
                        new List<string> { "-13" },
                        false,
                        new List<string>()),
                },
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownMissingPgName.Replace("-13", "-14"),
                },
                DdlByProcedure = new Dictionary<string, string>
                {
                    ["dbo.UP_TEST"] = DdlOneUpdateWithCode, // 디렉터리 이름 - 항상 접두사 있음
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(1, indicators.StepsMissingSpecCodes); // 표의 -13이 단계에 없다
            Assert.Equal(1, indicators.StepsWithUnknownCodes); // 단계의 -14가 표에 없다
        }

        // 비대칭 픽스처 - 단계 코드 집합이 SP 표 집합의 진부분집합이다(-13만 있고
        // 표에는 -13·-14가 있다). StepsMissingSpecCodes만 발화해야 하고
        // StepsWithUnknownCodes는 발화하면 안 된다. Except 방향이 뒤집히면
        // (0, 1)로 나와 이 단언이 죽는다.
        [Fact]
        public void StepCodesAreProperSubsetOfSpecCodes_OnlyMissingSpecCodesFires()
        {
            var job = OneJobInput().Jobs[0] with
            {
                DdlByProcedure = new Dictionary<string, string>
                {
                    ["dbo.UP_TEST"] = DdlWithTwoCodes,
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(1, indicators.StepsMissingSpecCodes);
            Assert.Equal(0, indicators.StepsWithUnknownCodes);
        }

        // 비대칭 픽스처 - 단계 코드 집합이 SP 표 집합의 진상위집합이다(단계에는
        // -13·-99가 있고 표에는 -13뿐이다). StepsWithUnknownCodes만 발화해야 하고
        // StepsMissingSpecCodes는 발화하면 안 된다. Except 방향이 뒤집히면
        // (1, 0)으로 나와 이 단언이 죽는다.
        [Fact]
        public void StepCodesAreProperSupersetOfSpecCodes_OnlyUnknownCodesFires()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownWithExtraCode,
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(0, indicators.StepsMissingSpecCodes);
            Assert.Equal(1, indicators.StepsWithUnknownCodes);
        }

        [Fact]
        public void MatchingCodeSetsCountAsNeitherMismatch()
        {
            var indicators = StepSweepService.Sweep(OneJobInput()).Indicators;

            Assert.Equal(0, indicators.StepsMissingSpecCodes);
            Assert.Equal(0, indicators.StepsWithUnknownCodes);
        }

        // 뮤테이션 발견: `if (stepCodes.Count == 0 && specCodes.Count == 0) continue;`를
        // 지워도 이 계획의 다른 세 테스트는 죽지 않는다 - 두 빈 집합의 Except는
        // 방향에 상관없이 항상 비어 있으므로 그 가드는 현재 코드에서 관찰 가능한
        // 차이를 만들지 않는다. 이 테스트도 그 가드 유무를 구분하지 못한다 -
        // 가드를 지워도 이 테스트는 여전히 통과한다. 그럼에도 "무재료는 어긋남이
        // 아니다"라는 계약을 테스트로 명시적으로 못박아 두는 것이 목적이다.
        [Fact]
        public void NoCodeMaterialOnEitherSideIsNotCountedAsMismatch()
        {
            const string stepMarkdownNoCodeAnchor = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
```
";
            const string ddlWithNoErrorCode = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD AND PGNAME = 'X';
END";

            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = stepMarkdownNoCodeAnchor,
                },
                DdlByProcedure = new Dictionary<string, string>
                {
                    ["dbo.UP_TEST"] = ddlWithNoErrorCode,
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(0, indicators.StepsMissingSpecCodes);
            Assert.Equal(0, indicators.StepsWithUnknownCodes);
        }

        // 리뷰 발견 A: 펜스 파싱 실패는 "코드 라벨 소실"이 아니라 "도구가 그
        // 관용구를 못 읽는다"는 신호다. 이 SQL은
        // StepSqlStatementReaderTests.GenuinelyUnparsableDmlStatement_CountsAsOneLostStatement가
        // 이미 lostStatementCount=1을 낸다고 확정한 것과 같은 문장이다 -
        // SELECT 목록이 통째로 주석이라 ScriptDom이 못 읽는다. 직접 실행해
        // 확인했다: StepSqlStatementReader.Read(markdown, out var lost)는
        // statements.Count=0, lost=1을 낸다.
        //
        // SP 표(DdlOneUpdateWithCode)에는 -13이 있으므로, 이 스킵이 없으면
        // stepCodes가 빈 채로 specCodes={-13}과 대조돼 StepsMissingSpecCodes가
        // 거짓으로 발화한다 - 이 테스트는 그 거짓 발화를 막는다.
        private const string StepMarkdownUnparsableFence = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
INSERT INTO dbo.T SELECT /* 주석뿐 */ FROM dbo.S;
```
";

        [Fact]
        public void StepWithUnparsableFenceIsSkippedNotCountedAsMismatch()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownUnparsableFence,
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(0, indicators.StepsMissingSpecCodes);
            Assert.Equal(0, indicators.StepsWithUnknownCodes);
            Assert.Equal(1, indicators.StepsSkippedForParseFailure);
        }
    }
}
