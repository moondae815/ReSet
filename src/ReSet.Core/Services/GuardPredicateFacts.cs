using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 원본 DDL 의 <c>IF [NOT] EXISTS (SELECT … FROM T WHERE …)</c> 가드와, 단계 SQL 의 <b>존재 확인 모양</b> 질의를 뽑는다.
    /// <see cref="MechanicalValidator"/> 의 가드 술어 대조 재료다(판독 <c>docs/audit-reports/2026-09-13-가드술어-표류-측정.md</c>).
    ///
    /// 두 쪽 WHERE 를 <b>같은 함수</b> <see cref="DmlScopeExtractor.PredicateTermsOf"/> 로 정규화한다 - 규칙이 두 곳에 있으면 조용히 갈린다
    /// (앵커 술어 대조와 같은 이유). 존재 확인 모양은 코퍼스가 쓴 셋이다: <c>EXISTS (…)</c> 부질의 · <c>SELECT TOP (1) …</c> ·
    /// <c>SELECT COUNT(…)</c> 한 열. FROM 이 표 하나(조인 없음)일 때만 본다 - 가드 원본이 전부 그 모양이다.
    /// </summary>
    internal static class GuardPredicateFacts
    {
        /// <param name="Negated">원본 가드가 <c>IF NOT EXISTS</c> 인가(존재 확인 질의 쪽은 늘 false). 같은 WHERE 라도 뜻이 반대다 -
        /// 프롬프트 가드 표가 행마다 싣는다(2026-09-14 최종 리뷰).</param>
        internal sealed record Query(
            string Table,
            IReadOnlyList<PredicateTerm> Terms,
            IReadOnlyList<string> SelectColumns,
            int Line,
            bool Negated = false);

        private static readonly Regex SqlFence = new(@"```sql[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        /// <summary>원본 DDL 의 가드들. 파싱이 안 되면 빈 목록 - 기준이 없으면 침묵한다.</summary>
        internal static IReadOnlyList<Query> GuardsFromDdl(string ddl)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return Array.Empty<Query>();

            var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(ddl), out var errors);
            if (fragment == null || errors is { Count: > 0 }) return Array.Empty<Query>();

            var visitor = new GuardVisitor();
            fragment.Accept(visitor);
            return visitor.Guards;
        }

        /// <summary>단계 SQL 펜스의 존재 확인 질의들. 조각 단위로 파싱해 한 조각의 구문 오류가 번지지 않게 한다.</summary>
        internal static IReadOnlyList<Query> ExistenceQueriesFromStep(string markdown)
        {
            var queries = new List<Query>();
            if (string.IsNullOrWhiteSpace(markdown)) return queries;

            foreach (Match fence in SqlFence.Matches(markdown))
            {
                var sql = fence.Groups["body"].Value;
                var tokens = new TSql160Parser(initialQuotedIdentifiers: true).GetTokenStream(new StringReader(sql), out var tokenErrors);
                if (tokens == null || tokenErrors is { Count: > 0 }) continue;

                foreach (var (start, end, _) in StepSqlStatementReader.SplitAtTopLevelSemicolons(sql, tokens))
                {
                    var chunk = sql.Substring(start, end - start);
                    if (string.IsNullOrWhiteSpace(chunk)) continue;

                    var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(chunk), out var errors);
                    if (fragment == null || errors is { Count: > 0 }) continue;

                    var visitor = new ExistenceVisitor();
                    fragment.Accept(visitor);
                    queries.AddRange(visitor.Queries);
                }
            }

            return queries;
        }

        private static Query? FromSpecification(QuerySpecification? spec)
        {
            if (spec?.WhereClause == null || spec.FromClause?.TableReferences is not { Count: 1 } references) return null;
            if (references[0] is not NamedTableReference named || named.SchemaObject?.BaseIdentifier?.Value is not { } table) return null;

            var terms = DmlScopeExtractor.PredicateTermsOf(new[] { spec.WhereClause });
            if (terms.Count == 0) return null;

            var selectColumns = spec.SelectElements
                .OfType<SelectScalarExpression>()
                .Select(e => e.Expression)
                .OfType<ColumnReferenceExpression>()
                .Select(c => c.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null)
                .OfType<string>()
                .ToList();

            return new Query(table, terms, selectColumns, spec.StartLine);
        }

        private static QuerySpecification? Unwrap(QueryExpression? expression) => expression switch
        {
            QuerySpecification spec => spec,
            QueryParenthesisExpression parenthesis => Unwrap(parenthesis.QueryExpression),
            _ => null
        };

        private sealed class GuardVisitor : TSqlFragmentVisitor
        {
            internal List<Query> Guards { get; } = new();

            public override void ExplicitVisit(IfStatement node)
            {
                if (ExistsOf(node.Predicate, false) is ({ } exists, var negated) &&
                    FromSpecification(Unwrap(exists.Subquery?.QueryExpression)) is { } guard)
                {
                    Guards.Add(guard with { Line = node.StartLine, Negated = negated });
                }

                base.ExplicitVisit(node);
            }

            private static (ExistsPredicate? Exists, bool Negated) ExistsOf(BooleanExpression? expression, bool negated) => expression switch
            {
                ExistsPredicate exists => (exists, negated),
                BooleanNotExpression not => ExistsOf(not.Expression, !negated),
                BooleanParenthesisExpression parenthesis => ExistsOf(parenthesis.Expression, negated),
                _ => (null, negated)
            };
        }

        private sealed class ExistenceVisitor : TSqlFragmentVisitor
        {
            internal List<Query> Queries { get; } = new();

            // `EXISTS (SELECT TOP (1) …)` 은 두 방문이 같은 절을 본다 - 한 번만 센다(가드와 질의를 일대일로 짝지으므로
            // 두 번 세면 한 질의가 가드 둘의 짝을 다 채운다).
            private readonly HashSet<QuerySpecification> _seen = new(ReferenceEqualityComparer.Instance);

            public override void ExplicitVisit(ExistsPredicate node)
            {
                if (Unwrap(node.Subquery?.QueryExpression) is { } spec) Add(spec);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                if (IsTopOne(node) || IsSingleCount(node)) Add(node);
                base.ExplicitVisit(node);
            }

            private void Add(QuerySpecification spec)
            {
                if (FromSpecification(spec) is { } query && _seen.Add(spec)) Queries.Add(query);
            }

            private static bool IsTopOne(QuerySpecification node) =>
                node.TopRowFilter?.Expression is { } top &&
                Unparenthesize(top) is IntegerLiteral { Value: "1" };

            private static bool IsSingleCount(QuerySpecification node) =>
                node.SelectElements.Count == 1 &&
                node.SelectElements[0] is SelectScalarExpression { Expression: FunctionCall call } &&
                call.FunctionName.Value.Equals("COUNT", StringComparison.OrdinalIgnoreCase);

            private static ScalarExpression Unparenthesize(ScalarExpression expression) =>
                expression is ParenthesisExpression parenthesis ? Unparenthesize(parenthesis.Expression) : expression;
        }
    }
}
