using System;
using System.IO;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ArtifactChangeDetectorTests : IDisposable
    {
        private readonly string _root;

        public ArtifactChangeDetectorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-artifact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void WriteFile(string relativePath, string content)
        {
            var full = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        [Fact]
        public void Snapshot_ShouldReturnEmpty_WhenDirectoryDoesNotExist()
        {
            var snapshot = ArtifactChangeDetector.Snapshot(Path.Combine(_root, "없는폴더"));

            Assert.Empty(snapshot);
        }

        [Fact]
        public void HasChanged_ShouldBeFalse_WhenNothingHappened()
        {
            WriteFile("Program.cs", "class C {}");

            var before = ArtifactChangeDetector.Snapshot(_root);
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.False(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileAdded()
        {
            var before = ArtifactChangeDetector.Snapshot(_root);
            WriteFile("Step1.cs", "class Step1 {}");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileModified()
        {
            WriteFile("Step1.cs", "class Step1 {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            // 길이가 달라지도록 고쳐야 타임스탬프 정밀도에 의존하지 않는다.
            WriteFile("Step1.cs", "class Step1 { public void Run() {} }");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileDeleted()
        {
            WriteFile("Step1.cs", "class Step1 {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            File.Delete(Path.Combine(_root, "Step1.cs"));
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeFalse_WhenOnlyBuildOutputChanged()
        {
            WriteFile("Program.cs", "class C {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            // 에이전트가 코드는 안 쓰고 빌드만 돌린 상황
            WriteFile(Path.Combine("bin", "Debug", "app.dll"), "binary");
            WriteFile(Path.Combine("obj", "project.assets.json"), "{}");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.False(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void Snapshot_ShouldIncludeNestedSourceFiles()
        {
            WriteFile(Path.Combine("Steps", "Step1.cs"), "class Step1 {}");

            var snapshot = ArtifactChangeDetector.Snapshot(_root);

            Assert.Single(snapshot);
        }

        /// <summary>
        /// 실측: 전체 테스트를 병렬로 돌리면 CodingEngineTests가 실행 디렉터리를
        /// 워킹 디렉터리로 넘겨 그 아래 266개 파일을 재귀 열거하는데, 같은 디렉터리의
        /// output/** 트리를 다른 테스트가 동시에 지운다. 열거가 어떤 파일에 닿는 순간
        /// 그것이 사라져 있으면 FileInfo.Length가 던지고, 코딩 엔진 호출 전체가 실패한다.
        ///
        /// 이 테스트는 그 경합을 재현하지 않는다 - 열거와 삭제가 겹치는 타이밍을 외부에서
        /// 주입할 방법이 없고, 억지로 만들면 이 테스트 자체가 플래키가 된다. 대신 그
        /// 상황에서 요구되는 동작만 좁게 고정한다: 목록에 사라진 경로가 섞여 있어도
        /// 나머지는 정상적으로 담긴다. 스냅샷의 의미가 "그 순간 존재한 파일들"이므로
        /// 읽는 사이 사라진 파일을 빼는 것은 손실이 아니다.
        /// </summary>
        [Fact]
        public void SnapshotFiles_ShouldSkipAPathThatVanishedAndKeepTheRest()
        {
            WriteFile("Kept.cs", "class Kept {}");
            var kept = Path.Combine(_root, "Kept.cs");
            var vanished = Path.Combine(_root, "Vanished.cs");

            var snapshot = ArtifactChangeDetector.SnapshotFiles(_root, new[] { vanished, kept });

            Assert.Equal(new[] { "Kept.cs" }, snapshot.Keys);
        }

        [Fact]
        public void SnapshotFiles_ShouldSkipAPathWhoseDirectoryVanished()
        {
            // 파일 하나가 아니라 상위 디렉터리째 사라지는 경우다 - 실측에서 다른 테스트가
            // Directory.Delete(recursive)로 output 트리를 통째로 지운다.
            var gone = Path.Combine(_root, "GoneDir", "Step1.cs");

            var snapshot = ArtifactChangeDetector.SnapshotFiles(_root, new[] { gone });

            Assert.Empty(snapshot);
        }
    }
}
