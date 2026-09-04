using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 골격 수리 호출 하나가 싣는 것. 「무엇을 고쳐라」(<paramref name="Feedback"/>)와
    /// 「무엇 위에서 고쳐라」(<paramref name="PreviousSkeleton"/>)는 항상 함께 움직인다 -
    /// 둘을 따로 된 매개변수로 두면 "피드백은 있는데 직전 본문이 없다"는 조합이 실수로
    /// 만들어져도 컴파일이 통과한다. 그 조합은 백지 재작성인데, 백지는 에스컬레이션에서
    /// <b>의도적으로</b> 고르는 것이라 실수와 구분돼야 한다.
    ///
    /// <paramref name="PreviousSkeleton"/>이 null이면 백지 재작성이다 - 같은 결함으로
    /// 연속 2회 지목된 골격에 쓴다(§3-8의 단계 패치 에스컬레이션과 같은 규칙).
    /// 「최소 변경만 하고 근본 결함을 안 고친다」가 패치 고유의 실패 모드이고, 그
    /// 상태로 수리 예산을 계속 태우면 안 되기 때문이다.
    /// </summary>
    public sealed record SkeletonRevision(string Feedback, string? PreviousSkeleton);

    /// <summary>
    /// 골격 패치 재생성이 「지적된 자리만 고치고 나머지는 바이트 그대로」를 지켰는지
    /// 기계로 본다.
    ///
    /// [왜 프롬프트 계약만으로는 안 되는가]
    /// 골격만 다시 만들 때 단계 섹션은 동결된 채 남는다. 그런데 골격은 <b>단계 목록
    /// 표</b>와 <b>단계별 허용 오류 코드 표</b>를 지고 있어서, 다시 쓰다가 그 표가
    /// 흔들리면 문서가 스스로 모순된다 - 표에서 사라진 오류 코드는 다음 회차의 L1
    /// (오류 코드 누락)으로 나타나고, 그 귀속은 코드를 선언한 <b>멀쩡한 단계</b>를 연다.
    /// 즉 골격을 고치려다 회귀 롤백이 막으려는 회귀를 들이게 된다. 이 저장소에서
    /// 프롬프트 계약만으로 서 있던 것은 조용히 무너진 전례가 있다(규칙 3-1·10이
    /// 기계 강제 0건이던 시절).
    ///
    /// [기준이 목차가 아니라 직전 골격인 이유]
    /// 요구하는 것은 「직전 골격이 실제로 담고 있던 앵커」뿐이다. 목차가 아는 전부를
    /// 요구하면 애초에 빠져 있던 것을 패치가 새로 만들어 내라고 조르게 되고, 그것은
    /// 정합 검사가 아니라 새 요구다 - 한 번의 패치로 닫을 수 없는 것을 반복해서
    /// 요구하면 수리 예산만 태운다. 애초에 빠져 있던 앵커는 이 가드가 아니라 L1의
    /// 오류 코드 누락 검사가 자기 경로로 잡는다.
    /// </summary>
    public static class SkeletonRevisionGuard
    {
        /// <summary>
        /// 직전 골격에는 있었는데 고쳐진 골격에서 사라진 앵커를 돌려준다. 순서는
        /// 결정적이다(필수 H2 → 단계 코드 → 오류 코드, 각각 목차 순서).
        ///
        /// 앵커 셋의 근거:
        /// <list type="bullet">
        /// <item>필수 H2 네 개 - 하나라도 빠지면 L1 <c>HeaderMissing</c>이고, 그중
        /// 단계 상세 H2는 <see cref="BatchPlanAssembler"/>가 합성으로 메워 버려
        /// 결함이 문서 레벨 L1에게도 안 보인다.</item>
        /// <item>단계 코드 - 동결된 단계 섹션이 그대로 붙는데 골격의 단계 목록에
        /// 그 단계가 없으면 문서가 자기 모순이다.</item>
        /// <item>오류 코드 - 위 요약이 적은 그대로다.</item>
        /// </list>
        ///
        /// 코드 대조는 <see cref="MechanicalValidator.ContainsToken"/>을 그대로 쓴다.
        /// 부분 문자열로 보면 `-1`이 `-10` 안에서 걸려 가드가 조용히 무력해진다.
        /// </summary>
        public static IReadOnlyList<string> FindLostAnchors(
            string? previousSkeleton, string? revisedSkeleton, IReadOnlyList<BatchStepPlan>? steps)
        {
            var lost = new List<string>();
            if (string.IsNullOrWhiteSpace(previousSkeleton) || string.IsNullOrWhiteSpace(revisedSkeleton))
            {
                return lost;
            }

            foreach (var header in MechanicalValidator.RequiredConsolidatedHeaders)
            {
                if (previousSkeleton.Contains(header, StringComparison.OrdinalIgnoreCase) &&
                    !revisedSkeleton.Contains(header, StringComparison.OrdinalIgnoreCase))
                {
                    lost.Add($"필수 섹션 `## {header}`");
                }
            }

            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps ?? Array.Empty<BatchStepPlan>())
            {
                if (string.IsNullOrWhiteSpace(step.Code) || !seenCodes.Add(step.Code)) continue;
                if (MechanicalValidator.ContainsToken(previousSkeleton, step.Code) &&
                    !MechanicalValidator.ContainsToken(revisedSkeleton, step.Code))
                {
                    lost.Add($"단계 `{step.Code}`");
                }
            }

            var seenErrorCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps ?? Array.Empty<BatchStepPlan>())
            {
                foreach (var rawCode in step.ErrorCodes ?? Array.Empty<string>())
                {
                    var code = (rawCode ?? string.Empty).Trim();
                    if (code.Length == 0 || !seenErrorCodes.Add(code)) continue;
                    if (MechanicalValidator.ContainsToken(previousSkeleton, code) &&
                        !MechanicalValidator.ContainsToken(revisedSkeleton, code))
                    {
                        lost.Add($"오류 코드 `{code}`");
                    }
                }
            }

            return lost;
        }
    }
}
