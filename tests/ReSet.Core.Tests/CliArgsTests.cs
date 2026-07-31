using System;
using System.IO;
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
                DependencyArtifactMode.Reference,
                CancellationToken.None);

            Assert.Equal("SnapshotDB", dependencyOrchestrator.LastRootKey?.Database);
            Assert.Equal("SnapshotDB", result.SpDef?.ObjectKey?.Database);
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

            public Task<CodeObjectPipelineResult> AnalyzeAsync(
                CodeObjectKey rootKey,
                DependencyAnalysisRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRootKey = rootKey;
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
    }
}
