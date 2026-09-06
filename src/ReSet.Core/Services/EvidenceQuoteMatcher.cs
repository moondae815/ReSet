using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 「인용문이 원본의 그 헤딩 절 안에 축자로 있는가」를 재는 공통 로직.
    ///
    /// [왜 뽑아냈는가] PRD와 정책서가 같은 질문을 한다. 다른 것은 원본이 하나냐
    /// 여럿이냐뿐이다. 두 벌로 두면 한쪽의 정규화 규칙만 고쳐져 같은 인용이 한 문서에서는
    /// 통과하고 다른 문서에서는 결함이 되는 날이 온다.
    ///
    /// 이 클래스는 PrdAttributionValidator의 private 메서드를 그대로 옮긴 것이며
    /// 동작을 바꾸지 않는다. PRD 쪽 회귀는 기존 PRD 테스트가 지킨다.
    /// </summary>
    public static class EvidenceQuoteMatcher
    {
        /// <summary>인용 대조용 정규화. 공백과 마크다운 강조·표 파이프를 걷어낸다.</summary>
        public static string NormalizeForQuoteMatch(string text) =>
            string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)
                                           && ch != '*' && ch != '`' && ch != '|'
                                           && ch != '_' && ch != '~'));

        /// <summary>마크다운 헤딩에서 부호를 정규화한다. 앞의 #, 공백, 접두 숫자·번호, 뒤의 구두점을 제거한다.</summary>
        public static string NormalizeHeading(string heading)
        {
            var text = heading.TrimStart('#').Trim();

            var match = Regex.Match(text, @"^\d+[.\)\s-]+(.*)$");
            if (match.Success)
            {
                text = match.Groups[1].Value.Trim();
            }

            return text.TrimEnd(':').Trim();
        }

        /// <summary>지정 헤딩 아래 본문만 이어 붙인다. 헤딩이 없으면 null. 정확 일치 뒤 부분 일치로 폴백한다.</summary>
        public static string? ExtractSectionBody(IReadOnlyList<string> sourceLines, string heading)
        {
            var exact = MarkdownSectionLocator.LocateSection(sourceLines, heading, "## ");
            var (headerIndex, endIndex) = exact.HeaderIndex >= 0
                ? exact
                : MarkdownSectionLocator.LocateSection(
                    sourceLines, "## " + NormalizeHeading(heading), "## ", exact: false);

            if (headerIndex < 0)
            {
                return null;
            }

            return string.Join("\n", sourceLines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1));
        }

        /// <summary>절 본문 안에 인용이 축자로 있는가(정규화 후 부분 문자열 대조).</summary>
        public static bool QuoteExistsIn(string sectionBody, string quote) =>
            NormalizeForQuoteMatch(sectionBody)
                .Contains(NormalizeForQuoteMatch(quote), StringComparison.Ordinal);
    }
}
