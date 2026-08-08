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
            // 공개 API로 확인한다. DbMetadataService가 실제로 이 분류기에
            // 위임하는지는 별도 가드 테스트(DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier)가 확인한다.
            Assert.False(SqlObjectTypeClassifier.IsTableOrView(dependencyType));
            Assert.True(SqlObjectTypeClassifier.IsCodeObject(dependencyType));
        }

        [Fact]
        public void DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier()
        {
            // 이 가드가 지키는 불변식: 직접 의존성 경로(703, 726행)와 재귀 경로(819, 835행)
            // 양쪽 모두 SqlObjectTypeClassifier에 위임해야 한다. 둘 중 하나라도
            // "rawDep.Type.Contains(...)" 같은 인라인 부분 문자열 판정이나, 삭제된
            // private 메서드 IsTableOrViewType/IsCodeObjectType의 부활로 되돌아가면,
            // "SQL_TABLE_VALUED_FUNCTION"이 "TABLE"을 포함하기 때문에 TVF가 다시
            // 테이블로 오분류되고 UIF_SettleYMD 같은 함수의 DDL이 다시 수집되지 않는다.
            //
            // 단순 Assert.Contains만으로는 두 호출부 중 한쪽만 원복해도 다른 쪽의
            // 리터럴이 파일에 여전히 남아 있어 걸리지 않는다(라운드 2 리뷰에서 지적됨).
            // 그래서 등장 횟수를 함께 확인한다: 정확히 2가 아니라 최소 2인 이유는,
            // 정당한 호출부가 나중에 하나 더 늘어도(예: 세 번째 경로가 추가되는 경우)
            // 이 가드가 무고하게 깨지지 않게 하기 위해서다. 그래도 두 호출부 중
            // 하나만 인라인 판정으로 되돌아가면 횟수가 1로 떨어져 여전히 잡힌다.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/DbMetadataService.cs"));

            Assert.True(
                CountOccurrences(source, "SqlObjectTypeClassifier.IsTableOrView(") >= 2,
                "직접 의존성 경로와 재귀 경로 양쪽 모두 IsTableOrView에 위임해야 한다.");
            Assert.True(
                CountOccurrences(source, "SqlObjectTypeClassifier.IsCodeObject(") >= 2,
                "직접 의존성 경로와 재귀 경로 양쪽 모두 IsCodeObject에 위임해야 한다.");

            Assert.DoesNotContain("rawDep.Type.Contains(\"TABLE\")", source);

            // 삭제된 private 메서드의 정의 자체가 되돌아오지 않았는지 확인한다.
            // 이것이 실제 회귀 형태였다: 호출부 리터럴이 아니라 메서드 정의가
            // 되살아나는 것.
            Assert.DoesNotContain("private static bool IsTableOrViewType", source);
            Assert.DoesNotContain("private static bool IsCodeObjectType", source);
        }

        private static int CountOccurrences(string source, string literal) =>
            (source.Length - source.Replace(literal, string.Empty).Length) / literal.Length;

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
