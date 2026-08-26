using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepClassifierTests
    {
        [Theory]
        [InlineData("S01 섹션이 `TSettleMst`에 대한 UPDATE를 8개만 담고 있습니다. 명세서 DML 범위 표는 15개를 확정합니다.", SweepCheck.A)]
        [InlineData("S01 섹션의 UPDATE 13(갱신 13) 문장에 명세서가 확정한 최상위 술어 컬럼 YMD이(가) 없습니다.", SweepCheck.B)]
        [InlineData("S01 섹션의 UPDATE 2(갱신 2) 문장이 명세서에 없는 술어 컬럼 USESTATE을(를) 씁니다.", SweepCheck.C)]
        [InlineData("S01 섹션이 `@v_cnt`을(를) 선언 없이 씁니다. 명세서 지역 변수 표는 이 변수의 타입을 `INT`으로 확정합니다.", SweepCheck.D)]
        [InlineData("S01 섹션이 `@v_err`을(를) `-13`로 초기화하고 CATCH에서 그 값을 `@po_intRetVal`로 돌려줍니다.", SweepCheck.E)]
        public void ClassifiesEachCheckByItsMessage(string message, SweepCheck expected)
        {
            Assert.Equal(expected, StepSweepClassifier.Classify(message));
        }

        // 미분류를 조용히 A로 접으면 검사 문구가 바뀐 날 집계가 틀린 채로 초록이 된다.
        [Fact]
        public void UnknownMessageIsUnclassifiedNotSilentlyBucketed()
        {
            Assert.Equal(
                SweepCheck.Unclassified,
                StepSweepClassifier.Classify("S01 섹션이 '### ' 헤딩으로 시작하지 않습니다."));
        }

        [Fact]
        public void NullOrEmptyMessageIsUnclassified()
        {
            Assert.Equal(SweepCheck.Unclassified, StepSweepClassifier.Classify(null));
            Assert.Equal(SweepCheck.Unclassified, StepSweepClassifier.Classify("   "));
        }

        [Fact]
        public void DescribeExtractsCoordinatesFromCheckBMessage()
        {
            const string message =
                "S07 섹션의 UPDATE 13(갱신 13) 문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 " +
                "YMD, PGNAME이(가) 없습니다. 명세서 DML 범위 표 UPDATE 13 행의 값은 `YMD, PGNAME`입니다 — ";

            var finding = StepSweepClassifier.Describe(
                "POQSettleBatch1", "S07", SweepCheck.B, SweepCondition.SimulatedCache17, message);

            Assert.Equal("UPDATE", finding.Kind);
            Assert.Equal(13, finding.Ordinal);
            Assert.Equal(new[] { "YMD", "PGNAME" }, finding.Items);
        }

        [Fact]
        public void DescribeExtractsCoordinatesFromCheckCMessage()
        {
            const string message =
                "S09 섹션의 UPDATE 2(갱신 2) 문장이 명세서에 없는 술어 컬럼 USESTATE을(를) 씁니다. " +
                "명세서 DML 범위 표 UPDATE 2 행의 최상위 술어 컬럼은 ";

            var finding = StepSweepClassifier.Describe(
                "POQSettleBatch1", "S09", SweepCheck.C, SweepCondition.SimulatedCache17, message);

            Assert.Equal("UPDATE", finding.Kind);
            Assert.Equal(2, finding.Ordinal);
            Assert.Equal(new[] { "USESTATE" }, finding.Items);
        }

        [Fact]
        public void Describe_NonUpdateKind_WithoutGloss_StillExtractsCoordinates()
        {
            var message =
                "S07 섹션의 INSERT 2 문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 UseState이(가) " +
                "없습니다. 명세서 DML 범위 표 INSERT 2 행의 값은 `UseState`입니다 — " +
                "이 컬럼이 빠지면 갱신 대상 행 집합이 원본과 달라집니다.";

            var finding = StepSweepClassifier.Describe(
                "POQSettleBatch1", "S07", SweepCheck.B, SweepCondition.AsIs, message);

            Assert.Equal("INSERT", finding.Kind);
            Assert.Equal(2, finding.Ordinal);
        }

        // 검사 A·D·E의 메시지에는 문장 좌표가 없다. 억지로 뽑아 채우면 없는 좌표가
        // 판정표에 실려 사람이 그 자리를 찾으러 간다.
        [Fact]
        public void DescribeLeavesCoordinatesEmptyForOtherChecks()
        {
            var finding = StepSweepClassifier.Describe(
                "J", "S01", SweepCheck.A, SweepCondition.AsIs,
                "S01 섹션이 `TSettleMst`에 대한 UPDATE를 8개만 담고 있습니다. 명세서 DML 범위 표는 15개를 확정합니다.");

            Assert.Null(finding.Kind);
            Assert.Null(finding.Ordinal);
            Assert.Empty(finding.Items);
        }

        // 위 테스트는 실제 검사 A 문구("섹션이 ...")로 가드를 확인하지만, 그 문구는
        // CoordinatePattern("섹션의 ...")과 애초에 안 맞으므로 가드를 지워도 이 테스트
        // 하나만으로는 통과해 버린다(가드가 죽지 않는 뮤테이션). 검사 A로 분류된
        // 메시지 안에 좌표 모양 조각이 실제로 있어도 가드가 막는지 직접 본다.
        [Fact]
        public void DescribeGuardBlocksCoordinateExtractionEvenWhenPatternWouldMatch()
        {
            var finding = StepSweepClassifier.Describe(
                "J", "S01", SweepCheck.A, SweepCondition.AsIs,
                "S01 섹션의 UPDATE 5(갱신 5) 문장 언급이 우연히 섞인 검사 A 메시지입니다.");

            Assert.Null(finding.Kind);
            Assert.Null(finding.Ordinal);
        }

        // 문구가 바뀌어 좌표를 못 뽑아도 발화 자체는 세어야 한다 - 집계까지 잃으면
        // 검사가 침묵한 것과 구분되지 않는다.
        [Fact]
        public void DescribeStillCountsWhenCoordinatesCannotBeParsed()
        {
            var finding = StepSweepClassifier.Describe(
                "J", "S01", SweepCheck.B, SweepCondition.SimulatedCache17,
                "문장에 명세서가 확정한 무언가가 없습니다");

            Assert.Equal(SweepCheck.B, finding.Check);
            Assert.Null(finding.Kind);
            Assert.Empty(finding.Items);
        }
    }
}
