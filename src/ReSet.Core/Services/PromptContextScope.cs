using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum ContextScopeMode
    {
        /// <summary>명세서 전량을 접두사에 싣는다. 접두사 캐시가 사는 제공자용.</summary>
        Full,

        /// <summary>단계의 프로시저와 그 1-hop 이웃만 싣는다. 캐시가 죽는 제공자용.</summary>
        Narrow
    }

    /// <summary>
    /// 단계 호출이 실을 명세서의 범위를 정한다.
    ///
    /// 이 클래스가 존재하는 이유: AppendSharedStepContext가 명세서 전량(실측 481KB)을
    /// 매 단계 호출에 싣는 것은 버그가 아니라 트레이드오프다 — 접두사가 단계 간 바이트까지
    /// 같아야 캐시가 살기 때문에, 캐시를 사려고 입력을 부풀렸다.
    ///
    /// 그런데 세 CLI 제공자는 모두 프롬프트를 단일 텍스트로만 받아 cache_control을 찍을
    /// 자리가 없다. 실측(POQSettleBatch4 2026-08-29): 캐시 쓰기 24,065,539 대 읽기
    /// 775,702 — 재사용률 3.1%. 전제가 거짓이므로 대가만 남는다.
    ///
    /// 비용만의 문제가 아니다. 250K 컨텍스트에서 16단계 규칙을 전부 지키게 하는 것
    /// 자체가 지시 이행력을 떨어뜨린다 — 재현성 저하의 원인이기도 하다.
    /// </summary>
    public static class PromptContextScope
    {
        public static ContextScopeMode ResolveMode(string? providerName, string? configured)
        {
            if (Enum.TryParse<ContextScopeMode>(configured, ignoreCase: true, out var explicitMode))
            {
                return explicitMode;
            }

            // CLI 제공자는 프롬프트를 단일 텍스트로만 받는다. 블록 배열도 다중 user
            // 메시지도 넘길 수단이 없어 cache_control을 찍을 자리가 물리적으로 없다.
            var isCli =
                providerName != null &&
                providerName.EndsWith("-cli", StringComparison.OrdinalIgnoreCase);

            return isCli ? ContextScopeMode.Narrow : ContextScopeMode.Full;
        }

        /// <summary>
        /// 이 단계가 봐야 할 명세서만 남긴다 — 자기 LegacyProcedures와 그것이 호출하는
        /// 1-hop 이웃.
        ///
        /// 이웃을 넣는 것이 요점이다. 실측 「필수 수정 1·2」가 정확히 그 관계였다:
        /// S13/S12가 S11 명세가 규정한 오류 코드(4000~4008 · ERROR_NUMBER 전파)를
        /// 지켜야 했다. 이웃을 빼면 이 유형의 결함이 오히려 늘어난다.
        ///
        /// 2-hop까지 끌지 않는다 — 그러면 전량으로 되돌아간다.
        ///
        /// 하나도 맞지 않으면 전량을 돌려준다. 빈 목록을 보내면 모델이 "원본 명세서가
        /// 없다"로 읽고 지어낸다.
        /// </summary>
        public static List<(string FileName, string Content)> NarrowSpecs(
            IReadOnlyList<(string FileName, string Content)> specs,
            BatchStepPlan step,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? callGraph)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var procedure in step.LegacyProcedures ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(procedure)) continue;
                wanted.Add(procedure.Trim());

                if (callGraph != null && callGraph.TryGetValue(procedure.Trim(), out var callees))
                {
                    foreach (var callee in callees)
                    {
                        if (!string.IsNullOrWhiteSpace(callee)) wanted.Add(callee.Trim());
                    }
                }
            }

            // 원본 목록 순서를 지킨다. 순서가 흔들리면 같은 재료라도 접두사가 달라져
            // 캐시가 죽고, 회차 간 프롬프트 대조도 불가능해진다.
            var narrowed = specs
                .Where(spec => wanted.Any(name => MatchesSpecName(spec.FileName, name)))
                .ToList();

            return narrowed.Count > 0 ? narrowed : specs.ToList();
        }

        /// <summary>
        /// 명세서 파일명과 프로시저 이름을 맞춘다. 파일명에 확장자나 경로가 붙는 경우가
        /// 있어 완전 일치만으로는 못 찾는다.
        ///
        /// 단순 부분 문자열 포함(IndexOf)은 쓰지 않는다 - "dbo.UP_Util_Settle_Summary"가
        /// "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA"의 접두 부분 문자열이라 오탐으로 매치된다
        /// (NarrowSpecs_PreservesSourceOrder에서 실측). 경로 마지막 세그먼트에서 확장자를
        /// 뗀 값과 정확히 일치할 때만 인정한다.
        /// </summary>
        private static bool MatchesSpecName(string fileName, string procedureName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (string.Equals(fileName, procedureName, StringComparison.OrdinalIgnoreCase)) return true;

            var lastSegment = fileName.Split('/', '\\').Last();
            var withoutExtension = System.IO.Path.GetFileNameWithoutExtension(lastSegment);
            return string.Equals(withoutExtension, procedureName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
