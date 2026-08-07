# 마이그레이션 지시서 분할 및 Step 단위 코드 생성 설계

- 작성일: 2026-08-07
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

`output/Jobs/<job>/agent/MigrationInstructions.md`는 외부 코딩 에이전트(Claude Code, Antigravity CLI, Codex)에 넘기는 유일한 진입점이다. `POQSettleProcDaily4` 기준 **7,816줄 / 283KB**이고, 그 98%인 7,661줄이 `BatchMigrationPlan.md` 본문을 통째로 인라인한 것이다(`MetadataExporter.cs:439`).

에이전트가 지침대로 읽어야 할 입력의 총량은 다음과 같다.

| 입력 | 규모 | 추정 토큰 |
|---|---|---|
| `MigrationInstructions.md` | 7,816줄 / 283KB | ~85k |
| 링크된 `Spec.md` 12개 | 3,663줄 / 234KB | ~110k |
| 링크된 `raw/ddl/*.md` 55개 | 164KB | ~58k |
| **합계** | | **~253k** |

**코드를 한 줄도 쓰기 전에 200k 컨텍스트를 초과한다.** 지시서 2장은 DDL을 "읽어(Read) 확인하십시오", 3장은 Spec을 "참조하십시오"라고 요구하므로, 이 총량은 선택이 아니라 지시된 것이다.

### 결함 1 — 핵심 지침이 문서 맨 끝에 있다

`MigrationInstructions.md`의 「4. 에이전트 핵심 수행 지침」은 7,759줄, 「5. 기술 스택 및 데이터 액세스 경계 규칙」은 7,773줄에서 시작한다. Claude Code의 Read는 기본 2,000줄에서 잘린다. 첫 Read 후 작업을 시작한 에이전트는 다음을 전혀 보지 못한다.

- `todo.md` 체크리스트를 따라 점진적으로 구현하라는 지시 (`MetadataExporter.cs:525`)
- Placeholder 금지 및 SQL 100% 완전 작성 요구 (`:532`)
- `AbstractSettleTasklet` 강제 상속 (`:534`)
- SQL/ORM 경계 규칙 전문 (`DataAccessPolicy.InstructionRules`)

`MetadataExporter.cs:430-432`에는 이미 이런 주석이 있다.

> 계획 본문보다 먼저 온다. 코딩 에이전트는 위에서부터 읽으므로 계획을 소비한 뒤에 경고를 만나면 이미 늦다.

이 통찰은 검증 상태 배너에만 적용되었고, 정작 실행 지침에는 적용되지 않았다.

### 결함 2 — 1회 기동으로 전체를 요구한다

`claude -p "write code using {instructions}"` 한 번에 `todo.md`의 0~17번 항목 전부(공통 인프라 + 12개 Step + 조립 + 검증)를 요구한다. 컨텍스트 압축이 중간에 반드시 일어나고, 압축되면 의사코드·조건절·오류코드 원문이 요약으로 뭉개진다. 그런데 지침 7번은 "축약 없이 100% 완전"을 요구한다. **구조적으로 지킬 수 없는 요구다.**

### 결함 3 — 재시도가 전량 재실행이다

`CodegenWorkflowOrchestrator.cs:70`은 검증 실패 시 새 프로세스로 처음부터 재실행한다. 마지막 Step에서 틀려도 첫 Step부터 다시 쓴다. 게다가 피드백은 문서 맨 끝에 붙으므로(`MetadataExporter.cs:895`), 가장 읽히지 않는 자리에 놓인다.

### 결함 4 — 검증도 같은 규모 문제를 갖는다

`FileMappingService.cs:27`은 Job 경로에서 `BatchMigrationPlan.md` **하나만** 찾는다. 즉 검증 대상은 "계획서 전문 ↔ 생성된 소스 전체"의 **1쌍**이고, L2 AI 검증이 7,661줄 계획서와 프로젝트 전체 소스를 한 번에 받는다. Gap 리포트도 Job 단위로만 나와 어느 Step이 틀렸는지 지목하지 못한다.

### 결함 5 — 아키텍처 테스트가 빈 껍데기다

`agent/tests/ArchitectureTests.cs`는 본문이 전부 주석 처리되어 있다. 지침 8·9번이 "반드시 ~해야 합니다"로 요구하는 것들(`AbstractSettleTasklet` 상속, DB Factory 전량 할당)을 기계적으로 강제하는 장치가 현재 존재하지 않는다.

### 아직 발생하지 않은 결함이다

`CodegenSettings.Enabled`는 `false`이고, 로그 4일치(`output/logs/`)에 `SelfHealing` 기록이 0건이다. 이 경로는 아직 한 번도 실행된 적이 없다. 실패를 관측한 뒤가 아니라 관측하기 전에 고친다.

## 목표와 범위

코딩 에이전트가 한 번에 소화해야 하는 입력을 Step 단위로 줄이고, 실패를 Step 단위로 격리하며, 지침이 읽히는 위치에 놓이도록 한다.

**범위 안**

- `MigrationInstructions.md`를 진입점 인덱스로 축소하고 계획 본문을 `common/`·`steps/`·`verification/`으로 분리
- 진입점 섹션 순서 교정 — 실행 지침과 경계 규칙을 계획 링크보다 앞으로
- 분할 생성이 이미 만든 조각(`SplitGeneration.Skeleton`/`Sections`)을 번들 작성기까지 전달
- `CodegenWorkflowOrchestrator`의 회차 오케스트레이션 (Bootstrap → Step 1..N → Assembly)
- Step 단위 검증 스코프 (`FileMappingService` 명시적 쌍 주입)
- 진행 상태 소유권을 에이전트에서 ReSet으로 이관 (`agent/progress.json`)
- `ArchitectureTests.cs` 템플릿을 실제 규칙으로 채움

**범위 밖**

- 계획서 생성 파이프라인 자체의 변경. 분할 생성·하한 검사·지목 재생성은 `2026-08-06-batch-plan-step-split-design.md`와 `2026-08-07-batch-step-parallel-generation-design.md`가 이미 다뤘고, 이 설계는 그 산출물을 **소비**할 뿐이다
- `appsettings.json`의 `CodegenSettings` 스키마. 회차 전환이 "다른 지시서 경로를 넘긴다"로 끝나므로 인자 템플릿은 손대지 않는다
- L3 인간 검토 흐름, `MaxL2Attempts` 예산 정책
- Step 병렬 생성. 정산 Step은 선행 Step의 산출 테이블에 의존하므로 순차로 둔다
- `DataAccessPolicy`의 경계 규칙 내용. 배치 위치만 바뀌고 문구는 그대로다

## 설계

### 1. 산출물 레이아웃

```
output/Jobs/<job>/agent/
  MigrationInstructions.md      ~250줄   진입점 (지침 → 읽기 계약 → 인덱스)
  todo.md                                사람이 읽는 체크리스트 (progress.json에서 렌더링)
  progress.json                          회차 상태. ReSet만 쓴다
  task-00-bootstrap.md          ~80줄    회차별 작업 지시서
  task-01-<code>.md … task-NN-<code>.md
  task-99-assembly.md
  common/
    00-architecture.md                   아키텍처 개요 + Mermaid 흐름도
    01-step-contract.md                  공통 Tasklet 실행 계약 (오류 추적·Shadow·청크 페이징)
    02-data-access-boundary.md           SQL/ORM 경계 + 기술 스택 (현 5장)
  steps/
    <code>.md                            Step 하나당 파일 하나
  verification/
    integrity-sql.md                     정합성 검증 SQL 세트
  src/AbstractSettleTasklet.cs           확장 (아래 7절)
  tests/ArchitectureTests.cs             실제 규칙 (아래 6절)
```

`task-*.md`를 하위 디렉터리가 아니라 `agent/` **직하**에 두는 것은 임의 선택이 아니다. `ArgumentTemplateResolver.ResolveJobDirectory`는 "지시서의 두 단계 위가 Job 루트"라는 관례에 묶여 있고(`ArgumentTemplateResolver.cs:39-41`), `{jobDir}`은 `--add-dir`에 바인딩된다. `agent/tasks/` 아래에 두면 `{jobDir}`이 `agent/`를 가리켜 에이전트가 `raw/ddl/`과 `Procedures/*/docs/Spec.md`에 접근하지 못한다.

#### 회차당 컨텍스트 예산

| | 현행 | 제안 |
|---|---|---|
| 진입점 + `common/` | 85k (전체 인라인) | ~13k |
| Step 상세 | (위에 포함, 전량) | ~10k (1개) |
| `Spec.md` | ~110k (12개) | ~9k (1개) |
| DDL | ~58k (55개) | ~8k (해당 Step 것만) |
| **입력 합계** | **~253k** | **~40k** |

### 2. 진입점 구성 — 순서가 계약이다

`MigrationInstructions.md`는 다음 순서를 지킨다. 이것이 이 설계의 가장 값싸고 가장 효과가 큰 변경이다.

1. 계획 검증 상태 배너 (현행 유지)
2. **에이전트 핵심 수행 지침** (현 4장 — 맨 끝에서 여기로)
3. **읽기 계약** — 신규
4. 기술 스택 요약 + `common/02-data-access-boundary.md` 링크
5. 파일 인덱스 (`common/`, `steps/`, `verification/`, `raw/ddl/`, `Spec.md`)
6. `todo.md` 링크

3번 읽기 계약은 다음을 명시한다.

> 지금 배정된 `task-*.md`와 그 파일이 링크한 것만 읽으십시오. 다른 Step 파일을 읽지 마십시오. 다른 Step의 코드를 작성하지 마십시오. 이미 존재하는 파일 중 `common/`이 정의한 공통 계약에 해당하는 것은 수정하지 마십시오.

### 3. 계획 조각의 전달 — `PlanLayout`

계획서는 **이미 조각 상태로 생성된 뒤 합쳐진다.** `VerificationPipelineOrchestrator.cs:2465`의 `SplitGeneration`이 그 조각을 들고 있다.

```csharp
private sealed record SplitGeneration(
    string Markdown,                       // 조립된 최종 문서
    AiResult Generation,
    string Skeleton,                       // 개요·흐름도·검증 SQL·공통 규약
    Dictionary<string, string> Sections,   // Step 코드 → 섹션 마크다운
    Dictionary<string, string> FloorViolations);
```

`Skeleton`이 `common/`과 `verification/`에, `Sections`가 `steps/`에 대응한다. 사후에 마크다운을 다시 파싱해 쪼갤 이유가 없다. 예외는 `common/02-data-access-boundary.md` 하나로, 이는 계획서가 아니라 `DataAccessPolicy.InstructionRules`에서 온다(현 지시서 5장).

이를 호출부까지 나르기 위해 파이프라인 결과에 항목 하나를 추가한다.

```csharp
public sealed record PlanLayout(
    string? Skeleton,
    IReadOnlyDictionary<string, string>? Sections,
    IReadOnlyList<BatchStepPlan>? Steps,
    IReadOnlyDictionary<string, string>? FloorViolations);

public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome,
    PlanLayout? Layout = null);
```

기본값 `null`이므로 기존 호출부는 변경되지 않는다. `Layout`이 `null`이면 계획서가 단일 호출 경로로 생성된 경우이며(`VerificationPipelineOrchestrator.cs:1810`), 3단 폴백의 마지막 단계로 떨어진다.

### 4. 경계 결정 — 3단 폴백

**조각의 본문을 그대로 쓰면 안 된다.** `split.Markdown`이 나온 뒤에도 최종 문서는 계속 변형되기 때문이다.

- L1 마크다운 정제 — `specificationMarkdown = l1Result.CleansedMarkdown ?? ...` (`:983`)
- 자가 교정 결과로 교체 (`:624`, `:708`)
- 구제 채택으로 교체 (`:977`, `:1014`, `:1152`)
- 검증 미달 배너 선두 삽입 (`:1020`)

조각을 본문 소스로 쓰면 정제·교정이 반영되지 않은 옛 본문이 `steps/*.md`에 실린다. 최종 `BatchMigrationPlan.md`와 에이전트가 읽는 문서가 조용히 달라지는 것이 이 설계에서 가장 피해야 할 실패다.

따라서 조각은 **본문 소스가 아니라 경계 앵커**로만 쓴다.

| 순위 | 방법 | 발동 조건 |
|---|---|---|
| 1 | `Sections`의 각 섹션 첫 헤딩 라인을 앵커로 최종 문서에서 위치를 찾아 자른다 | 정상 경로. 정제 결과가 보존되고 경계는 조각이 알려준다 |
| 2 | 앵커 실패 시 `Layout.Steps`의 `Code`/`Name`으로 헤딩을 탐색한다 | 정제가 헤딩 자체를 건드린 경우 |
| 3 | 현행 단일 파일 유지 + 콘솔 경고 | `Layout == null` 또는 2순위도 실패 |

3순위로 떨어져도 **2절의 순서 교정과 4·5장 전진 배치는 그대로 적용한다.** 분할이 실패해도 지침이 읽히는 위치에 놓이는 이득은 잃지 않는다.

#### 정규식 휴리스틱을 쓰지 않는 이유

초기 검토에서 `### S\d\d` 접두를 정규식으로 잡는 방안을 고려했으나 폐기했다. `BatchStepPlan.cs:12-15`가 이미 실측으로 반증한 접근이다.

> 목차의 헤딩을 파싱해서는 단계 목록을 얻을 수 없다. 실측한 두 산출물이 이미 반증한다 — 한쪽은 단계를 H3(`### P00.`)에, 다른 쪽은 H4(`#### S00.`)에 뒀고, 후자는 단계가 아닌 헤딩(`#### Phase 1.`)을 같은 레벨에 섞었다. 결정적으로 전자는 `### P20~P23.`으로 4개 단계를 헤딩 하나에 묶었다.

현재 `output/Jobs/` 4개 산출물이 `### S01` 형식으로 일관된 것은 표본의 우연이다. 새 파서를 만들지 않고 `BatchStepPlanParser`를 재사용한다.

#### Skeleton 내부 분할

`Skeleton`은 문자열 하나이므로 `common/` 2개와 `verification/` 1개로 나눌 경계가 따로 필요하다. Step 헤딩과 달리 여기서는 **헤딩 이름이 고정이다.** `MechanicalValidator.RequiredConsolidatedHeaders`(`MechanicalValidator.cs:56-62`)가 L1에서 H2 4개의 존재를 강제하기 때문이다.

| H2 | 대상 파일 |
|---|---|
| `통합 배치 아키텍처 개요` + `Mermaid 기반 통합 흐름도` | `common/00-architecture.md` |
| `단계별 이행 상세 및 의사코드` 중 첫 Step 섹션 이전 (공통 규약) | `common/01-step-contract.md` |
| `통합 데이터 정합성 검증 SQL 세트` | `verification/integrity-sql.md` |

다만 이 강제는 절대적이지 않다. `ValidateConsolidated`는 검증기 자체 오류 시 소프트 패스하고(`MechanicalValidator.cs:132-137`), L1 재시도를 소진하면 `L1Exhausted` 배너가 붙은 채 통과하는 경로도 있다(`VerificationPipelineOrchestrator.cs:1020`).

따라서 H2 중 하나라도 찾지 못하면 **Skeleton 분할을 포기하고 `common/00-architecture.md` 한 파일에 Skeleton 전문을 넣는다.** 이때 `verification/`은 생성하지 않고 진입점 인덱스도 그에 맞춰 링크한다.

**Skeleton 분할 실패는 Step 분할을 막지 않는다.** 둘은 독립적으로 판정한다. Skeleton이 통짜로 남아도 회차당 입력에서 가장 큰 몫인 Step 상세는 여전히 분리되기 때문이다.

#### 부수 효과 — Step별 품질 배너

`FloorViolations`는 Step 코드로 키가 잡혀 있다. 이를 이용해 **하한 미달로 기록된 Step의 `steps/<code>.md` 머리에 직접 경고 배너를 박는다.** 현재는 문서 전체 상단에 배너 하나뿐이라 어느 Step이 부실한지 에이전트가 알 수 없다.

### 5. 회차 오케스트레이션

`CodegenWorkflowOrchestrator`에 상위 루프 `RunStagedWorkflowAsync`를 추가한다. 기존 `RunSelfHealingWorkflowAsync`는 **회차 내부의 재시도 루프로 그대로 재사용**한다.

| 회차 | task 파일 | 산출물 | 검증 |
|---|---|---|---|
| 0 | `task-00-bootstrap.md` | 프로젝트 골격, DI, Worker, `appsettings`, Repository 구현체 | 빌드 성공 + 아키텍처 테스트 (대응 Spec 없음) |
| 1..N | `task-NN-<code>.md` | Tasklet 하나 | 해당 Step만 L1/L2 |
| 99 | `task-99-assembly.md` | Job 파이프라인 조립 | 전체 빌드 + 아키텍처 테스트 |

`N`은 고정값이 아니다. `Layout.Steps`가 정하며 `BatchStepPlanParser.MaxSteps`(40)가 상한이다.

회차 전환은 `_codingEngine.GenerateCodeAsync`에 **다른 지시서 경로를 넘기는 것**으로 끝난다. `ICodingEngine`이 이미 경로를 파라미터로 받으므로(`ICodingEngine.cs`), 인자 템플릿·`ArgumentTemplateResolver`·`ExternalCliCodingEngine`은 변경되지 않는다.

피드백 append 대상도 `task-*.md`가 된다. `AppendFeedbackToInstructionsAsync`는 시그니처 그대로이고 경로만 달라진다. task 파일은 80줄 안팎이므로 피드백이 파일 끝에 붙어도 읽힌다 — 결함 3의 절반은 이것만으로 해소된다.

#### Step 실패 정책

Step이 최대 시도를 소진하고도 검증을 통과하지 못하면 `Failed`로 마킹하고 **다음 회차로 진행한다.** 12개 중 하나가 까다로워도 나머지를 건지고, 사람이 실패한 것만 손보거나 그 Step만 재기동할 수 있다.

기존 `MaxConsecutiveNoArtifactRetries` 캡(`CodegenWorkflowOrchestrator.cs:21`)은 회차 내부에 그대로 유효하다. 산출물 없는 재시도는 명령이 바뀌지 않으므로 반복해도 같은 실패라는 근거가 회차 단위에서도 동일하다.

회차 99에는 `Failed` Step 목록을 전달하며 "이 Step들은 미완성이니 손대지 말고 파이프라인에서 제외하라"고 지시한다. 최종 콘솔 리포트에 성공/실패 Step 수와 실패별 Gap 요약을 낸다.

**미완성 프로젝트가 남는다는 것을 숨기지 않는다.** 최종 빌드가 깨진 상태로 끝날 수 있으며, 리포트가 그 사실을 명시한다.

### 6. Step 단위 검증 스코프

`FileMappingService`에 명시적 쌍 주입 오버로드를 추가한다.

```csharp
List<ValidationResult> ResolveMappings(ValidatorConfig config, IReadOnlyList<ExplicitPair> pairs);
```

Step 회차에서는 오케스트레이터가 `steps/<code>.md`(또는 대응 `Spec.md`)와 생성된 Tasklet 파일의 쌍을 직접 구성해 넘긴다. 기존 무인자 오버로드는 `BatchMigrationPlan.md` 자동 탐색을 그대로 유지해 단일 SP 검증 경로에 영향을 주지 않는다.

이로써 L2 AI 검증의 입력도 Job 전체에서 Step 하나로 줄고, `GapReport`가 Step 좌표를 갖게 되어 피드백이 해당 `task-*.md`로 정확히 돌아간다.

### 7. 공통 계약 소유권

ReSet이 **인터페이스를 소유하고, 조립은 회차 0의 에이전트가 한다.**

`AbstractSettleTasklet.cs`는 이미 `ISettleStep`, `AbstractSettleTasklet`, `SettleContext`, `StepResult`, `IDbConnectionFactory`, `ICheckpointRepository`를 고정하고 있다. 여기에 다음을 추가한다.

- Repository/DAO 인터페이스 규약
- Step 등록 규약 (`ISettleStep` 구현체를 DI에 등록하는 방식과 실행 순서 선언)

구현체·DI 조립·`appsettings.json`은 회차 0의 에이전트가 만든다. 계약은 결정론적으로 고정하되 보일러플레이트는 에이전트의 유연성에 남긴다. C#/Java 두 타깃의 전체 템플릿을 ReSet이 유지보수하는 부담을 지지 않기 위한 선택이다.

### 8. 아키텍처 테스트

`agent/tests/ArchitectureTests.cs` 템플릿을 실제 규칙으로 채운다. 지침 8·9번의 "반드시"를 기계적으로 강제하는 것이 목적이다.

| # | 규칙 | 근거 |
|---|---|---|
| 1 | `ISettleStep` 구현체는 모두 `AbstractSettleTasklet`을 상속한다 | 지침 9번 |
| 2 | Tasklet에서 DB 커넥션을 직접 생성하지 않는다 (`SettleContext` 팩토리만) | 경계 규칙 조항 1 |
| 3 | Domain 네임스페이스는 Infrastructure에 의존하지 않는다 | 지침 4번 |
| 4 | 모든 Tasklet의 `StepName`/`SourceProcName`이 비어 있지 않다 | 검증기 매핑 전제 |
| 5 | `SettleContext`의 모든 `IDbConnectionFactory` 속성이 DI에서 할당된다 | 지침 8번 |

C#은 NetArchTest, Java는 ArchUnit으로 같은 규칙을 표현한다.

회차 0 시점에는 Tasklet이 아직 없으므로 규칙 1·2·4는 대상 0건으로 자동 통과한다. **이를 "통과"로 보고하지 않는다** — 회차 0에서 실질적으로 검사되는 것은 규칙 3·5뿐임을 리포트에 명시한다. 대상이 없어서 통과한 것과 검사해서 통과한 것을 같은 표기로 내면, 결함 5가 지금 만들어낸 것과 같은 착시(빈 테스트를 방어로 착각)를 반복하게 된다.

#### 아키텍처 테스트가 잡지 못하는 것

경계 규칙 조항 1의 후반부 — **"EF Core를 쓰면 반드시 `RunBusinessSteps`가 받은 `conn`/`tran`에 참여시킬 것"** — 은 NetArchTest/ArchUnit으로 검증할 수 없다. 메서드 호출 그래프 분석이 필요하다.

이 항목은 **L1 정적 검증 플러그인**(`plugin.ValidateStaticAsync`, `CodeVerificationOrchestrator.cs:120`)의 규칙으로 배치한다. 아키텍처 테스트가 이를 잡아준다고 착각해서는 안 된다. 위반 시 `CSharpReflectionRunner`의 Rollback 격리가 깨져 정합성 대조 결과가 오염되는데, 이는 조용히 잘못된 검증 통과로 이어지므로 어느 계층이 이를 책임지는지 명확해야 한다.

### 9. 진행 상태 소유권

```json
{
  "steps": [
    { "code": "S01", "taskFile": "task-01-S01.md", "status": "Passed",
      "attempts": 1, "lastGapSummary": null }
  ]
}
```

`status`는 `Pending | InProgress | Passed | Failed`이다.

**ReSet이 검증 결과를 근거로만 갱신한다.** 현재는 에이전트가 `todo.md`의 `[x]`를 직접 갱신하도록 지시하는데(`MetadataExporter.cs:525`), 이는 에이전트가 지키지 않으면 그만이고 자기 보고를 검증 없이 신뢰하는 구조다. `todo.md`는 `progress.json`에서 렌더링되는 사람용 표시로 격하한다.

## 오류 처리

| 상황 | 처리 |
|---|---|
| `Layout == null` (단일 호출로 생성된 계획서) | 3순위 폴백. 단일 파일 유지 + 콘솔 경고. 순서 교정은 적용 |
| 앵커 탐색 실패 | 2순위(목차 JSON) → 실패 시 3순위 |
| 일부 Step만 앵커 탐색 실패 | 전체를 3순위로 떨어뜨린다. **부분 분할은 하지 않는다** — 빈 `steps/*.md`가 조용히 생기는 것이 최악이다 |
| Skeleton의 H2 4개 중 일부를 못 찾음 | Skeleton 분할만 포기하고 `common/00-architecture.md`에 전문을 넣는다. Step 분할은 그대로 진행 |
| 회차 0 실패 | 즉시 중단. 공통 계약이 없으면 이후 회차가 성립하지 않는다 |
| Step 회차 실패 | `Failed` 마킹 후 다음 회차 |
| 회차 99 실패 | 리포트에 기록. 생성된 Step 코드는 보존 |
| 전 회차 무산출물 | 기존 `MaxConsecutiveNoArtifactRetries` 캡이 회차 내부에서 발동 |

## 테스트 계획

**`PlanLayout` 전달**
- 분할 생성 성공 시 `ConsolidatedPipelineResult.Layout`이 `Skeleton`/`Sections`/`Steps`/`FloorViolations`를 모두 담는다
- 단일 호출 폴백 시 `Layout`이 `null`이다
- 구제 채택으로 문서가 교체되어도 `Layout`이 채택된 시도의 것과 일치한다

**경계 결정**
- 1순위: 앵커로 잘라낸 각 `steps/*.md`의 본문이 최종 `BatchMigrationPlan.md`의 해당 구간과 바이트 단위로 같다 (조각 본문이 아니라 최종 문서에서 온 것임을 보장)
- 2순위: 헤딩이 정제로 변형되어 앵커가 실패하면 목차 `Code`/`Name`으로 복구한다
- 3순위: 일부 Step만 실패해도 전체가 단일 파일로 떨어지고 경고가 나온다
- `FloorViolations`에 기록된 Step의 파일에만 배너가 붙는다
- Skeleton의 H2 하나가 없어도 Step 분할은 정상 수행된다 (두 판정의 독립성)

**번들 작성**
- 진입점에서 실행 지침의 위치가 계획 링크보다 **앞**이다
- `task-*.md`가 `agent/` 직하에 놓여 `ResolveJobDirectory`가 Job 루트를 반환한다
- 생성된 모든 상대 링크가 실제 존재하는 파일을 가리킨다
- 3순위 폴백에서도 지침 순서 교정이 적용된다

**회차 오케스트레이션**
- Step 실패 시 `Failed` 마킹 후 다음 회차로 진행한다
- 회차 0 실패 시 즉시 중단한다
- 전량 실패해도 리포트가 산출된다
- 회차 99에 `Failed` Step 목록이 전달된다
- `progress.json`이 에이전트의 `todo.md` 편집과 무관하게 검증 결과만 반영한다

**검증 스코프**
- 명시적 쌍 주입 시 해당 Step만 검증한다
- 무인자 오버로드의 기존 동작이 바뀌지 않는다

## 변경 지점

**신규**

| 파일 | 책임 |
|---|---|
| `ReSet.Core/Models/PlanLayout.cs` | 조각 전달 계약 |
| `ReSet.Core/Services/PlanBoundaryResolver.cs` | 3단 폴백 경계 결정 |
| `ReSet.Core/Services/InstructionBundleWriter.cs` | 진입점·`common/`·`steps/`·`verification/`·`task-*.md` 작성 |
| `ReSet.Validator.Core/Models/CodegenStagePlan.cs` | 회차 목록과 검증 쌍 |

**수정**

| 파일 | 변경 |
|---|---|
| `ConsolidatedPipelineResult.cs` | `Layout` 추가 (기본값 `null`) |
| `VerificationPipelineOrchestrator.cs` | `SplitGeneration` → `PlanLayout` 반환 |
| `MetadataExporter.cs` | `ExportConsolidatedMigrationInstructionsAsync`를 `InstructionBundleWriter` 호출로 축소 (현재 한 메서드 180줄) |
| `CodegenWorkflowOrchestrator.cs` | `RunStagedWorkflowAsync` 추가, 기존 루프는 회차 내부로 재사용 |
| `FileMappingService.cs` | 명시적 쌍 주입 오버로드 |
| `DataAccessPolicy.cs` | 계약 템플릿에 Repository·Step 등록 규약 추가 |
| `Program.cs` (`:895`, `:1414`) | `RunCodegenEngineAsync` 인자 |

**무변경**

`appsettings.json`의 `CodegenSettings`, `ArgumentTemplateResolver`, `ICodingEngine`, `ExternalCliCodingEngine`, `AppendFeedbackToInstructionsAsync` 시그니처, `DataAccessPolicy.InstructionRules`의 문구, `BatchStepPlanParser`.

## 관련 문서

- `2026-08-06-batch-plan-step-split-design.md` — 이 설계가 소비하는 조각을 만드는 쪽
- `2026-08-07-batch-step-parallel-generation-design.md` — 조각 생성의 병렬화
- `2026-08-07-codegen-headless-design.md` — 무인 기동 경로
