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
                    if (cells is null || cells.Count < ExpectedCellCount)
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

        /// <summary>계약이 고정한 칸 수. 넘치면 인용 안의 파이프가 행을 터뜨린 것이다.</summary>
        private const int ExpectedCellCount = 4;

        /// <summary>`| a | b |` 형태의 줄만 칸으로 가른다. 표가 아니면 null.</summary>
        private static List<string>? SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                return null;
            }

            // 렌더 관행(MarkdownTableCellCodec.Escape)이 남긴 `\|`는 칸 경계가 아니라
            // 칸 내용이다. 이 왕복을 위한 중립 헬퍼가 이미 있는데(AiService와
            // MechanicalValidator가 공유한다) 여기가 파이프 분해를 손수 다시 구현한
            // 네 번째 자리였다.
            var cells = MarkdownTableCellCodec.SplitRow(trimmed.Trim('|'));
            return RejoinOverSplitEvidence(cells);
        }

        /// <summary>
        /// 이스케이프되지 않은 파이프가 든 인용 때문에 다섯 칸 이상으로 터진 행을
        /// 계약대로 도로 잇는다.
        ///
        /// [왜 필요한가 - 2026-09-04 도입 스윕 실측] 생성 프롬프트는 근거를 "verbatim
        /// 인용"으로 요구하는데 Spec의 알찬 사실은 표 안에 산다. 그래서 모델이 지시를
        /// 지킬수록 인용에 표 파이프가 섞여 들어온다(도출 8건 중 2건에서 7행). 터진
        /// 행을 그대로 두면 검사가 "확신도가 CHAR(8)이다"·"근거 칸이 형식이 아니다"라는
        /// **거짓 진단**을 내는데, 그 거짓은 사람용 배너에 실릴 뿐 아니라 교정 재호출의
        /// 피드백이 되어 모델에게 실행 불가능한 지시("확신도를 9에서 고쳐라")로 간다.
        ///
        /// 추측으로 붙이지 않는다 - 근거 칸의 문법(`## 헤딩 > "구절"`)이 이미 계약이므로
        /// 그 문법이 여는 칸부터 인용이 닫히는 칸까지만 잇는다. 문법이 없으면 손대지
        /// 않고 원래대로 고발되게 둔다. 칸 수가 계약과 같은 행은 아예 건드리지 않으므로
        /// 지금 통과하는 문서의 판정은 이 되살리기로 달라질 수 없다.
        /// </summary>
        private static List<string> RejoinOverSplitEvidence(List<string> cells)
        {
            if (cells.Count <= ExpectedCellCount)
            {
                return cells;
            }

            var start = -1;
            for (var i = 1; i < cells.Count - 1; i++)
            {
                if (cells[i].StartsWith("## ", StringComparison.Ordinal))
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

            return new List<string>(ExpectedCellCount)
            {
                cells[0],
                string.Join(" | ", cells.GetRange(1, start - 1)),
                string.Join(" | ", cells.GetRange(start, end - start + 1)),
                string.Join(" | ", cells.GetRange(end + 1, cells.Count - end - 1)),
            };
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
