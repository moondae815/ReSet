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

        /// <summary>BuildOrchestrator가 마지막으로 만든 목. 피드백 호출을 검사하는 데 쓴다.</summary>
        private IMetadataExporter _metadataExporter = null!;

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

        /// <summary>
        /// 지시서 파일을 실제로 만든다. 루프는 파일이 있을 때만 피드백을 붙이므로
        /// (RunSelfHealingWorkflowAsync의 File.Exists 분기), 이것 없이는 피드백이
        /// 호출되지 않는다.
        /// </summary>
        private void SeedInstructionsFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_instructionsPath)!);
            File.WriteAllText(_instructionsPath, "# 마이그레이션 지시서\n\n본문\n");
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

        /// <summary>L2가 항상 GAP를 내도록 세운 AI 클라이언트. 매핑은 성립하지만 검증이 매번 떨어진다.</summary>
        private static IAiClient DefectiveAiClient()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("stub");
            client.ModelName.Returns("stub-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new AiResult { Content = "{\"OverallStatus\": \"GAP\"}" }));
            return client;
        }

        private CodegenWorkflowOrchestrator BuildOrchestrator(
            ICodingEngine engine,
            int maxL2Attempts,
            int maxTotalAttempts = 20,
            IAiClient? aiClient = null)
        {
            var validatorConfig = new ValidatorConfig
            {
                SpecDirectory = _specDir,
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_tempRoot, "validation")
            };
            var verifier = new CodeVerificationOrchestrator(validatorConfig, aiClient ?? MatchingAiClient(), null, null);
            _metadataExporter = Substitute.For<IMetadataExporter>();

            return new CodegenWorkflowOrchestrator(engine, verifier, _metadataExporter, maxL2Attempts, maxTotalAttempts);
        }

        /// <summary>
        /// 총 시도 상한이 없으면 이 루프는 끝나지 않는다. 테스트가 영원히 매달리는 대신
        /// 명확히 실패하도록, 상한보다 넉넉한 횟수를 넘기면 던진다.
        /// </summary>
        private sealed class RunawayGuardEngine : ICodingEngine
        {
            private readonly int _throwAfter;

            public RunawayGuardEngine(int throwAfter)
            {
                _throwAfter = throwAfter;
            }

            public string Name => "runaway-guard-engine";
            public string Command => "runaway-guard";
            public int CallCount { get; private set; }

            public Task<CodegenRunResult> GenerateCodeAsync(
                SpDefinition? spDef, string instructionsFilePath, string targetProjectDir, CancellationToken cancellationToken)
            {
                CallCount++;
                if (CallCount > _throwAfter)
                {
                    throw new InvalidOperationException(
                        $"루프가 {_throwAfter}회를 넘겨도 멈추지 않았습니다 - 총 시도 상한이 없습니다.");
                }

                return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
            }
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

        /// <summary>
        /// 두 번째 호출에서 파일 시스템에 부수효과를 일으키는 엔진. 에이전트가 드디어
        /// 규약에 맞는 이름으로 파일을 만든 상황을 재현한다.
        /// </summary>
        private sealed class SeedingCodingEngine : ICodingEngine
        {
            private readonly Action _seedOnSecondCall;

            public SeedingCodingEngine(Action seedOnSecondCall)
            {
                _seedOnSecondCall = seedOnSecondCall;
            }

            public string Name => "seeding-engine";
            public string Command => "seeding";
            public int CallCount { get; private set; }

            public Task<CodegenRunResult> GenerateCodeAsync(
                SpDefinition? spDef, string instructionsFilePath, string targetProjectDir, CancellationToken cancellationToken)
            {
                CallCount++;
                if (CallCount == 2)
                {
                    _seedOnSecondCall();
                }

                return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
            }
        }

        /// <summary>
        /// 산출물이 매번 나오고(무산출물 캡 리셋) 매핑도 성립하는데(미대조 캡 리셋)
        /// L1/L2만 매번 떨어지는 조합. 기존 두 연속 캡 어디에도 닿지 않아
        /// MaxL2Attempts가 "unlimited"이면 무인 배치가 끝나지 않는 유료 기동이 된다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_UnlimitedAttemptsWithFailingVerification_StopsAtTotalCap()
        {
            SeedVerifiableJob();
            SeedInstructionsFile();
            var engine = new RunawayGuardEngine(throwAfter: 40);

            var orchestrator = BuildOrchestrator(
                engine, maxL2Attempts: -1, maxTotalAttempts: 5, aiClient: DefectiveAiClient());

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(5, engine.CallCount);
            // 사람이 무엇을 올려야 하는지 알 수 있어야 한다. 예산 소진과 총 상한 도달은
            // 다른 사건이므로 사유도 달라야 한다.
            Assert.NotNull(result.AbortReason);
            Assert.Contains("MaxTotalAttempts", result.AbortReason);
        }

        /// <summary>
        /// 총 상한은 바닥이지 예산이 아니다. MaxL2Attempts가 유한하면 그쪽이 먼저 끊어야 한다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_FiniteBudget_TotalCapDoesNotExtendIt()
        {
            SeedVerifiableJob();
            SeedInstructionsFile();
            var engine = new RunawayGuardEngine(throwAfter: 40);

            var orchestrator = BuildOrchestrator(
                engine, maxL2Attempts: 2, maxTotalAttempts: 20, aiClient: DefectiveAiClient());

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(2, engine.CallCount);
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
            // 미대조 연속 캡이 생긴 뒤로는 사유가 붙되, 그 사유가 가리키는 것은
            // 기동 실패가 아니라 대조 실패여야 한다.
            Assert.NotNull(result.AbortReason);
            Assert.Contains(_specDir, result.AbortReason);
            Assert.DoesNotContain("CodegenSettings:Engines", result.AbortReason);
            // 통과로 읽었다면 1회에 끊겼을 것이다.
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

        /// <summary>
        /// MaxL2Attempts가 "unlimited"(-1)면 maxAttempts가 int.MaxValue가 된다. 대조가
        /// 계속 실패하는 상태에서 상한이 없으면 무인 배치가 끝나지 않는 유료 기동이 된다.
        /// 회차 경로는 같은 상황을 연속 캡으로 막는다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_UnlimitedAttempts_ShouldStopAfterTwoUnverifiedRuns()
        {
            // 계획서도 소스도 심지 않는다 - 매핑이 0건이 되는 실제 조건 그대로다.
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(2, engine.CallCount);
        }

        /// <summary>
        /// 중단 사유는 배치 구성 문제를 가리켜야 한다. BuildAbortResult를 재사용하면
        /// CliFailureClassifier가 만든 "CLI 기동 실패" 안내가 나가는데, 여기서는 기동이
        /// 성공하고 산출물까지 나왔으므로 그 안내는 사람을 엉뚱한 곳으로 보낸다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_UnverifiedCapReached_AbortReasonNamesTheDirectories()
        {
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.NotNull(result.AbortReason);
            Assert.Contains(_specDir, result.AbortReason);
            Assert.Contains(_codeDir, result.AbortReason);
            Assert.DoesNotContain("CodegenSettings:Engines", result.AbortReason);
        }

        /// <summary>
        /// 미대조 시도에도 피드백이 붙어야 한다. 붙이지 않으면 재시도는 같은 명령을
        /// 신호 없이 다시 던지는 것이다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_NothingVerified_ShouldAppendFeedbackToInstructions()
        {
            SeedInstructionsFile();
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            await _metadataExporter.Received().AppendFeedbackToInstructionsAsync(
                _instructionsPath,
                Arg.Is<string>(feedback => feedback.Contains("검증 대조 실패")),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// 캡에 못 미친 미대조(1회, 캡은 2)는 루프를 끊지 않아야 한다 - 1회차에 대조가
        /// 실패해도 2회차에 대조 쌍이 나타나면 그대로 성공해야 한다. 캡 미만에서
        /// 무조건 중단하는 버그나 카운터를 리셋하지 않는 버그를 잡는다.
        ///
        /// 주의: 이 시나리오만으로는 캡 판정과 통과 판정의 순서 자체를 고정하지 못한다.
        /// 카운터는 nothingVerified가 거짓인 바로 그 시도에서 0으로 리셋되므로, 캡에
        /// 걸리는 시도와 통과하는 시도는 결코 같은 회차일 수 없다 - 두 판정을 서로
        /// 바꿔도 이 테스트에서는 결과가 갈리지 않는다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_MappingAppearsOnSecondRun_ShouldSucceed()
        {
            var engine = new SeedingCodingEngine(SeedVerifiableJob);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(2, engine.CallCount);
        }
    }
}
