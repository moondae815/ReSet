using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL 펜스에서 <b>제어 계약 밖 <c>batch</c> 표</b>의 컬럼 정의와 컬럼 참조를 뽑는다.
    /// <see cref="MechanicalValidator"/> 의 「계약 밖 batch 표의 컬럼 계약 분열」 검사 재료다.
    ///
    /// [오라클은 문서 자신의 DDL 이다 - 자기일관성]
    /// 계약 표(<see cref="BatchControlContract"/>)의 컬럼은 계약이 정하고 어휘 검사가 본다. 계약 <b>밖</b> 표는 이 문서가
    /// 스스로 만들고 스스로 읽으므로, 두 자리가 어긋나면 배포하면 컬럼 없음 오류다 - 외부 오라클 없이 서는 검사이고
    /// <c>CheckDuplicateProjectionNames</c>·T36 과 같은 계열이다.
    ///
    /// [침묵의 범위를 알고 써라]
    /// ① 정의(<c>CREATE TABLE</c>)가 없으면 판정하지 않는다 - 부트스트랩이 만들고 산문이 컬럼을 적는 실물이 코퍼스에 여덟 자리다
    /// (그 판정은 산문 읽기이고 실측 오탐이 15 중 14 였다) ② 테이블이 둘 이상인 질의의 <b>비한정</b> 컬럼은 소속을 말할 수 없어
    /// 버린다(한정된 것만 본다) ③ 동적 SQL 문자열 안의 문장은 파서가 못 본다 ④ <c>SELECT *</c> 는 컬럼 참조가 아니다.
    /// 판독: docs/audit-reports/2026-09-16-제어표-컬럼계약-검사-사전선언.md
    /// </summary>
    internal static class ControlTableColumnFacts
    {
        /// <param name="Table">계약 밖 표의 맨이름.</param>
        /// <param name="Columns">그 표의 <c>CREATE TABLE</c>·<c>ALTER TABLE … ADD</c> 컬럼 합집합.</param>
        internal readonly record struct Definition(string Table, IReadOnlySet<string> Columns);

        /// <param name="Table">계약 밖 표의 맨이름.</param>
        /// <param name="Column">그 표 소속으로 읽히는 컬럼 이름.</param>
        /// <param name="Statement">그 참조가 있던 문장 원문(귀속 어휘의 재료).</param>
        internal readonly record struct Reference(string Table, string Column, string Statement);

        /// <summary>한 SQL 펜스에서 정의와 참조를 함께 뽑는다. 파싱이 실패하면 빈 결과다(부분 파스로 판정하지 않는다).</summary>
        internal static (IReadOnlyList<Definition> Definitions, IReadOnlyList<Reference> References) Read(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return (Array.Empty<Definition>(), Array.Empty<Reference>());
            }

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(sql);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return (Array.Empty<Definition>(), Array.Empty<Reference>());
                }

                var visitor = new ControlTableVisitor();
                fragment.Accept(visitor);
                return (visitor.Definitions, visitor.References);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[제어표 컬럼 계약] SQL 읽기 실패 - 빈 결과로 진행합니다.");
                return (Array.Empty<Definition>(), Array.Empty<Reference>());
            }
        }

        private static bool IsBatchSchema(SchemaObjectName? name) =>
            string.Equals(name?.SchemaIdentifier?.Value, "batch", StringComparison.OrdinalIgnoreCase);

        private static string BareTable(SchemaObjectName? name) => name?.BaseIdentifier?.Value ?? string.Empty;

        /// <summary>계약이 아는 표인가. 계약 표의 컬럼은 계약이 정하므로 이 검사의 관할이 아니다.</summary>
        private static bool IsContractTable(string bare) =>
            BatchControlContract.Tables.Any(t =>
                string.Equals(BatchControlContract.BareName(t.Name), bare, StringComparison.OrdinalIgnoreCase));

        private sealed class ControlTableVisitor : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, HashSet<string>> _definitions = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<Reference> _references = new();

            public IReadOnlyList<Definition> Definitions =>
                _definitions.Select(e => new Definition(e.Key, e.Value)).ToList();

            public IReadOnlyList<Reference> References => _references;

            public override void Visit(CreateTableStatement node)
            {
                var bare = BareTable(node.SchemaObjectName);
                if (!IsBatchSchema(node.SchemaObjectName) || bare.Length == 0 || IsContractTable(bare)) return;

                var columns = Columns(bare);
                foreach (var column in node.Definition?.ColumnDefinitions ?? new List<ColumnDefinition>())
                {
                    var name = column.ColumnIdentifier?.Value;
                    if (!string.IsNullOrEmpty(name)) columns.Add(name);
                }
            }

            /// <summary>나중에 더한 컬럼도 정의다 - 합집합으로 본다(보수적: 발화를 줄이는 쪽).</summary>
            public override void Visit(AlterTableAddTableElementStatement node)
            {
                var bare = BareTable(node.SchemaObjectName);
                if (!IsBatchSchema(node.SchemaObjectName) || bare.Length == 0 || IsContractTable(bare)) return;

                var columns = Columns(bare);
                foreach (var column in node.Definition?.ColumnDefinitions ?? new List<ColumnDefinition>())
                {
                    var name = column.ColumnIdentifier?.Value;
                    if (!string.IsNullOrEmpty(name)) columns.Add(name);
                }
            }

            public override void Visit(InsertSpecification node)
            {
                if (node.Target is not NamedTableReference target) return;
                var bare = BareTable(target.SchemaObject);
                if (!IsBatchSchema(target.SchemaObject) || bare.Length == 0 || IsContractTable(bare)) return;

                foreach (var column in node.Columns ?? new List<ColumnReferenceExpression>())
                {
                    var name = LastIdentifier(column);
                    if (name != null) _references.Add(new Reference(bare, name, StatementText(node)));
                }
            }

            /// <summary>
            /// 단일 테이블 <c>FROM</c> 질의만 비한정 컬럼을 그 표 것으로 읽는다. 조인이 있으면 <b>한정된</b> 참조만 본다 -
            /// 비한정 컬럼의 소속을 말할 수 없으면 보고하지 않는다(작성 계약 7).
            /// </summary>
            public override void Visit(QuerySpecification node)
            {
                var tables = new List<NamedTableReference>();
                var unresolved = false;
                foreach (var reference in node.FromClause?.TableReferences ?? new List<TableReference>())
                {
                    CollectTables(reference, tables, ref unresolved);
                }

                var batchTables = tables
                    .Where(t => IsBatchSchema(t.SchemaObject) && !IsContractTable(BareTable(t.SchemaObject)))
                    .ToList();
                if (batchTables.Count == 0) return;

                var single = tables.Count == 1 && !unresolved;
                foreach (var table in batchTables)
                {
                    var bare = BareTable(table.SchemaObject);
                    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bare };
                    if (!string.IsNullOrEmpty(table.Alias?.Value)) aliases.Add(table.Alias!.Value);

                    var collector = new ColumnCollector();
                    node.Accept(collector);
                    foreach (var (column, qualifier) in collector.Columns)
                    {
                        var mine = qualifier != null ? aliases.Contains(qualifier) : single;
                        if (mine) _references.Add(new Reference(bare, column, StatementText(node)));
                    }
                }
            }

            private HashSet<string> Columns(string bare)
            {
                if (!_definitions.TryGetValue(bare, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _definitions[bare] = columns;
                }

                return columns;
            }

            private static void CollectTables(TableReference reference, List<NamedTableReference> into, ref bool unresolved)
            {
                switch (reference)
                {
                    case NamedTableReference named:
                        into.Add(named);
                        break;
                    case QualifiedJoin qualified:
                        CollectTables(qualified.FirstTableReference, into, ref unresolved);
                        CollectTables(qualified.SecondTableReference, into, ref unresolved);
                        break;
                    case UnqualifiedJoin unqualified:
                        CollectTables(unqualified.FirstTableReference, into, ref unresolved);
                        CollectTables(unqualified.SecondTableReference, into, ref unresolved);
                        break;
                    default:
                        // 파생 테이블·함수 호출·CTE 참조는 컬럼 소속을 말할 수 없다.
                        unresolved = true;
                        break;
                }
            }

            private static string? LastIdentifier(ColumnReferenceExpression? column) =>
                column?.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;

            private static string StatementText(TSqlFragment fragment)
            {
                if (fragment.ScriptTokenStream == null || fragment.FirstTokenIndex < 0) return string.Empty;

                var text = new System.Text.StringBuilder();
                for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
                {
                    text.Append(fragment.ScriptTokenStream[i].Text);
                }

                return text.ToString();
            }

            private sealed class ColumnCollector : TSqlFragmentVisitor
            {
                public List<(string Column, string? Qualifier)> Columns { get; } = new();

                public override void Visit(ColumnReferenceExpression node)
                {
                    var identifiers = node.MultiPartIdentifier?.Identifiers;
                    if (identifiers == null || identifiers.Count == 0) return;

                    var column = identifiers[^1]?.Value;
                    if (string.IsNullOrEmpty(column)) return;

                    var qualifier = identifiers.Count >= 2 ? identifiers[^2]?.Value : null;
                    Columns.Add((column, qualifier));
                }
            }
        }
    }
}
