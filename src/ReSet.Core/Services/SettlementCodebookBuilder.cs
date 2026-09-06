using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코드값 사전을 만든다. 좌변(코드 상수)은 오프라인, 우변(업무 의미)은 DB가 있을 때만.
    ///
    /// [왜 채택을 명세서가 정하는가] 추출은 DDL로 해야 정확하지만(동적 SQL 조각을 걸러야
    /// 하므로 ScriptDom), 채택은 Spec.md가 정해야 「근거는 명세서뿐」이라는 계약이 선다.
    /// 둘을 나눠 두면 정확도와 계약을 함께 가진다. 실측 채택률은 (SP, 상수) 출현 기준
    /// 114/115다.
    /// </summary>
    public static class SettlementCodebookBuilder
    {
        /// <summary>
        /// 값만으로 매칭할 때 요구하는 최소 길이.
        ///
        /// 'Y'·'N'·'1' 같은 값은 아무 테이블에서나 걸려 매칭이 잡음이 된다(실질 상수
        /// 64개 중 13개가 길이 2 이하, 2026-09-06 실측). 이런 플래그의 의미는 코드
        /// 테이블이 아니라 컬럼 이름과 명세서 서술에 있고, 그건 AI가 Spec 인용으로
        /// 이미 다룬다.
        /// </summary>
        public const int MinimumMatchableLength = 3;

        public static SettlementCodebook BuildLeftSide(IReadOnlyList<PolicySource> sources)
        {
            // 값 → (컬럼 후보, 그 값을 쓰는 SP들)
            var adopted = new Dictionary<string, (string? Column, List<string> Procedures)>(StringComparer.Ordinal);
            var unlisted = new List<string>();

            foreach (var source in sources)
            {
                foreach (var pair in ConstantComparisonExtractor.Extract(source.DdlText))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    if (!IsAdoptable(source.SpecMarkdown, pair.Value))
                    {
                        if (!unlisted.Contains(pair.Value, StringComparer.Ordinal))
                        {
                            unlisted.Add(pair.Value);
                        }

                        continue;
                    }

                    if (!adopted.TryGetValue(pair.Value, out var existing))
                    {
                        adopted[pair.Value] = (pair.Column, new List<string> { source.Label });
                        continue;
                    }

                    // 컬럼은 먼저 잡힌 것을 지킨다. 같은 값이 여러 컬럼과 비교되면
                    // 어느 하나를 고를 근거가 없고, 매칭은 컬럼이 없어도 2단으로 돈다.
                    if (!existing.Procedures.Contains(source.Label, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Procedures.Add(source.Label);
                    }

                    adopted[pair.Value] = (existing.Column ?? pair.Column, existing.Procedures);
                }
            }

            var entries = adopted
                .Select(kv => new CodebookEntry(
                    kv.Key,
                    kv.Value.Column,
                    kv.Value.Procedures,
                    kv.Key.Length >= MinimumMatchableLength,
                    Array.Empty<CodebookMatch>()))
                .OrderBy(e => e.Value, StringComparer.Ordinal)
                .ToList();

            return new SettlementCodebook(entries, unlisted);
        }

        /// <summary>
        /// 값이 명세서에 채택할 만한 형태로 나타나는지 판정한다.
        ///
        /// [왜 부분 문자열만으로는 안 되는가] 순수 Contains는 'SUM'이 'SUMMARY' 안에서
        /// 조용히 채택되게 만든다 - 값은 없는데 형태만 우연히 겹친 「조용한 거짓
        /// 채택」이다. 그래서 값이 따옴표(홑·겹·백틱)로 감싸여 있거나, 양쪽 이웃이
        /// 영숫자·밑줄이 아닌 경계 위치에 있을 때만 채택한다. 명세서가 따옴표 없이
        /// 서술할 수 있으므로(예: 「결제수단이 impaymobile인 건」) 경계 조건도
        /// 필요하다. 이 판정에서 걸러진 값은 사라지지 않고 SpecUnlistedConstants에
        /// 남아 보인다 - 조용한 거짓 채택보다 보이는 누락이 낫다.
        /// </summary>
        private static bool IsAdoptable(string specMarkdown, string value)
        {
            if (specMarkdown.Contains($"'{value}'", StringComparison.Ordinal) ||
                specMarkdown.Contains($"\"{value}\"", StringComparison.Ordinal) ||
                specMarkdown.Contains($"`{value}`", StringComparison.Ordinal))
            {
                return true;
            }

            var index = 0;
            while ((index = specMarkdown.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = index == 0 || !IsAsciiWordChar(specMarkdown[index - 1]);
                var afterIndex = index + value.Length;
                var afterOk = afterIndex >= specMarkdown.Length || !IsAsciiWordChar(specMarkdown[afterIndex]);

                if (beforeOk && afterOk)
                {
                    return true;
                }

                index++;
            }

            return false;
        }

        /// <summary>
        /// ASCII 영숫자·밑줄만 「단어 문자」로 본다. .NET의 \b·char.IsLetterOrDigit는
        /// 한글도 문자로 쳐서 「impaymobile인」의 「인」을 경계로 인정하지 않는다 -
        /// 그러면 따옴표 없는 한국어 서술의 경계 매칭이 전부 깨진다.
        /// </summary>
        private static bool IsAsciiWordChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        /// <summary>
        /// 좌변만 있는 사전에 프로파일링 결과를 붙인다. 2단이다.
        ///
        /// 1단 - 컬럼을 아는 값은 같은 이름의 컬럼에서만 찾는다(정밀).
        /// 2단 - 컬럼을 모르는 값은 아무 문자열 칸에서나 찾되, 길이 조건을 통과한
        ///        값만 시도한다(MatchEligible).
        ///
        /// 실측으로 컬럼까지 잡히는 비율은 값 29개 / 82개다. 즉 2단이 다수 경로이고,
        /// 길이 조건이 실질적인 잡음 차단선이다.
        ///
        /// 부분 문자열은 매칭이 아니다 - 'payco'가 'payco_extra'에 걸리면 사전이
        /// 거짓 번역을 문서에 허가하게 된다.
        /// </summary>
        public static SettlementCodebook ApplyMatches(
            SettlementCodebook leftSide,
            IReadOnlyList<ProfiledTable> tables)
        {
            var entries = leftSide.Entries.Select(entry =>
            {
                if (!entry.MatchEligible)
                {
                    return entry;
                }

                var matches = new List<CodebookMatch>();

                foreach (var table in tables)
                {
                    foreach (var row in table.Rows)
                    {
                        var hit = entry.Column is null
                            ? row.Values.Any(v => Equals(v, entry.Value))
                            : row.TryGetValue(entry.Column, out var cell) && Equals(cell, entry.Value);

                        if (hit)
                        {
                            matches.Add(new CodebookMatch(table.Table, row));
                            break; // 한 테이블에서 첫 행이면 충분하다 - 코드 테이블은 값이 유일하다.
                        }
                    }
                }

                return entry with { Matches = matches };
            }).ToList();

            return leftSide with { Entries = entries };
        }

        private static bool Equals(string? cell, string value) =>
            cell is not null && string.Equals(cell, value, StringComparison.OrdinalIgnoreCase);
    }
}
