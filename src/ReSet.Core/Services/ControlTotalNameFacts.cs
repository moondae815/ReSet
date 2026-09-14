using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 본문의 SQL 에서 제어 합계 표(<c>ControlName</c> 칸이 있는 계약 표와 그 별칭)에 대한 쓰기·읽기 사실을 뽑는다.
    /// <see cref="MechanicalValidator.ValidateControlTotalNameConsistency"/> 의 재료다.
    ///
    /// [왜 파서인가 - 2026-09-13 최종 리뷰 I2·I3·M5] 첫 구현은 문장의 식별자 모양 리터럴을 전부 쓰기 이름으로 모았다.
    /// 그러면 이름이 다른 문장(임시 표)에서 오고 INSERT 문장엔 <c>N'Y'</c> 같은 엉뚱한 리터럴만 있을 때 「무엇을 쓰는지
    /// 모른다」 침묵이 풀려 옳은 읽는 단계를 고발했다. 읽기 몫도 같은 문장의 <b>다른 표</b> <c>StepCode</c> 조건까지 셌다.
    /// 그래서 <b>ControlName 칸에 실제로 들어가는 값</b>만 이름으로 인정하고, 조건은 제어 표 자신의 것만 본다.
    /// 문장 분할은 <see cref="StepSqlStatementReader.SplitAtTopLevelSemicolons"/> 를 재사용한다 - 토큰 기반이라 문자열·주석 안의
    /// <c>;</c> 에 안 속는다. 조각이 파싱되지 않으면 그 조각은 읽기에서 빠지고, 제어 표에 쓰는 조각이면 「모름」이 된다.
    /// </summary>
    internal static class ControlTotalNameFacts
    {
        internal sealed record Write(IReadOnlyCollection<string> Names, bool Unknown);

        internal sealed record Read(IReadOnlyCollection<string> Owners, IReadOnlyCollection<string> Names);

        private static readonly Regex SqlFence = new(@"```sql[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        internal static (List<Write> Writes, List<Read> Reads) Collect(string markdown, IReadOnlyCollection<string> tableBareNames)
        {
            var writes = new List<Write>();
            var reads = new List<Read>();
            if (string.IsNullOrWhiteSpace(markdown) || tableBareNames.Count == 0) return (writes, reads);

            var tables = new HashSet<string>(tableBareNames, StringComparer.OrdinalIgnoreCase);
            var writeText = new Regex(
                $@"\b(?:INSERT\s+INTO|MERGE(?:\s+INTO)?)\s+(?:\[?\w+\]?\.)?\[?(?:{string.Join("|", tables.Select(Regex.Escape))})\]?\b",
                RegexOptions.IgnoreCase);

            foreach (Match fence in SqlFence.Matches(markdown))
            {
                var sql = fence.Groups["body"].Value;
                var tokens = new TSql160Parser(initialQuotedIdentifiers: true).GetTokenStream(new StringReader(sql), out var tokenErrors);
                if (tokens == null || tokenErrors is { Count: > 0 })
                {
                    if (writeText.IsMatch(sql)) writes.Add(new Write(Array.Empty<string>(), Unknown: true));
                    continue;
                }

                foreach (var (start, end, _) in StepSqlStatementReader.SplitAtTopLevelSemicolons(sql, tokens))
                {
                    var chunk = sql.Substring(start, end - start);
                    if (string.IsNullOrWhiteSpace(chunk)) continue;

                    var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(chunk), out var errors);
                    if (fragment == null || errors is { Count: > 0 })
                    {
                        if (writeText.IsMatch(chunk)) writes.Add(new Write(Array.Empty<string>(), Unknown: true));
                        continue;
                    }

                    var visitor = new Visitor(tables);
                    fragment.Accept(visitor);
                    writes.AddRange(visitor.Writes);
                    reads.AddRange(visitor.Reads);
                }
            }

            return (writes, reads);
        }

        /// <summary>
        /// [D1 자기 제외] 이 SQL 에서 표 <paramref name="tableBareName"/> 을 <b>FROM·JOIN 으로 읽는 질의</b>가 그 표에 한정된(또는 한정자 없는)
        /// <c>StepCode &lt;&gt; / != '코드'</c> 로 제외하는 단계 코드들. 표가 INSERT 대상으로만 나오거나, 조건이 조인한 다른 표의 것이면 세지 않는다.
        /// 조각이 파싱되지 않으면 그 조각은 건너뛴다(덜 보고한다). 판독: <c>docs/audit-reports/2026-09-14-L1-귀속실패-측정.md</c>.
        /// </summary>
        internal static IReadOnlySet<string> SelfExcludedStepCodes(string sql, string tableBareName)
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(sql)) return codes;

            var tokens = new TSql160Parser(initialQuotedIdentifiers: true).GetTokenStream(new StringReader(sql), out var tokenErrors);
            if (tokens == null || tokenErrors is { Count: > 0 }) return codes;

            foreach (var (start, end, _) in StepSqlStatementReader.SplitAtTopLevelSemicolons(sql, tokens))
            {
                var chunk = sql.Substring(start, end - start);
                if (string.IsNullOrWhiteSpace(chunk)) continue;

                var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(chunk), out var errors);
                if (fragment == null || errors is { Count: > 0 }) continue;

                var visitor = new SelfExclusionVisitor(tableBareName);
                fragment.Accept(visitor);
                codes.UnionWith(visitor.Codes);
            }

            return codes;
        }

        private sealed class SelfExclusionVisitor : TSqlFragmentVisitor
        {
            private readonly string _table;

            internal SelfExclusionVisitor(string table) => _table = table;

            internal HashSet<string> Codes { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(QuerySpecification node)
            {
                var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in node.FromClause?.TableReferences ?? (IList<TableReference>)Array.Empty<TableReference>())
                    AddQualifiers(reference, qualifiers);

                if (qualifiers.Count > 0 && node.WhereClause?.SearchCondition != null)
                    Collect(node.WhereClause.SearchCondition, qualifiers);

                base.ExplicitVisit(node);
            }

            private void AddQualifiers(TableReference reference, HashSet<string> qualifiers)
            {
                switch (reference)
                {
                    case NamedTableReference named when named.SchemaObject?.BaseIdentifier?.Value is { } bare &&
                                                        bare.Equals(_table, StringComparison.OrdinalIgnoreCase):
                        qualifiers.Add(bare);
                        if (named.Alias != null) qualifiers.Add(named.Alias.Value);
                        break;
                    case JoinTableReference join:
                        AddQualifiers(join.FirstTableReference, qualifiers);
                        AddQualifiers(join.SecondTableReference, qualifiers);
                        break;
                    case JoinParenthesisTableReference parenthesis:
                        AddQualifiers(parenthesis.Join, qualifiers);
                        break;
                }
            }

            private void Collect(BooleanExpression expression, HashSet<string> qualifiers)
            {
                switch (expression)
                {
                    case BooleanBinaryExpression binary:
                        Collect(binary.FirstExpression, qualifiers);
                        Collect(binary.SecondExpression, qualifiers);
                        break;
                    case BooleanParenthesisExpression parenthesis:
                        Collect(parenthesis.Expression, qualifiers);
                        break;
                    case BooleanComparisonExpression
                    {
                        ComparisonType: BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                    } comparison:
                        var (column, literal) = comparison.FirstExpression is ColumnReferenceExpression c1 && comparison.SecondExpression is StringLiteral l1
                            ? (c1, l1)
                            : comparison.SecondExpression is ColumnReferenceExpression c2 && comparison.FirstExpression is StringLiteral l2
                                ? (c2, l2)
                                : ((ColumnReferenceExpression?)null, (StringLiteral?)null);
                        if (column?.MultiPartIdentifier?.Identifiers is not { Count: > 0 } ids || literal == null) break;
                        if (!ids[^1].Value.Equals("StepCode", StringComparison.OrdinalIgnoreCase)) break;
                        if (ids.Count > 1 && !qualifiers.Contains(ids[^2].Value)) break;
                        Codes.Add(literal.Value);
                        break;
                }
            }
        }

        private sealed class Visitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _tables;

            internal Visitor(HashSet<string> tables) => _tables = tables;

            internal List<Write> Writes { get; } = new();

            internal List<Read> Reads { get; } = new();

            // 지금 방문 중인 문장의 CTE. 「조인으로 끌어온 이름」이 같은 문장의 CTE 를 풀 때 쓴다.
            private IReadOnlyDictionary<string, CommonTableExpression> _ctes =
                new Dictionary<string, CommonTableExpression>(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(SelectStatement node)
            {
                var outer = _ctes;
                _ctes = CtesOf(node.WithCtesAndXmlNamespaces);
                base.ExplicitVisit(node);
                _ctes = outer;
            }

            public override void ExplicitVisit(InsertStatement node)
            {
                if (IsControlTable(node.InsertSpecification?.Target))
                {
                    Writes.Add(ReadInsert(node));
                }

                // WITH 가 INSERT 에 붙는 모양(`WITH … INSERT INTO … SELECT … JOIN …`)도 같은 문장 CTE 를 풀어야 한다 -
                // 처음엔 SelectStatement 에서만 채워 조인으로 끌어온 이름이 조용했다(최종 리뷰 Important 1).
                var outer = _ctes;
                _ctes = CtesOf(node.WithCtesAndXmlNamespaces);
                base.ExplicitVisit(node);
                _ctes = outer;
            }

            private static IReadOnlyDictionary<string, CommonTableExpression> CtesOf(WithCtesAndXmlNamespaces? with) =>
                with?.CommonTableExpressions
                    .GroupBy(c => c.ExpressionName.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, CommonTableExpression>(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(MergeStatement node)
            {
                // MERGE 의 이름 출처까지 따라가지 않는다 - 모른다고 둔다(덜 보고한다).
                if (IsControlTable(node.MergeSpecification?.Target)) Writes.Add(new Write(Array.Empty<string>(), Unknown: true));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                var qualifiers = ControlTableQualifiers(node.FromClause);
                if (qualifiers.Count > 0 && node.WhereClause?.SearchCondition != null)
                {
                    var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectFilters(node.WhereClause.SearchCondition, qualifiers, owners, names);
                    if (owners.Count > 0 && names.Count > 0) Reads.Add(new Read(owners, names));
                }

                CollectJoinedNameReads(node);
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// [조인으로 끌어온 이름 - POQSettleBatch13 V04, 2026-09-14] 제어 표 읽기 문장에 <c>ControlName</c> 리터럴이 없고, 이름은
            /// 조인 조건 <c>X.ControlName = Y.ControlName</c> 로 붙는 다른 출처(같은 문장의 CTE·인라인 <c>VALUES</c>)에 리터럴로만 있는 모양.
            /// X 는 몫(<c>StepCode</c> 리터럴)이 있는 제어 표 읽기(같은 문장 CTE 또는 제어 표 자신), Y 는 ControlName 출력이
            /// <b>전부</b> 문자열 리터럴인 출처여야 한다. 하나라도 어긋나면 더하지 않는다(덜 보고한다).
            /// </summary>
            private void CollectJoinedNameReads(QuerySpecification node)
            {
                if (node.FromClause == null) return;

                var sources = new List<TableReference>();
                var conditions = new List<BooleanExpression>();
                foreach (var reference in node.FromClause.TableReferences) JoinParts(reference, sources, conditions);
                if (conditions.Count == 0) return;

                foreach (var condition in conditions)
                {
                    foreach (var (left, right) in ControlNameEqualities(condition))
                    {
                        if (Source(sources, left) is not { } leftSource || Source(sources, right) is not { } rightSource) continue;

                        foreach (var (readerSide, nameSide) in new[] { (leftSource, rightSource), (rightSource, leftSource) })
                        {
                            if (ReaderOwners(node, readerSide) is not { Count: > 0 } owners) continue;
                            if (LiteralNames(nameSide) is not { Count: > 0 } names) continue;
                            Reads.Add(new Read(owners, names));
                        }
                    }
                }
            }

            private static void JoinParts(TableReference reference, List<TableReference> sources, List<BooleanExpression> conditions)
            {
                switch (reference)
                {
                    case QualifiedJoin qualified:
                        JoinParts(qualified.FirstTableReference, sources, conditions);
                        JoinParts(qualified.SecondTableReference, sources, conditions);
                        if (qualified.SearchCondition != null) conditions.Add(qualified.SearchCondition);
                        break;
                    case JoinTableReference join:
                        JoinParts(join.FirstTableReference, sources, conditions);
                        JoinParts(join.SecondTableReference, sources, conditions);
                        break;
                    case JoinParenthesisTableReference parenthesis:
                        JoinParts(parenthesis.Join, sources, conditions);
                        break;
                    default:
                        sources.Add(reference);
                        break;
                }
            }

            /// <summary>AND 로 이어진 <c>한정자.ControlName = 한정자.ControlName</c> 등식의 두 한정자.</summary>
            private static IEnumerable<(string Left, string Right)> ControlNameEqualities(BooleanExpression expression)
            {
                switch (expression)
                {
                    case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                        foreach (var pair in ControlNameEqualities(and.FirstExpression)) yield return pair;
                        foreach (var pair in ControlNameEqualities(and.SecondExpression)) yield return pair;
                        break;
                    case BooleanParenthesisExpression parenthesis:
                        foreach (var pair in ControlNameEqualities(parenthesis.Expression)) yield return pair;
                        break;
                    case BooleanComparisonExpression
                    {
                        ComparisonType: BooleanComparisonType.Equals,
                        FirstExpression: ColumnReferenceExpression first,
                        SecondExpression: ColumnReferenceExpression second
                    } when QualifiedControlName(first) is { } left && QualifiedControlName(second) is { } right:
                        yield return (left, right);
                        break;
                }
            }

            private static string? QualifiedControlName(ColumnReferenceExpression column) =>
                column.MultiPartIdentifier?.Identifiers is { Count: >= 2 } ids &&
                ids[^1].Value.Equals("ControlName", StringComparison.OrdinalIgnoreCase)
                    ? ids[^2].Value
                    : null;

            private static TableReference? Source(IEnumerable<TableReference> sources, string qualifier) =>
                sources.FirstOrDefault(r => r switch
                {
                    TableReferenceWithAlias { Alias: { } alias } => alias.Value.Equals(qualifier, StringComparison.OrdinalIgnoreCase),
                    NamedTableReference named => named.SchemaObject.BaseIdentifier.Value.Equals(qualifier, StringComparison.OrdinalIgnoreCase),
                    _ => false
                });

            /// <summary>
            /// 이 출처가 몫 리터럴이 있는 제어 표 읽기면 그 몫. 같은 문장 CTE 는 본문의 모든 가지가 그래야 하고, 제어 표 자신이면
            /// 바깥 문장의 WHERE 에서 그 한정자의 조건을 본다. 아니면 null.
            ///
            /// 읽기 쪽이 이름 리터럴을 따로 걸고 있어도 몫으로 인정한다 - 조인한 이름 출처와 안 겹치면 그 조인은 어떤 행도 짝짓지
            /// 못하므로 그 자체로 결함이다(처음엔 「이름 리터럴이 없을 때만」으로 좁혔으나 되돌림에서 지키는 것이 없었다).
            /// </summary>
            private IReadOnlyCollection<string>? ReaderOwners(QuerySpecification outer, TableReference source)
            {
                if (source is NamedTableReference named && IsControlTable(named))
                {
                    var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { named.SchemaObject.BaseIdentifier.Value };
                    if (named.Alias != null) qualifiers.Add(named.Alias.Value);
                    return Owners(outer.WhereClause?.SearchCondition, qualifiers);
                }

                if (CteBody(source) is not { } body) return null;

                var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in Flatten(body))
                {
                    var qualifiers = ControlTableQualifiers(spec.FromClause);
                    if (qualifiers.Count == 0) return null;
                    if (Owners(spec.WhereClause?.SearchCondition, qualifiers) is not { } specOwners) return null;
                    owners.UnionWith(specOwners);
                }

                return owners.Count > 0 ? owners : null;
            }

            private static IReadOnlyCollection<string>? Owners(BooleanExpression? where, HashSet<string> qualifiers)
            {
                if (where == null) return null;
                var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectFilters(where, qualifiers, owners, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                return owners.Count > 0 ? owners : null;
            }

            /// <summary>이 출처의 <c>ControlName</c> 출력이 전부 문자열 리터럴이면 그 이름들. 하나라도 아니면 null.</summary>
            private IReadOnlyCollection<string>? LiteralNames(TableReference source)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (source is InlineDerivedTable inline)
                    return ResolveInline(new[] { inline }, "ControlName", names) && names.Count > 0 ? names : null;

                if (source is not NamedTableReference { SchemaObject.SchemaIdentifier: null } named ||
                    !_ctes.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cte)) return null;

                var specs = Flatten(cte.QueryExpression).ToList();
                if (specs.Count == 0) return null;

                // 열 위치: CTE 열 목록이 있으면 그것, 없으면 첫 가지의 별칭·열 이름.
                var position = cte.Columns.Count > 0
                    ? cte.Columns.ToList().FindIndex(c => c.Value.Equals("ControlName", StringComparison.OrdinalIgnoreCase))
                    : specs[0].SelectElements.ToList().FindIndex(e => e is SelectScalarExpression scalar &&
                        (scalar.ColumnName?.Value ?? (scalar.Expression as ColumnReferenceExpression is { } c ? LastIdentifier(c) : null))
                            ?.Equals("ControlName", StringComparison.OrdinalIgnoreCase) == true);
                if (position < 0) return null;

                foreach (var spec in specs)
                {
                    if (spec.SelectElements.Count <= position ||
                        spec.SelectElements[position] is not SelectScalarExpression { Expression: StringLiteral literal }) return null;
                    names.Add(literal.Value);
                }

                return names;
            }

            private QueryExpression? CteBody(TableReference source) => source switch
            {
                NamedTableReference { SchemaObject.SchemaIdentifier: null } named when
                    _ctes.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cte) => cte.QueryExpression,
                QueryDerivedTable derived => derived.QueryExpression,
                _ => null
            };

            private bool IsControlTable(TableReference? reference) =>
                reference is NamedTableReference named &&
                named.SchemaObject?.BaseIdentifier?.Value is { } bare &&
                _tables.Contains(bare);

            private Write ReadInsert(InsertStatement node)
            {
                var columns = node.InsertSpecification.Columns;
                var index = columns.ToList().FindIndex(c => LastIdentifier(c) is { } n && n.Equals("ControlName", StringComparison.OrdinalIgnoreCase));
                if (index < 0) return new Write(Array.Empty<string>(), Unknown: true);

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unknown = false;

                switch (node.InsertSpecification.InsertSource)
                {
                    case ValuesInsertSource values:
                        foreach (var row in values.RowValues)
                        {
                            if (row.ColumnValues.Count > index && row.ColumnValues[index] is StringLiteral literal) names.Add(literal.Value);
                            else unknown = true;
                        }
                        break;

                    case SelectInsertSource select:
                        var ctes = node.WithCtesAndXmlNamespaces?.CommonTableExpressions
                            .ToDictionary(c => c.ExpressionName.Value, c => (TSqlFragment)c.QueryExpression, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, TSqlFragment>(StringComparer.OrdinalIgnoreCase);
                        foreach (var spec in Flatten(select.Select))
                        {
                            if (spec.SelectElements.Count <= index ||
                                spec.SelectElements[index] is not SelectScalarExpression { Expression: { } expression })
                            {
                                unknown = true;
                                continue;
                            }

                            switch (expression)
                            {
                                case StringLiteral literal:
                                    names.Add(literal.Value);
                                    break;
                                // 같은 문장의 인라인 VALUES 표 열을 끌어온 것(실물 POQSettleBatch11/S13 의
                                // CROSS APPLY (VALUES (N'LedgerRowCount', …)) AS V(ControlName, …))만 따라간다.
                                case ColumnReferenceExpression column when LastIdentifier(column) is { } columnName &&
                                                                          ResolveInline(SourceInlineTables(spec, column, ctes), columnName, names):
                                    break;
                                default:
                                    unknown = true;
                                    break;
                            }
                        }
                        break;

                    default:
                        unknown = true;
                        break;
                }

                return new Write(names, unknown || names.Count == 0);
            }

            private static bool ResolveInline(IReadOnlyList<InlineDerivedTable> inline, string columnName, HashSet<string> names)
            {
                var found = false;
                foreach (var table in inline)
                {
                    var position = table.Columns.ToList().FindIndex(c => c.Value.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                    if (position < 0) continue;

                    foreach (var row in table.RowValues)
                    {
                        if (row.ColumnValues.Count <= position || row.ColumnValues[position] is not StringLiteral literal) return false;
                        names.Add(literal.Value);
                    }

                    found = true;
                }

                return found;
            }

            /// <summary>
            /// 이 열이 오는 출처 안의 인라인 VALUES 표들. 출처는 열 한정자(없으면 FROM 의 유일한 출처)가 가리키는
            /// 인라인 표 자신, 또는 같은 문장 CTE 의 본문이다. 출처가 임시 표·물리 표면 빈 목록 - 이름을 모른다(리뷰 I2).
            /// </summary>
            private static List<InlineDerivedTable> SourceInlineTables(
                QuerySpecification spec, ColumnReferenceExpression column, IReadOnlyDictionary<string, TSqlFragment> ctes)
            {
                var sources = new List<TableReference>();
                foreach (var reference in spec.FromClause?.TableReferences ?? (IList<TableReference>)Array.Empty<TableReference>())
                    Leaves(reference, sources);

                var identifiers = column.MultiPartIdentifier.Identifiers;
                var qualifier = identifiers.Count > 1 ? identifiers[^2].Value : null;
                var matched = qualifier == null
                    ? (sources.Count == 1 ? sources : new List<TableReference>())
                    : sources.Where(r => r switch
                    {
                        InlineDerivedTable inline => inline.Alias?.Value.Equals(qualifier, StringComparison.OrdinalIgnoreCase) == true,
                        NamedTableReference named => (named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value)
                            .Equals(qualifier, StringComparison.OrdinalIgnoreCase),
                        _ => false
                    }).ToList();

                var result = new List<InlineDerivedTable>();
                foreach (var source in matched)
                {
                    switch (source)
                    {
                        case InlineDerivedTable inline:
                            result.Add(inline);
                            break;
                        case NamedTableReference named when named.SchemaObject.SchemaIdentifier == null &&
                                                            ctes.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var body):
                            result.AddRange(InlineTables(body));
                            break;
                    }
                }

                return result;
            }

            private static void Leaves(TableReference reference, List<TableReference> leaves)
            {
                switch (reference)
                {
                    case JoinTableReference join:
                        Leaves(join.FirstTableReference, leaves);
                        Leaves(join.SecondTableReference, leaves);
                        break;
                    case JoinParenthesisTableReference parenthesis:
                        Leaves(parenthesis.Join, leaves);
                        break;
                    default:
                        leaves.Add(reference);
                        break;
                }
            }

            private static List<InlineDerivedTable> InlineTables(TSqlFragment root)
            {
                var collector = new InlineCollector();
                root.Accept(collector);
                return collector.Tables;
            }

            private HashSet<string> ControlTableQualifiers(FromClause? from)
            {
                var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (from == null) return qualifiers;

                foreach (var reference in from.TableReferences) AddQualifiers(reference, qualifiers);
                return qualifiers;
            }

            private void AddQualifiers(TableReference reference, HashSet<string> qualifiers)
            {
                switch (reference)
                {
                    case NamedTableReference named when IsControlTable(named):
                        qualifiers.Add(named.SchemaObject.BaseIdentifier.Value);
                        if (named.Alias != null) qualifiers.Add(named.Alias.Value);
                        break;
                    case JoinTableReference join:
                        AddQualifiers(join.FirstTableReference, qualifiers);
                        AddQualifiers(join.SecondTableReference, qualifiers);
                        break;
                    case JoinParenthesisTableReference parenthesis:
                        AddQualifiers(parenthesis.Join, qualifiers);
                        break;
                }
            }

            /// <summary>
            /// 한정자가 없거나 제어 표(또는 그 별칭)로 한정된 <c>StepCode</c>·<c>ControlName</c> 의 <c>=</c>·<c>IN</c> 리터럴.
            /// 다른 표의 같은 이름 컬럼(<c>j.StepCode</c>)은 보지 않는다(리뷰 I3).
            /// </summary>
            private static void CollectFilters(BooleanExpression expression, HashSet<string> qualifiers,
                HashSet<string> owners, HashSet<string> names)
            {
                switch (expression)
                {
                    case BooleanBinaryExpression binary:
                        CollectFilters(binary.FirstExpression, qualifiers, owners, names);
                        CollectFilters(binary.SecondExpression, qualifiers, owners, names);
                        break;
                    case BooleanParenthesisExpression parenthesis:
                        CollectFilters(parenthesis.Expression, qualifiers, owners, names);
                        break;
                    case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } comparison:
                        var (column, literal) = comparison.FirstExpression is ColumnReferenceExpression c1 && comparison.SecondExpression is StringLiteral l1
                            ? (c1, l1)
                            : comparison.SecondExpression is ColumnReferenceExpression c2 && comparison.FirstExpression is StringLiteral l2
                                ? (c2, l2)
                                : ((ColumnReferenceExpression?)null, (StringLiteral?)null);
                        if (column != null && literal != null) Add(column, new[] { literal.Value }, qualifiers, owners, names);
                        break;
                    case InPredicate { NotDefined: false, Expression: ColumnReferenceExpression inColumn } inPredicate:
                        var values = inPredicate.Values.OfType<StringLiteral>().Select(v => v.Value).ToList();
                        if (values.Count == inPredicate.Values.Count && values.Count > 0) Add(inColumn, values, qualifiers, owners, names);
                        break;
                }
            }

            private static void Add(ColumnReferenceExpression column, IEnumerable<string> values, HashSet<string> qualifiers,
                HashSet<string> owners, HashSet<string> names)
            {
                var identifiers = column.MultiPartIdentifier?.Identifiers;
                if (identifiers == null || identifiers.Count == 0) return;
                if (identifiers.Count > 1 && !qualifiers.Contains(identifiers[^2].Value)) return;

                var name = identifiers[^1].Value;
                if (name.Equals("StepCode", StringComparison.OrdinalIgnoreCase)) owners.UnionWith(values);
                else if (name.Equals("ControlName", StringComparison.OrdinalIgnoreCase)) names.UnionWith(values);
            }

            private static IEnumerable<QuerySpecification> Flatten(QueryExpression expression)
            {
                switch (expression)
                {
                    case QuerySpecification spec:
                        yield return spec;
                        break;
                    case BinaryQueryExpression binary:
                        foreach (var s in Flatten(binary.FirstQueryExpression)) yield return s;
                        foreach (var s in Flatten(binary.SecondQueryExpression)) yield return s;
                        break;
                    case QueryParenthesisExpression parenthesis:
                        foreach (var s in Flatten(parenthesis.QueryExpression)) yield return s;
                        break;
                }
            }

            private static string? LastIdentifier(ColumnReferenceExpression column) =>
                column.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null;
        }

        private sealed class InlineCollector : TSqlFragmentVisitor
        {
            internal List<InlineDerivedTable> Tables { get; } = new();

            public override void ExplicitVisit(InlineDerivedTable node)
            {
                Tables.Add(node);
                base.ExplicitVisit(node);
            }
        }
    }
}
