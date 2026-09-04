using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 다중 선택 UI에 뿌린 "표시 문자열"을 <see cref="PrdTarget"/>으로 정확히 되돌리는 헬퍼.
    ///
    /// 실제 output/Procedures 코퍼스에는 한 객체의 Label이 다른 객체 Label의 접두어가 되는
    /// 사례가 있다 - 예: "dbo.UP_UTIL_SETTLE_INS"는 "dbo.UP_UTIL_SETTLE_INS_EXTRA"의 접두어이고,
    /// 그 자체가 다시 "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD"의 접두어다. 이런 코퍼스에서
    /// StartsWith로 되짚으면 사용자가 마지막 것 하나만 골라도 앞의 둘까지 함께 걸려
    /// (그중엔 기존 Prd.md를 가진 것도 있어) 고르지도 않은 문서를 확인 없이 덮어쓴다.
    /// 그래서 여기서는 부분/접두어 일치를 절대 쓰지 않고, 표시 문자열 → 대상의 정확한
    /// 사전(exact key) 하나로만 되짚는다.
    /// </summary>
    public static class PrdTargetSelection
    {
        /// <summary>대상 하나를 다중 선택 UI에 보여줄 표시 문자열로 바꾼다.</summary>
        public static string ToDisplayLabel(PrdTarget target) =>
            target.HasExistingPrd ? $"{target.Label} (기존 Prd.md 있음)" : target.Label;

        /// <summary>
        /// 선택된 표시 문자열들을 정확히 일치하는 대상으로만 되짚는다.
        /// 표시 문자열은 <see cref="ToDisplayLabel"/>로 만든 것과 정확히 같아야 매칭된다 -
        /// 접두어·부분 일치는 절대 허용하지 않는다.
        /// </summary>
        public static IReadOnlyList<PrdTarget> Resolve(
            IReadOnlyList<PrdTarget> targets, IReadOnlyList<string> pickedDisplayLabels)
            => Resolve(targets, pickedDisplayLabels, ToDisplayLabel);

        /// <summary>
        /// 선택된 표시 문자열들을 <paramref name="labelSelector"/>가 만든 표시 문자열과
        /// 정확히 일치하는 대상으로만 되짚는다.
        ///
        /// CLI 쪽에서 마크업 이스케이프가 걸린 문자열을 선택지로 보여줄 때 쓴다 - Core는
        /// Spectre.Console을 참조할 수 없어 이스케이프 자체는 여기서 못 하지만, "선택지를
        /// 만든 함수"와 "되짚을 때 쓰는 함수"가 다르면 이스케이프된 선택 문자열이 이스케이프
        /// 안 된 사전 키와 어긋나 전부 조용히 매칭 실패한다(선택했는데 아무 일도 안 일어나는
        /// 침묵 결함 - 되짚기 실패보다 더 위험하다). 그래서 호출부는 AddChoices에 넘긴 것과
        /// 정확히 같은 <paramref name="labelSelector"/> 함수 하나를 여기에도 넘겨야 한다 -
        /// 선택지 생성과 키 생성이 같은 함수 인스턴스에서 나오도록 강제한다.
        /// </summary>
        public static IReadOnlyList<PrdTarget> Resolve(
            IReadOnlyList<PrdTarget> targets,
            IReadOnlyList<string> pickedDisplayLabels,
            Func<PrdTarget, string> labelSelector)
        {
            var byDisplayLabel = targets.ToDictionary(labelSelector, t => t, StringComparer.Ordinal);

            var resolved = new List<PrdTarget>();
            foreach (var picked in pickedDisplayLabels)
            {
                if (byDisplayLabel.TryGetValue(picked, out var target))
                {
                    resolved.Add(target);
                }
            }

            return resolved;
        }
    }
}
