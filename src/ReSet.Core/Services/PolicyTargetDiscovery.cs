using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>정책서의 근거가 될 수 있는 대상 하나 - 명세서와 메타데이터가 함께 있는 객체.</summary>
    public sealed record PolicyTarget(string Label, string DocsDirectory, string MetadataPath);

    /// <summary>
    /// output/Procedures 아래에서 명세서가 있는 객체를 찾는다.
    ///
    /// [왜 DB에 붙지 않는가] 이 기능의 근거는 Spec.md 하나이고 상수 추출 보조로 쓰는
    /// DDL 사본도 raw/metadata.json에 이미 있다. 파일시스템만 읽으므로 이미 쌓인
    /// 산출물에 재분석 없이 소급 적용된다 - PrdTargetDiscovery와 같은 판단이다.
    ///
    /// [Functions·External을 뺀 이유] PrdTargetDiscovery와 같다. 넓힐 때는 여기 한 곳만
    /// 고치면 된다.
    /// </summary>
    public static class PolicyTargetDiscovery
    {
        public static IReadOnlyList<PolicyTarget> Find(string outputRoot)
        {
            var proceduresRoot = Path.Combine(outputRoot, "Procedures");
            if (!Directory.Exists(proceduresRoot))
            {
                return Array.Empty<PolicyTarget>();
            }

            var targets = new List<PolicyTarget>();
            foreach (var objectDir in Directory.EnumerateDirectories(proceduresRoot))
            {
                var docs = Path.Combine(objectDir, "docs");
                if (!File.Exists(Path.Combine(docs, OutputPathResolver.SpecFileNamePublic)))
                {
                    continue;
                }

                targets.Add(new PolicyTarget(
                    Path.GetFileName(objectDir),
                    docs,
                    Path.Combine(objectDir, "raw", "metadata.json")));
            }

            return targets.OrderBy(t => t.Label, StringComparer.Ordinal).ToList();
        }
    }
}
