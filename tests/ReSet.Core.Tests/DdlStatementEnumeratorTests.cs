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

            // 이 잎(UPDATE)을 품고 있는 문장은 정확히 6개다: CreateProcedureStatement,
            // 바깥 BeginEndBlockStatement(SP 본문), 바깥 IfStatement(IF @x=1),
            // 중간 BeginEndBlockStatement, 안쪽 IfStatement(IF @y=2), 안쪽
            // BeginEndBlockStatement(UPDATE를 직접 담은 블록). TSql160Parser로 고정돼
            // 있어 파서 버전 편차가 없으므로 느슨한 하한(>=4) 대신 정확한 값을 단언한다 -
            // 조상 하나를 덜 세거나 겹을 잘못 붙이는 회귀가 나도 하한 검사는 통과해
            // 놓친다.
            Assert.Equal(6, leaf.NestingDepth);
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
