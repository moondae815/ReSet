# 통합 배치 마이그레이션 설계 경로 보강 설계

- 작성일: 2026-08-01
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

TUI 메뉴 2번 "통합 배치 마이그레이션 설계 (Batch Design)" 경로를 코드 수준에서 검증한 결과, 지침(프롬프트) 5건과 플로우(코드) 4건의 결함이 확인되었다.

흐름 자체는 설계대로 동작한다. 명세서 선택 → 브레인스토밍 → 목차 설계 → 최종 생성 → L1 기계검증 → L2 Critic → L3 인간승인 → 지시서 생성 → 코딩 에이전트로 이어지며, L2 재시도 시 `feedbackHistory.Clear()`로 컨텍스트 오염을 막고(`VerificationPipelineOrchestrator.cs:1595`), 배치 모드에서 L3를 우회하는 처리(`:1631`)도 AGENTS.md 규칙에 부합한다. 문제는 개별 지점에 있다.

### 지침 결함

AGENTS.md 범주 7이 규정한 **통합 배치 도메인 5대 핵심 제약**(NOLOCK / INSERT-only / Chunk Key / 멱등성 / 예외 처리)은 생성 프롬프트(`AiService.cs:1871~`)에 모두 존재한다. 구멍은 Critic 쪽 그물과 규칙-예시 충돌이다.

| # | 결함 | 위치 |
|---|---|---|
| ⑤ | Critic이 NOLOCK 금지와 INSERT-only 롤백을 검사하지 않음. 5대 제약 중 1·2번이 L2를 그대로 통과한다 | `AiService.cs:2005-2009` |
| ⑥ | rule 4(c)의 복원 예시가 선행 `DELETE`를 빠뜨려 중복 삽입 패턴을 가르친다. 같은 프롬프트의 Few-Shot(`:1930-1938`)은 `DELETE` → `INSERT` 순서라 규칙과 예시가 상충한다 | `AiService.cs:1884` |
| ⑦ | AGENTS.md가 명시한 `GOTO` 예외 처리 차단 규칙이 `src/` 전체에 0건 | 부재 |
| ⑧ | 청킹 `WHILE` 루프의 `BEGIN TRAN`/`COMMIT TRAN` 경계가 규칙이 아닌 Few-Shot 예시로만 존재 | `AiService.cs:1920,1925` |
| ⑨ | Anti-Shortcut(UNION/JOIN 축약 금지)이 Critic 기준에 명시되지 않음. 재시도 피드백 문구(`:1599`)가 부분적으로만 보완한다 | `AiService.cs:1997-1998` |

⑥이 가장 무겁다. 프롬프트가 스스로 모순된 지시를 내리고 있고, 모델은 대개 규칙 옆 인라인 예시를 더 강하게 따른다.

### 플로우 결함

| # | 결함 | 위치 |
|---|---|---|
| ① | `Brainstorming.md`·`PlanStructure.md`가 `Directory.GetCurrentDirectory()/"output"`에 하드코딩되어, 설정된 출력 경로를 쓰는 `BatchMigrationPlan.md`와 산출물이 두 곳으로 쪼개진다 | `VerificationPipelineOrchestrator.cs:1520` |
| ② | 배치 스텝 후보를 `Spec.md` 전체 재귀 수집으로 뽑아 33개 중 SP가 14개뿐이다. UDF 17개와 `Jobs/**` 검증 중간산출물 2개가 스텝으로 선택 가능하다 | `Program.cs:977` |
| ③ | `metadata.json`이 없는 객체를 `if (File.Exists(...))`가 조용히 건너뛴다. 파일이 있는데 파싱이 깨지면 경고를 내면서, 파일 자체가 없으면 침묵하는 비대칭이다 | `Program.cs:1193-1211` |
| ④ | L2 리뷰가 예외로 실패해도 `l2Result == null` 조건에 걸려 "검증 성공"으로 표시된다. 리뷰를 못 돌린 것과 통과한 것이 구분되지 않는다 | `VerificationPipelineOrchestrator.cs:1583-1586,1615` |

③의 파급은 조사 중 처음 보고보다 넓은 것으로 드러났다. `spDefs`의 주 용도는 `sp.Dependencies`로 **테이블 스키마 컨텍스트 번들**(`raw/ddl/*.md`)을 만드는 것인데(`MetadataExporter.cs:390-424`), `metadata.json`은 단일 SP 경로에서 `OutputSettings:SaveRawJson` 플래그로 게이팅된다(`MetadataExporter.cs:250`). 즉 이 값이 `false`면 재귀 객체뿐 아니라 **모든 SP**의 스키마 컨텍스트가 지시서에서 빠진다. 현재 `appsettings.json`이 `true`라 드러나지 않았을 뿐이다.

재귀로만 분석된 객체(`UP_Util_Settle_Summary_AcqManual`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`)는 `raw/`에 `dependency-manifest.json`만 있어 항상 이 구멍에 빠진다. `ExportCodeObjectArtifactsAsync`가 표준 DDL·`prompt-context.md`·매니페스트만 쓰고 `metadata.json`을 쓰지 않기 때문이다(`MetadataExporter.cs:34-79`).

## 목표와 범위

### 목표

9건을 모두 해소한다. 생성 프롬프트가 모순 없는 지시를 내리고, Critic이 5대 제약 전부를 검사하며, 배치 흐름이 산출물 상태에 대해 사실을 말하게 한다.

### 범위 밖

- **Critic 점수 축 변경.** 5개 축(`ScoreAccuracy`/`Crud`/`Interface`/`Readability`/`Exception`)은 `ReviewResult` 모델과 L2 임계값에 묶여 있다. 기준 4·1 안에 검사 항목만 추가한다.
- **`defaultSpOrder` 하드코딩 목록**(`Program.cs:991-1005`). 업무 특화 기본값이며 이번 결함과 무관하다.
- **L1 `ValidateConsolidated`의 검사 범위 확대.** 헤더·Mermaid 검증을 유지한다. SQL 의사코드 내용 검사는 정적 파서 없이 오탐이 많아 L2의 몫으로 둔다.
- **기존 산출물 소급 생성.** ③의 근본 수정은 앞으로 생성될 산출물에만 적용된다. 원본 `SpDefinition`이 디스크에 없어 만들어낼 수 없고, 만들어낸다면 그것이 환각이다.
- **프롬프트 규칙의 데이터화.** 규칙을 설정/리소스로 외부화하고 AGENTS.md와 대조하는 검증기는 현재 필요보다 크다. 프롬프트는 문맥과 순서가 의미를 가져 데이터화하면 오히려 관리가 어려워진다.
- **LLM 응답 품질 검증.** 프롬프트에 규칙이 들어갔는지는 테스트하지만 모델이 그 규칙을 지키는지는 단위 테스트 범위 밖이다.

## 결정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 작업 구조 | 지침 트랙과 플로우 트랙으로 분리, 트랙별 독립 커밋 | 파일이 겹치지 않아 독립 검증이 가능하다 |
| ⑦ GOTO | 독립 조항 대신 rule 6-1에 한 절로 편입 | 원본 SP에 `GOTO` 0건. 예방 규칙이므로 프롬프트 지면을 아낀다 |
| ⑧ 트랜잭션 경계 | `8-1 [Chunk Transaction Boundary]`로 신설 | 고유 라벨이 있어야 테스트로 잠긴다. `BEGIN TRAN`은 Few-Shot에도 있어 단서로 무효하다 |
| ② 후보 필터 | 프로시저 산출물만 노출 (하드 필터) | 배치 스텝은 프로시저이지 함수가 아니다 |
| ③ 메타데이터 | 재귀 경로도 `metadata.json` 내보내기 + 부재 시 경고 | 지시서의 주 payload인 `Dependencies`는 `metadata.json`에서만 온다 |
| ③ 게이팅 | 재귀 경로는 `SaveRawJson` 게이팅 없이 쓴다 | 해당 메서드가 이미 `prompt-context.md`를 무조건 쓴다. 내부 일관성을 따른다 |
| ④ L2 실패 | 별도 알림 + 문서에 `[!NOTE]` 배너 | 나중에 문서만 보는 사람도 검증 상태를 알 수 있어야 한다 |
| ① 출력 경로 | `RunConsolidatedPipelineAsync`에 `outputRoot` 필수 파라미터 추가 | 같은 클래스의 재귀 경로가 이미 `request.OutputDirectory`를 받아 쓴다 |

## 설계

### 1. 신규 컴포넌트: `BatchStepCatalog`

②③이 요구하는 순수 로직만 TUI 루프에서 꺼낸다. 위치는 `src/ReSet.Cli/`로, 이미 테스트되고 있는 `CliArgs`·`SessionManager`·`CodingEngineFactory`와 같은 계층이다.

```csharp
public sealed class BatchStepCatalog
{
    public static IReadOnlyList<string> FindStepCandidates(string outputRoot);

    public static Task<BatchStepLoadResult> LoadDefinitionsAsync(
        string outputRoot,
        IEnumerable<string> specRelativePaths,
        CancellationToken cancellationToken);
}

public sealed record BatchStepLoadResult(
    IReadOnlyList<SpDefinition> Definitions,
    IReadOnlyList<string> MissingMetadata,
    IReadOnlyList<string> FailedToParse);
```

**스텝 자격 판정**은 `outputRoot` 기준 상대 경로의 형태로 한다.

```
Procedures/<객체>/docs/Spec.md
External/<DB>/Procedures/<객체>/docs/Spec.md
```

`Functions/`, `External/<DB>/Functions/`, `Jobs/**`는 탈락한다. 파일을 열지 않고 판정하는 이유는 `OutputPathResolver.ResolveObjectDirectory`가 객체 유형을 디렉터리 이름으로 인코딩하고 있어, 경로가 곧 유형이기 때문이다.

판정 전에 경로 구분자를 `/`로 정규화한다. 반환하는 상대 경로는 플랫폼 구분자를 그대로 유지해 `Path.Combine`으로 바로 쓸 수 있게 한다. 기존 `Program.cs:1010`이 같은 방식으로 `Replace('\\', '/')` 후 비교한다.

**`LoadDefinitionsAsync`는 입력 순서를 보존한다.** `selectedFiles`의 순서가 곧 배치 스텝 실행 순서이고 그대로 지시서에 반영되므로, 반환하는 `Definitions`는 입력 경로 순서와 일치해야 한다. 복원에 실패한 항목은 목록에서 빠지되 나머지의 상대 순서는 유지된다.

**`MissingMetadata`를 분리 반환하는 이유**는 ③의 근본 수정이 앞으로의 산출물에만 적용되기 때문이다. 이미 디스크에 있는 산출물과 `SaveRawJson=false` 설정은 여전히 구멍을 남기므로, Program.cs가 "이 SP들은 스키마 컨텍스트 없이 지시서에 들어갑니다"라고 경고할 수 있어야 한다.

### 2. Program.cs 변경

메뉴 2번 분기에서 두 곳만 교체한다. TUI 선택 루프 구조는 건드리지 않는다.

- `Directory.GetFiles(outputDir, "Spec.md", AllDirectories)`(`:977`) → `BatchStepCatalog.FindStepCandidates(outputDir)`
- `spDefs` 복원 루프(`:1190-1212`) → `BatchStepCatalog.LoadDefinitionsAsync(...)` 호출 + 경고 렌더링

후보 0건일 때의 경고 문구를 "분석된 **프로시저** 명세서가 없습니다"로 바꿔 필터가 걸렸을 때 원인이 드러나게 한다.

`RunConsolidatedPipelineAsync` 호출부(`:1146`)에 `outputDir`을 전달한다.

### 3. 프롬프트 변경

`AiService.cs`의 두 시스템 프롬프트만 수정한다.

**생성 프롬프트 (`GenerateConsolidatedBatchPlanAsync`)**

| 결함 | 위치 | 변경 |
|---|---|---|
| ⑥ | rule 4 (c) | 복원 예시를 선행 `DELETE` 후 `INSERT`로 교정하고 중복 방지 이유를 명시 |
| ⑦ | rule 6-1 끝 | 구조적 `TRY...CATCH`만 사용하고 레거시 `GOTO` 기반 예외 분기를 금지하는 절 추가 |
| ⑧ | rule 8 뒤 | `8-1 [Chunk Transaction Boundary]` 신설. 청킹 `WHILE` 루프의 각 반복은 `BEGIN TRAN`/`COMMIT TRAN`으로 감싼다 |

**Critic 프롬프트 (`ReviewConsolidatedPlanAsync`)**

| 결함 | 기준 | 추가 항목 |
|---|---|---|
| ⑤ | 4 | `NOLOCK` 잔존 여부 검사, 강한 감점 |
| ⑤ | 4 | INSERT-only 스텝이 Shadow 대신 `ROLLBACK TRAN`/`DELETE WHERE [ChunkKey]`를 쓰는지 |
| ⑥ | 4 | Shadow 복원이 대상 범위를 먼저 `DELETE`한 뒤 복원하는지 |
| ⑨ | 1 | `UNION`/`UNION ALL`/다중 JOIN이 축약 없이 보존됐는지, 원천 테이블·집계식 누락 시 감점 |

⑥을 생성과 검증 양쪽에 넣는 것은 중복이 아니다. 생성 규칙은 올바른 패턴을 가르치고 Critic 항목은 잘못 나온 결과를 잡는다. 현재는 둘 다 비어 있다.

**문법 제약.** 시스템 프롬프트는 `$@"..."` 보간 축자 문자열이다. 큰따옴표는 `""`로 이중화하고, 중괄호는 `{{}}`로 이스케이프한다(AGENTS.md 범주 7). 대괄호는 그대로 쓸 수 있다.

### 4. 재귀 경로의 `metadata.json` 내보내기

`MetadataExporter.ExportCodeObjectArtifactsAsync`가 `<객체>/raw/metadata.json`을 추가로 쓴다. `definition`을 이미 파라미터로 받고 있어 직렬화만 추가하면 되며, 단일 SP 경로와 같은 `JsonSerializerOptions { WriteIndented = true }`를 쓴다. 기존 try/catch 안에 배치해 디스크 오류가 분석 파이프라인을 중단시키지 않게 한다.

### 5. L2 미수행과 통과의 분리

`reviewSuccess`를 판정 조건에 반영해 "리뷰 예외"와 "리뷰 통과"를 분리한다(`:1615`).

- 콘솔: `NotifyValidationSuccess` 대신 미수행 전용 알림
- 문서: 계획서 상단에 `> [!NOTE]` 배너로 L2 미수행 사실과 사유 삽입

기존 품질 불합격 배너는 `[!CAUTION]`(`:1608`)이므로 표기로 구분된다. 소프트 페일 정책은 유지한다. 리뷰 실패가 파이프라인을 중단시키지 않는다.

### 6. 오류 처리

| 지점 | 실패 시 |
|---|---|
| `FindStepCandidates` 디렉터리 열거 IO 오류 | 경고 로그 후 수집분 반환. 예외 전파 없음 |
| `LoadDefinitionsAsync` 파일 단위 실패 | 해당 항목만 `FailedToParse`로 격리, 나머지 계속 |
| `metadata.json` 쓰기 실패 | 기존 try/catch(`MetadataExporter.cs:85`) 안에서 소프트 페일 |
| L2 리뷰 예외 | 계속 진행하되 ④로 사실 표시 |

**취소는 예외다.** `OperationCanceledException`은 세 지점 모두 재던진다. `ExportCodeObjectArtifactsAsync`가 이미 `:81`에서 그렇게 하며, 취소를 소프트 페일로 삼키면 Ctrl+C가 동작하지 않는다.

**Fail-fast 하는 곳 하나.** `RunConsolidatedPipelineAsync`의 `outputRoot`가 비어 있으면 `ArgumentException`을 던진다. 사용자 입력이 아니라 호출부 결함이고, `OutputPathResolver` 생성자가 같은 상황에서 이미 던진다. 조용히 CWD로 폴백하면 ①이 되살아난다.

## 테스트 계획

전부 TDD로 진행하며 항목마다 RED를 확인한다.

### 지침 트랙 (`AiServiceTests.cs` 확장)

| 결함 | 테스트 | 검증 수단 | RED 근거 |
|---|---|---|---|
| ⑥ | `..._ShadowRestoreDeletesBeforeInsert` | `result.SystemPrompt` | 옛 예시 문자열이 현존 |
| ⑦ | `..._ForbidsGotoErrorBranching` | `result.SystemPrompt` | `GOTO` 0건 |
| ⑧ | `..._ContainsChunkTransactionBoundaryRule` | `result.SystemPrompt` | 라벨 부재 |
| ⑤ | `ReviewConsolidated..._ChecksNolockAndInsertOnlyRollback` | `mockHandler.LastRequestBody` | Critic 프롬프트에 `NOLOCK` 0건 |
| ⑨ | `ReviewConsolidated..._ChecksUnionAndJoinPreservation` | `mockHandler.LastRequestBody` | Critic 프롬프트에 `UNION` 0건 |

⑥은 assert를 짝으로 쓴다. 새 문구 `Contains` + 옛 깨진 예시 `DoesNotContain`. 진짜 회귀 가드는 후자다. 저장소가 이미 프롬프트에 `Assert.DoesNotContain`을 쓴다(`AiServiceTests.cs:132-134`).

Critic 프롬프트는 `ReviewResult`를 반환해 `SystemPrompt` 필드가 없다. 대신 테스트의 `MockHttpMessageHandler`가 요청 본문을 `LastRequestBody`로 캡처하므로(`AiServiceTests.cs:661`) 모델 변경 없이 검증할 수 있다. 본문은 JSON 이스케이프되어 있어 따옴표·개행이 든 문자열은 매칭되지 않는다. `NOLOCK`, `INSERT-only`, `UNION`처럼 이스케이프 영향이 없는 단서만 assert한다.

### 플로우 트랙

| 파일 | 테스트 |
|---|---|
| `BatchStepCatalogTests` (신규) | `Procedures/`·`External/<DB>/Procedures/` 포함 / `Functions/`·`External/<DB>/Functions/`·`Jobs/**` 제외 / `MissingMetadata` 분리 / 깨진 JSON은 `FailedToParse`로 분리 / `Definitions`가 입력 순서를 보존 |
| `MetadataExporterTests` | `ExportCodeObjectArtifactsAsync`가 `<객체>/raw/metadata.json` 생성 |
| `VerificationPipelineOrchestratorTests` | ① `Brainstorming.md`·`PlanStructure.md`가 주입된 `outputRoot` 아래 생성되고 **CWD에는 생성되지 않음** / ④ L2 예외 시 `[!NOTE]` 배너 삽입 + 성공 알림 미발생 |

①의 테스트는 CWD에 아무것도 생기지 않는지까지 확인해야 한다. 파일 생성 여부만 보면 CWD 폴백이 살아 있어도 통과한다.

### 회귀 가드 (기존, 수정 없음)

`GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints`가 `[NOLOCK Prohibition]`·`[INSERT-only Rollback]`·`[Chunk Key Validation]`·`[Output Parameters Interface]` 라벨을 잠그고 있다. 이번 수정이 기존 라벨을 바꾸지 않으므로 그대로 통과해야 한다.

## 검증 시나리오

1. `dotnet build` 경고 0 · 오류 0
2. `dotnet test` 전량 통과 (현재 313 + 신규 약 13)
3. `AGENTS.md` 체크리스트의 단위 테스트 개수를 실제 실행 결과로 갱신
4. 트랙별 독립 커밋 2개

## 알려진 리스크 (이번 범위에서 수정하지 않음)

- **`SaveRawJson=false` 설정의 넓은 구멍.** 단일 SP 경로의 `metadata.json` 게이팅은 그대로 둔다. 배치 흐름은 ③의 경고로 이 상태를 드러내지만, 설정을 끈 사용자는 여전히 스키마 컨텍스트 없는 지시서를 받는다. 게이팅 자체를 재검토하려면 `SaveRawJson`의 원래 의도(디스크 절약)와 지시서 품질 사이의 트레이드오프를 따로 다뤄야 한다.
- **프롬프트 규칙과 AGENTS.md의 드리프트.** ⑦처럼 문서에만 있고 코드에 없는 규칙은 테스트로 잡히지 않는다. 이번엔 발견된 1건을 메우지만 구조적 방지책은 두지 않는다.
- **`Program.cs`의 크기.** 1,861줄이며 메뉴 2번 분기만 300줄이다. 이번엔 ②③이 요구하는 최소한만 추출하고 나머지 TUI 로직은 그대로 둔다.
