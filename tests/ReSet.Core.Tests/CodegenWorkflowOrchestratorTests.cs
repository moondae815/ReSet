using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Finding 3(무산출물 연속 재시도 캡)과 Finding 4(산출물 없이 소진됐을 때 이유 표시)를
    /// 실제 RunSelfHealingWorkflowAsync 루프로 검증한다.
    ///
    /// CodeVerificationOrchestrator는 구상 클래스라 통째로 목으로 감쌀 수 없다(CodegenLoopPolicy.cs
    /// 참조). 그래서 실제 매핑·L1·L2 경로를 그대로 태우되, 계획서와 소스 파일을 규약대로
    /// 심고(<see cref="SeedVerifiableJob"/>) L2를 항상 MATCH로 세운 AI 클라이언트를 쓴다.
    ///
    /// 예전에는 SpecDirectory/SourceCodeDirectory를 빈 폴더로 두어 "매핑 0건 → 실패 0건 →
    /// 통과"를 이용했다. 그 성질 자체가 결함이었다(빈 목록에 대한 실패 0건은 공허하게 참이다).
    /// 그것을 픽스처로 쓰면 결함을 고치는 순간 테스트가 함께 무너지므로, 통과 경로는 진짜
    /// 매핑으로 재현한다.
    /// </summary>
    public class CodegenWorkflowOrchestratorTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _specDir;
        private readonly string _codeDir;
        private readonly string _instructionsPath;

        public CodegenWorkflowOrchestratorTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "reset-codegen-workflow-" + Guid.NewGuid().ToString("N"));
            _specDir = Path.Combine(_tempRoot, "docs");
            _codeDir = Path.Combine(_tempRoot, "src");
            Directory.CreateDirectory(_specDir);
            Directory.CreateDirectory(_codeDir);
            _instructionsPath = Path.Combine(_tempRoot, "agent", "MigrationInstructions.md");
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        /// <summary>
        /// 자동 탐색이 짝지을 수 있는 계획서와 소스를 심는다. 매핑명은 계획서의 조부모
        /// 디렉터리 이름(= 이 픽스처의 임시 루트 이름)이므로 소스 파일도 그 이름을 쓴다.
        /// </summary>
        private void SeedVerifiableJob()
        {
            var mappedName = Path.GetFileName(_tempRoot);
            File.WriteAllText(Path.Combine(_specDir, "BatchMigrationPlan.md"), "# 통합 계획\n\n본문");
            File.WriteAllText(
                Path.Combine(_codeDir, mappedName + ".cs"),
                "public class JobEntryPoint { }");
        }

        private static IAiClient MatchingAiClient()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("stub");
            client.ModelName.Returns("stub-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new AiResult { Content = "{\"OverallStatus\": \"MATCH\"}" }));
            return client;
        }

        private CodegenWorkflowOrchestrator BuildOrchestrator(ICodingEngine engine, int maxL2Attempts)
        {
            var validatorConfig = new ValidatorConfig
            {
                SpecDirectory = _specDir,
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_tempRoot, "validation")
            };
            var verifier = new CodeVerificationOrchestrator(validatorConfig, MatchingAiClient(), null, null);
            var metadataExporter = Substitute.For<IMetadataExporter>();

            return new CodegenWorkflowOrchestrator(engine, verifier, metadataExporter, maxL2Attempts);
        }

        /// <summary>테스트마다 미리 정해 둔 CodegenRunResult 순서를 그대로 돌려주는 가짜 엔진.</summary>
        private sealed class ScriptedCodingEngine : ICodingEngine
        {
            private readonly Queue<CodegenRunResult> _results;

            public ScriptedCodingEngine(params CodegenRunResult[] results)
            {
                _results = new Queue<CodegenRunResult>(results);
            }

            public string Name => "scripted-engine";
            public string Command => "scripted";
            public int CallCount { get; private set; }

            public Task<CodegenRunResult> GenerateCodeAsync(
                SpDefinition? spDef, string instructionsFilePath, string targetProjectDir, CancellationToken cancellationToken)
            {
                CallCount++;
                // 마지막 결과를 스크립트가 소진된 뒤에도 계속 돌려준다 - 캡이 실제로
                // 루프를 끊는지(더 호출되지 않는지)를 CallCount로 별도 검증한다.
                var result = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
                return Task.FromResult(result);
            }
        }

        [Fact]
        public async Task RunSelfHealingWorkflowAsync_TwoConsecutiveNoArtifactRuns_AbortsWithoutReachingMaxAttempts()
        {
            // maxL2Attempts를 넉넉히 5로 둬도, 산출물 없는 재시도가 2회 연속이면 그 전에 끊겨야 한다.
            var noArtifact = new CodegenRunResult(false, 0, CliFailureKind.Unknown, "빈 응답, 이유 불명");
            var engine = new ScriptedCodingEngine(noArtifact, noArtifact, noArtifact, noArtifact, noArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 5);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.AbortReason);
            // "2회 연속" 같은 루프 서사는 로그 전용이다(원래 Abort 분기도 그렇다). 콘솔로
            // 나가는 AbortReason은 마지막 실행의 Diagnostic을 담고 있어야 한다 - 이게
            // Finding 3이 요구하는 최소 조건("마지막 Diagnostic으로 중단")이다.
            Assert.Contains("빈 응답, 이유 불명", result.AbortReason);
            // 캡이 없었다면 5까지 불렸을 것이다. 2에서 끊겼는지가 이 테스트의 핵심이다.
            Assert.Equal(2, engine.CallCount);
        }

        [Fact]
        public async Task RunSelfHealingWorkflowAsync_MaxAttemptsExhaustedWithoutArtifacts_SurfacesLastDiagnostic()
        {
            // maxL2Attempts=1이면 연속 재시도 캡(2)에 닿기 전에 시도 횟수 자체가 소진된다.
            // 이전에는 이 경로에서 AbortReason이 항상 null이었다(Finding 4).
            var noArtifact = new CodegenRunResult(false, 0, CliFailureKind.Unknown, "클라리파잉 질문만 남기고 종료");
            var engine = new ScriptedCodingEngine(noArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.AbortReason);
            // 이전에는 이 경로에서 AbortReason이 항상 null이었다 - Program.cs가 일반 빨간
            // 줄만 찍고 캡처해 둔 stderr는 버려졌다(Finding 4). 지금은 마지막 Diagnostic이
            // 콘솔까지 살아 있어야 한다.
            Assert.Contains("클라리파잉 질문만 남기고 종료", result.AbortReason);
            Assert.Equal(1, engine.CallCount);
        }

        [Fact]
        public async Task RunSelfHealingWorkflowAsync_ArtifactsAfterOneNoArtifactRetry_ResetsCounterAndSucceeds()
        {
            // 1회차: 산출물 없음(캡 카운터 1) -> 2회차: 산출물 있음(카운터 리셋, 실제 매핑을
            // 검증해 통과) -> 성공. 캡이 리셋되지 않았다면 2회차에서 카운터가 이미 2에
            // 도달해 검증 없이 중단됐을 것이다.
            SeedVerifiableJob();
            var noArtifact = new CodegenRunResult(false, 0, CliFailureKind.Unknown, "일시적 실패");
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(noArtifact, withArtifact, withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 5);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.AbortReason);
            Assert.Equal(2, engine.CallCount);
        }

        [Fact]
        public async Task RunSelfHealingWorkflowAsync_AbortReasonPointsAtCodegenSettings_NotAnalysisProviders()
        {
            // Finding 2가 Finding 3/4에서 새로 생기는 중단 경로에도 일관되게 적용되는지 확인한다.
            var noArtifact = new CodegenRunResult(false, 0, CliFailureKind.Unknown, "stderr 없음");
            var engine = new ScriptedCodingEngine(noArtifact, noArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 5);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.NotNull(result.AbortReason);
            Assert.DoesNotContain("AiSettings:Providers", result.AbortReason);
        }

        /// <summary>
        /// 빈 검증 결과에 대한 "실패 0건"은 공허하게 참이다. ResolveMappings(config)는
        /// SpecDirectory에 BatchMigrationPlan.md가 없거나 소스 트리에서 짝을 찾지 못하면
        /// 예외 없이 빈 목록을 돌려주므로, 코드가 한 줄도 검증되지 않았는데 "모든 검증
        /// 통과"로 끝났다. 회차 경로는 이 구멍을 닫았고 전체 Job 경로만 열려 있었다 -
        /// 메뉴 3에서 브랜치 이전의 모든 Job이 여전히 이 경로로 온다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_NothingWasVerified_ShouldNotReportSuccess()
        {
            // 계획서도 소스도 심지 않는다 - 매핑이 0건이 되는 실제 조건 그대로다.
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact, withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 2);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            // 산출물은 나왔으므로 "산출물을 못 만들었다"는 중단 사유 경로가 아니다.
            Assert.Null(result.AbortReason);
            // 통과로 읽었다면 1회에 끊겼을 것이다. 시도를 모두 소진했어야 한다.
            Assert.Equal(2, engine.CallCount);
        }

        /// <summary>
        /// 반대로 진짜 매핑이 있고 검증을 통과했다면 그 자리에서 끝나야 한다.
        /// 위 가드가 정상 통과 경로까지 막지 않는다는 것을 함께 고정한다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_RealMappingPasses_ShouldStopAtFirstAttempt()
        {
            SeedVerifiableJob();
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact, withArtifact, withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: 3);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, engine.CallCount);
        }
    }
}
