using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Cli;
using ReSet.Core.Models;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchStepCatalogTests
    {
        [Fact]
        public void FindStepCandidates_ReturnsProcedureSpecsFromCurrentAndExternalDatabases()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(
                    new[]
                    {
                        "External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md",
                        "Procedures/dbo.USP_Root/docs/Spec.md"
                    },
                    candidates);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ExcludesFunctionsAndJobArtifacts()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .ToList();

                Assert.DoesNotContain(candidates, path => path.Contains("/Functions/"));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Functions/", StringComparison.Ordinal));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Jobs/", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ReturnsEmptyWhenOutputRootIsMissing()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"ReSet-Missing-{Guid.NewGuid():N}");

            Assert.Empty(BatchStepCatalog.FindStepCandidates(missing));
        }

        [Fact]
        public async Task LoadDefinitionsAsync_PreservesInputOrder()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_First", "USP_First");
            WriteProcedure(root, "dbo.USP_Second", "USP_Second");
            WriteProcedure(root, "dbo.USP_Third", "USP_Third");
            try
            {
                var ordered = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Third", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_First", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_Second", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, ordered, CancellationToken.None);

                Assert.Equal(
                    new[] { "USP_Third", "USP_First", "USP_Second" },
                    result.Definitions.Select(definition => definition.Name));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsMissingMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Complete", "USP_Complete");
            WriteSpecOnly(root, "dbo.USP_NoMetadata");
            try
            {
                var selected = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Complete", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Equal("USP_Complete", Assert.Single(result.Definitions).Name);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md"),
                    Assert.Single(result.MissingMetadata));
                Assert.Empty(result.FailedToParse);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsUnparsableMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Broken", "USP_Broken");
            File.WriteAllText(
                Path.Combine(root, "Procedures", "dbo.USP_Broken", "raw", "metadata.json"),
                "{ this is not json");
            try
            {
                var selected = new[] { Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md") };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Empty(result.Definitions);
                Assert.Empty(result.MissingMetadata);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md"),
                    Assert.Single(result.FailedToParse));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void WriteProcedure(string root, string objectDirectory, string procedureName)
        {
            WriteSpecOnly(root, objectDirectory);
            var rawDirectory = Path.Combine(root, "Procedures", objectDirectory, "raw");
            Directory.CreateDirectory(rawDirectory);
            var definition = new SpDefinition { Schema = "dbo", Name = procedureName, DdlText = "SELECT 1;" };
            File.WriteAllText(
                Path.Combine(rawDirectory, "metadata.json"),
                System.Text.Json.JsonSerializer.Serialize(definition));
        }

        private static void WriteSpecOnly(string root, string objectDirectory)
        {
            var docsDirectory = Path.Combine(root, "Procedures", objectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }

        private static string CreateOutputTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchCatalog-{Guid.NewGuid():N}");
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Root"));
            WriteSpec(root, Path.Combine("Functions", "dbo.UF_Helper"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Procedures", "dbo.USP_External"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Functions", "dbo.UF_ExternalHelper"));
            WriteSpec(root, Path.Combine("Jobs", "Nightly", "validation", "raw"));
            return root;
        }

        private static void WriteSpec(string root, string relativeObjectDirectory)
        {
            var docsDirectory = Path.Combine(root, relativeObjectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }
    }
}
