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
    }
}
