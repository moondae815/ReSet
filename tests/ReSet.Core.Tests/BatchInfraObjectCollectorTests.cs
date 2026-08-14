using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchInfraObjectCollectorTests
    {
        [Fact]
        public void Collect_ShouldFindObjectsInsideAndOutsideCodeFences()
        {
            // 실측: EXEC는 펜스 안에(steps/S13.md), 산문 언급은 펜스 밖에(steps/S17.md:17)
            // 있다. 한쪽만 보면 목록이 조용히 짧아진다.
            var plan = """
                `batch.SwitchPublishedPartition`은 대상 테이블을 제한한다.

                ```sql
                EXEC batch.BuildS13InSummary @RunId = @pi_runId;
                INSERT INTO batch_shadow.TSettleByOUT_Run_S13 SELECT * FROM x;
                ```
                """;

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Contains("batch.SwitchPublishedPartition", result.Names);
            Assert.Contains("batch.BuildS13InSummary", result.Names);
            Assert.Contains("batch_shadow.TSettleByOUT_<RunId>_S13", result.Names);
        }

        [Fact]
        public void Collect_ShouldCollapseRunIdLiteralVariantsIntoOneEntry()
        {
            // 실측: 같은 규칙(batch_shadow.<Table>_<RunId>_<StepCode>)의 자리표시자가
            // _RunId_ 와 _Run_ 두 리터럴로 굳었다. 접지 않으면 목록이 부풀고,
            // 회차 0이 존재하지 않는 테이블 두 개를 만든다.
            var plan = "batch_shadow.TSettleMst_RunId_S06 와 batch_shadow.TSettleMst_Run_S06";

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Equal(new[] { "batch_shadow.TSettleMst_<RunId>_S06" }, result.Names);
            Assert.Equal(2, result.CollapsedRunIdVariants.Count);
        }

        [Fact]
        public void Collect_ShouldIgnoreTheEnglishWordBatch()
        {
            var result = BatchInfraObjectCollector.Collect("the batch job runs nightly. batch processing.");

            Assert.Empty(result.Names);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Collect_ShouldReturnEmptyForBlankInput(string? plan)
        {
            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Empty(result.Names);
            Assert.Empty(result.CollapsedRunIdVariants);
        }

        [Theory]
        [InlineData("batch.POQSettleRun", true)]
        [InlineData("batch_shadow.TSettleMst_Run_S03", true)]
        [InlineData("SETTLE_POQ_DB.batch.POQSettleCheckpoint", true)]
        [InlineData("dbo.TSettleMst", false)]
        [InlineData("PaymentDB.dbo.TTxMst", false)]
        [InlineData("TSettleMst", false)]
        [InlineData(null, false)]
        public void IsInfraObject_ShouldRecognizeOnlyTheBatchSchemas(string? name, bool expected)
        {
            Assert.Equal(expected, BatchInfraObjectCollector.IsInfraObject(name));
        }
    }
}
