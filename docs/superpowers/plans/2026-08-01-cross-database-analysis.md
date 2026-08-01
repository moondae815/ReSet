# 크로스 데이터베이스 의존성 분석 활성화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 같은 SQL Server 인스턴스 내 다른 데이터베이스의 코드 객체까지 재귀 분석하여 `output/External/<DB>/` 아래에 Spec.md를 생성하고 참조 링크로 연결한다.

**Architecture:** 분석 기준 DB(`rootKey.Database`)를 `DependencyAnalysisRequest.AnalysisDatabase`로 전파하여 캐시 판정 경로와 최종 저장 경로가 동일한 `OutputPathResolver` 기준을 쓰게 만든다. 그 위에서 `appsettings.json`의 `AllowExternalDatabaseConnections` 스위치를 노출한다. 외부 DB 조회는 기존 3-part 쿼리(`[DB].sys.sql_modules`)를 그대로 쓰므로 새 커넥션 관리가 없다.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute, Microsoft.Data.SqlClient, Spectre.Console

## Global Constraints

- 설계 문서: `docs/superpowers/specs/2026-08-01-cross-database-analysis-design.md`
- `AllowExternalDatabaseConnections`의 기본값은 **`false`** — 기존 산출물의 동작이 바뀌면 안 된다.
- 링크드 서버 및 타 인스턴스는 범위 밖. 같은 인스턴스 내 3-part 조회만 지원한다.
- 허용 목록(allowlist)은 도입하지 않는다. 단순 on/off만 만든다.
- 외부 DB 접근 실패는 강등하지 않는다. 기존 `MarkFailed` 경로로 `Failed` 노드를 만든다.
- `DependencyAnalysisOrchestratorTests.cs:338`, `:380`의 기존 `SkippedExternal` 테스트는 수정하지 않는다. 이 두 테스트가 "기본 동작 불변"의 보증이다.
- `DbMetadataService.cs:90`의 `dbo` 스키마 완화 폴백(`SELECT TOP 1 ... WHERE o.name = @ObjectName`)은 **손대지 않는다.** 스펙에 알려진 리스크로 기록된 항목이며 이번 범위 밖이다.
- 빌드 경고 0개, `dotnet test` 전체 통과가 각 태스크의 완료 조건이다.
- 커밋 메시지는 영문 Conventional Commits를 따른다 (저장소 기존 관례).

## File Structure

| 파일 | 책임 | 변경 유형 |
|---|---|---|
| `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs` | 요청 DTO에 `AnalysisDatabase` 추가, record 전환 | 수정 |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | `AnalysisDatabase`의 유일한 출처. 델리게이트로 파이프라인에 전달 | 수정 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | `analysisDatabase`를 받아 `OutputPathResolver` 생성 기준으로 사용 | 수정 |
| `src/ReSet.Core/Services/DbMetadataService.cs` | 호환성 수준을 대상 DB 기준으로 조회 | 수정 |
| `src/ReSet.Cli/Program.cs` | 설정 로드 → 요청 객체 전달 | 수정 |
| `src/ReSet.Cli/appsettings.json` | 설정 키 노출 | 수정 |
| `README.md` | 설정 키 문서화 | 수정 |
| `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs` | 전파·외부 분석·실패 노출 검증 | 수정 |
| `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs` | 호환성 수준 soft-fail 검증 | 수정 |

Task 4(CLI 노출)를 마지막에 두는 이유는, 그 아래 계층이 모두 올바르게 동작할 때까지 사용자가 플래그를 켤 수 없게 하기 위해서다.

---

### Task 1: 분석 기준 DB를 파이프라인까지 전파

`OutputPathResolver` 생성 기준이 두 곳에서 어긋나는 문제를 제거한다. 이 태스크가 끝나면 외부 객체의 캐시 판정 경로와 최종 저장 경로가 정의상 일치한다.

**Files:**
- Modify: `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs:7-21`
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:16-30`, `:47-68`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:105-121`, `:139-151`, `:222-235`
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Produces: `DependencyAnalysisRequest`가 `sealed record`가 되고 `string? AnalysisDatabase { get; init; }` 속성을 갖는다. `VerificationPipelineOrchestrator.RunCodeObjectPipelineAsync`의 마지막 파라미터로 `string? analysisDatabase = null`이 추가된다.

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`의 마지막 `[Fact]` 뒤, `private static DependencyAnalysisRequest Request(` 헬퍼 앞에 추가한다.

```csharp
    [Fact]
    public async Task AnalyzeAsync_PropagatesRootDatabaseAsAnalysisDatabaseToPipeline()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, externalFunction),
            Definition(externalFunction));
        var analysisDatabases = new List<string?>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (request, key, _) =>
            {
                analysisDatabases.Add(request.AnalysisDatabase);
                return Task.FromResult(PipelineResult(key));
            });

        await sut.AnalyzeAsync(
            root,
            Request(allowExternalDatabaseConnections: true),
            CancellationToken.None);

        Assert.Equal(2, analysisDatabases.Count);
        Assert.All(analysisDatabases, database => Assert.Equal("PaymentDB", database));
    }
```

`Key(...)` 헬퍼는 DB를 `"PaymentDB"`로 고정하므로, 외부 함수(`AuditDB`)를 분석할 때도 값이 `"PaymentDB"`여야 한다는 뜻이다. 호출이 2건인 이유는 자식(외부 함수)이 먼저, 루트가 나중에 파이프라인을 타기 때문이다.

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AnalyzeAsync_PropagatesRootDatabaseAsAnalysisDatabaseToPipeline"`

Expected: 컴파일 에러 `'DependencyAnalysisRequest' does not contain a definition for 'AnalysisDatabase'`

- [ ] **Step 3: 요청 DTO를 record로 전환하고 속성 추가**

`src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs`에서 클래스 선언을 바꾸고 속성 하나를 추가한다.

```csharp
public sealed record DependencyAnalysisRequest
{
    public string ConnectionString { get; init; } = string.Empty;
    public int MaxDepth { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string? ActorEffort { get; init; }
    public string Instructions { get; init; } = string.Empty;
    public bool IsBatchMode { get; init; }
    public string OutputDirectory { get; init; } = "./output";
    public bool EnableCache { get; init; }
    public bool AllowExternalDatabaseConnections { get; init; }

    /// <summary>
    /// 분석 기준 데이터베이스. <see cref="IDependencyAnalysisOrchestrator.AnalyzeAsync"/>가
    /// 루트 객체의 DB로 덮어쓰므로 호출자가 설정할 필요는 없다.
    /// 이 값이 <c>OutputPathResolver</c>의 "현재 DB" 기준이 되며,
    /// 이와 다른 DB의 객체는 <c>External/&lt;DB&gt;/</c> 아래로 배치된다.
    /// </summary>
    public string? AnalysisDatabase { get; init; }

    public DependencyArtifactMode DependencyArtifactMode { get; init; } = DependencyArtifactMode.Reference;
    public Action<DependencyAnalysisProgress>? Progress { get; init; }
}
```

`sealed class` → `sealed record`로만 바뀌었고 속성 본문은 그대로다. 기존 object-initializer 호출부는 문법 변경 없이 컴파일된다.

- [ ] **Step 4: AnalyzeAsync를 AnalysisDatabase의 유일한 출처로 만들기**

`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:47-68`의 본문을 바꾼다.

```csharp
    public async Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 호출자가 무엇을 넣었든 루트 객체의 DB가 분석 기준이 된다.
        // 캐시 판정(VerificationPipelineOrchestrator)과 최종 저장(PersistArtifactsAsync)이
        // 같은 OutputPathResolver 기준을 쓰도록 보장하는 지점이다.
        var effectiveRequest = request with { AnalysisDatabase = rootKey.Database };

        var execution = new ExecutionState(rootKey.Database);
        await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);
        await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);

        var result = new CodeObjectPipelineResult
        {
            Nodes = execution.Nodes.Values.ToList(),
            DependencyEdges = execution.Edges,
            AnalysisResults = execution.AnalysisResults
        };
        await PersistArtifactsAsync(rootKey, effectiveRequest, result, cancellationToken);
        return result;
    }
```

- [ ] **Step 5: 파이프라인 델리게이트에서 값 전달**

`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:16-30`의 편의 생성자에서 델리게이트에 인자를 하나 더 넘긴다.

```csharp
    public DependencyAnalysisOrchestrator(
        IDbMetadataService metadataService,
        VerificationPipelineOrchestrator pipelineOrchestrator)
        : this(
            metadataService,
            (request, key, cancellationToken) => pipelineOrchestrator.RunCodeObjectPipelineAsync(
                request.ConnectionString,
                key,
                request.MaxDepth,
                request.Provider,
                request.Instructions,
                request.IsBatchMode,
                request.OutputDirectory,
                request.EnableCache,
                cancellationToken,
                directDependenciesOnly: true,
                includeExternalCodeObjects: true,
                analysisDatabase: request.AnalysisDatabase),
            new MetadataExporter(),
            new MechanicalValidator())
    {
        ArgumentNullException.ThrowIfNull(pipelineOrchestrator);
    }
```

- [ ] **Step 6: 파이프라인이 analysisDatabase를 받도록 확장**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:105-121`의 공개 메서드에 파라미터를 추가하고 코어로 넘긴다.

```csharp
        public async Task<CodeObjectPipelineResult> RunCodeObjectPipelineAsync(
            string connectionString,
            CodeObjectKey key,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache = false,
            CancellationToken cancellationToken = default,
            bool directDependenciesOnly = false,
            bool includeExternalCodeObjects = true,
            string? analysisDatabase = null)
        {
            var (specMarkdown, spDef, review, thinkingText) = await RunCodeObjectPipelineCoreAsync(
                connectionString,
                key,
                maxDepth,
                provider,
                instructions,
                isBatchMode,
                outputDirectory,
                enableCache,
                cancellationToken,
                directDependenciesOnly,
                includeExternalCodeObjects,
                analysisDatabase);
```

`:139-151`의 private 코어 메서드 시그니처 끝에도 파라미터를 추가한다.

```csharp
        private async Task<(string? SpecMarkdown, SpDefinition? SpDef, ReviewResult? Review, string? ThinkingText)> RunCodeObjectPipelineCoreAsync(
            string connectionString,
            CodeObjectKey key,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache,
            CancellationToken cancellationToken,
            bool directDependenciesOnly,
            bool includeExternalCodeObjects,
            string? analysisDatabase = null)
```

`:91`의 `RunPipelineAsync` 내부 호출은 수정하지 않는다 — 기본값 `null`로 기존 동작이 유지된다.

- [ ] **Step 7: OutputPathResolver 생성 기준 교체**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:227`을 바꾼다.

```csharp
                    outputPaths = new OutputPathResolver(
                        analysisDatabase ?? cacheObjectKey.Database,
                        outputDirectory);
```

- [ ] **Step 8: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~DependencyAnalysisOrchestratorTests"`

Expected: PASS. 기존 `AnalyzeAsync_UsesDirectMetadataAndSkipsExternalObjectBeforeAdditionalLookup`과 `AnalyzeAsync_UnknownExternalCodeObjectCreatesSkippedNodeWithoutMetadataLookup`도 함께 통과해야 한다.

- [ ] **Step 9: 전체 빌드와 테스트**

Run: `dotnet build && dotnet test`

Expected: 경고 0개, 전체 테스트 통과

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs \
        src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs \
        src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -m "fix: propagate analysis database to pipeline output path resolution"
```

---

### Task 2: 외부 DB 완전 분석 동작 검증

Task 1로 경로가 정합해졌으므로, 플래그를 켠 상태의 정상 경로와 실패 경로를 테스트로 고정한다. `DependencyAnalysisOrchestrator.cs:147`의 조건문은 이미 존재하므로 프로덕션 코드 변경은 없다. 이 태스크의 산출물은 회귀 방지 테스트다.

**Files:**
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `DependencyAnalysisRequest.AnalysisDatabase` 전파 동작

- [ ] **Step 1: 외부 객체 완전 분석 테스트 작성**

Task 1에서 추가한 테스트 바로 뒤에 넣는다.

```csharp
    [Fact]
    public async Task AnalyzeAsync_AllowingExternalDatabasesWritesSpecUnderExternalDirectory()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), $"ReSet-ExternalDatabase-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, externalFunction),
            Definition(externalFunction));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, allowExternalDatabaseConnections: true),
                CancellationToken.None);

            var node = result.GetNode(externalFunction);
            Assert.Equal(AnalysisNodeStatus.Succeeded, node.Status);
            Assert.Equal(
                Path.Combine(
                    outputRoot, "External", "AuditDB", "Functions", "dbo.FN_Audit", "docs", "Spec.md"),
                node.SpecPath);
            Assert.True(File.Exists(node.SpecPath));

            var rootSpec = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Procedures", "dbo.USP_Root", "docs", "Spec.md"));
            Assert.Contains(
                "[dbo.FN\\_Audit](../../../External/AuditDB/Functions/dbo.FN_Audit/docs/Spec.md)",
                rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

링크 형식은 기존 아티팩트 테스트(`:473`)가 쓰는 escape 규칙(`dbo.FN\\_Child`)과 같다. 상대 경로는 `output/Procedures/dbo.USP_Root/docs`에서 `output/External/AuditDB/Functions/dbo.FN_Audit/docs`로 이동하므로 `../../../`이 된다.

- [ ] **Step 2: 외부 객체 접근 실패 테스트 작성**

바로 뒤에 이어서 넣는다.

```csharp
    [Fact]
    public async Task AnalyzeAsync_ExternalMetadataFailureIsSurfacedAsFailedNode()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), $"ReSet-ExternalDatabaseFailure-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<CodeObjectKey>(1) == root
                ? Task.FromResult(Definition(root, externalFunction))
                : Task.FromException<SpDefinition>(new InvalidOperationException(
                    "'[AuditDB].[dbo].[FN_Audit]'의 SQL Server 객체 타입을 찾을 수 없습니다.")));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, allowExternalDatabaseConnections: true),
                CancellationToken.None);

            Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(externalFunction).Status);
            Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(root).Status);

            var rootSpec = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Procedures", "dbo.USP_Root", "docs", "Spec.md"));
            Assert.Contains("분석 불가", rootSpec);
            Assert.DoesNotContain("분석 생략", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

`Assert.DoesNotContain("분석 생략", ...)`이 중요하다. 접근 실패를 `SkippedExternal`로 강등하지 않는다는 결정을 이 단언이 고정한다.

- [ ] **Step 3: 두 테스트 실행**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AnalyzeAsync_AllowingExternalDatabases|FullyQualifiedName~AnalyzeAsync_ExternalMetadataFailure"`

Expected: PASS. 실패한다면 Task 1의 경로 전파가 완결되지 않은 것이므로 Task 1로 돌아간다.

- [ ] **Step 4: 전체 테스트**

Run: `dotnet build && dotnet test`

Expected: 경고 0개, 전체 테스트 통과

- [ ] **Step 5: 커밋**

```bash
git add tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -m "test: cover external database analysis success and failure paths"
```

---

### Task 3: 정적 파서 호환성 수준을 대상 DB 기준으로 조회

`GetDatabaseCompatibilityLevelAsync`가 `DB_NAME()`으로 커넥션의 기본 DB만 보므로, 외부 DB 객체를 파싱할 때 잘못된 호환성 수준이 `SqlStaticParser`에 전달된다.

**Files:**
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs:173-197`, `:474`, `:562`
- Test: `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs`

**Interfaces:**
- Produces: `private async Task<int> GetDatabaseCompatibilityLevelAsync(string connectionString, string? database, CancellationToken cancellationToken)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs`의 클래스 안 마지막 테스트 뒤에 추가한다.

```csharp
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
```

이 테스트는 실 DB 연결 없이 두 가지를 보증한다. 파라미터가 추가된 뒤에도 시그니처가 의도한 형태이고(리플렉션 조회 성공), 조회 실패 시 예외 없이 160으로 폴백하는 기존 soft-fail 동작이 유지된다는 점이다.

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~GetDatabaseCompatibilityLevelAsync_WithInvalidConnection_FallsBackTo160"`

Expected: FAIL. `method.Invoke`가 인자 3개를 받는 시그니처를 찾지 못해 `TargetParameterCountException`이 발생한다.

- [ ] **Step 3: 메서드에 database 파라미터 추가**

`src/ReSet.Core/Services/DbMetadataService.cs:173-197`을 바꾼다.

```csharp
        private async Task<int> GetDatabaseCompatibilityLevelAsync(
            string connectionString,
            string? database,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    var sql = string.IsNullOrWhiteSpace(database)
                        ? "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();"
                        : "SELECT compatibility_level FROM sys.databases WHERE name = @Database;";
                    using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn))
                    {
                        if (!string.IsNullOrWhiteSpace(database))
                        {
                            cmd.Parameters.AddWithValue("@Database", database);
                        }

                        var result = await cmd.ExecuteScalarAsync(cancellationToken);
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt32(result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DbMetadata] 데이터베이스 호환성 수준 조회 실패 (Soft Fail) - 기본값 160으로 폴백합니다.");
            }
            return 160;
        }
```

- [ ] **Step 4: 호출부 두 곳 갱신**

`:474`와 `:562`는 모두 `GetCodeObjectDetailsCoreAsync` 안에 있고, 같은 스코프에 지역 변수 `database`(`:421`에서 `objectKey.Database`로 초기화)가 이미 존재한다. 두 곳 모두 다음으로 바꾼다.

```csharp
                int compatLevel = await GetDatabaseCompatibilityLevelAsync(
                    connectionString, database, cancellationToken);
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~GetDatabaseCompatibilityLevelAsync_WithInvalidConnection_FallsBackTo160"`

Expected: PASS (2건, `null`과 `"AuditDB"`)

- [ ] **Step 6: 전체 빌드와 테스트**

Run: `dotnet build && dotnet test`

Expected: 경고 0개, 전체 테스트 통과

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DbMetadataService.cs \
        tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs
git commit -m "fix: resolve compatibility level for the target database of a code object"
```

---

### Task 4: CLI 설정으로 스위치 노출

아래 계층이 모두 정합해졌으므로 사용자가 켤 수 있게 한다.

**Files:**
- Modify: `src/ReSet.Cli/appsettings.json:2-7`
- Modify: `src/ReSet.Cli/Program.cs:137-138`, `:611-629`, `:897-915`, `:1401-1420`, `:1443-1455`
- Modify: `README.md:144`

**Interfaces:**
- Consumes: `DependencyAnalysisRequest.AllowExternalDatabaseConnections` (기존), Task 1의 `AnalysisDatabase` 전파
- Produces: `RunConfiguredAnalysisAsync`에 `bool allowExternalDatabaseConnections` 파라미터가 `enableCache` 다음, `dependencyArtifactMode` 앞에 추가된다.

CLI 계층에는 테스트 프로젝트가 없다(`ReSet.slnx`의 테스트 폴더에는 `ReSet.Core.Tests` 하나뿐). 이 태스크의 검증은 빌드와 Step 8의 수동 시나리오다.

- [ ] **Step 1: 설정 키 추가**

`src/ReSet.Cli/appsettings.json`의 `DatabaseSettings` 블록을 바꾼다.

```jsonc
  "DatabaseSettings": {
    "Server": "localhost",              // SQL Server 주소
    "Database": "Northwind",            // 대상 데이터베이스 이름
    "MaxDependencyDepth": 3,            // 재귀적 의존성 탐색의 최대 깊이 (기본값: 3)
    "AllowExternalDatabaseConnections": false, // 같은 인스턴스 내 다른 DB의 코드 객체까지 재귀 분석할지 여부 (기본값: false). true면 output/External/<DB>/ 아래에 명세서가 생성됩니다. 링크드 서버는 지원하지 않습니다.
    "OfflineSnapshotPath": ""           // 오프라인 분석을 위한 SQL 스냅샷 파일 경로
  },
```

- [ ] **Step 2: 설정 로드**

`src/ReSet.Cli/Program.cs:137-138`의 `maxDepth` 파싱 바로 뒤에 추가한다.

```csharp
            var depthStr = configuration["DatabaseSettings:MaxDependencyDepth"] ?? "3";
            int.TryParse(depthStr, out int maxDepth);

            var allowExternalDbStr =
                configuration["DatabaseSettings:AllowExternalDatabaseConnections"] ?? "false";
            bool.TryParse(allowExternalDbStr, out bool allowExternalDatabaseConnections);
```

- [ ] **Step 3: RunConfiguredAnalysisAsync 시그니처 확장**

`src/ReSet.Cli/Program.cs:1401-1420`에서 `enableCache` 다음 줄에 파라미터를 추가한다.

```csharp
            string outputDirectory,
            bool enableCache,
            bool allowExternalDatabaseConnections,
            DependencyArtifactMode dependencyArtifactMode,
            CancellationToken cancellationToken)
```

- [ ] **Step 4: 요청 객체에 값 채우기**

`src/ReSet.Cli/Program.cs:1443-1455`의 요청 생성 블록을 바꾼다.

```csharp
                new DependencyAnalysisRequest
                {
                    ConnectionString = connectionString,
                    MaxDepth = maxDepth,
                    Provider = provider,
                    ModelName = modelName,
                    ActorEffort = actorEffort,
                    Instructions = instructions,
                    IsBatchMode = isBatchMode,
                    OutputDirectory = outputDirectory,
                    EnableCache = enableCache,
                    AllowExternalDatabaseConnections = allowExternalDatabaseConnections,
                    DependencyArtifactMode = dependencyArtifactMode
                },
```

- [ ] **Step 5: 호출부 두 곳 갱신**

`:611`(배치 모드)과 `:897`(대화형 모드) 모두 `enableCache,` 다음 줄에 인자를 추가한다. 두 호출 모두 위치 인자 방식이므로 순서가 Step 3과 일치해야 한다.

```csharp
                            outputDir,
                            enableCache,
                            allowExternalDatabaseConnections,
                            dependencyArtifactMode,
```

- [ ] **Step 6: README 설정 문서 갱신**

`README.md:144`의 `MaxDependencyDepth` 줄 바로 아래에 추가한다.

```jsonc
    "MaxDependencyDepth": 3,        // 재귀적 의존성 탐색의 최대 깊이 (기본값: 3)
    "AllowExternalDatabaseConnections": false, // 같은 인스턴스 내 다른 DB의 코드 객체까지 분석 (기본값: false)
```

- [ ] **Step 7: 빌드와 전체 테스트**

Run: `dotnet build && dotnet test`

Expected: 경고 0개, 전체 테스트 통과

- [ ] **Step 8: 수동 검증**

`appsettings.local.json`(git 미추적)에 `"DatabaseSettings": { "AllowExternalDatabaseConnections": true }`를 설정하고 `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`를 재분석하여 확인한다.

> **반드시 라이브 SQL Server 연결로 실행할 것.** `OfflineSnapshotPath`를 비워 오프라인 스냅샷 모드가 아님을 확인한 뒤 검증한다. 오프라인 스냅샷에는 의존성 타입이 이미 해석되어 있어 라이브 경로의 결함을 가린다. 최종 리뷰에서 실제로 이 차이 때문에 크로스 DB 타입 미해석 결함(`GatherDirectDependenciesAsync`의 `!isExternalDependency` 가드)이 모든 테스트와 기존 산출물을 통과한 채 남아 있었다.

1. `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md` 등 5개 파일이 생성된다.
2. `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md`의 참조 코드 객체 섹션에서 5개 UDF가 `분석 생략(외부 객체)` 대신 상대 경로 링크로 표시된다.
3. `raw/dependency-manifest.json`의 해당 노드 `Status`가 `SkippedExternal` → `Succeeded`로 바뀌고 `Sha256`이 채워진다.
4. 같은 조건으로 재실행하면 외부 객체 5개가 모두 캐시 적중하여 LLM 호출이 발생하지 않는다 (로그에서 확인).
5. 설정을 `false`로 되돌려 재실행하면 기존과 동일하게 `분석 생략(외부 객체)`로 표시된다.

6. 외부 UDF의 호환성 수준이 `SETTLE_CARD_DB` 기준으로 조회되는지 로그에서 확인한다 (Task 3의 `@Database` 분기는 실 연결이 없으면 CI에서 실행되지 않는다).

1번의 판정 기준은 파일 존재 여부가 아니라 `dependency-manifest.json`의 노드 `Status`다. 파일이 없더라도 `Status`가 `SkippedExternal`로 남아 있고 사유가 `외부 코드 객체 유형을 추가 조회 없이 확인할 수 없습니다`라면, 타입 해석이 아니라 다른 단계에서 막힌 것이다.

4번이 Task 1의 경로 정합성을 실제로 확인하는 항목이다. 여기서 캐시가 매번 미스 나면 `analysisDatabase` 전파가 어딘가에서 끊긴 것이다.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Cli/appsettings.json src/ReSet.Cli/Program.cs README.md
git commit -m "feat: expose cross-database dependency analysis switch in CLI settings"
```
