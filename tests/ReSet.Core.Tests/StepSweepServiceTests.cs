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

        // 리뷰 발견 (7) — 셀 안에 `|`가 있으면(예: 설정 대상이 `FLAGS | 4`처럼 비트
        // 연산 문자열이면) 표가 잘못 쪼개진다. ReadErrorCodeToOrdinal은 「문장」·「오류
        // 코드」 두 칸만 읽어 이 특정 손상을 지금은 관찰하지 못한다(손상은 그 뒤
        // 「설정 대상」 칸에서만 난다) - "현재 도달 불가"의 의미가 이것이다. 그래도
        // 실패 양식 자체가 나쁘다: 표가 깨지면 리더가 빈 사전이 아니라 틀린 사전을
        // 낼 수 있고 그건 조용하다. AiService.cs:1329가 쓰는
        // MarkdownTableCellCodec.Escape를 그대로 써서 렌더 시점에 막는다 - 그래서
        // 이 테스트는 렌더된 문자열 자체를 직접 본다.
        [Fact]
        public void RenderErrorCodeTableEscapesPipeCharactersInCells()
        {
            var facts = new List<ErrorCodeFact>
            {
                new("UPDATE", 1, "-13", "FLAGS | 4"),
            };

            var rendered = StepSweepService.RenderErrorCodeTable(facts);

            Assert.Contains(@"FLAGS \| 4", rendered);
            Assert.DoesNotContain("| FLAGS | 4 |", rendered);
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

        // 리뷰 발견 (3) — SweepCommand.cs:79와 StepSweepService.cs:252(위 Critical의
        // 자리)가 같은 실패 양식이다: 프로시저 참조를 못 찾으면 카운터 없이 continue한다.
        // DdlByProcedure를 비워 그 상태를 재현한다 - LegacyProcedures가 가리키는
        // "dbo.UP_TEST"가 어디에도 없다.
        [Fact]
        public void GapsCountsUnresolvedProcedureReferencesFromMissingDdlLookup()
        {
            var job = OneJobInput().Jobs[0] with
            {
                DdlByProcedure = new Dictionary<string, string>(),
            };

            var gaps = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Gaps;

            Assert.Equal(1, gaps.UnresolvedProcedureReferences);
        }

        // SweepCommand.cs:79(프로시저 디렉터리를 못 찾은 CLI 쪽 미해결)를 서비스가
        // 합산해서 실어야 한다 - 같은 필드에 두 실패 양식이 모인다.
        [Fact]
        public void GapsAddsCliReportedUnresolvedProcedureDirectoryLookups()
        {
            var input = new SweepInput(new List<SweepJob>(), new List<string>(), 0)
            {
                UnresolvedProcedureDirectoryLookups = 3,
            };

            var gaps = StepSweepService.Sweep(input).Gaps;

            Assert.Equal(3, gaps.UnresolvedProcedureReferences);
        }

        // 리뷰 발견 (4) — 측정 쌍이 0인 Job이 이름 없이 사라진다. StepSweepReportWriter의
        // 클래스 주석이 스스로 경고하는 함정("대상 범위가 줄면 개선처럼 보인다")이 여기서
        // 실제로 벌어진다 - 이 Job의 모든 단계가 markdown이 없어 측정 쌍이 0인데,
        // PlanParseFailedJobs에는 안 실린다(목차 파싱 자체는 성공했으므로).
        [Fact]
        public void GapsListsJobNamesWithZeroMeasuredPairs()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>(),
            };

            var gaps = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Gaps;

            Assert.Equal(new[] { "TestJob" }, gaps.JobsWithZeroMeasuredPairs);
        }

        // 측정 쌍이 있는 Job은 이 목록에 실리면 안 된다 - 실리면 정상 Job까지
        // "측정 0"으로 오인된다.
        [Fact]
        public void MeasuredJobIsNotListedAsZeroMeasuredPairs()
        {
            var gaps = StepSweepService.Sweep(OneJobInput()).Gaps;

            Assert.Empty(gaps.JobsWithZeroMeasuredPairs);
        }

        // 리뷰 발견 (7) — 한 Job이 던지면 326쌍 전체가 부분 보고 없이 죽는다. Job 단위
        // 가드로 감싸 다른 Job은 여전히 측정되고, 던진 Job의 이름은 HarnessGaps에
        // 남아야 한다(조용히 삼키면 "무엇을 못 쟀는지" 자체가 사라진다).
        [Fact]
        public void SweepContinuesPastAJobThatThrowsAndRecordsItInGaps()
        {
            var goodJob = OneJobInput().Jobs[0];
            var poisonJob = goodJob with { JobName = "PoisonJob", Steps = null! };

            var input = new SweepInput(
                new List<SweepJob> { poisonJob, goodJob }, new List<string>(), 0);

            var report = StepSweepService.Sweep(input);

            Assert.Equal(1, report.Gaps.MeasuredPairs);
            Assert.Equal(1, report.Gaps.MeasuredJobs);
            Assert.Contains("PoisonJob", report.Gaps.JobsThatThrew);
        }

        // 리뷰 발견 (6) — 목차 JSON은 정상 파싱되지만 상한(40단계) 초과로 버려진
        // Job은 "목차 파싱 실패"와 다른 라벨로 실려야 한다. SweepCommand가
        // SweepInput.StepCountCapExceededJobs로 채우면 Sweep이 그대로
        // HarnessGaps에 실어야 한다.
        [Fact]
        public void StepCountCapExceededJobsSurviveIntoTheReport()
        {
            var input = new SweepInput(new List<SweepJob>(), new List<string>(), 0)
            {
                StepCountCapExceededJobs = new[] { "POQSettleProc4 (선언 73단계)" },
            };

            var gaps = StepSweepService.Sweep(input).Gaps;

            Assert.Equal(
                new[] { "POQSettleProc4 (선언 73단계)" }, gaps.StepCountCapExceededJobs);
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

        // [N5] 잃은 조인 짝은 **발화시키지 않고 센다**. 이행이 결합을 CTE·파생
        // 테이블로 옮기는 관용구가 실재해 최상위만 보는 대조는 그 이전을 「잃었다」로
        // 읽는다 - 그 오탐은 SuggestedPromptFix 를 타고 재생성 프롬프트에 실려 재시도를
        // 소진시킨다. 그래도 **세지 않으면 안 보인다**(검사 D 가 18→0 으로 꺼졌는데
        // 아무도 몰랐던 자리와 같다). 다음 회차가 이 수를 보고 그 방향을 켤지 정한다.
        // 설계: docs/superpowers/specs/2026-09-05-n5-join-pair-design.md §2-5.
        [Fact]
        public void JoinPairsLostFromTheImplementationAreCountedButNotReported()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.UP_TEST
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    SET @po_intRetVal = -13
    UPDATE A
    SET    UseState = 1
    FROM   dbo.TSettleMst A
    JOIN   dbo.TClientRate B ON A.ClientID = B.ClientID
    WHERE  A.YMD = @pi_strYMD
END";

            // 이행은 그 결합을 통째로 잃었다 - 조인이 아예 없다.
            var step = "### S01. 갱신\n\n설명.\n\n```sql\n" +
                "/* U1: 상태 갱신 */\n" +
                "UPDATE A SET A.UseState = 1 FROM dbo.TSettleMst A WHERE A.YMD = @pi_strYMD;\n" +
                "```\n";

            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string> { ["S01"] = step },
                DdlByProcedure = new Dictionary<string, string> { ["dbo.UP_TEST"] = ddl },
            };

            var report = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0));

            Assert.Equal(1, report.Indicators.JoinPairsLostFromImplementation);
            Assert.DoesNotContain(report.Findings, f => f.Message.Contains("조인 짝"));
        }

        // 가드가 침묵시킨 대가의 크기. 코드 앵커가 둘 이상의 문장에 붙은 단계를 센다.
        [Fact]
        public void StepsWithReusedCodeAnchorsAreCounted()
        {
            var reused = "### S01. 재사용\n\n설명.\n\n```sql\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE dbo.TSettleMst SET UseState = 2 WHERE YMD = @pi_strYMD;\n" +
                "```\n";

            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string> { ["S01"] = reused },
            };

            var report = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0));

            Assert.Equal(1, report.Indicators.StepsWithReusedCodeAnchors);
        }

        [Fact]
        public void StepWithoutReusedCodeAnchorsIsNotCounted()
        {
            var indicators = StepSweepService.Sweep(OneJobInput()).Indicators;

            Assert.Equal(0, indicators.StepsWithReusedCodeAnchors);
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

        private const string DdlWithTwoDeclaresForCensus = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_intID INT;
    DECLARE @v_intCLTotal MONEY;
    SELECT @v_intID = 1;
END";

        private const string SpecWithoutLocalVariablesTable = @"# Spec

### 처리 개요

지역 변수 표가 없는 명세서다.
";

        // [왜 Sweep 이음매인가] 계수가 배선까지 함께 시험된다. 리플렉션으로 내부
        // 함수를 부르면 "계산은 맞는데 아무도 안 부른다"를 못 잡는다.
        //
        // 픽스처는 SpecMaterialCensusTests.Count_WhenDdlHasFactsButSpecHasNone_ReportsLossWithObjectName과
        // 같은 모양이다 - DDL은 DECLARE 둘, 명세서는 지역 변수 표가 없다. 이
        // 재료(LocalVariables)만 Spec·Ddl 양쪽을 실제로 세므로 소실이 관찰
        // 가능한 유일한 재료다(SpecMaterialCensus 클래스 문서 참고).
        [Fact]
        public void IndicatorsCarryMaterialCensusThroughSweep()
        {
            var job = new SweepJob(
                "CensusJob",
                new List<BatchStepPlan>(),
                new Dictionary<string, string>(),
                new[] { ("dbo.P", SpecWithoutLocalVariablesTable) },
                new Dictionary<string, string> { ["dbo.P"] = DdlWithTwoDeclaresForCensus },
                new Dictionary<string, string>());

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            var row = indicators.MaterialCensus.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(0, row.SpecRowCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }

        // Fix Round 1 Important(원래 서술) — SpecMaterialCensus.Count(input.Jobs)는
        // 이 파일이 이미 쓰는 per-job try/catch(jobsThatThrew) 밖에서 호출됐고, Count
        // 자신의 job 순회는 job.Specs/job.DdlByProcedure가 null이면 가드 없이
        // foreach를 돌아 NullReferenceException을 던졌다 - 그 예외가 StepSweepService의
        // 이음매 try/catch에 걸리면 이미 계산됐어야 할 나머지 Job의 census까지 통째로
        // 빈 목록이 됐다. 그 원인이었던 SpecMaterialCensus.cs가 Fix Round 2에서
        // 쓰기 집합 안으로 들어와 직접 고쳤다 - Minor(같은 파일 위쪽의
        // jobsThatThrew와 반대로 census는 per-job 가드가 없었다는 지적).
        //
        // [Fix Round 2 - 고친 뒤의 기대값] Count() 자신의 per-job try/catch가 poison
        // Job의 결함을 그 Job 하나로 가둔다. 그래서 이 테스트는 (1) Sweep이 죽지
        // 않고, (2) 정상 Job의 지표(MeasuredPairs)가 살아남고, (3) MaterialCensus는
        // 더 이상 통째로 비지 않고 goodJob의 데이터를 그대로 담고, (4) 이음매 밖
        // 가드(jobsThatThrew, 단계 검사 루프 쪽)가 poison Job 이름을 남기고, (5)
        // census 자신의 새 가드(JobsSkippedForFailure)도 같은 이름을 남긴다는 것을
        // 함께 단언한다.
        [Fact]
        public void MaterialCensusFailureDoesNotCrashSweepAndOtherIndicatorsSurvive()
        {
            var goodJob = OneJobInput().Jobs[0];
            var poisonJob = goodJob with { JobName = "CensusPoisonJob", DdlByProcedure = null! };

            var input = new SweepInput(
                new List<SweepJob> { poisonJob, goodJob }, new List<string>(), 0);

            var report = StepSweepService.Sweep(input);

            Assert.NotEmpty(report.Indicators.MaterialCensus);
            var localVariablesRow = report.Indicators.MaterialCensus
                .Single(r => r.MaterialName == "LocalVariables");
            // goodJob의 DdlOneUpdateWithCode 하나만 반영됐다는 뜻이다(poison Job이
            // 원자적으로 통째로 빠졌다 - 부분 반영이었다면 두 Job의 스펙/DDL이 섞여
            // 이 값이 달라질 수 있다). 값 자체(2)는 CountDeclaredVariables의 실측이다
            // - DdlOneUpdateWithCode 본문의 DECLARE는 하나(@v_err)지만, 괄호 없는
            // 프로시저 매개변수 선언(@pi_strYMD CHAR(8))도 DeclareVariableElement로
            // 잡힌다(SpecMaterialCensusTests가 별도로 잠그지 않는 파서 실측 동작 -
            // 이 테스트의 관심사는 그 수의 절대값이 아니라 poison Job이 하나도 안
            // 섞였다는 것이다).
            Assert.Equal(2, localVariablesRow.DdlFactCount);
            Assert.Contains("CensusPoisonJob", localVariablesRow.JobsSkippedForFailure);

            Assert.Equal(1, report.Gaps.MeasuredPairs);
            Assert.Contains("CensusPoisonJob", report.Gaps.JobsThatThrew);
        }
    }
}
