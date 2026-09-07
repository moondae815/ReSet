using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class AssignmentExpressionUnwrapperTests
    {
        /// <summary>DDL 한 조각에서 첫 `SELECT @v = <식>`의 우변을 꺼낸다.</summary>
        private static ScalarExpression RightHandSide(string ddl)
        {
            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddl);
            var fragment = parser.Parse(reader, out var errors);
            Assert.Empty(errors);

            var finder = new SetVariableFinder();
            fragment!.Accept(finder);
            return Assert.Single(finder.Expressions);
        }

        private sealed class SetVariableFinder : TSqlFragmentVisitor
        {
            public List<ScalarExpression> Expressions { get; } = new();

            public override void Visit(SelectSetVariable node)
            {
                if (node.Expression != null) Expressions.Add(node.Expression);
            }
        }

        private static string Ddl(string rhs) =>
            "CREATE PROCEDURE dbo.P AS BEGIN DECLARE @v INT SELECT @v = " + rhs +
            " FROM dbo.T WITH(NOLOCK) END";

        [Fact]
        public void Unwrap_PlainColumn_ReturnsItUnchangedWithNoWrapper()
        {
            var result = AssignmentExpressionUnwrapper.Unwrap(RightHandSide(Ddl("A.CommissionRate")));

            Assert.IsType<ColumnReferenceExpression>(result.Inner);
            Assert.Null(result.WrapperName);
            Assert.Null(result.Fallback);
        }

        [Fact]
        public void Unwrap_IsNullWithLiteralDefault_PeelsOneLayerAndKeepsTheDefault()
        {
            // UP_UTIL_SETTLE_PROC_ETC:116 실측 모양 - 이 자리가 두 추출기 사이로 새던
            // 곳이다. 집계 추출기는 "최상위가 집계 이름이 아님"으로, 비집계 추출기는
            // "맨 컬럼이 아님"으로 각각 떨어뜨렸다.
            var result = AssignmentExpressionUnwrapper.Unwrap(RightHandSide(Ddl("ISNULL(SUM(A.CLTotal), 0)")));

            var call = Assert.IsType<FunctionCall>(result.Inner);
            Assert.Equal("SUM", call.FunctionName.Value);
            Assert.Equal("ISNULL", result.WrapperName);
            Assert.Equal("0", Assert.IsAssignableFrom<Literal>(result.Fallback).Value);
        }

        [Fact]
        public void Unwrap_Coalesce_IsTreatedTheSameAsIsNull()
        {
            var result = AssignmentExpressionUnwrapper.Unwrap(RightHandSide(Ddl("COALESCE(MAX(A.ID), 0)")));

            Assert.IsType<FunctionCall>(result.Inner);
            Assert.Equal("COALESCE", result.WrapperName);
            Assert.NotNull(result.Fallback);
        }

        [Fact]
        public void Unwrap_IsNullWhoseDefaultIsNotALiteral_DoesNotPeel()
        {
            // 기본값이 리터럴이 아니면 무결과 시 무엇이 들어가는지 기계가 말할 수 없다.
            // 벗기면 네 번째 문장이 "<식>이 대입됩니다"를 지어내게 된다 - 침묵한다.
            var result = AssignmentExpressionUnwrapper.Unwrap(RightHandSide(Ddl("ISNULL(SUM(A.CLTotal), @v)")));

            Assert.IsType<FunctionCall>(result.Inner);
            Assert.Equal("ISNULL", ((FunctionCall)result.Inner).FunctionName.Value);
            Assert.Null(result.WrapperName);
            Assert.Null(result.Fallback);
        }

        [Fact]
        public void Unwrap_PeelsOnlyOneLayer()
        {
            // 겹겹이 감싼 것을 끝까지 벗기면 판정 범위가 소리 없이 넓어진다. 한 겹만 벗기고,
            // 그래도 안쪽이 판정 가능한 모양이 아니면 소비자가 침묵한다.
            var result = AssignmentExpressionUnwrapper.Unwrap(
                RightHandSide(Ddl("ISNULL(ISNULL(A.Rate, 0), 0)")));

            var inner = Assert.IsType<FunctionCall>(result.Inner);
            Assert.Equal("ISNULL", inner.FunctionName.Value);
            Assert.Equal("ISNULL", result.WrapperName);
        }

        [Fact]
        public void IsCapturableExpression_Iif_WhenBothBranchesAreColumns_Succeeds()
        {
            // UF_GET_COMM4CLIENT4PARTIALCANCEL:43 실측 - 축 A 🔴 #1 의 자리다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("IIF(@v IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)"))));
        }

        [Fact]
        public void IsCapturableExpression_Case_WhenEveryBranchIncludingElseIsAColumn_Succeeds()
        {
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA ELSE A.RateB END"))));
        }

        [Fact]
        public void IsCapturableExpression_CaseWithoutElse_Fails()
        {
            // ELSE가 없으면 어느 WHEN도 참이 아닐 때 NULL이 대입된다. 분기 결과가
            // "컬럼/리터럴/산술식"이라는 전제가 깨지므로 담지 않는다 - 거짓 행보다
            // 없는 행이 낫다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA END"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundBranchIsALiteral_Succeeds()
        {
            // 2 회차 넓힘 - 분기 결과가 리터럴이어도 이제 담는다.
            // (구 시험 Extract_IifWithANonColumnBranch_StaysSilent이 이 방향으로 뒤집혔다.)
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("IIF(@v = 1, A.RateA, 0)"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundBranchIsArithmeticOfColumns_Succeeds()
        {
            // 2 회차 넓힘 - 분기 결과가 컬럼 간 산술식이어도 이제 담는다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("IIF(@v = 1, A.RateA, A.RateB + 1)"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundBranchIsArithmeticOfColumnAndLiteral_Succeeds()
        {
            // 2 회차 넓힘 - 컬럼과 리터럴의 산술식(단항 부호 포함)도 담는다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA - B.RateB ELSE -1 END"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundTopLevelArithmeticContainingOneCase_Succeeds()
        {
            // UF_GET_COMM4CLIENT4INTEREST:35 · UF_GET_COMM4PG4INTEREST:42 실측 모양 -
            // 최상위가 분기식을 품은 산술식(`CASE … END / 100.0`)이면, 그 안의 분기
            // 결과가 컬럼/리터럴/산술식일 때 담는다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA ELSE 0 END / 100.0"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundNestedCaseInABranch_Fails()
        {
            // 좁게 유지 - 분기 결과 안에 또 분기식(중첩 CASE)이 오면 담지 않는다.
            // UF_GET_COLLECTYMD:31·UIF_SettleYMD:39 실측 모양이다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl(
                    "CASE WHEN A.Flag = 1 THEN CASE WHEN B.Flag = 1 THEN A.RateA ELSE A.RateB END ELSE A.RateC END"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundArithmeticWrappingANestedCaseInABranch_Fails()
        {
            // 좁게 유지 - 분기 결과가 "산술식이 분기식을 감싼" 모양이어도 담지 않는다.
            // 재귀가 산술식을 타고 내려가는 동안에도 "분기식은 한 겹만" 허용이 계속
            // 지켜져야 한다 - 산술식 재귀에 분기 재허용을 실어 보내면 이 자리가 뚫린다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl(
                    "IIF(A.Flag = 1, A.RateA * CASE WHEN B.Flag = 1 THEN A.RateB ELSE A.RateC END, A.RateD)"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundParenthesizedCaseInABranch_Fails()
        {
            // 좁게 유지 - 분기 결과가 괄호로 감싼 분기식이어도 담지 않는다. 괄호 벗기기가
            // "분기식 한 겹만" 허용을 함께 실어 보내면 이 자리가 뚫린다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl(
                    "IIF(A.Flag = 1, (CASE WHEN B.Flag = 1 THEN A.RateB ELSE A.RateC END), A.RateD)"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundFunctionCallInABranch_Fails()
        {
            // 좁게 유지 - 분기 결과가 함수 호출이면 담지 않는다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("IIF(@v = 1, dbo.UF_GET_RATE(A.ClientID), A.RateB)"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundSubqueryInABranch_Fails()
        {
            // 좁게 유지 - 분기 결과가 하위 질의면 담지 않는다.
            // UF_Get_CLComm4MobileCo:25 실측 모양이다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN (SELECT TOP 1 Rate FROM dbo.T2) ELSE A.RateB END"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundTopLevelBareLiteral_Succeeds()
        {
            // 구현이 §10-2 의 글보다 넓다 - 최상위가 분기식 없이 맨 리터럴이어도 담긴다
            // (`SELECT @v = 0 FROM T`류). 문장이 참이고 코퍼스 영향이 0 이라 좁히지
            // 않기로 했고(리뷰 라운드 1, FINDING M3), 그 대신 이 넓은 동작을 잠근다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("0"))));
        }

        [Fact]
        public void IsCapturableExpression_2RoundTopLevelArithmeticOfColumnsWithNoBranchAnywhere_Succeeds()
        {
            // 구현이 §10-2 의 글보다 넓다 - 최상위가 분기식 없이 컬럼 간 산술식이어도
            // 담긴다(`SELECT @v = A.x - B.y`류). FINDING M3와 같은 이유로 좁히지 않고
            // 잠근다.
            Assert.True(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("A.x - B.y"))));
        }

        [Fact]
        public void IsCapturableExpression_Aggregate_Fails()
        {
            // 집계는 잎이 아니므로 이 판정에 걸리지 않는다. 이것이 두 갈래를
            // 배타적으로 만드는 기전이다 - 비집계 소비자가 집계를 담을 길이 없다.
            Assert.False(AssignmentExpressionUnwrapper.IsCapturableExpression(
                RightHandSide(Ddl("SUM(A.CLTotal)"))));
        }

        [Fact]
        public void TextOf_NormalizesWhitespaceAndKeepsTheOriginalTokens()
        {
            var text = AssignmentExpressionUnwrapper.TextOf(
                RightHandSide(Ddl("IIF(@v IN (0,2),   A.CommissionRate,\n A.FreeInterestInstCommRate)")));

            Assert.Equal("IIF(@v IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)", text);
        }
    }
}
