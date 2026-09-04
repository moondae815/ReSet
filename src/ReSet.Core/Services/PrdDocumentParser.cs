using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>Prd.md 한 요구 표의 행 하나. LineNumber는 1부터 센다(결함 보고용).</summary>
    public sealed record PrdRequirement(
        string Section,
        string Id,
        string Text,
        string EvidenceRaw,
        string Confidence,
        int LineNumber);

    /// <summary>
    /// Prd.md의 요구 표를 읽는다. 섹션 경계와 코드 펜스 판정은
    /// <see cref="MarkdownSectionLocator"/>에 맡긴다 - 펜스 미닫힘 폴백을 두 곳이
    /// 각자 갖는 사고를 반복하지 않기 위해서다.
    /// </summary>
    public static class PrdDocumentParser
    {
        public static IReadOnlyList<PrdRequirement> Parse(string? prdMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(prdMarkdown);
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            var requirements = new List<PrdRequirement>();

            foreach (var rule in PrdSectionContract.Sections)
            {
                var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(lines, rule.Heading, "## ");
                if (headerIndex < 0)
                {
                    continue;
                }

                for (var i = headerIndex + 1; i < endIndex; i++)
                {
                    if (fenceFlags[i])
                    {
                        continue;
                    }

                    var cells = SplitRow(lines[i]);
                    if (cells is null || cells.Count < 4)
                    {
                        continue;
                    }

                    if (IsHeaderOrSeparator(cells))
                    {
                        continue;
                    }

                    requirements.Add(new PrdRequirement(
                        rule.Heading, cells[0], cells[1], cells[2], cells[3], i + 1));
                }
            }

            return requirements;
        }

        /// <summary>`| a | b |` 형태의 줄만 칸으로 가른다. 표가 아니면 null.</summary>
        private static List<string>? SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                return null;
            }

            var body = trimmed.Trim('|');
            return body.Split('|').Select(c => c.Trim()).ToList();
        }

        private static bool IsHeaderOrSeparator(List<string> cells)
        {
            if (cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return cells.All(c => c.Length > 0 && c.All(ch => ch == ':' || ch == '-'));
        }
    }
}
