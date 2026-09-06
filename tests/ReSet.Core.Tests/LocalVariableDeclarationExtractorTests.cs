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
            //
            // [픽스처가 부분 AST를 실제로 만들어야 한다] 앞서 쓰던
            // "CREATE PROCEDURE ((( AS"는 오류도 1이지만 선언도 0이라, 에러 가드를
            // 지워도 결과가 빈 목록이라 이 단언이 통과했다 - 이름만 정책을 주장하고
            // 아무것도 잠그지 않는 진공 테스트였다. 아래 입력은 오류 1과 함께
            // `@v_a`를 담은 부분 AST를 실제로 내므로, 가드를 지우면 이 단언이 깨진다.
            var facts = LocalVariableDeclarationExtractor.Extract("DECLARE @v_a INT = 7; SELECT (((");

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
        public void ExtractConstants_ExcludesVariableReassignedByFetchInto()
        {
            // PROC_ETC 실물의 모양이다 - @v_intCLTotal 은 `SET`/`SELECT` 대입이 한 번도
            // 없고 `FETCH NEXT ... INTO` 로만 값이 들어온다(실측: 29~31 행 선언,
            // 66·143 행 FETCH). 이 갈래를 안 보면 누산기가 상수로 분류된다 - 이 클래스의
            // 문서 블록이 애초에 지목한 실물 사례가 바로 그 FETCH 대상 변수다.
            const string ddl = @"
CREATE PROCEDURE dbo.UP_TEST
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0;
    DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;
    DECLARE Cur_SettlePost CURSOR FOR SELECT CLTotal FROM dbo.T;
    OPEN Cur_SettlePost;
    FETCH NEXT FROM Cur_SettlePost INTO @v_intCLTotal;
    CLOSE Cur_SettlePost;
    DEALLOCATE Cur_SettlePost;
END";

            var facts = LocalVariableDeclarationExtractor.ExtractConstants(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_valIncVat", fact.Name);
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
            // 이름이 「빈 목록이지 부분 결과가 아니다」라고 주장하므로 픽스처도 그
            // 구분을 실제로 시험해야 한다. "CREATE PROCEDURE ((("는 오류 1·선언 0이라
            // 에러 가드를 지워도 통과하는 진공 입력이었다. 이 입력은 오류 1과 함께
            // `@v_a`(초기값 7, 재대입 0)를 담은 부분 AST를 내므로, 가드가 없으면
            // 상수 하나가 새어 나와 이 단언이 깨진다.
            var facts = LocalVariableDeclarationExtractor.ExtractConstants("DECLARE @v_a INT = 7; SELECT (((");

            Assert.Empty(facts);
        }
    }
}
