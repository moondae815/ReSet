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
    }
}
