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
            // \b로 단어 경계를 요구한다 - 경계가 없으면 "범위"(scope)의 "위"에도 걸려
            // "동일"과 잘못 짝지어진다(실측: BatchMigrationPlan.md의
            // "범위 삭제 후 복원 - 동일 YMD만"). "위"는 "위(above)"를 가리킬 때만 유효하다.
            new(@"\b위\s.*동일", RegexOptions.Compiled),
        };

        // [왜 블록 주석을 따로 보는가] 감사가 🔴로 매긴 자리의 실제 모양은 `--`가 아니라
        // `/* UPDATE 13: ... */`였다(S08.md:155-159). 줄 단위 정규식은 그것을 못 본다.
        private static readonly Regex BlockCommentRegex = new(
            @"/\*(?<body>.*?)\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 생략으로 판정할 블록의 조건: DML 동사와 절 키워드를 함께 담아 "여기에 문장이
        // 있어야 한다"고 스스로 말하는 주석.
        private static readonly Regex DmlVerbRegex = new(
            @"\b(UPDATE|INSERT|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DmlClauseRegex = new(
            @"\b(WHERE|SET|VALUES|SELECT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyList<string> Scan(string? planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return Array.Empty<string>();
            }

            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var insideFence = false;
            var fenced = new System.Text.StringBuilder();

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

                fenced.AppendLine(line);

                var comment = CommentLineRegex.Match(line);
                if (!comment.Success)
                {
                    continue;
                }

                var body = comment.Groups["body"].Value;

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

            ScanBlockComments(fenced.ToString(), hits, seen);

            return hits;
        }

        /// <summary>
        /// 펜스 안 본문에서 「구현 대신 선 블록 주석」을 찾는다.
        ///
        /// [판별자가 문구가 아니라 구조인 이유] 종전에는 "유지한다"·"보존한다"가 든
        /// 주석을 화이트리스트로 면제했다. 그러나 감사가 코퍼스 최악의 결함으로 매긴
        /// 자리(S07 - 갱신 18개 중 10개 소실)가 정확히 "원본대로 유지한다"였다. 문구는
        /// 생략인지 보존인지를 가르지 못한다. 가르는 것은 <b>그 주석이 선 자리에 실행
        /// 가능한 DML 이 있는가</b>이다.
        /// </summary>
        private static void ScanBlockComments(string fencedBody, List<string> hits, HashSet<string> seen)
        {
            foreach (Match match in BlockCommentRegex.Matches(fencedBody))
            {
                var body = match.Groups["body"].Value;
                if (!DmlVerbRegex.IsMatch(body) || !DmlClauseRegex.IsMatch(body))
                {
                    continue;
                }

                // 주석 뒤에 실제 DML 이 서 있으면 앵커 주석이다 - 생략이 아니다.
                var tail = fencedBody.Substring(match.Index + match.Length);
                if (StartsWithDmlStatement(tail))
                {
                    continue;
                }

                var label = Regex.Replace(body.Trim(), @"\s+", " ");
                if (label.Length > 70) label = label.Substring(0, 70);
                if (seen.Add(label) && hits.Count < MaxReported)
                {
                    hits.Add(label);
                }
            }
        }

        /// <summary>주석 바로 뒤(주석·공백만 건너뛰고)에 DML 문장이 시작하는가.</summary>
        private static bool StartsWithDmlStatement(string tail)
        {
            foreach (var line in MarkdownSectionLocator.SplitLines(tail))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("--", StringComparison.Ordinal)) continue;
                // SET @v_... 대입은 문장이 아니라 오류 추적 표식이므로 건너뛴다.
                if (s.StartsWith("SET @", StringComparison.OrdinalIgnoreCase)) continue;
                return DmlVerbRegex.IsMatch(s);
            }

            return false;
        }
    }
}
