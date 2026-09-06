using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코드값 사전을 만든다. 좌변(코드 상수)은 오프라인, 우변(업무 의미)은 DB가 있을 때만.
    ///
    /// [왜 채택을 명세서가 정하는가] 추출은 DDL로 해야 정확하지만(동적 SQL 조각을 걸러야
    /// 하므로 ScriptDom), 채택은 Spec.md가 정해야 「근거는 명세서뿐」이라는 계약이 선다.
    /// 둘을 나눠 두면 정확도와 계약을 함께 가진다. 실측 채택률은 (SP, 상수) 출현 기준
    /// 114/115다.
    /// </summary>
    public static class SettlementCodebookBuilder
    {
        /// <summary>
        /// 값만으로 매칭할 때 요구하는 최소 길이.
        ///
        /// 'Y'·'N'·'1' 같은 값은 아무 테이블에서나 걸려 매칭이 잡음이 된다(실질 상수
        /// 64개 중 13개가 길이 2 이하, 2026-09-06 실측). 이런 플래그의 의미는 코드
        /// 테이블이 아니라 컬럼 이름과 명세서 서술에 있고, 그건 AI가 Spec 인용으로
        /// 이미 다룬다.
        /// </summary>
        public const int MinimumMatchableLength = 3;

        public static SettlementCodebook BuildLeftSide(IReadOnlyList<PolicySource> sources)
        {
            // 값 → (컬럼 후보, 그 값을 쓰는 SP들)
            var adopted = new Dictionary<string, (string? Column, List<string> Procedures)>(StringComparer.Ordinal);
            var unlisted = new List<string>();

            foreach (var source in sources)
            {
                foreach (var pair in ConstantComparisonExtractor.Extract(source.DdlText))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    if (!source.SpecMarkdown.Contains(pair.Value, StringComparison.Ordinal))
                    {
                        if (!unlisted.Contains(pair.Value, StringComparer.Ordinal))
                        {
                            unlisted.Add(pair.Value);
                        }

                        continue;
                    }

                    if (!adopted.TryGetValue(pair.Value, out var existing))
                    {
                        adopted[pair.Value] = (pair.Column, new List<string> { source.Label });
                        continue;
                    }

                    // 컬럼은 먼저 잡힌 것을 지킨다. 같은 값이 여러 컬럼과 비교되면
                    // 어느 하나를 고를 근거가 없고, 매칭은 컬럼이 없어도 2단으로 돈다.
                    if (!existing.Procedures.Contains(source.Label, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Procedures.Add(source.Label);
                    }

                    adopted[pair.Value] = (existing.Column ?? pair.Column, existing.Procedures);
                }
            }

            var entries = adopted
                .Select(kv => new CodebookEntry(
                    kv.Key,
                    kv.Value.Column,
                    kv.Value.Procedures,
                    kv.Key.Length >= MinimumMatchableLength,
                    Array.Empty<CodebookMatch>()))
                .OrderBy(e => e.Value, StringComparer.Ordinal)
                .ToList();

            return new SettlementCodebook(entries, unlisted);
        }
    }
}
