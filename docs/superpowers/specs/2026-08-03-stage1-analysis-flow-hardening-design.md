# 1단계 개별 SP 분석 플로우 견고화 설계

- 작성일: 2026-08-03
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행 작업: `2026-08-03-verification-honesty-followups` (설계·계획 완료, **구현 전**)

## 배경

ReSet.Cli 메인 메뉴 1번 "개별 Stored Procedure 역공학 분석"의 전체 경로를 점검했다. 검증 파이프라인 자체(L1/L2/L3)는 직전 두 사이클에서 정리되었으나, 그 결과를 **디스크에 남기고 사용자에게 보고하는 구간**에서 네 건의 결함이 남아 있다.

이번 사이클은 선행 작업(A~E)이 병합된 코드를 전제로 한다. 특히 A~E가 `SpecificationDocumentFormatter`를 `VerificationDocumentFormatter`로 개명하고 진입점을 셋으로 늘리므로, 이 문서의 포매터 관련 설계는 그 결과물 위에 얹힌다. 본문의 줄 번호는 A~E 구현 이전 시점(`319c965`) 기준이며, 구현 시점에는 이동해 있을 수 있다.

## 대상 결함

| # | 결함 | 근거 |
|---|---|---|
| 1 | 재귀 분석 중 취소하면 완료된 하위 명세서가 전량 소실된다 | `DependencyAnalysisOrchestrator.cs:64-78`, `:232-236` |
| 2 | 캐시 히트 문서가 새 `문서 작성일시`로 재기록된다 | `VerificationPipelineOrchestrator.cs:269-277`, `Program.cs:947`, `:1636-1645` |
| 3 | 참조분석 ON/OFF에 따라 루트 SP의 분석 컨텍스트 범위가 조용히 달라진다 | `Program.cs:1430-1467`, `DbMetadataService.cs:424-435` |
| 4 | 산출물 저장이 통째로 실패해도 화면에는 성공 패널이 뜬다 | `DependencyAnalysisOrchestrator.cs:453-456`, `Program.cs:954` |

부수적으로 재귀 모드에서 루트 `Spec.md`·`Thinking.md`·`raw/metadata.json`이 `PersistArtifactsAsync`와 `SaveOutputsAsync`에 의해 **두 번** 기록된다. 이것 자체는 내용이 같아 손상을 일으키지 않지만, 결함 4를 정직하게 고치려면 최종 저장자가 하나여야 하므로 함께 정리한다.

### 1 — 취소 시 산출물 소실

`AnalyzeAsync`는 그래프 실행을 **전부** 마친 뒤에야 디스크에 쓴다.

```csharp
var execution = new ExecutionState(rootKey.Database);
await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);
execution.ApplyCanonicalKeys();
await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);   // :70
...
await PersistArtifactsAsync(rootKey, effectiveRequest, result, cancellationToken);   // :78
```

`ExecuteDiscoveredNodesAsync`는 `OperationCanceledException`을 그대로 재던진다(`:232-236`). 객체 20개짜리 그래프에서 19번째에 취소하면, 이미 AI 호출을 마치고 L3 승인까지 받은 18개 명세서가 파일로 한 줄도 남지 않는다.

파이프라인 내부에서 객체별로 기록되는 `cleansing/*.sql`(`VerificationPipelineOrchestrator.cs:1158`)만 남으므로, 산출물 디렉터리는 정제 SQL은 있는데 명세서는 없는 상태가 된다.

이는 AI 비용이 실제로 버려지는 유일한 결함이다.

### 2 — 캐시 히트 시 타임스탬프 갱신

캐시 히트 경로는 AI를 호출하지 않고 기존 파일을 파싱해 반환한다.

```csharp
var specFilePath = outputPaths.ResolveSpecPath(cacheObjectKey);
if (System.IO.File.Exists(specFilePath))
{
    var cachedArtifact = await System.IO.File.ReadAllTextAsync(specFilePath, cancellationToken);
    var (cachedSpec, cachedReview) = ParseCachedSpecification(cachedArtifact);
    return (cachedSpec, spDef, cachedReview, null, verificationOutcome);   // :277
}
```

`Program`은 반환된 마크다운을 받아 `SaveOutputsAsync`를 호출하고, 포매터는 `DateTime.Now`를 찍는다(`:1636-1645`). 실제 분석은 없었는데 문서에는 새 날짜가 남는다.

캐시 유효 판정은 `ComputeCompositeHash(spDef, maxDepth)` 기반이므로, 히트했다는 것은 그 객체의 메타데이터가 이전과 동일하다는 뜻이다. `raw/metadata.json`을 포함해 다시 쓸 내용이 없다.

`EnableCache` 기본값이 `false`이므로 노출도는 낮다. 그러나 A가 캐시를 전량 무효화하면 그 직후부터 캐시를 켠 사용자는 이 경로를 매번 밟는다.

### 3 — 참조분석 ON/OFF의 컨텍스트 범위 차이

`RunConfiguredAnalysisAsync`의 두 분기는 서로 다른 메타데이터 수집 함수를 쓴다.

- 참조분석 OFF: `GetCodeObjectDetailsAsync(connectionString, key, maxDepth, ct)` — 전이 의존성 포함
- 참조분석 ON: 파이프라인이 `directDependenciesOnly: true`로 호출되어(`DependencyAnalysisOrchestrator.cs:28`) `maxDepth: 1`, `includeTransitiveDependencies: false` (`DbMetadataService.cs:424-435`)

하위 SP/UDF가 각자 명세서를 갖게 되므로 설계 의도는 일관된다. 그러나 결과적으로 **루트 SP를 분석하는 AI는 하위 SP의 소스는 보되 그 하위 SP가 건드리는 테이블의 스키마는 보지 못한다.** 루트 명세서의 CRUD 분석이 얕아지고, `raw/metadata.json`에 실리는 의존성 목록도 좁아진다.

이 metadata.json은 2단계 배치 설계(`BatchStepCatalog.cs:77`)와 지시서 번들(`MetadataExporter.cs:402-435`), Validator(`ReSet.Validator.Cli/Program.cs:631` 외)가 참조 테이블 스키마의 원천으로 쓴다.

조사 중 확인한 사실 하나를 기록해 둔다. `saveRawFiles`가 재귀+Reference 모드에서 강제로 꺼지지만(`Program.cs:950`), 지시서 번들은 `raw/ddl/*.md`를 **metadata.json에서 다시 생성**하므로(`MetadataExporter.cs:408-435`) 2단계가 깨지지는 않는다. SP 자신의 `raw/ddl` 폴더에 의존하지 않는다.

동작 자체는 바꾸지 않는다. 재귀 분석의 설계 의도를 되돌리는 대신, 사용자가 선택 시점에 그 트레이드오프를 알고 문서에서 사후 확인할 수 있게 한다.

### 4 — 무성 실패와 거짓 성공 메시지

`PersistArtifactsAsync` 전체가 하나의 try로 감싸여 있고 최종 catch는 로그만 남긴다.

```csharp
catch (Exception ex)
{
    Log.Warning(ex, "[의존성 분석] 객체 아티팩트 저장 중 오류가 발생했습니다 (계속 진행): {ObjectKey}", rootKey.CanonicalName);
}
```

`new OutputPathResolver(rootKey.Database, request.OutputDirectory)`(`:376`)가 빈 DB명으로 `ArgumentException`을 던지면 **모든 하위 명세서가 하나도 기록되지 않는데** TUI에는 아무 표시가 없다. 이어서 `Program.cs:954`가 성공 패널을 출력한다.

`RenderDependencyAnalysisFailures`(`Program.cs:1518`)는 `Failed` 노드만 훑으므로 이 경우를 잡지 못한다. `SkippedDepth`/`SkippedExternal` 노드도 화면에는 나오지 않는다.

## 설계

### 결과 계약: `SpAnalysisOutcome`

네 결함은 모두 같은 것을 요구한다 — **호출부가 파이프라인의 실제 결과를 알아야 한다.** 현재 `RunConfiguredAnalysisAsync`는 5-튜플을 반환하고, 여기에 "캐시 히트였나 / 저장은 됐나 / 그래프가 완전한가 / 분석 범위는 무엇인가"를 더 실어야 한다.

튜플을 키우지 않고 레코드를 도입한다. `ReSet.Core/Models`에 둔다.

```csharp
public enum AnalysisScope { Transitive, Direct }
public enum GraphCompletion { Complete, PartialCancelled }
public enum ArtifactPersistence { NotAttempted, Persisted, Failed }

public sealed record SpAnalysisOutcome
{
    public string? SpecMarkdown { get; init; }
    public SpDefinition? Definition { get; init; }
    public ReviewResult? Review { get; init; }
    public string? ThinkingText { get; init; }
    public VerificationOutcome Outcome { get; init; }

    public AnalysisScope Scope { get; init; }
    public GraphCompletion Completion { get; init; }
    public bool FromCache { get; init; }
    public DateTime? AnalyzedAt { get; init; }
    public ArtifactPersistence Persistence { get; init; }
    public IReadOnlyList<string> PersistenceErrors { get; init; } = Array.Empty<string>();
}
```

#### 열거형 0번 값의 선정

`VerificationOutcome`이 `ReviewNotRun`을 0번에 두어 "대입을 빠뜨린 생성부가 조용히 통과를 자칭하는 함정"을 막은 규칙(`CodeObjectAnalysisModels.cs:56-60`)을 따른다. 다만 필드마다 안전한 방향이 다르다.

- `GraphCompletion.Complete = 0` — 비재귀 경로는 그래프가 없어 실제로 항상 완결이다. 놓쳐도 사실과 같다.
- `ArtifactPersistence.NotAttempted = 0` — 놓치면 `Program`이 저장 책임을 떠안는다. 산출물이 안 생기는 것보다 두 번 생기는 쪽이 안전하다.
- `AnalysisScope.Transitive = 0` — **0번 값으로는 막을 수 없다.** 놓친 대입이 어느 쪽으로 틀릴지가 경로마다 다르다(비재귀는 실제로 `Transitive`, 재귀는 `Direct`). 대신 아래 팩토리 두 개로 생성부를 한정하고 테스트로 고정한다.

#### 생성부를 두 팩토리로 한정

`RunConfiguredAnalysisAsync`의 비재귀 분기는 `VerificationPipelineOrchestrator`를 **구상 타입**으로 받는다(`Program.cs:1412`). 메서드가 virtual이 아니라 NSubstitute로 대체할 수 없고, 그래서 이 메서드는 현재 테스트가 없다.

인터페이스를 새로 뽑는 대신 결과 조립을 순수 함수로 분리한다.

```csharp
public sealed record SpAnalysisOutcome
{
    /// 비재귀: 단일 객체 파이프라인 결과를 옮긴다. Scope = Transitive.
    public static SpAnalysisOutcome FromSingleObjectPipeline(CodeObjectPipelineResult result);

    /// 재귀: 그래프에서 루트 분석 결과를 찾아 옮긴다. Scope = Direct.
    /// 루트가 AnalysisResults에 없으면 SpecMarkdown = null, Outcome = ReviewNotRun.
    public static SpAnalysisOutcome FromDependencyGraph(CodeObjectPipelineResult result, CodeObjectKey rootKey);
}
```

`RunConfiguredAnalysisAsync`는 어느 쪽을 부를지만 고른다. 필드 대입은 전부 이 두 함수 안에 있고, 둘 다 `ReSet.Core`에 있으므로 인자만 만들어 직접 테스트할 수 있다.

두 팩토리가 모두 `CodeObjectPipelineResult`를 받으므로, 비재귀 분기는 튜플을 반환하는 `RunPipelineAsync` 대신 `RunCodeObjectPipelineAsync`를 직접 호출한다(`Program.cs:1432`).

#### `RunPipelineAsync`를 테스트 확장 메서드로 옮긴다

그 결과 `RunPipelineAsync`의 프로덕션 호출부가 0이 된다. 테스트는 40여 곳에서 이 튜플 반환을 구조분해로 쓰고 있다. A~E가 세운 "얇은 위임을 남기지 않는다"는 원칙에 따르면 테스트 전용 메서드를 프로덕션 코드에 남길 수 없고, 그렇다고 40개 테스트를 이번 결함과 무관하게 고치는 것도 리스크다.

메서드를 테스트 프로젝트의 확장 메서드로 옮긴다.

```csharp
// tests/ReSet.Core.Tests/PipelineTestExtensions.cs
internal static class PipelineTestExtensions
{
    public static async Task<(string? SpecMarkdown, SpDefinition? SpDef, ReviewResult? Review, string? ThinkingText, VerificationOutcome Outcome)>
        RunPipelineAsync(this VerificationPipelineOrchestrator orchestrator, /* 기존 시그니처 그대로 */)
    {
        var result = await orchestrator.RunCodeObjectPipelineAsync(...);
        return (result.SpecMarkdown, result.SpDef, result.Review, result.ThinkingText, result.Outcome);
    }
}
```

시그니처가 동일하므로 기존 테스트는 한 줄도 고치지 않는다. private인 `ResolveCurrentDatabase`의 역할(연결 문자열에서 `InitialCatalog` 추출)은 확장 메서드가 직접 수행한다.

#### 저장 책임의 분기

`Persistence`가 곧 저장 책임이다. 그리고 `SaveOutputsAsync`를 둘로 나눈다 — 캐시 히트는 문서만 건너뛰고 원천 산출물은 그대로 저장해야 하기 때문이다(아래 「5: 캐시 히트」 참조).

```csharp
if (result.Persistence == ArtifactPersistence.NotAttempted)
{
    await SaveRawArtifactsAsync(...);                              // raw/metadata.json, raw/prompt-context.md, raw/ddl/*
    if (!result.FromCache) await SaveDocumentsAsync(...);          // docs/Spec.md, docs/Thinking.md
}
```

`analyzeSelectedReferences` 플래그로 분기하지 않는다. 그 플래그로 판단하면 저장자가 바뀔 때마다 두 곳을 고쳐야 한다. 필드 이름이 계약이 되어야 한다.

### 1: 취소 시 부분 저장

`AnalyzeAsync`가 취소를 예외로 흘려보내지 않고 결과로 바꾼다.

```csharp
var completion = GraphCompletion.Complete;
try
{
    await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);
    execution.ApplyCanonicalKeys();
    await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);
}
catch (OperationCanceledException)
{
    completion = GraphCompletion.PartialCancelled;
}

var result = new CodeObjectPipelineResult { ..., Completion = completion };
using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await PersistArtifactsAsync(rootKey, effectiveRequest, result, persistCts.Token);
return result;   // 예외를 다시 던지지 않는다
```

예외로 던지면 "저장은 됐다"는 사실이 `Program`의 `catch (OperationCanceledException)`에 도달하지 못한다. 결과 레코드가 계약이라는 이 설계의 전제와도 어긋난다. 비재귀 경로는 기존대로 `OperationCanceledException`을 던지므로 `Program`의 catch 블록은 남는다.

`persistCts`는 취소된 원본 토큰이 아니라 별도의 grace 토큰이다. 취소된 토큰을 넘기면 `PersistArtifactsAsync` 내부의 `ThrowIfCancellationRequested`(`:398`, `:431`)가 즉시 던져 아무것도 쓰지 못한다. `CancellationToken.None`을 쓰지 않는 이유는 네트워크 드라이브에서 저장이 무한정 매달릴 수 있기 때문이다. 30초는 파일 수십 개 쓰기에 충분하고, 사용자가 두 번째 Ctrl+C로 빠져나오려 할 때의 인내 한계이기도 하다.

#### 취소는 문서가 아니라 화면으로 보고한다

`DiscoverAsync`는 자식을 모두 재귀한 **뒤에** 자기를 `ExecutionOrder`에 넣는다(`:170-174`). 후위 순회다. `ExecuteDiscoveredNodesAsync`는 첫 취소에서 멈추므로, 성공한 노드의 자식은 이미 처리된 상태다. 즉 **취소로 인해 "미완료 자식을 가진 성공 문서"가 생기는 일은 일반적으로 없다.** 저장되는 문서는 저마다 정확하고, 저장되지 않은 문서는 아예 없다.

그러므로 취소의 실제 위험은 문서 수준이 아니라 실행 수준이다 — 사용자가 출력 디렉터리를 보고 "이것이 이 SP의 전체 그래프"라고 오해하는 것. 이는 `Program`의 부분 완료 패널이 담당한다(아래 「4: 저장 실패 표면화」).

#### 참조 미완 배너 (엣지 케이스)

후위 순회 규칙에는 예외가 하나 있고, 이때는 문서 수준 경고가 필요하다.

- **순환 의존성**: `root→A→root`이면 `TryRegisterDepth`가 두 번째 `root` 진입을 막아 순서가 `[A, root]`가 된다. A의 자식인 `root`가 A보다 뒤에 온다.

> **정정(최종 리뷰).** 초안은 여기에 "깊이 재등록"이라는 두 번째 발화 경로를 적었다 — 깊이 제한으로 `SkippedDepth`가 된 객체가 더 얕은 경로로 재발견되면 부모보다 늦게 `ExecutionOrder`에 들어간다는 것. **이 시나리오는 존재하지 않는다.** `DiscoverAsync`가 `node.Status = Queued`와 `ExecutionOrder.Add`를 같은 지점에서 수행하므로 `Queued` 노드는 반드시 `ExecutionOrder`에 있고, 완주 실행은 그 목록을 전부 소진한다. 재등록이 일어나도 그 노드는 실행되며 `Queued`로 남지 않는다. 따라서 배너는 **순환 의존성과 취소가 함께 걸려야만** 발화한다. 이 문장을 남겨 두면 다음 사이클이 존재하지 않는 시나리오를 테스트하려 든다.

이 경우에만 성공한 문서의 참조 목록에 미완료 항목이 남는다. 배너는 그 문서에만 붙인다.

```csharp
// VerificationBanner
public static string UnresolvedReferences(IReadOnlyList<string> objectNames) =>
    "\n> [!CAUTION]\n> **[참조 미완] 사용자 취소로 아래 참조 객체가 분석되지 않았습니다.**\n"
    + string.Join("\n", objectNames.Select(name => $">   - {name}"))
    + "\n\n";
```

`L1Exhausted`의 오류 목록과 같은 형태다. 개수 대신 이름을 싣는 이유는, 이 배너를 읽는 사람이 다음에 할 일이 "그 객체를 다시 분석하는 것"이기 때문이다.

배너를 붙이는 곳은 `PersistArtifactsAsync`다. `linker.UpdateReferencesAsync`로 참조 섹션을 갱신한 **직후**, `BuildPersistedSpecification`이 포매터를 호출하기 **전에** 본문 앞에 붙인다. 기존 `VerificationBanner` 삽입부와 같은 순서이므로 최종 문서 구조는 `YAML → NOTE 블록 → CAUTION 배너 → 본문`이 된다.

판정은 참조 섹션 생성에 이미 쓰이는 자식 노드 상태로 한다 — `graph.DependencyEdges`에서 이 문서의 자식을 찾아 `Status`가 `Cancelled` 또는 `Queued`인 것을 모은다. 비어 있으면 배너를 붙이지 않는다.

### 2: YAML 확장 — `분석 범위`

A~E가 만드는 `VerificationDocumentFormatter.FormatSpecification`에 파라미터를 하나만 추가한다.

```csharp
public static string FormatSpecification(
    string body, ReviewResult? review, VerificationOutcome outcome,
    string provider, string modelName, string? effort, DateTime timestamp,
    AnalysisScope? scope = null);
```

출력:

```
---
검증 상태: 통과
분석 범위: 직접 의존성      # 참조 SP/UDF 재귀 분석 모드
종합 신뢰도: 92
정합성 점수: 9/10
---
```

- 값은 `직접 의존성`(`Direct`) 또는 `전이 의존성`(`Transitive`).
- `scope == null`이면 줄을 내지 않는다. 통합 계획서 경로(`FormatConsolidatedPlan`, `FormatUnverifiedPlan`)는 영향받지 않는다.

`GraphCompletion`은 **YAML에 싣지 않는다.** 그것은 실행 단위 사실이라 문서 단위 헤더에 넣으면 어긋난다 — 취소된 실행에서도 저장된 문서 각각은 완전하다. 실행 상태는 화면이 보고한다.

`SpecHeaderReader`(`ReSet.Cli/SpecHeaderReader.cs`)는 인식하지 못하는 키를 무시하므로 변경이 필요 없다. 이번 사이클에서 `분석 범위`를 읽는 소비부는 없다 — 사람이 읽는 기록이다.

### 3: 저장 일원화

재귀 경로에서 `Program`이 `SaveOutputsAsync`를 부르지 않게 되면, `PersistArtifactsAsync`가 흡수해야 할 것이 둘 있다.

**`Thinking.md` 헤더.** 현재 `PersistThinkingAsync`(`:471-499`)는 제목과 구분선만 쓴다. `SaveOutputsAsync`(`Program.cs:1668-1673`)는 provider/model/effort와 작성일시를 넣는다. 후자 수준으로 맞춘다. 재귀 모드에서 하위 객체의 `Thinking.md`가 루트보다 정보가 적을 이유가 없다.

**`raw/ddl/*` 분산 덤프.** `PersistArtifactsAsync`에는 없다. 재귀 경로에서는 `DependencyArtifactMode`가 이미 참조 DDL 사본을 통제하므로 `saveRawFiles`를 무시한다. 이는 현행 `Program.cs:950`의 게이팅이 사실상 내리고 있는 판단이며, 그 판단을 orchestrator로 옮기는 것뿐이다.

`raw/metadata.json`과 `raw/prompt-context.md`는 `ExportCodeObjectArtifactsAsync`가 이미 쓴다(`MetadataExporter.cs:62-89`). 이중 기록이 사라진다.

### 4: 저장 실패 표면화

`CodeObjectPipelineResult`에 저장 결과를 싣는다.

```csharp
// CodeObjectPipelineResult
public GraphCompletion Completion { get; set; }
public ArtifactPersistence Persistence { get; set; }
public List<string> PersistenceErrors { get; set; } = new();
public bool FromCache { get; set; }
public DateTime? AnalyzedAt { get; set; }
```

`PersistArtifactsAsync`의 최종 catch는 로그만 남기는 대신 `Persistence = Failed`와 사유를 기록한다. 개별 노드 저장 실패는 기존 do/while 재링크 루프를 그대로 유지한다 — 그 루프는 실패한 노드를 성공 집합에서 빼고 링크를 다시 계산하기 위한 것이므로 정확하다.

`RenderDependencyAnalysisFailures`(`Program.cs:1518`)를 `RenderAnalysisDiagnostics`로 넓힌다.

- `Failed` 노드 (기존)
- `Persistence == Failed`이면 `PersistenceErrors`
- `SkippedDepth` / `SkippedExternal` 노드 요약 (개수와 사유별 집계)

`Program`은 `Persistence == Failed`이면 성공 패널 대신 실패 패널을 낸다.

`Completion == PartialCancelled`이면 부분 완료 패널을 낸다. **취소 보고의 본체가 여기다.** 저장된 문서는 저마다 정확하므로 문서에 남길 것이 없고, 사용자가 알아야 하는 것은 "출력 디렉터리에 있는 것이 이 SP의 전체 그래프가 아니다"라는 실행 수준 사실이다.

```
┌─ dbo.USP_Settle 부분 완료 ─────────────────────────┐
│ 사용자 취소로 분석이 중단되었습니다.                 │
│ 완료: 3 / 발견: 7 객체                              │
│ 저장되지 않은 객체: dbo.USP_Calc, dbo.FN_Rate, ...  │
└────────────────────────────────────────────────────┘
```

`발견`은 `graph.Nodes.Count`다. `ExecutionOrder.Count`가 아니다 — 깊이 제한과 외부 DB로 스킵된 객체도 사용자가 알아야 할 그래프의 일부다. `저장되지 않은 객체`는 `Status != Succeeded`인 노드 이름이며, 10개를 넘으면 앞 10개와 `외 N건`으로 줄인다.

### 5: 캐시 히트

`ParseCachedSpecification`이 `문서 작성일시`를 NOTE 블록 제거 **전에** 뽑아 함께 반환한다.

```csharp
private static (string Specification, ReviewResult Review, DateTime? AnalyzedAt)
    ParseCachedSpecification(string cachedArtifact)
```

이 값이 파이프라인 결과의 `AnalyzedAt`으로 실린다. "다시 쓰지 않는다"는 원칙이 두 경로에서 다르게 실현된다.

**비재귀**: `FromCache == true`면 `Program`이 `SaveDocumentsAsync`를 건너뛴다. `Spec.md`는 이미 그 내용이므로 아무 일도 일어나지 않고, `Thinking.md`는 캐시 히트 시 `ThinkingText`가 `null`이라 어차피 대상이 아니다.

`SaveRawArtifactsAsync`는 **건너뛰지 않는다.** `raw/*`는 타임스탬프를 담지 않아 거짓 주장을 만들 수 없고, 사용자가 `SaveRawJson`을 나중에 켠 경우 캐시 히트가 계속되는 한 `raw/metadata.json`이 영원히 생기지 않는 함정이 생긴다. 그 파일이 없으면 2단계가 해당 SP를 배치 스텝에서 제외한다(`Program.cs:1206`).

화면에는 `캐시 재사용 (원본 분석: 2026-08-01 14:22:03)`을 표시한다. `AnalyzedAt == null`이면 `캐시 재사용 (원본 분석 시각 불명)`으로 표시한다 — 모르는 것을 아는 척하지 않는다.

**재귀**: 파일을 건드리지 않을 수 없다. 참조 섹션은 **이번 실행의 자식 노드 상태**에 의존하므로, 캐시가 유효해도(= 그 객체의 해시가 같아도) 자식이 이번에 실패하면 링크가 달라져야 한다. 대신 `AnalyzedAt`을 포매터의 `timestamp` 인자로 넘겨 `문서 작성일시`가 원본 분석 시각을 유지하게 한다.

두 경로 모두 "분석하지 않았는데 새 날짜가 찍히는 일"은 없다.

`FromCache`와 `AnalyzedAt`은 두 곳에 실린다.

- `CodeObjectPipelineResult` 최상위 — 비재귀 경로에서 `FromSingleObjectPipeline`이 읽는다.
- `CodeObjectAnalysisResult` — 재귀 경로에서 노드별로 전달되어 `PersistArtifactsAsync`가 읽는다.

재귀 경로의 `SpAnalysisOutcome.FromCache`는 **루트 노드의 값**이다. 그래프 전체가 캐시였는지가 아니다 — 그 정보가 필요한 소비부가 없고, 노드마다 다른 값을 하나로 접으면 어느 쪽으로 접어도 거짓이 된다.

### 6: 분석 범위 고지

동작은 바꾸지 않고 확인 프롬프트 앞에 안내를 낸다(`Program.cs:899-901`).

```
[grey]참조 분석을 켜면 참조 객체마다 별도 명세서와 승인 화면이 생기고,
루트 SP는 직접 의존성만으로 분석됩니다(하위 SP가 쓰는 테이블 스키마는 루트 컨텍스트에서 제외).[/]
선택한 SP가 참조하는 SP/UDF도 함께 분석하시겠습니까? [y/n]
```

사후 확인은 명세서 YAML의 `분석 범위` 줄이 담당한다.

## 에러 처리

| 상황 | 처리 |
|---|---|
| grace 토큰(30초) 만료 | `PersistArtifactsAsync` 내부 `ThrowIfCancellationRequested`가 던짐 → 최종 catch가 `Persistence = Failed` + 사유 기록 → `Program`이 실패 패널 |
| 빈 DB명으로 `OutputPathResolver` 생성 불가 | `AnalyzeAsync` 진입부에서 즉시 throw. 호출부 결함을 조용히 삼키지 않는 선례(`VerificationPipelineOrchestrator.cs:1570-1574`의 `outputRoot` 검사)를 따른다. `Program`의 일반 catch가 잡아 에러를 표시한다 |
| 개별 노드 저장 실패 | 기존 do/while 재링크 루프 유지. 해당 노드만 `Failed`, 나머지는 계속 |
| 캐시 `문서 작성일시` 파싱 실패 | `AnalyzedAt = null` → `DateTime.Now` 폴백 + `Log.Warning`. A가 레거시 캐시를 전량 무효화하므로 히트하는 문서는 반드시 신형 포맷이다. 이 경로에 도달했다면 포매터 출력이 깨졌다는 뜻이고, 그 사실이 날짜보다 중요하다 |
| 저장 중 두 번째 Ctrl+C | **정정됨(아래 참조).** 이제 핸들러가 이미 취소 요청된 상태에서도 `e.Cancel = true`를 설정하고 "산출물을 저장 중입니다" 안내를 낸다. 두 번째 이후의 Ctrl+C는 프로세스를 죽이지 못하며, 30초 grace 상한이 실제로 유일한 탈출구가 된다 |

> **정정(최종 리뷰).** 위 행의 초안은 "grace CTS는 `_currentCts`와 별개라 영향받지 않는다. 30초 상한이 유일한 탈출구다"였다. **틀렸다.** grace CTS가 `_currentCts`와 별개인 것은 맞지만, 그것이 프로세스가 살아 있다는 뜻은 아니었다. `Console.CancelKeyPress` 핸들러가 `if (_currentCts != null && !_currentCts.IsCancellationRequested)` 안에서만 `e.Cancel = true`를 설정했으므로, 첫 Ctrl+C 이후의 입력은 조건을 통과하지 못하고 **.NET 기본 동작(프로세스 즉시 종료)**을 탔다. 즉 두 번째 Ctrl+C가 즉시 탈출구였고, 그 대가는 `File.WriteAllTextAsync`가 중간에 죽어 남는 **truncate된 `Spec.md`** 였다. 게다가 재링크 do/while이 성공 노드를 전부 다시 쓰므로 **이전 실행의 멀쩡한 명세서까지** 손상됐다 — 이 사이클이 지키려던 AI 비용을 정반대로 파괴하는 경로다.
>
> 이 위험이 초안 시점에는 실제로 없었다는 점은 짚어 둘 만하다. 이전에는 취소 후 남은 일이 스택 되감기뿐이라 즉시 종료가 무해했다. 취소 뒤에 최대 30초짜리 파일 쓰기 구간을 **새로 만든 것이 이 사이클 자신**이고, 그러면서 핸들러의 조건을 갱신하지 않아 무해했던 동작이 유해해졌다.
>
> 수정은 핸들러 안에서 끝난다. `_currentCts == null`이면 그대로 반환하고, 그렇지 않으면 첫 입력(취소 요청 + 정리 안내)과 두 번째 이후 입력(저장 중이니 기다리라는 안내)을 구분한 뒤 **두 경우 모두** `e.Cancel = true`를 설정한다. orchestrator가 CLI의 전역 CTS를 알 필요는 여전히 없다 — 초안의 그 판단은 유효하다.

## 테스트 전략

`DependencyAnalysisOrchestratorTests`

- 취소 시 완료 노드의 `Spec.md`가 디스크에 남고 `Completion == PartialCancelled`
- 취소 시 미완료 노드의 `Spec.md`는 생기지 않음
- 취소 시 미완료 노드가 참조 목록에 "분석 취소"로 표기
- **순환 의존성** 그래프(`root→A→root`)에서 취소하면 A의 문서에 참조 미완 배너가 붙고 미분석 객체 이름이 실림
- 취소 없이 완료한 그래프에서는 어느 문서에도 참조 미완 배너가 붙지 않음
- 저장 실패 시 `Persistence == Failed`이고 `PersistenceErrors`가 사유를 담음
- 빈 DB명이면 `AnalyzeAsync`가 즉시 throw
- 캐시 히트 노드가 원본 `AnalyzedAt`으로 렌더링됨
- `Thinking.md` 헤더에 provider/model/effort가 실림

`SpAnalysisOutcomeTests` (신규)

- `FromSingleObjectPipeline` → `Scope == Transitive`, `Persistence == NotAttempted`
- `FromSingleObjectPipeline`이 최상위 `FromCache`/`AnalyzedAt`을 옮김
- `FromDependencyGraph` → `Scope == Direct`, `Persistence`/`Completion`/`PersistenceErrors`가 그래프 결과를 그대로 옮김
- `FromDependencyGraph`의 `FromCache`/`AnalyzedAt`이 **루트 노드**의 값을 옮김 (자식이 캐시여도 루트가 아니면 `false`)
- `FromDependencyGraph`에서 루트가 `AnalysisResults`에 없으면 `SpecMarkdown == null`, `Outcome == ReviewNotRun`
- 대입을 생략한 `new SpAnalysisOutcome()`의 기본값이 `Transitive`/`Complete`/`NotAttempted`

`PipelineTestExtensions` 이전은 별도 테스트를 만들지 않는다. 기존 40여 개 `RunPipelineAsync_*` 테스트가 한 줄도 바뀌지 않은 채 통과하는 것이 곧 검증이다.

> **정정(최종 리뷰).** 위 문장은 **틀렸다.** 시그니처가 같아 테스트가 컴파일되고 통과한 것은 맞지만, 그 테스트들이 무엇을 검증하는지가 바뀌었다. 이전에는 `RunPipelineAsync_*`가 **프로덕션 코드**(`VerificationPipelineOrchestrator.RunPipelineAsync`)를 실행했다. 이전 후에는 **테스트 전용 사본**(`PipelineTestExtensions`)을 실행하고, 프로덕션의 대응 경로(`Program.RunConfiguredAnalysisAsync`의 비재귀 분기)는 같은 로직을 따로 갖게 되었다. 그 결과 40여 개 테스트가 통과해도 프로덕션 경로가 옳다는 보장이 사라졌다 — 두 사본이 갈라지면 테스트는 계속 초록불이다.
>
> 최종 수정에서 키 조립을 `VerificationPipelineOrchestrator.CreateProcedureKey(connectionString, schema, name)` 하나로 뽑고 확장 메서드와 `Program`이 **같은 것**을 호출하게 해 이 갭을 닫았다. 이제 위 문장이 다시 참이 된다.
>
> 교훈은 일반적이다. "얇은 위임을 프로덕션에 남기지 않는다"보다 **"테스트가 프로덕션을 검증한다"가 상위 원칙**이다. 프로덕션 코드를 테스트로 옮길 때는 옮긴 쪽이 아니라 남은 쪽이 무엇으로 검증되는지를 먼저 확인해야 한다.

`VerificationDocumentFormatterTests` (A~E 파일 확장)

- `scope == null`이면 `분석 범위` 줄이 없음
- `Direct` → `분석 범위: 직접 의존성`, `Transitive` → `분석 범위: 전이 의존성`
- `분석 범위` 줄이 YAML 블록 안에 들어가고 점수 줄과 공존함

`VerificationPipelineOrchestratorTests`

- `ParseCachedSpecification`이 `문서 작성일시`를 파싱하고 NOTE 블록은 제거
- 날짜가 없거나 형식이 깨지면 `AnalyzedAt == null`
- 캐시 히트 시 결과의 `FromCache == true`이고 `AnalyzedAt`이 원본 값을 담음
- 캐시 미스 시 `FromCache == false`, `AnalyzedAt == null`

`Program`의 저장 분기(`SaveRawArtifactsAsync` / `SaveDocumentsAsync`)는 자동 테스트 대상이 아니다. `RunConfiguredAnalysisAsync`가 구상 타입 의존을 갖는 한 이 구간은 테스트 하네스에 올릴 수 없고, 그것을 푸는 일은 범위 밖(C안)이다. 분기 조건을 `Persistence`/`FromCache` 같은 결과 필드로만 구성해 각 조건 안의 판단은 없앴다.

> **정정(최종 리뷰).** 초안은 여기에 "분기 조건을 두 필드로만 구성해 **판단 로직 자체를 없앴다** — 테스트할 로직이 남지 않는 것이 이 구간의 방어책이다"라고 적었다. **절반만 사실이다.** 개별 조건이 단순해진 것은 맞지만 조건의 **개수**가 줄지 않았다 — 대화형 블록은 세 조건(`Completion`, 빈 명세서, `Persistence`), 배치 블록은 다섯 조건(`Completion`, 빈 명세서, `Persistence`, `FromCache`, `migrationPlan` 유무)을 갖는다. 없어진 것은 조건 **안**의 판단이고, 남은 것은 조건 **사이의 순서**라는 판단이다.
>
> 그리고 정확히 그 지점에서 이 사이클에 **두 번** 버그가 났다. 배치 블록은 빈 명세서 검사가 취소 판정보다 앞에 있어 사용자 취소를 "명세서 획득 실패"로 오보했고(실행 중 발견), 대화형 블록은 같은 결함이 고쳐지지 않은 채 남았다(최종 리뷰에서 발견). 세 번째로 계획서 저장이 `Persistence` 게이트 **안**에 놓여 재귀 경로에서 산출물을 버렸다.
>
> 정확한 자평은 이렇다: **판단 로직이 조건 안에서 조건 사이의 순서로 옮겨갔고, 그 순서는 테스트되지 않는다.** 이 구간의 실질적 방어책은 "테스트할 로직이 없음"이 아니라 코드 리뷰와 「완료 확인」의 수동 검증이며, 그 둘이 실제로 세 건을 모두 잡았다. 순서를 테스트 가능하게 만들려면 `Program`의 분기를 순수 함수로 뽑아야 하고, 그것은 C안(범위 밖)에 속한다.

`VerificationBannerTests`

- `UnresolvedReferences`가 객체 이름을 목록으로 렌더링하고 `[!CAUTION]`으로 시작

## 범위 밖

- 비재귀 경로를 `DependencyAnalysisOrchestrator`로 통일하는 일. 요청 모델과 파이프라인 호출, 배치 모드(`Program.cs:641`)를 함께 재배선해야 하며, 이번 네 결함 중 어느 것도 그것을 요구하지 않는다.
- 재개 가능한 중간 상태 저장. 캐시 기능과 역할이 겹친다.
- `AllowExternalDatabaseConnections`가 비재귀 경로와 메타데이터 계층(`includeExternalCodeObjects`는 모든 호출부에서 `true` 하드코딩)에 도달하지 않는 문제.
- `SaveOutputsAsync`의 경로 조립이 `OutputPathResolver.EncodePathSegment`를 쓰지 않아 식별자에 `.`이나 파일명 금지문자가 있으면 캐시 조회 경로와 저장 경로가 갈라지는 문제. 발생률이 낮고 이번 네 결함과 독립적이다.
- SP 목록이 시작 시 1회만 로드되어 세션 중 DB 변경이 반영되지 않는 문제.
- L2 리뷰 호출 재시도 인프라. A~E 스펙이 이미 별도 사이클로 남겨둔 항목이다.

> **정정(최종 리뷰).** 이 목록에는 "배치 모드에서 참조분석과 `MigrationSettings:Enabled`를 동시에 켜면 단일 SP의 `BatchMigrationPlan.md`가 생성되지 않는다"는 항목이 있었고, 근거로 "저장 책임이 오케스트레이터로 넘어갔는데 오케스트레이터는 계획서 개념을 갖지 않기 때문"을 들었다. **근거가 사실이 아니었다.** `SaveMigrationPlanAsync`가 쓰는 `{outputDir}/Procedures/{schema}.{name}/docs/BatchMigrationPlan.md`는 `OutputPathResolver.ResolveDocsDirectory`와 같은 경로이고, 오케스트레이터는 그 파일을 읽지도 쓰지도 않는다. 소유권 충돌이 없으므로 `Persistence` 게이트 밖에서 저장하면 그만이었다.
>
> 실제 증상은 "생성되지 않는다"보다 나빴다. `GenerateBatchMigrationPlanAsync`는 `Persistence`와 무관하게 호출되므로 재귀 경로에서도 **AI 비용은 그대로 냈고**, 그 결과를 게이트 안의 `SaveMigrationPlanAsync`가 받지 못해 **버렸으며**, 그러고도 `[green]성공:[/] … 분석 완료 및 저장!`을 출력했다. "저장하지 않음"이 아니라 "버리고 저장했다고 보고함"이다.
>
> 최종 수정에서 `SaveMigrationPlanAsync` 호출을 `Persistence` 게이트 밖으로 옮겨 해소했다. `migrationPlan`이 비어 있으면 아무것도 하지 않는 가드는 유지된다. **이 항목은 더 이상 범위 밖이 아니다.**
