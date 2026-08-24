using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <param name="Line">이 앵커가 지목하는 원본 DDL 줄.</param>
    /// <param name="Source">"표: {제목}" · "절 제목" · "셀 내 (라인 N)".</param>
    /// <param name="RowText">근거 패널에 그대로 띄울 원문 한 줄.</param>
    /// <param name="IsCommentAnchor">원본 주석 표에서 나왔으면 true. 커버리지 판정에서 뺀다.</param>
    public sealed record SpecAnchor(int Line, string Source, string RowText, bool IsCommentAnchor);

    /// <summary>
    /// Spec.md가 지목하는 원본 DDL 줄을 전부 걷는다.
    ///
    /// [왜 제목 화이트리스트를 두지 않는가 - 2026-08-24 실측]
    /// 표 제목이 표준화돼 있지 않다. 주석 표 하나가 '원본 주석 기록'·'원본 주석 보존'·
    /// '원본 주석 보존 내역'·'원본 주석 및 이력'·'원본 주석 및 구현 대조'·
    /// '원본 주석 및 실제 구현 대조' 여섯으로 갈리고, EXCEPTION_PROC은 아예 제목 없이
    /// '## 로직 흐름 요약' 아래 산문 뒤에 붙인다. 그래서 <b>헤더에 '라인' 칸이 있는
    /// 표를 전부</b> 줍는다.
    ///
    /// [왜 칸 위치를 상수로 박지 않는가] '라인' 칸 위치가 표마다 다르다. 집합 술어·
    /// 잠금 힌트·DML 범위·실행 의미는 2번째인데 CASE 분기와 주석 표는 1번째다.
    /// 위치를 박으면 CASE 분기 표에서 '순서' 값(1, 2, 3...)을 라인 번호로 줍는다 -
    /// 설계서 첫 판이 실제로 낸 오류다.
    ///
    /// [왜 주석 앵커를 갈라 두는가] 원본 주석 표도 '라인' 칸을 갖는다(14개 SP 합 223행).
    /// 주석 표가 말하는 것은 "원본 38번 줄에 이런 주석이 있었다"이지 "38번 줄의 문장이
    /// 문서화됐다"가 아니다. 섞어 세면 주석이 빽빽한 SP일수록 커버리지가 높게 나와,
    /// 맵이 재려는 것과 정반대의 것을 재게 된다. 버리지는 않고 근거 패널에 참고로 띄운다.
    /// </summary>
    public static class SpecAnchorIndex
    {
        private const string LineColumnName = "라인";

        private static readonly Regex SectionHeadingLine =
            new(@"원본 DDL 라인\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ParenthesizedLine =
            new(@"\(라인\s*(\d+)\)", RegexOptions.Compiled);

        private static readonly Regex HeadingLine =
            new(@"^#{2,6}\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex SeparatorRow =
            new(@"^\|[\s:|-]+\|\s*$", RegexOptions.Compiled);

        public static IReadOnlyList<SpecAnchor> Build(string? specMarkdown)
        {
            var anchors = new List<SpecAnchor>();
            if (string.IsNullOrWhiteSpace(specMarkdown)) return anchors;

            foreach (var (heading, header, row, isComment) in EnumerateLineBearingRows(specMarkdown))
            {
                var index = IndexOfLineColumn(header);
                var cells = MarkdownTableCellCodec.SplitRow(row);
                if (index < 0 || index >= cells.Count) continue;
                if (!int.TryParse(cells[index].Trim(), out var line)) continue;

                anchors.Add(new SpecAnchor(
                    line,
                    $"표: {heading}",
                    row.Trim(),
                    isComment));
            }

            foreach (var raw in MarkdownSectionLocator.SplitLines(specMarkdown))
            {
                foreach (Match m in SectionHeadingLine.Matches(raw))
                {
                    anchors.Add(new SpecAnchor(int.Parse(m.Groups[1].Value), "절 제목", raw.Trim(), false));
                }

                foreach (Match m in ParenthesizedLine.Matches(raw))
                {
                    anchors.Add(new SpecAnchor(
                        int.Parse(m.Groups[1].Value), "셀 내 (라인 N)", raw.Trim(), false));
                }
            }

            return anchors;
        }

        public static int CountLineBearingTables(string? specMarkdown)
        {
            if (string.IsNullOrWhiteSpace(specMarkdown)) return 0;
            return EnumerateLineBearingRows(specMarkdown)
                .Select(r => r.Heading)
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        /// <summary>
        /// '라인' 칸을 가진 표의 (제목, 헤더 칸 목록, 데이터 행, 주석 표 여부)를 순서대로 흘린다.
        /// 코드 펜스 안은 건너뛴다.
        /// </summary>
        private static IEnumerable<(string Heading, List<string> Header, string Row, bool IsComment)>
            EnumerateLineBearingRows(string markdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var heading = "(제목 없음)";
            var inFence = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                var h = HeadingLine.Match(line);
                if (h.Success)
                {
                    heading = h.Groups[1].Value.Trim();
                    continue;
                }

                if (!line.StartsWith("|", StringComparison.Ordinal)) continue;
                if (i + 1 >= lines.Count || !SeparatorRow.IsMatch(lines[i + 1])) continue;

                var header = MarkdownTableCellCodec.SplitRow(line);
                if (IndexOfLineColumn(header) < 0)
                {
                    i++;
                    continue;
                }

                var isComment = header.Any(c => c.Contains("주석", StringComparison.Ordinal));

                for (var j = i + 2; j < lines.Count && lines[j].StartsWith("|", StringComparison.Ordinal); j++)
                {
                    yield return (heading, header, lines[j], isComment);
                    i = j;
                }
            }
        }

        private static int IndexOfLineColumn(List<string> header) =>
            header.FindIndex(c => string.Equals(c.Trim(), LineColumnName, StringComparison.Ordinal));
    }
}
