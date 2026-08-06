using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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

            // 골격이 H2를 빠뜨렸더라도 단계 본문을 잃지 않는다. 문서 레벨 L1이
            // 그 누락을 별도로 잡으므로 여기서 조용히 버리면 안 된다.
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
            return stripped.Replace("\r\n", "\n").Split('\n').ToList();
        }

        /// <summary>
        /// 단계 상세 H2의 헤더 줄 인덱스와, 그 블록이 끝나는(= 다음 H2가 시작하는)
        /// 인덱스를 돌려준다. 헤더가 없으면 (-1, -1).
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateStepDetailBlock(List<string> lines)
        {
            var headerIndex = lines.FindIndex(line => line.Trim() == StepDetailHeader);
            if (headerIndex < 0)
            {
                return (-1, -1);
            }

            // "### "는 인덱스 2가 '#'이라 StartsWith("## ")에 걸리지 않는다.
            var endIndex = lines.FindIndex(
                headerIndex + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }
    }
}
