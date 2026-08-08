using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecReturnCodeExtractorTests
    {
        // 픽스처는 실측 명세서(output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md)의
        // 「로직 흐름 요약」 형태를 그대로 축약한 것이다. 이 형태에서 뽑히지 않으면
        // 실제 산출물에서도 뽑히지 않는다.
        private const string CommUpdSpec = @"## 로직 흐름 요약

1. `BEGIN TRAN`으로 트랜잭션을 시작합니다.
   - 오류 시 `@po_intRetVal = -1`을 설정하고 롤백합니다.

2. 해외카드 정상거래의 수수료를 계산합니다.
   - 오류 시 `@po_intRetVal = -2`를 설정하고 롤백합니다.

3. 취소거래의 금액 관련 컬럼을 `-1`배 처리합니다.
   - 대상은 `UseState IN (1,2,3)`인 행입니다.
   - 오류 시 `@po_intRetVal = -4`를 설정하고 롤백합니다.

> **문서 작성일시**: 2026-08-05 12:52:30
";

        private static (string, string)[] Specs(params (string, string)[] items) => items;

        [Fact]
        public void Extract_ShouldPullCodesFromReturnVariableAssignments()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_UTIL_SETTLE_COMM_UPD", CommUpdSpec)));

            Assert.Equal(new[] { "-1", "-2", "-4" }, result["up_util_settle_comm_upd"]);
        }

        [Fact]
        public void Extract_ShouldNotMistakeNarrativeNegativesForCodes()
        {
            // "`-1`배 처리합니다"의 -1과 날짜의 -05는 오류코드가 아니다.
            // 일반 음수 패턴으로 훑으면 이 둘을 코드로 오인한다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_UTIL_SETTLE_COMM_UPD", CommUpdSpec)));

            Assert.DoesNotContain("-05", result["up_util_settle_comm_upd"]);
            Assert.DoesNotContain("-08", result["up_util_settle_comm_upd"]);
        }

        [Fact]
        public void Extract_ShouldIgnoreOtherVariables()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "오류 시 `@v_currentStepId = -7`을 설정합니다.")));

            Assert.False(result.ContainsKey("up_x"));
        }

        [Fact]
        public void Extract_ShouldKeepFirstAppearanceOrderAndDedupe()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "`@po_intRetVal = -9` ... `@po_intRetVal = -1` ... `@po_intRetVal = -9`")));

            Assert.Equal(new[] { "-9", "-1" }, result["up_x"]);
        }

        [Fact]
        public void Extract_ShouldNotCreateKeyForSpecWithNoMatch()
        {
            // 빈 목록과 "그런 프로시저 없음"은 다른 사실이다. 빈 목록을 만들면
            // 보강기가 "코드가 없는 프로시저"로 오해한다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("Feedback_Log.txt", "이전 시도에 대한 검토 피드백")));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldKeyByBareNameLowercased()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("SETTLE_CARD_DB.dbo.UP_Mixed_Case", "`@po_intRetVal = -3`")));

            Assert.True(result.ContainsKey("up_mixed_case"));
        }

        [Fact]
        public void Extract_ShouldMergeDuplicateFileNames()
        {
            // 같은 프로시저가 두 번 실릴 일은 없어야 하지만, 들어와도 마지막 것이
            // 앞의 것을 조용히 덮어쓰면 코드가 사라진다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "`@po_intRetVal = -1`"), ("dbo.UP_X", "`@po_intRetVal = -2`")));

            Assert.Equal(new[] { "-1", "-2" }, result["up_x"]);
        }
    }
}
