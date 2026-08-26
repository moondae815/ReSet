using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepReportWriterTests
    {
        private static SweepReport Report(params SweepFinding[] findings) => new(
            findings,
            new SweepIndicators(3, 2, 1),
            new HarnessGaps(
                new List<string> { "POQSettleProc4" }, 51, 326, 18,
                StepInterfacesWereNull: true,
                RunRowOwnedTablesWereNull: true,
                KnownTableNamesWereEmpty: true));

        // 결손을 안 실으면 줄어든 대상 범위가 개선처럼 보인다.
        [Fact]
        public void HeaderAlwaysCarriesHarnessGaps()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16");

            Assert.Contains("abc1234", markdown);
            Assert.Contains("16", markdown);
            Assert.Contains("POQSettleProc4", markdown);
            Assert.Contains("51", markdown);
            Assert.Contains("326", markdown);
            Assert.Contains("stepInterfaces", markdown);
            Assert.Contains("runRowOwnedTables", markdown);
        }

        // (B)가 상한이라는 사실을 보고서가 스스로 말해야 한다 - 재생성 후 실제
        // 발화량의 예측으로 읽히면 다음 사람이 잘못된 기대를 갖는다.
        [Fact]
        public void ReportStatesThatConditionBIsAnUpperBound()
        {
            Assert.Contains("상한", StepSweepReportWriter.Render(Report(), "abc1234", "16"));
        }

        [Fact]
        public void TalliesSplitByCheckAndCondition()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S02", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S01", SweepCheck.A, SweepCondition.AsIs, "m")),
                "abc1234", "16");

            Assert.Contains("| B | 0 | 2 |", markdown);
            Assert.Contains("| A | 1 | 0 |", markdown);
        }

        [Fact]
        public void AnchoredFindingsBecomeAJudgementTableWithAnEmptyVerdictColumn()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("POQSettleProc13", "S09", SweepCheck.B,
                        SweepCondition.SimulatedCache17, "m")
                    { Kind = "UPDATE", Ordinal = 3, Items = new[] { "PGNAME", "MALLID" } }),
                "abc1234", "16");

            Assert.Contains("POQSettleProc13", markdown);
            Assert.Contains("UPDATE 3", markdown);
            Assert.Contains("PGNAME, MALLID", markdown);
            Assert.Contains("판정", markdown);
        }

        [Fact]
        public void PerJobTableSplitsByJob()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J2", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m")),
                "abc1234", "16");

            Assert.Contains("| J1 | B | 0 | 1 |", markdown);
            Assert.Contains("| J2 | B | 0 | 1 |", markdown);
        }

        // 미분류가 0이 아니면 검사 문구가 바뀐 것이다. 표에 안 실으면 아무도 모른다.
        [Fact]
        public void UnclassifiedCountIsShown()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(new SweepFinding("J", "S01", SweepCheck.Unclassified, SweepCondition.AsIs, "m")),
                "abc1234", "16");

            Assert.Contains("미분류", markdown);
        }
    }
}
