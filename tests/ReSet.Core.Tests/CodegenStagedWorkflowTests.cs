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
        private readonly string _root;
        private readonly string _agentDir;
        private readonly string _codeDir;

        public CodegenStagedWorkflowTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-staged-" + Guid.NewGuid().ToString("N"));
            _agentDir = Path.Combine(_root, "agent");
            _codeDir = Path.Combine(_root, "src");
            Directory.CreateDirectory(_agentDir);
            Directory.CreateDirectory(_codeDir);

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

        private CodegenStagePlan Plan() => CodegenStagePlan.FromBundle(
            new BundleResult(
                Path.Combine(_agentDir, "MigrationInstructions.md"),
                new[] { "S01", "S02" },
                Array.Empty<string>(),
                StepsSplit: true,
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
                SpecDirectory = Path.Combine(_root, "empty-spec"),
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };
            Directory.CreateDirectory(config.SpecDirectory);

            var verifier = new CodeVerificationOrchestrator(
                config, aiClient ?? MatchingAiClient(), null, null);

            return new CodegenWorkflowOrchestrator(
                engine, verifier, new MetadataExporter(), maxAttempts);
        }

        /// <summary>L2가 항상 MATCH를 내도록 세운 AI 클라이언트.</summary>
        private static IAiClient MatchingAiClient()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("stub");
            client.ModelName.Returns("stub-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "{\"OverallStatus\": \"MATCH\"}" }));
            return client;
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
                "assembly" => "PipelineHost",
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
