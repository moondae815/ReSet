using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 골격 문서와 단계별 섹션을 하나의 계획서로 합친다.
    ///
    /// 조립은 모델이 넣은 자리표시자의 위치를 신뢰하지 않는다. 자리표시자가
    /// 빠지거나 순서가 틀려도 조립이 깨지지 않도록, 목록 순서대로 `## 단계별
    /// 이행 상세 및 의사코드` 블록 끝에 결정적으로 덧붙이고 자리표시자는 지운다.
    /// 프롬프트가 자리표시자를 요구하는 것은 모델이 단계 본문까지 써 버리는 것을
    /// 막기 위해서지, 조립이 그것에 의존하기 때문이 아니다.
    /// </summary>
    public static class BatchPlanAssembler
    {
        public const string StepDetailHeader = "## 단계별 이행 상세 및 의사코드";

        private static readonly Regex StepPlaceholderRegex = new(
            @"(?m)^[ \t]*<!--\s*STEP:[^>]*-->[ \t]*\r?\n?",
            RegexOptions.Compiled);

        /// <summary>
        /// 골격의 `## 단계별 이행 상세 및 의사코드` 본문(공통 규약 소절들)만 뽑는다.
        /// 단계별 호출에 그대로 실어, 13개 단계가 서로 다른 오류 처리 관례를
        /// 선언하는 일을 막는다.
        /// </summary>
        public static string ExtractSharedConventions(string? skeletonMarkdown)
        {
            var lines = Normalize(skeletonMarkdown);
            var (headerIndex, endIndex) = LocateStepDetailBlock(lines);
            if (headerIndex < 0)
            {
                return string.Empty;
            }

            return string.Join("\n", lines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1)).Trim();
        }

        public static string Assemble(string? skeletonMarkdown, IReadOnlyList<string> stepSections)
        {
            var sections = (stepSections ?? Array.Empty<string>())
                .Where(section => !string.IsNullOrWhiteSpace(section))
                .Select(section => section.Trim())
                .ToList();

            var lines = Normalize(skeletonMarkdown);
            if (sections.Count == 0)
            {
                return string.Join("\n", lines);
            }

            var body = string.Join("\n\n", sections);
            var (headerIndex, endIndex) = LocateStepDetailBlock(lines);

            // 골격이 H2를 빠뜨렸더라도 단계 본문을 잃지 않는다. 아래에서 헤더를
            // 직접 합성해 붙이므로 문서 레벨 L1은 이 누락을 볼 수 없다 — 그래서
            // 여기서 조용히 버리면 그 결함을 잡아낼 곳이 아무 데도 없다.
            if (headerIndex < 0)
            {
                return string.Join("\n", lines).TrimEnd() + "\n\n" + StepDetailHeader + "\n\n" + body + "\n";
            }

            var merged = new List<string>(lines);
            merged.InsertRange(endIndex, new[] { string.Empty }.Concat(body.Split('\n')).Append(string.Empty));
            return string.Join("\n", merged);
        }

        private static List<string> Normalize(string? markdown)
        {
            var stripped = StepPlaceholderRegex.Replace(markdown ?? string.Empty, string.Empty);
            return MarkdownSectionLocator.SplitLines(stripped);
        }

        /// <summary>
        /// 단계 상세 H2의 헤더 줄 인덱스와, 그 블록이 끝나는(= 다음 H2가 시작하는)
        /// 인덱스를 돌려준다. 헤더가 없으면 (-1, -1).
        ///
        /// [왜 폴백이 있는가 - POQSettleProc17·18 연속 재발]
        /// 골격 프롬프트는 이 H2를 VERBATIM으로 쓰라고 요구하지만(AiService의 Skeleton
        /// Contract) 모델은 `## 단계별 이행 상세 및 의사코드:`처럼 꼬리표를 붙여 쓴다.
        /// 정확 일치로만 찾으면 못 찾고, 호출부가 문서 끝에 같은 H2를 새로 합성해
        /// 계획서에 H2가 둘이 된다 - 공통 규약 절과 단계 본문이 갈라지고,
        /// MechanicalValidator는 헤더를 Contains로 보므로 그 문서를 통과시킨다.
        ///
        /// 조립 시점의 문서는 골격 하나뿐이라 같은 텍스트를 담은 H2가 둘일 수 없다.
        /// 폴백이 잡는 자리는 유일하다.
        ///
        /// 폴백으로 찾았다는 사실은 남긴다. 조립이 성공했다고 계약 위반이 없던 일이
        /// 되지는 않으며, 흔적이 없으면 골격 프롬프트가 지켜지지 않는다는 사실을
        /// 아무도 보지 못한다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateStepDetailBlock(List<string> lines)
        {
            var exact = MarkdownSectionLocator.LocateSection(lines, StepDetailHeader, "## ");
            if (exact.HeaderIndex >= 0) return exact;

            var loose = MarkdownSectionLocator.LocateSection(lines, StepDetailHeader, "## ", exact: false);
            if (loose.HeaderIndex >= 0)
            {
                Log.Warning(
                    "골격의 단계 상세 H2가 계약 문구와 달라 느슨하게 찾았습니다 - 기대: {Expected}, 실제: {Actual}",
                    StepDetailHeader, lines[loose.HeaderIndex].Trim());
            }

            return loose;
        }
    }
}
