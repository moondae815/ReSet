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
        public void Extract_CompoundAssignment_ShouldNotBeCollected()
        {
            // `SELECT @v += MAX(x)`도 SelectSetVariable로 담기지만 대상 칸은
            // `SELECT @v = MAX(x)`로 렌더된다(ExecutionSemanticsFacts) - 원문에 없는 문장이
            // 「수정 금지」 표에 실린다. 형제 둘(LoopVariableResetExtractor ·
            // NonAggregateAssignmentExtractor)이 같은 자리에서 거르는 것과 같은 규칙이다.
            // 코퍼스 영향은 0건이다 - 24개 객체의 SelectSetVariable 26건 중 복합 대입이
            // 0건임을 NonAggregateAssignmentExtractorTests의 코퍼스 테스트가 못박고 있고,
            // 그 분모는 이 추출기에도 그대로 적용된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    SELECT @v += MAX(ID) FROM dbo.TA
END";

            Assert.Empty(AggregateAssignmentExtractor.Extract(ddl));
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

        [Fact]
        public void Extract_MinWithGroupBy_ShouldReportPreviousValueRetainedNotNull()
        {
            // 리뷰 발견(수정 라운드 1): GROUP BY가 있으면 무결과 시 그룹이 0개이므로
            // 이 SELECT 자체가 0행을 돌려주고 대입이 일어나지 않는다 - NULL이 아니라
            // 변수가 대입 전 값을 그대로 유지한다. GROUP BY 없는 경우와 정반대다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v VARCHAR(8) = ''
    SELECT @v = MIN(ReqYMD) FROM dbo.T GROUP BY Grp
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("MIN", fact.Aggregate);
            Assert.DoesNotContain("NULL", fact.Sentence);
            Assert.Contains("이전 값", fact.Sentence);
            Assert.Contains("일어나지 않습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_CountWithGroupBy_ShouldReportPreviousValueRetainedNotZero()
        {
            // COUNT도 GROUP BY 앞에서는 예외가 아니다 - 그룹이 0개면 이 SELECT가
            // 0행이므로 대입 자체가 없다. 0이 들어간다는 주장은 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @n INT
    SELECT @n = COUNT(*) FROM dbo.T GROUP BY Grp
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("COUNT", fact.Aggregate);
            Assert.Contains("이전 값", fact.Sentence);
            Assert.Contains("일어나지 않습니다", fact.Sentence);
        }
    }
}
