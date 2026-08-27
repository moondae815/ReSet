using System.Collections.Generic;
using System.Reflection;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 태스크 4 - 침묵 분모 열 개를 <see cref="StepSweepService.Sweep"/>(공개 API)을
    /// 통해 관측한다. 합성 <see cref="SweepJob"/>을 배선까지 함께 시험하므로
    /// <see cref="MechanicalValidator"/>의 internal 판정을 리플렉션으로 직접 부르는
    /// 것보다 강하다 - 배선(코드 사전 조회·bare name 정규화 등)이 깨져도 잡는다.
    ///
    /// [왜 여기서는 리플렉션을 안 쓰는가] 위의 <c>StepSweepSilenceDenominatorTests</c>
    /// (태스크 3)는 BuildSpecTargets 하나만 격리해서 보므로 리플렉션이 자연스럽다.
    /// 이 클래스는 배선 전체(Sweep → InjectSimulatedCodes → ResolveOrdinal/
    /// ResolveAnchoredStatements/StagingSources/ReadsOnlyStaging → SweepIndicators)를
    /// 보는 것이 목적이라 공개 API로 도는 것이 시험 대상에 더 가깝다.
    /// </summary>
    public class StepSweepSilenceDenominatorSweepTests
    {
        // 명세서 DML 범위 표 - specTargets = {"TSettleMst"}를 이 표에서 뽑는다.
        private const string SpecTargetingTSettleMst = @"
### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 | 술어 컬럼 | 조인 키 | GROUP BY | ORDER BY |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 1 | 10 | TSettleMst | YMD, PGNAME | — | — | — |
";

        // 조건 (B)의 코드 사전 - "-13" -> (Kind: UPDATE, Ordinal: 1)만 낳는다.
        private const string DdlWithOneUpdateCode = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD AND PGNAME = 'X';
    IF @@ERROR <> 0 SET @v_err = -13;
END";

        private static SweepJob JobWithStep(string stepMarkdown) => new(
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
            new Dictionary<string, string> { ["S01"] = stepMarkdown },
            new List<(string, string)> { ("dbo.UP_TEST", SpecTargetingTSettleMst) },
            new Dictionary<string, string> { ["dbo.UP_TEST"] = DdlWithOneUpdateCode },
            new Dictionary<string, string> { ["dbo.UP_TEST"] = "@pi_strYMD" });

        private static SweepIndicators SweepIndicatorsFor(string stepMarkdown) =>
            StepSweepService.Sweep(
                    new SweepInput(new List<SweepJob> { JobWithStep(stepMarkdown) }, new List<string>(), 0))
                .Indicators;

        // 스테이징(S06CancelSettle)을 먼저 채우고, 자기 대상(TSettleMst)을 FROM
        // 별칭(Y)으로 다시 읽으면서 그 스테이징을 조인하는 문장. 원천은 리더의
        // 자기참조 가드가 TSettleMst를 이미 뺐으므로 스테이징 하나뿐이다.
        private const string StagingExemptionCancelledMarkdown = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
INSERT INTO S06CancelSettle (YMD, PGNAME)
SELECT YMD, PGNAME FROM TSourceStaging WHERE Flag = 1;

UPDATE TSettleMst
SET UseState = 1
FROM TSettleMst AS Y
INNER JOIN S06CancelSettle AS S ON Y.YMD = S.YMD
WHERE Y.PGNAME = S.PGNAME;
```
";

        /// <summary>
        /// 계수 「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」를 실제로 센다.
        ///
        /// [왜 이 계수가 가장 중요한가] 2026-08-27 최종 리뷰가 잡은 Critical 이
        /// 정확히 이 자리다. 방어선 둘이 서로의 전제를 무너뜨려 검사 C 가 35
        /// 좌표에서 꺼졌는데 관측 변화가 0 이었다. 승격 후에도 이 값이 0 이면
        /// 그 방어가 도달하지 못한 것이고, 재지 않았다는 증거다.
        /// </summary>
        [Fact]
        public void StagingExemptionCancelledByOwnTarget_IsCounted()
        {
            var indicators = SweepIndicatorsFor(StagingExemptionCancelledMarkdown);

            Assert.Equal(1, indicators.StagingExemptionsCancelledByOwnTarget);
            Assert.Equal(0, indicators.StatementsReadingOnlyStaging);
            Assert.True(indicators.StatementsReadingOwnTarget >= 1);
            Assert.True(indicators.StatementsWithLineage >= 1);
            Assert.True(indicators.StagingSourceTotal >= 1);
        }

        // 대조군 - 자기 대상을 FROM 별칭으로 다시 읽지 않고 스테이징만 조인한다.
        // 위 테스트가 없으면 첫 단언이 "언제나 true"로도 통과한다.
        private const string StagingExemptionSurvivesMarkdown = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
INSERT INTO S06CancelSettle (YMD, PGNAME)
SELECT YMD, PGNAME FROM TSourceStaging WHERE Flag = 1;

UPDATE TSettleMst
SET UseState = 1
FROM S06CancelSettle AS S
WHERE TSettleMst.YMD = S.YMD AND TSettleMst.PGNAME = S.PGNAME;
```
";

        [Fact]
        public void StagingExemption_Survives_WhenStatementDoesNotReadItsOwnTarget()
        {
            var indicators = SweepIndicatorsFor(StagingExemptionSurvivesMarkdown);

            Assert.Equal(0, indicators.StagingExemptionsCancelledByOwnTarget);
            Assert.True(indicators.StatementsReadingOnlyStaging >= 1);
        }

        // 첫 문장은 코드 앵커(-13)가 DDL 사전과 일치해 서수로 해결되고, 둘째
        // 문장은 앵커가 아예 없어 해결되지 않는다.
        private const string AnchorResolutionMarkdown = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;

UPDATE TSettleMiss SET UseState = 2 WHERE YMD = @pi_strYMD;
```
";

        [Fact]
        public void AnchorsResolved_And_AnchorsUnresolved_AreCounted()
        {
            var indicators = SweepIndicatorsFor(AnchorResolutionMarkdown);

            Assert.Equal(1, indicators.AnchorsResolved);
            Assert.Equal(1, indicators.AnchorsUnresolved);
        }

        // 같은 코드(-13)를 두 UPDATE 문장에 붙였다 - 둘 다 (Kind=UPDATE,
        // Ordinal=1)로 개별 해결되지만 모호성 가드(둘 다 U-앵커가 없다)가 묶어서
        // 버린다. AnchorsDroppedForAmbiguity = 개별 해결 수(2) - 살아남은 수(0).
        private const string ReusedCodeMarkdown = @"### S01. 재사용

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
SET @v_currentStepId = -13;
UPDATE TSettleMst SET UseState = 2 WHERE YMD = @pi_strYMD;
```
";

        [Fact]
        public void AnchorsDroppedForAmbiguity_IsCounted()
        {
            var indicators = SweepIndicatorsFor(ReusedCodeMarkdown);

            Assert.Equal(2, indicators.AnchorsDroppedForAmbiguity);
            Assert.Equal(0, indicators.AnchorsResolved);
        }

        // EXISTS 하위질의의 WHERE에 있는 컬럼(YMD·Flag)이 하위 범위 술어로 잡힌다.
        private const string SubordinatePredicateMarkdown = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE TSettleMst
SET UseState = 1
WHERE YMD = @pi_strYMD
  AND EXISTS (
      SELECT 1 FROM TSettleDetail
      WHERE TSettleDetail.YMD = @pi_strYMD AND TSettleDetail.Flag = 1
  );
```
";

        [Fact]
        public void StatementsWithSubordinatePredicates_And_ColumnTotal_AreCounted()
        {
            var indicators = SweepIndicatorsFor(SubordinatePredicateMarkdown);

            Assert.Equal(1, indicators.StatementsWithSubordinatePredicates);
            Assert.Equal(2, indicators.SubordinatePredicateColumnTotal);
        }
    }

    /// <summary>
    /// 침묵 분모가 제품 규칙을 그대로 부르는지 본다.
    ///
    /// [왜 스윕이 아니라 여기서 보는가] 스윕은 코퍼스가 있어야 돌아 CI에서 건너뛴다.
    /// 이 가드는 합성 재료로 돌아 어디서나 빨개진다.
    ///
    /// [왜 리플렉션인가] BuildSpecTargets는 internal이고 이 프로젝트에는
    /// InternalsVisibleTo가 어디에도 없다(MechanicalValidatorTests의
    /// InvokeFirstStepRowCreationMessage·InvokeBatchRunRowCreationMessage와 같은
    /// 전례) - 그래서 테스트 조립체에서 직접 부를 수 없어 리플렉션으로 부른다.
    /// </summary>
    public class StepSweepSilenceDenominatorTests
    {
        [Fact]
        public void BuildSpecTargets_CollectsTargetTablesFromDmlRows()
        {
            var facts = new List<SpecStatementFacts>
            {
                SpecStatementFactsForTest("dbo.TSettleMst", "dbo.TSettleByTX"),
            };

            var targets = InvokeBuildSpecTargets(facts);

            Assert.Contains("dbo.TSettleMst", targets);
            Assert.Contains("dbo.TSettleByTX", targets);
        }

        private static HashSet<string> InvokeBuildSpecTargets(IEnumerable<SpecStatementFacts> facts)
        {
            var method = typeof(MechanicalValidator).GetMethod(
                "BuildSpecTargets", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (HashSet<string>)method!.Invoke(null, new object[] { facts })!;
        }

        /// <summary>
        /// DmlRows의 TargetTable만 채운 최소 재료. BuildSpecTargets가 읽지 않는
        /// 다른 칸(PredicateColumns·JoinKeys·GroupBy·OrderBy·SetTargets·
        /// LocalVariables)은 빈 값으로 둔다.
        /// </summary>
        private static SpecStatementFacts SpecStatementFactsForTest(params string[] targetTables)
        {
            var rows = new List<SpecDmlRow>();
            for (var i = 0; i < targetTables.Length; i++)
            {
                rows.Add(new SpecDmlRow(
                    "UPDATE",
                    i + 1,
                    (i + 1) * 10,
                    targetTables[i],
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>()));
            }

            return new SpecStatementFacts(
                rows,
                System.Array.Empty<SpecSetTarget>(),
                System.Array.Empty<SpecLocalVariable>());
        }
    }
}
