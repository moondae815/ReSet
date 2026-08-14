using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코드 자리에 대신 서 있는 주석을 찾는다.
    ///
    /// 지시서 규칙 7은 에이전트에게 `// TODO` 같은 자리표시자를 금지하는데, 실측
    /// 계획서 자신이 그 형태를 시범 보였다 - `-- 나머지 실제 컬럼도 ... 모두 기술`.
    /// 에이전트는 계획서를 본보기로 삼으므로 그대로 복사한다.
    ///
    /// 차단이 아니라 배너인 이유: 같은 자리에 `-- 원본 필터 ...를 모두 유지한다`처럼
    /// 생략이 아니라 지시인 주석도 있다. 기계가 둘을 완벽히 가르지 못하고, 재생성을
    /// 걸면 모델이 표현만 바꿔 우회하며 재시도만 소모한다.
    ///
    /// 패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다.
    /// </summary>
    public static class OmissionCommentScanner
    {
        private const int MaxReported = 20;

        private static readonly Regex CommentLineRegex = new(
            @"^\s*(?:--|//)\s*(?<body>.+)$",
            RegexOptions.Compiled);

        private static readonly Regex[] OmissionPatterns =
        {
            new(@"나머지.*?(기술|적용|같은)", RegexOptions.Compiled),
            new(@"모두\s*기술", RegexOptions.Compiled),
            new(@"위\s.*동일", RegexOptions.Compiled),
        };

        // "유지하라/보존하라"는 생략 지시가 아니라 보존 지시다. 원본 로직을 지키라는
        // 요구를 결함으로 들면 배너의 변별력이 사라진다.
        private static readonly string[] PreservationMarkers = { "유지한다", "보존한다", "유지하십시오", "보존하십시오" };

        public static IReadOnlyList<string> Scan(string? planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return Array.Empty<string>();
            }

            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var insideFence = false;

            foreach (var line in MarkdownSectionLocator.SplitLines(planMarkdown))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    insideFence = !insideFence;
                    continue;
                }

                if (!insideFence)
                {
                    continue;
                }

                var comment = CommentLineRegex.Match(line);
                if (!comment.Success)
                {
                    continue;
                }

                var body = comment.Groups["body"].Value;

                if (PreservationMarkers.Any(marker => body.Contains(marker, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!OmissionPatterns.Any(pattern => pattern.IsMatch(body)))
                {
                    continue;
                }

                var trimmed = line.Trim();
                if (seen.Add(trimmed) && hits.Count < MaxReported)
                {
                    hits.Add(trimmed);
                }
            }

            return hits;
        }
    }
}
