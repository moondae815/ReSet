using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 마크다운에서 헤딩 위치를 찾는다. 코드 펜스 안의 `#`을 헤딩으로 오인하지 않는다.
    ///
    /// BatchPlanAssembler의 private 헬퍼였던 것을 승격했다. PlanBoundaryResolver가
    /// 같은 문제(계획서 본문에 SQL 블록이 많고 그 안에 `##`이 등장한다)를 풀어야 하는데,
    /// 이 판정을 두 곳에서 각자 구현하면 한쪽만 펜스 미닫힘 폴백을 갖게 된다.
    /// </summary>
    public static class MarkdownSectionLocator
    {
        /// <summary>줄 바꿈을 정규화해 라인 배열로 만든다. null은 빈 줄 하나로 취급한다.</summary>
        public static List<string> SplitLines(string? markdown) =>
            (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();

        /// <summary>
        /// 펜스(```)로 둘러싸인 줄은 건너뛰고 조건을 만족하는 첫 줄의 인덱스를 찾는다.
        ///
        /// 펜스가 끝까지 닫히지 않으면(모델이 ``` 하나를 빠뜨린 경우) inFence가 참인 채로
        /// 스캔이 끝난다 - 그러면 이후 모든 줄이 "펜스 안"으로 오인되어 경계를 영영 못 찾고
        /// 한 섹션이 문서 나머지 전부를 삼킨다. 이 경우 펜스 상태를 신뢰할 수 없으므로
        /// 펜스를 무시하고 다시 스캔한다 - 오탐(코드 안의 헤딩)보다 미탐(전체 삼킴)이
        /// 훨씬 나쁘다.
        /// </summary>
        public static int FindIndexOutsideFence(
            IReadOnlyList<string> lines, int startIndex, Func<string, bool> predicate)
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

        /// <summary>
        /// 지정한 헤딩 줄의 인덱스와, 그 섹션이 끝나는(= 다음 경계 헤딩이 시작하는) 인덱스를
        /// 돌려준다. 헤딩이 없으면 (-1, -1). 다음 경계가 없으면 EndIndex는 문서 끝이다.
        /// </summary>
        /// <param name="headingLine">찾을 헤딩 줄 전체 (예: "## 단계별 이행 상세 및 의사코드").</param>
        /// <param name="boundaryPrefix">섹션의 끝을 정하는 헤딩 접두 (예: "## ").</param>
        public static (int HeaderIndex, int EndIndex) LocateSection(
            IReadOnlyList<string> lines, string headingLine, string boundaryPrefix)
        {
            var headerIndex = FindIndexOutsideFence(lines, 0, line => line.Trim() == headingLine);
            if (headerIndex < 0)
            {
                return (-1, -1);
            }

            var endIndex = FindIndexOutsideFence(
                lines,
                headerIndex + 1,
                line => line.TrimStart().StartsWith(boundaryPrefix, StringComparison.Ordinal));

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }
    }
}
