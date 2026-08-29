using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LocalVariableDeclarationExtractorTests
    {
        [Fact]
        public void Extract_ShouldReturnNameTypeAndInitialValue()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    DECLARE @v_strClientID VARCHAR(20)
END";

            var facts = LocalVariableDeclarationExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("@v_intCLTotal", facts[0].Name);
            Assert.Equal("MONEY", facts[0].DataType);
            Assert.Equal("0", facts[0].InitialValue);
            Assert.Equal("@v_strClientID", facts[1].Name);
            Assert.Equal("VARCHAR(20)", facts[1].DataType);
            Assert.Equal("", facts[1].InitialValue);
        }

        [Fact]
        public void Extract_ShouldNotReturnProcedureParameters()
        {
            // 파라미터는 `## 파라미터 목록`의 매개변수 표가 담는다. 여기 섞이면 같은
            // 사실이 두 표에 실리고 둘이 갈릴 때 어느 쪽이 정본인지 알 수 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT, @pi_strYMD VARCHAR(8)
AS
BEGIN
    DECLARE @v_only INT
END";

            var names = LocalVariableDeclarationExtractor.Extract(ddl).Select(f => f.Name).ToList();

            Assert.Equal(new[] { "@v_only" }, names);
        }

        [Fact]
        public void Extract_ShouldNotReturnCursorOrTableVariables()
        {
            // 커서는 DeclareCursorStatement, 테이블 변수는 DeclareTableVariableStatement라
            // DeclareVariableElement가 아니다. 이 단언이 SpecMaterialCensus의 DDL 계수와
            // 같은 분모를 유지시킨다 - 갈리면 Task 7의 69 대조가 깨진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_scalar INT
    DECLARE @v_table TABLE (Col INT)
    DECLARE cur CURSOR FOR SELECT 1
END";

            var names = LocalVariableDeclarationExtractor.Extract(ddl).Select(f => f.Name).ToList();

            Assert.Equal(new[] { "@v_scalar" }, names);
        }

        [Fact]
        public void Extract_ShouldFoldRepeatedNamesKeepingTheFirst()
        {
            // SpecMaterialCensus가 HashSet(OrdinalIgnoreCase)로 세므로 접지 않으면
            // 두 계수가 갈린다. 첫 등장을 남긴다 - 원본에서 먼저 선언된 타입이 정본이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF 1 = 1
        BEGIN DECLARE @v_dup INT END
    ELSE
        BEGIN DECLARE @V_DUP MONEY END
END";

            var facts = LocalVariableDeclarationExtractor.Extract(ddl);

            Assert.Single(facts);
            Assert.Equal("INT", facts[0].DataType);
        }

        [Fact]
        public void Extract_WhenDdlDoesNotParse_ShouldReturnEmpty()
        {
            // 부분 파스 결과가 기계 확정 표에 섞이면 표 전체의 신뢰가 무너진다
            // (SetAssignmentExtractor와 같은 정책).
            var facts = LocalVariableDeclarationExtractor.Extract("CREATE PROCEDURE ((( AS");

            Assert.Empty(facts);
        }

        [Fact]
        public void Extract_WhenDdlIsNullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(LocalVariableDeclarationExtractor.Extract(null));
            Assert.Empty(LocalVariableDeclarationExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldUseTheSharedSuffix()
        {
            Assert.Equal(
                "### 지역 변수 " + MachineConfirmedTables.HeadingSuffix,
                LocalVariableDeclarationExtractor.TableHeading);
        }
    }
}
