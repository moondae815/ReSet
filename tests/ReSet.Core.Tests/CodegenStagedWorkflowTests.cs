using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 회차 루프의 규율만 검증한다. L1/L2 판정 알고리즘 자체는 대상이 아니다.
    ///
    /// CodeVerificationOrchestrator는 구상 클래스라 목으로 감쌀 수 없다. 그렇다고
    /// "매핑 0건이면 통과"라는 성질에 기대어 통과 회차를 만들면(기존
    /// CodegenWorkflowOrchestratorTests가 쓰는 수법) 이 태스크가 막아야 할 결함 -
    /// 검증할 코드가 없어서 통과하는 회차 - 을 테스트가 오히려 전제하게 된다.
    /// 그래서 여기서는 회차 설계서(agent/steps/*.md)와 단계 코드로 시작하는 소스
    /// 파일을 실제로 만들어 놓고, IAiClient만 MATCH를 돌려주도록 세워 L1/L2를
    /// 진짜로 통과시킨다.
    /// </summary>
    public class CodegenStagedWorkflowTests : IDisposable
    {
        /// <summary>조립 회차의 Job 전체 검증이 매핑할 대상. 계획서 폴더 이름이 곧 매핑 이름이다.</summary>
        private const string JobName = "JobX";

        private readonly string _root;
        private readonly string _agentDir;
        private readonly string _codeDir;
        private readonly string _specDir;

        public CodegenStagedWorkflowTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-staged-" + Guid.NewGuid().ToString("N"));
            _agentDir = Path.Combine(_root, "agent");
            _codeDir = Path.Combine(_root, "src");

            // 실제 배선과 같은 배치다 - FileMappingService의 자동 탐색은 계획서의
            // <job>/docs/BatchMigrationPlan.md 구조에서 상위 폴더 이름을 매핑 이름으로 쓴다.
            // 빈 폴더로 두면 자동 탐색이 0건을 돌려주어 "건너뛰었다"와 "찾은 게 없다"가
            // 구별되지 않으므로, 계획서를 실제로 깔아 둔다.
            _specDir = Path.Combine(_root, JobName, "docs");
            Directory.CreateDirectory(_agentDir);
            Directory.CreateDirectory(_codeDir);
            Directory.CreateDirectory(_specDir);
            File.WriteAllText(Path.Combine(_specDir, "BatchMigrationPlan.md"), "# JobX 통합 계획");

            foreach (var name in new[]
            {
                "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md",
            })
            {
                File.WriteAllText(Path.Combine(_agentDir, name), "# " + name);
            }

            // 번들이 실제로 쓰는 회차 설계서. 이것이 있어야 단계 회차의 검증 범위를
            // 좁힐 수 있고, 없으면 회차 게이트가 "검증 못 함"으로 실패한다.
            var stepsDir = Path.Combine(_agentDir, "steps");
            Directory.CreateDirectory(stepsDir);
            File.WriteAllText(Path.Combine(stepsDir, "S01.md"), "# S01 단계 설계서");
            File.WriteAllText(Path.Combine(stepsDir, "S02.md"), "# S02 단계 설계서");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private CodegenStagePlan Plan(bool stepsSplit = true) => CodegenStagePlan.FromBundle(
            new BundleResult(
                Path.Combine(_agentDir, "MigrationInstructions.md"),
                new[] { "S01", "S02" },
                Array.Empty<string>(),
                StepsSplit: stepsSplit,
                new[]
                {
                    Path.Combine(_agentDir, "task-00-bootstrap.md"),
                    Path.Combine(_agentDir, "task-01-S01.md"),
                    Path.Combine(_agentDir, "task-02-S02.md"),
                    Path.Combine(_agentDir, "task-99-assembly.md"),
                }),
            _agentDir);

        private CodegenWorkflowOrchestrator Build(
            ICodingEngine engine, int maxAttempts = 2, IAiClient? aiClient = null)
        {
            var config = new ValidatorConfig
            {
                SpecDirectory = _specDir,
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };

            var verifier = new CodeVerificationOrchestrator(
                config, aiClient ?? MatchingAiClient(), null, null);

            return new CodegenWorkflowOrchestrator(
                engine, verifier, new MetadataExporter(), maxAttempts);
        }

        /// <summary>
        /// L2가 항상 MATCH를 내도록 세운 AI 클라이언트.
        /// <paramref name="verifiedSpecs"/>를 주면 어떤 설계서가 L2에 실려 갔는지를 순서대로 기록한다 -
        /// 어느 회차가 무엇을 검증했는지(그리고 무엇을 검증하지 않았는지)를 고정하기 위한 것이다.
        /// </summary>
        private static IAiClient MatchingAiClient(List<string>? verifiedSpecs = null)
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("stub");
            client.ModelName.Returns("stub-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    verifiedSpecs?.Add(SpecMarker(callInfo.ArgAt<string>(1)));
                    return Task.FromResult(new AiResult { Content = "{\"OverallStatus\": \"MATCH\"}" });
                });
            return client;
        }

        /// <summary>L2 프롬프트에 실린 설계서가 무엇인지 한 낱말로 되짚는다.</summary>
        private static string SpecMarker(string userPrompt)
        {
            if (userPrompt.Contains("# JobX 통합 계획", StringComparison.Ordinal)) return "PLAN";
            if (userPrompt.Contains("# S01 단계 설계서", StringComparison.Ordinal)) return "S01";
            if (userPrompt.Contains("# S02 단계 설계서", StringComparison.Ordinal)) return "S02";
            return "UNKNOWN";
        }

        /// <summary>지정한 단계의 설계서에 대해서만 MISMATCH를 내는 AI 클라이언트.</summary>
        private static IAiClient AiClientMismatchingOn(string stepCode)
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("stub");
            client.ModelName.Returns("stub-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    // userPrompt에는 검증 대상 설계서 전문이 실린다. 그 단계의 설계서인지로 가른다.
                    var mismatch = callInfo.ArgAt<string>(1)
                        .Contains($"# {stepCode} 단계 설계서", StringComparison.Ordinal);

                    return Task.FromResult(new AiResult
                    {
                        Content = mismatch
                            ? "{\"OverallStatus\": \"MISMATCH\", \"BusinessLogicGap\": \"루프 조건이 다릅니다\"}"
                            : "{\"OverallStatus\": \"MATCH\"}",
                    });
                });
            return client;
        }

        /// <summary>
        /// 회차 지시서 경로에서 그 회차가 남겨야 할 소스 파일 이름을 만든다.
        /// 단계 회차는 단계 코드로 시작해야 FileMappingService가 짝지을 수 있다.
        /// </summary>
        private static string ArtifactNameFor(string taskFilePath)
        {
            var tail = string.Join("-", Path.GetFileNameWithoutExtension(taskFilePath).Split('-').Skip(2));
            return tail switch
            {
                "bootstrap" => "CommonInfra",
                // 조립 산출물은 Job 이름을 달아야 계획서(BatchMigrationPlan.md)의 자동 탐색이
                // 짝지을 수 있다 - 실제 파이프라인에서 이 회차가 만드는 것이 Job 진입점이다.
                "assembly" => JobName,
                _ => tail + "Tasklet",
            };
        }

        private static void WriteArtifactFor(string taskFilePath, string codeDir)
        {
            Directory.CreateDirectory(codeDir);
            var name = ArtifactNameFor(taskFilePath);
            File.WriteAllText(Path.Combine(codeDir, name + ".cs"), $"public class {name} {{ }}");
        }

        /// <summary>회차마다 그 회차의 산출물을 남기는(= 검증을 통과하는) 엔진.</summary>
        private ICodingEngine ProductiveEngine()
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    WriteArtifactFor(callInfo.ArgAt<string>(1), callInfo.ArgAt<string>(2));
                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        /// <summary>특정 task 파일에 대해서만 산출물을 전혀 남기지 않는 엔진.</summary>
        private ICodingEngine EngineFailingOn(string taskFileName)
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var instructions = callInfo.ArgAt<string>(1);
                    if (Path.GetFileName(instructions) == taskFileName)
                    {
                        return Task.FromResult(new CodegenRunResult(false, 1, CliFailureKind.Unknown, "산출물 없음"));
                    }

                    WriteArtifactFor(instructions, callInfo.ArgAt<string>(2));
                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        /// <summary>특정 task 파일에서 회차와 무관한 환경 실패를 내는 엔진.</summary>
        private ICodingEngine EngineFailingWith(string taskFileName, CliFailureKind kind)
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var instructions = callInfo.ArgAt<string>(1);
                    if (Path.GetFileName(instructions) == taskFileName)
                    {
                        return Task.FromResult(new CodegenRunResult(false, 1, kind, "환경 실패"));
                    }

                    WriteArtifactFor(instructions, callInfo.ArgAt<string>(2));
                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        /// <summary>
        /// 특정 task 파일에 대해 "파일은 남기지만 그 회차의 것은 아닌" 엔진.
        /// 종료 코드 0에 산출물도 있으니 루프 정책은 검증으로 넘긴다 - 회차 게이트가
        /// 이것을 통과로 읽는지 실패로 읽는지를 가르는 조건이다.
        /// </summary>
        private ICodingEngine EngineProducingUnrelatedOutputFor(string taskFileName)
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var instructions = callInfo.ArgAt<string>(1);
                    var dir = callInfo.ArgAt<string>(2);

                    if (Path.GetFileName(instructions) == taskFileName)
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "Scratch.cs"), "public class Scratch { }");
                    }
                    else
                    {
                        WriteArtifactFor(instructions, dir);
                    }

                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        private static List<string> InstructionCalls(ICodingEngine engine) => engine.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICodingEngine.GenerateCodeAsync))
            .Select(c => Path.GetFileName((string)c.GetArguments()[1]!))
            .ToList();

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldPassOneTaskFilePerStage()
        {
            var engine = ProductiveEngine();

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.Equal(
                new[] { "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md" },
                InstructionCalls(engine));
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldContinueAfterAFailedStep()
        {
            // 12개 중 하나가 까다로워도 나머지를 건진다.
            var engine = EngineFailingOn("task-01-S01.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01" }, result.FailedStepCodes);

            var calls = InstructionCalls(engine);

            Assert.Contains("task-02-S02.md", calls);
            Assert.Contains("task-99-assembly.md", calls);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldAbortWhenBootstrapFails()
        {
            // 공통 계약이 없으면 이후 회차가 성립하지 않는다.
            var engine = EngineFailingOn("task-00-bootstrap.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.NotNull(result.AbortReason);

            Assert.DoesNotContain("task-01-S01.md", InstructionCalls(engine));
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldWriteProgressForEveryStage()
        {
            var engine = EngineFailingOn("task-01-S01.md");

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var progress = AgentProgressStore.Load(_agentDir);

            Assert.NotNull(progress);
            Assert.Equal(4, progress!.Stages.Count);
            Assert.Equal(StageStatus.Failed, progress.Stages.Single(s => s.Id == "01-S01").Status);
            Assert.Equal(StageStatus.Passed, progress.Stages.Single(s => s.Id == "02-S02").Status);

            // 아예 아무것도 만들지 못한 회차라는 사실이 기록에 남아야 한다.
            Assert.Contains(
                "파일을 남기지 않았습니다",
                progress.Stages.Single(s => s.Id == "01-S01").LastGapSummary);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldRewriteAssemblyTaskWithFailedSteps()
        {
            // 조립 회차는 어떤 단계가 미완성인지 알아야 그것을 제외하고 조립한다.
            var engine = EngineFailingOn("task-01-S01.md");

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var assembly = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-99-assembly.md"));

            Assert.Contains("S01", assembly);
            Assert.Contains("손대지 마십시오", assembly);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldReportAllPassed_WhenNothingFailed()
        {
            var result = await Build(ProductiveEngine()).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.AllPassed);
            Assert.Empty(result.FailedStepCodes);
            Assert.Null(result.AbortReason);
        }

        /// <summary>
        /// 조립 회차에는 단계 코드가 없어 FailedStepCodes에 잡히지 않는다. 그 목록으로
        /// 전체 성공을 판정하면 조립이 실패했는데도 "모든 회차 통과"로 끝난다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldNotReportAllPassed_WhenAssemblyFailed()
        {
            var engine = EngineFailingOn("task-99-assembly.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Null(result.AbortReason);

            // 조립 실패는 "단계" 실패가 아니다 - 두 목록은 서로 다른 것을 센다.
            Assert.Empty(result.FailedStepCodes);

            var progress = AgentProgressStore.Load(_agentDir);

            Assert.Equal(StageStatus.Failed, progress!.Stages.Single(s => s.Id == "99-assembly").Status);
        }

        /// <summary>
        /// 요청한 회차 검증 쌍이 하나도 매칭되지 않으면 반환값은 빈 목록이고, 이는
        /// "요청 자체가 없어서 빈 목록"과 형태가 같다(FileMappingService.cs:87-93).
        /// 회차 게이트가 그것을 통과로 읽으면, 그 단계의 코드가 아예 만들어지지
        /// 않았는데도 초록으로 끝난다. 이 테스트가 그 경로를 막는다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldFailStepWhenItsCodeWasNeverGenerated()
        {
            var engine = EngineProducingUnrelatedOutputFor("task-01-S01.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01" }, result.FailedStepCodes);

            var progress = AgentProgressStore.Load(_agentDir);
            var stage = progress!.Stages.Single(s => s.Id == "01-S01");

            Assert.Equal(StageStatus.Failed, stage.Status);

            // "산출물을 아예 안 냈다"와 "이 단계의 코드를 못 찾았다"는 다른 사건이다.
            Assert.Contains("소스 파일을 찾지 못해", stage.LastGapSummary);
        }

        /// <summary>
        /// 세 번째 실패 형태 - 코드를 만들었고 검증까지 갔는데 떨어진 경우. 앞의 두 가지
        /// ("아무것도 안 남김", "이 단계 코드를 못 찾음")와 진행 기록의 문구가 달라야
        /// 사람이 어디를 손볼지 알 수 있다. 피드백이 그 회차 작업 파일에 붙는지도 함께 본다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldFailStepThatWasVerifiedAndRejected()
        {
            var result = await Build(ProductiveEngine(), aiClient: AiClientMismatchingOn("S01"))
                .RunStagedWorkflowAsync(
                    "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01" }, result.FailedStepCodes);

            var stage = AgentProgressStore.Load(_agentDir)!.Stages.Single(s => s.Id == "01-S01");

            Assert.Equal(StageStatus.Failed, stage.Status);
            Assert.Contains("L2 MISMATCH", stage.LastGapSummary);

            // 피드백은 7,800줄 지시서의 맨 끝이 아니라 그 회차의 작업 파일에 붙는다.
            var taskFile = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-01-S01.md"));

            Assert.Contains("Critic Feedback - 01-S01", taskFile);
            Assert.Contains("루프 조건이 다릅니다", taskFile);

            // 회차 경로와 전체 Job 경로가 같은 피드백 조립기를 쓴다. 두 벌로 갈라졌을 때
            // 새 경로에서만 빠져 있던 문구다 - 다시 갈라지면 여기서 잡힌다.
            Assert.Contains("지시서 5장의 SQL/ORM 경계 규칙 참조", taskFile);
        }

        /// <summary>
        /// 회차별 L2의 합이 Job 전체 검증을 대신하지만, 단계들이 하나의 파이프라인으로
        /// 엮였는지는 아무 회차도 보지 않는다. 그래서 조립 회차가 마지막에 한 번 전체를 본다.
        /// Bootstrap은 대조할 설계서가 없으므로 L2를 아예 태우지 않는다 - 태우면 계획서 전문을
        /// 상대로 공통 인프라만 있는 트리를 검증하게 되어 반드시 실패하고, 회차 1이 못 돈다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldVerifyStepsInScopeAndTheJobOnlyAtAssembly()
        {
            var verifiedSpecs = new List<string>();

            var result = await Build(ProductiveEngine(), aiClient: MatchingAiClient(verifiedSpecs))
                .RunStagedWorkflowAsync(
                    JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.AllPassed);

            // Bootstrap은 없다. 단계는 각자 자기 설계서만. 조립에서만 계획서 전체.
            Assert.Equal(new[] { "S01", "S02", "PLAN" }, verifiedSpecs);
        }

        /// <summary>
        /// 미완성 단계가 있으면 Job 전체 대조는 성립하지 않는다(계획서는 전 단계를 요구하는데
        /// 트리에는 일부가 없다). 건너뛰되 그 사실이 진행 기록과 todo.md에 남아야 한다 -
        /// 로그만으로는 사람이 전체 검증이 돌았다고 오해한다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldSkipJobWideVerification_WhenAStepIsUnfinished()
        {
            var verifiedSpecs = new List<string>();

            await Build(EngineFailingOn("task-01-S01.md"), aiClient: MatchingAiClient(verifiedSpecs))
                .RunStagedWorkflowAsync(
                    JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            // S01은 산출물이 없어 검증에 닿지 못했고, 조립은 계획서를 태우지 않았다.
            Assert.Equal(new[] { "S02" }, verifiedSpecs);

            var assembly = AgentProgressStore.Load(_agentDir)!.Stages.Single(s => s.Id == "99-assembly");

            // 조립 작업 자체는 했으므로 통과지만, 무엇을 건너뛰었는지가 남는다.
            Assert.Equal(StageStatus.Passed, assembly.Status);
            Assert.Contains("Job 전체 검증 건너뜀", assembly.LastGapSummary);
            Assert.Contains("S01", assembly.LastGapSummary);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("Job 전체 검증 건너뜀", todo);
        }

        /// <summary>
        /// 할당량 소진·인증 실패·도구 권한 거부는 다음 회차에서도 똑같이 실패한다. 남은 회차를
        /// 각각 같은 벽에 부딪히게 두지 않고 끝낸다. 이때 남은 회차는 Failed가 아니라
        /// Pending으로 남아야 재시도할 때 "실패한 것"과 "돌려보지도 않은 것"이 구별된다.
        /// </summary>
        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        public async Task RunStagedWorkflowAsync_ShouldStopRemainingStages_OnEnvironmentFailure(
            CliFailureKind kind)
        {
            var engine = EngineFailingWith("task-01-S01.md", kind);

            var result = await Build(engine).RunStagedWorkflowAsync(
                JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.NotNull(result.AbortReason);
            Assert.Contains(kind.ToString(), result.AbortReason);

            var calls = InstructionCalls(engine);

            Assert.DoesNotContain("task-02-S02.md", calls);
            Assert.DoesNotContain("task-99-assembly.md", calls);

            var stages = AgentProgressStore.Load(_agentDir)!.Stages;

            Assert.Equal(StageStatus.Failed, stages.Single(s => s.Id == "01-S01").Status);
            Assert.Equal(StageStatus.Pending, stages.Single(s => s.Id == "02-S02").Status);
            Assert.Equal(StageStatus.Pending, stages.Single(s => s.Id == "99-assembly").Status);
        }

        /// <summary>
        /// 단계 분할에 실패해 대조할 설계서가 없는 회차. 검증하지 못했으므로 통과가 아니다 -
        /// 통과로 두면 이 태스크가 막으려던 구멍이 방향만 뒤집힌 채 되살아난다.
        /// 재시도로 달라질 것이 없으므로 그 자리에서 접는다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldFailStepThatHasNoSpecToCompareAgainst()
        {
            var engine = ProductiveEngine();

            var result = await Build(engine).RunStagedWorkflowAsync(
                JobName, Plan(stepsSplit: false), _agentDir, _codeDir,
                isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01", "S02" }, result.FailedStepCodes);

            var stage = AgentProgressStore.Load(_agentDir)!.Stages.Single(s => s.Id == "01-S01");

            Assert.Equal(StageStatus.Failed, stage.Status);
            Assert.Contains("대조할 설계서 경로가 없어", stage.LastGapSummary);

            // 재시도해도 번들이 다시 만들어지지 않는 한 같은 결과다. 한 번만 기동한다.
            Assert.Equal(1, InstructionCalls(engine).Count(c => c == "task-01-S01.md"));
        }

        /// <summary>
        /// "이 회차의 코드를 못 찾음" 분기는 산출물이 나왔으므로 무산출물 캡에 걸리지 않는다.
        /// 상한이 없으면 MaxL2Attempts가 "unlimited"일 때 유료 기동이 끝나지 않는다.
        /// 진전을 낼 수 있도록 이름 규약을 피드백으로 주고, 그래도 안 되면 접는다.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task RunStagedWorkflowAsync_ShouldCapRetries_WhenTheStepSourceNeverAppears()
        {
            var engine = EngineProducingUnrelatedOutputFor("task-01-S01.md");

            // -1 = unlimited. 상한이 없으면 이 호출은 끝나지 않는다.
            var result = await Build(engine, maxAttempts: -1).RunStagedWorkflowAsync(
                JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01" }, result.FailedStepCodes);
            Assert.Equal(2, InstructionCalls(engine).Count(c => c == "task-01-S01.md"));

            // 다음 시도가 진전을 낼 수 있도록 파일 이름 규약을 알려 준다.
            var taskFile = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-01-S01.md"));

            Assert.Contains("회차 산출물 확인 실패", taskFile);
            Assert.Contains("`S01`로 시작해야 합니다", taskFile);
        }

        /// <summary>
        /// 계획이 그대로인 재실행(크래시 후 재기동)이 통과 기록 N개를 지우고 전 회차를 다시
        /// 돌린다. 가장 흔하고 가장 비싼 경우이므로 아무 흔적 없이 일어나서는 안 된다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldReportReplacingPreviousProgress_EvenWhenPlanIsUnchanged()
        {
            await Build(ProductiveEngine()).RunStagedWorkflowAsync(
                JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
            try
            {
                await Build(ProductiveEngine()).RunStagedWorkflowAsync(
                    JobName, Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);
            }
            finally
            {
                Log.Logger = previousLogger;
            }

            var replaced = sink.Messages.Single(m => m.Contains("이전 진행 기록을 대체"));

            Assert.Contains("통과 4개", replaced);
            Assert.Contains("회차 구성 변경: False", replaced);
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();

            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }

        /// <summary>
        /// 회차 목록의 진실은 방금 쓰인 task 파일에서 파생된 CodegenStagePlan이지
        /// 지난 실행이 남긴 progress.json이 아니다. 계획이 다시 생성되어 단계 집합이
        /// 바뀌면 옛 목록을 이어받지 않고 현재 계획으로 새로 시작해야 한다.
        /// </summary>
        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldRestartFromCurrentPlanWhenPersistedStagesDiverge()
        {
            await File.WriteAllTextAsync(
                Path.Combine(_agentDir, "progress.json"),
                """
                {
                  "JobName": "JobX",
                  "Stages": [
                    {
                      "Id": "07-SOLD",
                      "StepCode": "SOLD",
                      "TaskFileName": "task-07-SOLD.md",
                      "Status": "Passed",
                      "Attempts": 1,
                      "LastGapSummary": null
                    }
                  ]
                }
                """);

            await Build(ProductiveEngine()).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var progress = AgentProgressStore.Load(_agentDir);

            Assert.NotNull(progress);
            Assert.Equal(
                new[] { "00-bootstrap", "01-S01", "02-S02", "99-assembly" },
                progress!.Stages.Select(s => s.Id).ToArray());
        }
    }
}
