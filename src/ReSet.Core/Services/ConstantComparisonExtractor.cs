using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>비교식 하나에서 뽑은 (컬럼, 문자열 리터럴) 쌍. 컬럼을 특정하지 못하면 Column은 null.</summary>
    public sealed record ConstantComparison(string? Column, string Value);

    /// <summary>
    /// DDL의 비교식에서 코드 상수를 뽑는다.
    ///
    /// [왜 정규식이 아니라 AST인가] 이 코퍼스는 동적 SQL 조립이 많아, 정규식으로
    /// `= '...'`를 찾으면 `' + @v_strClientID+ '` 같은 문자열 연결 조각이 상수로
    /// 잡힌다(고유 상수 82개 중 18개, 2026-09-06 실측). `NOT`을 컬럼명으로 잡기도
    /// 했다. 파스 트리를 보면 문자열 연결의 피연산자는 비교식의 우변이 아니므로
    /// 구조적으로 배제된다.
    ///
    /// [왜 SET 대입을 뽑지 않는가] 코드값 사전의 좌변은 「이 값과 같으면 이 분기」라는
    /// 판단 기준이다. 대입은 판단이 아니라 결과이므로 사전의 좌변이 아니다.
    /// </summary>
    public static class ConstantComparisonExtractor
    {
        public static IReadOnlyList<ConstantComparison> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText))
            {
                return Array.Empty<ConstantComparison>();
            }

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out _);
            }
            catch (Exception)
            {
                // 파서가 던지면 재료가 없는 것이지 실행을 세울 일은 아니다 -
                // 이 저장소의 다른 추출기와 같은 소프트 페일이다.
                return Array.Empty<ConstantComparison>();
            }

            if (fragment == null)
            {
                return Array.Empty<ConstantComparison>();
            }

            var visitor = new ComparisonVisitor();
            fragment.Accept(visitor);
            return visitor.Pairs;
        }

        private sealed class ComparisonVisitor : TSqlFragmentVisitor
        {
            public List<ConstantComparison> Pairs { get; } = new();

            public override void Visit(BooleanComparisonExpression node)
            {
                Add(node.FirstExpression, node.SecondExpression);
                Add(node.SecondExpression, node.FirstExpression);
            }

            public override void Visit(InPredicate node)
            {
                var column = ColumnNameOf(node.Expression);
                foreach (var value in node.Values)
                {
                    if (value is StringLiteral literal)
                    {
                        Pairs.Add(new ConstantComparison(column, literal.Value));
                    }
                }
            }

            public override void Visit(LikePredicate node)
            {
                if (node.SecondExpression is StringLiteral literal)
                {
                    Pairs.Add(new ConstantComparison(ColumnNameOf(node.FirstExpression), literal.Value));
                }
            }

            private void Add(ScalarExpression columnSide, ScalarExpression valueSide)
            {
                if (valueSide is StringLiteral literal)
                {
                    Pairs.Add(new ConstantComparison(ColumnNameOf(columnSide), literal.Value));
                }
            }

            /// <summary>컬럼 참조이면 마지막 식별자를, 아니면 null을 준다(변수·함수·식은 컬럼이 아니다).</summary>
            private static string? ColumnNameOf(ScalarExpression? expression) =>
                expression is ColumnReferenceExpression column && column.MultiPartIdentifier?.Identifiers.Count > 0
                    ? column.MultiPartIdentifier.Identifiers[^1].Value
                    : null;
        }
    }
}
