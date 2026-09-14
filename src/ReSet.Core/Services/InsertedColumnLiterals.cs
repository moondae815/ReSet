using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL 에서 <c>INSERT … (컬럼 목록) VALUES (…)</c> · <c>INSERT … SELECT …</c> · <c>MERGE … WHEN NOT MATCHED THEN INSERT (…) VALUES (…)</c> 가
    /// <b>주어진 컬럼 위치에 넣는 문자열 리터럴</b>들을 뽑는다(발생 수만큼, 중복 포함).
    ///
    /// 쓰는 곳 둘이 같은 읽기를 쓴다 - 규칙이 두 곳에 있으면 조용히 갈린다:
    /// <see cref="RunGateFacts.WrittenStepCodes"/>(게이트 면제 - StepCode 칸) · <see cref="MechanicalValidator.ValidateControlStatusTerminalWrites"/>
    /// (T25 - 상태 칸. 2026-09-14 전에는 대입 <c>컬럼 = N'값'</c> 만 세어 위치 INSERT 를 못 보고 거짓 「하한 미달」 배너를 배송했다).
    ///
    /// 펜스 본문을 최상위 세미콜론 문장으로 자르고 조각마다 파싱한다. 파싱되지 않는 조각은 건너뛴다.
    /// </summary>
    internal static class InsertedColumnLiterals
    {
        internal static IReadOnlyList<string> Collect(string sql, string columnName, Func<string, bool>? targetTableFilter = null)
        {
            var literals = new List<string>();
            if (string.IsNullOrWhiteSpace(sql)) return literals;

            var tokens = new TSql160Parser(initialQuotedIdentifiers: true).GetTokenStream(new StringReader(sql), out var tokenErrors);
            if (tokens == null || tokenErrors is { Count: > 0 }) return literals;

            foreach (var (start, end, _) in StepSqlStatementReader.SplitAtTopLevelSemicolons(sql, tokens))
            {
                var chunk = sql.Substring(start, end - start);
                if (string.IsNullOrWhiteSpace(chunk)) continue;

                var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(chunk), out var errors);
                if (fragment == null || errors is { Count: > 0 }) continue;

                var visitor = new Visitor(columnName, targetTableFilter);
                fragment.Accept(visitor);
                literals.AddRange(visitor.Literals);
            }

            return literals;
        }

        private sealed class Visitor : TSqlFragmentVisitor
        {
            private readonly string _column;
            private readonly Func<string, bool>? _targetFilter;

            internal Visitor(string column, Func<string, bool>? targetFilter) => (_column, _targetFilter) = (column, targetFilter);

            internal List<string> Literals { get; } = new();

            public override void ExplicitVisit(InsertStatement node)
            {
                if (IsTarget(node.InsertSpecification?.Target))
                    Collect(node.InsertSpecification!.Columns, node.InsertSpecification.InsertSource);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(MergeStatement node)
            {
                if (IsTarget(node.MergeSpecification?.Target))
                {
                    foreach (var action in node.MergeSpecification!.ActionClauses.Select(c => c.Action).OfType<InsertMergeAction>())
                        Collect(action.Columns, action.Source);
                }
                base.ExplicitVisit(node);
            }

            private bool IsTarget(TableReference? reference) =>
                reference is NamedTableReference named && named.SchemaObject?.BaseIdentifier?.Value is { } bare &&
                (_targetFilter == null || _targetFilter(bare));

            private void Collect(IList<ColumnReferenceExpression> columns, InsertSource? source)
            {
                var position = columns.ToList().FindIndex(c =>
                    c.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids && ids[^1].Value.Equals(_column, StringComparison.OrdinalIgnoreCase));
                if (position < 0) return;

                switch (source)
                {
                    case ValuesInsertSource values:
                        foreach (var row in values.RowValues)
                            if (row.ColumnValues.Count > position && row.ColumnValues[position] is StringLiteral literal) Literals.Add(literal.Value);
                        break;
                    case SelectInsertSource select:
                        foreach (var spec in Flatten(select.Select))
                            if (spec.SelectElements.Count > position &&
                                spec.SelectElements[position] is SelectScalarExpression { Expression: StringLiteral literal }) Literals.Add(literal.Value);
                        break;
                }
            }

            private static IEnumerable<QuerySpecification> Flatten(QueryExpression expression) => expression switch
            {
                QuerySpecification spec => new[] { spec },
                BinaryQueryExpression binary => Flatten(binary.FirstQueryExpression).Concat(Flatten(binary.SecondQueryExpression)),
                QueryParenthesisExpression parenthesis => Flatten(parenthesis.QueryExpression),
                _ => Array.Empty<QuerySpecification>()
            };
        }
    }
}
