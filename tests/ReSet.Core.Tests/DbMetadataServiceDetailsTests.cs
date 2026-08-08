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

        [Fact]
        public void ResolveCatalogKey_AdoptsCatalogSchemaAndObjectNameCasing()
        {
            var method = typeof(DbMetadataService).GetMethod(
                "ResolveCatalogKey",
                BindingFlags.NonPublic | BindingFlags.Static);
            var requestedKey = CodeObjectKey.Create(
                "PaymentDB", "DBO", "UF_Get_WorkDay2", CodeObjectType.Function);

            Assert.NotNull(method);
            var resolved = (CodeObjectKey)method.Invoke(
                null,
                new object?[] { requestedKey, "dbo", "UF_GET_WORKDAY2" })!;

            Assert.Equal("dbo", resolved.Schema);
            Assert.Equal("UF_GET_WORKDAY2", resolved.Name);
            Assert.Equal("PaymentDB", resolved.Database);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "  ")]
        public void ResolveCatalogKey_KeepsRequestedNameWhenCatalogValuesAreMissing(
            string? catalogSchema,
            string? catalogName)
        {
            var method = typeof(DbMetadataService).GetMethod(
                "ResolveCatalogKey",
                BindingFlags.NonPublic | BindingFlags.Static);
            var requestedKey = CodeObjectKey.Create(
                "PaymentDB", "dbo", "UF_Get_WorkDay2", CodeObjectType.Function);

            Assert.NotNull(method);
            var resolved = (CodeObjectKey)method.Invoke(
                null,
                new object?[] { requestedKey, catalogSchema, catalogName })!;

            Assert.Equal("dbo", resolved.Schema);
            Assert.Equal("UF_Get_WorkDay2", resolved.Name);
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
        public void SqlObjectTypeClassifier_TreatsTableValuedFunctionsAsCodeObjects(
            string dependencyType)
        {
            // 분류 판정은 SqlObjectTypeClassifier로 이전되었다(두 private 메서드는
            // 삭제됨). 여기서는 DbMetadataService가 아니라 그 분류기 자체를
            // 공개 API로 확인한다. 호출부가 실제로 이 분류기에 위임하는지는
            // TypeClassificationPolicyTests가 src/ 전체를 구문 트리로 훑어 확인한다.
            Assert.False(SqlObjectTypeClassifier.IsTableOrView(dependencyType));
            Assert.True(SqlObjectTypeClassifier.IsCodeObject(dependencyType));
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

        [Fact]
        public void DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning()
        {
            // 배선이 빠지면 조용히 예전 표기가 저장된다. 호출이 존재하는지뿐 아니라
            // 그 호출에 넘기는 인자까지 고정한다: DB 컨텍스트는 ObjectKey에서,
            // 스키마는 정의 자체에서 와야 한다. 호출만 있고 엉뚱한 인자(예: null, null)를
            // 넘기면 컴파일은 되지만 정규화가 무력화되는데, Assert.Contains 하나로는
            // 그 실수를 잡지 못한다(Task 3 리뷰에서 지적된 것과 같은 종류의 약한 가드).
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/DbMetadataService.cs"));

            const string callPrefix = "StaticAnalysisNormalizer.Normalize(";
            Assert.Contains(callPrefix, source);

            var callStart = source.IndexOf(callPrefix, StringComparison.Ordinal);
            var callEnd = source.IndexOf(");", callStart, StringComparison.Ordinal);
            Assert.True(callEnd > callStart, "StaticAnalysisNormalizer.Normalize 호출의 닫는 괄호를 찾지 못했다.");
            var callSite = source.Substring(callStart, callEnd - callStart);

            Assert.Contains("objectDefinition.ObjectKey?.Database", callSite);
            Assert.Contains("objectDefinition.Schema", callSite);

            // 존재 확인만으로는 두 인자가 자리를 바꿔도 (Normalize(analysis,
            // objectDefinition.Schema, objectDefinition.ObjectKey?.Database)) 잡히지
            // 않는다 - 두 리터럴이 여전히 callSite 안에 있기 때문이다. 계약(§3)은
            // 첫 인자가 DB, 둘째가 스키마이므로, 자리가 바뀌면 컴파일은 되지만
            // 정규화가 조용히 틀린 컨텍스트로 수행된다. 두 리터럴이 서로의 부분
            // 문자열이 아니므로(하나가 "ObjectKey?.Database"로 끝나고 다른 하나는
            // "Schema"로 끝난다) 등장 순서를 그대로 인자 순서로 볼 수 있다.
            var databaseArgIndex = callSite.IndexOf("objectDefinition.ObjectKey?.Database", StringComparison.Ordinal);
            var schemaArgIndex = callSite.IndexOf("objectDefinition.Schema", StringComparison.Ordinal);
            Assert.True(
                databaseArgIndex < schemaArgIndex,
                "DB 인자(ObjectKey?.Database)는 스키마 인자(Schema)보다 먼저 나와야 한다.");
        }
    }
}
