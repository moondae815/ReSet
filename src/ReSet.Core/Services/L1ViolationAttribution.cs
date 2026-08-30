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
        /// 어휘가 처음 나타나는 자리를 감싼 단계 헤딩의 단계 코드를 돌려준다.
        ///
        /// 코드 펜스 안을 건너뛰지 않는 이유: 위반 어휘 자체가 대개 SQL 코드 블록
        /// 안에 있다(`END TRY`가 그렇다). 헤딩 탐지만 펜스를 존중한다 -
        /// MarkdownSectionLocator.ComputeFenceFlags로 줄마다 펜스 여부를 미리 계산해
        /// 헤딩 후보 판정에서만 펜스 안 줄을 걸러낸다. 펜스 안에 `###`로 시작하는 줄이
        /// 있어도(예: SQL 주석) 그것은 진짜 단계 헤딩이 아니다.
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
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            string? currentStep = null;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (!fenceFlags[i])
                {
                    var heading = TryReadStepHeading(line, steps);
                    if (heading != null)
                    {
                        currentStep = heading;
                        continue;
                    }
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
        /// `### S02. 이름` 또는 `#### S02. 이름` 꼴에서 목차가 아는 단계 코드를 읽는다.
        ///
        /// 헤딩 레벨을 `###`로 고정하지 않는 이유: 실측 산출물이 이미 갈린다
        /// (BatchStepPlan 참고) - 한쪽은 단계를 H3에, 다른 쪽은 H4에 두면서 같은 H4에
        /// 단계가 아닌 헤딩(`#### Phase 1.`)을 섞는다. 레벨로 가르는 대신 "헤딩이 선언하는
        /// 선행 코드가 정확히 무엇인가"로 가른다 - `#### Phase 1.`은 선행 토큰이
        /// "Phase"라 어떤 단계 코드와도 같지 않으므로 자연히 걸러진다.
        ///
        /// 선행 토큰만 보는 이유(부분 문자열 포함 판정을 쓰지 않는 이유):
        /// PlanBoundaryResolver.TryLocateByCode가 이미 겪은 함정이다 - "### S02 (S01 이후)"
        /// 같은 헤딩에서 본문에 언급된 다른 단계 코드(S01)가 먼저 걸리면 그 단계로 잘못
        /// 귀속되고, 억지 귀속은 멀쩡한 단계를 다시 쓰게 만든다. 헤딩이 스스로 선언하는
        /// 선행 코드만 인정하면 이 함정을 원천에서 피한다.
        ///
        /// 선행 토큰이 어느 코드와도 정확히 같지 않으면(하나로 판정할 수 없으면) null이다 -
        /// 목차에 없는 코드와 같은 이유로, 판정 불가한 헤딩은 경계로 쓰지 않는다.
        /// </summary>
        private static string? TryReadStepHeading(string line, IReadOnlyList<BatchStepPlan> steps)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal)) return null;

            var afterMarker = trimmed.TrimStart('#').TrimStart();
            var leadingToken = afterMarker
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(leadingToken)) return null;

            var normalizedToken = leadingToken.Trim('.', ':', ')', ',', ';');

            return steps
                .Select(step => step.Code)
                .FirstOrDefault(code =>
                    !string.IsNullOrWhiteSpace(code) &&
                    string.Equals(code, normalizedToken, StringComparison.OrdinalIgnoreCase));
        }
    }
}
