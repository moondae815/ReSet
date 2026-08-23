using System;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 변수(파라미터·지역 변수)가 어느 `테이블.컬럼`과 실제로 결합되는지를 AST에서 뽑는다.
    ///
    /// 배경 - 2026-08-23 9회차 축 A 재감사 🟡(`UP_UTIL_SETTLE_EXCEPTION_PROC` Spec.md:34):
    /// 「파라미터와 변수의 컬럼 관계」 표가 `@pi_strYMD`의 연결 컬럼으로 `TPLCardTxMst.YMD`·
    /// `TClientSettleRate4MobileCo.YMD`를 적었는데 전자는 함수 인자로만 함께 나오고(393행)
    /// 후자는 `A.AYMD = B.YMD`(416행)로 승인일에 결합된다. 이 재료가 그 주장의 기준값이다.
    ///
    /// 결합으로 치는 것: 비교·IN·BETWEEN·LIKE 술어 안에서 변수와 함께 나오는 컬럼, 대입
    /// (`UPDATE … SET C = @p`, INSERT 매핑, `SELECT @v = C`, `SET @v = (SELECT C …)`).
    /// 결합으로 치지 않는 것: 같은 함수 호출의 인자로만 함께 나오는 것.
    /// 별칭은 **문장 단위**로 푼다 - 같은 별칭이 문장마다 다른 테이블일 수 있다(EXCEPTION_PROC의 `B`).
    /// </summary>
    public class ParameterColumnBindingExtractorTests
    {
        private static bool Has(System.Collections.Generic.IReadOnlyList<ParameterColumnBinding> b,
            string variable, string table, string column)
            => b.Any(x => x.Variable.Equals(variable, StringComparison.OrdinalIgnoreCase)
                       && x.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                       && x.Column.Equals(column, StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void Extract_ComparisonWithAliasedColumn_BindsParameterToResolvedTable()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.YMD = @pi_strYMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TSettleMst", "YMD"));
        }

        [Fact]
        public void Extract_ColumnToColumnComparison_DoesNotBindAnyVariable()
        {
            // EXCEPTION_PROC:416 - `A.AYMD = B.YMD`에는 변수가 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A
    JOIN   dbo.TClientSettleRate4MobileCo B ON A.ClientID = B.ClientID
    WHERE  A.AYMD = B.YMD AND A.YMD = @pi_strYMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.False(Has(b, "@pi_strYMD", "TClientSettleRate4MobileCo", "YMD"));
            Assert.True(Has(b, "@pi_strYMD", "TSettleMst", "YMD"));
        }

        [Fact]
        public void Extract_FunctionArgumentCoOccurrence_IsNotABinding()
        {
            // EXCEPTION_PROC:393 - 함수 인자로만 함께 나온다.
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET CLCOMM = dbo.UF_X(B.ClientID, B.YMD, @pi_strYMD)
    FROM   dbo.TSettleMst A
    JOIN   dbo.TPLCardTxMst B ON A.PLTID = B.PLTID
    WHERE  A.YMD = @pi_strYMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.False(Has(b, "@pi_strYMD", "TPLCardTxMst", "YMD"));
        }

        [Fact]
        public void Extract_WrappedColumnAndExpressionSide_StillBind()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @pi_intDays INT AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A
    WHERE  ISNULL(A.EDIReqYmd, '') = @pi_strYMD
    AND    A.OutYMD >= CONVERT(CHAR(8), DATEADD(D, @pi_intDays, @pi_strYMD), 112)
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TSettleMst", "EDIReqYmd"));
            Assert.True(Has(b, "@pi_strYMD", "TSettleMst", "OutYMD"));
            Assert.True(Has(b, "@pi_intDays", "TSettleMst", "OutYMD"));
        }

        [Fact]
        public void Extract_InAndBetweenAndLike_Bind()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @a CHAR(8), @b CHAR(8), @c VARCHAR(20) AS
BEGIN
    DELETE A FROM dbo.TOut A
    WHERE  A.YMD BETWEEN @a AND @b
    AND    A.PGName IN (@c, 'X')
    AND    A.MallID LIKE @c + '%'
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@a", "TOut", "YMD"));
            Assert.True(Has(b, "@b", "TOut", "YMD"));
            Assert.True(Has(b, "@c", "TOut", "PGName"));
            Assert.True(Has(b, "@c", "TOut", "MallID"));
        }

        [Fact]
        public void Extract_AliasReusedAcrossStatements_ResolvesPerStatement()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET A.Flag = 1 FROM dbo.TSettleMst A JOIN dbo.TPLCardTxMst B ON A.PLTID = B.PLTID WHERE B.YMD = @pi_strYMD
    UPDATE A SET A.Flag = 2 FROM dbo.TSettleMst A JOIN dbo.TClientSettleRate4MobileCo B ON A.ClientID = B.ClientID WHERE A.AYMD = B.YMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TPLCardTxMst", "YMD"));
            Assert.False(Has(b, "@pi_strYMD", "TClientSettleRate4MobileCo", "YMD"));
        }

        [Fact]
        public void Extract_UnqualifiedColumnInSingleTableStatement_BindsToThatTable()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    DELETE FROM dbo.TOut WHERE YMD = @pi_strYMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TOut", "YMD"));
        }

        [Fact]
        public void Extract_Assignments_BindVariableToTargetOrSourceColumn()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    DECLARE @v_strReqYMD CHAR(8)
    SELECT @v_strReqYMD = MIN(A.ReqYMD) FROM dbo.TTx A WHERE A.ResYMD = @pi_strYMD
    UPDATE dbo.TOut SET ProcYMD = @pi_strYMD WHERE Flag = 1
    INSERT INTO dbo.TLog (YMD, RetVal) VALUES (@pi_strYMD, @po_intRetVal)
    INSERT INTO dbo.TLog2 (YMD) SELECT @pi_strYMD FROM dbo.TTx
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@v_strReqYMD", "TTx", "ReqYMD"));
            Assert.True(Has(b, "@pi_strYMD", "TOut", "ProcYMD"));
            Assert.True(Has(b, "@pi_strYMD", "TLog", "YMD"));
            Assert.True(Has(b, "@po_intRetVal", "TLog", "RetVal"));
            Assert.True(Has(b, "@pi_strYMD", "TLog2", "YMD"));
        }

        [Fact]
        public void Extract_ComparisonInsideSubqueryAndOnClause_Binds()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A
    JOIN   dbo.TPGProperty PG ON A.PGName = PG.PGName AND PG.ApplyYMD <= @pi_strYMD
    WHERE  A.PLTID IN (SELECT C.PLTID FROM dbo.TCCanceledMst C WHERE C.CYMD = @pi_strYMD)
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TPGProperty", "ApplyYMD"));
            Assert.True(Has(b, "@pi_strYMD", "TCCanceledMst", "CYMD"));
        }

        [Fact]
        public void Extract_CursorFetchInto_BindsVariablesPositionally()
        {
            // PROC_ETC 실물 - 커서 원천 SELECT의 열과 FETCH INTO 변수가 자리로 대응한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_strYMD VARCHAR(8), @v_intCLTotal MONEY
    DECLARE Cur CURSOR FOR SELECT A.YMD, SUM(A.CLTotal) FROM dbo.TSettleMst A GROUP BY A.YMD
    OPEN Cur
    FETCH NEXT FROM Cur INTO @v_strYMD, @v_intCLTotal
    CLOSE Cur
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@v_strYMD", "TSettleMst", "YMD"));
            Assert.True(Has(b, "@v_intCLTotal", "TSettleMst", "CLTotal"));
        }

        [Fact]
        public void Extract_ArithmeticWithVariable_BindsTheColumnsInThatExpression()
        {
            // COMM_UPD:413 실물 - `CAST(CLEtc/@v_valIncVat AS INT)`가 CLComm에 대입된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_valIncVat DECIMAL(2,1) = 1.1
    UPDATE dbo.TSettleMst SET CLComm = CAST(CLComm/@v_valIncVat AS INT) + CAST(CLEtc/@v_valIncVat AS INT)
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@v_valIncVat", "TSettleMst", "CLEtc"));
            Assert.True(Has(b, "@v_valIncVat", "TSettleMst", "CLComm"));
        }

        [Fact]
        public void Extract_JoinEquality_PropagatesBindingTransitively()
        {
            // COMM_UPD:68·76 실물 - `A.YMD = B.YMD` 이고 `A.YMD = @pi_strYMD` 이면 B.YMD도 결합이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8) AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A, dbo.TClientSettleRate B
    WHERE  A.YMD = B.YMD AND A.ClientID = B.ClientID AND A.YMD = @pi_strYMD
END";
            var b = ParameterColumnBindingExtractor.Extract(ddl);
            Assert.True(Has(b, "@pi_strYMD", "TClientSettleRate", "YMD"));
            Assert.False(Has(b, "@pi_strYMD", "TClientSettleRate", "ClientID"));
        }

        [Fact]
        public void Extract_NullOrUnparsable_ReturnsEmpty()
        {
            Assert.Empty(ParameterColumnBindingExtractor.Extract(null));
            Assert.Empty(ParameterColumnBindingExtractor.Extract("   "));
        }
    }
}
