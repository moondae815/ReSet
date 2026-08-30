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
    /// 귀속하지 못하면 빈 목록을 돌려준다. 억지로 아무 단계에나 붙이면 멀쩡한 단계를
    /// 다시 쓰게 되어, 회귀 롤백이 막으려는 회귀를 다시 들인다. 호출부는 빈 목록을
    /// "전량 재생성"으로 읽는다.
    ///
    /// 한 어휘가 문서 안 여러 단계 섹션에 나타나면 그 단계 전부를 담는다 - 처음
    /// 발견한 단계 하나로 멈추지 않는다(최종 whole-branch 리뷰, Important 5 참고).
    /// </summary>
    public static class L1ViolationAttribution
    {
        /// <summary>
        /// 어휘가 나타나는 모든 자리를 감싼 단계 헤딩들의 단계 코드를 돌려준다(중복 없이,
        /// 처음 나온 순서대로). 하나만 돌려주면 안 되는 이유(최종 whole-branch 리뷰,
        /// Important 5): 규칙 3-1(`BEGIN TRY`/`END TRY`)·규칙 10류 위반은 체계적이다 -
        /// 모델이 한 단계에서 그렇게 쓰면 보통 여러 단계에서 그렇게 쓴다.
        /// `MechanicalValidator`는 검사당 <c>DetailedError</c> 하나만 내므로(발생당이
        /// 아니다) 이 메서드가 첫 발견에서 멈추면 나머지 위반 단계는 <c>StepFreezeState</c>가
        /// 영영 열지 않는다 - L1이 다음 회차에도 같은 위반으로 다시 실패하면서
        /// Job 전체 예산인 <c>l1RepairAttempt</c>만 태우다가 소진된다.
        ///
        /// 코드 펜스 안을 건너뛰지 않는 이유: 위반 어휘 자체가 대개 SQL 코드 블록
        /// 안에 있다(`END TRY`가 그렇다). 헤딩 탐지만 펜스를 존중한다 -
        /// MarkdownSectionLocator.ComputeFenceFlags로 줄마다 펜스 여부를 미리 계산해
        /// 헤딩 후보 판정에서만 펜스 안 줄을 걸러낸다. 펜스 안에 `###`로 시작하는 줄이
        /// 있어도(예: SQL 주석) 그것은 진짜 단계 헤딩이 아니다.
        /// </summary>
        public static IReadOnlyList<string> AttributeByLexeme(
            string? documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps)
        {
            if (string.IsNullOrEmpty(documentMarkdown) ||
                string.IsNullOrWhiteSpace(lexeme) ||
                steps == null || steps.Count == 0)
            {
                return Array.Empty<string>();
            }

            var lines = MarkdownSectionLocator.SplitLines(documentMarkdown);
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            string? currentStep = null;
            var attributedSteps = new List<string>();

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (!fenceFlags[i])
                {
                    var (isHeading, code) = ReadStepHeading(line, steps);
                    if (isHeading)
                    {
                        // 헤딩을 만나면 항상 갱신한다 - 코드가 확정되면 그 코드로,
                        // 확정되지 않으면(아래 ReadStepHeading 참고) null로 리셋한다.
                        // "헤딩인데 판정 불가"를 "헤딩이 아니다"와 같게 다루면(= 갱신하지
                        // 않으면) 직전의 무관한 단계가 currentStep에 그대로 남아, 그 뒤
                        // 어휘가 엉뚱한 단계로 새어 들어간다 - 억지 귀속과 같은 결과다.
                        currentStep = code;
                        continue;
                    }
                }

                if (line.IndexOf(lexeme, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    currentStep != null &&
                    !attributedSteps.Contains(currentStep, StringComparer.OrdinalIgnoreCase))
                {
                    // currentStep이 null이면 아직 어떤 단계 섹션에도 들어가지 않았거나
                    // (공통 규약 절), 판정 불가한 헤딩 아래로 들어와 리셋됐다는 뜻이다.
                    // 어느 쪽이든 단계에 억지로 붙이지 않는다 - 이 발생은 그냥 건너뛰고
                    // 스캔을 계속한다(멈추지 않는다). 뒤에 나오는 판정 가능한 단계까지
                    // 이 발생 하나 때문에 포기하면 안 된다.
                    attributedSteps.Add(currentStep);
                }
            }

            return attributedSteps;
        }

        /// <summary>
        /// 한 줄이 단계 헤딩인지, 헤딩이라면 어느 단계 코드를 선언하는지를 함께 돌려준다.
        ///
        /// 반환을 <c>(IsHeading, Code)</c> 둘로 나누는 이유: 호출부가 세 상태를 구분해야
        /// 한다 - 헤딩이 아니면 currentStep을 그대로 두고, 헤딩이면서 코드가 확정되면
        /// 그 코드로 갱신하고, 헤딩인데 코드를 확정할 수 없으면(<c>IsHeading=true,
        /// Code=null</c>) currentStep을 리셋해야 한다. 코드 하나만 돌려주면(이전 버전처럼)
        /// 뒤의 두 경우가 똑같이 "null"이 되어 구분이 사라진다 - 실측(BatchStepPlan.cs
        /// 주석)의 "### P20~P23." 같은 여러 단계를 묶은 헤딩이 그 구분 없이는 직전의
        /// 무관한 단계(P19)로 잘못 귀속됐다.
        ///
        /// `### S02. 이름` 또는 `#### S02. 이름` 꼴에서 목차가 아는 단계 코드를 읽는다.
        ///
        /// 헤딩 레벨을 `###`로 고정하지 않는 이유: 실측 산출물이 이미 갈린다
        /// (BatchStepPlan 참고) - 한쪽은 단계를 H3에, 다른 쪽은 H4에 두면서 같은 H4에
        /// 단계가 아닌 헤딩(`#### Phase 1.`)을 섞는다. 레벨로 가르는 대신 "헤딩이 선언하는
        /// 선행 코드가 정확히 무엇인가"로 가른다 - `#### Phase 1.`은 선행 토큰이
        /// "Phase"라 어떤 단계 코드와도 같지 않으므로 자연히 걸러진다(Code=null이지만
        /// IsHeading=true이므로 currentStep은 리셋된다).
        ///
        /// 선행 토큰만 보는 이유(부분 문자열 포함 판정을 쓰지 않는 이유):
        /// PlanBoundaryResolver.TryLocateByCode가 이미 겪은 함정이다 - "### S02 (S01 이후)"
        /// 같은 헤딩에서 본문에 언급된 다른 단계 코드(S01)가 먼저 걸리면 그 단계로 잘못
        /// 귀속되고, 억지 귀속은 멀쩡한 단계를 다시 쓰게 만든다. 헤딩이 스스로 선언하는
        /// 선행 코드만 인정하면 이 함정을 원천에서 피한다.
        ///
        /// 선행 토큰이 어느 코드와도 정확히 같지 않으면(하나로 판정할 수 없으면) Code는
        /// null이다 - "### P20~P23."처럼 여러 단계를 묶은 헤딩, 목차에 없는 코드, 코드와
        /// 무관한 하위 헤딩이 모두 이 경우다.
        /// </summary>
        private static (bool IsHeading, string? Code) ReadStepHeading(
            string line, IReadOnlyList<BatchStepPlan> steps)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal)) return (false, null);

            var afterMarker = trimmed.TrimStart('#').TrimStart();
            var leadingToken = afterMarker
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(leadingToken)) return (true, null);

            var normalizedToken = leadingToken.Trim('.', ':', ')', ',', ';');

            var code = steps
                .Select(step => step.Code)
                .FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c) &&
                    string.Equals(c, normalizedToken, StringComparison.OrdinalIgnoreCase));

            return (true, code);
        }
    }
}
