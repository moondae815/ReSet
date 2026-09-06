using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

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
    ///
    /// [왜 NOT IN도 담는가] Fix Round 1 좌표자 판정 - 사전의 목적은 「이 값이 무슨
    /// 뜻인가」이지 「등호로 쓰였는가」가 아니다. `NOT IN ('PLTEST','SKTEST')`의
    /// 값도 인수인계 문서에서 번역이 필요한 코드값이고, 오히려 「테스트 가맹점은
    /// 정산에서 제외한다」가 알짜 정보다. 값의 의미는 그 값이 긍정문에 쓰였는지
    /// 부정문에 쓰였는지로 달라지지 않는다. 이 판단은 의도적이다 - `NotDefined`를
    /// 걸러 값을 빼면 사전에서 코드값이 조용히 사라지므로 그렇게 「고치지」 말 것.
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
            catch (Exception ex)
            {
                // 이 catch는 방어적이다 - TSql160Parser.Parse는 구문 오류에 던지지
                // 않고 오류 목록을 담은 조각을 돌려주므로(파스에_실패해도_빈_목록을_
                // 돌려준다 테스트가 초록인 이유는 이 catch가 아니라 방문자가 비교
                // 노드를 못 찾아서다), 이 자리는 파서가 다른 이유로(예: 스택 오버플로,
                // 내부 버그) 던질 때만 걸린다. 그래도 걸리면 재료가 없는 것이지
                // 실행을 세울 일은 아니므로, SessionOptionsExtractor·
                // RoundingSemanticsExtractor와 같은 관행으로 로그를 남기고 소프트
                // 페일한다 - 코퍼스 전체가 이 경로를 타도 진단 흔적 없이 조용히
                // 빈 결과만 나오는 상태를 남기지 않기 위해서다.
                Log.Warning(ex, "[ConstantComparisonExtractor] 상수 비교 쌍 추출 실패 - 빈 목록으로 진행합니다.");
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
