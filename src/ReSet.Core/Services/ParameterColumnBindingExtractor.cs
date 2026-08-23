using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Variable">`@pi_strYMD` 같은 변수 이름(원문 표기).</param>
    /// <param name="Table">결합된 컬럼이 속한 테이블의 기본 식별자(스키마·DB 접두사 없음).</param>
    /// <param name="Column">컬럼 이름.</param>
    public sealed record ParameterColumnBinding(string Variable, string Table, string Column);

    /// <summary>
    /// 변수(파라미터·지역 변수)가 어느 `테이블.컬럼`과 **실제로 결합**되는지를 AST에서 뽑는다.
    ///
    /// [왜 - 2026-08-23 9회차 축 A 재감사 🟡 `UP_UTIL_SETTLE_EXCEPTION_PROC` Spec.md:34]
    /// 「파라미터와 변수의 컬럼 관계」 표는 모델이 쓰는 표라 기계 확정 표가 아닌데, `@pi_strYMD`의
    /// 연결 컬럼으로 `TPLCardTxMst.YMD`(393행 - 함수 인자로만 함께 나옴)·
    /// `TClientSettleRate4MobileCo.YMD`(416행 - `A.AYMD = B.YMD`, 변수 없음)를 적었다. 기계 확정
    /// 표 두 곳이 실제 술어를 보존하므로 🟡이지만 그 표를 믿으면 "정산일로 거른다"고 오독한다.
    /// 이 재료가 그 주장의 기준값이 되어 L1(`MechanicalValidator.CheckParameterColumnClaims`)이
    /// 대조한다.
    ///
    /// [결합의 정의 - 넓게 잡는다] 이 재료는 "주장을 **기각**할 근거"로만 쓰이므로(결합이 하나라도
    /// 있으면 통과), 결합을 넓게 잡을수록 거짓 양성이 줄고 좁게 잡을수록 정상 서술을 결함으로
    /// 만든다. 그래서 술어 노드(비교·IN·BETWEEN·LIKE) 하나 안에 변수가 하나라도 있으면 그 노드
    /// 안의 모든 컬럼 참조를 그 변수와 결합된 것으로 친다 - `ISNULL(A.X,'') = @p`·
    /// `A.Y >= DATEADD(D,@n,@p)`·`A.PLTID IN (SELECT … WHERE C.YMD = @p)`의 바깥 IN도 결합이다.
    /// 대입도 결합이다 - `UPDATE … SET C = 식(@p)`, INSERT 컬럼 ↔ 값/원천 식(@p) 자리 대응,
    /// `SELECT @v = 식(C)`, `SET @v = (SELECT C …)`.
    /// 산술식도 결합이다 - `CAST(CLEtc/@v_valIncVat AS INT)`(COMM_UPD:413)에서 CLEtc는 그 변수로
    /// 나눠진다. 커서도 결합이다 - `DECLARE c CURSOR FOR SELECT A.YMD, … ` 뒤의
    /// `FETCH … INTO @v_strYMD, …`는 자리로 대응한다(PROC_ETC:66). 조인 등식은 결합을 **전파**한다 -
    /// 한 문장 안에서 `A.YMD = B.YMD`이고 `A.YMD = @p`이면 `B.YMD`도 `@p`와 결합이다(COMM_UPD:68·76).
    /// 이 셋은 코퍼스 스윕의 거짓 양성 6건(PROC_ETC 4·COMM_UPD 2)이 가르쳐 준 모양이다.
    /// <b>결합으로 치지 않는 유일한 모양은 같은 함수 호출의 인자로만 함께 나오는 것</b>이다 -
    /// 그것이 바로 393행의 오독이고, 이것까지 결합으로 치면 이 재료는 그 결함을 영영 못 잡는다.
    ///
    /// [별칭은 문장 단위로 푼다] 같은 별칭 `B`가 EXCEPTION_PROC에서 문장마다 다른 테이블이다
    /// (TPLCardTxMst·TClientSettleRate4MobileCo). 전역 별칭 맵(SqlStaticParser.TableAliasVisitor의
    /// GlobalAliasToTableMap)은 마지막 등록이 이기므로 쓰지 않고, 문장(DML·독립 SELECT·IF·
    /// SET·DECLARE)마다 그 안의 NamedTableReference로 맵을 새로 만든다. 한 문장 안에서 별칭이
    /// 둘 이상의 테이블에 매이면(바깥과 하위 질의가 같은 별칭을 쓴 경우) 둘 다에 결합한다 -
    /// 넓게 잡는 쪽이다. 한정자 없는 컬럼은 문장의 테이블이 하나면 그것, 여럿이면 전부에 결합한다.
    /// 파생 테이블 별칭(`X`)은 테이블이 아니므로 그 한정자의 컬럼은 결합을 내지 않는다.
    /// </summary>
    public static class ParameterColumnBindingExtractor
    {
        public static IReadOnlyList<ParameterColumnBinding> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<ParameterColumnBinding>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<ParameterColumnBinding>();

                var visitor = new StatementVisitor();
                fragment.Accept(visitor);
                return visitor.Bindings;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[ParameterColumnBindingExtractor] 변수-컬럼 결합 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<ParameterColumnBinding>();
            }
        }

        /// <summary>문장 하나를 단위로 별칭 맵을 만들고 그 안의 결합을 모은다.</summary>
        private sealed class StatementVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<(string, string, string)> _seen = new();
            /// <summary>커서 이름 → (원천 SELECT의 열 식, 그 SELECT의 별칭 맵). FETCH INTO가 자리로 대응한다.</summary>
            private readonly Dictionary<string, (List<ScalarExpression> Columns, AliasCollector Aliases)> _cursors =
                new(StringComparer.OrdinalIgnoreCase);
            public List<ParameterColumnBinding> Bindings { get; } = new();

            public override void ExplicitVisit(DeclareCursorStatement node)
            {
                var select = node.CursorDefinition?.Select;
                var name = node.Name?.Value;
                if (select != null && !string.IsNullOrWhiteSpace(name))
                {
                    var aliases = new AliasCollector();
                    select.Accept(aliases);
                    aliases.Normalize();
                    var columns = new List<ScalarExpression>();
                    if (select.QueryExpression is QuerySpecification spec)
                    {
                        foreach (var el in spec.SelectElements)
                            columns.Add(el is SelectScalarExpression sse ? sse.Expression : null!);
                    }
                    _cursors[name!] = (columns, aliases);
                    Handle(select); // 원천 SELECT 자신의 술어 결합도 센다
                }
                else
                {
                    base.ExplicitVisit(node);
                }
            }

            public override void ExplicitVisit(FetchCursorStatement node)
            {
                var name = node.Cursor?.Name?.Value;
                if (!string.IsNullOrWhiteSpace(name) && _cursors.TryGetValue(name!, out var cur) && node.IntoVariables != null)
                {
                    for (var i = 0; i < Math.Min(cur.Columns.Count, node.IntoVariables.Count); i++)
                    {
                        if (cur.Columns[i] == null) continue;
                        var finder = new BindingCollector.ColumnFinder();
                        cur.Columns[i].Accept(finder);
                        foreach (var col in finder.Columns)
                            foreach (var t in cur.Aliases.Resolve(col))
                                AddBinding(node.IntoVariables[i].Name, t, BindingCollector.ColumnName(col));
                    }
                }
                base.ExplicitVisit(node);
            }

            private void AddBinding(string variable, string table, string column)
            {
                var key = (variable.ToUpperInvariant(), table.ToUpperInvariant(), column.ToUpperInvariant());
                if (_seen.Add(key)) Bindings.Add(new ParameterColumnBinding(variable, table, column));
            }

            public override void ExplicitVisit(UpdateStatement node) => Handle(node);
            public override void ExplicitVisit(DeleteStatement node) => Handle(node);
            public override void ExplicitVisit(InsertStatement node) => Handle(node);
            public override void ExplicitVisit(SelectStatement node) => Handle(node);
            public override void ExplicitVisit(SetVariableStatement node) => Handle(node);
            public override void ExplicitVisit(DeclareVariableStatement node) => Handle(node);
            public override void ExplicitVisit(MergeStatement node) => Handle(node);

            public override void ExplicitVisit(IfStatement node)
            {
                // 술어만 이 문장의 단위로 보고, 분기 본문은 각자의 문장으로 방문한다.
                if (node.Predicate != null) HandleFragment(node.Predicate);
                node.ThenStatement?.Accept(this);
                node.ElseStatement?.Accept(this);
            }

            public override void ExplicitVisit(WhileStatement node)
            {
                if (node.Predicate != null) HandleFragment(node.Predicate);
                node.Statement?.Accept(this);
            }

            private void Handle(TSqlStatement statement) => HandleFragment(statement);

            private void HandleFragment(TSqlFragment fragment)
            {
                var aliases = new AliasCollector();
                fragment.Accept(aliases);
                aliases.Normalize();

                var bindings = new BindingCollector(aliases);
                fragment.Accept(bindings);

                // 조인 등식(컬럼 = 컬럼)으로 결합을 전파한다 - 같은 문장 안에서만.
                var classes = new EqualityClasses();
                foreach (var (left, right) in bindings.ColumnEqualities) classes.Union(left, right);

                foreach (var (variable, table, column) in bindings.Found)
                {
                    AddBinding(variable, table, column);
                    foreach (var (t2, c2) in classes.MembersOf((table, column)))
                        AddBinding(variable, t2, c2);
                }
            }

            /// <summary>(테이블, 컬럼) 위의 합집합-찾기. 전파는 한 문장 안에서 닫힌다.</summary>
            private sealed class EqualityClasses
            {
                private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);
                private readonly Dictionary<string, (string, string)> _keys = new(StringComparer.OrdinalIgnoreCase);
                private static string K((string, string) x) => $"{x.Item1}|{x.Item2}";
                private string Find(string k) { if (!_parent.TryGetValue(k, out var p)) return k; if (p == k) return k; var r = Find(p); _parent[k] = r; return r; }
                public void Union((string, string) a, (string, string) b)
                {
                    var ka = K(a); var kb = K(b);
                    _keys[ka] = a; _keys[kb] = b;
                    _parent.TryAdd(ka, ka); _parent.TryAdd(kb, kb);
                    var ra = Find(ka); var rb = Find(kb);
                    if (ra != rb) _parent[ra] = rb;
                }
                public IEnumerable<(string, string)> MembersOf((string, string) x)
                {
                    var k = K(x);
                    if (!_parent.ContainsKey(k)) yield break;
                    var root = Find(k);
                    foreach (var kv in _keys) if (Find(kv.Key) == root && !kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) yield return kv.Value;
                }
            }
        }

        /// <summary>문장 안의 별칭/테이블 이름 → 기본 테이블 식별자(들). 파생 테이블 별칭은 등록하지 않는다.</summary>
        private sealed class AliasCollector : TSqlFragmentVisitor
        {
            public Dictionary<string, HashSet<string>> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DerivedAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(NamedTableReference node)
            {
                var table = node.SchemaObject?.BaseIdentifier?.Value;
                if (!string.IsNullOrWhiteSpace(table) && !table.StartsWith("@", StringComparison.Ordinal))
                {
                    Tables.Add(table);
                    Add(table, table);
                    var alias = node.Alias?.Value;
                    if (!string.IsNullOrWhiteSpace(alias)) Add(alias, table);
                }
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QueryDerivedTable node)
            {
                var alias = node.Alias?.Value;
                if (!string.IsNullOrWhiteSpace(alias)) DerivedAliases.Add(alias);
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// `UPDATE A SET … FROM dbo.TSettleMst A`·`DELETE A FROM …`의 대상 `A`는 AST에서
            /// NamedTableReference로 나와 "테이블 A"로 등록된다. 같은 문장 안에서 그 이름이 실제
            /// 테이블의 별칭으로도 등록돼 있으면 그쪽으로 풀고 가짜 테이블 항목을 지운다.
            /// </summary>
            public void Normalize()
            {
                var pseudo = Tables.Where(t => Map.TryGetValue(t, out var set) && set.Any(x => !x.Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var name in pseudo)
                {
                    Tables.Remove(name);
                    if (Map.TryGetValue(name, out var set)) set.Remove(name);
                }
            }

            private void Add(string key, string table)
            {
                if (!Map.TryGetValue(key, out var set)) Map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(table);
            }

            public IEnumerable<string> Resolve(ColumnReferenceExpression column)
            {
                var ids = column.MultiPartIdentifier?.Identifiers;
                if (ids == null || ids.Count == 0) yield break;
                if (ids.Count >= 2)
                {
                    var qualifier = ids[ids.Count - 2].Value;
                    if (DerivedAliases.Contains(qualifier) && !Map.ContainsKey(qualifier)) yield break;
                    if (Map.TryGetValue(qualifier, out var tables))
                    {
                        foreach (var t in tables) yield return t;
                    }
                    yield break;
                }
                // 한정자 없음 - 테이블이 하나면 그것, 여럿이면 전부(넓게 잡는 쪽).
                foreach (var t in Tables) yield return t;
            }
        }

        /// <summary>술어·대입 노드 단위로 (변수, 테이블, 컬럼) 결합을 모은다.</summary>
        private sealed class BindingCollector : TSqlFragmentVisitor
        {
            private readonly AliasCollector _aliases;
            public List<(string Variable, string Table, string Column)> Found { get; } = new();
            /// <summary>컬럼 = 컬럼 등식 쌍(테이블 해석 후). 조인 키 전파용.</summary>
            public List<((string, string) Left, (string, string) Right)> ColumnEqualities { get; } = new();

            public BindingCollector(AliasCollector aliases) => _aliases = aliases;

            // 산술식(`CLEtc/@v`)도 결합이다 - 함수 호출(FunctionCall)은 여기 포함되지 않는다.
            public override void ExplicitVisit(BinaryExpression node) { BindWithin(node); base.ExplicitVisit(node); }

            // ── 술어 노드: 변수가 하나라도 있으면 그 안의 모든 컬럼과 결합 ──
            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                BindWithin(node);
                if (node.ComparisonType == BooleanComparisonType.Equals
                    && node.FirstExpression is ColumnReferenceExpression lc
                    && node.SecondExpression is ColumnReferenceExpression rc)
                {
                    foreach (var lt in _aliases.Resolve(lc))
                        foreach (var rt in _aliases.Resolve(rc))
                            ColumnEqualities.Add(((lt, ColumnName(lc)), (rt, ColumnName(rc))));
                }
                base.ExplicitVisit(node);
            }
            public override void ExplicitVisit(InPredicate node) { BindWithin(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(BooleanTernaryExpression node) { BindWithin(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(LikePredicate node) { BindWithin(node); base.ExplicitVisit(node); }

            // ── 대입 ──
            public override void ExplicitVisit(AssignmentSetClause node)
            {
                // UPDATE … SET Column = 식(@p) / SET @v = 식(Column)
                if (node.Column != null && node.NewValue != null)
                {
                    foreach (var v in VariablesIn(node.NewValue))
                        foreach (var t in _aliases.Resolve(node.Column))
                            Found.Add((v, t, ColumnName(node.Column)));
                }
                if (node.Variable != null && node.NewValue != null)
                {
                    foreach (var (t, c) in ColumnsIn(node.NewValue))
                        Found.Add((node.Variable.Name, t, c));
                }
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SelectSetVariable node)
            {
                if (node.Variable != null && node.Expression != null)
                {
                    foreach (var (t, c) in ColumnsIn(node.Expression))
                        Found.Add((node.Variable.Name, t, c));
                }
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SetVariableStatement node)
            {
                if (node.Variable != null && node.Expression != null)
                {
                    foreach (var (t, c) in ColumnsIn(node.Expression))
                        Found.Add((node.Variable.Name, t, c));
                }
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeclareVariableElement node)
            {
                if (node.VariableName != null && node.Value != null)
                {
                    foreach (var (t, c) in ColumnsIn(node.Value))
                        Found.Add((node.VariableName.Value, t, c));
                }
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InsertSpecification node)
            {
                var targetTable = (node.Target as NamedTableReference)?.SchemaObject?.BaseIdentifier?.Value;
                if (!string.IsNullOrWhiteSpace(targetTable) && node.Columns != null && node.Columns.Count > 0)
                {
                    var columns = node.Columns.Select(ColumnName).ToList();
                    if (node.InsertSource is ValuesInsertSource values)
                    {
                        foreach (var row in values.RowValues)
                        {
                            for (var i = 0; i < Math.Min(columns.Count, row.ColumnValues.Count); i++)
                                foreach (var v in VariablesIn(row.ColumnValues[i]))
                                    Found.Add((v, targetTable!, columns[i]));
                        }
                    }
                    else if (node.InsertSource is SelectInsertSource select)
                    {
                        foreach (var spec in QuerySpecificationsOf(select.Select))
                        {
                            for (var i = 0; i < Math.Min(columns.Count, spec.SelectElements.Count); i++)
                            {
                                if (spec.SelectElements[i] is SelectScalarExpression sse && sse.Expression != null)
                                    foreach (var v in VariablesIn(sse.Expression))
                                        Found.Add((v, targetTable!, columns[i]));
                            }
                        }
                    }
                }
                base.ExplicitVisit(node);
            }

            private void BindWithin(TSqlFragment node)
            {
                var variables = VariablesIn(node).ToList();
                if (variables.Count == 0) return;
                foreach (var (t, c) in ColumnsIn(node))
                    foreach (var v in variables)
                        Found.Add((v, t, c));
            }

            private IEnumerable<(string Table, string Column)> ColumnsIn(TSqlFragment fragment)
            {
                var cols = new ColumnFinder();
                fragment.Accept(cols);
                foreach (var col in cols.Columns)
                    foreach (var t in _aliases.Resolve(col))
                        yield return (t, ColumnName(col));
            }

            private static IEnumerable<string> VariablesIn(TSqlFragment fragment)
            {
                var vars = new VariableFinder();
                fragment.Accept(vars);
                return vars.Names;
            }

            internal static string ColumnName(ColumnReferenceExpression col)
            {
                var ids = col.MultiPartIdentifier?.Identifiers;
                return ids == null || ids.Count == 0 ? string.Empty : ids[ids.Count - 1].Value;
            }

            private static IEnumerable<QuerySpecification> QuerySpecificationsOf(QueryExpression? query)
            {
                switch (query)
                {
                    case QuerySpecification spec: yield return spec; break;
                    case BinaryQueryExpression bin:
                        foreach (var s in QuerySpecificationsOf(bin.FirstQueryExpression)) yield return s;
                        foreach (var s in QuerySpecificationsOf(bin.SecondQueryExpression)) yield return s;
                        break;
                    case QueryParenthesisExpression paren:
                        foreach (var s in QuerySpecificationsOf(paren.QueryExpression)) yield return s;
                        break;
                }
            }

            internal sealed class ColumnFinder : TSqlFragmentVisitor
            {
                public List<ColumnReferenceExpression> Columns { get; } = new();
                public override void Visit(ColumnReferenceExpression node)
                {
                    if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier != null) Columns.Add(node);
                }
            }

            private sealed class VariableFinder : TSqlFragmentVisitor
            {
                public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
                public override void Visit(VariableReference node) => Names.Add(node.Name);
            }
        }
    }
}
