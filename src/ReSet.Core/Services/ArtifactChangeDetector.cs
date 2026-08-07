using System;
using System.Collections.Generic;
using System.IO;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 에이전트가 작업 디렉터리에 실제로 무언가를 남겼는지 판정한다.
    ///
    /// 종료 코드는 믿을 수 없다. claude와 agy는 권한 자동 거부로 아무것도 못 하고도
    /// 0을 반환한다. 그래서 파일시스템 변화를 직접 본다.
    /// </summary>
    public static class ArtifactChangeDetector
    {
        // 빌드 부산물을 세면 에이전트가 코드는 안 쓰고 빌드만 돌려도 "산출물 생성"으로
        // 잡혀 이 감지 자체가 무력해진다.
        private static readonly string[] ExcludedDirectories =
        {
            "bin", "obj", ".git", "node_modules", ".vs", "target"
        };

        public static IReadOnlyDictionary<string, string> Snapshot(string directory)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return snapshot;
            }

            var root = Path.GetFullPath(directory);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (IsExcluded(relative))
                {
                    continue;
                }

                var info = new FileInfo(file);
                snapshot[relative] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }

            return snapshot;
        }

        public static bool HasChanged(
            IReadOnlyDictionary<string, string> before,
            IReadOnlyDictionary<string, string> after)
        {
            if (before.Count != after.Count)
            {
                return true;
            }

            foreach (var entry in after)
            {
                if (!before.TryGetValue(entry.Key, out var previous) || previous != entry.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExcluded(string relativePath)
        {
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 마지막 세그먼트는 파일명이므로 디렉터리 세그먼트만 본다.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                foreach (var excluded in ExcludedDirectories)
                {
                    if (string.Equals(excluded, segments[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
