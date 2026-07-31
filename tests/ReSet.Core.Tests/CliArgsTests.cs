using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Xunit;
using ReSet.Cli;

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
    }
}
