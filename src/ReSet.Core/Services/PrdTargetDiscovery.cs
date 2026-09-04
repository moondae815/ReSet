using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>PRD를 도출할 수 있는 대상 하나 - 명세서가 이미 있는 객체.</summary>
    public sealed record PrdTarget(string Label, string DocsDirectory, bool HasExistingPrd);

    /// <summary>
    /// output/Procedures 아래에서 명세서가 있는 객체를 찾는다.
    ///
    /// Functions·External은 1차 범위 밖이다 - 함수 명세서에서 「업무 요구」를 뽑는 것은
    /// 의미가 얇다. 넓힐 때는 여기 한 곳만 고치면 된다.
    ///
    /// 이 클래스는 파일시스템만 읽는다 - DB 연결이 필요 없다는 것이 이 기능의 핵심이다
    /// (이미 디스크에 있는 분석 산출물에 대해서만 동작한다).
    /// </summary>
    public static class PrdTargetDiscovery
    {
        public static IReadOnlyList<PrdTarget> Find(string outputRoot)
        {
            var proceduresRoot = Path.Combine(outputRoot, "Procedures");
            if (!Directory.Exists(proceduresRoot))
            {
                return Array.Empty<PrdTarget>();
            }

            var targets = new List<PrdTarget>();
            foreach (var objectDir in Directory.EnumerateDirectories(proceduresRoot))
            {
                var docs = Path.Combine(objectDir, "docs");
                var specPath = Path.Combine(docs, OutputPathResolver.SpecFileNamePublic);
                if (!File.Exists(specPath))
                {
                    continue;
                }

                targets.Add(new PrdTarget(
                    Path.GetFileName(objectDir),
                    docs,
                    File.Exists(Path.Combine(docs, OutputPathResolver.PrdFileName))));
            }

            return targets.OrderBy(t => t.Label, StringComparer.Ordinal).ToList();
        }
    }
}
