using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 지목 재생성 회차에서 「읽는 쪽 → 쓰는 쪽」 제어 합계 간선을 찾아, 쓰는 쪽을 먼저 만들고 그 새 본문을 읽는 쪽 요청에 싣게 하는 계획.
    /// 사전 선언: <c>docs/audit-reports/2026-09-17-지목재생성-쓰는쪽먼저-사전선언.md</c>.
    /// </summary>
    public static class UpstreamSectionOrder
    {
        /// <param name="First">먼저 만들 쓰는 쪽 코드(대상 목록 순서).</param>
        /// <param name="UpstreamOf">읽는 쪽 코드 → 그 단계가 읽는 쓰는 쪽 코드들.</param>
        public sealed record Plan(IReadOnlyList<string> First, IReadOnlyDictionary<string, IReadOnlyList<string>> UpstreamOf);

        /// <summary>간선이 없거나 대상이 하나거나 순환이면 null - 종전 순서 그대로 둔다.</summary>
        public static Plan? For(IReadOnlyList<string> pendingCodes, IReadOnlyDictionary<string, string> sectionsBeforeRegeneration)
        {
            if (pendingCodes.Count < 2) return null;
            var pending = new HashSet<string>(pendingCodes, StringComparer.OrdinalIgnoreCase);
            var tables = ControlTotalNameFacts.ControlTableBareNames();
            if (tables.Count == 0) return null;

            var upstreamOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var reader in pendingCodes)
            {
                if (!sectionsBeforeRegeneration.TryGetValue(reader, out var markdown) || string.IsNullOrWhiteSpace(markdown)) continue;

                var (_, reads) = ControlTotalNameFacts.Collect(markdown, tables);
                var owners = reads
                    .SelectMany(read => read.Owners)
                    // 자기 몫 읽기는 간선이 아니다 - 대조형 단계(동결값 대 현재값)는 자기 몫도 읽는다(실물 B21 S19).
                    .Where(owner => !owner.Equals(reader, StringComparison.OrdinalIgnoreCase) && pending.Contains(owner))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(owner => IndexOf(pendingCodes, owner))
                    .ToList();
                if (owners.Count > 0) upstreamOf[reader] = owners;
            }

            if (upstreamOf.Count == 0) return null;

            var writers = new HashSet<string>(upstreamOf.Values.SelectMany(o => o), StringComparer.OrdinalIgnoreCase);
            // 쓰는 쪽이 다른 대상의 몫을 읽으면(순환 · 사슬) 순서를 정하지 않는다 - 종전대로 둔다(덜 바꾼다).
            if (writers.Any(upstreamOf.ContainsKey)) return null;

            return new Plan(pendingCodes.Where(writers.Contains).ToList(), upstreamOf);
        }

        private static int IndexOf(IReadOnlyList<string> codes, string code)
        {
            for (var i = 0; i < codes.Count; i++)
                if (codes[i].Equals(code, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
    }
}
