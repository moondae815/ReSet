using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 귀속 결과. StepCodes가 비고 HasUnattributed가 참이면 "고칠 단계가 없다"가 아니라
    /// "어디를 고쳐야 할지 모른다"는 뜻이다. 둘을 한 필드로 겸하면 후자가 전자로 읽혀
    /// 누락이 조용히 사라진다.
    /// </summary>
    public sealed record ErrorCodeAttributionResult(
        IReadOnlyList<string> StepCodes,
        bool HasUnattributed);

    /// <summary>
    /// 문서에서 빠진 원본 오류 코드를 그 코드를 선언한 단계로 되돌린다.
    ///
    /// 이 클래스가 존재하는 이유: 누락 자체는 MechanicalValidator가 결정적으로 잡지만,
    /// 그 결과가 "문서 어딘가"라서 문서 전체를 다시 만들게 했다. 목차(BatchStepPlan)는
    /// 단계별 ErrorCodes를 이미 들고 있으므로 좌표를 복원할 수 있다.
    ///
    /// 귀속하지 못하는 것을 억지로 붙이지 않는다. 잘못 귀속하면 멀쩡한 단계를 다시 쓰게
    /// 되어, 회귀 롤백이 막으려는 회귀를 다시 들인다.
    /// </summary>
    public static class ErrorCodeAttribution
    {
        public static ErrorCodeAttributionResult Attribute(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? missingByProcedure,
            IReadOnlyList<BatchStepPlan>? steps)
        {
            if (missingByProcedure == null || missingByProcedure.Count == 0)
            {
                return new ErrorCodeAttributionResult(Array.Empty<string>(), false);
            }

            // 목차가 없으면 귀속할 좌표가 없다. 빈 목록만 돌려주면 "고칠 단계가 없다"로
            // 읽히므로 미귀속을 함께 알린다.
            if (steps == null || steps.Count == 0)
            {
                return new ErrorCodeAttributionResult(Array.Empty<string>(), true);
            }

            var attributed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var unattributed = false;

            foreach (var codes in missingByProcedure.Values)
            {
                foreach (var raw in codes)
                {
                    var code = raw?.Trim();
                    if (string.IsNullOrEmpty(code)) continue;

                    var owners = steps
                        .Where(step => step.ErrorCodes.Any(declared =>
                            string.Equals(declared?.Trim(), code, StringComparison.OrdinalIgnoreCase)))
                        .Select(step => step.Code)
                        .ToList();

                    if (owners.Count == 0)
                    {
                        // 어느 단계도 이 코드를 맡겠다고 선언하지 않았다. 목차 결함이다.
                        unattributed = true;
                        continue;
                    }

                    // 둘 이상이면 좁히지 않는다 - 어느 쪽이 빠뜨렸는지 모른다.
                    foreach (var owner in owners) attributed.Add(owner);
                }
            }

            return new ErrorCodeAttributionResult(attributed.ToList(), unattributed);
        }
    }
}
