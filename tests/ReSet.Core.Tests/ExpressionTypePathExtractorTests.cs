using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ExpressionTypePathExtractorTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Extract_DivisionInsideTheCast_ShouldReportNumericTruncation()
        {
            // 실행 대조 2026-08-22: 10050 x 1.50%가 이 경로에서는 150이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_intCommission MONEY
    SET @v_intCommission = 1.50
    RETURN CAST(@pi_intTxAmt * (@v_intCommission / 100.0) AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("numeric", fact.Sentence);
            Assert.Contains("절사", fact.Sentence);
        }

        [Fact]
        public void Extract_DivisionOutsideTheCast_ShouldReportMoneyRounding()
        {
            // 실행 대조 2026-08-22: 같은 값이 이 경로에서는 151이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_intRate MONEY
    SET @v_intRate = 0.015
    RETURN CAST(@pi_intTxAmt * @v_intRate AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("money", fact.Sentence);
            Assert.Contains("반올림", fact.Sentence);
        }

        [Fact]
        public void Extract_UnknownLeafType_ShouldOmitTheRow()
        {
            // 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다.
            // 컬럼 타입 사전에 없는 컬럼이 잎으로 들어오면 행을 내지 않는다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
        }

        [Fact]
        public void Extract_AmbiguousColumnType_ShouldOmitTheRow()
        {
            // BuildColumnTypeMap이 같은 컬럼명의 타입이 테이블마다 달라 "(모호)"를 넣은
            // 경우다. 그 값은 타입 이름이 아니라 "모른다"는 표시이므로 행을 생략해야
            // 한다 - 다른 잎(2, 리터럴 int)이 알려져 있어도 마찬가지다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "(모호)" };
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
        }

        [Fact]
        public void Extract_FloatOperand_ShouldOmitTheRow()
        {
            // float/real은 money·decimal/numeric보다 데이터 형식 우선순위가 더 높다.
            // 이 추출기는 money → int(반올림)와 numeric → int(절사) 두 경로만 실행으로
            // 확인했으므로, float가 섞인 식은 방향을 단정하지 않고 생략한다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_floatRate FLOAT
    SET @v_floatRate = 1.5
    RETURN CAST(@pi_intTxAmt * @v_floatRate AS INT)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
        }

        [Fact]
        public void Extract_IntOnlyOperands_ShouldOmitTheRow()
        {
            // int끼리의 곱은 money/numeric 갈림과 무관해 확정 사실로 실을 내용이 없다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intA INT, @pi_intB INT) RETURNS INT AS
BEGIN
    RETURN CAST(@pi_intA * @pi_intB AS INT)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
        }

        [Fact]
        public void Extract_CastInSelectList_ShouldBeVisited()
        {
            // 방문 커버리지: SELECT 목록 안의 CAST.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a MONEY = 10
    DECLARE @b MONEY = 2
    SELECT CAST(@a * @b AS INT) AS Result
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
            Assert.Contains("money", fact.Sentence);
        }

        [Fact]
        public void Extract_CastInSetStatement_ShouldBeVisited()
        {
            // 방문 커버리지: SET 문 안의 CAST.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a MONEY = 10
    DECLARE @b MONEY = 2
    DECLARE @out INT
    SET @out = CAST(@a * @b AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
            Assert.Contains("money", fact.Sentence);
        }

        [Fact]
        public void Extract_CastNestedInsideDerivedTable_ShouldBeVisited()
        {
            // 방문 커버리지: FROM 절 파생 테이블 서브쿼리 안의 CAST(RETURN/최상위 SELECT
            // 목록보다 한 겹 더 깊다).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a MONEY = 10
    DECLARE @b MONEY = 2
    SELECT x.Val
    FROM (SELECT CAST(@a * @b AS INT) AS Val) x
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
            Assert.Contains("money", fact.Sentence);
        }

        [Fact]
        public void Extract_CastSpanningMultipleLines_TargetMatchesRenderedTableCell()
        {
            // I3 수정 라운드: MechanicalValidator.CheckExecutionSemantics는 렌더되지 않은
            // fact.Target(=SpecExpectations가 그대로 옮긴 ExecutionSemanticFact.Target)을
            // 렌더된(개행이 공백으로 접힌) 표 셀 문자열과 원문 그대로(==) 비교한다
            // (MechanicalValidator.cs:3494-3503). Target에 개행이 남아 있으면 렌더
            // 파이프라인(MarkdownTableCellCodec.Escape)을 거친 셀과 영원히 일치하지
            // 않는다 - 이 산술식은 다섯 종류 중 유일하게 통째로 Target에 실려 줄바꿈
            // 확률이 가장 높다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_intRate MONEY
    SET @v_intRate = 0.015
    RETURN CAST(
        @pi_intTxAmt
        *
        @v_intRate
        AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.DoesNotContain("\n", fact.Expression);
            Assert.DoesNotContain("\r", fact.Expression);

            // 렌더러가 실제로 거치는 변환(개행 접기 + | 이스케이프)을 그대로 적용한 뒤,
            // L1이 쓰는 셀 분리기로 되돌려 실제 렌더된 셀과 fact.Target(=Expression)이
            // 같은지 확인한다 - 둘 중 하나만 보면 이 결함이 또 숨는다.
            var renderedRow = $"| {MarkdownTableCellCodec.Escape(fact.Expression)} |";
            var renderedCell = MarkdownTableCellCodec.SplitRow(renderedRow)[1];
            Assert.Equal(fact.Expression, renderedCell);
        }

        [Fact]
        public void Extract_ColumnTypeFromBuildColumnTypeMap_DecimalWithPrecisionScale_ShouldReportNumericTruncation()
        {
            // I1 수정 라운드: DbMetadataService(:898-907, :334, :365)가 실제로 만드는
            // 컬럼 타입 문자열은 "decimal(18,2)"이지 "decimal"이 아니다 - 손으로 지은
            // 맵이 아니라 BuildColumnTypeMap을 직접 태워야 이 결함이 다시 숨지 않는다.
            var dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    Name = "T",
                    Schema = "dbo",
                    Columns = { new ColumnInfo { ColumnName = "Amt", DataType = "decimal(18,2)" } },
                },
            };
            var columnTypes = ExecutionSemanticsFacts.BuildColumnTypeMap(dependencies);

            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
            Assert.Contains("numeric", fact.Sentence);
            Assert.Contains("절사", fact.Sentence);
        }

        [Fact]
        public void Extract_ColumnTypeFromBuildColumnTypeMap_MoneyPlainName_ShouldReportMoneyRounding()
        {
            // I1이 money/int/float처럼 괄호가 없는 타입 이름까지 깨뜨리지 않는지
            // 못박는다 - DbMetadataService의 ELSE '' 분기가 이 형태를 만든다.
            var dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    Name = "T",
                    Schema = "dbo",
                    Columns = { new ColumnInfo { ColumnName = "Amt", DataType = "money" } },
                },
            };
            var columnTypes = ExecutionSemanticsFacts.BuildColumnTypeMap(dependencies);

            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
            Assert.Contains("money", fact.Sentence);
            Assert.Contains("반올림", fact.Sentence);
        }

        [Fact]
        public void Extract_ColumnFromRealBaseTableAlias_ShouldStillReportKnownDirection()
        {
            // I2 수정의 회귀 방지: 진짜 영속 테이블 별칭을 통한 컬럼은 한정자 게이트가
            // 생겨도 여전히 열려야 한다 - 과잉 침묵으로 이 과제를 무력화하면 안 된다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "money" };
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
            Assert.Contains("money", fact.Sentence);
        }

        [Fact]
        public void Extract_ColumnFromDerivedTableAlias_ShouldOmitTheRow()
        {
            // I2: 파생 테이블 별칭 x는 dbo.T의 의존성이 아니다. x.Amt가 우연히
            // columnTypes의 "Amt"(실제로는 dbo.T 소속)와 이름이 같아도, x가 물리
            // 테이블이 아니므로 그 컬럼에 대해 어떤 타입도 확정할 수 없다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "money" };
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(x.Amt * 2 AS INT) FROM (SELECT Amt FROM dbo.T) x)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
        }

        [Fact]
        public void Extract_ColumnFromCteAlias_ShouldOmitTheRow()
        {
            // I2: CTE 참조는 구문상 NamedTableReference와 구분되지 않지만 물리
            // 테이블이 아니다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "money" };
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    ;WITH C AS (SELECT Amt FROM dbo.T)
    SELECT CAST(C.Amt * 2 AS INT) FROM C
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
        }

        [Fact]
        public void Extract_ColumnFromTempTableAlias_ShouldOmitTheRow()
        {
            // I2: #Tmp는 dbo.T와 이름이 같은 컬럼이 있어도 별개의 임시 테이블이다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "money" };
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT Amt INTO #Tmp FROM dbo.T
    SELECT CAST(t.Amt * 2 AS INT) FROM #Tmp t
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
        }

        [Fact]
        public void Extract_ColumnFromTableVariableAlias_ShouldOmitTheRow()
        {
            // I2: 테이블 변수도 임시 테이블과 같은 이유로 물리 테이블이 아니다.
            var columnTypes = new Dictionary<string, string> { ["Amt"] = "money" };
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @Tmp TABLE (Amt MONEY)
    INSERT INTO @Tmp (Amt) SELECT Amt FROM dbo.T
    SELECT CAST(t.Amt * 2 AS INT) FROM @Tmp t
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, columnTypes));
        }

        [Fact]
        public void Extract_SmallMoneyOperands_SentenceNamesSmallMoneyNotMoney()
        {
            // M-a: "기계 확정" 행이 식에 없는 타입 이름을 대면 감사자나 모델이 그것을
            // "고치려" 든다 - smallmoney 식인데 문장이 money라고 말하면 안 된다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_a SMALLMONEY, @pi_b SMALLMONEY) RETURNS INT AS
BEGIN
    RETURN CAST(@pi_a * @pi_b AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("smallmoney", fact.Sentence);
            Assert.NotEqual(ExpressionTypePathExtractor.MoneyRoundingSentence, fact.Sentence);
        }

        [Fact]
        public void Extract_PureDecimalOperandWithNoMoney_SentenceDoesNotClaimPromotion()
        {
            // M-b: money가 애초에 전혀 없으면(피연산자가 처음부터 decimal/numeric)
            // "승격되어"는 거짓 원인절이다 - 방향은 옳아도 근거가 틀렸다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_decimalRate DECIMAL) RETURNS INT AS
BEGIN
    RETURN CAST(@pi_decimalRate * 2 AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("numeric", fact.Sentence);
            Assert.Contains("절사", fact.Sentence);
            Assert.DoesNotContain("승격", fact.Sentence);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(ExpressionTypePathExtractor.Extract("CREATE FUNCTION (((", NoColumns));
        }
    }
}
