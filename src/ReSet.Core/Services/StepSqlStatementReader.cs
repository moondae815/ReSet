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

        public static IReadOnlyList<StepSqlStatement> Read(string? stepMarkdown) =>
            Read(stepMarkdown, out _);

        /// <param name="stepMarkdown">단계 지시서 전문.</param>
        /// <param name="lostStatementCount">
        /// 파싱에 실패해 잃어버린 INSERT·UPDATE·DELETE 문장 개수(펜스 개수가 아니다).
        ///
        /// [Task 16 C2 → Task 20 실측으로 의미가 바뀜]
        /// 예전에는 펜스 하나에 구문 오류가 하나라도 있으면 그 펜스 전체를
        /// 버렸다 - 코퍼스 실측(891개 펜스 중 191개(21%) 파싱 실패, 326파일
        /// 중 119파일이 최소 1개)이 보여준 것은, 그 손실 대부분이 펜스
        /// 앞부분의 오류(`EXEC … sp_getapplock @Resource = CONCAT(...)`처럼
        /// ScriptDom이 함수 호출식을 named-parameter 값으로 거부하는 관용구)
        /// 때문에 오류 뒤에 오는 멀쩡한 UPDATE·DELETE까지 통째로 사라진
        /// 것이었다는 사실이다.
        ///
        /// [Task 20 실측 - ScriptDom은 오류 지점 이후를 복구하지 않는다]
        /// `TSqlParser.Parse`가 구문 오류를 만나면 그 지점에서 완전히
        /// 멈춘다 - 오류 뒤 문장은 반환된 fragment에 전혀 나타나지 않는다
        /// (실물 확인: POQSettleProc3/S08의 sp_getapplock 오류(펜스 앞부분,
        /// offset 560) 뒤에 있는 UPDATE 2개가 fragment에 없다 - "오류와
        /// 겹치는 문장만 뺀다"는 접근은 통하지 않는다). 그래서 이제는 펜스를
        /// 최상위(괄호 깊이 0) 세미콜론으로 잘라 조각마다 독립적으로
        /// 파싱한다 - 한 조각의 오류가 다른 조각에 번지지 않는다(아래
        /// SplitAtTopLevelSemicolons 참고).
        ///
        /// [왜 "펜스 개수"가 아니라 "문장 개수"인가]
        /// 조각 단위 파싱은 BEGIN·IF·TRY·CATCH 같은 제어문 조각도 단독으로는
        /// 파싱되지 않는 부작용을 낳는다(문법상 정상인 펜스에서도 일어난다).
        /// 그 조각들은 애초에 DML이 아니므로 잃어버린 게 없다 - 실패한 조각의
        /// 토큰에 INSERT·UPDATE·DELETE 키워드가 있을 때만 손실로 센다. 검사
        /// A(`CheckStatementCountAgainstSpec`)는 이 신호가 0보다 크면 그
        /// 단계의 개수 대조 전체를 여전히 접는다 - 어떤 (Kind,TargetTable)
        /// 조합이 영향받았는지 알 수 없으므로 보수적으로 접는 것이 맞다.
        /// </param>
        public static IReadOnlyList<StepSqlStatement> Read(string? stepMarkdown, out int lostStatementCount)
        {
            var statements = new List<StepSqlStatement>();
            lostStatementCount = 0;
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return statements;

            foreach (Match fence in FencePattern.Matches(stepMarkdown))
            {
                // 펜스 하나에 못 읽는 조각이 있어도 나머지 조각은 읽는다 - 다만
                // 잃어버린 DML 문장 개수는 lostStatementCount로 남긴다.
                try
                {
                    var (fenceStatements, fenceLostCount) = ReadFence(fence.Groups["sql"].Value);
                    lostStatementCount += fenceLostCount;
                    statements.AddRange(fenceStatements);
                }
                catch (Exception ex)
                {
                    // 이 경로는 조각 분할·재파싱 자체가 예기치 않게 던질 때만 탄다 -
                    // 몇 문장을 잃었는지 알 수 없으니 최소 1로 보수적으로 잡는다.
                    lostStatementCount++;
                    // 기본 로그 수준(Information)에서 보이도록 Warning을 쓴다 - Debug는
                    // 코퍼스 스윕에서 이 실패율(21%)을 아무도 못 보게 숨겼다.
                    Log.Warning(ex, "단계 SQL 펜스를 읽지 못했습니다 - 이 펜스는 건너뜁니다.");
                }
            }

            return statements;
        }

        private static (IReadOnlyList<StepSqlStatement> Statements, int LostStatementCount) ReadFence(string sql)
        {
            var lexer = new TSql160Parser(initialQuotedIdentifiers: true);
            var originalTokens = lexer.GetTokenStream(new StringReader(sql), out var tokenErrors);

            // 토큰화 자체가 실패하면(문자열·주석 미종료 등) 문장 경계를 알 방법이
            // 없다 - 이 펜스는 예전처럼 통째로 버린다. 코퍼스 실측(891개 펜스)에서는
            // 이 분기가 한 번도 발동하지 않았다 - 관측된 구문 오류는 전부 어휘
            // 분석 단계가 아니라 그 다음 문법 분석 단계에서 났다.
            if (originalTokens == null || tokenErrors is { Count: > 0 })
            {
                return (Array.Empty<StepSqlStatement>(), 1);
            }

            var statements = new List<StepSqlStatement>();
            var lostStatementCount = 0;

            foreach (var (start, endExclusive, containsDmlKeyword) in
                     SplitAtTopLevelSemicolons(sql, originalTokens))
            {
                var chunkText = sql.Substring(start, endExclusive - start);
                if (string.IsNullOrWhiteSpace(chunkText)) continue;

                var parser = new TSql160Parser(initialQuotedIdentifiers: true);
                var fragment = parser.Parse(new StringReader(chunkText), out var errors);

                if (fragment == null || errors is { Count: > 0 })
                {
                    // 의사코드·제어문 조각(BEGIN·IF·END 등)은 단독으로 파싱되지
                    // 않는 게 정상이다 - DML 키워드가 없으면 잃어버린 게 없다.
                    if (containsDmlKeyword) lostStatementCount++;
                    continue;
                }

                var visitor = new DmlCollector();
                fragment.Accept(visitor);

                foreach (var found in visitor.Found)
                {
                    // 앵커 탐지는 원본 펜스 전체의 토큰 스트림을 봐야 한다 - 조각
                    // 자신의 토큰 스트림만 보면 "이전 조각 끝에 붙은 꼬리 주석"과
                    // "이 조각 맨 앞의 선행 주석"을 가를 단서(이전 실토큰과 같은
                    // 줄인지)가 조각 경계에서 사라진다. 그래서 조각 안에서 찾은
                    // 문장의 시작 오프셋을 원본 좌표로 변환해 원본 토큰 스트림에서
                    // 다시 찾는다.
                    var globalOffset = start + found.StartOffset;
                    var globalTokenIndex = FindTokenIndexAtOffset(originalTokens, globalOffset);
                    statements.Add(found.Statement with
                    {
                        Anchor = ReadAnchor(originalTokens, globalTokenIndex)
                    });
                }
            }

            return (statements, lostStatementCount);
        }

        /// <summary>
        /// 펜스 텍스트를 최상위(괄호 깊이 0) 세미콜론 기준으로 조각낸다.
        ///
        /// [왜 필요한가 - 실측]
        /// ScriptDom은 구문 오류를 만나면 그 지점에서 완전히 멈춘다 - 오류
        /// 뒤의 문장은 절대 복구하지 않는다(1라운드 실측: POQSettleProc3/S08의
        /// sp_getapplock 오류 뒤 UPDATE 2개가 배치 전체 실패로 사라짐).
        /// 반면 펜스를 최상위 세미콜론으로 잘라 조각마다 독립적으로 파싱하면
        /// 한 조각의 오류가 다른 조각을 건드리지 않는다 - ScriptDom의 자체
        /// 오류 복구 능력에 기대지 않는다.
        ///
        /// [왜 문자열 스캔이 아니라 토큰 스트림인가]
        /// 문자열 리터럴·주석 안의 `;`·`(`·`)`는 분할 지점이 아니다.
        /// `GetTokenStream`은 문법 분석 없이 어휘 분석만 하므로 구문 오류가
        /// 있어도 안정적으로 동작하면서 문자열·주석을 이미 올바른 토큰
        /// 종류로 분류해 준다 - 직접 스캔하면 이 처리를 다시 구현해야 한다.
        /// </summary>
        private static IEnumerable<(int Start, int EndExclusive, bool ContainsDmlKeyword)> SplitAtTopLevelSemicolons(
            string sql, IList<TSqlParserToken> tokens)
        {
            var depth = 0;
            var chunkStart = 0;
            var containsDml = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                switch (token.TokenType)
                {
                    case TSqlTokenType.LeftParenthesis:
                        depth++;
                        break;
                    case TSqlTokenType.RightParenthesis:
                        if (depth > 0) depth--;
                        break;
                    case TSqlTokenType.Insert:
                    case TSqlTokenType.Update:
                    case TSqlTokenType.Delete:
                        containsDml = true;
                        break;
                    case TSqlTokenType.Semicolon when depth == 0:
                    {
                        var endExclusive = token.Offset + token.Text.Length;
                        yield return (chunkStart, endExclusive, containsDml);
                        chunkStart = endExclusive;
                        containsDml = false;
                        break;
                    }
                    // [코퍼스 실측 - 25개 표본 중 6개] `IF … BEGIN UPDATE … WHERE …; END`처럼
                    // DML이 세미콜론 없는 `BEGIN` 바로 다음에 오면, `BEGIN`을 분할
                    // 지점으로 두지 않는 한 "IF … BEGIN UPDATE …;" 조각 전체가 BEGIN의
                    // 짝(END)이 없어 파싱에 실패한다 - 그 안의 진짜 UPDATE까지 억울하게
                    // 손실로 잡힌다. `BEGIN TRAN`·`BEGIN TRANSACTION`은 블록을 여는
                    // `BEGIN`이 아니라 그 자체로 완결된 문장이므로 분할하지 않는다.
                    case TSqlTokenType.Begin when depth == 0 && !IsBeginTransaction(tokens, i):
                    {
                        var beginEnd = token.Offset + token.Text.Length;
                        yield return (chunkStart, token.Offset, containsDml);
                        chunkStart = beginEnd;
                        containsDml = false;
                        break;
                    }
                }
            }

            if (chunkStart < sql.Length)
            {
                yield return (chunkStart, sql.Length, containsDml);
            }
        }

        /// <summary>다음 실토큰(공백·주석 제외)이 TRAN·TRANSACTION인지 본다.</summary>
        private static bool IsBeginTransaction(IList<TSqlParserToken> tokens, int beginIndex)
        {
            for (int i = beginIndex + 1; i < tokens.Count; i++)
            {
                var type = tokens[i].TokenType;
                if (type is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    continue;
                }

                return type is TSqlTokenType.Tran or TSqlTokenType.Transaction;
            }

            return false;
        }

        /// <summary>
        /// 주어진 문자 오프셋을 가진 토큰의 인덱스를 이분 탐색으로 찾는다. 토큰
        /// 배열은 Offset 기준 오름차순이다(ScriptDom이 순서대로 토큰화한다).
        /// 조각을 토큰 경계에서 정확히 잘랐으므로 항상 정확히 일치해야 하지만,
        /// 혹시 일치하지 않으면 바로 다음 토큰으로 안전하게 폴백한다.
        /// </summary>
        private static int FindTokenIndexAtOffset(IList<TSqlParserToken> tokens, int offset)
        {
            int lo = 0, hi = tokens.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (tokens[mid].Offset == offset) return mid;
                if (tokens[mid].Offset < offset) lo = mid + 1; else hi = mid - 1;
            }

            return Math.Max(0, Math.Min(lo, tokens.Count - 1));
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
            /// <summary>
            /// 문장과 그 시작 문자 오프셋(조각 텍스트 안의 상대 좌표). 앵커는
            /// ReadFence가 이 오프셋을 원본 펜스 좌표로 변환해 원본 토큰
            /// 스트림에서 채운다 - 조각 자신의 좁은 토큰 스트림만으로는 조각
            /// 경계 너머의 문맥(예: 꼬리 주석인지 선행 주석인지)을 알 수 없다.
            /// </summary>
            public List<(StepSqlStatement Statement, int StartOffset)> Found { get; } = new();

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
                    statement.StartOffset));
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
