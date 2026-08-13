using System;
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
    /// <param name="StepsVerified">
    /// 하한 검사를 실제로 실행한 단계 수. 대조할 재료가 없었거나
    /// (<see cref="StepDefectKind.Unverifiable"/>) 본문이 생성되지 않은
    /// (<see cref="StepDefectKind.GenerationFailed"/>) 단계는 빠진다.
    /// <see cref="StepDefectKind.QualityFloor"/>는 빠지지 않는다 - 검사가 돌았고
    /// 떨어진 것이라 실행된 쪽에 속한다.
    /// </param>
    /// <param name="HasDocumentCodeGap">원본 오류코드 중 문서 어디에도 없는 것이 있는가.</param>
    /// <param name="HasUncoveredProcedures">
    /// 목차가 원본 프로시저 커버리지를 확인해 주지 못했는가. 오케스트레이터의
    /// <c>CoverageUnverifiable</c>("모든 단계가 출신을 비워 대조 자체를 못
    /// 돌렸다")과 <c>UncoveredProcedures</c>("대조는 돌았지만 일부 프로시저가
    /// 어느 단계에도 없다") 두 배너가 이 플래그 하나로 합쳐진다. 필드를 하나로
    /// 합쳐도 되는 것은 오케스트레이터에서 두 조건이 if/else if로 이미 상호
    /// 배타적이기 때문이다 - "대조를 아예 못 돌렸다"와 "돌렸는데 빠졌다"는 같은
    /// 순간에 참일 수 없다. 그러나 그 사실이 "한 문장으로 둘 다 정확히 말할 수
    /// 있다"는 뜻은 아니다 - 상호 배타성은 어느 쪽이 발화했는지를 보장할 뿐, 그
    /// 발화의 서술이 두 상태 모두에서 참인지는 별도로 확인해야 한다. 실제로 첫
    /// 시도("프로시저가 나타나지 않았다")는 <c>CoverageUnverifiable</c> 쪽에서
    /// 거짓이었다 - 그 배너 자체가 "문서가 그 프로시저들을 다루지 않았다는 뜻은
    /// 아닙니다"라고 명시하는데, 부재를 단정하는 문장은 정확히 그 오해를
    /// 재현한다. 이 필드를 소비하는 §0(<see
    /// cref="InstructionEntryPointComposer.PlanVerificationSection"/>)은
    /// "확인되지 않았다"처럼 두 상태 모두에서 참인 문구만 써야 한다.
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

            // 채택된 목차에 있는 단계의 위반만 센다. stepFloorViolations는 회차별
            // 스냅샷이고 adoptedSteps는 채택 확정 후 다시 파싱한 값이라, 구제 채택이
            // 이전 회차 문서를 되살리면 두 집합이 어긋날 수 있다. 그때 위반 수를
            // 그대로 빼면 존재하지도 않는 단계를 미검증으로 세어 비율이 틀어진다.
            //
            // 교집합을 취하면 검증 수가 구조적으로 음수가 될 수 없다. 종전의 음수
            // 클램프는 이 불일치를 "0 검증"이라는 그럴듯한 값으로 덮고 있었다.
            var adoptedCodes = new HashSet<string>(
                adoptedSteps.Select(step => step.Code), StringComparer.OrdinalIgnoreCase);

            var notVerified = stepFloorViolations
                .Count(entry =>
                    adoptedCodes.Contains(entry.Key)
                    && entry.Value.Kind is StepDefectKind.Unverifiable or StepDefectKind.GenerationFailed);

            return new VerificationCoverage(
                adoptedSteps.Count,
                adoptedSteps.Count - notVerified,
                hasDocumentCodeGap,
                hasUncoveredProcedures);
        }
    }
}
