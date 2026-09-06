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
        public void TryColumnBranches_Iif_WhenBothBranchesAreColumns_Succeeds()
        {
            // UF_GET_COMM4CLIENT4PARTIALCANCEL:43 실측 - 축 A 🔴 #1 의 자리다.
            var ok = AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("IIF(@v IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)")),
                out var branches);

            Assert.True(ok);
            Assert.Equal(2, branches.Count);
        }

        [Fact]
        public void TryColumnBranches_Case_WhenEveryBranchIncludingElseIsAColumn_Succeeds()
        {
            var ok = AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA ELSE A.RateB END")),
                out var branches);

            Assert.True(ok);
            Assert.Equal(2, branches.Count);
        }

        [Fact]
        public void TryColumnBranches_CaseWithoutElse_Fails()
        {
            // ELSE가 없으면 어느 WHEN도 참이 아닐 때 NULL이 대입된다. 분기가 "전부 컬럼"이라는
            // 전제가 깨지므로 담지 않는다 - 거짓 행보다 없는 행이 낫다.
            Assert.False(AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("CASE WHEN A.Flag = 1 THEN A.RateA END")), out _));
        }

        [Fact]
        public void TryColumnBranches_WhenAnyBranchIsALiteralOrExpression_Fails()
        {
            Assert.False(AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("IIF(@v = 1, A.RateA, 0)")), out _));
            Assert.False(AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("IIF(@v = 1, A.RateA, A.RateB + 1)")), out _));
        }

        [Fact]
        public void TryColumnBranches_Aggregate_Fails()
        {
            // 집계는 잎이 아니므로 컬럼 분기 판정에 걸리지 않는다. 이것이 두 갈래를
            // 배타적으로 만드는 기전이다 - 비집계 소비자가 집계를 담을 길이 없다.
            Assert.False(AssignmentExpressionUnwrapper.TryColumnBranches(
                RightHandSide(Ddl("SUM(A.CLTotal)")), out _));
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
