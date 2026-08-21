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

            // 조회는 이제 스냅샷 인스턴스를 복제해 돌려준다(공유 상태 오염 방지) — 그래서
            // 저장된 expectedDef 자체와의 전체 동일성이 아니라 실려온 값이 같은지를 본다.
            Assert.Equal(expectedDef.Name, sp.Name);
            Assert.Equal(expectedDef.Schema, sp.Schema);
            Assert.Equal(
                CodeObjectKey.Create(
                    "PaymentDB",
                    "dbo",
                    "TestSp",
                    CodeObjectType.Procedure),
                sp.ObjectKey);
        }

        [Fact]
        public async Task GetSpDetailsAsync_DoesNotMutateStoredSnapshotInstance()
        {
            // 위 테스트가 완화된 이유를 직접 증명한다: 조회 후에도 스냅샷 원본의
            // ObjectKey는 null로 남아 있어야 한다. 남아 있지 않다면 반환값이 원본을
            // 그대로 변형한 것이고, 두 번째 조회가 첫 번째 조회에 오염된다.
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            var storedDef = new SpDefinition { Name = "TestSp", Schema = "dbo" };
            snapshot.StoredProcedures.Add("dbo.TestSp", storedDef);

            var service = new OfflineDbMetadataService(snapshot);
            await service.GetSpDetailsAsync("dummy", "dbo", "TestSp", 1, CancellationToken.None);

            Assert.Null(storedDef.ObjectKey);
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
        public async Task GetCodeObjectDetailsDirectAsync_NormalizesDependencyNameToCatalogCasing()
        {
            // 2026-08-20 축 A 교차 감사 실측. sys.sql_expression_dependencies의
            // referenced_entity_name은 카탈로그 표기가 아니라 호출식에 쓰인 표기를
            // 돌려준다. T-SQL이 대소문자를 안 가리므로 원본이 dbo.UF_Get_WorkDay2로
            // 부르면 의존성 이름도 그 표기로 들어온다.
            //
            // 산출물 디렉터리는 그 함수를 직접 분석할 때 쓴 카탈로그 표기
            // (dbo.UF_GET_WORKDAY2)로 만들어지므로, 의존성 이름을 그대로 경로에 쓰면
            // 대소문자 구분 파일시스템에서 깨지는 링크가 나온다. 실제로
            // UP_UTIL_SETTLE_INS_EXTRA의 「참조 함수」 표 링크가 그렇게 어긋났고,
            // 같은 문서의 「참조 코드 객체」 링크는 그래프 키를 써서 정본이라
            // 문서 내부에서 서로 불일치했다.
            //
            // 스냅샷에 저장된 객체가 카탈로그 표기를 갖고 있으므로, 의존성을 그것에
            // 맞춰 두면 아래로 흐르는 모든 소비자(매니페스트·링커·프롬프트 표)가
            // 한 표기로 모인다.
            var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "SP_Root", CodeObjectType.Procedure);
            var functionKey = CodeObjectKey.Create("PaymentDB", "dbo", "UF_GET_WORKDAY2", CodeObjectType.Function);

            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[rootKey.CanonicalName] = new SpDefinition
            {
                ObjectKey = rootKey,
                Schema = rootKey.Schema,
                Name = rootKey.Name,
                Dependencies = new List<DependencyInfo>
                {
                    new()
                    {
                        SourceObjectKey = rootKey,
                        Schema = "dbo",
                        Name = "UF_Get_WorkDay2",   // 호출식 표기
                        Type = "SQL_SCALAR_FUNCTION"
                    }
                }
            };
            snapshot.CodeObjects[functionKey.CanonicalName] = new SpDefinition
            {
                ObjectKey = functionKey,
                Schema = functionKey.Schema,
                Name = functionKey.Name,            // 카탈로그 표기
                DdlText = "CREATE FUNCTION dbo.UF_GET_WORKDAY2() RETURNS char(8) AS BEGIN RETURN '' END"
            };

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsDirectAsync("ignored", rootKey);

            var dependency = Assert.Single(result.Dependencies);
            Assert.Equal("UF_GET_WORKDAY2", dependency.Name);
            Assert.StartsWith("CREATE FUNCTION", dependency.ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_NormalizesNameEvenWhenDdlAlreadyPresent()
        {
            // 2026-08-20 리뷰 Critical. 정규화를 DDL 재연결 루프 안에 두면
            // `if (ReferencedDdlText가 이미 있으면) continue;` 가드 뒤에 놓여, DDL이
            // 채워져 있는 <경우>에는 아예 실행되지 않는다. 그런데 온라인 추출기는
            // 코드 객체 의존성의 DDL을 항상 채우고(DbMetadataService) 스냅샷은 그
            // 객체를 그대로 저장하므로, 실제로 감사에서 어긋난 자리가 바로 그 경로다.
            //
            // 즉 이 테스트가 없으면 "고쳤다"는 수정이 정작 문제가 난 경로에서
            // 한 번도 돌지 않는다.
            var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "SP_Root", CodeObjectType.Procedure);
            var functionKey = CodeObjectKey.Create("PaymentDB", "dbo", "UF_GET_WORKDAY2", CodeObjectType.Function);

            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[rootKey.CanonicalName] = new SpDefinition
            {
                ObjectKey = rootKey,
                Schema = rootKey.Schema,
                Name = rootKey.Name,
                Dependencies = new List<DependencyInfo>
                {
                    new()
                    {
                        SourceObjectKey = rootKey,
                        Schema = "dbo",
                        Name = "UF_Get_WorkDay2",   // 호출식 표기
                        Type = "SQL_SCALAR_FUNCTION",
                        // 온라인 추출기가 이미 채워 둔 상태 - 감사에서 어긋난 실제 경로다.
                        ReferencedDdlText = "CREATE FUNCTION dbo.UF_GET_WORKDAY2() RETURNS char(8) AS BEGIN RETURN '' END"
                    }
                }
            };
            snapshot.CodeObjects[functionKey.CanonicalName] = new SpDefinition
            {
                ObjectKey = functionKey,
                Schema = functionKey.Schema,
                Name = functionKey.Name,            // 카탈로그 표기
                DdlText = "CREATE FUNCTION dbo.UF_GET_WORKDAY2() RETURNS char(8) AS BEGIN RETURN '' END"
            };

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsDirectAsync("ignored", rootKey);

            Assert.Equal("UF_GET_WORKDAY2", Assert.Single(result.Dependencies).Name);
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

        [Fact]
        public async Task GetSpDetailsAsync_ShouldReparseStoredDdlInsteadOfReplayingStaleAnalysis()
        {
            // 기본 설정(AnalyzeReferencedCodeObjects=false)에서는 GetCodeObjectDetailsDirectAsync가
            // 아니라 GetSpDetailsAsync/GetCodeObjectDetailsAsync 경로를 탄다. 그 경로도
            // 재파싱을 거치지 않으면 스냅샷 시점의 옛 분석이 계속 재생된다.
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

            var definition = await service.GetSpDetailsAsync(
                "dummy", "dbo", "UP_TEST", 1, CancellationToken.None);

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.UpdateTables);
            Assert.Contains("SETTLE_POQ_DB.dbo.TClientCMRate", definition.StaticAnalysis.SelectTables);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_ShouldRelinkCodeObjectDdlFromSnapshot()
        {
            // UIF_SettleYMD의 DDL은 CodeObjects에 들어 있는데 의존성 항목의 링크만 비어 있다.
            // 이 경로(재귀 기본 경로)에서도 재링크가 일어나야 한다.
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

            var definition = await service.GetCodeObjectDetailsAsync("dummy", objectKey, 1, CancellationToken.None);

            var dependency = Assert.Single(definition.Dependencies);
            Assert.Contains("RETURNS TABLE", dependency.ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_CalledTwice_ReturnsIndependentReanalyzedCopiesWithoutMutatingSnapshot()
        {
            // GetCodeObjectDetailsAsync는 스냅샷 딕셔너리가 들고 있는 바로 그 인스턴스를
            // 돌려주고 그 위에 ObjectKey를 대입해 왔다. 재분석 결과까지 그 위에 얹으면
            // 다음 조회가 오염되고, 같은 객체를 두 번 조회할 때 결과가 달라질 수 있다.
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
    FROM   TSettleMst A;
END;
",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    UpdateTables = new List<string> { "A", "TSettleMst" }
                }
            };
            snapshot.StoredProcedures.Add("dbo.UP_TEST", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var first = await service.GetCodeObjectDetailsAsync("dummy", objectKey, 1, CancellationToken.None);
            var second = await service.GetCodeObjectDetailsAsync("dummy", objectKey, 1, CancellationToken.None);

            Assert.NotSame(first, second);
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                first.StaticAnalysis.UpdateTables);
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                second.StaticAnalysis.UpdateTables);

            // 스냅샷 원본 엔트리는 여전히 옛(재파싱 이전) 분석을 그대로 담고 있어야 한다 —
            // 조회가 원본을 변형했다면 이 값이 이미 정규화된 값으로 바뀌어 있을 것이다.
            Assert.Equal(new[] { "A", "TSettleMst" }, stored.StaticAnalysis.UpdateTables);
        }
    }
}
