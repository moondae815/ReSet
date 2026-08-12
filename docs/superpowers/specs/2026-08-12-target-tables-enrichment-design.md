# 목차 대상 테이블 보강 설계

- 작성일: 2026-08-12
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행: [2026-08-08 단계 오류코드 검증](2026-08-08-step-error-code-verification-design.md) §후속 3,
  [2026-08-07 지시서 번들 분할](2026-08-07-migration-instructions-split-design.md)

## 배경

`MechanicalValidator.ValidateBatchStep`의 하한 검사는 목차가 선언한 `TargetTables`와 `ErrorCodes`를
단계 본문에서 찾는다. `ErrorCodes`는 선행 브랜치가 명세서에서 뽑아 채웠고, 그 뒤 실측에서 12/12
단계가 83개 코드로 실제 대조를 받았다. **`TargetTables`는 그 처리를 받지 않았다.**

2026-08-12에 같은 12개 SP로 두 회차를 돌렸다. 하나는 CLI 제공자(`claude-cli`), 하나는 API
제공자(`Claude`)이며 둘 다 시도 1회에 L1/L2를 통과했다.

| | `POQSettleProc` (CLI) | `POQSettleProc3` (API) |
|---|---:|---:|
| 목차가 선언한 `ErrorCodes` | 83개 | 83개 |
| 목차가 선언한 `TargetTables` | **7개** | **17개** |
| `TargetTables`가 빈 단계 | **5개** (S01·S09~S12) | **1개** (S01) |

`ErrorCodes`가 두 회차에서 정확히 같은 것은 그 값을 모델이 아니라 `SpecReturnCodeExtractor`가
명세서에서 뽑기 때문이다. 도구가 채우면 결정적이고, 모델에게 맡기면 같은 입력에 2.4배가 흔들린다.
`TargetTables`는 후자다.

### 결함 1 — 검사가 거의 돌지 않는다

`TargetTables`가 빈 단계는 `PlanDefects`에 "대상 테이블 대조를 실행할 수 없습니다"로 기록되고
검사를 건너뛴다. 침묵은 아니지만 대조는 0회다. CLI 회차에서 12단계 중 5개가 그 상태였고, 나머지
7개도 **테이블을 하나씩만** 선언해 전체 대조 항목이 7건이었다. 같은 회차의 오류코드 대조가
83건인 것과 대비된다.

### 결함 2 — 회차 지시서의 스키마 스코프가 함께 무너진다

`InstructionBundleWriter.DependenciesForStep`은 같은 `TargetTables`로 그 회차 에이전트에게 붙일
DDL을 좁힌다. 비면 전체 목록으로 폴백한다.

```
[WRN] 단계의 목차 TargetTables가 비어 있어 의존성 스키마를 좁히지 못하고
      전체 목록으로 대체합니다 - Step: S01, 스키마 수: 55개   (S09·S10·S11·S12 동일)
```

CLI 회차에서 12단계 중 5개가 **DDL 55개를 통째로** 받았다. 지시서 분할 설계가 "회차당 입력
약 40k"를 목표로 세우고 `BatchStepPlan.TargetTables`로 스코프를 좁히기로 결정한 그 장치가,
목차가 비어 있어 발동하지 않은 것이다. DDL 55개는 약 58k 토큰으로, 그 설계가 줄이려던 몫에서
가장 큰 항목이다.

### 결함 3 — 목차의 선언에 이미 허위가 섞여 있다

API 회차의 S11이 네 개를 선언했는데 셋이 **원본 DDL에 한 번도 등장하지 않는다.**

```
S11  "취소영향 요약 보정"       Legacy: UP_UTIL_SETTLE_SUMMARY_ETC
  목차 선언   TSettleByTX, TPartialCancelByTX, TSettleByIN, TSettleByOUT
  정적 분석   Insert/Delete: TSettleByOUT      Select: TSettleMst
  DDL 원문 등장 횟수   TSettleByIN 0 · TSettleByTx 0 · TPartialCancelByTx 0
  IsParsedSuccessfully = True, 동적 SQL 없음 — 파서가 놓친 것이 아니다
```

현재 검사가 이를 통과시키는 이유는 모델이 목차와 본문에 **같은 허위를 일관되게** 썼기 때문이다.
검사는 "선언한 이름이 본문에 등장하는가"만 보므로 허위끼리 일관되면 통과한다.

같은 회차의 S09에는 다른 종류가 있다. `TSettleMst`를 대상으로 선언했는데 정적 분석에서는
**읽기 전용**이다. 프롬프트 계약(`AiService`의 목차 규칙: "`TargetTables` must list every table
the step creates or modifies")을 어긴 것이고, 그대로 두면 읽기 테이블이 검증 요건이 된다.

### 재료는 이미 있고, 목차까지 도달하지 않을 뿐이다

S01의 원본 SP(`dbo.UP_Util_PG_Client_CMRate_Ins`)의 `raw/metadata.json`에는 대상이 다 들어 있다.

```
InsertTables  TPGSettleRate, TClientSettleRate, TPGSettleRate4Extra,
              TClientSettleRate4Extra, TClientSettleRate4MobileCo    (canonical 3-part)
DeleteTables  같은 5개
SelectTables  TSettleMst, TPGCMRate, TClientContract, TClient,
              TClientCMRate, TClientCMRate4Extra, TClientCMRate4MobileCo
```

두 제공자가 **독립적으로 같은 자리를 비웠다.** 모델의 변덕이 아니라 구조적 결손이며, `ErrorCodes`가
26단계 중 25개 비었던 것과 같은 양상이다.

### 착수 전 대조 — 검사를 진짜로 돌려도 안전한가

`ErrorCodes` 설계가 착수 전에 했던 것과 같은 확인을 했다. 두 회차의 24개 단계에 대해 정적 분석의
쓰기 대상을 추출하고, 그 이름이 단계 본문에 실제로 등장하는지 대조했다.

| 회차 | 추출된 쓰기 대상 | 본문에 없는 것 |
|---|---:|---:|
| `POQSettleProc` | 19개 | **0개** |
| `POQSettleProc3` | 19개 | **0개** |

**미달 0건이다.** 보강해도 재시도 폭주나 배너 홍수는 생기지 않는다.

스코프 크기도 쟀다. 쓰기 ∪ 읽기는 단계당 평균 6.5개, 최대 12개로 **전체 55개의 12%**다.

## 목표와 범위

1. 목차의 `TargetTables`를 정적 분석의 쓰기 대상으로 채워 하한 검사가 실제로 대조하게 한다.
2. 회차 지시서의 DDL 스코프를 위한 별도 필드를 두어, 검증 재료와 스코프 재료를 가른다.
3. 모델이 선언했으나 정적 분석에 없는 이름을 드러낸다.

**범위 안**

- `SpecTargetTableExtractor` 신설
- `PlanStructureEnricher`의 `TargetTables`·`SchemaTables` 보강
- `BatchStepPlan`에 `SchemaTables` 추가와 파서 대응
- `InstructionBundleWriter.DependenciesForStep`의 스코프 원천 교체
- 두 진입 경로(무인 배치, 메뉴 3)에 정적 분석 배선
- `PlanStructureEnricher.ReadStringArray`의 뮤테이션 하중 결손 해소 (오류코드 검증 §후속 2)

**범위 밖**

- **명세서 산문에서 `TargetTables` 추출.** 선행 설계가 「갱신 대상 테이블」 절에서 뽑는 방안을
  적었으나, 그 문구는 코드에 존재하지 않는다(`grep` 0건). 프롬프트가 강제하는 헤딩은
  `### INSERT 대상 테이블:`·`### UPDATE 대상 테이블:`이다. 더 중요하게는 뽑을 이유가 없다 —
  대상 테이블은 오류코드와 달리 산문이 아니라 AST에 구조화된 데이터로 이미 존재한다.
- **프롬프트 계약 변경.** 모델에게 `SchemaTables`를 요구하지 않는다. 빈칸을 규정하는 것 자체가
  주장을 유도한다는 판단은 스키마 주장 게이트 설계가 이미 적어 두었다.
- **`StepDefect`·배너 종류 확장.** 약 90개 참조를 건드리는 작업이고, 이 설계가 새로 드러내는
  사실은 재생성으로 고칠 수 없어 배너에 실을 성격이 아니다(§오류 처리).
- **읽기 원본에 대한 검증.** `SchemaTables`는 스코프 재료일 뿐 검사 재료가 아니다.
- **`ErrorCodes` 보강 규칙 변경.** 합집합 그대로 둔다(§3에 근거).

## 설계

### 1. 필드를 둘로 나눈다

`TargetTables`는 소비자가 둘이고, 둘이 요구하는 집합이 다르다.

| 소비자 | 쓰임 | 필요한 집합 |
|---|---|---|
| `MechanicalValidator.ValidateBatchStep` | 단계 본문에 그 테이블이 등장하는지 대조 | 쓰기 대상만 |
| `InstructionBundleWriter.DependenciesForStep` | 회차 지시서에 붙일 DDL을 좁히는 필터 | 읽기 원본도 필요 |

프롬프트 계약은 이미 한쪽 편을 들고 있다 — `TargetTables`는 "creates or modifies"다. 그런데 DDL
스코프가 같은 필드를 쓰고 있어서, 지금은 필드가 비어 있는 덕분에 전체 폴백이 걸려 **우연히**
읽기 원본까지 실린다.

그래서 `TargetTables`를 계약대로 쓰기 대상만으로 채우면 **DDL 스코프는 지금보다 나빠진다.** S01은
55개에서 5개로 줄지만 그 5개로는 SELECT를 쓸 수 없다. `DependenciesForStep`의 주석이 정확히 이
경우를 경계한다 — "데이터 액세스 코드를 쓰다가 필요한 테이블의 컬럼 정의를 찾지 못하는 쪽이,
몇 개 더 실리는 쪽보다 훨씬 나쁘다."

따라서 `BatchStepPlan`에 필드를 하나 더 둔다.

```csharp
/// <summary>
/// 이 회차 에이전트에게 DDL을 붙일 테이블. 쓰기 대상과 읽기 원본의 합집합이다.
///
/// TargetTables와 나누는 이유: 저쪽은 "본문이 이 테이블을 기술했는가"를 묻는 검증 재료이고
/// 이쪽은 "에이전트가 어떤 스키마를 봐야 하는가"를 정하는 스코프 재료다. 한 필드로 겸하면
/// 읽기 원본을 넣을 때 검증이 과해지고, 빼면 스코프가 모자란다.
///
/// 모델은 이 필드를 모른다. 도구가 정적 분석에서 채운다.
/// </summary>
public List<string> SchemaTables { get; set; } = new();
```

**쓰기까지 담아 완전한 집합으로 둔다.** "읽기 전용 추가분"만 담으면 `DependenciesForStep`이 두
필드를 합쳐야 하고, 그 순간 판정이 소비자마다 갈릴 여지가 생긴다 — 이 저장소가 반복해서 물린
모양이다.

### 2. `SpecTargetTableExtractor`

`SpDefinition` 목록에서 `프로시저 맨이름 → (쓰기 집합, 읽기 집합)`을 만드는 순수 함수다.

- **키는 `SpecReturnCodeExtractor`와 같은 규칙** — 마지막 점 뒤를 소문자화한다
  (`dbo.UP_UTIL_SETTLE_INS` → `up_util_settle_ins`). 목차의 `LegacyProcedures`와 대조하기
  위해서이고, 두 추출기가 다른 규칙을 쓰면 한쪽만 매칭되는 날이 온다.
- 쓰기 = `InsertTables ∪ UpdateTables ∪ DeleteTables`, 읽기 = `SelectTables`
- **임시 테이블(`#`·`##`)과 테이블 변수(`@`)는 제외한다.** 물리 테이블이 아니라 DDL도 없고,
  검증에 걸면 UPDATE 매핑 브랜치가 물렸던 "존재하지 않는 요건" 결함을 새로 만든다.
- **표기는 canonical 3-part 그대로** 넘긴다. 정적 분석이 이미 `StaticAnalysisNormalizer`를
  거쳤고, 소비자 둘 다 맨이름 토큰 매칭이라 안전하다.
- 정의가 없는 프로시저는 **키를 만들지 않는다.** "그런 프로시저 없음"과 "대상이 0개임"을 구별한다.

파일 I/O가 없다. 호출부가 이미 들고 있는 `SpDefinition`을 받는다.

### 3. 보강 규칙 — 교체하되 재료를 0으로 만들지 않는다

단계마다 `LegacyProcedures`의 각 이름으로 사전을 조회해 합친다(프로시저가 여럿이면 합집합).

| 조건 | `TargetTables` | 버려진 선언 |
|---|---|---|
| 쓰기 집합이 비어 있지 않다 | **추출값으로 교체** | 기존 선언 중 새 집합에 없는 이름을 경고로 보고 |
| 쓰기 집합이 비었다 (프로시저 미발견·파싱 실패·대상 0개) | **기존값 유지** | 없음 |

`SchemaTables`는 조건 없이 쓰기 ∪ 읽기로 채운다. 모델이 내지 않는 필드라 교체·유지를 가릴
기존값이 없고, 둘 다 비면 필드를 넣지 않는다.

**`ErrorCodes`와 규칙이 다른 이유.** 저쪽은 합집합이다. 그 설계의 근거는 "모델이 추가로 선언한
코드는 어차피 본문에 있으므로 검사 범위만 넓어진다"였다. 대상 테이블에서는 그 근거가 함정이
된다 — S11의 허위 3개가 본문에도 있으므로 합집합하면 **허위가 검증 요건으로 승격**되고, 재생성
때 모델이 그 이름을 빼는 순간 오류가 나므로 게이트가 허위를 고착시킨다.

근본적으로는 두 재료의 신뢰도가 대칭이 아니다. 오류코드는 명세서 산문에서 뽑고 모델도 같은
산문을 본다. 테이블은 파서가 AST에서 확정하고 모델은 추측한다.

나머지 필드는 손대지 않고, 두 번 보강해도 결과가 같다(교체라 자연히 멱등). 두 번째 보강에서
경고가 나오지 않는 것도 옳다 — 그때는 이미 허위가 사라진 뒤다.

### 4. 버려진 선언의 경고 채널

`Log.Warning`으로 남기고, 오케스트레이터가 모아 `_userInteraction.NotifyWarnings`로 한 번
표시한다. **배너와 `StepDefect`는 건드리지 않는다.**

근거는 스키마 주장 게이트가 "A 위반"에 내린 판단과 같다. 이 사실은 목차가 확정된 뒤에 관측되므로
**재생성으로 고칠 수 없다.** L1 오류나 `StepDefect`로 승격시키면 고칠 수 없는 것을 재시도 루프에
넣는 셈이고, 그것은 이 저장소가 이미 두 번 물린 실패 모드다.

동시에 조용히 넘어가지도 않는다. S11의 허위 3개는 **계획서 본문에도 들어가 있다**는 신호이고,
검사에서 빼는 것과 별개로 사람이 알아야 할 사실이다.

### 5. 배선

오케스트레이터가 `SpDefinition` 목록을 받아 안에서 추출기를 부른다.

```csharp
RunConsolidatedPipelineAsync(
    List<(string FileName, string Content)> specs,
    string targetLanguage, string jobName, string provider, string outputRoot,
    bool isBatchMode = false,
    IReadOnlyList<SpDefinition>? definitions = null,   // 신설, 기본 null
    CancellationToken cancellationToken = default)
```

`SpecReturnCodeExtractor.Extract`가 이미 오케스트레이터 안에서 돌므로 추출 지점이 나란히 모인다.
오케스트레이터는 이미 `SpDefinition`을 안다(`SpecExpectations.From(spDef)`를 부른다) — 새 의존이
아니다.

**기본값 `null`이 회귀 방어의 본체다.** 넘기지 않으면 보강이 일어나지 않고 종전 동작이 그대로다.
기존 테스트가 한 줄도 바뀌지 않는다.

함정이 하나 있다. `Program.cs`의 무인 배치 호출부가 `CancellationToken`을 **위치 인자로** 넘기고
있어(`isBatchMode: true` 뒤), 매개변수를 그 앞에 끼우면 조용히 잘못된 자리에 바인딩된다. 두
호출부를 명명 인자로 바꾼다. 구현 계획의 명시적 단계로 둔다.

두 진입 경로는 조건이 다르다.

| 경로 | 정적 분석 가용성 | 처리 |
|---|---|---|
| 무인 배치 (`--job-name`) | `spDefs`가 이미 스코프에 있다 | 그대로 넘긴다 |
| 메뉴 3 (기존 산출물로 계획 재수립) | `specsData`를 디스크의 `Spec.md`에서 만든다 | `BatchStepCatalog.LoadDefinitionsAsync` 호출을 파이프라인 앞으로 옮기고 결과를 지시서 생성에서 재사용 |

메뉴 3은 이미 그 로더를 부르고 있다 — 파이프라인이 **끝난 뒤**에, 지시서를 쓰려고. 앞으로 옮기면
부수 효과가 하나 생기는데 이득이다. 지금은 메타데이터가 없는 SP를 수십 분짜리 계획 수립이 끝난
뒤에야 "지시서에서 제외됩니다"라고 알린다.

**두 경로를 함께 고치는 이유**는 경로마다 판정이 갈리는 것이 이 저장소가 반복해서 물린 결함이기
때문이다.

### 6. 소비자 두 곳의 변화

| 소비자 | 종전 | 이후 |
|---|---|---|
| `ValidateBatchStep` | `TargetTables`가 비어 `PlanDefect`("검증 불가") | 채워져서 실제 대조 — 회차당 19건(두 회차 합계 38건), 본문 부재 0건 |
| `DependenciesForStep` | `TargetTables`로 좁힘, 비면 전체 55개 | **`SchemaTables`로 좁힘** — 평균 6.5개(전체의 12%) |

`DependenciesForStep`의 폴백 둘(필드가 빔 / 일치하는 의존성 0건 → 전체 목록 + 경고)은 그대로
둔다. 좁히기의 근거가 사라졌을 때 조용히 빈 목록을 내보내지 않는다는 기존 판단이 옳고, 이번
변경이 그것을 바꿀 이유가 없다.

### 7. 기록

보강된 목차는 기존 경로 그대로 `raw/PlanStructure.md`에 기록된다. 사람이 파일만 열어 무엇을
검사했고(`TargetTables`) 무엇을 스코프로 줬는지(`SchemaTables`) 확인할 수 있다 — `ErrorCodes`
설계가 세운 목표 2와 같은 정신이다.

## 오류 처리

**예외 탈출 경로는 다음 세 함수를 이름으로 특정해 호출부까지 따라가며 확인한다.** 직전 두
브랜치가 연속으로 "예외를 새로 던지는 경로는 없다"고 적고 실제로는 거짓이었다. 함수를 특정하지
않은 확인 선언은 다음 사람에게 전부를 확인했다는 인상을 준다.

1. **`SpecTargetTableExtractor.Extract`** — `SpDefinition.StaticAnalysis`가 `null`일 수 있고 각
   목록도 `null`일 수 있다. 호출 지점이 `SpecReturnCodeExtractor.Extract` 바로 옆이라 그 자리에
   봉투가 있는지부터 확인하고, 없으면 추출기 자체를 방어적으로 짠다.
2. **`PlanStructureEnricher`** — `TryRewriteBlock` 본문은 이미
   `catch (Exception ex) when (ex is not OperationCanceledException)`로 덮여 있다. **새 코드가 그
   `try` 안에 있어야 한다.** 침묵 실패 브랜치가 정확히 여기서 물렸다 — `TryLocateStepsBlock`을
   `try` 밖에서 부르는 바람에 예외가 파이프라인을 뚫고 나가 `RetryRescue`가 실행되지 못했다.
3. **`BatchStepPlanParser`** — `SchemaTables` 읽기가 추가된다. `catch`는 이미 넓혀져 있으니 그
   계약을 깨지 않는지 확인한다.

| 상황 | 동작 |
|---|---|
| `definitions`를 넘기지 않음 (`null`) | 보강 없음. 종전 동작 |
| `metadata.json` 없음 / 파싱 실패 | 그 프로시저는 키 없음 → 해당 단계는 기존값 유지, "검증 불가"도 그대로 |
| `StaticAnalysis == null` 또는 `IsParsedSuccessfully == false` | 쓰기 집합이 비므로 기존값 유지 |
| `LegacyProcedures`가 빈 단계 | 조회할 것이 없어 손대지 않는다 (계획이 새로 설계한 단계) |
| 목차 JSON 파싱 실패 | 기존 규칙 — 원본 반환 + `Log.Warning` |
| 구 산출물의 목차에 `SchemaTables`가 없음 | 파서가 빈 목록 → `DependenciesForStep`이 전체 폴백 + 경고. 종전과 동일 |

마지막 줄이 하위 호환의 전부다. 이전에 만들어진 Job을 메뉴 3으로 다시 돌려도 나빠지지 않는다.

## 테스트

TDD로 진행한다. 각 단위마다 실패하는 테스트를 먼저 쓴다.

**`SpecTargetTableExtractorTests`** (신규) — 쓰기/읽기 분리 / 임시 테이블·테이블 변수 제외 / 키
규칙이 `SpecReturnCodeExtractor`와 같음 / `StaticAnalysis`가 null / 정의가 없는 프로시저는 키
없음 / 한 단계에 프로시저가 여럿이면 합집합

**`PlanStructureEnricherTests`** (추가) — 추출값으로 교체 / 쓰기가 비면 기존값 유지 /
`SchemaTables`를 채움 / `ErrorCodes`·`Chunkable` 등 다른 필드 보존 / JSON 블록 밖 산문 보존 /
멱등 / 버린 선언이 경고에 실림

**왕복** — 보강 → `BatchStepPlanParser.TryParse` → 값 일치. `ErrorCodes` 설계가 "가장 중요하다"고
한 테스트다. 깨지면 파일에 기록된 목차와 실제로 쓰인 목차가 갈라지는데, 그것이 이 작업이
고치려는 결함과 같은 종류다.

**소비자** — `ValidateBatchStep`이 채워진 `TargetTables`로 실제 대조를 돌리는지 /
`DependenciesForStep`이 `SchemaTables`로 좁히고, 비면 전체로 폴백하는지

**회귀 픽스처** — 실측 두 회차의 목차와 정적 분석을 축약해 체크인한다. `output/`은 추적되지
않으므로 픽스처가 필요하고, 이 저장소에 `Fixtures/` + `RepoPaths.FindRepoRoot()` 선례가 있다.
고정할 사실은 하나다.

> S11의 `TargetTables`가 4개에서 `TSettleByOUT` 하나로 줄고, 허위 3개(`TSettleByTX`·
> `TPartialCancelByTX`·`TSettleByIN`)가 경고에 실린다.

지어낸 예제가 아니라 실제 모델이 실제로 쓴 형태라는 점이 요점이다.

### 뮤테이션 저항 확인 — 구현 계획의 명시적 단계

침묵 실패 브랜치에서 테스트 8개가 가드를 지워도 전부 통과했고, 그것이 그 브랜치가 닫으려던
결함과 정확히 같은 모양이었다. 최소한 다음 넷은 가드를 지우고 테스트가 깨지는지 확인한 뒤
복원한다.

1. 임시 테이블·테이블 변수 제외 조건
2. **쓰기 집합이 비면 기존값을 유지하는 조건** — 지우면 빈 배열로 교체되어 멀쩡한 단계가
   "검증 불가"로 떨어진다
3. `SchemaTables`에 쓰기를 포함하는 것 — 지워도 읽기만으로 대부분 통과하므로 하중이 없기 쉽다
4. 버린 선언을 경고로 내는 조건

### 함께 닫는 것

`PlanStructureEnricher.ReadStringArray`의 비문자열 방어 테스트가 뮤테이션 저항이 없다는 기존
후속 작업(오류코드 검증 §후속 2)을 닫는다. 같은 파일을 여는 김이고, `codesByProcedure`에 `"123"`
키를 넣어 통과 시 결과가 달라지게 만들면 된다.

## 문서 동기화

- `docs/architecture.md` 2.2 테이블에 `SpecTargetTableExtractor` 추가. §4 메커니즘의 목차 보강
  항목에 대상 테이블 축 추가
- `AGENTS.md`에 목차의 검사 재료는 도구가 채우며, 대상 테이블의 진실의 원천은 명세서 산문이
  아니라 정적 분석이라는 규칙
- `README.md`는 외부 사용자에게 드러나는 변화가 없어 대상 아님

## 완료 기준

1. `dotnet clean && dotnet build`에서 오류 0건, 경고 정확히 8건 (기존 `DbMetadataServiceTests`의
   CS8600/CS8602 — 현재 기준선 유지)
2. `dotnet test`가 기존 **1,318건** + 신규분 전부 통과
3. `POQSettleProc3`의 목차를 보강하면 12단계 전부 `TargetTables`가 비지 않고 **"검증 불가" 0건**이
   된다
4. 같은 회차에서 `SchemaTables`로 좁힌 회차별 DDL이 평균 6.5개(전체 55개의 12%)가 된다
5. S11 회귀 픽스처가 교체와 경고를 함께 고정한다

## 사람이 직접 확인해야 하는 것

이 설계의 테스트는 전부 단위 수준이다. 실제 AI 응답으로 파이프라인을 끝까지 돌린 검증은 포함되지
않는다.

1. 실제 Job 1회 — 보강된 `TargetTables`로 하한 검사가 실제로 돌아 통과하는지, 그리고 진입점 §0에서
   "검증 불가" 목록이 사라지는지
2. 회차 지시서(`task-NN-*.md`)에 실리는 DDL이 실제로 좁혀졌는지, 그리고 **에이전트가 그것만으로
   데이터 액세스 코드를 쓸 수 있는지** — 좁히기가 과하면 이 설계가 결함 2를 고치면서 새 결함을
   만든 것이다
3. 버려진 선언 경고가 실제 실행에서 뜨는지, 뜬다면 그것이 진짜 허위인지
