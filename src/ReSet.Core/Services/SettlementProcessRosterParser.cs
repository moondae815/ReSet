using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// `settlement-process.md`를 모델로 읽는다.
    ///
    /// H2가 단계, 그 아래 `- ` 목록이 소속 프로시저, 순서는 등장 순서다.
    /// HTML 주석(`&lt;!--`)은 초안이 근거를 남기는 자리이므로 항목으로 읽지 않는다.
    /// 섹션 경계 판정은 MarkdownSectionLocator의 관행(H2 접두사)을 따르되, 이 문서는
    /// 코드 펜스를 쓰지 않으므로 펜스 계산은 하지 않는다.
    ///
    /// [왜 주석에 블록 상태가 필요한가] 이 파일은 사람이 손으로 고친다. 가장 자연스러운
    /// 편집 동작 하나가 단계를 통째로 주석으로 잠시 꺼두는 것이다(`&lt;!-- ... --&gt;`가
    /// 여러 줄에 걸침). 한 줄 시작 판정(`item.StartsWith("&lt;!--")`)만으로는 이 블록
    /// 안의 `- ` 줄이 살아 있는 프로시저로, `## ` 줄이 살아 있는 단계로 읽혀 「사람이
    /// 껐다고 믿는 것이 조용히 켜진 채로 정책서에 들어가는」 결함이 생긴다 - 이 설계가
    /// 막으려던 「조용한 누락」의 거울상이다. 그래서 줄 단위 판정에 앞서 문서 전체를
    /// 한 번 스캔해 "이 줄이 주석 블록 안인가"를 미리 계산한다.
    /// </summary>
    public static class SettlementProcessRosterParser
    {
        public static SettlementProcessRoster Parse(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return SettlementProcessRoster.Empty;
            }

            var stages = new List<PolicyStage>();
            var excluded = new List<string>();

            string? currentTitle = null;
            var currentItems = new List<string>();
            var inExcluded = false;

            void Flush()
            {
                if (inExcluded)
                {
                    excluded.AddRange(currentItems);
                }
                else if (currentTitle is not null)
                {
                    stages.Add(new PolicyStage(currentTitle, currentItems.ToList()));
                }

                currentItems.Clear();
            }

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var inCommentBlock = ComputeCommentBlockFlags(lines);

            for (var i = 0; i < lines.Count; i++)
            {
                // 주석 블록 검사가 헤딩/항목 분기보다 먼저다 - 그래야 블록 안에 적힌
                // "## " 메모용 헤딩이 진짜 단계로 둔갑하지 않는다.
                if (inCommentBlock[i])
                {
                    continue;
                }

                var line = lines[i].Trim();

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    inExcluded = line.Equals(SettlementProcessRoster.ExcludedHeading, StringComparison.Ordinal);
                    currentTitle = inExcluded ? null : line[3..].Trim();
                    continue;
                }

                if (!line.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                var item = line[2..].Trim();
                if (item.Length == 0 || item.StartsWith("<!--", StringComparison.Ordinal))
                {
                    // 이 가드는 ComputeCommentBlockFlags의 닫힘-폴백과 독립적으로
                    // 필요하다. 목록 표식으로 연 주석(`- <!-- ...`)이 문서 끝까지 안
                    // 닫히면, 그 폴백은 "주석 상태를 신뢰할 수 없다"며 플래그를 전부
                    // 지운다(Array.Clear) - 그러면 본 루프가 이 줄을 다시 평범한
                    // 항목으로 읽어 "<!-- ..." 라는 문자열 자체가 프로시저로 명부에
                    // 들어간다. 폴백이 플래그를 지우는 것과 별개로, 이 줄이 주석을
                    // 여는 줄이라는 사실 자체는 모호하지 않다 - 문자 그대로 "<!--"가
                    // 들어 있다. 그래서 이 가드가 그 자리를 막는다.
                    //
                    // 2026-09-06 라운드 2에서 이 가드를 "스캐너가 흡수해 잉여가 됐다"고
                    // 보고 지웠다가 정확히 이 회귀를 만들었다 - 그때의 제거 증명
                    // (14건 전부 통과 유지)에는 "목록 표식으로 열고 EOF까지 안 닫힌
                    // 주석" 표본이 없어 이 자리를 가리지 못했다.
                    //
                    // [스캐너와의 겹침은 의도된 것이다] 한 줄짜리 `- <!-- ... -->`는 이
                    // 가드와 ComputeCommentBlockFlags의 목록 표식 벗기기가 함께 막는다.
                    // 그 겹침을 없애지 않는다 - 가드는 "닫히지 않은 주석"(바로 위 문단)
                    // 때문에, 스캐너는 "여러 줄 블록" 때문에 각각 따로 필요한데, 한 줄
                    // 경우가 두 정의에 동시에 걸릴 뿐이다. 겹침을 지우려고 어느 한쪽을
                    // 좁히면 그 한쪽이 홀로 지키던 자리가 얇아진다 - 라운드 2가 가드를
                    // 지워서 실제로 그렇게 됐다.
                    //
                    // 대가는 있다: 한 줄 경우로는 스캐너의 회귀를 잡을 수 없다. 격리
                    // 사본에서 실측했다(2026-09-06, 알파벳 15 x 길이 5 = 813,615 문서
                    // 전수). 스캐너의 "한 줄" 벗기기만 되돌린 변이는 관측 가능한 입력이
                    // 0이고(가드가 전부 가린다), 같은 변이가 가드를 함께 끄면 132,160
                    // 문서에서 드러난다. 즉 "가드가 안 걸리면서 스캐너의 한 줄 벗기기가
                    // 필요한 입력"은 없다 - 두 술어가 같은 식(line[2..].Trim()이 "<!--"로
                    // 시작하는가)이기 때문이다. 그래서 스캐너의 목록 표식 벗기기를
                    // 지키는 것은 여러 줄 표본 두 건뿐이다(테스트 파일 참조).
                    continue;
                }

                currentItems.Add(item);
            }

            Flush();

            return new SettlementProcessRoster(stages, excluded);
        }

        /// <summary>
        /// 각 줄이 `&lt;!-- ... --&gt;` 주석 블록 안에 있는지 미리 계산한다.
        /// 한 줄 안에서 열고 닫히는 주석(`&lt;!-- 메모 --&gt;`)은 그 줄 하나만 표시하고
        /// 다음 줄로 상태를 끌고 가지 않는다 - 안 그러면 그 뒤에 오는 정상 항목까지
        /// 통째로 삼켜지는 반대 결함이 생긴다.
        ///
        /// [왜 목록 표식을 먼저 벗기는가] 사람은 목록 항목 하나를 통째로 주석 처리할
        /// 때도 있다 - `- &lt;!-- 메모 --&gt;`(한 줄) 또는 `- &lt;!-- 여러 줄`(블록을
        /// 여는 줄)처럼. 목록 표식(`- `)을 벗기지 않고 줄 맨 앞의 `&lt;!--`만 보면 이
        /// 패턴을 놓쳐 블록이 하나도 안 열린 것으로 오인하고, 블록 안의 `- dbo.UP_X`가
        /// 살아 있는 프로시저로 샌다. 벗기는 규칙은 좁게 잡는다 - `- ` 하나만 벗긴다.
        /// 파서가 항목을 읽을 때도 `line.StartsWith("- ")`만 보므로 그 이상(중첩 들여쓰기
        /// 등)은 애초에 이 문서에서 항목이 되지 않는다. 다만 벗기기의 두 몫 중 한 줄
        /// 짜리(<c>- &lt;!-- 메모 --&gt;</c>)는 본 루프의 <c>item.StartsWith("&lt;!--")</c>
        /// 가드가 똑같이 막는다 - 의도된 겹침이며 그 근거와 실측은 그 가드의 주석에 있다.
        /// 이 벗기기가 홀로 지키는 것은 여러 줄 블록을 여는 몫이다.
        ///
        /// 문서 끝까지 닫히지 않은 주석은 <see cref="MarkdownSectionLocator.ComputeFenceFlags"/>와
        /// 같은 판단을 따른다: 주석 상태를 신뢰할 수 없으므로 전부 무시한다. 오탐(주석
        /// 아닌 내용을 주석으로 오인)보다 미탐(문서 나머지 전부가 주석 하나에 묶여
        /// 통째로 삼켜지는 것)이 훨씬 나쁘기 때문이다.
        /// </summary>
        private static IReadOnlyList<bool> ComputeCommentBlockFlags(IReadOnlyList<string> lines)
        {
            var flags = new bool[lines.Count];
            var inComment = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();

                if (inComment)
                {
                    flags[i] = true;
                    if (line.Contains("-->", StringComparison.Ordinal))
                    {
                        inComment = false;
                    }

                    continue;
                }

                var candidate = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim() : line;

                if (!candidate.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                flags[i] = true;
                if (!candidate.Contains("-->", StringComparison.Ordinal))
                {
                    inComment = true;
                }
            }

            if (inComment)
            {
                Array.Clear(flags, 0, flags.Length);
            }

            return flags;
        }
    }
}
