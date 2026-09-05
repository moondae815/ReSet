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

        // [왜 약칭 표식도 신호인가 - 실측 2026-09-05] 감사의 🔴(ConsistencyReport.md:138 ·
        // S07.md:143-152)는 DML 낱말도 절 키워드도 전혀 없다 - `U4:`·`U7~U11:` 같은 갱신
        // 번호 약칭과 한글 산문뿐이다. DmlVerbRegex && DmlClauseRegex 만 요구하면 이 자리를
        // 통째로 건너뛴다. `U<n>`·`U<n>~U<m>`·`UPDATE <n>`·`갱신 <n>` 을 후보로 잡는다.
        private static readonly Regex UpdateLabelRegex = new(
            @"\b(?:UPDATE\s+\d+|U\d+(?:\s*[~\-]\s*U?\d+)?\s*:|갱신\s*\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "SET @v_currentStepId = N;"은 원본이 각 실행 DML 직전에 남기는 오류 추적
        // 표식이자 <b>새 단계의 경계</b>다(S08.md 서두: "실행 DML 직전에는 해당 원본
        // 코드로 @v_currentStepId를 설정한다"). 이 표식 뒤에 오는 것은 이전 주석이
        // 아니라 다음 단계에 속하므로, 꼬리 검사가 이것을 만나면 앵커를 못 찾은 것이다.
        private static readonly Regex StepIdMarkerRegex = new(
            @"^SET\s+@v_currentStepId\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 꼬리 검사가 "DML 문이 시작한다"고 인정할 때만 쓰는 좁은 판별자 - 줄 전체가 아니라
        // 줄의 <b>맨 앞</b>에서 시작해야 한다(문장 어딘가에 낱말이 있는 것과 다르다).
        private static readonly Regex LeadingDmlStatementRegex = new(
            @"^(UPDATE|INSERT|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                var describesDml = (DmlVerbRegex.IsMatch(body) && DmlClauseRegex.IsMatch(body))
                    || UpdateLabelRegex.IsMatch(body);
                if (!describesDml)
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

        /// <summary>
        /// 주석 바로 뒤에 <b>이 주석 자신의</b> DML 문장이 서 있는가.
        ///
        /// [연쇄 억제 버그 - 실측 2026-09-05] 종전에는 이 줄이 "SET @"로 시작하지 않고
        /// 비어 있지도 않으면 그 줄 <i>전체</i>를 DmlVerbRegex 로 검사했다. 뒤따르는
        /// 다음 블록 주석의 첫 줄(`/* UPDATE 2: ... */`)이 그 검사를 통과해 "UPDATE"
        /// 낱말을 포함한다는 이유로 「진짜 문장이 뒤따른다」고 오판했다 - 앞 주석이
        /// 죽고 S08 에서 연쇄의 마지막 하나만 살아남았다(UPDATE 1·2·8·9·10·13·14).
        ///
        /// 그래서 두 가지를 <b>줄의 맨 앞</b>에서 먼저 검사해 "멈춤" 신호로 삼는다.
        /// 다른 블록 주석이 시작하면 그것은 또 다른 자리표시자이지 이 주석의 앵커가
        /// 아니다. <see cref="StepIdMarkerRegex"/>가 시작하면 새 단계의 경계이므로
        /// 그 뒤는 다른 단계에 속한다. 둘 다 아니고 줄이 DML 동사로 시작해야만
        /// 앵커로 인정한다(줄 안 어딘가가 아니라 <b>맨 앞</b> - <see cref="LeadingDmlStatementRegex"/>).
        /// 그 외의 줄(`DECLARE` 같은 부수 설정문)은 같은 단계에 속할 수 있으므로 건너뛴다.
        /// </summary>
        private static bool StartsWithDmlStatement(string tail)
        {
            foreach (var line in MarkdownSectionLocator.SplitLines(tail))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("--", StringComparison.Ordinal)) continue;

                if (s.StartsWith("/*", StringComparison.Ordinal)) return false;
                if (StepIdMarkerRegex.IsMatch(s)) return false;
                if (LeadingDmlStatementRegex.IsMatch(s)) return true;

                // DECLARE 등 부수 설정문 - 같은 단계에 속할 수 있으므로 계속 건너뛴다.
            }

            return false;
        }
    }
}
