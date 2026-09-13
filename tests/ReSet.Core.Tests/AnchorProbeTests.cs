using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 앵커 프로브의 <b>순수 로직</b>. AI 호출도 코퍼스도 없이 돈다 - 그래야
    /// 「무엇을 재는가」가 「무엇을 생성했는가」와 갈린다.
    ///
    /// [이 시험이 코퍼스를 안 쓰는 이유] 프로브의 값은 판마다 달라진다. 판정 로직을
    /// 실물로 재면 시험이 모델 출력에 묶여, 생성이 나쁜 날 판정기까지 빨개진다.
    /// 실물 대조는 <see cref="SelectAnchorPairCorpusTests"/> 의 일이다.
    /// </summary>
    public class AnchorProbeTests
    {
        private static readonly string[] None = System.Array.Empty<string>();

        private static BatchStepPlan Step(string code, params string[] procedures) =>
            new(code, code + " 단계", procedures, None, None, false, None);

        private static SpecDmlRow Row(string kind, int ordinal) =>
            new(kind, ordinal, 0, "T", None, None, None, None);

        private static SpecStatementFacts FactsFor(params SpecDmlRow[] rows) =>
            new(rows, System.Array.Empty<SpecSetTarget>(), System.Array.Empty<SpecLocalVariable>());

        private static IReadOnlyDictionary<string, SpecStatementFacts> Facts(
            string procedure, params (string Kind, int Ordinal)[] rows) =>
            new Dictionary<string, SpecStatementFacts>
            {
                [procedure] = FactsFor(rows.Select(r => Row(r.Kind, r.Ordinal)).ToArray())
            };

        [Fact]
        public void SelectsOnlyStepsWhoseSpecDeclaresSelectRows()
        {
            // 프로브의 값은 호출 수다. SELECT 행이 없는 단계까지 생성하면 재지도 않을
            // 답에 돈을 쓴다 - 그리고 그 단계들이 분모에 들어가면 도달률이 희석된다.
            var steps = new List<BatchStepPlan> { Step("S01", "dbo.UP_A"), Step("S02", "dbo.UP_B") };
            var facts = new Dictionary<string, SpecStatementFacts>
            {
                ["UP_A"] = FactsFor(Row("SELECT", 1), Row("INSERT", 1)),
                ["UP_B"] = FactsFor(Row("UPDATE", 1))
            };

            var selected = AnchorProbe.SelectSteps(steps, facts);

            Assert.Single(selected);
            Assert.Equal("S01", selected[0].Step.Code);
            Assert.Equal(new[] { 1 }, selected[0].Declared.OrderBy(x => x));
        }

        [Fact]
        public void MatchesTheProcedureEvenWhenTheStepSpellsTheSchemaPrefix()
        {
            // 목차는 `dbo.UP_A` 로 적고 명세서 사실은 `UP_A` 로 색인된다. 접두사를
            // 안 벗기면 선별이 통째로 빈다 - 그리고 그 빈 결과는 「선언한 단계가
            // 없다」로 읽혀 조용히 통과한다.
            var selected = AnchorProbe.SelectSteps(
                new List<BatchStepPlan> { Step("S01", "dbo.UP_A") },
                Facts("UP_A", ("SELECT", 1)));

            Assert.Single(selected);
        }

        [Fact]
        public void JudgeReportsReachAndMismatchAndFoldedSeparately()
        {
            // 셋을 한 수로 뭉개면 처방이 안 갈린다. 도달 실패는 「안 달았다」,
            // 불일치는 「없는 서수를 지어냈다」, 접힘은 「달았는데 자리가 틀렸다」다.
            const string markdown = @"### S01 단계

```sql
-- SQL_SOURCE
/* SELECT 1: 원천 조회 */
SELECT 1;

-- SQL_GUARD (SELECT 9: 접어 넣은 앵커)
SELECT 2;
```
";

            var outcome = AnchorProbe.Judge("S01", new HashSet<int> { 1, 2 }, markdown);

            Assert.Equal(new[] { 1 }, outcome.Anchors);
            Assert.Equal(1, outcome.FoldedCount);
            Assert.Equal(new[] { 2 }, outcome.MissedDeclarations);
            Assert.Empty(outcome.FabricatedOrdinals);
        }

        [Fact]
        public void JudgeFlagsAnOrdinalTheSpecNeverDeclared()
        {
            // POQSettleBatch9/S08 이 이 모양이었다 - 계획이 새로 들인 가드 조회에
            // `SELECT 1` 을 달았고 그 SP 의 DML 범위 표에는 SELECT 행이 없었다.
            const string markdown = @"### S08 단계

```sql
-- SQL_CHECK_EXISTING
/* SELECT 1: 정산 데이터 존재 여부 확인 */
SELECT COUNT(1) FROM dbo.T;
```
";

            var outcome = AnchorProbe.Judge("S08", new HashSet<int>(), markdown);

            Assert.Equal(new[] { 1 }, outcome.FabricatedOrdinals);
        }

        [Fact]
        public void SummarizeCountsAStepAsReachedOnlyWhenItCarriesAnAnchor()
        {
            var reached = AnchorProbe.Judge("S01", new HashSet<int> { 1 },
                "```sql\n-- N\n/* SELECT 1: a */\nSELECT 1;\n```");
            var missed = AnchorProbe.Judge("S02", new HashSet<int> { 1 },
                "```sql\n-- N\nSELECT 1;\n```");

            var report = AnchorProbe.Summarize(new[] { reached, missed });

            Assert.Equal(2, report.StepsProbed);
            Assert.Equal(1, report.Reached);
            Assert.Equal(1, report.MissedDeclarationRows);
            Assert.Equal(0, report.FabricatedOrdinalCount);
            Assert.Equal(0, report.FoldedCount);
        }

        [Fact]
        public void SummarizeIsEmptyRatherThanGreenWhenNothingWasProbed()
        {
            // 표본 0 을 「도달률 100%」로 내면 재지 않은 판이 합격으로 읽힌다.
            var report = AnchorProbe.Summarize(new List<AnchorProbeStepOutcome>());

            Assert.Equal(0, report.StepsProbed);
            Assert.Equal(0, report.Reached);
        }
    }
}
