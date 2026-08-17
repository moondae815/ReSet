using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class RoundingSemanticsExtractorTests
    {
        [Fact]
        public void Extract_ThreeArgumentRound_ShouldCaptureTheThirdArgument()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T
    SET    PGComm = ROUND(A.TxAmt * B.Rate / 100, 0, dbo.UF_GET_PGCommOption(A.PGName))
END";

            var calls = RoundingSemanticsExtractor.Extract(ddl);

            var call = Assert.Single(calls);
            Assert.Contains("UF_GET_PGCommOption", call.ThirdArgument);
        }

        [Fact]
        public void Extract_TwoArgumentRound_ShouldBeIgnored()
        {
            // 2인자 ROUND는 항상 반올림이므로 기술할 값 매핑이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT ROUND(1.5, 0)
END";

            Assert.Empty(RoundingSemanticsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(RoundingSemanticsExtractor.Extract(null));
        }

        [Fact]
        public void Extract_NestedRound_ShouldCaptureBothOuterAndInnerCalls()
        {
            // 실측(UP_UTIL_SETTLE_COMM_UPD.Procedure:63): 바깥 ROUND의 첫 인자가 또 다른
            // 3인자 ROUND다. 두 호출 모두 값 매핑을 필요로 하므로 둘 다 잡아야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT CAST(ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
END";

            var calls = RoundingSemanticsExtractor.Extract(ddl);

            Assert.Equal(2, calls.Count);
            Assert.Contains(calls, c => c.ThirdArgument == "Y.CommSumRoundFlag");
            Assert.Contains(calls, c => c.ThirdArgument == "Y.CommRoundFlag");
        }

        [Fact]
        public void Extract_RoundInsideLineComment_ShouldNotBeCaptured()
        {
            // 파서가 실제 AST를 보므로 주석 속 텍스트는 함수 호출로 파싱되지 않는다 -
            // 정규식 기반 추출이었다면 여기서 오탐을 냈을 것이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    -- 참고: ROUND(A.Amt, 0, B.Flag) 방식으로 계산했었음(폐기)
    SELECT 1
END";

            Assert.Empty(RoundingSemanticsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_RoundInsideStringLiteral_ShouldNotBeCaptured()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 'ROUND(A.Amt, 0, B.Flag)' AS Note
END";

            Assert.Empty(RoundingSemanticsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ThirdArgumentAsLiteral_ShouldCaptureTheLiteralText()
        {
            // 실측(UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure:167): 세 번째 인자가 UDF/컬럼이
            // 아니라 리터럴 1인 형태도 있다 - 코드가 이 모양도 놓치지 않아야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT ROUND(ABS(A.TXAMT) * (B.COMMISSIONRATE / 100), -1, 1)
END";

            var calls = RoundingSemanticsExtractor.Extract(ddl);

            var call = Assert.Single(calls);
            Assert.Equal("1", call.ThirdArgument);
        }
    }
}
