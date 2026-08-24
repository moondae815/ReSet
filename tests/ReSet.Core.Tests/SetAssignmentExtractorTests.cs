using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SetAssignmentExtractorTests
    {
        [Fact]
        public void Extract_SimpleAssignment_ShouldKeepVariableAndExpressionVerbatim()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = @@ERROR
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal(4, fact.Line);
            Assert.Equal("@v", fact.Variable);
            Assert.Equal("@@ERROR", fact.Expression);
        }

        [Fact]
        public void Extract_SelfReferencingIncrement_ShouldKeepWholeExpression()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @c INT
    SET @c = @c + 1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@c", fact.Variable);
            Assert.Equal("@c + 1", fact.Expression);
        }

        [Fact]
        public void Extract_FunctionCallExpression_ShouldNotSummarise()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @d VARCHAR(8)
    SET @d = CONVERT(VARCHAR(8), GETDATE(), 112)
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Contains("CONVERT", fact.Expression);
            Assert.Contains("112", fact.Expression);
        }

        [Fact]
        public void Extract_SelectAssignment_ShouldNotBeCollected()
        {
            // 관할 경계다. `SELECT @v = ...`는 ScriptDom에서 SelectSetVariable이고
            // AggregateAssignmentExtractor(:104)·NonAggregateAssignmentExtractor(:75)가
            // 그 타입만 본다. 이 표가 그것까지 담으면 정본이 둘로 갈린다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SELECT @v = COUNT(*) FROM dbo.T
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_DeclareWithInitializer_ShouldNotBeCollected()
        {
            // DECLARE @v INT = 15는 DeclareVariableStatement다. 백로그 ④의 몫이라
            // 이 표는 담지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT = 15
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_MultipleAssignments_ShouldBeOrderedByLine()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT, @b INT
    SET @a = 1
    SET @b = 2
END";

            var facts = SetAssignmentExtractor.Extract(ddl);

            Assert.Equal(new[] { "@a", "@b" }, facts.Select(f => f.Variable).ToArray());
            Assert.True(facts[0].Line < facts[1].Line);
        }

        [Fact]
        public void Extract_StringLiteralWithInternalDoubleSpace_ShouldPreserveLiteralVerbatim()
        {
            // Fix Round 1 - Important 1. 리터럴 내부 공백은 "표 셀이 깨지는 것을 막는"
            // 개행 접기와 무관한 값의 일부다. 토큰 단위가 아니라 문자열 전체를 이어 붙인
            // 뒤 공백을 접으면 리터럴 안의 두 칸까지 한 칸으로 뭉개져 원문이 아닌 값을
            // 대입식으로 보고하게 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v VARCHAR(10)
    SET @v = 'a  b'
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("'a  b'", fact.Expression);
        }

        [Fact]
        public void Extract_AlignmentWhitespaceBetweenTokens_ShouldStillCollapseToSingleSpace()
        {
            // 리터럴 보존을 고치더라도 토큰 사이 정렬 공백은 여전히 한 칸으로 접혀야
            // 한다 - 이 접기가 원래 CollapseWhitespace를 둔 이유(표 셀 붕괴 방지)다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT
    SET @a = @a     +     1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@a + 1", fact.Expression);
        }

        [Fact]
        public void Extract_MultilineExpression_ShouldCollapseNewlineToSingleSpace()
        {
            // 개행이 든 식이 표 셀을 깨뜨리지 않아야 한다 - 토큰 단위 처리로 바꿔도
            // 유지되어야 하는 원래 계약.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT
    SET @a = @a +
        1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@a + 1", fact.Expression);
        }

        [Fact]
        public void Extract_SetInsideWhileBody_ShouldStillBeCollected_BecauseThisTableIsExhaustive()
        {
            // Fix Round 1 - Important 2. 클래스 docstring이 [LoopVariableResetExtractor와의
            // 관계] 문단에서 길게 정당화하는 바로 그 중복이다 - WHILE 본문 최상위 SET도
            // 이 표는 담는다(실행 의미 표와 층이 다르다: "어떤 대입이 있나" vs
            // "매 반복 다시 설정된다"). LoopVariableResetExtractorTests의
            // UP_UTIL_SETTLE_PROC_ETC:69 실측 모양을 그대로 쓴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
        SELECT @v_intID = ID FROM dbo.TA WITH(NOLOCK)
    END
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal(7, fact.Line);
            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal("0", fact.Expression);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract(null));
            Assert.Empty(SetAssignmentExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldCarryTheMachineConfirmedSuffix()
        {
            Assert.EndsWith(
                MachineConfirmedTables.HeadingSuffix,
                SetAssignmentExtractor.TableHeading);
        }
    }
}
