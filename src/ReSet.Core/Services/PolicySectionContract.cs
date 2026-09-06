using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>정책서 근거 칸을 셋으로 가른 것 - 어느 명세서의, 어느 헤딩의, 어느 구절인가.</summary>
    public sealed record PolicyEvidenceReference(string Label, string Heading, string Quote);

    /// <summary>
    /// 정책서 규칙 표의 계약.
    ///
    /// [왜 계약을 클래스가 소유하는가] 생성 프롬프트와 검증기가 같은 표를 읽어야 둘이
    /// 갈라지지 않는다. PrdSectionContract가 이미 같은 자리를 지키고 있고, 거기 달린
    /// 주석이 하드코딩하면 조용히 갈라진다고 경고한다.
    ///
    /// [PRD와 다른 점] 정책서는 근거가 여러 명세서에 흩어져 있어 근거 칸이
    /// `dbo.UP_X · ## 헤딩 > "구절"` 로 SP 식별자를 앞에 단다.
    /// </summary>
    public static class PolicySectionContract
    {
        public const string TableHeader = "| ID | 업무 규칙 | 근거 | 코드값 |";
        public const string TableSeparator = "| :--- | :--- | :--- | :--- |";
        public const int ExpectedCellCount = 4;

        /// <summary>근거 칸에서 SP 식별자와 나머지를 가르는 구분자.</summary>
        public const string LabelSeparator = " · ";

        /// <summary>코드값이 없을 때 쓰는 표기. 빈 칸이 아니라 이 값이어야 「빠뜨림」과 「해당 없음」이 갈린다.</summary>
        public const string NoCodeValue = "-";

        public static string IdPrefixFor(int stageNumber) =>
            "S" + stageNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>`## 1. 제목` 에서 1을 뽑는다. 번호가 없으면 0.</summary>
        public static int StageNumberOf(string heading)
        {
            var match = Regex.Match(heading.TrimStart('#').Trim(), @"^(\d+)[.\)\s-]");
            return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>`dbo.UP_X · ## 헤딩 > "구절"` 을 가른다.</summary>
        public static bool TryParseEvidence(string? raw, out PolicyEvidenceReference reference)
        {
            reference = new PolicyEvidenceReference(string.Empty, string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var separator = raw.IndexOf(LabelSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var label = raw[..separator].Trim();
            var rest = raw[(separator + LabelSeparator.Length)..].Trim();

            var arrow = rest.IndexOf('>');
            if (arrow < 0)
            {
                return false;
            }

            var heading = rest[..arrow].Trim();
            var quoted = rest[(arrow + 1)..].Trim();

            var first = quoted.IndexOfAny(new[] { '"', '“', '”' });
            var last = quoted.LastIndexOfAny(new[] { '"', '“', '”' });
            if (first < 0 || last <= first)
            {
                return false;
            }

            var quote = quoted[(first + 1)..last].Trim();
            if (label.Length == 0 || heading.Length == 0 || quote.Length == 0)
            {
                return false;
            }

            reference = new PolicyEvidenceReference(label, heading, quote);
            return true;
        }
    }
}
