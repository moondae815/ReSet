using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Xunit;
using ReSet.Cli;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CliArgsTests
    {
        [Fact]
        public void AppSettings_DefaultsReferencedCodeObjectAnalysisToFalse()
        {
            var configuration = LoadCliConfiguration();

            Assert.False(configuration.GetValue<bool>("AnalysisSettings:AnalyzeReferencedCodeObjects"));
            Assert.Equal("Reference", configuration["OutputSettings:DependencyArtifactMode"]);
        }

        [Fact]
        public void ParseCommandLineArgs_ShouldBindCorrectly()
        {
            // Arrange
            string[] args = new[] { "--conn", "Server=my_server;", "--all", "--sp", "dbo.USP_1,dbo.USP_2" };

            // Act
            CliArgs result = Program.ParseCommandLineArgs(args);

            // Assert
            Assert.Equal("Server=my_server;", result.ConnectionString);
            Assert.True(result.AnalyzeAll);
            Assert.Equal(2, result.TargetProcedures.Count);
            Assert.Equal("dbo.USP_1", result.TargetProcedures[0]);
            Assert.Equal("dbo.USP_2", result.TargetProcedures[1]);
            Assert.True(result.IsBatchMode);
        }

        [Fact]
        public void ParseCommandLineArgs_CoverageMap_ShouldCaptureTarget()
        {
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map", "POQSettlePrco20" });

            Assert.Equal("POQSettlePrco20", result.CoverageMapTarget);
        }

        [Fact]
        public void ParseCommandLineArgs_CoverageMap_ShouldBeBatchMode()
        {
            // 커버리지 맵은 TUI 로그인 흐름을 타면 안 된다.
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map", "dbo.UP_X" });

            Assert.True(result.IsBatchMode);
        }

        [Fact]
        public void ParseCommandLineArgs_CoverageMapWithoutValue_ShouldLeaveTargetNull()
        {
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map" });

            Assert.Null(result.CoverageMapTarget);
        }

        [Fact]
        public async Task RunConfiguredAnalysisAsync_UsesOfflineSnapshotDatabaseForRecursiveRoot()
        {
            var snapshot = new DbSnapshot { Database = "SnapshotDB" };
            var metadata = new OfflineDbMetadataService(snapshot);
            var dependencyOrchestrator = new CapturingDependencyAnalysisOrchestrator();

            var result = await Program.RunConfiguredAnalysisAsync(
                analyzeReferencedCodeObjects: true,
                dependencyOrchestrator,
                verificationPipelineOrchestrator: null!,
                metadata,
                connectionString: string.Empty,
                configuredDatabase: "ConfiguredDB",
                schema: "dbo",
                name: "usp_Root",
                maxDepth: 2,
                provider: "OpenAI",
                modelName: "gpt-test",
                actorEffort: "high",
                instructions: "rules",
                isBatchMode: true,
                outputDirectory: "/tmp/output",
                enableCache: false,
                allowExternalDatabaseConnections: false,
                DependencyArtifactMode.Reference,
                CancellationToken.None);

            Assert.Equal("SnapshotDB", dependencyOrchestrator.LastRootKey?.Database);
            Assert.Equal("SnapshotDB", result.Definition?.ObjectKey?.Database);
            Assert.True(dependencyOrchestrator.LastRequest?.AnalyzeReferencedCodeObjects);
        }

        /// <summary>
        /// 참조분석 OFF도 오케스트레이터를 탄다. 예전에는 CLI가 파이프라인을 직접 부르고
        /// 저장까지 손으로 했는데, 그 경로에는 dependency-manifest.json도 Objects/ 정본도
        /// 없었고 metadata.json은 스위치 하나에 걸려 있었다.
        /// verificationPipelineOrchestrator에 null을 넘겨도 통과한다는 것이 곧 증거다 —
        /// OFF가 그 인자를 더 이상 역참조하지 않는다.
        /// OFF 명세서의 범위 표기(전이 의존성)는 여기가 아니라 산출물에서 잠근다 —
        /// DependencyAnalysisOrchestratorTests의 AnalyzeAsync_WhenReferencesAreDisabled_StampsTransitiveScope.
        /// </summary>
        [Fact]
        public async Task RunConfiguredAnalysisAsync_WhenReferencesAreDisabled_StillUsesOrchestrator()
        {
            var snapshot = new DbSnapshot { Database = "SnapshotDB" };
            var metadata = new OfflineDbMetadataService(snapshot);
            var dependencyOrchestrator = new CapturingDependencyAnalysisOrchestrator();

            var result = await Program.RunConfiguredAnalysisAsync(
                analyzeReferencedCodeObjects: false,
                dependencyOrchestrator,
                verificationPipelineOrchestrator: null!,
                metadata,
                connectionString: string.Empty,
                configuredDatabase: "ConfiguredDB",
                schema: "dbo",
                name: "usp_Root",
                maxDepth: 2,
                provider: "OpenAI",
                modelName: "gpt-test",
                actorEffort: "high",
                instructions: "rules",
                isBatchMode: true,
                outputDirectory: "/tmp/output",
                enableCache: false,
                allowExternalDatabaseConnections: false,
                DependencyArtifactMode.Reference,
                CancellationToken.None);

            Assert.Equal("SnapshotDB", dependencyOrchestrator.LastRootKey?.Database);
            Assert.False(dependencyOrchestrator.LastRequest?.AnalyzeReferencedCodeObjects);
        }

        [Fact]
        public void RecursiveAnalysisUserInteraction_ForwardsStatusAndProgressScope()
        {
            var interactiveUserInteraction = new RecordingUserInteraction();
            var nestedType = typeof(Program).GetNestedType(
                "RecursiveAnalysisUserInteraction",
                BindingFlags.NonPublic);
            Assert.NotNull(nestedType);

            var adapter = Assert.IsAssignableFrom<IVerificationUserInteraction>(
                Activator.CreateInstance(nestedType!, interactiveUserInteraction));

            adapter.NotifyStatus("UDF: dbo.Child - 검증 중...");
            var progressScope = adapter.CreateProgressScope("Critic 검토");

            Assert.Equal("UDF: dbo.Child - 검증 중...", interactiveUserInteraction.LastStatus);
            Assert.Same(interactiveUserInteraction.ProgressScope, progressScope);
            Assert.Equal("Critic 검토", interactiveUserInteraction.LastProgressTitle);
        }

        private static IConfiguration LoadCliConfiguration()
        {
            var repositoryRoot = FindRepositoryRoot();
            return new ConfigurationBuilder()
                .SetBasePath(Path.Combine(repositoryRoot, "src", "ReSet.Cli"))
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ReSet.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("ReSet 저장소 루트를 찾을 수 없습니다.");
        }

        private sealed class CapturingDependencyAnalysisOrchestrator
            : IDependencyAnalysisOrchestrator
        {
            public CodeObjectKey? LastRootKey { get; private set; }
            public DependencyAnalysisRequest? LastRequest { get; private set; }

            public Task<CodeObjectPipelineResult> AnalyzeAsync(
                CodeObjectKey rootKey,
                DependencyAnalysisRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRootKey = rootKey;
                LastRequest = request;
                var definition = new SpDefinition
                {
                    ObjectKey = rootKey,
                    Schema = rootKey.Schema,
                    Name = rootKey.Name
                };
                return Task.FromResult(new CodeObjectPipelineResult
                {
                    Nodes = new List<AnalysisNode>
                    {
                        new(rootKey) { Status = AnalysisNodeStatus.Succeeded }
                    },
                    AnalysisResults = new List<CodeObjectAnalysisResult>
                    {
                        new()
                        {
                            Key = rootKey,
                            Definition = definition,
                            SpecMarkdown = "# Spec"
                        }
                    }
                });
            }
        }

        private sealed class RecordingUserInteraction : IVerificationUserInteraction
        {
            public string? LastStatus { get; private set; }
            public string? LastProgressTitle { get; private set; }
            public IMultiProgressScope ProgressScope { get; } = new RecordingProgressScope();

            public void NotifyStatus(string message) => LastStatus = message;
            public void NotifyError(string message) { }
            public void NotifyWarnings(string selectedOption, List<string> warnings) { }
            public void NotifyCatalogMismatches(string jobName, List<string> mismatches) { }
            public void NotifyL1Errors(string selectedOption, int attempt, int maxAttempts, List<string> errors) { }
            public void NotifyL2Defects(string selectedOption, int attempt, int maxAttempts, string feedbackComment) { }
            public void NotifyValidationSuccess(string selectedOption) { }
            public Task<HumanReviewResult> RequestHumanReviewAsync(string selectedOption, string specificationMarkdown, VerificationOutcome outcome, bool structureRedraftSupported = false, IReadOnlyList<BatchStepPlan>? steps = null) =>
                Task.FromResult(new HumanReviewResult());
            public Task<bool> ConfirmMetadataSyncAsync(string selectedOption) => Task.FromResult(false);
            public IMultiProgressScope CreateProgressScope(string title)
            {
                LastProgressTitle = title;
                return ProgressScope;
            }
        }

        private sealed class RecordingProgressScope : IMultiProgressScope
        {
            public void AddTask(string taskName, string description) { }
            public void UpdateTask(string taskName, double value, string? description = null) { }
            public void CompleteTask(string taskName) { }
            public void FailTask(string taskName) { }
            public void Dispose() { }
        }
    }
}
