using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Anchor">주석에서 읽은 갱신 번호. 없으면 null.</param>
    /// <param name="HasOpaqueJoinSource">
    /// FROM 절의 조인 파트너 중 물리 테이블이 아닌 것(CTE·파생 테이블)이 있으면
    /// true. 마이그레이션이 원본 단일 UPDATE를 `UPDATE 대상 ... FROM 대상 AS Y
    /// INNER JOIN &lt;계산용 CTE·파생 테이블&gt; ON &lt;좁은 키&gt;`로 재구성하는
    /// 관용구가 실물(S07 U2·U13·U17)에 있다 - 실제 필터(예: PGName·ClientID)는
    /// 최상위 ON절이 아니라 그 서브쿼리 자신의 WHERE 안에 있는데, JoinColumns는
    /// 최상위만 보므로 이 값들을 볼 수 없다. 조인 키 대조(MechanicalValidator의
    /// CheckAnchoredStatementFacts)가 이 신호가 서면 "조인 키 없음"을 보고하지
    /// 않도록 스스로를 가린다 - 값을 보정하지 않고 신뢰할 수 없다는 사실만 남긴다.
    /// </param>
    /// <param name="CodeAnchor">
    /// 같은 구간(직전 문장의 끝 ~ 이 문장의 시작)에서 읽은 음수 오류 코드 라벨
    /// (`SET @&lt;변수&gt; = &lt;음수 정수 리터럴&gt;;`)의 코드 원문(예: `"-13"`).
    /// 구간에 그런 대입이 정확히 하나가 아니면 null. <see cref="Anchor"/>와는
    /// 독립적으로 읽히며 둘이 공존할 수 있다.
    /// </param>
    /// <param name="SourceTable">이 문장이 읽는, 같은 단계의 앞선 문장이 쓴 테이블.</param>
    /// <param name="Columns">그 앞선 문장의 술어·조인 키·하위 범위 컬럼 전부.</param>
    public sealed record StepLineageSource(string SourceTable, IReadOnlyList<string> Columns);

    public sealed record StepSqlStatement(
        string Kind,
        string TargetTable,
        int? Anchor,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinColumns,
        bool HasGrouping,
        bool HasOpaqueJoinSource = false,
        string? CodeAnchor = null)
    {
        /// <summary>
        /// 하위 스코프(CTE 본문·파생 테이블·최상위 WHERE 안의 하위질의·JOIN ON 안의
        /// 하위질의)의 WHERE에 나오는 컬럼. <see cref="PredicateColumns"/>와 겹칠 수
        /// 있다 — 최상위 WHERE가 두 수집기의 공통 진입점이라(<c>DmlCollector.Add</c>가
        /// 절마다 `where.Accept(predicates)`·`where.Accept(subordinate)`를 각각 부른다),
        /// 최상위 WHERE 안의 하위질의에 최상위와 같은 이름의 컬럼이 있으면 양쪽에
        /// 다 잡힌다(예: `WHERE Y.YMD = @p AND EXISTS
        /// (SELECT 1 FROM B WHERE B.YMD = @p AND B.ID = Y.ID)` → Pred=[YMD],
        /// Sub=[YMD, ID, ID]). 판정에는 무해하다 — 검사 B는 두 값이 모두 있으면
        /// 어차피 침묵하므로 겹침 자체가 결과를 바꾸지 않는다.
        ///
        /// [무엇을 위한 값인가] 원본이 최상위 WHERE에 두었던 술어를 이행이 하위
        /// 스코프로 옮기는 관용구가 실재한다(2026-08-26 표본 판정 30건). 최상위만
        /// 보는 대조는 그것을 "없어졌다"로 읽는다. 이 값이 있으면 검사 B가
        /// <b>소실과 이전을 구분</b>할 수 있다.
        ///
        /// [무엇을 뜻하지 않는가] 하위 스코프에 있다고 의미 동등은 아니다.
        /// 동등성은 조인이 대상 행 집합을 보존하느냐에 달렸고 그 전제는 로컬에서
        /// 검증할 수 없다. 이 값은 "옮겨갔다"까지만 말한다.
        ///
        /// [SET 절은 세지 않는다] 갱신할 "값"을 고르는 하위질의의 술어는 갱신할
        /// "행"을 고르는 술어가 아니다. 세면 우연히 이름이 같은 컬럼이 진짜 소실을
        /// 가린다.
        /// </summary>
        public IReadOnlyList<string> SubordinatePredicateColumns { get; init; }
            = Array.Empty<string>();

        /// <summary>
        /// 이 문장의 FROM·JOIN이 이름으로 참조하는 테이블(마지막 식별자).
        /// CTE 이름은 제외한다 - 테이블이 아니다. 파생 테이블·스칼라 하위질의
        /// 안쪽은 이 층이 아니므로 NamedSourceFinder가 하강을 막는다.
        /// </summary>
        public IReadOnlyList<string> RowSourceTables { get; init; }
            = Array.Empty<string>();

        /// <summary>
        /// 이 문장이 읽는 「단계 내부 스테이징」 후보와 그것을 쓴 문장의 컬럼.
        ///
        /// [불변식 - 검사 쪽이 이것에 의존한다] 행 원천이 **전부** 앞선 쓰기 대상일
        /// 때만 채워진다. 하나라도 앞서 쓰인 적 없는 테이블이면 빈 목록이다.
        /// 이 불변식이 없으면 검사 쪽의 All(…)이 부분집합 위에서 공허하게 참이 된다.
        ///
        /// [명세서 대상 제외는 여기서 하지 않는다] 리더는 명세서를 보지 않는다.
        /// 원본이 쓰는 테이블을 걸러 내는 것은 MechanicalValidator의 몫이다 -
        /// 그 제외가 없으면 DELETE 후 INSERT로 재게시하고 다시 UPDATE … FROM 하는
        /// 흔한 관용구가 게시문으로 오분류된다(설계서 §2-1, 코퍼스 탐침 118건 중
        /// 최다 원천이 원본 대상 테이블 tsettlemst 52건).
        /// </summary>
        public IReadOnlyList<StepLineageSource> LineageSources { get; init; }
            = Array.Empty<StepLineageSource>();
    }

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

            return AttachLineage(statements);
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

            var lostStatementCount = 0;

            // 조각마다 독립적으로 파싱하므로 먼저 (문장, 원본 좌표 시작·끝)을 전부
            // 모은 뒤에 앵커를 매긴다 - 앵커 판정(아래 ReadAnchor)이 "직전 문장의
            // 끝"을 알아야 하는데, 그 직전 문장이 다른 조각(다른 세미콜론 구간)에
            // 있을 수 있어 조각 단위 반복 안에서는 알 수 없다.
            var found = new List<(StepSqlStatement Statement, int GlobalStart, int GlobalEnd)>();

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

                foreach (var item in visitor.Found)
                {
                    // 원본 펜스 좌표로 변환한다 - 조각 자신의 좁은 좌표로는 조각
                    // 경계 너머의 문맥(꼬리 주석인지 선행 주석인지, 직전 문장이
                    // 어디서 끝나는지)을 알 수 없다.
                    found.Add((item.Statement, start + item.StartOffset, start + item.EndOffset));
                }
            }

            found.Sort((a, b) => a.GlobalStart.CompareTo(b.GlobalStart));

            var statements = new List<StepSqlStatement>(found.Count);
            var previousEnd = 0;
            foreach (var (statement, globalStart, globalEnd) in found)
            {
                var globalTokenIndex = FindTokenIndexAtOffset(originalTokens, globalStart);
                statements.Add(statement with
                {
                    Anchor = ReadAnchor(originalTokens, previousEnd, globalTokenIndex),
                    CodeAnchor = ReadCodeAnchor(originalTokens, previousEnd, globalTokenIndex)
                });
                previousEnd = globalEnd;
            }

            return (statements, lostStatementCount);
        }

        /// <summary>
        /// 단계 전체에서 「행 원천이 전부 앞선 문장의 쓰기 대상」인 문장을 찾아
        /// 그 쓰기 문장의 컬럼을 매단다.
        ///
        /// [왜 Read에서 도는가 - 실물] 적재문과 게시문이 **다른 펜스**에 있다.
        /// POQSettleProc2/S13은 적재가 펜스 2, 게시가 펜스 3이다. ReadFence 안에서
        /// 돌면 이 관용구를 통째로 놓친다.
        ///
        /// [왜 오프셋이 아니라 인덱스인가] Read는 펜스를 문서 순서로 순회하고
        /// ReadFence는 반환 전에 오프셋으로 정렬한다. 누적 리스트가 이미 문서
        /// 순서이므로 인덱스가 곧 순서다.
        ///
        /// [한 홉만] 사슬(A → S1, S1을 읽어 S2를 씀, S2를 읽는 문장)은 따라가지
        /// 않는다. 실물 셋 다 한 홉이고, 미추적은 오탐 방향이라 안전하다.
        ///
        /// [제어 흐름은 보지 않는다] 앞선다는 것은 문서 순서다. 조건부로만 실행되는
        /// 적재문의 술어도 상속되는데 이는 침묵 방향이다 - 한계로 기록한다.
        /// </summary>
        private static IReadOnlyList<StepSqlStatement> AttachLineage(
            IReadOnlyList<StepSqlStatement> statements)
        {
            if (statements.Count < 2) return statements;

            var writtenAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var attached = new List<StepSqlStatement>(statements.Count);

            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                var sources = statement.RowSourceTables;

                // 원천이 하나도 없거나(VALUES 삽입 등) 하나라도 앞서 쓰인 적이
                // 없으면 계보가 아니다 - 불변식.
                if (sources.Count > 0 && sources.All(writtenAt.ContainsKey))
                {
                    attached.Add(statement with
                    {
                        LineageSources = sources
                            .Select(s => new StepLineageSource(
                                s, ColumnsOf(statements[writtenAt[s]])))
                            .ToList()
                    });
                }
                else
                {
                    attached.Add(statement);
                }

                // 자기 자신은 뒤 문장에게만 보인다 - 먼저 읽고 나중에 등록한다.
                //
                // [한계 - 같은 테이블에 두 번 쓰면 첫 쓰기만 기억한다] TryAdd이므로
                // `DELETE FROM T; INSERT INTO T ...;`처럼 같은 단계 안에서 T를 두 번
                // 쓰면, 뒤에서 T를 읽는 문장은 항상 DELETE의 WHERE 컬럼을 물려받고
                // INSERT의 진짜 술어는 못 본다. 이것은 **과소** 이전 쪽으로 기운다 -
                // 나중 쓰기의 진짜 술어 변화가 하류에 인정되지 않으므로 침묵이 아니라
                // 오탐(검사가 여전히 발화) 방향이다. 설계가 밝힌 선호(거짓 음성보다
                // 거짓 양성)와 일치한다. 가리는 경로는 찾지 못했다 - 코퍼스 실측이
                // 아니라 추론으로 짚은 한계다.
                if (!string.IsNullOrEmpty(statement.TargetTable))
                {
                    writtenAt.TryAdd(statement.TargetTable, i);
                }
            }

            return attached;

            // [한 홉만이 유지되는 진짜 이유 - 구조적 우연, ColumnsOf의 결정이 아니다]
            // writer는 항상 이 함수의 매개변수 `statements`(계보 부착 이전 원본
            // 스냅샷)에서 온다 - `attached`(계보가 이미 매겨진 목록)가 아니다.
            // `statements[j]`는 이 함수 안에서 절대 갱신되지 않으므로 그 `.LineageSources`는
            // 언제나 기본값(빈 목록)이다. 그래서 여기에 `.Concat(writer.LineageSources
            // .SelectMany(l => l.Columns))`를 더해도 관측 가능한 차이가 전혀 없다 -
            // 사슬(S1 → S2 → 게시)이 새지 않는 것은 이 메서드가 사슬을 막아서가
            // 아니라 애초에 `statements[writtenAt[s]]`가 계보를 가질 수 없어서다.
            // **`writer`를 `attached[writtenAt[s]]`로 바꾸지 말 것** - 그 순간 이
            // 불변이 깨지고 사슬 추적이 조용히 되살아난다(2026-08-27 픽스 라운드 1
            // 리뷰가 변이로 확인: 이 Concat을 더해도 Lineage_DoesNotFollowChains가
            // 죽지 않는다 - 결과만 같고 기제가 다르다는 뜻이다).
            static IReadOnlyList<string> ColumnsOf(StepSqlStatement writer) => writer
                .PredicateColumns
                .Concat(writer.JoinColumns)
                .Concat(writer.SubordinatePredicateColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
        /// 직전 문장(또는 펜스 시작)과 이 문장 사이 구간에서 앵커 번호를 읽는다.
        ///
        /// [1라운드 리뷰 실측 - 왜 "구간에 정확히 1개"인가]
        /// 예전 구현은 "바로 앞 토큰이 주석이면 그것" 하나만 봤다. 그러면 두 가지가
        /// 깨진다: (1) `A문장; -- U4 참고\nB문장`처럼 A의 꼬리 주석이 B의 앵커로
        /// 잘못 붙는다(개행으로 가른다 - 아래 PrecededByNewline). (2) 실물 관용구
        /// `/* U13: … */ → SET @v_currentStepId = -20; → UPDATE …`처럼 주석과 DML
        /// 사이에 `SET` 한 줄이 끼면(AiService의 [Precise Error Tracking] 규칙이
        /// 모든 DML 직전에 요구하는 필수 패턴) "바로 앞 토큰"이 주석이 아니라 SET이
        /// 되어 앵커를 통째로 놓친다(코퍼스 326개 전수 0개, docs/known-defects.md).
        ///
        /// [태스크 22 - 왜 SET을 그냥 건너뛰는 것만으로는 안 되는가]
        /// 단순히 SET을 건너뛰면 더 나빠진다는 것이 태스크 11의 실측이다 - 미구현
        /// 갱신의 서술 주석(DML 없음)이 SET과 함께 남아 있으면, 뒤에 오는 무관한
        /// 실제 DML이 그 주석을 훔친다(실물 3건: S07:244가 U15를 훔쳐 실제로는
        /// spec UPDATE 16인데 15로 오귀속). 그래서 "가장 가까운 것" 대신 "직전
        /// 문장의 끝부터 이 문장의 시작까지 구간에 앵커 모양 주석이 몇 개인가"를
        /// 센다 - 정확히 1개일 때만 신뢰한다. 훔친 사례는 이 구간에 주석이 2개
        /// (U14 자리 + U15 자리) 걸리므로 유일하지 않아 자동으로 침묵한다 - 문장의
        /// 내용(컬럼)을 전혀 보지 않고 순수하게 기계적으로 판별된다.
        /// </summary>
        private static int? ReadAnchor(IList<TSqlParserToken> tokens, int windowStartOffset, int firstTokenIndex)
        {
            var windowStartTokenIndex = FindTokenIndexAtOffset(tokens, windowStartOffset);

            int? ordinal = null;
            var matchCount = 0;

            for (int i = firstTokenIndex - 1; i >= windowStartTokenIndex; i--)
            {
                var token = tokens[i];
                if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                {
                    // SET 문·세미콜론 등 주석이 아닌 토큰은 건너뛰고 계속 거슬러
                    // 올라간다 - 이게 "SET이 끼어도 앵커를 놓치지 않는다"의 핵심이다.
                    continue;
                }

                if (!PrecededByNewline(tokens, i)) continue; // 꼬리 주석은 후보가 아니다.

                var match = AnchorPattern.Match(token.Text);
                if (!match.Success) continue;

                matchCount++;
                ordinal = int.Parse(match.Groups["ordinal"].Value);
            }

            return matchCount == 1 ? ordinal : null;
        }

        /// 음수 정수 리터럴 대입만. `@v = 0`·`@v = @@ROWCOUNT`는 후보가 아니다.
        /// <see cref="ReadCodeAnchor"/> 참고 - 왜 부호로 좁히는지는 그쪽 문서에 있다.
        private static readonly Regex CodeAnchorPattern = new(
            @"\bSET\s+@[A-Za-z_][A-Za-z_0-9]*\s*=\s*(?<code>-\s*\d+)\s*;?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// <see cref="ReadAnchor"/>가 쓰는 것과 같은 구간(직전 문장의 끝 ~ 이
        /// 문장의 시작)에서 음수 오류 코드 라벨을 읽는다.
        ///
        /// [왜 같은 구간을 재사용하는가]
        /// 두 앵커(U-주석 기반 <see cref="Anchor"/>와 이 코드 앵커)가 서로 다른
        /// 구간을 쓰면 서로 다른 자리를 가리킬 수 있다 - 태스크 22가 세운
        /// 「구간 내 유일성」의 안전성 논거가 그 순간 무너진다. 그래서 새 구간
        /// 계산을 만들지 않고 <see cref="ReadAnchor"/>와 동일한 windowStartOffset·
        /// firstTokenIndex를 받는다.
        ///
        /// [왜 "정확히 하나일 때만"인가]
        /// <see cref="ReadAnchor"/>와 같은 이유다 - 구간에 후보가 둘 이상이면 그
        /// 중 어느 것이 이 문장의 라벨인지 기계적으로 알 수 없다.
        ///
        /// [왜 음수만 후보인가]
        /// 규약 6-1이 요구하는 `DECLARE @v_currentStepId INT = 0;` 초기화와
        /// `SET @v_cnt = @@ROWCOUNT;` 같은 관용구가 전부 비음수라, 음수로 좁혀야
        /// 이들이 후보에서 자연히 빠지고 「구간에 정확히 하나」가 실제로 성립한다.
        ///
        /// [왜 토큰 하나가 아니라 텍스트를 재구성해 정규식을 돌리는가]
        /// `SET @v = -13;`은 SET·변수·`=`·`-`·`13`·`;` 여러 토큰에 걸친다.
        /// <see cref="ReadAnchor"/>처럼 토큰 하나씩 보는 방식으로는 이 모양을
        /// 잡을 수 없다 - 구간 안 토큰의 원문 Text를 이어붙여 그 문자열에
        /// 정규식을 돌린다. 토큰은 Offset 기준으로 빈틈없이 이어지므로 이어붙인
        /// 문자열은 원본 펜스의 해당 구간과 문자 단위로 같다.
        ///
        /// [왜 주석 토큰은 빼는가 - 리뷰 라운드 1 발견 2]
        /// `ReadAnchor`는 주석 토큰만 후보로 본다. 이 메서드는 반대로 실코드만
        /// 봐야 한다 - 주석 안에 `-- 예시: SET @v_currentStepId = -101;`처럼
        /// 예시 문구가 있으면(실물: output/Jobs/POQSettleProc12/agent/common/
        /// 01-step-contract.md) 그 문구가 실제 SET 문이 아닌데도 잡힌다. 주석
        /// 토큰의 Text를 통째로 빼되 공백 하나로 치환한다 - 그냥 빼면 주석
        /// 앞뒤 실토큰이 공백 없이 이어붙어(`SET-- 주석 --@v`처럼) 엉뚱하게
        /// 합쳐질 위험이 있다.
        /// </summary>
        private static string? ReadCodeAnchor(IList<TSqlParserToken> tokens, int windowStartOffset, int firstTokenIndex)
        {
            var windowStartTokenIndex = FindTokenIndexAtOffset(tokens, windowStartOffset);

            var window = new StringBuilder();
            for (int i = windowStartTokenIndex; i < firstTokenIndex; i++)
            {
                var token = tokens[i];
                if (token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    window.Append(' ');
                    continue;
                }

                window.Append(token.Text);
            }

            var matches = CodeAnchorPattern.Matches(window.ToString());
            if (matches.Count != 1) return null;

            return Regex.Replace(matches[0].Groups["code"].Value, @"\s+", string.Empty);
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
            /// 문장과 그 시작·끝 문자 오프셋(조각 텍스트 안의 상대 좌표). 앵커는
            /// ReadFence가 이 오프셋을 원본 펜스 좌표로 변환해 원본 토큰
            /// 스트림에서 채운다 - 조각 자신의 좁은 토큰 스트림만으로는 조각
            /// 경계 너머의 문맥(예: 꼬리 주석인지 선행 주석인지, 직전 문장이
            /// 어디서 끝나는지)을 알 수 없다. 끝 오프셋(<see cref="TSqlFragment.FragmentLength"/>
            /// 기반)은 "직전 문장 이후 ~ 이 문장 이전" 구간에서 앵커 후보 주석을
            /// 세는 데 쓴다(ReadAnchor 참고).
            /// </summary>
            public List<(StepSqlStatement Statement, int StartOffset, int EndOffset)> Found { get; } = new();

            public override void Visit(UpdateStatement node) =>
                Add("UPDATE", node, node.UpdateSpecification?.Target,
                    One(node.UpdateSpecification?.WhereClause),
                    One(node.UpdateSpecification?.FromClause),
                    node.UpdateSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            public override void Visit(DeleteStatement node) =>
                Add("DELETE", node, node.DeleteSpecification?.Target,
                    One(node.DeleteSpecification?.WhereClause),
                    One(node.DeleteSpecification?.FromClause),
                    node.DeleteSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            /// <summary>
            /// INSERT의 술어는 InsertSpecification이 아니라 원천 SELECT에 있다.
            /// UNION 원천이면 QuerySpecification이 여럿이고, DmlScopeExtractor는
            /// 그것들을 같은 서수 하나로 합쳐 명세서 DML 범위 표에 적는다 -
            /// 그래서 읽기 쪽도 합친다.
            ///
            /// targetAliasScope가 null인 이유: INSERT 대상은 별칭일 수 없고
            /// (`INSERT INTO &lt;별칭&gt;`은 문법에 없다), 원천 SELECT의 FROM은
            /// 대상과 다른 이름 범위다. 거기에 `FROM dbo.TFoo AS TSettleMst`가
            /// 있으면 `INSERT INTO TSettleMst`의 대상이 TFoo로 잘못 풀린다.
            /// </summary>
            public override void Visit(InsertStatement node)
            {
                var specs = DmlScopeExtractor
                    .SourceQuerySpecifications(node.InsertSpecification?.InsertSource)
                    .ToList();

                Add("INSERT", node, node.InsertSpecification?.Target,
                    specs.Select(s => s.WhereClause).OfType<WhereClause>().ToList(),
                    specs.Select(s => s.FromClause).OfType<FromClause>().ToList(),
                    targetAliasScope: null,
                    node.WithCtesAndXmlNamespaces);
            }

            private void Add(
                string kind,
                TSqlStatement statement,
                TableReference? target,
                IReadOnlyList<WhereClause> wheres,
                IReadOnlyList<FromClause> froms,
                FromClause? targetAliasScope,
                WithCtesAndXmlNamespaces? ctes)
            {
                var predicates = new ColumnCollector();
                var joins = new ColumnCollector();
                var grouping = new GroupingProbe();

                foreach (var where in wheres) where.Accept(predicates);
                foreach (var from in froms) from.Accept(joins);
                statement.Accept(grouping);

                // 실릴·바뀔 행을 고를 수 있는 네 자리(WITH 본문·파생 테이블·JOIN ON
                // 절 안의 하위질의·최상위 WHERE 안의 하위질의)에서만 모은다.
                // UPDATE·DELETE에서는 "거를 대상 행"이고 INSERT에서는 "실릴 원천
                // 행"이다 - 셋 다 같은 네 자리를 본다. 파생
                // 테이블과 JOIN ON 하위질의는 둘 다 절 하나당 from.Accept 한 번으로
                // 함께 잡힌다(FROM 절 순회가 JOIN ON 절도 훑는다). JOIN ON 하위질의는
                // INNER JOIN이면 대상 행을 실제로 거르므로 여기서 모으는 것이
                // 의도와 어긋나지 않는다(실측: `INNER JOIN dbo.TCost AS C ON
                // ... AND C.ID IN (SELECT Z.ID FROM dbo.TZ AS Z WHERE Z.Hidden = 1)`
                // → Sub=[Hidden]). statement.Accept로 문장 전체를 훑으면 SET 절
                // 안의 하위질의까지 걸리는데, 그건 갱신할 "값"을 고르는 술어이지
                // 갱신할 "행"을 고르는 술어가 아니다.
                var aliases = CteProjectionAliases.Build(ctes);
                var subordinate = new SubordinatePredicateCollector(aliases);
                ctes?.Accept(subordinate);
                foreach (var from in froms) from.Accept(subordinate);
                foreach (var where in wheres) where.Accept(subordinate);

                var resolvedTarget = ResolveTargetTable(target, targetAliasScope);

                Found.Add((
                    new StepSqlStatement(
                        kind,
                        resolvedTarget,
                        Anchor: null,
                        predicates.Columns.ToList(),
                        joins.Columns.ToList(),
                        grouping.Found,
                        HasOpaqueJoinSource: DetectOpaqueJoinSource(statement, froms))
                    {
                        SubordinatePredicateColumns = subordinate.Columns.ToList(),
                        RowSourceTables = CollectRowSourceTables(froms, ctes, resolvedTarget),
                    },
                    statement.StartOffset,
                    statement.StartOffset + statement.FragmentLength));
            }

            /// <summary>
            /// FROM·JOIN의 이름 테이블에서 CTE 이름을 뺀 것. 대상 테이블 자신도
            /// 뺀다 - UPDATE … FROM 대상 AS A 는 자기를 읽는 것이 아니다.
            ///
            /// [왜 대상 자신을 빼는가 - 리뷰 라운드 1 Critical 1]
            /// `UPDATE X SET … FROM stage.Foo AS X WHERE …`처럼 대상을 FROM 별칭으로
            /// 다시 참조하는 관용구는 다른 상류 테이블을 읽는 게 아니라 자기가 이미
            /// 쓴 행을 되읽을 뿐이다. 이 자기참조가 걸러지지 않으면, Foo가 명세서
            /// DML 범위 표의 대상이 아닐 때(Task 2/3의 specTargets 필터가 이 경우를
            /// 막지 못한다 - 그 필터는 "자기참조 테이블이 명세서 대상과 우연히
            /// 같을 때"만 방어한다) 무관한 앞 문장의 술어가 「이전됨」으로 조용히
            /// 붙는다(실측: Lineage_SelfReferencingUpdateOnStaging_DoesNotInheritUnrelatedPredicate).
            ///
            /// [왜 도달 가능한 CTE만 훑는가 - 리뷰 라운드 1 Critical 2]
            /// 이전 구현은 `ctes?.Accept(finder)`로 선언된 CTE 전부를 무조건 훑었다.
            /// 그러면 최상위 질의가 참조하지 않는 죽은 CTE(`WITH Unused AS (…),
            /// Pick AS (…) SELECT … FROM Pick`의 Unused)의 본문이 앞서 쓰인 테이블을
            /// 참조하기만 해도 그 테이블이 이 문장의 원천으로 잡히고, 그 죽은 CTE의
            /// 무관한 술어가 계보로 새며 「원천이 전부 앞선 쓰기여야 한다」 불변식의
            /// 성립 여부 자체가 죽은 코드에 좌우된다(실측:
            /// Lineage_UnreachableCte_DoesNotContributeRowSourceOrLineage).
            /// 그래서 최상위 FROM에서 시작해 실제로 참조되는 CTE만 너비 우선으로
            /// 따라간다 - 참조되지 않는 CTE의 본문은 아예 방문하지 않는다.
            /// NamedSourceFinder가 파생 테이블·스칼라 하위질의 하강은 이미 막으므로
            /// 방문한 CTE 본문 안의 더 깊은 층까지 새지 않는다.
            /// </summary>
            private static IReadOnlyList<string> CollectRowSourceTables(
                IReadOnlyList<FromClause> froms, WithCtesAndXmlNamespaces? ctes, string selfTarget)
            {
                var cteBodies = new Dictionary<string, IReadOnlyList<FromClause>>(
                    StringComparer.OrdinalIgnoreCase);
                if (ctes?.CommonTableExpressions != null)
                {
                    foreach (var cte in ctes.CommonTableExpressions)
                    {
                        var name = cte.ExpressionName?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        cteBodies[name!] = DmlScopeExtractor
                            .QuerySpecificationsOf(cte.QueryExpression)
                            .Select(spec => spec.FromClause)
                            .OfType<FromClause>()
                            .ToList();
                    }
                }

                var physicalTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // [방어적 - 어떤 테스트도 이 줄의 필요성을 재지 않는다] 이 방문 기록이
                // 없으면 두 CTE가 서로를 참조하는 순환이나 같은 CTE를 두 경로에서
                // 참조하는 다이아몬드에서 무한 루프·중복 처리가 날 수 있다. 이
                // 코퍼스에는 그런 순환·다이아몬드가 없어 실제로 제거해 봐도(2026-08-27
                // 픽스 라운드 1) 열한 개 테스트가 전부 그대로 통과한다 - 이 줄을
                // 죽이는 테스트가 없다는 뜻이다. 무한 루프를 실제로 만드는 순환 CTE
                // 테스트는 테스트 스위트 자체를 멈출 위험이 있어 만들지 않았다 - 이
                // 줄은 검증된 결정이 아니라 방어적 코드로 남겨 둔다.
                var visitedCtes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var frontier = new Queue<IReadOnlyList<FromClause>>();
                frontier.Enqueue(froms);

                while (frontier.Count > 0)
                {
                    var currentFroms = frontier.Dequeue();
                    var finder = new NamedSourceFinder();
                    foreach (var from in currentFroms) from.Accept(finder);

                    foreach (var source in finder.Sources.Select(s => s.Source))
                    {
                        if (cteBodies.TryGetValue(source, out var bodyFroms))
                        {
                            if (visitedCtes.Add(source)) frontier.Enqueue(bodyFroms);
                        }
                        else
                        {
                            physicalTables.Add(source);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(selfTarget)) physicalTables.Remove(selfTarget);

                return physicalTables.ToList();
            }

            /// <summary>
            /// 절 하나를 목록으로 감싼다. UPDATE·DELETE는 절이 최대 하나이므로
            /// 이걸 쓰고, INSERT만 원천 명세 수만큼 여럿을 넘긴다.
            /// </summary>
            private static IReadOnlyList<T> One<T>(T? node) where T : class =>
                node is null ? Array.Empty<T>() : new[] { node };

            /// <summary>
            /// FROM 절의 조인 파트너 중 CTE·파생 테이블이 있는지 본다 - 위
            /// <see cref="StepSqlStatement.HasOpaqueJoinSource"/> 문서 참고.
            /// </summary>
            private static bool DetectOpaqueJoinSource(TSqlStatement statement, IReadOnlyList<FromClause> froms)
            {
                if (froms.Count == 0) return false;

                var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (statement is StatementWithCtesAndXmlNamespaces withCtes &&
                    withCtes.WithCtesAndXmlNamespaces != null)
                {
                    foreach (var cte in withCtes.WithCtesAndXmlNamespaces.CommonTableExpressions)
                    {
                        if (!string.IsNullOrWhiteSpace(cte.ExpressionName?.Value))
                        {
                            cteNames.Add(cte.ExpressionName!.Value);
                        }
                    }
                }

                // UNION 원천의 한 갈래만 불투명해도 접는다 - 오탐보다 침묵이 안전한 방향이다.
                var probe = new OpaqueJoinSourceProbe(cteNames);
                foreach (var from in froms) from.Accept(probe);
                return probe.Found;
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
        /// FROM 절에 물리 테이블이 아닌 조인 파트너(파생 테이블 또는 CTE 이름
        /// 참조)가 있는지만 본다 - 그 안쪽 내용은 보지 않는다(<see
        /// cref="StepSqlStatement.HasOpaqueJoinSource"/> 참고).
        /// </summary>
        private sealed class OpaqueJoinSourceProbe : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _cteNames;
            public bool Found { get; private set; }

            public OpaqueJoinSourceProbe(HashSet<string> cteNames) => _cteNames = cteNames;

            /// <summary>파생 테이블을 만나는 순간으로 충분하다 - 안쪽으로 내려가지 않는다.</summary>
            public override void ExplicitVisit(QueryDerivedTable node) => Found = true;

            public override void Visit(NamedTableReference node)
            {
                // 한정자 없는 이름만 CTE일 수 있다 - `dbo.T`처럼 점으로 한정되면
                // 물리 테이블이 확실하다(FromClauseAliasFinder와 같은 판정).
                var identifiers = node.SchemaObject?.Identifiers;
                if (identifiers == null || identifiers.Count != 1) return;

                var name = identifiers[0].Value;
                if (!string.IsNullOrWhiteSpace(name) && _cteNames.Contains(name))
                {
                    Found = true;
                }
            }
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
            private readonly List<(string? Qualifier, string Name)> _references = new();

            public IReadOnlyList<string> Columns => _columns;

            /// <summary>
            /// 같은 참조를 한정자와 함께 남긴 것. 대조는 이름만 쓰지만, CTE 투영
            /// 별칭 해석은 `R.Flag`(실테이블 R의 실컬럼)와 한정자 없는 `Flag`를
            /// 갈라야 한다 - 이름만 보면 둘이 구분되지 않아 무관한 컬럼에 별칭
            /// 해석이 붙는다(리뷰 R1).
            /// </summary>
            public IReadOnlyList<(string? Qualifier, string Name)> References => _references;

            public override void Visit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;
                var last = identifiers?.LastOrDefault();
                if (string.IsNullOrWhiteSpace(last?.Value)) return;

                _columns.Add(last!.Value);
                _references.Add((
                    identifiers!.Count >= 2 ? identifiers[identifiers.Count - 2].Value : null,
                    last.Value));
            }

            /// <summary>스칼라 하위질의 안쪽으로 내려가지 않는다 - 최상위 술어 컬럼만 센다.</summary>
            public override void ExplicitVisit(ScalarSubquery node) { }

            /// <summary>파생 테이블(FROM 절 안의 (SELECT …) 별칭) 안쪽도 최상위가 아니다.</summary>
            public override void ExplicitVisit(QueryDerivedTable node) { }
        }

        /// <summary>
        /// CTE가 투영하며 붙인 별칭을 원천 컬럼 이름으로 되돌리는 사상.
        ///
        /// [왜 필요한가 - 2026-08-27 코퍼스 판정 3건]
        /// 하위 수집기는 컬럼 **이름**만 모으고 검사 B도 이름으로만 대조한다.
        /// 바깥 CTE가 `A.USESTATE AS ContractUseState`로 투영하고 안쪽 CTE가
        /// `WHERE ContractUseState IN (0,4)`로 거르면, 필터는 그대로 있는데
        /// 수집되는 이름이 명세서의 `USESTATE`와 글자가 달라 "명세서가 확정한
        /// 컬럼이 없어졌다"는 오탐이 난다(docs/known-defects.md (5-3-3) 부류 2).
        ///
        /// [두 형태를 다 봐야 한다]
        /// 실물 두 건이 서로 다른 구문이다. POQSettlePrco20/S03은 인라인 `AS`이고,
        /// POQSettleProc17/S04는 `AS`가 **하나도 없이** CTE의 명시 컬럼 목록에
        /// 위치로 붙인다(`;WITH Base (…, ContractUseState, RateUseState) AS
        /// (SELECT …, A.USESTATE, B.USESTATE …)`). SelectScalarExpression의
        /// ColumnName만 보는 구현은 뒤쪽을 통째로 놓친다.
        ///
        /// [모호하면 해석하지 않는다]
        /// 같은 별칭이 서로 다른 원천을 가리키면 그 별칭을 버린다. 이 저장소가
        /// 모호성 앞에서 늘 택하는 방향이다(MergeErrorCodeMaps의 충돌 코드 제거,
        /// ResolveAnchoredStatements의 모호 서수 제거) - 조용한 거짓 음성보다
        /// 사람이 판정하는 거짓 양성이 낫다.
        ///
        /// [여기서 풀지 않는 같은 결함 둘 - 근거의 세기가 다르다]
        /// (가) **최상위에는 붙이지 않는다.** 최상위 WHERE·JOIN ON이 CTE 별칭을
        /// 참조하면 그 이름이 PredicateColumns·JoinColumns에 들어가 같은 masking이
        /// 난다. **코퍼스 전수 실측 0건** - 최상위에도 해석을 붙인 빌드로 전수 스윕을
        /// 돌려 발화가 59에서 한 건도 움직이지 않는 것을 확인했다(2026-08-27).
        /// (나) **파생 테이블 별칭은 보지 않는다.** `(SELECT A.USESTATE AS Foo …) X`를
        /// 바깥에서 `WHERE X.Foo = …`로 거르면 같은 모양인데 CTE가 아니라 잡히지
        /// 않는다. **이쪽은 재지 않았다** - (가)의 탐침은 CTE 별칭을 최상위까지
        /// 넓힌 것일 뿐, 파생 테이블 투영은 이 사상이 아예 보지 않는 다른 원천이다.
        /// (가)와 (나)를 「실측 0건」으로 묶어 적지 말 것.
        ///
        /// [사슬은 한 홉만] 별칭을 다시 별칭으로 덮는 CTE 사슬은 따라가지 않는다.
        /// 실물 두 건 모두 한 홉이다.
        /// </summary>
        private sealed class CteProjectionAliases
        {
            /// <summary>
            /// (CTE 이름, 별칭) → 원천 컬럼. 값이 null이면 모호해서 버려진 별칭이다.
            ///
            /// [왜 CTE 이름이 키에 들어가는가 - 2026-08-27 리뷰 F1]
            /// 처음에는 별칭 이름 하나로만 키를 잡았는데, 그러면 사상이 문장 전역이
            /// 되어 **아무 관계 없는 스코프의 같은 이름 실컬럼**에 붙는다. 실측:
            /// `;WITH Unused AS (SELECT A.CANCELYMD AS YMD …) DELETE … WHERE EXISTS
            /// (SELECT 1 FROM dbo.TZ AS Z WHERE Z.YMD = @p)`가 Sub=[YMD, CANCELYMD]를
            /// 냈다. `Unused`는 아무도 읽지 않고 걸러지는 것은 `TZ.YMD`인데
            /// `CANCELYMD`가 relocated에 실려, **명세서가 확정한 CANCELYMD 필터를
            /// 이행이 실제로 지웠어도 검사 B가 침묵한다.** 이 변경 자신이 내건
            /// 「모호하면 해석하지 않는다」를 정면으로 어기는 거짓 음성 경로였다.
            /// </summary>
            private readonly Dictionary<(string Cte, string Alias), string?> _map =
                new(new CteAliasKeyComparer());

            /// <summary>CTE 이름 집합. 스코프가 무엇을 읽는지 판정할 때 쓴다.</summary>
            private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

            private sealed class CteAliasKeyComparer : IEqualityComparer<(string Cte, string Alias)>
            {
                public bool Equals((string Cte, string Alias) x, (string Cte, string Alias) y) =>
                    StringComparer.OrdinalIgnoreCase.Equals(x.Cte, y.Cte)
                    && StringComparer.OrdinalIgnoreCase.Equals(x.Alias, y.Alias);

                public int GetHashCode((string Cte, string Alias) key) => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(key.Cte),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(key.Alias));
            }

            public static CteProjectionAliases Build(WithCtesAndXmlNamespaces? ctes)
            {
                var aliases = new CteProjectionAliases();
                if (ctes?.CommonTableExpressions == null) return aliases;

                foreach (var cte in ctes.CommonTableExpressions)
                {
                    var name = cte.ExpressionName?.Value;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    aliases._cteNames.Add(name!);

                    // 별칭을 정의하는 CTE 본문이 UNION이면 캐스트 하나로는 갈래를
                    // 못 편다. 이 헬퍼가 그 자리를 이미 갖고 있다.
                    foreach (var spec in DmlScopeExtractor.QuerySpecificationsOf(cte.QueryExpression))
                    {
                        aliases.RecordSpec(name!, cte.Columns, spec);
                    }
                }

                return aliases;
            }

            private void RecordSpec(string cte, IList<Identifier>? declared, QuerySpecification spec)
            {
                var elements = spec.SelectElements;
                if (elements == null) return;

                if (declared != null && declared.Count > 0)
                {
                    // 위치 짝짓기는 개수가 맞고 모든 요소가 스칼라일 때만 성립한다.
                    // `SELECT *`는 요소 하나가 몇 컬럼으로 펼쳐지는지 파서가 모르므로
                    // 전제가 깨진다 - 그 CTE는 통째로 건너뛴다.
                    if (elements.Count != declared.Count) return;
                    if (elements.Any(e => e is not SelectScalarExpression)) return;

                    for (var i = 0; i < declared.Count; i++)
                    {
                        Record(cte, declared[i]?.Value, SourceColumnOf(elements[i]));
                    }

                    return;
                }

                foreach (var element in elements.OfType<SelectScalarExpression>())
                {
                    Record(cte, element.ColumnName?.Value, SourceColumnOf(element));
                }
            }

            private static string? SourceColumnOf(SelectElement element) =>
                (element as SelectScalarExpression)?.Expression is ColumnReferenceExpression column
                    ? column.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value
                    : null;

            private void Record(string cte, string? alias, string? source)
            {
                if (string.IsNullOrWhiteSpace(alias)) return;

                var key = (cte, alias!);

                // [왜 원천이 null이어도 자리를 차지하는가 - 2026-08-27 리뷰 F2]
                // 원천 식이 컬럼 참조가 아니면(CONVERT·ISNULL·CASE·리터럴·함수)
                // 어느 컬럼에서 왔는지 알 수 없다. 그건 "정의가 없다"가 아니라
                // **"정의가 모호하다"**이므로 그 별칭을 오염시켜야 한다. 그냥
                // 넘어가면 경쟁하는 정의로 세어지지 않아, 같은 이름을 붙인 다른
                // 정의가 무경쟁으로 이긴다.
                if (string.IsNullOrWhiteSpace(source))
                {
                    _map[key] = null;
                    return;
                }

                if (!_map.TryGetValue(key, out var existing))
                {
                    _map[key] = source;
                    return;
                }

                // 항등 사상(`X AS X`)도 경쟁하는 정의로 센다. 빼면 같은 이름을
                // 그대로 투영하는 CTE가 다른 CTE의 개명과 충돌해도 안 걸린다.
                if (!string.Equals(existing, source, StringComparison.OrdinalIgnoreCase))
                {
                    _map[key] = null;
                }
            }

            /// <summary>
            /// 이 스코프의 FROM이 읽는 CTE 이름들. 별칭 해석의 적용 범위다.
            /// 파생 테이블·하위질의 안쪽은 그 QuerySpecification을 따로 방문할 때
            /// 그 층의 FROM으로 다시 판정되므로 여기서 내려가지 않는다.
            /// </summary>
            public IReadOnlyDictionary<string, string> CteBindingsIn(FromClause? from)
            {
                var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (from == null || _cteNames.Count == 0) return bindings;

                var finder = new NamedSourceFinder();
                from.Accept(finder);
                foreach (var (binding, source) in finder.Sources)
                {
                    if (_cteNames.Contains(source)) bindings[binding] = source;
                }

                return bindings;
            }

            /// <summary>
            /// 이 스코프가 읽는 CTE들이 정의한 별칭에 한해, 해석된 원천 이름을 낸다.
            /// 호출자가 수집된 이름 뒤에 **더한다** - 별칭은 남는다. 명세서에 별칭
            /// 이름이 있을 리 없어 무해하고, 지우면 이 목록에 기대는 다른 대조가
            /// 무엇을 보고 있었는지 알 수 없게 된다.
            ///
            /// [한정자를 보는 이유 - 2026-08-27 재리뷰 R1]
            /// 이름만 보면 같은 스코프의 **다른 원천에 있는 동명 실컬럼**에 별칭
            /// 해석이 붙는다. 실측: `Filtered AS (SELECT 1 FROM B1 INNER JOIN
            /// dbo.TReal AS R ON … WHERE R.Flag = 9)`에서 걸러지는 것은 `TReal.Flag`
            /// 인데 B1의 `Flag → USESTATE`가 실렸다. F1을 스코프 단위로 가둔 뒤에도
            /// **스코프 안**에 같은 병이 축소판으로 남아 있었다.
            ///
            /// 그래서 해석은 두 경우에만 한다.
            /// - **한정자가 없다.** 유효한 T-SQL에서 한정자 없는 이름이 통과했다면
            ///   그 스코프에서 그 이름을 가진 원천이 하나뿐이라는 뜻이다(둘이면
            ///   「모호한 컬럼 이름」으로 파싱이 아니라 실행이 거부된다). 따라서
            ///   그 하나가 별칭을 정의한 CTE다.
            /// - **한정자가 이 스코프에서 CTE에 결합돼 있다.** `FROM Base AS X`의
            ///   `X.Flag`가 그 경우다. 실테이블 별칭이면 해석하지 않는다.
            ///
            /// 후보 CTE 둘 이상이 같은 별칭을 다르게 정의하면 이 스코프에서 모호하므로
            /// 아무것도 내지 않는다.
            /// </summary>
            public IEnumerable<string> Resolve(
                IReadOnlyDictionary<string, string> bindings,
                IReadOnlyList<(string? Qualifier, string Name)> references)
            {
                if (_map.Count == 0 || bindings.Count == 0) yield break;

                foreach (var (qualifier, name) in references)
                {
                    IEnumerable<string> candidates;
                    if (string.IsNullOrWhiteSpace(qualifier))
                    {
                        candidates = bindings.Values;
                    }
                    else if (bindings.TryGetValue(qualifier!, out var cte))
                    {
                        candidates = new[] { cte };
                    }
                    else
                    {
                        continue;   // 실테이블 별칭 - 이 CTE들과 무관하다
                    }

                    string? resolved = null;
                    var ambiguous = false;

                    foreach (var candidate in candidates)
                    {
                        if (!_map.TryGetValue((candidate, name), out var source)) continue;
                        if (source == null) { ambiguous = true; break; }
                        if (resolved == null) { resolved = source; continue; }
                        if (!string.Equals(resolved, source, StringComparison.OrdinalIgnoreCase))
                        {
                            ambiguous = true;
                            break;
                        }
                    }

                    if (ambiguous || resolved == null) continue;
                    if (string.Equals(resolved, name, StringComparison.OrdinalIgnoreCase)) continue;

                    yield return resolved;
                }
            }
        }

        /// <summary>
        /// 이 층의 FROM이 이름으로 참조하는 원천과, 그것을 가리키는 결합 이름.
        /// 별칭이 있으면 별칭이(T-SQL은 별칭을 붙이면 원래 이름을 못 쓴다),
        /// 없으면 원천 이름 자신이 결합 이름이다.
        /// </summary>
        private sealed class NamedSourceFinder : TSqlFragmentVisitor
        {
            private readonly List<(string Binding, string Source)> _sources = new();
            public IReadOnlyList<(string Binding, string Source)> Sources => _sources;

            public override void Visit(NamedTableReference node)
            {
                var name = node.SchemaObject?.BaseIdentifier?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                var alias = node.Alias?.Value;
                _sources.Add((string.IsNullOrWhiteSpace(alias) ? name! : alias!, name!));
            }

            // 파생 테이블·스칼라 하위질의 안쪽은 이 층이 아니다. 그 층의 FROM은
            // 그 QuerySpecification을 따로 방문할 때 판정된다.
            public override void ExplicitVisit(QueryDerivedTable node) { }
            public override void ExplicitVisit(ScalarSubquery node) { }
        }

        /// <summary>
        /// 하위 스코프의 WHERE 컬럼만 모은다.
        ///
        /// [왜 QuerySpecification이 곧 하위 스코프인가] UPDATE·DELETE의 최상위
        /// WHERE는 QuerySpecification이 아니라 UpdateSpecification·
        /// DeleteSpecification에 달린다. 그래서 이 방문자가 만나는 모든
        /// QuerySpecification은 정의상 CTE 본문이거나 파생 테이블이거나
        /// 하위질의다 - "여기가 최상위인가"를 따로 판정할 필요가 없다.
        ///
        /// [ColumnCollector를 재사용하는 이유] 스코프마다 "그 스코프의 최상위
        /// WHERE만"이라는 같은 규칙이 적용된다. 더 안쪽 스코프는 이 방문자의
        /// 기본 순회가 각각 따로 방문해 모은다.
        /// </summary>
        private sealed class SubordinatePredicateCollector : TSqlFragmentVisitor
        {
            private readonly List<string> _columns = new();
            private readonly CteProjectionAliases _aliases;

            public SubordinatePredicateCollector(CteProjectionAliases aliases) => _aliases = aliases;

            public IReadOnlyList<string> Columns => _columns;

            /// <summary>
            /// 이 층의 WHERE와 **FROM(JOIN ON 포함)** 둘 다에서 모은다.
            ///
            /// [왜 FROM도 보는가 - 2026-08-26 코퍼스 판정 15건]
            /// 최상위 대조는 `PredicateColumns ∪ JoinColumns`다 - 명세서 DML 범위 표의
            /// 술어 칸이 "조인 결합 포함"이기 때문이다(MechanicalValidator의
            /// predicatePresent 참고). 그런데 이 수집기가 WHERE만 보면 하위 범위만
            /// 조인을 잃어 **비대칭**이 생긴다. 실물: CTE `ClientRateSource` 안의
            /// `ON A.CLIENTID = B.CLIENTID`는 이전으로 인정되지 않는데 명세서는 CLIENTID를
            /// 술어 칸에 적으므로, 검사 B가 "명세서가 확정한 컬럼이 없어졌다"고 오인한다.
            /// INSERT 재편입 증가분 37건 중 15건이 이 축 하나였다
            /// (docs/known-defects.md (5-3-3) 부류 1).
            ///
            /// ColumnCollector가 QueryDerivedTable·ScalarSubquery 하강을 막으므로 여기서
            /// 모으는 것은 **이 층의** FROM 절뿐이다. 더 깊은 층은 이 방문자가 그
            /// QuerySpecification을 따로 만날 때 잡으므로 중복도 누락도 없다
            /// (SubordinateJoinOn_DoesNotReachIntoDeeperDerivedTable_AtThisLevel이 잠근다).
            ///
            /// [한계 둘 - 이 수집은 「거를 수 있는 술어」보다 넓다]
            /// (1) **조인 종류를 가리지 않는다.** 위 Add()의 주석은 하위 수집을 정당화하며
            /// "JOIN ON 하위질의는 INNER JOIN이면 대상 행을 실제로 거르므로"라고 조건을
            /// 못 박는데, 여기서는 행을 거르지 않는 LEFT/RIGHT/FULL JOIN의 ON까지 이전으로
            /// 인정한다. 원본이 최상위 WHERE에 두었던 진짜 필터가 사라졌는데 마침 같은
            /// 이름이 어떤 외부 조인의 ON에 있으면 검사가 조용해진다. 최상위 JoinColumns
            /// 수집도 같은 성질을 이미 갖고 있어 **대칭**이지만, 좁은 쪽으로 맞춘 것이
            /// 아니라 넓은 쪽으로 맞춘 것이다. 2026-08-26 실측: QualifiedJoinType이
            /// Inner인 ON만 모으는 변형으로 코퍼스를 전수 재측정해도 발화가 62건으로
            /// 동일했다 - 오늘 코퍼스에서는 이 차이가 한 건도 드러나지 않는 **잠복**이다.
            /// (2) FROM 절에는 ON 말고도 컬럼 참조가 올 수 있다(CROSS APPLY 함수 인자,
            /// PIVOT 컬럼 목록 등). 그것들도 함께 이전으로 인정된다. 이 코퍼스에 그런
            /// 구문이 있는지는 재지 않았다.
            /// </summary>
            public override void Visit(QuerySpecification node)
            {
                if (node.WhereClause == null && node.FromClause == null) return;

                var inner = new ColumnCollector();
                node.WhereClause?.Accept(inner);
                node.FromClause?.Accept(inner);
                _columns.AddRange(inner.Columns);

                // 이 스코프가 읽는 CTE가 개명해 투영한 이름이면 원천 컬럼 이름을
                // 함께 싣는다. 읽지 않는 CTE의 별칭은 붙지 않는다 - 그것이 리뷰
                // F1이 잡은 거짓 음성 경로였다.
                _columns.AddRange(
                    _aliases.Resolve(_aliases.CteBindingsIn(node.FromClause), inner.References));
            }
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
