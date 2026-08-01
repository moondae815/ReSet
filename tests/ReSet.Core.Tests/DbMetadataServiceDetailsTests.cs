using System;
using System.Reflection;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DbMetadataServiceDetailsTests
    {
        [Fact]
        public async Task GetSpDetailsAsync_WithInvalidConn_ShouldThrowException()
        {
            // Arrange
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            IDbMetadataService service = new DbMetadataService();

            // Act & Assert
            // maxDepth=3 인자를 전달하여 호출 시그니처 변경에 따른 오류 유발 및 1차 예외 통과 확인
            await Assert.ThrowsAnyAsync<Exception>(() => service.GetSpDetailsAsync(invalidConnString, "dbo", "USP_NonExistent", 3));
        }

        [Theory]
        [InlineData("P", CodeObjectType.Procedure)]
        [InlineData("P ", CodeObjectType.Procedure)]
        [InlineData("PC", CodeObjectType.Procedure)]
        [InlineData("FN", CodeObjectType.Function)]
        [InlineData("IF", CodeObjectType.Function)]
        [InlineData("TF", CodeObjectType.Function)]
        [InlineData("FS", CodeObjectType.Function)]
        [InlineData("FT", CodeObjectType.Function)]
        public void NormalizeCodeObjectType_MapsSupportedSqlServerTypes(
            string sqlServerType,
            CodeObjectType expected)
        {
            var method = typeof(DbMetadataService).GetMethod(
                "NormalizeCodeObjectType",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.Equal(expected, method.Invoke(null, new object[] { sqlServerType }));
        }

        [Fact]
        public void BuildVisitedObjectName_DistinguishesSameNamedObjectsInDifferentDatabases()
        {
            var method = typeof(DbMetadataService).GetMethod(
                "BuildVisitedObjectName",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var paymentObject = method.Invoke(
                null,
                new object[] { "PaymentDB", "dbo", "usp_Shared" });
            var auditObject = method.Invoke(
                null,
                new object[] { "AuditDB", "dbo", "usp_Shared" });

            Assert.Equal("PaymentDB.dbo.usp_Shared", paymentObject);
            Assert.Equal("AuditDB.dbo.usp_Shared", auditObject);
            Assert.NotEqual(paymentObject, auditObject);
        }

        [Theory]
        [InlineData(null, "AuditDB")]
        [InlineData("PaymentDB", "PaymentDB")]
        public void ResolveDependencyDatabase_UsesSourceDatabaseForUnqualifiedDependency(
            string? dependencyDatabase,
            string expected)
        {
            var method = typeof(DbMetadataService).GetMethod(
                "ResolveDependencyDatabase",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sourceKey = CodeObjectKey.Create(
                "AuditDB", "dbo", "usp_Source", CodeObjectType.Procedure);

            Assert.NotNull(method);
            Assert.Equal(
                expected,
                method.Invoke(null, new object?[] { dependencyDatabase, sourceKey }));
        }

        [Fact]
        public void ResolveDynamicDependencyDatabases_UsesSourceForLookupAndNullForStorage()
        {
            var method = typeof(DbMetadataService).GetMethod(
                "ResolveDynamicDependencyDatabases",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sourceKey = CodeObjectKey.Create(
                "AuditDB", "dbo", "usp_Source", CodeObjectType.Procedure);

            Assert.NotNull(method);
            var result = ((string LookupDatabase, string? StoredDatabase))method.Invoke(
                null,
                new object?[] { null, sourceKey })!;

            Assert.Equal("AuditDB", result.LookupDatabase);
            Assert.Null(result.StoredDatabase);
        }

        [Theory]
        [InlineData("", "PaymentDB", "PaymentDB")]
        [InlineData("ConfiguredDB", "ConnectedDB", "ConfiguredDB")]
        public void ResolveCurrentDatabase_UsesConnectedDatabaseWhenCatalogIsMissing(
            string configuredDatabase,
            string connectedDatabase,
            string expected)
        {
            var method = typeof(DbMetadataService).GetMethod(
                "ResolveCurrentDatabase",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.Equal(
                expected,
                method.Invoke(
                    null,
                    new object[] { configuredDatabase, connectedDatabase }));
        }

        [Theory]
        [InlineData("SQL_TABLE_VALUED_FUNCTION")]
        [InlineData("SQL_INLINE_TABLE_VALUED_FUNCTION")]
        [InlineData("CLR_TABLE_VALUED_FUNCTION")]
        public void DirectDependencyClassification_TreatsTableValuedFunctionsAsCodeObjects(
            string dependencyType)
        {
            var tableMethod = typeof(DbMetadataService).GetMethod(
                "IsTableOrViewType",
                BindingFlags.NonPublic | BindingFlags.Static);
            var codeMethod = typeof(DbMetadataService).GetMethod(
                "IsCodeObjectType",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(tableMethod);
            Assert.NotNull(codeMethod);
            Assert.False((bool)tableMethod.Invoke(null, new object?[] { dependencyType })!);
            Assert.True((bool)codeMethod.Invoke(null, new object?[] { dependencyType })!);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("AuditDB")]
        public async Task GetDatabaseCompatibilityLevelAsync_WithInvalidConnection_FallsBackTo160(
            string? database)
        {
            var method = typeof(DbMetadataService).GetMethod(
                "GetDatabaseCompatibilityLevelAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(method);
            var invalidConnString =
                "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var task = (Task<int>)method.Invoke(
                new DbMetadataService(),
                new object?[] { invalidConnString, database, CancellationToken.None })!;

            Assert.Equal(160, await task);
        }
    }
}
