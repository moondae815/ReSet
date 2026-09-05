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

        // "SET @v_currentStepId = N;"은 원본이 <b>실행 DML 직전에</b> 남기는 오류 추적
        // 관용구다(S08.md 서두: "실행 DML 직전에는 해당 원본 코드로 @v_currentStepId를
        // 설정한다") - 표식 하나당 DML 하나가 원칙이다. 이 표식을 무조건 멈춤으로
        // 삼으면(라운드 1) 정상 완료 자리(`/* U1: ... */ SET ...=-101; UPDATE A
        // SET ... WHERE ...`)를 생략으로 오판하고, 무조건 건너뛰기만 하면(라운드 2)
        // 이미 자기 표식을 단 주석 뒤에 또 다른 단계의 표식+DML 이 와도 앵커로
        // 오인한다(S07.md:240-244, U15). 그래서 판정은 <see cref="PrecededByStepIdMarker"/>
        // 로 나뉜다 - <see cref="StartsWithDmlStatement"/> 참고.
        private static readonly Regex StepIdMarkerRegex = new(
            @"^SET\s+@v_currentStepId\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 꼬리 검사가 "문장이 시작한다"고 인정할 때만 쓰는 좁은 판별자 - 줄 전체가
        // 아니라 줄의 <b>맨 앞</b>에서 시작해야 한다(문장 어딘가에 낱말이 있는 것과
        // 다르다). [오탐 - 실측 2026-09-05 라운드 4] UPDATE/INSERT/DELETE 만
        // 인정하면 SELECT·WITH(CTE)·MERGE·EXEC 로 시작하는 진짜 문장은 앵커를
        // 영영 못 찾는다 - 규칙 준수를 설명하는 산문 주석(POQSettleProc19의
        // "C#은 아래 SELECT 결과를 ... 적재한 뒤 ... UPDATE 또는 INSERT를
        // 발행하지 않는다")이 우연히 DmlVerbRegex·DmlClauseRegex(SELECT)를 만족시켜
        // 게이트를 통과한 뒤, 뒤따르는 진짜 SELECT 문을 앵커로 인정받지 못해
        // 생략으로 오판됐다. `;WITH`처럼 세미콜론이 CTE 앞에 붙는 관용구도 받는다.
        private static readonly Regex LeadingDmlStatementRegex = new(
            @"^;?(UPDATE|INSERT|DELETE|SELECT|WITH|MERGE|EXEC)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                var head = fencedBody.Substring(0, match.Index);
                var tail = fencedBody.Substring(match.Index + match.Length);
                if (StartsWithDmlStatement(tail, alreadyHasOwnStepMarker: PrecededByStepIdMarker(head)))
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
        /// 이 블록 주석 <b>바로 앞</b>(공백·`--`만 건너뛰고)이 이미
        /// <see cref="StepIdMarkerRegex"/>인가 - 즉 이 주석이 <b>자신의</b> 단계
        /// 표식을 이미 달고 있는가.
        ///
        /// [왜 필요한가 - 실측 2026-09-05 라운드 2 재검토] 원본 관용구는 "표식 하나당
        /// DML 하나"다. 이 주석이 이미 자기 표식을 앞에 달고 있다면, 꼬리에서 <b>또</b>
        /// 표식을 만나는 것은 다음 단계가 시작됐다는 뜻이지 이 주석 자신의 표식이
        /// 아니다(S07.md:240-244, U15 - 자기 표식(-21)을 이미 달았는데 꼬리에 다시
        /// 표식(-27)이 나오고서야 진짜 UPDATE 가 있다. 그 UPDATE 는 다음 단계에
        /// 속한다). 반대로 이 주석 앞에 표식이 없다면(S07.md:35, U1), 꼬리의 표식은
        /// 이 주석 자신의 것이므로 건너뛰고 그 뒤의 DML 을 앵커로 인정해야 한다.
        /// </summary>
        private static bool PrecededByStepIdMarker(string head)
        {
            var lines = MarkdownSectionLocator.SplitLines(head);
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("--", StringComparison.Ordinal)) continue;
                return StepIdMarkerRegex.IsMatch(s);
            }

            return false;
        }

        /// <summary>
        /// 주석 바로 뒤에 <b>이 주석 자신의</b> DML 문장이 서 있는가.
        ///
        /// [연쇄 억제 버그 - 실측 2026-09-05 라운드 1] 종전에는 이 줄이 "SET @"로
        /// 시작하지 않고 비어 있지도 않으면 그 줄 <i>전체</i>를 DmlVerbRegex 로
        /// 검사했다. 뒤따르는 다음 블록 주석의 첫 줄(`/* UPDATE 2: ... */`)이 그
        /// 검사를 통과해 "UPDATE" 낱말을 포함한다는 이유로 「진짜 문장이 뒤따른다」고
        /// 오판했다 - 앞 주석이 죽고 S08 에서 연쇄의 마지막 하나만 살아남았다.
        ///
        /// 그래서 다음 블록 주석 시작을 <b>멈춤</b> 신호로 삼는다 - 다른 블록 주석이
        /// 시작하면 그것은 또 다른 자리표시자이지 이 주석의 앵커가 아니다.
        ///
        /// [오탐 회귀 - 실측 2026-09-05 라운드 2] 라운드 1은 <see cref="StepIdMarkerRegex"/>
        /// 도 같은 멈춤 신호로 취급했다. 그러나 그 표식은 원본이 <b>실행 DML 직전에</b>
        /// 남기는 관용구라(S08.md 서두) 정상 완료된 자리에도 항상 나타난다 - 멈춤
        /// 신호로 삼으면 「주석; SET 표식; 진짜 UPDATE」 모양의 정상 자리를 전부
        /// 생략으로 고발한다(리뷰어 최소 재현, S07 17건 중 7건 오탐).
        ///
        /// [정밀화 - 실측 2026-09-05 라운드 2 재검토] 표식을 무조건 건너뛰기만 하면
        /// 이번엔 반대로 놓친다 - <see cref="PrecededByStepIdMarker"/>가 참이면(이
        /// 주석이 이미 자기 표식을 달고 있으면) 꼬리의 표식은 <b>다음</b> 단계의
        /// 경계이므로 멈춤(false)이다. 거짓이면(표식이 없으면) 꼬리의 표식은 이
        /// 주석 자신의 것이므로 건너뛴다("--"·`DECLARE`처럼).
        ///
        /// 남는 것은 세 갈래다: 다음 블록 주석이면 멈춤(false), 표식이고 이 주석이
        /// 이미 자기 표식을 달았으면 멈춤(false)·아니면 건너뛰고 계속 본다, 줄 맨
        /// 앞이 DML 동사로 시작하면 앵커다(true - 줄 안 어딘가가 아니라 <b>맨 앞</b>
        /// - <see cref="LeadingDmlStatementRegex"/>). 그 외의 줄(`DECLARE` 같은 부수
        /// 설정문)은 건너뛴다.
        /// </summary>
        private static bool StartsWithDmlStatement(string tail, bool alreadyHasOwnStepMarker)
        {
            foreach (var line in MarkdownSectionLocator.SplitLines(tail))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("--", StringComparison.Ordinal)) continue;

                if (s.StartsWith("/*", StringComparison.Ordinal)) return false;
                if (StepIdMarkerRegex.IsMatch(s))
                {
                    if (alreadyHasOwnStepMarker) return false;
                    continue;
                }
                if (LeadingDmlStatementRegex.IsMatch(s)) return true;

                // DECLARE 등 부수 설정문 - 같은 단계에 속할 수 있으므로 계속 건너뛴다.
            }

            return false;
        }
    }
}
