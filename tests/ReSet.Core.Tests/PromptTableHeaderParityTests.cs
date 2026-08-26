using System;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 프롬프트 본문은 접두사 캐싱과 얽혀 있다 - 공유 접두사가 호출 N번에 걸쳐
    /// 바이트 단위로 같아야 한다. 헤더 행을 상수 조립으로 바꾸는 변경이 그 바이트를
    /// 건드리면 캐시가 깨진다. 이 테스트가 조립 결과를 옛 리터럴에 못박는다.
    ///
    /// 상수를 쓴다고 해서 프롬프트가 그것을 쓰는지는 별개다. 참조를 끊고 리터럴로
    /// 되돌리는 변경까지 막으려면 조립식 자체를 여기서 재현해 비교해야 한다.
    /// </summary>
    public class PromptTableHeaderParityTests
    {
        [Fact]
        public void TransactionBoundaryHeaderRow_ShouldRenderByteIdenticalToTheOldLiteral()
        {
            var composed =
                $"   | {string.Join(" | ", TransactionBoundaryExtractor.TableHeaderCells)} |";

            Assert.Equal("   | 라인 | 종류 | 이름 |", composed);
        }

        [Fact]
        public void TransactionBoundaryHeaderCells_ShouldBeTheThreeColumnsInRenderOrder()
        {
            Assert.Equal(
                new[] { "라인", "종류", "이름" },
                TransactionBoundaryExtractor.TableHeaderCells);
        }
    }
}
