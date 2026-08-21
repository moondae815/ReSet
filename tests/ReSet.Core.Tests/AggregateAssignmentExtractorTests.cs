using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class AggregateAssignmentExtractorTests
    {
        [Fact]
        public void Extract_MinAssignment_ShouldReportNullOnNoRows()
        {
            // UP_UTIL_SETTLE_INS_EXTRA 실측: 초기값 ''가 집계 대입에 덮여 NULL이 되고,
            // 이후 여덟 DML의 YMD >= @v 술어가 전부 UNKNOWN이 되어 0행이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_strReqYMD VARCHAR(8) = ''
    SELECT @v_strReqYMD = MIN(ReqYMD) FROM dbo.TExtraSettleIn WHERE ResultCode = '00'
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_strReqYMD", fact.Variable);
            Assert.Equal("MIN", fact.Aggregate);
            Assert.True(fact.HasInitializer);
            Assert.Contains("NULL", fact.Sentence);
            Assert.Contains("초기값", fact.Sentence);
        }

        [Fact]
        public void Extract_CountAssignment_ShouldReportZeroNotNull()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @n INT
    SELECT @n = COUNT(*) FROM dbo.T
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("COUNT", fact.Aggregate);
            Assert.Contains("0", fact.Sentence);
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_NonAggregateAssignment_ShouldBeIgnored()
        {
            // 비집계 대입은 무결과면 변수가 그대로 남는다 - 반대 의미라 담으면 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = c FROM dbo.T
END";

            Assert.Empty(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(AggregateAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }
    }
}
