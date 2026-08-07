using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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
        public void ResolveMappings_WithExplicitPairs_ShouldNotMatchDecoyFromAnotherStepSharingCodePrefix()
        {
            // 단계 코드는 AI가 생성한 계획서 텍스트에서 온 자유 형식 문자열이라
            // 자릿수 고정이 강제되지 않는다(S1/S10/S11 혼재 가능). 앵커 없는
            // Contains/StartsWith라면 "S1"이 "S10DecoyTasklet"의 접두사라는 이유로
            // 다른 회차의 파일을 집어삼켜, 그 회차가 엉뚱한 코드로 게이트를
            // 통과하게 만든다. 디렉터리에는 미끼(S10)만 두고 S1의 진짜 파일은
            // 없는 상태로 만들어, 폴백이 미끼를 절대 집어 오면 안 된다는 것을
            // 확인한다.
            var spec = Path.Combine(_specDir, "S1.md");
            File.WriteAllText(spec, "### S1");
            File.WriteAllText(Path.Combine(_codeDir, "S10DecoyTasklet.cs"), "class S10DecoyTasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(), new[] { new ExplicitPair(spec, "S1", null) });

            Assert.Empty(results);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldPickOwnFileOverDecoyFromAnotherStep()
        {
            // 위 테스트의 반대 축: 미끼와 진짜 파일이 함께 있을 때도 앵커가
            // 정확한 파일을 골라야 한다 - 폴백이 무조건 실패로 흘러가는 것만
            // 확인하면 앵커 규칙 자체가 지나치게 엄격해져 정상 매치까지
            // 막아버리는 회귀를 놓친다.
            var spec = Path.Combine(_specDir, "S1.md");
            File.WriteAllText(spec, "### S1");
            File.WriteAllText(Path.Combine(_codeDir, "S10DecoyTasklet.cs"), "class S10DecoyTasklet {}");
            var target = Path.Combine(_codeDir, "S1Tasklet.cs");
            File.WriteAllText(target, "class S1Tasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(), new[] { new ExplicitPair(spec, "S1", null) });

            var pair = Assert.Single(results);
            Assert.Equal(target, pair.SourceCodePath);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldExcludeOnlyTheFailingPairAmongMultiple()
        {
            // 여러 쌍을 한 번에 요청했을 때 실패한 쌍만 개별적으로 빠지고,
            // 성공한 쌍은 영향받지 않아야 한다 (부분 실패가 전체를 무너뜨리지 않음).
            var okSpec = Path.Combine(_specDir, "S01.md");
            File.WriteAllText(okSpec, "### S01");
            var okCode = Path.Combine(_codeDir, "SnapshotTasklet.cs");
            File.WriteAllText(okCode, "class SnapshotTasklet {}");

            var missingSpec = Path.Combine(_specDir, "S02.md");
            File.WriteAllText(missingSpec, "### S02");
            // S02의 소스 파일은 의도적으로 만들지 않는다.

            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[]
                {
                    new ExplicitPair(okSpec, "S01", "SnapshotTasklet"),
                    new ExplicitPair(missingSpec, "S02", "LedgerTasklet"),
                });

            var pair = Assert.Single(results);
            Assert.Equal("S01", pair.MappedName);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldLogWarningWhenAllRequestedPairsFail()
        {
            // 매칭 실패한 쌍을 조용히 버리기만 하면 "검증할 게 없어서 게이트를
            // 통과함"과 "실제로 검증해서 통과함"이 반환값만으로 구별되지 않는다.
            // 반환 타입을 바꾸는 건 이 태스크 범위를 넘으므로, 최소한 요청한
            // 쌍이 전부 실패했다는 사실을 경고 로그로 남기는지 확인한다.
            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
            try
            {
                var spec = Path.Combine(_specDir, "S01.md");
                File.WriteAllText(spec, "### S01");
                // 소스 파일을 만들지 않아 유일하게 요청한 쌍이 실패하도록 만든다.

                var results = new FileMappingService().ResolveMappings(
                    Config(), new[] { new ExplicitPair(spec, "S01", "MissingTasklet") });

                Assert.Empty(results);
                Assert.Contains(sink.Messages, m => m.Contains("모두 매칭 실패"));
            }
            finally
            {
                Log.Logger = previousLogger;
            }
        }

        [Fact]
        public void ResolveMappings_WithoutExplicitPairs_ShouldKeepLegacyBehaviour()
        {
            // 기존 오버로드는 BatchMigrationPlan.md 자동 탐색을 그대로 유지해야 한다.
            // Assert.NotNull만으로는 이 오버로드가 절대 null을 반환하지 않는 한
            // 무조건 통과하므로, 실제로 계획서를 찾아 올바른 필드로 매핑했는지까지
            // 단언한다. 매칭 규칙은 "설계서 파일의 조부모 폴더명"이 SP 이름이 되므로
            // agent/dbo.CustOrderHist/docs/BatchMigrationPlan.md 형태로 배치한다.
            var planDir = Path.Combine(_root, "agent", "dbo.CustOrderHist", "docs");
            Directory.CreateDirectory(planDir);
            var planPath = Path.Combine(planDir, "BatchMigrationPlan.md");
            File.WriteAllText(planPath, "## 개요");

            var config = new ValidatorConfig
            {
                SpecDirectory = Path.Combine(_root, "agent"),
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };
            // 규칙 2(폴더 매치): 스키마를 뺀 SP 이름과 같은 폴더가 소스 디렉터리 밑에 있어야 한다.
            Directory.CreateDirectory(Path.Combine(_codeDir, "CustOrderHist"));

            var results = new FileMappingService().ResolveMappings(config);

            var pair = Assert.Single(results);
            Assert.Equal(planPath, pair.SpecFilePath);
            Assert.Equal("dbo.CustOrderHist", pair.MappedName);
            Assert.Equal(Path.Combine(_codeDir, "CustOrderHist"), pair.SourceCodePath);
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }
    }
}
