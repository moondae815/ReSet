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
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var root = Path.GetFullPath(directory);
            return SnapshotFiles(root, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }

        /// <summary>
        /// 파일 목록을 받아 스냅샷을 만든다. 목록을 밖에서 넣을 수 있게 갈라 둔 이유는
        /// 사라진 경로를 만났을 때의 동작을 테스트가 고정할 수 있게 하기 위해서다 -
        /// 열거와 삭제가 겹치는 타이밍은 밖에서 주입할 수 없다.
        ///
        /// 읽는 사이 사라진 파일은 건너뛴다. 스냅샷의 의미가 "그 순간 존재한 파일들"이므로
        /// 손실이 아니고, 여기서 던지면 그 시점에 다른 무언가가 같은 트리를 건드렸다는
        /// 이유만으로 코딩 에이전트 호출 전체가 실패한다 - 실측: 전체 테스트를 병렬로
        /// 돌릴 때 실행 디렉터리를 스냅샷하던 호출이 같은 디렉터리의 output 트리를 지우는
        /// 다른 테스트와 겹쳐 간헐적으로 터졌다.
        /// </summary>
        public static IReadOnlyDictionary<string, string> SnapshotFiles(
            string root, IEnumerable<string> files)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file);
                if (IsExcluded(relative))
                {
                    continue;
                }

                // 파일이 사라졌는지 미리 Exists로 묻지 않는다 - 물어본 다음과 읽는 사이에
                // 또 사라질 수 있어 검사 자체가 같은 경합을 안는다. 읽어 보고 없으면 넘긴다.
                try
                {
                    var info = new FileInfo(file);
                    snapshot[relative] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException ||
                    ex is DirectoryNotFoundException ||
                    ex is UnauthorizedAccessException ||
                    ex is IOException)
                {
                    continue;
                }
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
