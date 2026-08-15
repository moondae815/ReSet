using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchInfraObjectCollectorTests
    {
        /// <summary>
        /// 클래스 문서 주석이 "접두사 정의를 단독 소유"한다고 주장한다. 예전에는 그
        /// 주장과 달리 ObjectRegex 리터럴과 Schemas 배열에 접두사가 따로 적혀 있어,
        /// 한쪽에만 새 접두사를 추가해도 컴파일이 통과했다 - 이 테스트는 Collect가
        /// Schemas에 있는 모든 스키마를 실제로 인식하는지 그 목록에서 직접 대조해
        /// 정규식이 배열과 갈라지면 여기서 걸리게 한다.
        /// </summary>
        [Fact]
        public void Collect_ShouldRecognizeEverySchemaListedInSchemas()
        {
            var plan = string.Join(
                " ", BatchInfraObjectCollector.Schemas.Select(schema => $"{schema}.SomeObject"));

            var result = BatchInfraObjectCollector.Collect(plan);

            foreach (var schema in BatchInfraObjectCollector.Schemas)
            {
                Assert.Contains($"{schema}.SomeObject", result.Names);
            }
        }

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
        public void Collect_ShouldReadThePlaceholderSpellingItsOwnOutputUses()
        {
            // 실측(POQSettleProc11): 계획서가 표에는 `batch_shadow.TSettleByTX_<RunId>_S11`을,
            // SQL에는 리터럴을 쓰자 목록에 `batch_shadow.TSettleByTX_`라는 잘린 이름이 올랐다.
            // 객체명 정규식이 `<`에서 멈추기 때문이다 - 접기 결과로 우리가 내보내는 바로 그
            // 표기(RunIdPlaceholder)를 정작 우리가 다시 읽지 못했다.
            var plan = "batch_shadow.TSettleByTX_<RunId>_S11";

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Equal(new[] { "batch_shadow.TSettleByTX_<RunId>_S11" }, result.Names);
        }

        [Fact]
        public void Collect_ShouldFoldEveryPlaceholderSpellingIntoTheSameEntry()
        {
            // 세 표기가 같은 테이블을 가리키는데 목록에 세 항목으로 오르면, 회차 0은
            // 존재하지 않는 테이블 두 개를 더 만든다.
            var plan = """
                batch_shadow.TSettleMst_<RunId>_S06,
                batch_shadow.TSettleMst_RunId_S06,
                batch_shadow.TSettleMst_Run_S06
                """;

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Equal(new[] { "batch_shadow.TSettleMst_<RunId>_S06" }, result.Names);
        }

        [Fact]
        public void Collect_ShouldNotSwallowMarkdownTagsThatFollowAnObjectName()
        {
            // `<RunId>`를 이름의 일부로 받아들이되 그 문을 아무 태그에나 열어 주면,
            // 테이블 셀의 `<br/>`가 객체명에 붙어 들어온다.
            var plan = "| `batch.BatchExecution<br/>` | 실행 저널 |";

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Equal(new[] { "batch.BatchExecution" }, result.Names);
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

        /// <summary>
        /// 실측(POQSettleProc10): 계획서가 배치 전용 스키마를 batch(214회)·poqbatch(144회)
        /// ·poqsettlebatch(94회) 세 이름으로 갈라 썼다. 수집기는 Schemas만 알므로
        /// bootstrap 회차의 "만들 객체" 목록에는 batch.* 24개만 들어갔고, 나머지
        /// 238건이 참조하는 객체는 아무도 만들지 않는 채 지시서가 나갔다.
        ///
        /// 객체명 조각은 보지 않는다 - dbo.TBatchLog는 batch 스키마와 무관한
        /// 업무 테이블이고, 이름에 batch가 들어갔다는 이유로 걸리면 안 된다.
        /// </summary>
        [Theory]
        [InlineData("poqbatch.usp_S04_DailyRateSnapshot", true)]
        [InlineData("poqsettlebatch.POQSettleSummaryStage", true)]
        [InlineData("SETTLE_POQ_DB.POQBatch.POQSettleStepRun", true)]
        [InlineData("batch.POQSettleRun", false)]
        [InlineData("batch_shadow.TSettleMst_Run_S03", false)]
        [InlineData("SETTLE_POQ_DB.batch.POQSettleCheckpoint", false)]
        [InlineData("dbo.TSettleMst", false)]
        [InlineData("dbo.TBatchLog", false)]
        [InlineData("TSettleMst", false)]
        [InlineData(null, false)]
        // 실측: 이 검사를 POQSettleProc10 S06에 처음 돌렸을 때 `BatchStepResult.LegacyRetVal`이
        // 걸렸다. 스키마가 아니라 C# 타입의 멤버 접근인데, 이름에 batch가 들어갔다는
        // 이유만으로 잡힌 것이다. 갈라진 스키마들(poqbatch·poqsettlebatch·POQBatch)은
        // 하나같이 batch로 끝나므로, 포함이 아니라 끝나는지를 본다.
        [InlineData("BatchStepResult.LegacyRetVal", false)]
        [InlineData("batchResult.Code", false)]
        public void IsNonCanonicalBatchObject_ShouldFlagBatchSchemasThatAreNotTheCanonicalOnes(
            string? name, bool expected)
        {
            Assert.Equal(expected, BatchInfraObjectCollector.IsNonCanonicalBatchObject(name));
        }
    }
}
