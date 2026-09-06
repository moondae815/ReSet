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
                if (item.Length == 0)
                {
                    // "<!--"로 시작하는 항목("- <!-- ... -->" 포함)은 이제 여기 오지
                    // 않는다 - ComputeCommentBlockFlags가 목록 표식을 벗기고 그 자리를
                    // 흡수해 이 줄 자체를 이미 주석으로 표시했기 때문이다. 예전에는
                    // 여기 별도 item.StartsWith("<!--") 가드가 있었는데, removal-proof로
                    // 확인해 보니 그 가드가 실제로 지키는 유일한 패턴이 이 자리였고
                    // 스캐너가 흡수한 뒤로는 지울 때와 지우지 않을 때 테스트 결과가
                    // 똑같았다(전체 14건 통과 유지) - 그래서 지웠다.
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
        /// 등)은 애초에 이 문서에서 항목이 되지 않는다.
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
