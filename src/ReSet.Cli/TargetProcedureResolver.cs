using System;
using System.Collections.Generic;

namespace ReSet.Cli
{
    /// <summary>
    /// <c>--sp</c>로 지목한 이름을 DB의 프로시저 목록(<c>스키마.이름</c>)에 대조한다.
    /// 점이 있으면 전체 이름으로, 없으면 스키마를 뺀 이름으로 대소문자 무시 비교한다.
    /// 부분 문자열·약칭은 맞추지 않는다 — 지목한 이름이 목록에 없으면 <see cref="Unmatched"/>에
    /// 그대로 남겨 호출자가 "건너뜀"이 아니라 "오류"로 다룰 수 있게 한다(2026-08-23 실측:
    /// 약칭이 경고만 남기고 종료 코드 0으로 건너뛰어져 재생성이 조용히 빠졌다).
    /// </summary>
    public static class TargetProcedureResolver
    {
        public sealed record Resolution(IReadOnlyList<string> Matched, IReadOnlyList<string> Unmatched);

        public static Resolution Resolve(IEnumerable<string> targets, IReadOnlyList<string> spNames)
        {
            var matched = new List<string>();
            var unmatched = new List<string>();

            foreach (var target in targets)
            {
                string? hit = null;
                foreach (var candidate in spNames)
                {
                    var comparand = target.Contains('.') ? candidate : NameOnly(candidate);
                    if (comparand.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = candidate;
                        break;
                    }
                }

                if (hit != null) matched.Add(hit);
                else unmatched.Add(target);
            }

            return new Resolution(matched, unmatched);
        }

        private static string NameOnly(string schemaQualified)
        {
            var parts = schemaQualified.Split('.', 2);
            return parts.Length > 1 ? parts[1] : parts[0];
        }
    }
}
