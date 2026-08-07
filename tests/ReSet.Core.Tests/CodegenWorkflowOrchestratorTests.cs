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
    /// 참조). 대신 SpecDirectory/SourceCodeDirectory를 빈 임시 폴더로 두면 ResolveMappings가
    /// 매핑 대상 0개를 돌려주고, 그러면 RunVerificationAsync가 빈 목록을 반환해 allPassed가
    /// 항상 참이 된다 - 이 성질을 이용해 "산출물이 나와 검증까지 갔고, 통과했다"를 실제
    /// 코드 경로로 재현한다. 정적/AI 검증 자체(L1/L2 판정)는 이 테스트의 대상이 아니다.
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

        private CodegenWorkflowOrchestrator BuildOrchestrator(ICodingEngine engine, int maxL2Attempts)
        {
            var validatorConfig = new ValidatorConfig
            {
                SpecDirectory = _specDir,
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_tempRoot, "validation")
            };
            var aiClient = Substitute.For<IAiClient>();
            var verifier = new CodeVerificationOrchestrator(validatorConfig, aiClient, null, null);
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
            // 1회차: 산출물 없음(캡 카운터 1) -> 2회차: 산출물 있음(카운터 리셋, 매핑 0건이라
            // 검증이 트리비얼하게 통과) -> 성공. 캡이 리셋되지 않았다면 2회차에서 카운터가
            // 이미 2에 도달해 검증 없이 중단됐을 것이다.
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
    }
}
