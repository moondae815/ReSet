using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SnapshotManagerTests
    {
        [Fact]
        public async Task ExportSnapshotAsync_StoresRootProcedureAndFunctionDependencyAsCodeObjects()
        {
            const string connectionString =
                "Server=localhost;Database=PaymentDB;Integrated Security=true;TrustServerCertificate=true;";
            var rootKey = CodeObjectKey.Create(
                "PaymentDB", "dbo", "usp_Root", CodeObjectType.Procedure);
            var functionKey = CodeObjectKey.Create(
                "AuditDB", "dbo", "FN_Calc", CodeObjectType.Function);
            var rootDefinition = new SpDefinition
            {
                Name = "usp_Root",
                Schema = "dbo",
                Dependencies =
                {
                    new DependencyInfo
                    {
                        SourceObjectKey = CodeObjectKey.Create(
                            "AuditDB", "dbo", "usp_Audit", CodeObjectType.Procedure),
                        Schema = "dbo",
                        Name = "FN_Calc",
                        Type = "SQL_SCALAR_FUNCTION"
                    }
                }
            };
            var functionDefinition = new SpDefinition
            {
                Name = "FN_Calc",
                Schema = "dbo",
                ObjectType = CodeObjectType.Function
            };
            var service = Substitute.For<IDbMetadataService>();
            service.GetStoredProcedureNamesAsync(connectionString, Arg.Any<CancellationToken>())
                .Returns(new[] { "dbo.usp_Root" }.ToList());
            service.GetCodeObjectDetailsAsync(
                    connectionString, rootKey, 2, Arg.Any<CancellationToken>())
                .Returns(rootDefinition);
            service.GetCodeObjectDetailsAsync(
                    connectionString, functionKey, 2, Arg.Any<CancellationToken>())
                .Returns(functionDefinition);
            var outputPath = Path.Combine(
                Path.GetTempPath(), $"reset-snapshot-{System.Guid.NewGuid():N}.json");

            try
            {
                await SnapshotManager.ExportSnapshotAsync(
                    service,
                    connectionString,
                    outputPath,
                    2,
                    NullProgressScope.Instance);

                var snapshot = await SnapshotManager.ImportSnapshotAsync(outputPath);

                Assert.Equal(
                    "usp_Root",
                    snapshot.StoredProcedures["dbo.usp_Root"].Name);
                Assert.Equal("usp_Root", snapshot.CodeObjects[rootKey.CanonicalName].Name);
                Assert.Equal(
                    CodeObjectType.Function,
                    snapshot.CodeObjects[functionKey.CanonicalName].ObjectType);
            }
            finally
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }

        [Fact]
        public async Task ExportSnapshotAsync_WithoutInitialCatalog_UsesActualDatabaseInOfflinePipeline()
        {
            const string connectionString =
                "Server=localhost;Integrated Security=true;TrustServerCertificate=true;";
            var rootKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "usp_Root",
                CodeObjectType.Procedure);
            var rootDefinition = new SpDefinition
            {
                ObjectKey = rootKey,
                Name = rootKey.Name,
                Schema = rootKey.Schema,
                DdlText = "CREATE PROCEDURE dbo.usp_Root AS SELECT 1;"
            };
            var onlineService = Substitute.For<IDbMetadataService>();
            onlineService.GetCurrentDatabaseNameAsync(
                    connectionString,
                    Arg.Any<CancellationToken>())
                .Returns("PaymentDB");
            onlineService.GetStoredProcedureNamesAsync(
                    connectionString,
                    Arg.Any<CancellationToken>())
                .Returns(new[] { "dbo.usp_Root" }.ToList());
            onlineService.GetCodeObjectDetailsAsync(
                    connectionString,
                    rootKey,
                    2,
                    Arg.Any<CancellationToken>())
                .Returns(rootDefinition);

            var snapshotPath = Path.Combine(
                Path.GetTempPath(),
                $"reset-snapshot-no-catalog-{Guid.NewGuid():N}.json");
            var outputRoot = Path.Combine(
                Path.GetTempPath(),
                $"reset-offline-output-{Guid.NewGuid():N}");

            try
            {
                await SnapshotManager.ExportSnapshotAsync(
                    onlineService,
                    connectionString,
                    snapshotPath,
                    2,
                    NullProgressScope.Instance);
                var snapshot = await SnapshotManager.ImportSnapshotAsync(snapshotPath);

                Assert.Equal("PaymentDB", snapshot.Database);
                Assert.True(snapshot.CodeObjects.ContainsKey(rootKey.CanonicalName));

                var cacheManager = Substitute.For<ICacheManager>();
                cacheManager.ComputeCompositeHash(
                        Arg.Any<SpDefinition>(),
                        2)
                    .Returns("offline-hash");
                cacheManager.IsCacheValid(
                        rootKey,
                        "offline-hash",
                        Arg.Any<OutputPathResolver>())
                    .Returns(true);
                var paths = new OutputPathResolver(snapshot.Database, outputRoot);
                var specPath = paths.ResolveSpecPath(rootKey);
                Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
                await File.WriteAllTextAsync(specPath, "## Cached offline spec");

                var orchestrator = new VerificationPipelineOrchestrator(
                    new OfflineDbMetadataService(snapshot),
                    Substitute.For<IAiService>(),
                    new MechanicalValidator(),
                    Substitute.For<IVerificationUserInteraction>(),
                    cacheManager: cacheManager);

                var result = await orchestrator.RunPipelineAsync(
                    connectionString,
                    rootKey.Schema,
                    rootKey.Name,
                    2,
                    "OpenAI",
                    "instructions",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.Equal("## Cached offline spec", result.SpecMarkdown);
                cacheManager.Received(1).IsCacheValid(
                    rootKey,
                    "offline-hash",
                    Arg.Is<OutputPathResolver>(resolver =>
                        resolver.ResolveSpecPath(rootKey) == specPath));
            }
            finally
            {
                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }

                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
            }
        }
    }
}
