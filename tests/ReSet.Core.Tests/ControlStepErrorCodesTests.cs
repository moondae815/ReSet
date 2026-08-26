using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ControlStepErrorCodesTests
    {
        [Theory]
        [InlineData("S01", -9010)]
        [InlineData("S16", -9160)]
        [InlineData("S40", -9400)]
        [InlineData("s07", -9070)]
        public void BlockStart_ShouldDeriveFromTheStepNumber(string stepCode, int expected)
        {
            // 값을 보면 어느 단계에서 죽었는지 읽혀야 한다. 모델이 B160/B161로
            // 표현하려던 것과 같은 구조이고, 이쪽은 T-SQL INT에 그대로 들어간다.
            Assert.Equal(expected, ControlStepErrorCodes.BlockStart(stepCode));
        }

        [Theory]
        [InlineData("BOOTSTRAP")]
        [InlineData("S")]
        [InlineData("")]
        [InlineData(null)]
        public void BlockStart_WithoutAStepNumber_ShouldReturnNull(string? stepCode)
        {
            // 번호를 못 읽으면 발급하지 않는다. 임의의 값을 지어내면 그것이 곧
            // 이 설계가 없애려는 지어낸 어휘다.
            Assert.Null(ControlStepErrorCodes.BlockStart(stepCode));
        }

        [Fact]
        public void IsInBlock_ShouldAcceptTheWholeBlockAndRejectItsNeighbours()
        {
            Assert.True(ControlStepErrorCodes.IsInBlock("S16", -9160));
            Assert.True(ControlStepErrorCodes.IsInBlock("S16", -9169));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9159));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9170));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9));
        }

        [Fact]
        public void IsReserved_ShouldNotClaimAnyCodeTheLegacyCorpusUses()
        {
            // 코퍼스 전수 실측: 레거시 반환 코드는 -1 ~ -201이다. 대역이 그것을
            // 삼키면 원본 코드가 제어 코드로 오인된다.
            Assert.False(ControlStepErrorCodes.IsReserved(-201));
            Assert.False(ControlStepErrorCodes.IsReserved(-1));
            Assert.True(ControlStepErrorCodes.IsReserved(-9010));
            Assert.True(ControlStepErrorCodes.IsReserved(-9400));
        }
    }
}
