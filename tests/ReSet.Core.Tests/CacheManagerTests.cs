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
            // 15: DML 범위 표 안내문이 조건부가 되고(A) 파서의 파생 테이블 컬럼 과잉 귀속이 빠져(G)
            //     프롬프트 재료가 바뀌었다. CacheManager.cs의 버전 15 주석 참고.
            //     프롬프트 입력이 달라졌으므로 옛 엔트리를 재사용하면 산출물이 옛
            //     재료 그대로 남는다. 전건 재분석을 의도한 것이 맞다.
            // 16: 기계 확정 표 둘(「트랜잭션 경계」·「변수 대입」)이 새로 생겼다. 프롬프트
            //     입력(표 뼈대 둘)과 출력 계약(명세서가 그 표를 담아야 한다)이 함께
            //     바뀌었고, 두 표는 모든 객체에 무조건 실려 기존 31개의 프롬프트 바이트가
            //     전부 달라진다. CacheManager.cs의 버전 16 주석 참고.
            //     **전건 재분석을 의도한 것이 맞다** - 이 물음에 답하고 올렸다. 근거는
            //     캐시 인상 전에 돌린 코퍼스 전수 스윕이다(31쌍 · 거짓 양성 0 · 다른 검사
            //     카운트 BASE와 동일). 오탐을 안은 채 전건 재생성을 걸면 그것이 곧바로
            //     재시도 소진으로 번지므로, 그 확인이 이 인상의 전제였다.
            // 17: 「오류 코드」 표(문장 번호·오류 코드·대입 대상 변수)가 산출물에 실린다.
            //     표 자체는 2026-08-25에 프롬프트와 카탈로그에 들어갔으나 버전은 16에
            //     머물러 있었다 - 인상 전 스윕에 단계 검사의 거짓 양성 원인 넷이 남아
            //     있어, 그것을 닫기 전에 전건 재생성을 걸면 거짓 오류 33건이 한꺼번에
            //     켜지기 때문이다(같은 규칙의 두 번째 적용). 원인 넷은 2026-08-27에
            //     닫혔다. 프롬프트 입력과 출력 계약이 함께 바뀌므로 인상 대상이다.
            //     CacheManager.cs의 버전 17 주석 참고.
            //     **전건 재분석을 의도한 것이 맞다** - 이 물음에 답하고 올렸다. 근거는
            //     셋이다. (1) 인상 전에 ErrorCodeTableCorpusTests로 만족가능성을
            //     확인했다 - 31 객체 · 사실을 가진 객체 12 · 사실 합 84 · 갈래 셋(완전
            //     전사된 표에 발화 0 / 사실 0건 객체는 조기 반환으로 침묵 / 사실 있는
            //     객체는 표 부재에 발화) 전부 발화 0. (2) 기준선 스윕을 인상 전에 떠
            //     두었다 - 46 좌표 + 침묵 분모 열 값
            //     (docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md).
            //     (3) 계기를 변이로 검증했다 - 변이 셋 전부 죽음. 오탐을 안은 채 전건
            //     재생성을 걸면 그것이 곧바로 재시도 소진으로 번지므로, 그 확인이 이
            //     인상의 전제였다.
            // 18: 기계 확정 「지역 변수」 표가 새로 생겼다(known-defects (5-3-7)).
            //     MachineConfirmedTables.All에 표가 하나 늘어 Critic 면제 블록의 바이트가
            //     바뀌고, Actor 프롬프트의 세 갈래(SP 전체·함수·OverviewAndParameters)에
            //     새 표가 실린다. CacheManager.cs의 버전 18 주석 참고.
            //     [이 회차는 재생성을 하지 않는다] 위 회차들과 달리 이 승격 자체는 전건
            //     재생성을 실행하지 않는다 - 강제만 걸고 다음에 생성을 돌리는 사람이 그것을
            //     문다.
            //     [오탐 위험 - 실측은 이 커밋이 아니라 같은 물결의 Task 7이 잰 것이다]
            //     LocalVariableTableCorpusTests(Task 7)가 재고, 그 테스트는 이 커밋보다
            //     먼저 통합된다(통합 순서 6 → 7 → 8). 실측치(Task 7, 조율자 재현):
            //     객체 31 · 사실을 가진 객체 25 · 사실 합 101
            //       프로시저     : 14 · 9 · 40
            //       함수(같은 DB): 10 · 9 · 23
            //       함수(외부 DB): 7 · 7 · 38
            //     갈래 셋(완전 전사 표 → 발화 0 / 사실 0건 객체 → 조기 반환으로 침묵 /
            //     사실 있는데 표 없음 → 발화) 전부 만족. 이 숫자가 흔들리거나 갈래 셋 중
            //     하나라도 깨지면 이 승격은 근거를 잃는다.
            // 19: 「실행 의미」 표의 `집계 대입`·`비집계 대입` 두 갈래가 함께 넓어지고
            //     대상 칸이 우변 원문을 싣는다 - 집계 쪽은 기존 8행 전부가 바뀌고,
            //     비집계 쪽은 새 7행만 새 표기를 실으며 기존 36행의 표기 값은
            //     그대로다. CacheManager.cs의 버전 19 주석 참고.
            //     **전건 재분석을 의도한 것이 맞다** - 옛 엔트리를 재사용하면 수수료율
            //     분기(UF_GET_COMM4CLIENT4PARTIALCANCEL:43류)와 대사 집계식
            //     (UP_UTIL_SETTLE_PROC_ETC:116·130류)이 어디에도 없는 명세서가 그대로
            //     살아남는다.
            //     [2026-09-07 2 회차 - 같은 19 안에서 비집계 갈래가 다시 넓어졌다] 분기
            //     결과 판정이 「전부 컬럼 참조」에서 「컬럼 참조 / 리터럴 / 그 둘의
            //     산술식」(한 겹만)으로 넓어져 비집계 대장이 43행에서 52행(+9)이 됐다 -
            //     새 9행도 새 표기를 실으며, 기존 43행(1 회차의 신규 7행 포함)의 표기
            //     값은 그대로다. 최상위 리터럴 개방이 `GROUP BY` 없는 `HAVING`·형제
            //     집계와 만나 무결과여도 대입이 일어나는 거짓 행 경로를 열었으나
            //     (최종 브랜치 검토 Critical), 코퍼스에 두 모양 다 0건이라 이 회차의
            //     대장 행 수는 그대로 52다. CacheManager.cs의 버전 19 주석(2 회차 추가분)
            //     참고. 캐시 형식 버전은 20으로 올리지 않는다 - 19가 아직 어떤 산출물에도
            //     적용되지 않았다(명세서 재생성 전, 공유 캐시 인덱스 31건 전량 18 확인).
            //
            // 20: 집합 술어 표가 `HAVING` 절의 항을 싣는다. 축 A 감사 🟠 -
            //     `COMM_UPD:248`의 `HAVING SUM(TxAmt) = 0`이 어느 기계 확정 표에도 없어
            //     명세서에서 사라졌고 단계·계획서까지 전파됐다(원본 1 · 명세서 0 ·
            //     steps/S05.md 0 · 계획서 0). CacheManager.cs의 버전 20 주석 참고.
            //
            //     **묻는 질문에 답한다 - 전건 재분석을 의도한 것이 맞다.** 영향 객체는
            //     COMM_UPD 하나뿐인데(코퍼스 31개 중 HAVING 보유 1건, 전수 grep) 31건이
            //     전부 다시 돈다. 그래도 올리는 이유는 인상이 유일한 무효화 수단이라서다 -
            //     복합 해시의 입력은 DDL SHA256이라 프롬프트 재료만 늘어난 이 변경은
            //     해시를 안 바꾸고, 안 올리면 COMM_UPD가 캐시 적중으로 건너뛰어져 새 행이
            //     영영 안 실리고 L1도 그 자리에서 안 돈다. 비용은 실측 약 54분이다.
            //     (19의 「아직 어떤 산출물에도 적용되지 않았다」는 그때의 사실이다 -
            //     2026-09-08 재생성으로 지금 공유 캐시 인덱스는 31건 전량 19다.)
            //
            // 21: 3부 참조 목록을 소속 DB 안/밖으로 갈라 프롬프트에 싣는다.
            //     **전건 재분석을 의도한 것이 맞다** - 20 이 이미 셋에 적용된 상태에서
            //     프롬프트가 바뀌었다. 20 을 유지하면 그 셋만 옛 계약으로 남아 같은 번호
            //     아래 두 계약이 생긴다. CacheManager.cs 의 버전 21 주석 참고.
            //
            //
            // 22: 축자 복사 블록 안의 산문 지시를 표 밖으로 뺐다.
            //     **전건 재분석을 의도한 것이 맞다.** 인트로 다섯 중 넷은 해당 표가 나는
            //     모든 객체에 실리므로 프롬프트 바이트가 코퍼스 전반에서 바뀌었다 -
            //     「영향 객체 0」 조건부 예외에 걸리지 않는다(유출만 세어도 10/31 편이다).
            //     옛 계약 산출물은 작성 지시를 문서 본문에 담고 있어 다음 감사에 결함으로
            //     남는다. CacheManager.cs 의 버전 22 주석 참고.
            //
            // 이 리터럴은 일부러 못 박혀 있다. 버전을 올리면 이 테스트가 깨지고, 깨진
            // 자리에서 "정말 전건 재분석을 의도했는가"를 한 번 더 묻게 된다.
            Assert.Equal(22, (int)entry["FormatVersion"]!);
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
