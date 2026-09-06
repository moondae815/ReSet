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

            foreach (var heading in stageHeadings)
            {
                var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(lines, heading, "## ");
                if (headerIndex < 0)
                {
                    continue;
                }

                var stageNumber = PolicySectionContract.StageNumberOf(heading);

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
            return trimmed.StartsWith("|", StringComparison.Ordinal)
                ? MarkdownTableCellCodec.SplitRow(trimmed.Trim('|'))
                : null;
        }

        private static bool IsHeaderOrSeparator(List<string> cells) =>
            cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase)
            || cells.All(c => c.Length > 0 && c.All(ch => ch == ':' || ch == '-'));
    }
}
