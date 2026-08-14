# output/ 산출물 명세

`output/` 아래에 생기는 파일을 **누가·언제·어디에·무엇을·어떻게·왜** 만드는지 정리한 문서다.
모든 항목은 소스 코드의 실제 쓰기 지점에서 추출했다.

- 작성일: 2026-08-14
- 기준: `agents-md-restructure` 브랜치 `ba07b02`
- 줄 번호는 작성 시점의 것이다. 시간이 지나면 클래스·멤버 이름으로 찾는 편이 정확하다.
- `output/` 전체는 `.gitignore:8`(`[Oo]utput/`)로 추적 제외된다. 여기 있는 것은 전부 **재생성 가능한
  파생물**이며, 사람이 직접 편집해 보존할 자리가 아니다.

---

## 1. 큰 그림

산출물은 생성 시점이 다른 네 계층으로 나뉜다. 뒤 계층은 앞 계층을 입력으로 받는다.

```
① 객체 단위 역공학        Procedures/  Functions/  Objects/  External/
        ↓ Spec.md를 읽어
② 통합 Job 계획 수립      Jobs/[Job]/raw/  Jobs/[Job]/docs/
        ↓ 계획서를 잘라
③ 지시서 번들 생성        Jobs/[Job]/agent/
        ↓ 외부 코딩 에이전트가 소비
④ 코드 생성과 검증        Jobs/[Job]/src/  Jobs/[Job]/validation/

그 외    logs/  cleansing/  .sp_cache_index.json  (임의 경로) offline_snapshot.json
```

두 개의 CLI가 이 계층을 나눠 갖는다. **`ReSet.Cli`가 ①~③과 ④의 기동을 담당**하고,
**`ReSet.Validator.Cli`가 ④의 데이터 정합성 대조**를 담당한다.

경로 계산의 단일 창구는 `OutputPathResolver`다. 객체 종류(`Procedure`/`Function`/`Unresolved`)와
DB 소속(분석 루트 DB인지 아닌지)만으로 디렉터리가 결정되며, 루트 DB가 아니면 모든 경로 앞에
`External/[DB]/`가 붙는다. 경로에 쓸 수 없는 문자·`.`·`%`는 `%XX`로 인코딩된다
(`OutputPathResolver.EncodePathSegment`).

---

## 2. ① 객체 단위 역공학 산출물

**언제** — 메인 메뉴에서 SP 하나를 골라 분석하거나, 그 SP가 참조하는 객체를 재귀 분석할 때.
의존성 그래프의 노드 하나가 성공할 때마다 그 노드 몫이 기록된다.

**어디에** — 루트 DB는 `output/Procedures/[Schema].[이름]/`, UDF는 `output/Functions/[Schema].[이름]/`.
다른 DB의 객체는 `output/External/[DB]/` 아래 같은 모양으로 격리된다.
객체 종류를 끝내 판정하지 못하면 `Unresolved/`로 떨어진다(`OutputPathResolver.ResolveObjectDirectory`).

### docs/ — 사람이 읽는 결과물

| 파일 | 누가 | 왜 |
|---|---|---|
| `Spec.md` | 재귀 분석은 `DependencyAnalysisOrchestrator`(`:446`), 단일 SP는 `Program.SaveSpecAsync`(`Program.cs:1955`) | 최종 비즈니스 명세서. 뒤따르는 모든 계층의 **입력 원본**이다 |
| `BatchMigrationPlan.md` | `Program.cs:2008` | SP 하나짜리 배치 전환 계획서. **L1도 L2도 거치지 않으므로** `FormatUnverifiedDocument`로 감싸 "검증 없음"을 헤더에 명시한다 |
| `Thinking.md` | 재귀는 `DependencyAnalysisOrchestrator`(`:568`), 단일 SP는 `Program.cs:1977` | 채택된 시도가 무엇을 사고했는지 되짚기 위한 추론 로그. **본문이 비어도 반드시 쓴다** — 파일이 없다는 것과 추론이 없었다는 것을 산출물만 보고 구분할 수 있어야 하기 때문 |

`Spec.md`에는 `VerificationDocumentFormatter.FormatVerifiedDocument`가 검증 결과(종합 신뢰도, 축별
점수, `VerificationOutcome`, 검증 커버리지)를 YAML 헤더로 얹는다. 이 헤더는 나중에
`SpecHeaderReader`가 되읽는다.

### raw/ — 재현과 감사를 위한 원본

| 파일 | 누가 | 왜 |
|---|---|---|
| `metadata.json` | `MetadataExporter.ExportCodeObjectArtifactsAsync`(`:94`), 레거시 경로는 `ExportRawMetadataAsync`(`:275`) | 의존성이 전부 덤프된 `SpDefinition` 직렬화본. **지시서 번들이 참조 테이블 스키마를 만들 때 쓰는 원천**이라 매니페스트와 같은 디렉터리에 둔다 |
| `dependency-manifest.json` | `MetadataExporter`(`:87`), 경로는 `OutputPathResolver.ResolveManifestPath` | 의존 객체 식별자와 각 객체의 산출물 경로를 잇는 매니페스트. 크로스 DB 분석에서 어느 산출물이 어디로 갔는지 되짚는 유일한 색인 |
| `prompt-context.md` | `MetadataExporter`(`:72`, `:300`), 통합 계획은 `Program.cs:890` | AI에 **실제로 주입된 원문**(System/User 프롬프트). 결과가 이상할 때 모델을 탓하기 전에 입력부터 확인하라고 남긴다 |
| `deconstructed_logic.json` | `VerificationPipelineOrchestrator`(`:888`) | **Ollama 전용**. 로컬 LLM은 1단계 구조화 추론을 따로 돌리므로 그 중간 산출을 백업해 둔다 |
| `chunks/chunk_N.json` | `AiService`(`:1011`) | 로컬 LLM의 AST 기반 분할 생성(`EnableLocalChunking`) 시 조각별 응답 캐시. 중단·재시도에서 이미 끝난 조각을 다시 태우지 않기 위한 것 |
| `ddl/sp_definition.sql` | `MetadataExporter.ExportRawMetadataAsync`(`:315`) | 분석 대상 본문의 DDL 백업 |
| `ddl/tables/[DB.]스키마.이름.md` | `MetadataExporter`(`:334`) | 참조 테이블의 컬럼 스키마를 마크다운 표로. **SQL이 아니라 md인 이유**는 이 표가 그대로 프롬프트와 지시서에 실리기 때문 |
| `ddl/procedures/*.sql`, `ddl/functions/*.sql` | `MetadataExporter`(`:350`), 이식용 번들은 `ExportReferencedCodeDdlsAsync`(`:151`) | 참조된 코드 객체의 DDL. 하위 폴더 이름은 `SqlObjectTypeClassifier`가 판정한 종류를 따른다 |

### Objects/ — 객체 종류와 무관한 표준 DDL 보관소

| 경로 | 누가 | 왜 |
|---|---|---|
| `Objects/[Schema].[이름].[Type]/raw/object_definition.sql` | `MetadataExporter`(`:63`), 경로는 `OutputPathResolver.ResolveCanonicalDdlPath` | 같은 객체를 여러 경로에서 만나도 DDL 원본은 **한 벌만** 두기 위한 정본. 디렉터리 이름에 `Type`이 붙는 것은 스키마·이름이 겹치는 서로 다른 종류의 객체를 구분하기 위한 것 |
| `Objects/[...]/raw/prompt-context.md` | `MetadataExporter`(`:72`) | 위 DDL을 뽑을 때 쓴 프롬프트 원문 |

---

## 3. ② 통합 Job 계획 수립 산출물

**언제** — 여러 SP를 하나의 배치 Job으로 묶어 통합 전환 계획서를 만들 때.
3단계로 진행되며 각 단계가 자기 산출물을 남긴다: **1/3 Brainstorm → 2/3 Structure(목차) → 3/3 Finalize**.

**어디에** — `output/Jobs/[Job이름]/`

| 파일 | 누가 | 언제 | 왜 |
|---|---|---|---|
| `raw/Brainstorming.md` | `VerificationPipelineOrchestrator`(`:1831`) | 1/3 직후 | 최종 계획서에 남지 않은 초기 발상. 계획이 왜 그 모양이 됐는지는 여기에만 있다 |
| `raw/PlanStructure.md` | `VerificationPipelineOrchestrator`(`:1840`, `:3233`) | 2/3 직후, 목차 재작성 시 갱신 | 3/3이 채워 넣을 **목차 계약**. 단계 본문을 동시 생성해도 순서와 경계가 흔들리지 않게 하는 기준 |
| `raw/PlanStructure.superseded-N.md` | `VerificationPipelineOrchestrator`(`:3231`) | 목차를 다시 짤 때마다 | 폐기된 이전 목차. 덮어쓰지 않고 번호를 늘려 보존한다 — 목차가 왜 바뀌었는지 추적하려면 이전 판이 남아 있어야 하므로 |
| `raw/prompt-context.md` | `Program.cs:890` | 3/3 성공 시 | 계획서를 만든 System/User 프롬프트 원문 |
| `raw/ddl/*.md` | `InstructionBundleWriter`(`:523`) | 번들 생성 시 | Job 전체가 건드리는 참조 테이블의 스키마 표 |
| `docs/BatchMigrationPlan.md` | `Program.cs:868` | 3/3 성공 시 | **통합 전환 계획서**. `FormatVerifiedDocument`로 검증 결과와 커버리지를 헤더에 얹는다 |
| `docs/Thinking.md` | `Program.cs:885` | 계획서와 한 쌍 | 채택된 시도의 추론 로그. 계획서와 짝이라 한쪽만 나가면 안 된다 |

---

## 4. ③ 지시서 번들 (`Jobs/[Job]/agent/`)

**누가** — `InstructionBundleWriter`가 뼈대를, `MetadataExporter.ExportConsolidatedMigrationInstructionsAsync`가
스텁을, `AgentProgressStore`가 진행 상태를 쓴다.

**왜 쪼개는가** — 계획서 한 덩어리를 통째로 주면 에이전트가 매 회차 전부를 읽는다.
`PlanBoundaryResolver`가 계획서를 절 단위로 잘라 공통 문서와 단계 본문으로 나누고, 회차별 지시서는
자기 몫만 링크한다. 조각 본문을 지시서에 **복사하지 않고 링크**하는 것은, 계획서가 교정·구제 채택으로
계속 바뀌는 동안 두 벌이 조용히 어긋나는 것을 막기 위한 것이다(`PlanLayout` 주석).

| 경로 | 누가 | 왜 |
|---|---|---|
| `MigrationInstructions.md` | `InstructionBundleWriter`(`:209`) + `InstructionEntryPointComposer` | 진입점. **지켜야 할 지침이 앞, 나머지는 링크**로만 둔다 |
| `task-00-bootstrap.md` | `TaskFileComposer.FileName` | 0회차 = 골격 |
| `task-NN-[단계코드].md` | 〃 | 1..N회차 = 단계별 이행 |
| `task-99-assembly.md` | 〃 | 99회차 = 조립 |
| `common/00-architecture.md` | `InstructionBundleWriter`(`:64`) | 전 회차 공통 — 아키텍처 |
| `common/01-step-contract.md` | 〃(`:66`) | 전 회차 공통 — 단계 계약. **슬라이스가 비면 파일을 지운다**(`:76`) |
| `common/02-data-access-boundary.md` | 〃(`:83`) | 전 회차 공통 — 데이터 액세스 경계 규칙 |
| `common/03-hosting-and-config.md` | 〃(`:93`) | 전 회차 공통 — 호스팅·설정 |
| `steps/[단계코드].md` | 〃(`:168`) | 단계별 이행 상세 본문. 슬라이스가 없으면 디렉터리째 삭제된다(`:180`) |
| `verification/integrity-sql.md` | 〃(`:102`) | 무결성 대조 SQL. 계획서에 검증 절이 없으면 디렉터리째 삭제된다(`:110`) |
| `src/*.cs` \| `*.java` | `MetadataExporter`(`:532` 이하) | **에이전트에 미리 제공하는 뼈대 스텁**. `AbstractSettleTasklet` 상속을 강제해 임의 구조와 자의적 에러코드를 막는다. Java는 `com.reset.batch.core` 패키지 인터페이스 7종을 함께 낸다 |
| `tests/ArchitectureTests.*`, `tests/StepLogicTests.*` | `MetadataExporter`(`:775`, `:825`) | 함께 제공하는 테스트 스텁. 뼈대를 우회한 구현을 아키텍처 테스트가 잡아내게 하기 위한 것 |
| `progress.json` | `AgentProgressStore`(`:183`) | **회차 진행 상태의 진실의 원천**. 도구가 소유하며 에이전트는 쓰지 않는다 |
| `todo.md` | `AgentProgressStore`(`:184`) | `progress.json`에서 렌더링되는 사람용 표시. 예전에는 에이전트에게 `[x]`를 직접 갱신하라고 요구했으나 아무 일도 일어나지 않았고, 이제 **검증 결과만이 상태를 바꾼다** |

두 상태 파일은 `WriteAtomicAsync`로 임시 파일에 쓴 뒤 옮긴다. 최종 경로에 바로 쓰면 중단 시 잘린
파일이 남고, 그 시점에 진행 상태가 이미 망가지기 때문이다. 읽거나 파싱하지 못한 `progress.json`은
지우지 않고 옆으로 옮겨 보존한다(`:121`).

**인코딩 주의** — C# 산출물은 `Encoding.UTF8`(BOM 포함), Java 산출물은 BOM 없는 UTF-8을 쓴다.
javac가 BOM으로 시작하는 소스를 거부한다는 보고(JDK-4508058) 때문이다.

---

## 5. ④ 코드 생성과 검증

### Jobs/[Job]/src/ — 에이전트의 작업 결과물

**누가** — 외부 코딩 에이전트(claude / codex / agy CLI). ReSet은 이 디렉터리를 만들어
작업 디렉터리로 넘길 뿐(`Program.cs:918`·`1484` → `ExternalCliCodingEngine:52`) 내용을 쓰지 않는다.

**왜 격리하는가** — 생성된 코드가 ReSet 자신의 `src/`나 사용자 워킹 트리를 오염시키지 않게 하기 위한 것.

### Jobs/[Job]/validation/ — 정합성 대조 리포트

**누가·언제** — 두 주체가 같은 디렉터리를 나눠 쓴다.

`CodeVerificationOrchestrator`(소스코드 대조, `ReSet.Cli`가 기동):

| 경로 | 왜 |
|---|---|
| `docs/[SP]/ValidationReport.md` | 명세서와 생성 코드의 격차 리포트 |
| `docs/[SP]/AI_Response.md` | 판정 근거가 된 AI 원문 |
| `docs/validation_summary.md` | Job 전체 요약 |
| `raw/[SP]/Spec.md` | 대조에 실제로 쓰인 명세서 사본 — 원본이 이후 바뀌어도 판정 근거가 남게 |
| `raw/[SP]/Source.[확장자]` | 대조에 쓰인 소스 사본 |
| `raw/[SP]/AI_Prompt.md` | 주입된 프롬프트 원문 |

`ReSet.Validator.Cli`(데이터 정합성 대조):

| 경로 | 언제 | 왜 |
|---|---|---|
| `[SP]_test_inputs.json` | 테스트 입력 생성 시 | 레거시와 신규에 **같은 입력**을 넣기 위한 고정본 |
| `mock/[SP]_mock_data.json` | 샌드박스 시딩 시 | 시드 데이터 |
| `[SP]_legacy_results.json` | 레거시 SP 실행 후 | 기준값 |
| `[SP]_target_results.json` (또는 `_new_results.json`) | 신규 코드 실행 후 | 비교 대상 |
| `[SP]_CompareReport.md` | 대조 후 | 최종 판정 |

---

## 6. 그 외 산출물

| 경로 | 누가 | 언제 | 왜 |
|---|---|---|---|
| `logs/reset-YYYYMMDD.log` | `Program.ConfigureLogging`(`Program.cs:2538`, Serilog 일별 롤링) | 분석기 실행 내내 | 실행 로그. 경로·보관 개수는 `LoggingSettings:LogDirectory`·`RetainedFileCountLimit`(기본 31)로 조정 |
| `logs/reset-validator-YYYYMMDD.log` | `ReSet.Validator.Cli/Program.cs:387` | 검증기 실행 내내 | 검증기 로그를 분석기와 섞지 않기 위해 파일명을 분리 |
| `cleansing/[대상]_MetadataCleansing.sql` | `VerificationPipelineOrchestrator`(`:1255`, `:3317`) | 메타데이터 결손이 발견될 때 | AI가 제안한 **메타데이터 보정 SQL**. 자동 실행하지 않고 파일로만 남긴다 — DB를 바꾸는 일은 사람이 읽고 결정할 몫이므로 |
| `.sp_cache_index.json` | `CacheManager.SaveCacheIndex`(`:381`), 위치는 `GetGlobalCacheDirectory` | 분석 성공 시 | SP 본문·의존성 해시로 재분석을 건너뛰기 위한 색인. 하위 디렉터리에 흩어져 있던 레거시 인덱스는 `MigrateLegacyCaches`가 이 한 벌로 합친다. **캐시를 통째로 버리려면 이 파일을 지우면 된다** |
| `offline_snapshot.json` (경로는 사용자 지정) | `SnapshotManager.ExportSnapshotAsync`, `--extract-snapshot` 인자로 기동 | 명시적으로 요청할 때만 | DB 연결 없이 구동하기 위한 스냅샷. `DatabaseSettings:OfflineSnapshotPath`에 지정하면 DB 조회를 우회한다 |

---

## 7. 산출물이 아닌 것

혼동하기 쉬운 두 가지는 `output/` 밖에 있고, 남기지 않는다.

- **CLI 제공자 작업 공간** — `CliWorkspace`가 시스템 임시 디렉터리에 `reset-cli-[GUID]/`를 만들어
  프롬프트 파일을 넘긴다. `IDisposable`로 정리된다. 시스템 프롬프트 파일에는 BOM을 붙이지 않는데,
  파일 맨 앞의 보이지 않는 문자를 모델이 지시로 읽기 때문이다.
- **Mermaid 렌더 검사** — `MechanicalValidator`(`:1039`)가 `%TEMP%/ReSet_Mermaid/`에서 다이어그램
  문법을 검사한다. L1의 판정 근거일 뿐 산출물이 아니다.

---

## 8. 읽는 순서 제안

문제를 되짚을 때 열어야 할 순서는 대개 정해져 있다.

1. **결과가 틀렸다** → `docs/Spec.md`의 YAML 헤더에서 `VerificationOutcome`과 축별 점수 확인
2. **왜 그렇게 나왔나** → 같은 폴더의 `Thinking.md`
3. **모델이 무엇을 봤나** → `raw/prompt-context.md`. 입력이 부실하면 그 위의 둘을 탓할 이유가 없다
4. **입력이 왜 부실한가** → `raw/metadata.json`과 `raw/ddl/` — 의존성 수집 단계의 문제다
5. **계획서가 이상하다** → `Jobs/[Job]/raw/Brainstorming.md` → `PlanStructure.md`(+`superseded-N`) 순서로
   3단계 중 어디서 어긋났는지 좁힌다
6. **에이전트가 엉뚱한 코드를 냈다** → `agent/progress.json`으로 어느 회차인지 특정하고,
   그 회차의 `task-NN-*.md`와 그것이 링크하는 `steps/`·`common/`만 읽는다

---

## 관련 문서

- [프로젝트 구조](../README.md#-프로젝트-구조-project-structure) — 디렉터리 트리 요약
- [아키텍처](architecture.md) — 산출물을 만드는 모듈과 데이터 흐름
- [남은 후속 작업](todo.md) — 산출물 생성 경로에 남아 있는 결함 목록
