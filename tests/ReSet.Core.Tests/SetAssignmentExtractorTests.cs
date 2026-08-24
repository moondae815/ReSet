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
