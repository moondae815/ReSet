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
    }
}
