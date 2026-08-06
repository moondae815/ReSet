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
            return stripped.Replace("\r\n", "\n").Split('\n').ToList();
        }

        /// <summary>
        /// 단계 상세 H2의 헤더 줄 인덱스와, 그 블록이 끝나는(= 다음 H2가 시작하는)
        /// 인덱스를 돌려준다. 헤더가 없으면 (-1, -1).
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateStepDetailBlock(List<string> lines)
        {
            var headerIndex = FindIndexOutsideFence(lines, 0, line => line.Trim() == StepDetailHeader);
            if (headerIndex < 0)
            {
                return (-1, -1);
            }

            // "### "는 인덱스 2가 '#'이라 StartsWith("## ")에 걸리지 않는다.
            var endIndex = FindIndexOutsideFence(
                lines,
                headerIndex + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        /// <summary>
        /// 펜스(```)로 둘러싸인 줄은 건너뛰고 조건을 만족하는 첫 줄의 인덱스를
        /// 찾는다. 공통 규약 소절에 실린 SQL 코드 블록 안에 "## "로 시작하는
        /// 줄이 있어도 헤더나 블록 경계로 오인하지 않기 위함이다.
        ///
        /// 펜스가 끝까지 닫히지 않으면(모델이 ``` 하나를 빠뜨린 경우) inFence가
        /// 참인 채로 끝까지 스캔이 끝난다 — 그러면 이후 모든 줄이 "펜스 안"으로
        /// 오인되어 다음 H2를 영영 못 찾고, 공통 규약 소절이 문서 나머지 전부를
        /// (검증 SQL H2까지 포함해) 삼켜 버린다. 이 경우 펜스 상태를 신뢰할 수
        /// 없으므로 펜스를 무시하고 다시 스캔한다 — 오탐(코드 안의 "## ")보다
        /// 미탐(전체 삼킴)이 훨씬 나쁘다.
        /// </summary>
        private static int FindIndexOutsideFence(List<string> lines, int startIndex, Func<string, bool> predicate)
        {
            var inFence = false;
            for (var i = startIndex; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (!inFence && predicate(lines[i]))
                {
                    return i;
                }
            }

            if (inFence)
            {
                for (var i = startIndex; i < lines.Count; i++)
                {
                    if (predicate(lines[i]))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }
    }
}
