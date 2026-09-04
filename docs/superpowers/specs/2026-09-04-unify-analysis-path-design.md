# 비재귀 분석 경로를 `DependencyAnalysisOrchestrator`로 통일하는 설계

- 작성일: 2026-09-04
- 대상: `ReSet.Cli/Program.cs`의 분석 기동부, `ReSet.Core`의 `DependencyAnalysisOrchestrator`·`MetadataExporter`
- 닫는 결함: `docs/known-defects.md`의 「기본 설정에서 `dependency-manifest.json`이 아예 안 생기고
  `metadata.json`이 스위치에 걸린다」와 「비재귀 경로가 `DependencyAnalysisOrchestrator`로
  통일되지 않았다」 두 항목

## 1. 배경 — 하나의 축, 두 명의 작성자

`AnalysisSettings:AnalyzeReferencedCodeObjects` 하나가 산출물의 저장 규칙 전체를 가른다.
그런데 그 축을 집행하는 코드가 **서로 모르는 둘로 갈라져 있다.**

| | **OFF**(기본값) | **ON** |
| :--- | :--- | :--- |
| 분석 | `VerificationPipelineOrchestrator.RunCodeObjectPipelineAsync` 직접 호출 | `DependencyAnalysisOrchestrator.AnalyzeAsync` |
| 저장 | `Program.SaveRawArtifactsAsync` + `Program.SaveDocumentsAsync` | `DependencyAnalysisOrchestrator.PersistArtifactsAsync` |
| 원천 덤프 | `MetadataExporter.ExportRawMetadataAsync` (스위치 3개) | `MetadataExporter.ExportCodeObjectArtifactsAsync` (스위치 없음) |

같은 파일이 경로마다 다른 규칙을 받는다. `metadata.json`은 한쪽에서만 스위치에 걸리고,
`dependency-manifest.json`과 `Objects/` 정본은 한쪽에서 아예 안 나오며,
`prompt-context.md`는 **경로에 따라 집도 형식도 다르다**(OFF는 마크다운 머리말을 씌우고
ON은 원문을 그대로 쓴다).

이 갈라짐은 설계가 아니라 이력이다. `docs/output-artifacts.md` §3의 표에 있는
「어느 경로가 쓰나」 칸 전체가 이 이력의 그림자다.

### 1.1 실측 — 스위치 셋은 실사용 경로에서 이미 무력하다

기본값은 OFF인데(`appsettings.json:224`), 저장소의 `output/` 트리는 **전량 ON으로**
만들어져 있다.

```
객체 폴더 31개 (Objects/ 24 + External/*/Objects/ 7)
  Procedures|Functions/[객체]/raw/  → metadata.json 31 · dependency-manifest.json 31
  (External/)Objects/[객체]/raw/    → object_definition.sql 31 · prompt-context.md 31
  raw/ddl/ · sp_definition.sql      → 0건
```

`SaveRawJson`·`SaveRawContext`·`SaveRawFiles`는 ON 경로가 읽지 않으므로,
**실사용 실행에서 세 키는 아무 효력이 없다.** 용량을 아끼려고 둔 스위치가
아무것도 아끼지 않고 문서의 표만 두 겹으로 불렸다.

## 2. OFF는 「깊이 0」이 아니다 — 통일의 유일한 함정

`MaxDepth = 0`으로 오케스트레이터에 태우면 안 된다. 두 경로는 **메타데이터 수집 범위**가
다르기 때문이다.

| | 메타데이터 수집 | 이유 |
| :--- | :--- | :--- |
| OFF | `GetCodeObjectDetailsAsync(maxDepth)` — **전이적** | 자식이 자기 명세서를 갖지 않으므로 루트 하나가 전부를 설명해야 한다 |
| ON | `GetCodeObjectDetailsDirectAsync` — **직접 의존성만** | 자식마다 `Spec.md`가 따로 나오므로 루트가 손자까지 볼 이유가 없다 |

`VerificationPipelineOrchestrator.RunCodeObjectPipelineCoreAsync`의 `directDependenciesOnly`
분기가 이 차이를 만들고, `AnalysisScope`(`Transitive` / `Direct`)가 그 사실을
`Spec.md` 머리에 적는다. 대화형 메뉴도 이 차이를 사용자에게 고지한다
(`Program.cs:1320`, "루트 SP는 직접 의존성만으로 분석됩니다").

**따라서 통일은 이 축을 지운다는 뜻이 아니라, 한 코드가 두 값을 다루게 한다는 뜻이다.**

## 3. 확정된 전제 (사람 결정, 2026-09-04)

| 축 | 결정 |
| :--- | :--- |
| 통일 범위 | **분석까지 통일.** `DependencyAnalysisOrchestrator`가 유일 진입점이 된다 |
| 세 스위치 | **셋 다 폐기.** raw는 항상 쓴다 |
| `prompt-context.md` 자리 | **객체 디렉터리로 이사.** `Procedures\|Functions/[객체]/raw/` |
| OFF 모드 존치 | **존치.** 대화형 선택지와 전이적 스코프 명세서를 유지한다 |

## 4. 설계

### 4.1 플래그 하나로 두 축을 함께 넘긴다

`DependencyAnalysisRequest`에 필드 하나를 더한다.

```csharp
public bool AnalyzeReferencedCodeObjects { get; init; } = true;
```

이 값이 두 곳을 동시에 결정한다. 두 축은 실제로 한 축이므로 값 조합을 넷으로
불리는 `AnalysisScope` enum 안은 채택하지 않는다.

| 소비 지점 | ON | OFF |
| :--- | :--- | :--- |
| `DiscoverAsync`의 자식 순회 | 돈다 | **건너뛴다** → `ExecutionOrder = [root]`, 간선 0 |
| pipelineRunner의 `directDependenciesOnly` | `true` | **`false`** → 전이적 메타데이터 보존 |
| `Spec.md`의 `AnalysisScope` | `Direct` | `Transitive` |

> **주의** — `DependencyAnalysisRequest.PrintMembers`는 손으로 쓴 것이다.
> 필드를 더하면 거기에도 더해야 `ToString`이 새 값을 조용히 빠뜨리지 않는다.

### 4.2 CLI 분기 삭제

`Program.RunConfiguredAnalysisAsync`(`Program.cs:2014`)의 OFF 분기(`:2033-2055`)를 지우고
항상 `AnalyzeAsync`를 부른다. `analyzeReferencedCodeObjects`는 request에 실어 넘긴다.
함수의 바깥 계약(`SpAnalysisOutcome` 반환)은 바뀌지 않는다.

`ResolveAnalysisDatabaseAsync`가 이제 두 모드 모두에서 돈다. OFF가 쓰던
`VerificationPipelineOrchestrator.CreateProcedureKey`는 DB를 연결 문자열의
`InitialCatalog` **하나로만** 해석하고 없으면 빈 문자열을 넣는데(`ResolveCurrentDatabase`),
그 빈 값은 `AnalyzeAsync`의 빈 DB 가드에 걸린다 — 오프라인 스냅샷 모드가 정확히 그 경우다.
`ResolveAnalysisDatabaseAsync`는 `InitialCatalog` → DB 조회 → 설정값 순으로 폴백하므로
그 구멍이 없고, 오프라인에서 이 해석이 옳다는 것은
`CliArgsTests.RunConfiguredAnalysisAsync_UsesOfflineSnapshotDatabaseForRecursiveRoot`가 이미 지킨다.

경로는 그대로다 — 해석된 DB가 곧 분석 기준 DB이므로 `OutputPathResolver.IsCurrentDatabase`가
참이고, 루트는 지금과 같이 `Procedures/[스키마].[이름]/`에 남는다. 달라지는 것은
식별자에 특수문자가 있을 때뿐이며, 그때는 `EncodePathSegment`를 거쳐 **캐시 조회 경로와
일치하는 쪽으로** 바뀐다.

### 4.3 저장부에서 함께 고치는 것 넷

**(1) `PersistThinkingAsync`의 캐시 히트 파괴 — 통일의 전제조건**

`VerificationPipelineOrchestrator.cs:315`의 캐시 히트는 `thinking: null`을 돌려주고,
`DependencyAnalysisOrchestrator.cs:575`는 그것을 그대로
`ThinkingLogDocument.Compose(null, …, DateTime.Now)`에 넣는다. 결과적으로
**캐시 히트 회차마다 멀쩡한 `Thinking.md`가 「추론 없음」 자리표시자와 오늘 날짜로 덮인다.**

`prompt-context.md`는 `MetadataExporter.cs:79`에서 정확히 이 이유로 보호받는데
`Thinking.md`에는 그 가드가 없다. 지금 OFF 경로는 CLI의 `if (!result.FromCache)` 게이트
덕분에 **우연히** 이 결함을 피하고 있으므로, 그 게이트를 지우기 전에 가드를 옮겨야 한다.

같은 규약을 쓴다 — 남길 본문이 없고 파일이 이미 있으면 덮지 않는다. 파일이 아예 없을
때만 자리표시자 판본을 만든다("파일 없음"과 "추론 없음"은 산출물만 보고 구분되어야 한다).

**(2) `prompt-context.md` 이사**

`MetadataExporter.cs:71`이 쓰는 `rawDirectory`(정본 DDL 옆, `Objects/[객체].[종류]/raw/`)를
`Path.GetDirectoryName(manifestPath)`(객체 디렉터리의 `raw/`)로 바꾼다. 캐시 히트 보존
로직도 함께 옮긴다.

근거는 소유권이다. 이 파일은 **정본이 아니라 회차별 분석 흔적**이므로 `metadata.json`·
`dependency-manifest.json`과 같은 집이 맞다. 이사하면 한 객체의 `raw/`가 두 폴더로
쪼개지는 것이 사라지고, `docs/output-artifacts.md` §11의 3번(「모델이 무엇을 봤나 →
`raw/prompt-context.md`」)이 다시 사실이 된다. 이사 뒤 `Objects/`에는 정본 DDL만 남는다.

**(3) `AnalysisScope` 하드코딩 둘 제거**

`DependencyAnalysisOrchestrator.BuildPersistedSpecification`과
`SpAnalysisOutcome.FromDependencyGraph`가 `AnalysisScope.Direct`를 박아 두고 있다.
request 값을 따라가게 한다. 안 고치면 **OFF 명세서가 자기 수집 범위를 거짓으로 신고한다** —
전이적으로 모은 문서가 머리에 "직접 의존성만"이라고 적게 된다.

**(4) `SaveMigrationPlanAsync`의 손조립 경로**

이번에 CLI의 손조립 출력 경로 둘을 지우므로, `Path.Combine(outputDir, "Procedures",
$"{schema}.{name}", "docs")`를 직접 만드는 이 자리만 홀로 남는다(원장에 이미 등재된 결함).
`OutputPathResolver.ResolveDocsDirectory`로 바꾼다. 특수문자가 든 식별자에서
캐시 조회 경로와 갈라지던 것이 함께 닫힌다.

### 4.4 삭제 목록

| 대상 | 자리 |
| :--- | :--- |
| `SaveRawArtifactsAsync` · `SaveDocumentsAsync` | `ReSet.Cli/Program.cs` |
| `Persistence == NotAttempted` 게이트 블록 2개 | `Program.cs:935`, `:1384` |
| `SpAnalysisOutcome.FromSingleObjectPipeline` | `ReSet.Core/Models/SpAnalysisOutcome.cs` |
| `ExportRawMetadataAsync`(인터페이스 포함) | `IMetadataExporter.cs:17`, `MetadataExporter.cs:254-368` |
| `SaveRawJson`·`SaveRawContext`·`SaveRawFiles` | `appsettings.json:229-231`, `Program.cs:364-366` |

`MetadataExporter.FormatTableSchemaToMarkdown`은 **남긴다** — `InstructionBundleWriter.cs:556`이
Job 단위 `Jobs/[Job]/raw/ddl/`을 만들 때 여전히 쓴다. 그 자리는 이번 통일과 무관하고,
태스크 파일이 링크로 실제 소비한다.

### 4.5 통일 후의 산출물 규칙

「어느 경로가 쓰나」 칸이 사라지고 규칙이 하나가 된다.

| 파일 | 자리 | 언제 |
| :--- | :--- | :--- |
| `Spec.md` · `Thinking.md` | `[객체]/docs/` | 항상(성공 노드) |
| `metadata.json` · `dependency-manifest.json` · `prompt-context.md` | `[객체]/raw/` | 항상 |
| `object_definition.sql` | `Objects/[객체].[종류]/raw/` | 항상 |
| `ddl/procedures\|functions/*.sql` | `Objects/[객체].[종류]/raw/` | `DependencyArtifactMode=PortableBundle`일 때만 |

OFF에서 새로 생기는 것: `dependency-manifest.json`(1노드) · `Objects/` 정본 ·
스위치 없는 `metadata.json`.

## 5. 이 설계가 닫지 **않는** 것

정직하게 적는다. 아래는 이번 작업의 성과로 주장하지 않는다.

- **OFF의 커버리지 폐포 크기는 그대로다.** 매니페스트가 1노드이므로
  `CoverageMapCommand.ClosureOf`가 세는 폐포는 여전히 「분석된 객체 목록」이다.
  이득은 **조용한 폴백이 사라지는 것**이다 — 매니페스트가 없어서 폐포가 줄던 것이,
  이제 「자식을 분석하지 않았으니 폐포에 넣을 산출물이 없다」는 정직한 사실이 된다.
  `SaveRawJson`이 꺼져 `LoadObject`가 전건을 건너뛰던 갈래는 완전히 사라진다.
  원장의 증상 문단은 이 선을 따라 정정한다.
- **`raw/ddl/tables/*.md`가 없어진다.** 읽는 코드가 없고(감사용), 같은 내용이
  `metadata.json`과 `prompt-context.md`에 그대로 있다.
- **`deconstructed_logic.json`과 `chunks/`의 자리는 그대로 둔다.** 둘 다 대상 종류·DB와
  무관하게 `Procedures/` 아래로 가고 `chunks/`는 실행 위치 기준 `output/`을 본다.
  별건이므로 이번에 건드리지 않는다.
- **기존 `output/` 31개 객체의 `prompt-context.md`는 옛 자리에 남는다.** 이행 스크립트를
  두지 않는다 — `output/`은 재생성 가능한 파생물이고, 코퍼스 테스트가 읽는 것은
  Job 단위 파일(`Jobs/POQSettleBatch4/raw/prompt-context.md`)이라 깨지지 않는다.

## 6. 테스트

TDD로 간다 — 실패하는 테스트를 먼저 쓴다.

| 테스트 | 무엇을 지키나 |
| :--- | :--- |
| `DependencyAnalysisOrchestratorTests` OFF 요청 | 자식 노드 0 · 루트에 `GetCodeObjectDetailsAsync`(전이적) 호출 · 매니페스트/`metadata.json`/정본 DDL/`prompt-context.md` 생성 |
| 같은 곳, scope 단언 | OFF 명세서 머리의 범위가 `Transitive`, ON은 `Direct` |
| 신규 회귀 | **캐시 히트 회차가 `Thinking.md`를 파괴하지 않는다** |
| `MetadataExporterTests` 경로 3건 갱신 | `prompt-context.md`가 객체 `raw/`에 있고 `Objects/`에는 **없다**(역단언 포함) |
| `MetadataExporterTests` 4건 삭제 | `ExportRawMetadataAsync` 소멸에 따른 것 |
| `CliArgsTests` | OFF도 오케스트레이터를 탄다 |

합격 기준은 통과 수가 아니라 **실패 0 · 건너뜀 0 · 경고 0**이다.

## 7. 문서 갱신

| 문서 | 무엇을 |
| :--- | :--- |
| `docs/output-artifacts.md` | §3의 「어느 경로가 쓰나」 칸 제거, §4.5의 단일 규칙으로 교체. §3 끝의 두 겹 설명과 「끌 수단이 없는 것 하나」 문단 삭제. §11의 3·4번 재확인 |
| `docs/known-defects.md` | 대상 두 항목을 해소로 옮기고, §5의 「닫지 않는 것」을 그 자리에 정확히 남긴다 |
| `docs/architecture.md` · `AGENTS.md` | 분석 기동 경로가 하나가 된 사실 반영 (`reset-doc-sync` 스킬로) |
| `appsettings.json` | 세 키 삭제 |
