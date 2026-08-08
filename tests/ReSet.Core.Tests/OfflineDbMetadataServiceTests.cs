using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OfflineDbMetadataServiceTests
    {
        [Fact]
        public async Task GetStoredProcedureNamesAsync_ReturnsNamesFromSnapshot()
        {
            var snapshot = new DbSnapshot();
            snapshot.StoredProcedures.Add("dbo.TestSp", new SpDefinition { Name = "TestSp", Schema = "dbo" });
            
            var service = new OfflineDbMetadataService(snapshot);
            var names = await service.GetStoredProcedureNamesAsync("dummy_conn", CancellationToken.None);
            
            Assert.Single(names);
            Assert.Contains("dbo.TestSp", names);
        }

        [Fact]
        public async Task GetSpDetailsAsync_ReturnsSpDefinition()
        {
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            var expectedDef = new SpDefinition { Name = "TestSp", Schema = "dbo" };
            snapshot.StoredProcedures.Add("dbo.TestSp", expectedDef);
            
            var service = new OfflineDbMetadataService(snapshot);
            var sp = await service.GetSpDetailsAsync("dummy", "dbo", "TestSp", 1, CancellationToken.None);

            Assert.Equal(expectedDef, sp);
            Assert.Equal(
                CodeObjectKey.Create(
                    "PaymentDB",
                    "dbo",
                    "TestSp",
                    CodeObjectType.Procedure),
                sp.ObjectKey);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_ReturnsFunctionFromCodeObjects()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[key.CanonicalName] = new SpDefinition
            {
                Name = "FN_Calc",
                ObjectType = CodeObjectType.Function
            };

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsAsync("ignored", key, 2);

            Assert.Equal(CodeObjectType.Function, result.ObjectType);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_NormalizesObjectKeyToSnapshotObjectNameCasing()
        {
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            var storedKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "UF_GET_WORKDAY2",
                CodeObjectType.Function);
            snapshot.CodeObjects[storedKey.CanonicalName] = new SpDefinition
            {
                Schema = "dbo",
                Name = "UF_GET_WORKDAY2",
                ObjectType = CodeObjectType.Function
            };
            var callSiteKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "UF_Get_WorkDay2",
                CodeObjectType.Function);

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsAsync("ignored", callSiteKey, 2);

            Assert.Equal("UF_GET_WORKDAY2", result.ObjectKey!.Name);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_FallsBackToLegacyStoredProcedureKey()
        {
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.StoredProcedures["dbo.usp_Legacy"] = new SpDefinition { Name = "usp_Legacy" };
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Legacy", CodeObjectType.Procedure);

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsAsync("ignored", key, 2);

            Assert.Equal("usp_Legacy", result.Name);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_DoesNotUseCurrentDatabaseLegacyEntryForExternalKey()
        {
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.StoredProcedures["dbo.usp_Legacy"] =
                new SpDefinition { Name = "usp_Legacy" };
            var externalKey = CodeObjectKey.Create(
                "AuditDB",
                "dbo",
                "usp_Legacy",
                CodeObjectType.Procedure);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                new OfflineDbMetadataService(snapshot)
                    .GetCodeObjectDetailsAsync("ignored", externalKey, 2));
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_ExcludesExternalRecursiveContextWhenNotAllowed()
        {
            var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Root", CodeObjectType.Procedure);
            var externalKey = CodeObjectKey.Create("AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
            var snapshotDefinition = new SpDefinition
            {
                ObjectKey = rootKey,
                ObjectType = CodeObjectType.Procedure,
                Schema = rootKey.Schema,
                Name = rootKey.Name,
                DdlText = "CREATE PROCEDURE dbo.usp_Root AS SELECT 1",
                RawPromptContext = "external CREATE FUNCTION dbo.FN_Audit",
                Dependencies = new List<DependencyInfo>
                {
                    new()
                    {
                        SourceObjectKey = rootKey,
                        Database = externalKey.Database,
                        Schema = externalKey.Schema,
                        Name = externalKey.Name,
                        Type = "SQL_SCALAR_FUNCTION",
                        DiscoveryDepth = 1,
                        ReferencedDdlText = "CREATE FUNCTION dbo.FN_Audit() RETURNS int AS BEGIN RETURN 1 END"
                    },
                    new()
                    {
                        SourceObjectKey = externalKey,
                        Database = externalKey.Database,
                        Schema = "dbo",
                        Name = "AuditTable",
                        Type = "USER_TABLE",
                        DiscoveryDepth = 2,
                        ReferencedDdlText = "CREATE TABLE dbo.AuditTable (Id int)"
                    }
                }
            };
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[rootKey.CanonicalName] = snapshotDefinition;

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsDirectAsync(
                    "ignored",
                    rootKey,
                    CancellationToken.None,
                    includeExternalCodeObjects: false);

            Assert.NotSame(snapshotDefinition, result);
            Assert.Empty(result.Dependencies);
            Assert.Null(result.RawPromptContext);
            Assert.Equal(2, snapshotDefinition.Dependencies.Count);
            Assert.NotNull(snapshotDefinition.Dependencies[0].ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_PreservesDirectSchemaAndReferencedDdlContext()
        {
            var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Root", CodeObjectType.Procedure);
            var tableDependency = new DependencyInfo
            {
                SourceObjectKey = rootKey,
                Schema = "dbo",
                Name = "Payments",
                Type = "USER_TABLE",
                Description = "결제 원장",
                Columns = new List<ColumnInfo>
                {
                    new() { ColumnName = "PaymentId", DataType = "bigint" }
                },
                Indexes = new List<TableIndexInfo>
                {
                    new() { IndexName = "PK_Payments", IsPrimaryKey = true }
                }
            };
            var functionDependency = new DependencyInfo
            {
                SourceObjectKey = rootKey,
                Schema = "dbo",
                Name = "FN_Fee",
                Type = "SQL_SCALAR_FUNCTION",
                ReferencedDdlText = "CREATE FUNCTION dbo.FN_Fee() RETURNS int AS BEGIN RETURN 1 END"
            };
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[rootKey.CanonicalName] = new SpDefinition
            {
                ObjectKey = rootKey,
                Schema = rootKey.Schema,
                Name = rootKey.Name,
                Dependencies = new List<DependencyInfo> { tableDependency, functionDependency }
            };

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsDirectAsync("ignored", rootKey);

            var table = Assert.Single(result.Dependencies, dependency => dependency.Name == "Payments");
            Assert.Equal("결제 원장", table.Description);
            Assert.Equal("PaymentId", Assert.Single(table.Columns).ColumnName);
            Assert.Equal("PK_Payments", Assert.Single(table.Indexes).IndexName);
            var function = Assert.Single(result.Dependencies, dependency => dependency.Name == "FN_Fee");
            Assert.StartsWith("CREATE FUNCTION", function.ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_WhenMissing_IncludesCanonicalNameInException()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Missing", CodeObjectType.Function);

            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                new OfflineDbMetadataService(new DbSnapshot())
                    .GetCodeObjectDetailsAsync("ignored", key, 2));

            Assert.Contains("PaymentDB.dbo.FN_Missing.Function", exception.Message);
        }

        [Fact]
        public async Task GetTableDataPreviewAsync_ThrowsNotSupportedException()
        {
            var service = new OfflineDbMetadataService(new DbSnapshot());
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.GetTableDataPreviewAsync("dummy", null, "dbo", "Table1", 100, CancellationToken.None));
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_ShouldReparseStoredDdlInsteadOfReplayingStaleAnalysis()
        {
            // 스냅샷에 저장된 StaticAnalysis는 옛 파서가 만든 것이다. 그대로 재생하면
            // 파서를 고쳐도 오프라인 모드는 영원히 예전 결과를 낸다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = @"
CREATE PROCEDURE dbo.UP_TEST
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = '20260808'
    FROM   TSettleMst A
    JOIN   TClientCMRate C ON A.ClientID = C.ClientID;
END;
",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    // 옛 파서의 산출물을 흉내 낸다.
                    UpdateTables = new List<string> { "A", "TSettleMst", "TClientCMRate" }
                }
            };
            snapshot.StoredProcedures.Add("dbo.UP_TEST", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.UpdateTables);
            Assert.Contains("SETTLE_POQ_DB.dbo.TClientCMRate", definition.StaticAnalysis.SelectTables);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_ShouldRelinkCodeObjectDdlFromSnapshot()
        {
            // UIF_SettleYMD의 DDL은 CodeObjects에 들어 있는데 의존성 항목의 링크만 비어 있다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var functionKey = CodeObjectKey.Create(
                "SETTLE_POQ_DB", "dbo", "UIF_SettleYMD", CodeObjectType.Function);
            snapshot.CodeObjects.Add(
                functionKey.CanonicalName,
                new SpDefinition
                {
                    Schema = "dbo",
                    Name = "UIF_SettleYMD",
                    DdlText = "CREATE FUNCTION dbo.UIF_SettleYMD() RETURNS TABLE AS RETURN SELECT 1 AS OutYMD;"
                });

            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "CREATE PROCEDURE dbo.UP_TEST AS BEGIN SELECT 1; END;"
            };
            stored.Dependencies.Add(new DependencyInfo
            {
                SourceObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UIF_SettleYMD",
                Type = "SQL_TABLE_VALUED_FUNCTION",
                ReferencedDdlText = null
            });
            snapshot.StoredProcedures.Add("dbo.UP_TEST", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            var dependency = Assert.Single(definition.Dependencies);
            Assert.Contains("RETURNS TABLE", dependency.ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_WhenDdlCannotBeParsed_ShouldKeepStoredAnalysis()
        {
            // 재파싱이 실패해도 오프라인 모드가 지금보다 나빠지면 안 된다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_BROKEN",
                // SELECT 뒤에 선택 목록 없이 FROM이 오면 T-SQL 문법 오류다.
                DdlText = "CREATE PROCEDURE dbo.UP_BROKEN AS BEGIN SELECT FROM; END;",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    UpdateTables = new List<string> { "TSettleMst" }
                }
            };
            snapshot.StoredProcedures.Add("dbo.UP_BROKEN", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_BROKEN", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            // 저장본이 살아남되 표기는 통일된다.
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.UpdateTables);
        }
    }
}
