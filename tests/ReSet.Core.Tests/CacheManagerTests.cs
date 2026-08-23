using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CacheManagerTests : IDisposable
    {
        private readonly string _tempOutputDir;
        private readonly CacheManager _cacheManager;
        private readonly OutputPathResolver _paths;

        public CacheManagerTests()
        {
            // 각 테스트 실행 시 임시 디렉토리 생성
            _tempOutputDir = Path.Combine(Path.GetTempPath(), "ReSetTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempOutputDir);
            _cacheManager = new CacheManager();
            _paths = new OutputPathResolver("PaymentDB", _tempOutputDir);
        }

        public void Dispose()
        {
            // 테스트 종료 후 임시 디렉토리 및 파일 정리
            if (Directory.Exists(_tempOutputDir))
            {
                try
                {
                    Directory.Delete(_tempOutputDir, true);
                }
                catch
                {
                    // 무시
                }
            }
        }

        [Fact]
        public void ComputeCompositeHash_IdenticalDefinitions_ReturnsSameHash()
        {
            // Arrange
            var sp1 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "TableA", ReferencedDdlText = "CREATE TABLE TableA (Id INT);" },
                    new DependencyInfo { Schema = "dbo", Name = "TableB", ReferencedDdlText = "CREATE TABLE TableB (Id INT);" }
                }
            };

            var sp2 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;",
                // 의존성 등록 순서가 다름
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "TableB", ReferencedDdlText = "CREATE TABLE TableB (Id INT);" },
                    new DependencyInfo { Schema = "dbo", Name = "TableA", ReferencedDdlText = "CREATE TABLE TableA (Id INT);" }
                }
            };

            // Act
            var hash1 = _cacheManager.ComputeCompositeHash(sp1, 3);
            var hash2 = _cacheManager.ComputeCompositeHash(sp2, 3);

            // Assert
            Assert.False(string.IsNullOrEmpty(hash1));
            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void ComputeCompositeHash_DifferentDefinitions_ReturnsDifferentHash()
        {
            // Arrange
            var sp1 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;"
            };

            var sp2 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 2;" // DDL이 다름
            };

            // Act
            var hash1 = _cacheManager.ComputeCompositeHash(sp1, 3);
            var hash2 = _cacheManager.ComputeCompositeHash(sp2, 3);

            // Assert
            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void ComputeCompositeHash_DependencyIdentityIsCaseInsensitive()
        {
            var upperCaseDefinition = new SpDefinition
            {
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;",
                Dependencies = new List<DependencyInfo>
                {
                    new()
                    {
                        Database = "PaymentDB",
                        Schema = "dbo",
                        Name = "TableA",
                        Type = "TABLE",
                        ReferencedDdlText = "CREATE TABLE dbo.TableA (Id int);"
                    }
                }
            };
            var lowerCaseDefinition = new SpDefinition
            {
                DdlText = upperCaseDefinition.DdlText,
                Dependencies = new List<DependencyInfo>
                {
                    new()
                    {
                        Database = "paymentdb",
                        Schema = "DBO",
                        Name = "tablea",
                        Type = "table",
                        ReferencedDdlText = "CREATE TABLE dbo.TableA (Id int);"
                    }
                }
            };

            Assert.Equal(
                _cacheManager.ComputeCompositeHash(upperCaseDefinition, 3),
                _cacheManager.ComputeCompositeHash(lowerCaseDefinition, 3));
        }

        [Fact]
        public void ComputeCompositeHash_DifferentMaxDepth_ReturnsDifferentHash()
        {
            var definition = new SpDefinition
            {
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;"
            };

            var shallowHash = _cacheManager.ComputeCompositeHash(definition, 1);
            var deepHash = _cacheManager.ComputeCompositeHash(definition, 3);

            Assert.NotEqual(shallowHash, deepHash);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_WhenCacheIndexOrSpecMissing()
        {
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "TestSp",
                CodeObjectType.Procedure);

            // Act & Assert
            // 1. 인덱스도 파일도 없는 상태
            var isValid = _cacheManager.IsCacheValid(key, "somehash", _paths);
            Assert.False(isValid);

            // 2. 인덱스는 존재하지만, Spec.md 파일이 존재하지 않는 상태
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC" },
                "somehash",
                _paths,
                "# Spec");
            isValid = _cacheManager.IsCacheValid(key, "somehash", _paths);
            Assert.False(isValid); // Spec.md가 없어 false
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_WhenObjectKeyCannotResolveToAPath()
        {
            var invalidKey = CodeObjectKey.Create(
                "PaymentDB",
                " ",
                "TestSp",
                CodeObjectType.Procedure);

            var isValid = _cacheManager.IsCacheValid(
                invalidKey,
                "somehash",
                _paths);

            Assert.False(isValid);
        }

        [Fact]
        public void UpdateCache_And_IsCacheValid_ReturnsTrue_WhenBothExistAndMatch()
        {
            // Arrange
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "TestSp",
                CodeObjectType.Procedure);
            var hash = "expectedcompositehash12345";
            var specContent = "# Spec Report for TestSp";

            // Spec 파일 생성
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // Act
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);
            var isValid = _cacheManager.IsCacheValid(key, hash, _paths);

            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_WhenHashMismatches()
        {
            // Arrange
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "TestSp",
                CodeObjectType.Procedure);
            var specContent = "# Spec Report";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // Act
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC" },
                "hash_a",
                _paths,
                specContent);
            var isValid = _cacheManager.IsCacheValid(key, "hash_b", _paths); // 다른 해시로 조회

            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public void UpdateCache_SeparatesSameNamedProcedureAndFunction()
        {
            var procedureKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "Calculate",
                CodeObjectType.Procedure);
            var functionKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "Calculate",
                CodeObjectType.Function);
            WriteSpec(procedureKey);
            WriteSpec(functionKey);

            _cacheManager.UpdateCache(
                procedureKey,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.Calculate AS SELECT 1;" },
                "procedure-hash",
                _paths,
                "# Spec");
            _cacheManager.UpdateCache(
                functionKey,
                new SpDefinition
                {
                    ObjectType = CodeObjectType.Function,
                    DdlText = "CREATE FUNCTION dbo.Calculate() RETURNS int AS BEGIN RETURN 1 END"
                },
                "function-hash",
                _paths,
                "# Spec");

            Assert.True(_cacheManager.IsCacheValid(procedureKey, "procedure-hash", _paths));
            Assert.True(_cacheManager.IsCacheValid(functionKey, "function-hash", _paths));
            Assert.False(_cacheManager.IsCacheValid(procedureKey, "function-hash", _paths));

            using var index = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(_tempOutputDir, ".sp_cache_index.json")));
            var entries = index.RootElement.GetProperty("Entries");
            Assert.True(entries.TryGetProperty(procedureKey.CanonicalName, out var procedureEntry));
            Assert.True(entries.TryGetProperty(functionKey.CanonicalName, out _));
            Assert.Equal(
                CodeObjectType.Procedure.ToString(),
                procedureEntry.GetProperty("ObjectKey").GetProperty("Type").GetString());
        }

        [Fact]
        public void IsCacheValid_UsesExternalPathFromResolver()
        {
            var key = CodeObjectKey.Create(
                "AuditDB",
                "dbo",
                "usp_Archive",
                CodeObjectType.Procedure);
            WriteSpec(key);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.usp_Archive AS SELECT 1;" },
                "external-hash",
                _paths,
                "# Spec");

            Assert.True(_cacheManager.IsCacheValid(key, "external-hash", _paths));
            Assert.True(File.Exists(Path.Combine(
                _tempOutputDir,
                "External",
                "AuditDB",
                "Procedures",
                "dbo.usp_Archive",
                "docs",
                "Spec.md")));
        }

        [Fact]
        public void IsCacheValid_ReturnsFalseWhenAnotherDatabaseOverwritesSharedProcedureSpec()
        {
            var paymentKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "usp_Shared",
                CodeObjectType.Procedure);
            var auditKey = CodeObjectKey.Create(
                "AuditDB",
                "dbo",
                "usp_Shared",
                CodeObjectType.Procedure);
            var paymentPaths = new OutputPathResolver(
                paymentKey.Database,
                _tempOutputDir);
            var auditPaths = new OutputPathResolver(
                auditKey.Database,
                _tempOutputDir);
            Assert.Equal(
                paymentPaths.ResolveSpecPath(paymentKey),
                auditPaths.ResolveSpecPath(auditKey));

            var sharedSpecPath = paymentPaths.ResolveSpecPath(paymentKey);
            Directory.CreateDirectory(Path.GetDirectoryName(sharedSpecPath)!);
            File.WriteAllText(sharedSpecPath, "# PaymentDB specification");
            _cacheManager.UpdateCache(
                paymentKey,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.usp_Shared AS SELECT 'PaymentDB';" },
                "payment-hash",
                paymentPaths,
                "# PaymentDB specification");

            File.WriteAllText(sharedSpecPath, "# AuditDB specification");
            _cacheManager.UpdateCache(
                auditKey,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.usp_Shared AS SELECT 'AuditDB';" },
                "audit-hash",
                auditPaths,
                "# AuditDB specification");

            Assert.False(_cacheManager.IsCacheValid(
                paymentKey,
                "payment-hash",
                paymentPaths));
            Assert.True(_cacheManager.IsCacheValid(
                auditKey,
                "audit-hash",
                auditPaths));
        }

        [Fact]
        public void UpdateCache_BeforeDecoratedSpecIsSaved_ValidatesFinalSpecBody()
        {
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "usp_Decorated",
                CodeObjectType.Procedure);
            var specBody = "## 개요\nPaymentDB specification";

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.usp_Decorated AS SELECT 1;" },
                "decorated-hash",
                _paths,
                specBody);

            var specPath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(
                specPath,
                "---\n종합 신뢰도: 100\n---\n\n> [!NOTE]\n> metadata\n\n" + specBody);

            Assert.True(_cacheManager.IsCacheValid(
                key,
                "decorated-hash",
                _paths));
        }

        [Fact]
        public void UpdateCache_BeforeRecursiveLinksAreSaved_ValidatesFinalLinkedSpec()
        {
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "usp_Recursive",
                CodeObjectType.Procedure);
            var specBody = "## 개요\nPaymentDB specification";
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROCEDURE dbo.usp_Recursive AS SELECT 1;" },
                "recursive-hash",
                _paths,
                specBody);

            var specPath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(
                specPath,
                specBody +
                "\n\n## 참조 코드 객체\n\n" +
                "- [dbo.FN_Fee](../../../Functions/dbo.FN_Fee/docs/Spec.md)\n");

            Assert.True(_cacheManager.IsCacheValid(
                key,
                "recursive-hash",
                _paths));
        }

        [Fact]
        public void CacheEntry_DeserializesLegacyProcedureNameWithoutObjectKey()
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(
                """{"ProcedureName":"dbo.Legacy","CompositeHash":"hash"}""");

            Assert.NotNull(entry);
            Assert.Equal("dbo.Legacy", entry.ProcedureName);
            Assert.Null(entry.ObjectKey);
        }

        [Fact]
        public void CacheEntry_SupportsOriginalSpecPathProperty()
        {
            var entry = new CacheEntry
            {
                ProcedureName = "dbo.TestProc",
                OriginalSpecPath = "output/PaymentDB/Procedures/dbo.TestProc/docs/Spec.md"
            };

            var json = JsonSerializer.Serialize(entry);
            var deserialized = JsonSerializer.Deserialize<CacheEntry>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("output/PaymentDB/Procedures/dbo.TestProc/docs/Spec.md", deserialized.OriginalSpecPath);
        }

        private void WriteSpec(CodeObjectKey key)
        {
            var specPath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Spec");
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_ForEntriesWrittenBeforeTheFormatVersionExisted()
        {
            // 수정 이전 코드는 종료 상태와 무관하게 캐시를 썼다. 그 엔트리가 히트하면
            // 파이프라인은 무조건 Passed를 반환하고(VerificationPipelineOrchestrator.cs:164, :277)
            // 미검증 문서가 "통과"로 재발행된다. 어느 레거시 엔트리가 미검증이었는지
            // 판별할 방법이 없으므로 전량 무효화한다.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var hash = "expectedcompositehash12345";
            var specContent = "# Spec Report for TestSp";

            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // 정상 엔트리를 만든 뒤 FormatVersion만 제거해 레거시 JSON을 재현한다.
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);

            var indexPath = Path.Combine(_tempOutputDir, ".sp_cache_index.json");
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!;
            foreach (var pair in root["Entries"]!.AsObject())
            {
                pair.Value!.AsObject().Remove("FormatVersion");
            }
            File.WriteAllText(indexPath, root.ToJsonString());

            // 인덱스가 여전히 유효한 JSON이어야 한다. 깨진 JSON이면 soft-fail 경로가
            // false를 반환해 게이트를 검증하지 않은 채 테스트가 통과해 버린다.
            var rewritten = File.ReadAllText(indexPath);
            Assert.DoesNotContain("FormatVersion", rewritten);
            Assert.NotNull(JsonNode.Parse(rewritten));

            // 해시도 경로도 파일 내용도 전부 일치하지만 포맷 버전이 없으므로 미스여야 한다.
            Assert.False(_cacheManager.IsCacheValid(key, hash, _paths));
        }

        [Fact]
        public void UpdateCache_StampsTheCurrentFormatVersion()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var specContent = "# Spec Report for TestSp";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                "hash",
                _paths,
                specContent);

            // CacheManager는 JsonStringEnumConverter로 직렬화하므로 기본 옵션의
            // Deserialize<CacheIndex>는 문자열 enum에서 실패한다. JsonNode로 읽는다.
            var root = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_tempOutputDir, ".sp_cache_index.json")))!;
            var entry = root["Entries"]!.AsObject().Single().Value!;

            // 13: 기계 확정 표 넷이 한꺼번에 넓어졌다. 표마다 폭이 다르므로 갈라 적는다 -
            //     참조 함수 표는 DML 셋에 더해 독립 SELECT와 `IF` 술어의 호출을 담고(문장
            //     칸이 없는 표라 「호출 위치」 칸의 `SELECT n`·`IF n`으로 나타난다),
            //     집합 술어 표는 독립 SELECT까지만 담으며(`IF n` 행은 없다), 잠금 힌트
            //     표는 문장 집합은 그대로인 채 하위 질의 수집이 WHERE 절에서 문장 노드
            //     전체로 넓어졌고, 실행 의미 표에는 종류 둘(`비집계 대입`·`루프 내
            //     재설정`)이 늘었다. CacheManager.cs의 버전 13 주석 참고.
            // 14: 집합 술어 표가 JOIN ON 절의 조인 키 등식이 아닌 항을 `조인 ON T` 범위로 싣는다
            //     (9회차 축 A 재감사 🟠 회귀 - INS_EXTRA4PLCARD의 `PG.ExtraType IN (2,3)`).
            //     도입문도 바뀌었다. CacheManager.cs의 버전 14 주석 참고.
            //     프롬프트 입력이 달라졌으므로 옛 엔트리를 재사용하면 산출물이 옛
            //     재료 그대로 남는다. 전건 재분석을 의도한 것이 맞다.
            //
            // 이 리터럴은 일부러 못 박혀 있다. 버전을 올리면 이 테스트가 깨지고, 깨진
            // 자리에서 "정말 전건 재분석을 의도했는가"를 한 번 더 묻게 된다.
            Assert.Equal(14, (int)entry["FormatVersion"]!);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_ForEntriesFromAFutureFormatVersion()
        {
            // 신버전으로 캐시를 쌓은 뒤 구버전 바이너리로 롤백하면, '보다 작음' 검사는
            // 구버전이 해석할 수 없는 엔트리를 히트시킨다. 정확히 일치할 때만 신뢰한다.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var hash = "hash";
            var specContent = "# Spec Report for TestSp";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);

            var indexPath = Path.Combine(_tempOutputDir, ".sp_cache_index.json");
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!;
            foreach (var pair in root["Entries"]!.AsObject())
            {
                pair.Value!["FormatVersion"] = 99;
            }
            File.WriteAllText(indexPath, root.ToJsonString());

            Assert.False(_cacheManager.IsCacheValid(key, hash, _paths));
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_ForEntriesFromFormatVersionOne()
        {
            // 포맷 버전 1은 정적 분석 식별자 정규화 이전에 만들어졌다. 해시가 그대로
            // 일치하더라도 스키마 표와 테이블 목록이 정규화되지 않은 채 만들어졌으므로
            // 재사용하면 정정 대상이던 잘못된 Spec.md가 그대로 복원된다.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var hash = "hash";
            var specContent = "# Spec Report for TestSp";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);

            var indexPath = Path.Combine(_tempOutputDir, ".sp_cache_index.json");
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!;
            foreach (var pair in root["Entries"]!.AsObject())
            {
                pair.Value!["FormatVersion"] = 1;
            }
            File.WriteAllText(indexPath, root.ToJsonString());

            Assert.False(_cacheManager.IsCacheValid(key, hash, _paths));
        }
    }
}
