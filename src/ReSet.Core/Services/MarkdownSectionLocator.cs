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
        /// <param name="exact">
        /// 참이면 헤딩 줄이 <paramref name="headingLine"/>과 완전히 같아야 한다(기본값, 종전 동작).
        /// 거짓이면 <paramref name="boundaryPrefix"/>로 시작하면서 그 텍스트를 포함하는 헤딩도 받는다.
        ///
        /// [왜 이 옵션이 필요한가]
        /// 모델이 쓰는 헤딩은 계약대로 나오지 않는다. 실측에서 두 가지 변형이 반복됐다 -
        /// 접두가 붙는 형태(`## 3. CRUD 분석`)와 꼬리표가 붙는 형태
        /// (`## 단계별 이행 상세 및 의사코드:`). 전자는 <see cref="MechanicalValidator"/>가
        /// CRUD 절 대조를 조용히 끄는 사고를 냈고, 후자는 <see cref="BatchPlanAssembler"/>가
        /// 단계 블록을 못 찾아 문서 끝에 같은 H2를 새로 합성하게 만들었다(POQSettleProc17·18
        /// 연속 재발). 두 소비자가 각자 폴백을 손으로 쓰면 판정이 갈리므로 여기에 둔다.
        ///
        /// 기본값을 정확 일치로 두는 이유: 넓히는 판정을 기본으로 삼으면 이 변경이 닿을
        /// 의도가 없던 호출부까지 함께 움직인다. 넓힐 자리는 호출부가 명시한다.
        /// </param>
        public static (int HeaderIndex, int EndIndex) LocateSection(
            IReadOnlyList<string> lines, string headingLine, string boundaryPrefix, bool exact = true)
        {
            var headerIndex = FindIndexOutsideFence(
                lines,
                0,
                exact
                    ? line => line.Trim() == headingLine
                    : line => line.TrimStart().StartsWith(boundaryPrefix, StringComparison.Ordinal) &&
                              line.Contains(HeadingText(headingLine), StringComparison.OrdinalIgnoreCase));
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

        /// <summary>`## 제목`에서 `#`과 공백을 걷어낸 제목 부분. 느슨 매칭이 대조하는 것은 이 텍스트다.</summary>
        private static string HeadingText(string headingLine) =>
            headingLine.TrimStart().TrimStart('#').Trim();

        /// <summary>
        /// 각 줄이 코드 펜스 안에 있는지를 <see cref="FindIndexOutsideFence"/>와 같은 원칙으로
        /// 미리 계산한다. 단일 조건을 찾는 <see cref="FindIndexOutsideFence"/>는 여러 줄을
        /// 순회하며 어휘도 찾고 헤딩도 찾아야 하는 호출부(예: L1ViolationAttribution)에는
        /// 맞지 않는다 - 매 줄마다 처음부터 다시 스캔하게 되거나, 펜스 판정을 호출부가
        /// 직접 다시 구현하게 된다. 후자는 이 클래스의 존재 이유(펜스 미닫힘 폴백을
        /// 두 곳이 각자 갖는 사고)를 그대로 반복한다.
        ///
        /// 펜스가 끝까지 닫히지 않으면 배열 전체를 false로 되돌린다 -
        /// <see cref="FindIndexOutsideFence"/>가 펜스 상태를 신뢰할 수 없을 때 펜스를 무시하고
        /// 다시 스캔하는 것과 같은 이유다: 오탐(코드 안의 헤딩)보다 미탐(이후 모든 헤딩을
        /// 놓쳐 한 섹션이 문서 나머지 전부를 삼키는 것)이 훨씬 나쁘다.
        /// </summary>
        public static IReadOnlyList<bool> ComputeFenceFlags(IReadOnlyList<string> lines)
        {
            var flags = new bool[lines.Count];
            var inFence = false;

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    // 여닫는 줄 자체는 펜스 토글 마커이지 본문이 아니므로 펜스 밖으로 둔다.
                    inFence = !inFence;
                    flags[i] = false;
                    continue;
                }

                flags[i] = inFence;
            }

            if (inFence)
            {
                Array.Clear(flags, 0, flags.Length);
            }

            return flags;
        }
    }
}
