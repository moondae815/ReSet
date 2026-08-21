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
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(ExpressionTypePathExtractor.Extract("CREATE FUNCTION (((", NoColumns));
        }
    }
}
