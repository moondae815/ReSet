using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CaseBranchExtractorTests
    {
        [Fact]
        public void Extract_SearchedCase_ShouldKeepOperatorsVerbatim()
        {
            // UIF_SettleYMD 실측: 명세서가 엄격 초과(>)를 "비교해"로 뭉개 경계에서
            // 오프셋이 일주일 어긋났다. 조건은 원문 그대로여야 한다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("WHEN 1", facts[0].Ordinal);
            Assert.Contains(">", facts[0].Condition);
            Assert.Equal("ELSE", facts[1].Ordinal);
            Assert.Equal("(그 외 전부)", facts[1].Condition);
        }

        [Fact]
        public void Extract_SimpleCase_ShouldRecordTheInputExpressionInEachCondition()
        {
            const string ddl = @"
CREATE FUNCTION dbo.F(@p VARCHAR(2)) RETURNS INT AS
BEGIN
    RETURN CASE @p WHEN '02' THEN 2 WHEN '03' THEN 3 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains("@p", facts[0].Condition);
            Assert.Contains("'02'", facts[0].Condition);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(CaseBranchExtractor.Extract("CREATE FUNCTION ((("));
        }

        [Fact]
        public void Extract_CaseWithoutElse_ShouldNotSynthesizeAnElseRow()
        {
            // ELSE가 없는 CASE를 있는 것처럼 다루면 거짓 행이 된다 - ELSE가 없으면
            // 그 행을 내면 안 된다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN CASE WHEN 1 = 1 THEN 10 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("WHEN 1", fact.Ordinal);
            Assert.DoesNotContain(facts, f => f.Ordinal == "ELSE");
        }

        [Fact]
        public void Extract_NestedCase_ShouldAttributeEachBranchToItsOwnCaseOnly()
        {
            // 바깥 CASE의 THEN 안에 또 CASE가 있을 때, 안쪽 분기가 바깥 것으로
            // 잘못 귀속되거나 같은 분기가 두 번 실리면 안 된다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN 1 = 1 THEN (CASE WHEN 2 = 2 THEN 20 ELSE 21 END) ELSE 2 END
    RETURN @v
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            // 바깥 CASE: WHEN 1, ELSE. 안쪽 CASE: WHEN 1, ELSE. 합쳐서 4행 - 각
            // 분기가 정확히 한 번씩만 실린다.
            Assert.Equal(4, facts.Count);
            Assert.Equal(2, facts.Count(f => f.Ordinal == "WHEN 1"));
            Assert.Equal(2, facts.Count(f => f.Ordinal == "ELSE"));

            var outerWhen = facts.Single(f => f.Condition == "1 = 1");
            Assert.Equal("(CASE WHEN 2 = 2 THEN 20 ELSE 21 END)", outerWhen.Result);

            var innerWhen = facts.Single(f => f.Condition == "2 = 2");
            Assert.Equal("20", innerWhen.Result);

            var innerElse = facts.Single(f => f.Condition == "(그 외 전부)" && f.Result == "21");
            Assert.NotNull(innerElse);
        }

        [Fact]
        public void Extract_CaseInsideWhereClause_ShouldStillBeVisited()
        {
            // CASE는 SELECT 목록·SET뿐 아니라 WHERE 자리에도 나온다 - 방문 지점
            // 커버리지 확인.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT * FROM T1 WHERE Col1 = CASE WHEN 1 = 1 THEN 1 ELSE 2 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
        }

        [Fact]
        public void Extract_CaseInsideSelectList_ShouldStillBeVisited()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT CASE WHEN Col1 > 1 THEN 'a' ELSE 'b' END AS X FROM T1
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
        }
    }
}
