using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// L1 위반이 어느 단계 섹션 안에서 일어났는지 찾는다.
    ///
    /// 이 클래스가 존재하는 이유: 실측(POQSettleBatch4 2026-08-29)의 3차 L1 실패는
    /// 규칙 3-1 위반 `END TRY` 하나였고 4차는 `batch.BatchRun` INSERT 부재였다.
    /// 지점이 특정되는 결함인데도 문서 전체를 다시 만들었고, 그렇게 두 회차를 태웠다.
    ///
    /// 귀속하지 못하면 null을 돌려준다. 억지로 아무 단계에나 붙이면 멀쩡한 단계를
    /// 다시 쓰게 되어, 회귀 롤백이 막으려는 회귀를 다시 들인다. 호출부는 null을
    /// "전량 재생성"으로 읽는다.
    /// </summary>
    public static class L1ViolationAttribution
    {
        /// <summary>
        /// 어휘가 처음 나타나는 자리를 감싼 `###` 단계 헤딩의 단계 코드를 돌려준다.
        ///
        /// 코드 펜스 안을 건너뛰지 않는 이유: 위반 어휘 자체가 대개 SQL 코드 블록
        /// 안에 있다(`END TRY`가 그렇다). 헤딩 탐지만 펜스를 존중한다 -
        /// MarkdownSectionLocator가 이미 그 판정을 소유한다.
        /// </summary>
        public static string? AttributeByLexeme(
            string? documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps)
        {
            if (string.IsNullOrEmpty(documentMarkdown) ||
                string.IsNullOrWhiteSpace(lexeme) ||
                steps == null || steps.Count == 0)
            {
                return null;
            }

            var lines = MarkdownSectionLocator.SplitLines(documentMarkdown);
            string? currentStep = null;

            foreach (var line in lines)
            {
                var heading = TryReadStepHeading(line, steps);
                if (heading != null)
                {
                    currentStep = heading;
                    continue;
                }

                if (line.IndexOf(lexeme, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // currentStep이 null이면 아직 어떤 단계 섹션에도 들어가지 않았다는 뜻 -
                    // 공통 규약 절의 어휘이므로 단계에 붙이지 않는다.
                    return currentStep;
                }
            }

            return null;
        }

        /// <summary>
        /// `### S02. 이름` 꼴에서 목차가 아는 단계 코드를 읽는다. 목차에 없는 코드는
        /// null이다 - 우리가 아는 단계가 아니면 귀속의 근거가 없다.
        /// </summary>
        private static string? TryReadStepHeading(string line, IReadOnlyList<BatchStepPlan> steps)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("###", StringComparison.Ordinal)) return null;

            return steps
                .Select(step => step.Code)
                .FirstOrDefault(code =>
                    !string.IsNullOrWhiteSpace(code) &&
                    trimmed.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
