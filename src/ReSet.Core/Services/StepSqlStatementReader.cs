using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

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
        // 1라운드 리뷰 실측: `갱신` 앞에도 형제 대안들과 같은 `\b`가 있어야 한다 -
        // 없으면 "재갱신4" 같은 합성어의 "갱신4"가 앵커 4로 오검출된다.
        private static readonly Regex AnchorPattern = new(
            @"(?:\bU|\b갱신\s*|\bUPDATE\s+|\bINSERT\s+|\bDELETE\s+)(?<ordinal>\d{1,2})\b",
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
                    Log.Debug(ex, "단계 SQL 펜스를 읽지 못했습니다 - 이 펜스는 건너뜁니다.");
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
        /// 문장 바로 앞의 주석 토큰에서 갱신 번호를 읽는다.
        ///
        /// [1라운드 리뷰 실측 - 왜 가장 가까운 주석 하나만 보는가]
        /// 예전 구현은 일치하지 않는 주석을 계속 지나쳐 더 앞의 주석까지 훑었다.
        /// 그러면 `A문장; -- U4 참고\nB문장`처럼 A의 꼬리 주석이 B의 앵커로
        /// 잘못 붙는다 - 토큰 스트림만 보면 "A 뒤에 붙은 꼬리 주석"과 "B 앞에 놓인
        /// 선행 주석"이 똑같이 "공백 다음 주석"으로 보이기 때문이다. 이 둘을 가르는
        /// 유일한 단서는 개행이다: 주석이 이전 실토큰과 같은 줄에 있으면(개행으로
        /// 갈라지지 않으면) 그건 그 이전 문장의 꼬리 주석이지 이 문장의 것이 아니다.
        /// 그래서 가장 가까운 주석 하나만 보고, 그 주석이 자기 줄에 있을 때만
        /// 앵커 후보로 인정한다 - 맞지 않으면 그 자리에서 멈추고 더 앞으로 가지 않는다.
        /// </summary>
        private static int? ReadAnchor(IList<TSqlParserToken> tokens, int firstTokenIndex)
        {
            int i = firstTokenIndex - 1;
            while (i >= 0 && tokens[i].TokenType is TSqlTokenType.WhiteSpace) i--;
            if (i < 0) return null;

            var token = tokens[i];
            if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            {
                return null;
            }

            if (!PrecededByNewline(tokens, i)) return null;

            var match = AnchorPattern.Match(token.Text);
            return match.Success ? int.Parse(match.Groups["ordinal"].Value) : null;
        }

        /// <summary>
        /// 주어진 위치의 토큰이 그 앞의 실토큰과 다른 줄에 있는지 본다. 개행이 든
        /// 공백 토큰을 만나면 다른 줄이고(자기 줄의 주석), 개행 없이 실토큰에
        /// 닿으면 같은 줄이다(그 실토큰의 꼬리 주석).
        /// </summary>
        private static bool PrecededByNewline(IList<TSqlParserToken> tokens, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (token.TokenType is TSqlTokenType.WhiteSpace)
                {
                    if (token.Text.Contains('\n')) return true;
                    continue; // 같은 줄의 탭·스페이스 - 계속 거슬러 올라간다.
                }

                return false; // 개행 전에 실토큰을 만났다 - 같은 줄이다.
            }

            return true; // 펜스 맨 앞 - 앞에 아무 토큰도 없으므로 자기 줄로 본다.
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
