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

    public class LocalVariableDeclarationExtractorConstantsTests
    {
        // COMM_UPD 실물의 모양이다 - 상수 하나, 초기값 없는 것 하나,
        // SET 으로 재대입되는 누산기 하나, SELECT 로 재대입되는 것 하나.
        private const string Ddl = @"
CREATE PROCEDURE dbo.UP_TEST
    @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;
    DECLARE @v_cnt INT;
    DECLARE @v_acc MONEY = 0;
    DECLARE @v_rate DECIMAL(5,2) = 1.25;
    SET @v_acc = @v_acc + 1;
    SELECT @v_rate = 2.5;
    UPDATE dbo.T SET Amt = CAST(Amt / @v_valIncVat AS INT);
END";

        [Fact]
        public void ExtractConstants_KeepsOnlyInitializedAndNeverReassigned()
        {
            var facts = LocalVariableDeclarationExtractor.ExtractConstants(Ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_valIncVat", fact.Name);
            Assert.Equal("DECIMAL(2,1)", fact.DataType, ignoreCase: true);
            Assert.Equal("1.1", fact.InitialValue);
        }

        [Fact]
        public void ExtractConstants_ExcludesVariableReassignedBySelect()
        {
            // SetAssignmentExtractor 는 SetVariableStatement 만 본다 - 그것을 그대로
            // 재사용하면 이 변수가 상수로 분류돼 오탐이 된다.
            var facts = LocalVariableDeclarationExtractor.ExtractConstants(Ddl);

            Assert.DoesNotContain(facts, f => f.Name == "@v_rate");
        }

        [Fact]
        public void ExtractConstants_ExcludesProcedureParameters()
        {
            var facts = LocalVariableDeclarationExtractor.ExtractConstants(
                "CREATE PROCEDURE dbo.UP_TEST @pi_rate DECIMAL(2,1) = 1.1 AS BEGIN SELECT 1; END");

            Assert.Empty(facts);
        }

        [Fact]
        public void ExtractConstants_OnUnparsableDdl_IsEmptyNotPartial()
        {
            var facts = LocalVariableDeclarationExtractor.ExtractConstants("CREATE PROCEDURE (((");

            Assert.Empty(facts);
        }
    }
}
