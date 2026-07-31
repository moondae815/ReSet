# 재귀 코드 객체(SP/UDF) 분석 및 문서 연결 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 개별 SP 분석에서 발견한 하위 SP/UDF를 최대 깊이까지 한 번씩 독립 분석하고, 모든 호출 문서가 생성된 단일 `Spec.md`를 안전하게 링크하도록 만든다.

**Architecture:** 코드 객체를 `CodeObjectKey`로 식별하고 `DependencyAnalysisOrchestrator`가 의존 그래프와 작업 상태를 관리한다. 기존 검증 파이프라인은 SP/UDF 공통 코드 객체 파이프라인으로 일반화하며, 출력은 표준 DDL 저장소와 문서별 매니페스트로 분리한다.

**Tech Stack:** .NET 10, C#, SQL Server system catalog, ScriptDom, Markdig, xUnit, NSubstitute

## Global Constraints

- `CodeObjectKey`는 database, schema, name, type을 모두 포함하며 대소문자를 구분하지 않는다.
- 현재 DB SP의 기존 `output/Procedures/{schema}.{name}` 문서 경로는 변경하지 않는다.
- UDF는 `output/Functions/{schema}.{name}`에 저장하고, 외부 DB 객체만 `output/External/{database}/` 아래에 저장한다.
- 기본 `Reference` 모드에서는 참조 SP/UDF DDL 파일을 호출자별 `raw/ddl`에 복제하지 않는다.
- `prompt-context.md`는 실제 AI 요청 원문을 계속 보존한다.
- 각 객체의 메타데이터·AI·파일 저장 실패는 소프트 페일로 격리하며 다른 객체 분석을 중단하지 않는다.
- AI Thinking은 TUI에 출력하지 않고 `Thinking.md`와 파일 로그에만 기록한다.
- 새 프롬프트의 시스템 지시는 영문으로 작성하고 최종 문서는 한국어로 생성한다.

---

## 변경 파일 구조

| 파일 | 책임 |
| --- | --- |
| `src/ReSet.Core/Models/CodeObjectType.cs` | 분석 가능한 코드 객체 유형 열거형 |
| `src/ReSet.Core/Models/CodeObjectKey.cs` | 중복 제거·캐시·경로의 불변 식별자 |
| `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs` | 분석 상태, 그래프 노드, 실행 결과, 매니페스트 DTO |
| `src/ReSet.Core/Models/SpDefinition.cs` | 기존 모델에 `ObjectType`과 UDF 반환 메타데이터 추가 |
| `src/ReSet.Core/Models/DependencyInfo.cs` | 직접 호출자와 동적 SQL 후보 정보 추가 |
| `src/ReSet.Core/Models/DbSnapshot.cs` | SP/UDF를 함께 보관하는 객체 키 기반 스냅샷 |
| `src/ReSet.Core/Services/IDbMetadataService.cs` | 일반 코드 객체 조회 계약 |
| `src/ReSet.Core/Services/DbMetadataService.cs` | SP/UDF 조회, 직접 간선 보존, UDF 반환 계약 수집 |
| `src/ReSet.Core/Services/OfflineDbMetadataService.cs` | 일반 객체 조회와 이전 SP 스냅샷 키 호환 |
| `src/ReSet.Core/Services/SnapshotManager.cs` | SP/UDF 객체 키 기반 오프라인 스냅샷 내보내기 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | SP/UDF 공통 분석 파이프라인 진입점 |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | 그래프 순회, 중복 제거, 자식 우선 작업 실행 |
| `src/ReSet.Core/Services/SpecificationLinker.cs` | 문서의 `## 참조 코드 객체` 섹션을 결정론적으로 갱신 |
| `src/ReSet.Core/Services/OutputPathResolver.cs` | 현재 DB/외부 DB의 표준 문서·DDL 경로 계산 |
| `src/ReSet.Core/Services/MetadataExporter.cs` | 표준 DDL, 매니페스트, Reference/PortableBundle 저장 |
| `src/ReSet.Core/Services/CacheManager.cs` | 객체 키 기반 캐시와 문서 경로 검증 |
| `src/ReSet.Core/Services/AiService.cs` | UDF 전용 시스템 프롬프트·체크리스트 분기 |
| `src/ReSet.Cli/Program.cs` | 설정 로드, 단일 분석 플로우와 진행 UI 연결 |
| `src/ReSet.Cli/appsettings.json` | 기능 활성화와 아티팩트 모드 기본값 |
| `tests/ReSet.Core.Tests/*` | 모델, 그래프, 링크, 저장, 캐시, 오프라인 및 파이프라인 테스트 |

## Task 1: 코드 객체 식별 모델과 의존 관계 표현

**Files:**
- Create: `src/ReSet.Core/Models/CodeObjectType.cs`
- Create: `src/ReSet.Core/Models/CodeObjectKey.cs`
- Create: `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs`
- Modify: `src/ReSet.Core/Models/SpDefinition.cs`
- Modify: `src/ReSet.Core/Models/DependencyInfo.cs`
- Test: `tests/ReSet.Core.Tests/CodeObjectKeyTests.cs`

**Interfaces:**
- Produces: `CodeObjectKey`, `CodeObjectType`, `FunctionReturnInfo`, `AnalysisNodeStatus`, `AnalysisNode`, `DependencyEdge`, `CodeObjectPipelineResult`, `CodeObjectAnalysisResult`.
- Consumes: 기존 `SpDefinition.Dependencies`와 `DependencyInfo.Type`.

- [ ] **Step 1: 식별자 충돌과 상태 모델의 실패 테스트를 작성한다.**

```csharp
[Fact]
public void CodeObjectKey_DistinguishesDatabaseAndObjectType()
{
    var procedure = new CodeObjectKey("PaymentDB", "dbo", "Calc", CodeObjectType.Procedure);
    var function = new CodeObjectKey("PaymentDB", "dbo", "Calc", CodeObjectType.Function);
    var external = new CodeObjectKey("AuditDB", "dbo", "Calc", CodeObjectType.Procedure);

    Assert.NotEqual(procedure, function);
    Assert.NotEqual(procedure, external);
    Assert.Equal(procedure, new CodeObjectKey("paymentdb", "DBO", "calc", CodeObjectType.Procedure));
}

[Fact]
public void AnalysisNode_InitializesAsQueued()
{
    var node = new AnalysisNode(new CodeObjectKey("PaymentDB", "dbo", "usp_A", CodeObjectType.Procedure));
    Assert.Equal(AnalysisNodeStatus.Queued, node.Status);
}
```

- [ ] **Step 2: 테스트를 실행해 컴파일 실패를 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~CodeObjectKeyTests`

Expected: `CodeObjectKey`, `CodeObjectType`, `AnalysisNode` 타입 미정의로 실패.

- [ ] **Step 3: 불변 식별자와 분석 DTO를 구현한다.**

```csharp
public enum CodeObjectType { Procedure, Function }

public sealed record CodeObjectKey(string Database, string Schema, string Name, CodeObjectType Type)
{
    public string CanonicalName => $"{Database}.{Schema}.{Name}.{Type}";
    public static CodeObjectKey Create(string database, string schema, string name, CodeObjectType type) =>
        new(database.Trim(), schema.Trim(), name.Trim(), type);
}

public enum AnalysisNodeStatus { Queued, Running, Succeeded, Failed, SkippedExternal, SkippedDepth, Cancelled }

public sealed class AnalysisNode
{
    public AnalysisNode(CodeObjectKey key) => Key = key;
    public CodeObjectKey Key { get; }
    public AnalysisNodeStatus Status { get; set; } = AnalysisNodeStatus.Queued;
    public string? Error { get; set; }
    public string? SpecPath { get; set; }
}
```

`CodeObjectKey`에는 `StringComparer.OrdinalIgnoreCase` 기반의 명시적 `Equals`/`GetHashCode`를 구현하고, `SpDefinition.ObjectType`의 기본값은 `Procedure`로 둔다. `DependencyInfo`에는 nullable `SourceObjectKey`와 `IsDynamicSqlCandidate`를 추가한다.

- [ ] **Step 4: 모델 테스트를 다시 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~CodeObjectKeyTests`

Expected: PASS.

- [ ] **Step 5: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Models tests/ReSet.Core.Tests/CodeObjectKeyTests.cs
git commit -m "feat: add code object analysis models"
```

## Task 2: SP/UDF 공통 메타데이터 조회와 스냅샷 호환

**Files:**
- Modify: `src/ReSet.Core/Services/IDbMetadataService.cs`
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs`
- Modify: `src/ReSet.Core/Services/OfflineDbMetadataService.cs`
- Modify: `src/ReSet.Core/Services/SnapshotManager.cs`
- Modify: `src/ReSet.Core/Models/DbSnapshot.cs`
- Test: `tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs`
- Test: `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs`

**Interfaces:**
- Consumes: `CodeObjectKey`, `SpDefinition.ObjectType`, `DependencyInfo.SourceObjectKey`.
- Produces: `IDbMetadataService.GetCodeObjectDetailsAsync` and `DbSnapshot.CodeObjects`.

- [ ] **Step 1: UDF와 이전 SP 키를 조회하는 실패 테스트를 추가한다.**

```csharp
[Fact]
public async Task GetCodeObjectDetailsAsync_ReturnsFunctionFromCodeObjects()
{
    var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
    var snapshot = new DbSnapshot { Database = "PaymentDB" };
    snapshot.CodeObjects[key.CanonicalName] = new SpDefinition { Name = "FN_Calc", ObjectType = CodeObjectType.Function };

    var result = await new OfflineDbMetadataService(snapshot)
        .GetCodeObjectDetailsAsync("ignored", key, 2);

    Assert.Equal(CodeObjectType.Function, result.ObjectType);
}

[Fact]
public async Task GetCodeObjectDetailsAsync_FallsBackToLegacyStoredProcedureKey()
{
    var snapshot = new DbSnapshot { Database = "PaymentDB" };
    snapshot.StoredProcedures["dbo.usp_Legacy"] = new SpDefinition { Name = "usp_Legacy" };
    var key = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Legacy", CodeObjectType.Procedure);

    Assert.Equal("usp_Legacy", (await new OfflineDbMetadataService(snapshot)
        .GetCodeObjectDetailsAsync("ignored", key, 2)).Name);
}
```

- [ ] **Step 2: 해당 테스트가 새 계약 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~OfflineDbMetadataServiceTests`

Expected: `GetCodeObjectDetailsAsync`와 `CodeObjects` 미정의로 컴파일 실패.

- [ ] **Step 3: 공통 조회 API와 DB 구현을 추가한다.**

`IDbMetadataService`에 아래 메서드를 추가하고, 기존 `GetSpDetailsAsync`는 `CodeObjectType.Procedure` 키를 만들어 새 메서드를 호출하게 한다.

```csharp
Task<SpDefinition> GetCodeObjectDetailsAsync(
    string connectionString,
    CodeObjectKey objectKey,
    int maxDepth,
    CancellationToken cancellationToken = default);
```

`DbMetadataService`는 `sys.objects`에서 `P`, `PC`를 `Procedure`, `FN`, `IF`, `TF`, `FS`, `FT`를 `Function`으로 정규화한다. 최상위 객체의 DDL 수집 실패는 기존 SP와 같이 예외를 유지한다. 의존성 DFS에서는 새 `DependencyInfo`를 만들 때 현재 탐색 대상을 `SourceObjectKey`에 설정하고, 동적 SQL 해석으로 발견한 항목은 `IsDynamicSqlCandidate = true`로 저장한다. UDF는 `sys.parameters`, `sys.columns`를 조회해 반환 형식 또는 TVF 반환 컬럼을 `SpDefinition`에 보관한다.

- [ ] **Step 4: 스냅샷 저장소와 오프라인 구현을 확장한다.**

```csharp
public class DbSnapshot
{
    public Dictionary<string, SpDefinition> StoredProcedures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SpDefinition> CodeObjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

`OfflineDbMetadataService.GetCodeObjectDetailsAsync`는 먼저 `CodeObjects[key.CanonicalName]`을 조회하고, Procedure일 때만 기존 `StoredProcedures[$"{schema}.{name}"]`로 폴백한다. 누락 시 현재의 `KeyNotFoundException` 메시지에 완전한 객체 키를 포함한다. `SnapshotManager.ExportSnapshotAsync`는 기존 SP 목록을 순회하면서 각 루트 SP의 `Dependencies`에서 발견한 FUNCTION/PROCEDURE도 `GetCodeObjectDetailsAsync`로 조회해 `CodeObjects`에 넣고, 루트 SP는 이전 호환용 `StoredProcedures`에도 계속 저장한다.

- [ ] **Step 5: 오프라인 테스트와 기존 메타데이터 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~OfflineDbMetadataServiceTests|FullyQualifiedName~DbMetadataServiceDetailsTests"`

Expected: PASS.

- [ ] **Step 6: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Services/IDbMetadataService.cs src/ReSet.Core/Services/DbMetadataService.cs src/ReSet.Core/Services/OfflineDbMetadataService.cs src/ReSet.Core/Services/SnapshotManager.cs src/ReSet.Core/Models/DbSnapshot.cs tests/ReSet.Core.Tests
git commit -m "feat: load stored procedures and functions as code objects"
```

## Task 3: 출력 경로·표준 DDL·객체 캐시 기반 만들기

**Files:**
- Create: `src/ReSet.Core/Services/OutputPathResolver.cs`
- Modify: `src/ReSet.Core/Services/ICacheManager.cs`
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `src/ReSet.Core/Models/CacheEntry.cs`
- Test: `tests/ReSet.Core.Tests/OutputPathResolverTests.cs`
- Test: `tests/ReSet.Core.Tests/CacheManagerTests.cs`

**Interfaces:**
- Consumes: `CodeObjectKey`, `SpDefinition`, output root.
- Produces: `ResolveSpecPath`, `ResolveCanonicalDdlPath`, `ComputeCompositeHash(SpDefinition, int)`, `IsCacheValid(CodeObjectKey, ...)`, `UpdateCache(CodeObjectKey, ...)`.

- [ ] **Step 1: 현재 DB/외부 DB 경로와 동일명 SP·UDF 캐시의 실패 테스트를 작성한다.**

```csharp
[Fact]
public void ResolveSpecPath_KeepsExistingProcedurePathForCurrentDatabase()
{
    var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
    var key = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Settle", CodeObjectType.Procedure);
    Assert.Equal("/tmp/output/Procedures/dbo.usp_Settle/docs/Spec.md", paths.ResolveSpecPath(key));
}

[Fact]
public void ResolveSpecPath_SeparatesExternalFunction()
{
    var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
    var key = CodeObjectKey.Create("AuditDB", "dbo", "FN_Calc", CodeObjectType.Function);
    Assert.Equal("/tmp/output/External/AuditDB/Functions/dbo.FN_Calc/docs/Spec.md", paths.ResolveSpecPath(key));
}
```

- [ ] **Step 2: 테스트가 resolver 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~OutputPathResolverTests`

Expected: `OutputPathResolver` 미정의로 실패.

- [ ] **Step 3: 출력 경로 resolver와 캐시 키를 구현한다.**

`OutputPathResolver`는 `ResolveSpecPath`, `ResolveDocsDirectory`, `ResolveCanonicalDdlPath`, `ResolveManifestPath`를 제공한다. 파일 시스템에 쓸 경로 세그먼트는 `Path.GetInvalidFileNameChars()`를 `_`로 바꾼다. `CacheEntry`의 `ProcedureName`은 역직렬화 호환을 위해 남기되 새 `ObjectKey`를 추가한다. `ICacheManager.ComputeCompositeHash`는 `int maxDepth`를 받고 해시 입력에 `MaxDepth:{maxDepth}`를 추가한다. `ICacheManager`와 `CacheManager`의 나머지 public 메서드는 `string procedureName` 대신 `CodeObjectKey objectKey`를 받고, resolver가 반환한 문서 경로를 검증한다.

- [ ] **Step 4: resolver 및 캐시 회귀 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~OutputPathResolverTests|FullyQualifiedName~CacheManagerTests"`

Expected: PASS.

- [ ] **Step 5: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Services/OutputPathResolver.cs src/ReSet.Core/Services/ICacheManager.cs src/ReSet.Core/Services/CacheManager.cs src/ReSet.Core/Models/CacheEntry.cs tests/ReSet.Core.Tests
git commit -m "feat: resolve canonical code object output paths"
```

## Task 4: SP/UDF 공통 검증 파이프라인과 UDF 프롬프트

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: `IDbMetadataService.GetCodeObjectDetailsAsync`, `CodeObjectKey`, `OutputPathResolver`.
- Produces: `RunCodeObjectPipelineAsync` and UDF 전용 프롬프트 분기.

- [ ] **Step 1: UDF가 공통 파이프라인으로 분석되고 프로시저 전용 지시가 제외되는 실패 테스트를 추가한다.**

```csharp
[Fact]
public async Task RunCodeObjectPipelineAsync_UsesFunctionMetadata()
{
    var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
    _dbService.GetCodeObjectDetailsAsync(Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(new SpDefinition { Name = "FN_Calc", ObjectType = CodeObjectType.Function, DdlText = "CREATE FUNCTION..." });

    var result = await _orchestrator.RunCodeObjectPipelineAsync("conn", key, 2, "OpenAI", "rules", true, "/tmp/out");

    Assert.NotNull(result.SpecMarkdown);
    await _aiService.Received().GenerateSpecificationAsync(
        Arg.Is<SpDefinition>(x => x.ObjectType == CodeObjectType.Function), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task GenerateSpecificationAsync_FunctionPrompt_DoesNotRequireTransaction()
{
    var result = await _service.GenerateSpecificationAsync(new SpDefinition { ObjectType = CodeObjectType.Function, DdlText = "CREATE FUNCTION..." }, "rules");
    Assert.DoesNotContain("BEGIN TRAN", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 새 테스트가 API와 프롬프트 분기 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~RunCodeObjectPipelineAsync_UsesFunctionMetadata|FullyQualifiedName~FunctionPrompt"`

Expected: `RunCodeObjectPipelineAsync` 미정의 또는 `BEGIN TRAN` 지시가 존재해 실패.

- [ ] **Step 3: 공통 내부 파이프라인을 추출한다.**

`RunPipelineAsync`는 Procedure 키를 만들어 아래 메서드를 호출하는 호환 래퍼로 바꾼다.

```csharp
public Task<CodeObjectPipelineResult> RunCodeObjectPipelineAsync(
    string connectionString, CodeObjectKey key, int maxDepth, string provider,
    string instructions, bool isBatchMode, string outputDirectory,
    bool enableCache = false, CancellationToken cancellationToken = default);
```

기존의 캐시, L1 정화 반영, L2 재시도, L3 배치 우회, Thinking 축적 로직은 공통 내부 메서드에 보존한다. UI 상태 문구는 `SP` 또는 `UDF`와 객체 키를 표시한다.

- [ ] **Step 4: UDF 시스템 프롬프트와 L2 체크리스트를 분기한다.**

`AiService`의 영문 시스템 프롬프트는 `ObjectType == Function`일 때 “return contract, determinism, side effects, formula, referenced tables/functions, TVF result schema”를 요구한다. UDF의 프롬프트와 체크리스트에는 `BEGIN TRAN`, `ROLLBACK`, 프로시저 오류 반환 코드 요구를 넣지 않는다. 기존 Procedure 프롬프트 문자열은 그대로 유지한다.

- [ ] **Step 5: 관련 파이프라인·프롬프트 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests|FullyQualifiedName~AiServiceTests"`

Expected: PASS.

- [ ] **Step 6: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests
git commit -m "feat: analyze functions through verification pipeline"
```

## Task 5: 재귀 그래프 실행과 중복·순환·실패 격리

**Files:**
- Create: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs`
- Create: `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs`
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: `IDbMetadataService`, `VerificationPipelineOrchestrator`, `CodeObjectKey`, `AnalysisNode`.
- Produces: `AnalyzeAsync`와 모든 객체의 `CodeObjectAnalysisResult`.

- [ ] **Step 1: 다이아몬드 의존성·순환 참조·자식 실패의 실패 테스트를 작성한다.**

```csharp
[Fact]
public async Task AnalyzeAsync_AnalyzesSharedFunctionOnlyOnceAndLinksBothCallers()
{
    // A -> X, B -> X 그래프를 구성하는 fake metadata/pipeline을 주입한다.
    var result = await sut.AnalyzeAsync(rootA, request, CancellationToken.None);

    Assert.Equal(1, result.Nodes.Single(x => x.Key.Name == "FN_X").AnalysisAttempts);
    Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(functionX).Status);
    Assert.Contains(result.Edges, x => x.Source == rootA && x.Target == functionX);
    Assert.Contains(result.Edges, x => x.Source == rootB && x.Target == functionX);
}

[Fact]
public async Task AnalyzeAsync_CycleDoesNotRequeueRunningObject()
{
    var result = await sut.AnalyzeAsync(cyclicA, request, CancellationToken.None);
    Assert.Equal(1, result.Nodes.Single(x => x.Key == cyclicA).AnalysisAttempts);
    Assert.Equal(1, result.Nodes.Single(x => x.Key == cyclicB).AnalysisAttempts);
}

[Fact]
public async Task AnalyzeAsync_ChildFailureDoesNotFailRoot()
{
    var result = await sut.AnalyzeAsync(rootA, request, CancellationToken.None);
    Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(failingChild).Status);
    Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(rootA).Status);
}
```

- [ ] **Step 2: 테스트가 오케스트레이터 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`

Expected: `DependencyAnalysisOrchestrator` 미정의로 실패.

- [ ] **Step 3: 그래프 구축과 자식 우선 실행을 구현한다.**

`AnalyzeAsync`는 루트의 `GetCodeObjectDetailsAsync` 결과에서 `SourceObjectKey`가 현재 노드인 PROCEDURE/FUNCTION 의존성만 간선으로 등록한다. 대상 DB가 현재 DB와 다르고 연결이 허용되지 않은 경우 `SkippedExternal`과 사유를 기록한다. `DiscoveryDepth > maxDepth`는 `SkippedDepth`로 기록한다.

각 노드 실행은 `Dictionary<CodeObjectKey, Task<AnalysisNode>>`로 단일 작업을 보장한다. DFS 방문 상태 `Visiting`을 만나면 새 작업을 만들지 않고 간선만 남긴다. 자식 작업을 모두 기다린 뒤 `RunCodeObjectPipelineAsync`를 호출한다. 예외는 노드의 `Error`와 `Failed` 상태로 변환하고 로그 경고 후 형제·부모를 계속 처리한다. `OperationCanceledException`만 재전파해 새 작업을 시작하지 않게 한다.

- [ ] **Step 4: 그래프 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`

Expected: PASS.

- [ ] **Step 5: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -m "feat: orchestrate recursive code object analysis"
```

## Task 6: 매니페스트·표준 DDL 저장과 결정론적 문서 링크

**Files:**
- Create: `src/ReSet.Core/Services/SpecificationLinker.cs`
- Modify: `src/ReSet.Core/Services/IMetadataExporter.cs`
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs`
- Test: `tests/ReSet.Core.Tests/SpecificationLinkerTests.cs`
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs`

**Interfaces:**
- Consumes: `CodeObjectAnalysisResult`, `OutputPathResolver`, `MechanicalValidator`.
- Produces: `ExportCodeObjectArtifactsAsync`와 `SpecificationLinker.UpdateReferencesAsync`.

- [ ] **Step 1: 링크·실패 상태·Reference 모드 중복 제거의 실패 테스트를 작성한다.**

```csharp
[Fact]
public async Task UpdateReferencesAsync_WritesRelativeLinkForSucceededChild()
{
    var markdown = "## 로직 흐름 요약\n본문";
    var updated = await linker.UpdateReferencesAsync(parentKey, markdown, graph);

    Assert.Contains("## 참조 코드 객체", updated);
    Assert.Contains("[dbo.FN_X](../../../Functions/dbo.FN_X/docs/Spec.md)", updated);
}

[Fact]
public async Task UpdateReferencesAsync_WritesReasonInsteadOfBrokenLink()
{
    var updated = await linker.UpdateReferencesAsync(parentKey, "# 명세", graphWithFailedChild);
    Assert.Contains("분석 불가: DDL 수집 권한 없음", updated);
    Assert.DoesNotContain("](../../../Functions/dbo.FN_X/docs/Spec.md)", updated);
}

[Fact]
public async Task ExportCodeObjectArtifactsAsync_ReferenceModeWritesCanonicalDdlOnly()
{
    await exporter.ExportCodeObjectArtifactsAsync(definition, key, graph, DependencyArtifactMode.Reference, outputRoot);
    Assert.True(File.Exists(resolver.ResolveCanonicalDdlPath(key)));
    Assert.False(File.Exists(Path.Combine(parentRawDir, "ddl", "functions", "dbo.FN_X.sql")));
}
```

- [ ] **Step 2: 테스트가 linker/export API 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SpecificationLinkerTests|FullyQualifiedName~ReferenceModeWritesCanonicalDdlOnly"`

Expected: 새 타입 또는 메서드 미정의로 실패.

- [ ] **Step 3: 링크 갱신기를 구현한다.**

`SpecificationLinker`는 기존 `## 참조 코드 객체` 섹션이 있으면 해당 섹션만 교체하고, 없으면 문서 끝에 추가한다. 링크 텍스트와 사유는 Markdown 문법을 이스케이프한다. 상대 경로는 `Path.GetRelativePath(parentSpecDirectory, childSpecPath)`를 `/` 구분자로 바꿔 계산한다. 링크 삽입 후 `MechanicalValidator.Validate`를 호출하고 `CleansedMarkdown`을 반환한다.

- [ ] **Step 4: MetadataExporter의 객체 아티팩트 저장을 구현한다.**

`ExportCodeObjectArtifactsAsync`는 표준 DDL을 `OutputPathResolver.ResolveCanonicalDdlPath`에 한 번 저장하고 `dependency-manifest.json`에 키, SHA-256, 호출 간선, 상태, 오류, Spec/DDL 상대 경로를 기록한다. `Reference` 모드에서는 부모의 `raw/ddl/procedures`와 `raw/ddl/functions`를 만들지 않는다. `PortableBundle` 모드에서만 기존 `ExportRawMetadataAsync`의 참조 DDL 파일 저장 분기를 실행한다. `prompt-context.md`는 두 모드 모두 AI에 전달한 완전한 원문을 저장한다.

- [ ] **Step 5: linker/exporter 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SpecificationLinkerTests|FullyQualifiedName~MetadataExporterTests"`

Expected: PASS.

- [ ] **Step 6: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Core/Services/SpecificationLinker.cs src/ReSet.Core/Services/IMetadataExporter.cs src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests
git commit -m "feat: link code object specifications and deduplicate ddl"
```

## Task 7: CLI 설정·단일 SP 플로우·진행 상태 연결

**Files:**
- Modify: `src/ReSet.Cli/appsettings.json`
- Modify: `src/ReSet.Cli/Program.cs`
- Test: `tests/ReSet.Core.Tests/CliArgsTests.cs`

**Interfaces:**
- Consumes: `IDependencyAnalysisOrchestrator.AnalyzeAsync`, `AnalysisSettings`, `DependencyArtifactMode`.
- Produces: 대화형·배치형 루트 SP 분석의 선택적 재귀 코드 객체 분석.

- [ ] **Step 1: 기본 비활성화와 모드 파싱의 실패 테스트를 작성한다.**

```csharp
[Fact]
public void AppSettings_DefaultsReferencedCodeObjectAnalysisToFalse()
{
    var configuration = LoadCliConfiguration();
    Assert.False(configuration.GetValue<bool>("AnalysisSettings:AnalyzeReferencedCodeObjects"));
    Assert.Equal("Reference", configuration["OutputSettings:DependencyArtifactMode"]);
}
```

- [ ] **Step 2: 테스트가 설정 키 부재로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~CliArgsTests`

Expected: 설정 키 값이 null이거나 assertion 실패.

- [ ] **Step 3: 설정과 분석 분기를 추가한다.**

`appsettings.json`에 아래 값을 추가한다.

```json
"AnalysisSettings": {
  "AnalyzeReferencedCodeObjects": false
},
"OutputSettings": {
  "DependencyArtifactMode": "Reference"
}
```

`Program.cs`는 `AnalyzeReferencedCodeObjects`가 true일 때만 기존 루트 `RunPipelineAsync` 대신 `DependencyAnalysisOrchestrator.AnalyzeAsync`를 호출한다. 배치 모드와 대화형 개별 SP 메뉴 모두에 적용한다. 대화형 메뉴는 루트 선택 뒤 “참조 SP/UDF도 분석” 여부를 기본 설정값으로 한 번 확인한다. 진행 UI에는 `n/total. SP|UDF 객체명 분석 중`만 출력하고 Thinking을 출력하지 않는다. 실패 노드는 `Markup.Escape`한 오류와 빈 줄을 포함해 경고하고, 루트 성공 문서는 계속 안내한다.

- [ ] **Step 4: CLI 설정 테스트와 기존 인수 파싱 테스트를 실행한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~CliArgsTests`

Expected: PASS.

- [ ] **Step 5: 변경 파일을 커밋한다.**

```bash
git add src/ReSet.Cli/appsettings.json src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/CliArgsTests.cs
git commit -m "feat: enable recursive code object analysis from cli"
```

## Task 8: 전체 회귀 검증과 문서 동기화

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md` (기능 상태가 변경된 경우만)

**Interfaces:**
- Consumes: Task 1~7의 실제 공개 API와 설정 이름.
- Produces: 사용자·에이전트가 현재 동작을 정확히 이해할 수 있는 프로젝트 문서.

- [ ] **Step 1: 설계 수용 기준별 검증 체크리스트를 작성한다.**

```text
- 하위 SP/UDF 각각의 Spec.md 생성
- 공유 UDF/SP의 단 한 번 분석
- 성공 대상만 상대 링크 생성
- 실패·외부 DB·깊이 초과 사유 표기
- Reference 모드 DDL 파일 중복 없음
- PortableBundle 모드 DDL 사본 존재
- 기존 단일 SP·캐시·오프라인·L1/L2/L3 회귀 없음
```

- [ ] **Step 2: 전체 테스트를 실행한다.**

Run: `dotnet test`

Expected: 모든 테스트 PASS, 실패 0건.

- [ ] **Step 3: 릴리스 빌드를 실행한다.**

Run: `dotnet build SettleProcDaily.slnx --no-restore --verbosity minimal`

Expected: build succeeded, error 0개.

- [ ] **Step 4: 프로젝트 핵심 문서를 소스와 동기화한다.**

`reset-doc-sync` 스킬을 사용해 README의 분석 플로우·설정 예시, AGENTS의 아키텍처 참조·소프트 페일 규칙, architecture 문서의 컴포넌트/데이터 흐름을 실제 구현과 일치시킨다. 구현하지 않은 옵션이나 경로는 문서에 적지 않는다.

- [ ] **Step 5: 문서와 작업 트리를 검사한다.**

Run: `git diff --check`

Expected: 출력 없음, 종료 코드 0.

Run: `git status --short`

Expected: 이번 기능과 문서 변경만 표시.

- [ ] **Step 6: 최종 변경을 커밋한다.**

```bash
git add README.md AGENTS.md docs/architecture.md docs/roadmap.md
git commit -m "docs: document recursive code object analysis"
```

## Self-Review

- 설계의 코드 객체 식별, SP/UDF 일반화, 그래프 중복 제거·순환 차단, DDL 정규화, 문서 링크, 캐시, 오프라인, CLI, 실패 격리, 테스트 수용 기준을 각각 Task 1~8에 배정했다.
- 모든 공개 타입과 메서드는 최초 사용 전 Task 1~6에 정의했다.
- 구현 단계에는 실제 테스트 이름, API 서명, 실행 명령과 기대 결과를 넣었으며 모호한 작업 지시를 남기지 않았다.
- 핵심 문서 동기화는 구현 후 실제 코드 기준으로만 수행하도록 Task 8에 포함했다.
