using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 이번 회차에 다시 생성할 단계를 정한다. 나머지는 동결되어 직전 본문이
    /// 바이트 그대로 재사용된다.
    ///
    /// 이 클래스가 존재하는 이유: 회차마다 통과한 단계까지 다시 쓰이며 새 결함이
    /// 들어왔다. 실측(POQSettleBatch4)에서 6차가 5차 대비 정합성 8->7, 예외 7->6으로
    /// 떨어진 것이 그 결과다.
    ///
    /// 동결은 셋의 AND다 - 하한 검사 통과 · 오류 코드 검사 통과 · Critic 미지목.
    /// 확률적인 신호(Critic) 하나에만 맡기지 않는 것이 요점이다. 기계가 아는 결함은
    /// 동결되지 않는다.
    ///
    /// 단 하나의 예외가 Unverifiable 이다 - 재생성으로 고쳐지지 않는 판정이므로
    /// 열어 두면 예산만 태운다. 아래 루프의 주석 참조.
    /// </summary>
    public static class StepFreezeState
    {
        /// <summary>
        /// 다시 생성할 단계 코드를 목차 순서로 돌려준다.
        ///
        /// null을 돌려주는 경우: 목차가 단계 목록을 내지 못했다. 빈 목록을 돌려주면
        /// "고칠 것이 없다"로 읽혀 결함이 조용히 남으므로, 호출부가 전량 재생성을
        /// 택할 수 있도록 없음과 구분한다.
        /// </summary>
        public static IReadOnlyList<string>? OpenSteps(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyCollection<string> criticDefectiveSteps,
            IReadOnlyDictionary<string, StepDefect> floorViolations,
            IReadOnlyList<string> errorCodeSteps)
        {
            if (steps == null || steps.Count == 0)
            {
                return null;
            }

            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var code in criticDefectiveSteps ?? Array.Empty<string>()) open.Add(code);
            foreach (var code in errorCodeSteps ?? Array.Empty<string>()) open.Add(code);

            // 하한 위반 중 재생성으로 고칠 수 있는 것만 연다.
            //
            // Unverifiable 을 빼는 이유: 그것은 "대조할 재료가 목차에 없어 검사가 돌지
            // 못했다"이지 "본문이 나쁘다"가 아니다(StepDefectKind 의 주석이 "재생성으로
            // 고쳐지지 않는다"고 명시한다). 열어 두면 매 회차 같은 단계를 다시 뽑는데
            // 판정은 영원히 그대로다 - 예산만 태우고 새 결함을 들인다. 재생성이 못 고치는
            // 것은 루프가 아니라 배너가 처리한다(설계서 §3-7).
            //
            // Critic 이 그 단계를 따로 지목했다면 위에서 이미 열렸다 - 재료가 없는 것과
            // 본문에 결함이 있는 것은 별개다.
            foreach (var (code, defect) in floorViolations ?? new Dictionary<string, StepDefect>())
            {
                if (defect.Kind != StepDefectKind.Unverifiable) open.Add(code);
            }

            // 목차 순서로 투영한다. HashSet 열거 순서에 맡기면 회차마다 생성 순서가
            // 달라져 로그 대조가 불가능해진다. 목차에 없는 코드는 여기서 자연히 빠진다 -
            // 생성할 대상이 없기 때문이다.
            return steps
                .Where(step => open.Contains(step.Code))
                .Select(step => step.Code)
                .ToList();
        }
    }
}
