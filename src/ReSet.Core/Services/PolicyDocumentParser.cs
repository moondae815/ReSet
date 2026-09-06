using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>정책서 규칙 표의 한 행.</summary>
    public sealed record PolicyRule(
        string StageHeading,
        int StageNumber,
        string Id,
        string Text,
        string EvidenceRaw,
        string CodeValue,
        int LineNumber);

    /// <summary>
    /// 정책서의 규칙 표를 읽는다.
    ///
    /// 섹션 경계와 코드 펜스 판정은 MarkdownSectionLocator에, 파이프 분해는
    /// MarkdownTableCellCodec에 맡긴다 - PrdDocumentParser 주석이 지적한 대로,
    /// 파이프 분해를 손수 다시 구현하는 자리를 늘리지 않는다.
    ///
    /// [부록을 읽지 않는 이유] 부록은 기계가 만든 표이지 AI가 쓴 규칙이 아니다.
    /// 검사 대상은 AI가 쓴 것뿐이어야 한다.
    /// </summary>
    public static class PolicyDocumentParser
    {
        public static IReadOnlyList<PolicyRule> Parse(
            string? policyMarkdown, IReadOnlyList<string> stageHeadings)
        {
            var lines = MarkdownSectionLocator.SplitLines(policyMarkdown);
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            var rules = new List<PolicyRule>();

            for (var stageIndex = 0; stageIndex < stageHeadings.Count; stageIndex++)
            {
                var heading = stageHeadings[stageIndex];
                var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(lines, heading, "## ");
                if (headerIndex < 0)
                {
                    continue;
                }

                var stageNumber = PolicySectionContract.EffectiveStageNumber(heading, stageIndex);

                for (var i = headerIndex + 1; i < endIndex; i++)
                {
                    if (fenceFlags[i])
                    {
                        continue;
                    }

                    var cells = SplitRow(lines[i]);
                    if (cells is null
                        || cells.Count != PolicySectionContract.ExpectedCellCount
                        || IsHeaderOrSeparator(cells))
                    {
                        continue;
                    }

                    rules.Add(new PolicyRule(
                        heading, stageNumber, cells[0], cells[1], cells[2], cells[3], i + 1));
                }
            }

            return rules;
        }

        private static List<string>? SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                return null;
            }

            var cells = MarkdownTableCellCodec.SplitRow(trimmed.Trim('|'));
            return RejoinOverSplitEvidence(cells);
        }

        /// <summary>
        /// 근거 칸 안의 이스케이프되지 않은 파이프 때문에 다섯 칸 이상으로 터진 행을
        /// 계약대로 도로 잇는다. PrdDocumentParser.RejoinOverSplitEvidence와 같은 문제를
        /// 풀지만 앵커가 다르다 - PRD의 근거 칸은 `## 헤딩 > "구절"`로 시작해 "## "로
        /// 여는 칸을 바로 찾을 수 있지만, 정책서는 `&lt;SP&gt; · ## 헤딩 > "구절"`이라
        /// SP 식별자가 앞에 붙는다. 그래서 여는 앵커는 "## "로 시작하는 칸이 아니라
        /// LabelSeparator와 "## "를 함께 담은 칸이다.
        ///
        /// [왜 필요한가] 정책서는 근거가 여러 명세서에 흩어져 있어 이 위험이 PRD보다
        /// 크다 - 생성 프롬프트가 축자 인용을 요구하는데 명세서의 알찬 사실은 표 안에
        /// 살아서, 모델이 지시를 지킬수록 인용에 표 파이프가 섞인다.
        ///
        /// 추측으로 붙이지 않는다 - 근거 칸의 문법이 열리는 자리(SP 라벨 뒤의 "## ")부터
        /// 인용이 닫히는 칸까지만 잇는다. 문법이 안 보이면 손대지 않고 원래대로 둔다.
        /// 칸 수가 계약과 같은 행은 아예 건드리지 않으므로 지금 통과하는 문서의 판정은
        /// 이 되살리기로 달라질 수 없다.
        /// </summary>
        private static List<string> RejoinOverSplitEvidence(List<string> cells)
        {
            if (cells.Count <= PolicySectionContract.ExpectedCellCount)
            {
                return cells;
            }

            var openAnchor = PolicySectionContract.LabelSeparator + "## ";

            var start = -1;
            for (var i = 1; i < cells.Count - 1; i++)
            {
                if (cells[i].Contains(openAnchor, StringComparison.Ordinal))
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                return cells;
            }

            var end = -1;
            for (var i = cells.Count - 2; i >= start; i--)
            {
                if (cells[i].EndsWith("\"", StringComparison.Ordinal))
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return cells;
            }

            return new List<string>(PolicySectionContract.ExpectedCellCount)
            {
                cells[0],
                string.Join(" | ", cells.GetRange(1, start - 1)),
                string.Join(" | ", cells.GetRange(start, end - start + 1)),
                string.Join(" | ", cells.GetRange(end + 1, cells.Count - end - 1)),
            };
        }

        private static bool IsHeaderOrSeparator(List<string> cells) =>
            cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase)
            || cells.All(c => c.Length > 0 && c.All(ch => ch == ':' || ch == '-'));
    }
}
