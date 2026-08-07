using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class FileMappingServiceScopeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _specDir;
        private readonly string _codeDir;

        public FileMappingServiceScopeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-mapping-" + Guid.NewGuid().ToString("N"));
            _specDir = Path.Combine(_root, "agent", "steps");
            _codeDir = Path.Combine(_root, "src");
            Directory.CreateDirectory(_specDir);
            Directory.CreateDirectory(_codeDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private ValidatorConfig Config() => new()
        {
            SpecDirectory = _specDir,
            SourceCodeDirectory = _codeDir,
            OutputDirectory = Path.Combine(_root, "validation"),
        };

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldMapOneStepToOneFile()
        {
            var spec = Path.Combine(_specDir, "S01.md");
            File.WriteAllText(spec, "### S01 스냅샷 생성");
            var code = Path.Combine(_codeDir, "SnapshotTasklet.cs");
            File.WriteAllText(code, "class SnapshotTasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(spec, "S01", "SnapshotTasklet") });

            var pair = Assert.Single(results);
            Assert.Equal(spec, pair.SpecFilePath);
            Assert.Equal(code, pair.SourceCodePath);
            Assert.Equal("S01", pair.MappedName);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldDropPairWhenSourceMissing()
        {
            // Tasklet이 생성되지 않은 것 자체가 실패 신호다. 소스 디렉터리 전체로
            // 폴백하면 L2에 프로젝트 전체가 들어가 회차 분할의 목적이 사라진다.
            var spec = Path.Combine(_specDir, "S01.md");
            File.WriteAllText(spec, "### S01");

            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(spec, "S01", "SnapshotTasklet") });

            Assert.Empty(results);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldDropPairWhenSpecMissing()
        {
            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(Path.Combine(_specDir, "없음.md"), "S01", "X") });

            Assert.Empty(results);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldFallBackToStepCodeInFileName()
        {
            // 힌트가 없으면 파일명에 단계 코드가 든 것을 찾는다.
            var spec = Path.Combine(_specDir, "S02.md");
            File.WriteAllText(spec, "### S02");
            var code = Path.Combine(_codeDir, "S02LedgerTasklet.cs");
            File.WriteAllText(code, "class S02LedgerTasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(), new[] { new ExplicitPair(spec, "S02", null) });

            var pair = Assert.Single(results);
            Assert.Equal(code, pair.SourceCodePath);
        }

        [Fact]
        public void ResolveMappings_WithoutExplicitPairs_ShouldKeepLegacyBehaviour()
        {
            // 기존 오버로드는 BatchMigrationPlan.md 자동 탐색을 그대로 유지해야 한다.
            var planDir = Path.Combine(_root, "agent", "docs");
            Directory.CreateDirectory(planDir);
            File.WriteAllText(Path.Combine(planDir, "BatchMigrationPlan.md"), "## 개요");

            var config = new ValidatorConfig
            {
                SpecDirectory = Path.Combine(_root, "agent"),
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };
            Directory.CreateDirectory(Path.Combine(_codeDir, "docs"));

            var results = new FileMappingService().ResolveMappings(config);

            Assert.NotNull(results);
        }
    }
}
