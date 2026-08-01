using System;
using System.IO;
using System.Linq;
using ReSet.Cli;
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
