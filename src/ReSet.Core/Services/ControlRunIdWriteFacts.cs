using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL 펜스에서 <b>제어 계약 표의 run id 자리에 값을 쓰는 문장</b>을 뽑는다.
    /// <see cref="MechanicalValidator"/> 의 「발급 전 잠금 쓰기」 검사 재료다.
    ///
    /// [무엇이 run id 자리인가 - 계약이 말한다]
    /// <see cref="ControlColumn.ReferencesRunId"/> 가 참인 컬럼이다. 이름(RunId·OwnerRunId)으로 짐작하지 않는다 -
    /// 발급 자리(<c>batch.BatchRun.RunId</c>, <see cref="ControlColumn.IsIdentity"/>)와 참조 자리가 이름이 겹친다.
    ///
    /// [쓰기만 본다]
    /// <c>INSERT</c> 의 <b>컬럼 목록</b> · <c>UPDATE</c> 의 <b>SET 절 왼쪽</b> · <c>MERGE</c> 의 <b>INSERT·UPDATE 절</b>.
    /// <c>WHERE</c>·<c>ON</c>·<c>OUTPUT</c>·<c>SELECT</c> 의 같은 컬럼은 읽기라서 담지 않는다 - 실물 B17 S02 의
    /// 셋째 UPDATE(<c>SQL_REFRESH_OWN_LOCK</c>)가 <c>OwnerRunId</c> 를 WHERE 에만 쓰는 모양이고, 그것을 쓰기로 세면
    /// 발급 뒤 하트비트 갱신까지 고발한다.
    ///
    /// [침묵의 범위를 알고 써라]
    /// ① 컬럼 목록이 없는 <c>INSERT INTO t VALUES (…)</c> 는 어느 값이 어느 컬럼인지 말할 수 없어 버린다(위치 대응을
    /// 계약 순서로 짐작하면 계약의 컬럼 순서가 곧 DDL 순서라는 근거 없는 전제가 생긴다) ② 동적 SQL 문자열 안의 문장은
    /// 파서가 못 본다 ③ 파싱이 실패한 펜스는 통째로 버린다(부분 파스로 판정하지 않는다).
    /// 판독: docs/audit-reports/2026-09-16-발급전-잠금-계약-사전선언.md
    /// </summary>
    internal static class ControlRunIdWriteFacts
    {
        /// <param name="Table">제어 계약 표의 이름(계약에 적힌 그대로, 예 <c>batch.BatchRunLock</c>).</param>
        /// <param name="Column">그 표의 run id 자리 컬럼 이름.</param>
        /// <param name="Statement">그 쓰기가 있던 문장 원문(귀속 어휘의 재료).</param>
        internal readonly record struct Write(string Table, string Column, string Statement);

        /// <summary>한 SQL 펜스에서 run id 자리 쓰기를 뽑는다.</summary>
        internal static IReadOnlyList<Write> Read(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Write>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(sql);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0)) return Array.Empty<Write>();

                var visitor = new RunIdWriteVisitor(sql);
                fragment.Accept(visitor);
                return visitor.Writes;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[발급 전 잠금] SQL 읽기 실패 - 빈 결과로 진행합니다.");
                return Array.Empty<Write>();
            }
        }

        /// <summary>계약이 run id 자리로 정한 (표, 컬럼) 전부.</summary>
        internal static IReadOnlyDictionary<string, IReadOnlySet<string>> RunIdColumnsByTable { get; } =
            BatchControlContract.Tables.ToDictionary(
                table => table.Name,
                table => (IReadOnlySet<string>)table.Columns
                    .Where(column => column.ReferencesRunId)
                    .Select(column => column.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>표 이름을 계약 표로 푼다 - 스키마를 적었든 안 적었든 맨이름으로 맞춘다.</summary>
        private static string? ResolveContractTable(SchemaObjectName? name)
        {
            var bare = name?.BaseIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(bare)) return null;

            foreach (var table in RunIdColumnsByTable)
            {
                var contractBare = table.Key.Split('.').Last();
                if (string.Equals(contractBare, bare, StringComparison.OrdinalIgnoreCase) && table.Value.Count > 0)
                {
                    return table.Key;
                }
            }

            return null;
        }

        private sealed class RunIdWriteVisitor : TSqlFragmentVisitor
        {
            private readonly string _sql;

            internal RunIdWriteVisitor(string sql) => _sql = sql;

            internal List<Write> Writes { get; } = new();

            public override void Visit(InsertSpecification node)
            {
                var table = ResolveContractTable((node.Target as NamedTableReference)?.SchemaObject);
                if (table == null) return;

                // 컬럼 목록이 없으면 무엇에 쓰는지 말할 수 없다(침묵 ①).
                foreach (var column in node.Columns ?? new List<ColumnReferenceExpression>())
                {
                    Add(table, LastIdentifier(column), node);
                }
            }

            public override void Visit(UpdateSpecification node)
            {
                var table = ResolveContractTable(TargetOf(node));
                if (table == null) return;

                foreach (var clause in node.SetClauses ?? new List<SetClause>())
                {
                    if (clause is AssignmentSetClause { Column: { } column })
                    {
                        Add(table, LastIdentifier(column), node);
                    }
                }
            }

            public override void Visit(MergeSpecification node)
            {
                var table = ResolveContractTable((node.Target as NamedTableReference)?.SchemaObject);
                if (table == null) return;

                foreach (var clause in node.ActionClauses ?? new List<MergeActionClause>())
                {
                    switch (clause.Action)
                    {
                        case InsertMergeAction insert:
                            foreach (var column in insert.Columns ?? new List<ColumnReferenceExpression>())
                            {
                                Add(table, LastIdentifier(column), node);
                            }

                            break;
                        case UpdateMergeAction update:
                            foreach (var set in update.SetClauses ?? new List<SetClause>())
                            {
                                if (set is AssignmentSetClause { Column: { } column })
                                {
                                    Add(table, LastIdentifier(column), node);
                                }
                            }

                            break;
                    }
                }
            }

            /// <summary>
            /// <c>UPDATE</c> 의 대상. <c>UPDATE batch.X SET …</c> 는 Target 이 그 표이고,
            /// <c>UPDATE A SET … FROM batch.X AS A</c> 는 Target 이 별칭이므로 FROM 절에서 그 별칭의 표를 찾는다.
            /// </summary>
            private static SchemaObjectName? TargetOf(UpdateSpecification node)
            {
                if (node.Target is not NamedTableReference target) return null;

                var direct = ResolveContractTable(target.SchemaObject);
                if (direct != null) return target.SchemaObject;

                var alias = target.SchemaObject?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(alias) || node.FromClause == null) return target.SchemaObject;

                var finder = new AliasedTableFinder(alias!);
                node.FromClause.Accept(finder);
                return finder.Found ?? target.SchemaObject;
            }

            private static string LastIdentifier(ColumnReferenceExpression column) =>
                column.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value ?? string.Empty;

            private void Add(string table, string column, TSqlFragment statement)
            {
                if (string.IsNullOrWhiteSpace(column)) return;
                if (!RunIdColumnsByTable[table].Contains(column)) return;

                var text = statement.FragmentLength > 0 && statement.StartOffset >= 0
                    ? _sql.Substring(statement.StartOffset, Math.Min(statement.FragmentLength, _sql.Length - statement.StartOffset))
                    : _sql;
                Writes.Add(new Write(table, column, text));
            }
        }

        /// <summary>FROM 절에서 별칭이 가리키는 표를 찾는다 - <c>UPDATE A … FROM batch.X AS A</c> 모양을 풀기 위한 것뿐이다.</summary>
        private sealed class AliasedTableFinder : TSqlFragmentVisitor
        {
            private readonly string _alias;

            internal AliasedTableFinder(string alias) => _alias = alias;

            internal SchemaObjectName? Found { get; private set; }

            public override void Visit(NamedTableReference node)
            {
                if (Found != null) return;
                if (string.Equals(node.Alias?.Value, _alias, StringComparison.OrdinalIgnoreCase))
                {
                    Found = node.SchemaObject;
                }
            }
        }
    }
}
