using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DdlStatementEnumeratorTests
    {
        [Fact]
        public void Enumerate_CreateProcedureBody_ShouldNotSwallowEverythingIntoOneLeaf()
        {
            // CreateProcedureStatement가 컨테이너로 잡히지 않으면 SP 본문 전체가
            // 잎 하나가 되어 맵이 통째로 무의미해진다. 이 계획이 설계서 목록에서
            // 빠진 것을 발견한 자리라 가드로 고정한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1 WHERE B = 2
    DELETE FROM dbo.U WHERE C = 3
END";

            var all = DdlStatementEnumerator.Enumerate(ddl);
            var leaves = DdlStatementEnumerator.Leaves(all);

            Assert.Equal(2, leaves.Count);
            Assert.Contains(leaves, s => s.StatementType == "UpdateStatement");
            Assert.Contains(leaves, s => s.StatementType == "DeleteStatement");
            Assert.Contains(all, s => s.StatementType == "CreateProcedureStatement" && s.IsContainer);
        }

        [Fact]
        public void Enumerate_IfWithTwoStatements_ShouldCountTwoLeavesNotOne()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    IF @x = 1
    BEGIN
        UPDATE dbo.T SET A = 1
        UPDATE dbo.T SET A = 2
    END
END";

            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));

            Assert.Equal(2, leaves.Count);
            Assert.All(leaves, s => Assert.Equal("UpdateStatement", s.StatementType));
        }

        [Fact]
        public void Enumerate_MultiLineInsertSelect_ShouldSpanToItsLastLine()
        {
            // 앵커는 문장 시작점만 지목한다. 끝줄이 맞아야 20줄짜리 INSERT가
            // 술어 행들을 한 덩어리로 끌어안는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.T (A, B)
    SELECT
        X.A,
        X.B
    FROM dbo.S AS X
    WHERE X.C = 1
END";

            var insert = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl))
                .Single(s => s.StatementType == "InsertStatement");

            Assert.Equal(3, insert.StartLine);
            Assert.Equal(8, insert.EndLine);
        }

        [Fact]
        public void Enumerate_NestedIf_ShouldReportNestingDepth()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    IF @x = 1
    BEGIN
        IF @y = 2
        BEGIN
            UPDATE dbo.T SET A = 1
        END
    END
END";

            var leaf = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl)).Single();

            // CREATE PROC > BEGIN..END > IF > BEGIN..END > IF > BEGIN..END = 6겹
            Assert.True(leaf.NestingDepth >= 4, $"깊이가 {leaf.NestingDepth}로 너무 얕다");
        }

        [Fact]
        public void Enumerate_SubqueryInWhere_ShouldStayOneLeaf()
        {
            // 하위 질의는 TSqlStatement가 아니라 QueryExpression이므로 잎을 쪼개지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1 WHERE B IN (SELECT C FROM dbo.S)
END";

            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));

            Assert.Single(leaves);
            Assert.Equal("UpdateStatement", leaves[0].StatementType);
        }

        [Fact]
        public void Enumerate_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(DdlStatementEnumerator.Enumerate("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Enumerate_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(DdlStatementEnumerator.Enumerate(null));
            Assert.Empty(DdlStatementEnumerator.Enumerate("   "));
        }
    }
}
