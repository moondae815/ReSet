using Microsoft.Extensions.Configuration;
using ReSet.Core.Models;
using ReSet.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodingEngineTests
    {
        [Fact]
        public void CodingEngineFactory_ShouldCreateEngineFromConfiguration()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"CodegenSettings:Engines:test-claude:Command", "claude-cli"},
                {"CodegenSettings:Engines:test-claude:Arguments", "run {instructions}"}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            var engine = factory.CreateEngine("test-claude", isBatchMode: false);

            Assert.NotNull(engine);
            Assert.Equal("test-claude", engine.Name);
        }

        [Fact]
        public void CodingEngineFactory_ShouldThrowException_WhenEngineConfigDoesNotExist()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            Assert.Throws<InvalidOperationException>(() => factory.CreateEngine("non-existent", isBatchMode: false));
        }

        [Fact]
        public void CodingEngineFactory_ShouldUseInteractiveArguments_WhenNotBatchMode()
        {
            var factory = new ReSet.Cli.CodingEngineFactory(BuildBothArgumentsConfig());

            var engine = Assert.IsType<ExternalCliCodingEngine>(factory.CreateEngine("test-claude", isBatchMode: false));

            Assert.Equal("run {instructions}", engine.ArgumentsTemplate);
            Assert.False(engine.IsHeadless);
        }

        [Fact]
        public void CodingEngineFactory_ShouldUseBatchArguments_WhenBatchMode()
        {
            var factory = new ReSet.Cli.CodingEngineFactory(BuildBothArgumentsConfig());

            var engine = Assert.IsType<ExternalCliCodingEngine>(factory.CreateEngine("test-claude", isBatchMode: true));

            Assert.Equal("-p run {instructions}", engine.ArgumentsTemplate);
            Assert.True(engine.IsHeadless);
        }

        [Fact]
        public void CodingEngineFactory_ShouldThrow_WhenBatchModeAndBatchArgumentsMissing()
        {
            // 대화형 인자로 폴백하면 TTY 오류로 조용히 실패한다. 명시적으로 막는다.
            var inMemorySettings = new Dictionary<string, string?> {
                {"CodegenSettings:Engines:test-agy:Command", "agy"},
                {"CodegenSettings:Engines:test-agy:Arguments", "--prompt-interactive {instructions}"},
                {"CodegenSettings:Engines:test-agy:BatchArguments", ""}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEngine("test-agy", isBatchMode: true));

            Assert.Contains("BatchArguments", ex.Message);
        }

        private static IConfiguration BuildBothArgumentsConfig()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    {"CodegenSettings:Engines:test-claude:Command", "claude"},
                    {"CodegenSettings:Engines:test-claude:Arguments", "run {instructions}"},
                    {"CodegenSettings:Engines:test-claude:BatchArguments", "-p run {instructions}"}
                })
                .Build();
        }

        [Fact]
        public async Task ExternalCliCodingEngine_ShouldThrow_WhenCommandDoesNotExist()
        {
            var engine = new ExternalCliCodingEngine("test-engine", "non-existent-command-12345", "--help", isHeadless: false);
            var spDef = new SpDefinition { Schema = "dbo", Name = "TestSp" };

            var tempFile = Path.GetTempFileName();

            // 워킹 디렉터리로 실행 디렉터리를 넘기지 않는다. 엔진은 프로세스를 띄우기
            // 전에 그 디렉터리를 통째로 재귀 스냅샷하는데, 실행 디렉터리에는 파일이
            // 266개 있고 그중 output 트리는 다른 테스트들이 동시에 만들고 지운다
            // (MetadataExporterTests·VerificationPipelineOrchestratorTests 등). 열거가
            // 그 파일에 닿는 순간과 삭제가 겹치면 이 테스트가 간헐적으로 터졌다.
            // 이 테스트가 보는 것은 "없는 명령어면 예외"뿐이라 워킹 디렉터리는 비어 있어도 된다.
            var workDir = Path.Combine(Path.GetTempPath(), "reset-coding-engine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await engine.GenerateCodeAsync(spDef, tempFile, workDir, CancellationToken.None);
                });
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
        }
    }
}
