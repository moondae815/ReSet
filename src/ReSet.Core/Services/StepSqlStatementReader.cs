using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Anchor">주석에서 읽은 갱신 번호. 없으면 null.</param>
    public sealed record StepSqlStatement(
        string Kind,
        string TargetTable,
        int? Anchor,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinColumns,
        bool HasGrouping);

    /// <summary>
    /// 단계 지시서의 ```sql 펜스에서 DML 문장을 읽는다.
    ///
    /// [왜 정규식이 아니라 ScriptDom인가]
    /// 정규식으로 UPDATE를 세면 문자열 리터럴 안의 단어와 주석에 적힌 예시가 함께
    /// 잡힌다. 단계 문서는 산문과 SQL이 섞여 있어 그 오검출이 개수 대조를 무의미하게
    /// 만든다.
    ///
    /// [왜 CleanedSqlFences를 쓰지 않는가]
    /// 그 헬퍼는 주석을 공백으로 지운다. 앵커(`/* U4: … */`)가 주석 안에 있어
    /// 지워진 사본에서는 읽을 수 없다. ScriptDom은 주석을 토큰으로 남기므로
    /// 원본 펜스를 파싱하면 문장과 그 앞 주석을 함께 얻는다.
    /// </summary>
    public static class StepSqlStatementReader
    {
        private static readonly Regex FencePattern = new(
            @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // `U4` · `갱신 4` · `UPDATE 4` 세 표기를 인정한다. S07이 이미 `/* U4: … */`를 쓴다.
        private static readonly Regex AnchorPattern = new(
            @"(?:\bU|갱신\s*|\bUPDATE\s+|\bINSERT\s+|\bDELETE\s+)(?<ordinal>\d{1,2})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyList<StepSqlStatement> Read(string? stepMarkdown)
        {
            var statements = new List<StepSqlStatement>();
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return statements;

            foreach (Match fence in FencePattern.Matches(stepMarkdown))
            {
                // 펜스 하나가 T-SQL이 아니어도 나머지 펜스는 읽는다.
                try
                {
                    statements.AddRange(ReadFence(fence.Groups["sql"].Value));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "단계 SQL 펜스를 읽지 못했습니다 - 이 펜스는 건너뜁니다.");
                }
            }

            return statements;
        }

        private static IEnumerable<StepSqlStatement> ReadFence(string sql)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);

            // 파싱에 실패한 펜스는 침묵한다 - 의사코드·C# 조각이 온다.
            if (fragment == null || errors is { Count: > 0 }) yield break;

            var tokens = fragment.ScriptTokenStream;
            var visitor = new DmlCollector();
            fragment.Accept(visitor);

            foreach (var (statement, firstTokenIndex) in visitor.Found)
            {
                yield return statement with { Anchor = ReadAnchor(tokens, firstTokenIndex) };
            }
        }

        /// <summary>
        /// 문장 바로 앞의 주석 토큰에서 갱신 번호를 읽는다. 공백과 주석만 거슬러
        /// 올라가고, 다른 토큰을 만나면 멈춘다 - 앞 문장의 꼬리 주석을 자기 앵커로
        /// 삼으면 대응이 한 칸씩 밀린다.
        /// </summary>
        private static int? ReadAnchor(IList<TSqlParserToken> tokens, int firstTokenIndex)
        {
            for (int i = firstTokenIndex - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (token.TokenType is TSqlTokenType.WhiteSpace) continue;
                if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                {
                    return null;
                }

                var match = AnchorPattern.Match(token.Text);
                if (match.Success) return int.Parse(match.Groups["ordinal"].Value);
            }

            return null;
        }

        private sealed class DmlCollector : TSqlFragmentVisitor
        {
            /// <summary>문장과 그 첫 토큰 위치. 앵커는 ReadFence가 토큰 스트림에서 채운다.</summary>
            public List<(StepSqlStatement Statement, int FirstTokenIndex)> Found { get; } = new();

            public override void Visit(UpdateStatement node) =>
                Add("UPDATE", node, node.UpdateSpecification?.Target,
                    node.UpdateSpecification?.WhereClause, node.UpdateSpecification?.FromClause);

            public override void Visit(DeleteStatement node) =>
                Add("DELETE", node, node.DeleteSpecification?.Target,
                    node.DeleteSpecification?.WhereClause, node.DeleteSpecification?.FromClause);

            public override void Visit(InsertStatement node) =>
                Add("INSERT", node, node.InsertSpecification?.Target, null, null);

            private void Add(
                string kind,
                TSqlStatement statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from)
            {
                var predicates = new ColumnCollector();
                var joins = new ColumnCollector();
                var grouping = new GroupingProbe();

                where?.Accept(predicates);
                from?.Accept(joins);
                statement.Accept(grouping);

                Found.Add((
                    new StepSqlStatement(
                        kind,
                        ResolveTargetTable(target, from),
                        Anchor: null,
                        predicates.Columns.ToList(),
                        joins.Columns.ToList(),
                        grouping.Found),
                    statement.FirstTokenIndex));
            }

            /// <summary>
            /// 갱신 대상 이름을 물리 테이블명으로 해석한다.
            ///
            /// [왜 그냥 대상의 BaseIdentifier를 쓰면 안 되는가 - 실측]
            /// 단계 SQL은 `UPDATE A SET ... FROM dbo.TSettleMst AS A WHERE ...`처럼
            /// 별칭을 대상으로 쓰는 형태가 흔하다(S07 등). 이때 대상 노드의
            /// BaseIdentifier는 "A"이지 "TSettleMst"가 아니다 - FROM 절에서 그 별칭이
            /// 가리키는 물리 테이블을 찾아야 한다. 이 해석은 SqlStaticParser의
            /// FindAliasForTarget과 같은 문제를 풀지만, 여기서는 자기참조 컬럼 판정이
            /// 필요 없으므로 더 단순하다.
            /// </summary>
            private static string ResolveTargetTable(TableReference? target, FromClause? from)
            {
                if (target is not NamedTableReference named || named.SchemaObject == null) return string.Empty;

                var identifiers = named.SchemaObject.Identifiers;
                if (identifiers == null || identifiers.Count == 0) return string.Empty;

                var written = identifiers[^1].Value;
                if (string.IsNullOrWhiteSpace(written)) return string.Empty;

                // 점으로 한정된 이름(dbo.T 등)은 별칭일 수 없다 - 그대로 확정.
                if (identifiers.Count > 1) return written;

                if (from != null)
                {
                    var finder = new FromClauseAliasFinder();
                    from.Accept(finder);
                    if (finder.AliasToTable.TryGetValue(written, out var resolved)) return resolved;
                }

                return written;
            }
        }

        /// <summary>
        /// FROM 절의 별칭 → 물리 테이블명 사전을 모은다. 파생 테이블(`(SELECT …) X`)
        /// 안쪽으로는 내려가지 않는다 - 그 별칭은 바깥 갱신 대상과 무관하고, 이름이
        /// 같으면 엉뚱한 테이블을 물어 온다(SqlStaticParser.NamedTableCollector와 같은 이유).
        /// </summary>
        private sealed class FromClauseAliasFinder : TSqlFragmentVisitor
        {
            public Dictionary<string, string> AliasToTable { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(NamedTableReference node)
            {
                var name = node.SchemaObject?.Identifiers?.LastOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;
                if (node.Alias != null && !string.IsNullOrWhiteSpace(node.Alias.Value))
                {
                    AliasToTable[node.Alias.Value] = name!;
                }
            }

            public override void ExplicitVisit(QueryDerivedTable node) { }
        }

        /// <summary>
        /// 명세서의 술어 컬럼 칸이 "최상위 WHERE 기준"이므로, 스칼라 하위질의 안쪽
        /// 컬럼을 여기서 세면 뒤 검사(Task 4)가 통째로 오탐이 된다. ScriptDom의
        /// `ExplicitVisit`을 비워 하위 순회를 끊는 방식은 SqlStaticParser의
        /// ColumnReferenceCollector와 같다.
        /// </summary>
        private sealed class ColumnCollector : TSqlFragmentVisitor
        {
            private readonly List<string> _columns = new();
            public IReadOnlyList<string> Columns => _columns;

            public override void Visit(ColumnReferenceExpression node)
            {
                var last = node.MultiPartIdentifier?.Identifiers?.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(last?.Value)) _columns.Add(last!.Value);
            }

            /// <summary>스칼라 하위질의 안쪽으로 내려가지 않는다 - 최상위 술어 컬럼만 센다.</summary>
            public override void ExplicitVisit(ScalarSubquery node) { }

            /// <summary>파생 테이블(FROM 절 안의 (SELECT …) 별칭) 안쪽도 최상위가 아니다.</summary>
            public override void ExplicitVisit(QueryDerivedTable node) { }
        }

        /// <summary>문장 전체(WHERE의 IN 서브쿼리 포함)에 GROUP BY·HAVING이 있는지만 본다 - 값은 안 본다.</summary>
        private sealed class GroupingProbe : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void Visit(GroupByClause node) => Found = true;
            public override void Visit(HavingClause node) => Found = true;
        }
    }
}
