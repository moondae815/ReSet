using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Models
{
    /// <summary>
    /// 이 산출물이 실제로 받은 기계 검증의 양.
    ///
    /// 점수(<see cref="ReviewResult"/>)와 나란히 놓이지만 다른 것을 잰다. 점수는
    /// 읽어 본 품질이고 이것은 대조해 본 분량이다. 실측 세 회차에서 둘이 정반대로
    /// 움직였다 - 원본 오류코드 76개 중 20개가 사라진 문서가 92점, 76개를 모두
    /// 지킨 문서가 88점이었다. Critic은 읽기 좋음을 보고 없는 것은 보지 못한다.
    /// </summary>
    /// <param name="StepsTotal">
    /// 채택된 목차의 단계 수. <c>null</c>은 분할이 실행되지 않았다는 뜻이며,
    /// <c>0</c>과 다르다 - 분모가 없는 상태를 0으로 적으면 비율처럼 보이는 거짓이
    /// 된다.
    /// </param>
    /// <param name="StepsVerified">하한 검사를 실제로 실행한 단계 수.</param>
    /// <param name="HasDocumentCodeGap">원본 오류코드 중 문서 어디에도 없는 것이 있는가.</param>
    /// <param name="HasUncoveredProcedures">
    /// 목차가 원본 프로시저 커버리지를 밝히지 못했는가. 오케스트레이터의
    /// <c>CoverageUnverifiable</c>("모든 단계가 출신을 비워 대조 자체를 못
    /// 돌렸다")과 <c>UncoveredProcedures</c>("일부 프로시저가 어느 단계에도
    /// 없다") 두 배너가 이 플래그 하나로 합쳐진다. 두 조건은 오케스트레이터에서
    /// if/else if로 이미 상호 배타적이므로 - "대조를 아예 못 돌렸다"와 "돌렸는데
    /// 빠졌다"는 같은 순간에 참일 수 없다 - 배너별로 필드를 나눠도 의미가
    /// 갈리지 않고, 소비자(§0)가 구분해서 다르게 말할 필요도 없다.
    /// </param>
    public sealed record VerificationCoverage(
        int? StepsTotal,
        int StepsVerified,
        bool HasDocumentCodeGap,
        bool HasUncoveredProcedures)
    {
        /// <summary>분할 생성이 실행되어 단계 단위 검증이 성립했는가.</summary>
        public bool SplitRan => StepsTotal.HasValue;

        /// <summary>대조할 재료가 없어 검사를 못 돌린 단계가 남았는가.</summary>
        public bool HasUnverifiedSteps => StepsTotal.HasValue && StepsVerified < StepsTotal.Value;

        /// <summary>
        /// 종료 상태가 Passed라도 사람이 봐야 하는가. 네 사유가 각자 독립적으로 발화한다.
        /// </summary>
        public bool NeedsHumanAttention =>
            !SplitRan || HasUnverifiedSteps || HasDocumentCodeGap || HasUncoveredProcedures;

        /// <summary>
        /// 파이프라인이 들고 있는 재료에서 커버리지를 만든다.
        ///
        /// <see cref="StepDefectKind.QualityFloor"/>는 빼지 않는다. 그것은 검사가
        /// 돌았고 떨어진 것이라 "검사를 실행했다"에 속한다. 두 종류를 합치면
        /// StepDefectKind가 가르려고 만들어진 구분이 여기서 다시 무너진다.
        /// </summary>
        public static VerificationCoverage From(
            IReadOnlyList<BatchStepPlan>? adoptedSteps,
            IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
            bool hasDocumentCodeGap,
            bool hasUncoveredProcedures)
        {
            if (adoptedSteps == null)
            {
                return new VerificationCoverage(null, 0, hasDocumentCodeGap, hasUncoveredProcedures);
            }

            var unverifiable = stepFloorViolations?
                .Values.Count(defect => defect.Kind == StepDefectKind.Unverifiable) ?? 0;

            var verified = adoptedSteps.Count - unverifiable;
            return new VerificationCoverage(
                adoptedSteps.Count,
                verified < 0 ? 0 : verified,
                hasDocumentCodeGap,
                hasUncoveredProcedures);
        }
    }
}
