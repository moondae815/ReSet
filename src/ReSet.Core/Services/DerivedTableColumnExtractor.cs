using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Alias">파생 테이블의 별칭(예: "X").</param>
    /// <param name="Column">파생 테이블이 노출하는 컬럼 이름.</param>
    /// <param name="Expression">그 컬럼의 정의 표현식 원문.</param>
    /// <param name="Anchors">표현식 안의 식별자. 명세서 본문에서 그대로 찾는다.</param>
    public sealed record DerivedColumnDefinition(
        string Alias, string Column, string Expression, IReadOnlyList<string> Anchors);

    /// <summary>
    /// UPDATE/INSERT/DELETE의 FROM 절(또는 INSERT의 소스 SELECT)에 있는 파생 테이블의
    /// 컬럼 정의를 뽑는다.
    ///
    /// SET 우변이 X.PGCOMM에서 멈추면 명세서도 거기서 멈춘다. X 안의
    /// IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)가 프로모션
    /// 건의 원가 기준금액인데, 그 사실이 통째로 소실된 것이 이번 감사의
    /// 유일한 축 A 🔴이다.
    ///
    /// 표현식 안의 식별자가 그대로 앵커이므로 여기는 앵커 방식이 성립한다.
    ///
    /// [수집 범위] TSqlFragmentVisitor는 자식으로 계속 내려가므로 QueryDerivedTable을
    /// 방문하는 이 방문자는 UPDATE...FROM, DELETE...FROM, INSERT...SELECT 어디에
    /// 나타나든, 그리고 파생 테이블이 다른 파생 테이블 안에 중첩되어도 모두
    /// 매칭된다 - ScriptDom 자체가 트리를 순회하며 자식 QueryDerivedTable마다
    /// Visit을 다시 호출하기 때문이다. 실측 코퍼스(24개 SP·Function)에 둘 다
    /// 실물로 있다: UF_GET_COLLECTYMD/UIF_SettleYMD는 파생 테이블 Z가 파생
    /// 테이블 A를 감싸는 2단 중첩(Z.YMD FROM (SELECT A.YMD FROM (SELECT ...) A ...) Z)이고,
    /// UP_UTIL_SETTLE_INS_EXTRA(4PLCARD)는 UPDATE가 아니라 INSERT INTO ...
    /// SELECT X.컬럼... FROM (SELECT ...) X 형태다 - 둘 다 별도 코드 없이
    /// 그대로 잡힌다.
    /// </summary>
    public static class DerivedTableColumnExtractor
    {
        public const string DerivedTableHeading = "### 파생 테이블 정의 (기계 확정 — 수정 금지)";

        private static readonly Regex IdentifierRegex =
            new(@"\b[A-Za-z][A-Za-z0-9_]{2,}\b", RegexOptions.Compiled);

        /// <summary>
        /// SQL 작은따옴표 문자열 리터럴('' 이스케이프 포함). 앵커를 뽑기 전에 이
        /// 구간을 지운다 - 실측(UP_UTIL_SETTLE_INS_EXTRA4PLCARD)에서 `A.PGName =
        /// 'dacomcard'`의 리터럴 값 'dacomcard'가 식별자처럼 보여 앵커로 잘못
        /// 뽑혔다. 리터럴은 값이지 식별자가 아니고, 명세서가 같은 값을 다른
        /// 표기로 서술할 수 있으므로 앵커가 될 수 없다.
        /// </summary>
        private static readonly Regex StringLiteralRegex =
            new(@"'(?:[^']|'')*'", RegexOptions.Compiled);

        /// <summary>
        /// SQL 키워드·내장 함수·자료형은 앵커가 아니다 - 한국어 명세서가 그대로
        /// 옮겨 적을 수 없는 토큰이면 L1 요구 조건이 되어서는 안 된다(앵커 규율).
        /// 실측 코퍼스 24건 전체를 이 추출기로 훑어 나온 앵커를 검토해 확정한
        /// 목록이다 - 최초 초안(SELECT/FROM/... 등 SQL 키워드)에
        /// STRING_SPLIT·OVER·PARTITION·ROW_NUMBER·COALESCE·NULLIF·EXISTS·DISTINCT·
        /// TOP·LEFT·RIGHT·INNER·OUTER·JOIN 계열과 GETDATE·DATEADD·DATEDIFF 같은
        /// 날짜 내장 함수, INT·BIGINT·DECIMAL 같은 자료형을 더했다.
        /// </summary>
        private static readonly HashSet<string> NonAnchors = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "JOIN", "ON", "AND", "OR", "NOT", "NULL", "CASE",
            "WHEN", "THEN", "ELSE", "END", "AS", "IIF", "ISNULL", "CAST", "CONVERT",
            "SUM", "MIN", "MAX", "COUNT", "AVG", "ROUND", "INT", "VARCHAR", "MONEY", "dbo",
            "INNER", "OUTER", "LEFT", "RIGHT", "FULL", "CROSS", "APPLY", "DISTINCT", "TOP",
            "EXISTS", "COALESCE", "NULLIF", "OVER", "PARTITION", "BY", "ORDER", "GROUP",
            "HAVING", "UNION", "ALL", "IN", "BETWEEN", "LIKE", "IS", "STRING_SPLIT", "VALUE",
            "ROW_NUMBER", "GETDATE", "DATEADD", "DATEDIFF", "BIGINT", "DECIMAL", "NUMERIC",
            "BIT", "DATETIME", "DATE", "CHAR", "NVARCHAR", "NCHAR", "FLOAT", "REAL"
        };

        public static IReadOnlyList<DerivedColumnDefinition> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DerivedColumnDefinition>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<DerivedColumnDefinition>();

                var visitor = new DerivedTableVisitor();
                fragment.Accept(visitor);
                return visitor.Definitions;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DerivedTableColumnExtractor] 파생 테이블 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<DerivedColumnDefinition>();
            }
        }

        private sealed class DerivedTableVisitor : TSqlFragmentVisitor
        {
            public List<DerivedColumnDefinition> Definitions { get; } = new();

            private readonly HashSet<(string Alias, string Column, string Expression)> _seen = new();

            public override void Visit(QueryDerivedTable node)
            {
                var alias = node.Alias?.Value;
                if (string.IsNullOrWhiteSpace(alias)) return;

                foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                {
                    foreach (var element in spec.SelectElements.OfType<SelectScalarExpression>())
                    {
                        var column = element.ColumnName?.Value
                            ?? (element.Expression as ColumnReferenceExpression)
                                ?.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                        if (string.IsNullOrWhiteSpace(column)) continue;

                        var expression = TextOf(element.Expression);
                        if (!_seen.Add((alias!, column!, expression))) continue;

                        Definitions.Add(new DerivedColumnDefinition(
                            alias!, column!, expression, BuildAnchors(expression)));
                    }
                }
            }

            /// <summary>
            /// 파생 테이블의 본문에서 QuerySpecification을 전부 끌어낸다.
            ///
            /// [왜 캐스트 하나로는 안 되는가] UNION/UNION ALL/EXCEPT/INTERSECT로 묶인
            /// 파생 테이블은 QueryExpression이 <b>BinaryQueryExpression</b>이라
            /// QuerySpecification 캐스트가 실패한다. 예전 코드는 그 자리에서 그냥
            /// 반환해 그런 파생 테이블을 통째로 놓쳤다 - EXCEPTION_PROC 문장 17
            /// (PointPay/Payco, object_definition.sql:469-508)의 X가 실측 사례다.
            /// BB.PGVT가 X.PGETC·X.PGIncVTax·X.PGETC4SUM을 참조하는데 그 정의가 표에서
            /// 빠져, 2026-08-18 축 A 감사에서 🔴로 잡혔다. 이 추출기를 만들게 한
            /// 결함(문장 13의 X 정의 누락)과 같은 종류인데 UNION 변형만 남아 있었다.
            ///
            /// [갈래를 합치지 않는 이유] 갈래마다 식이 다를 수 있다 - 위 X는 PointPay
            /// 갈래와 Payco 갈래의 PGCOMM4SUM 산식이 서로 다르다. 하나로 접으면 그
            /// 차이가 사라지므로 전부 수집하고, 완전히 같은 (별칭·컬럼·식)만 접는다.
            /// </summary>
            private static IEnumerable<QuerySpecification> QuerySpecificationsOf(QueryExpression? query)
            {
                switch (query)
                {
                    case QuerySpecification spec:
                        yield return spec;
                        break;
                    case BinaryQueryExpression binary:
                        foreach (var s in QuerySpecificationsOf(binary.FirstQueryExpression)) yield return s;
                        foreach (var s in QuerySpecificationsOf(binary.SecondQueryExpression)) yield return s;
                        break;
                    case QueryParenthesisExpression paren:
                        foreach (var s in QuerySpecificationsOf(paren.QueryExpression)) yield return s;
                        break;
                }
            }

            private static string TextOf(TSqlFragment? fragment)
            {
                if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

                var text = string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text));

                return Regex.Replace(text, @"\s+", " ").Trim();
            }

            private static IReadOnlyList<string> BuildAnchors(string expression)
            {
                // 문자열 리터럴 구간을 먼저 지워, 그 안의 값이 식별자로 오인되지
                // 않게 한다. 표시용 expression 원문은 그대로 두고 앵커 추출에만
                // 이 정화된 텍스트를 쓴다.
                var sanitized = StringLiteralRegex.Replace(expression, " ");

                var anchors = new List<string>();
                foreach (Match match in IdentifierRegex.Matches(sanitized))
                {
                    if (NonAnchors.Contains(match.Value)) continue;
                    if (anchors.Contains(match.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    anchors.Add(match.Value);
                }

                return anchors;
            }
        }
    }
}
