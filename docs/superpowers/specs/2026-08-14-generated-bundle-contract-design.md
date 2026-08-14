# 생성 번들의 계약 정합성 설계

**작성일**: 2026-08-14
**상태**: 설계 확정

## 목표

`output/Jobs/<Job>/agent/` 번들이 **외부 코딩 에이전트에게 그대로 넘길 수 있는 상태**로
생성되게 한다. 지금은 번들 안에서 두 문서가 서로 다른 계약을 말하고, 존재하지 않는
테이블을 게시 대상으로 지목하며, 아무것도 검사하지 않는 테스트를 "통과시키라"고 지시한다.

## 배경 — POQSettleProc9 산출물 실측

18단계·12 프로시저 규모의 실제 산출물을 외부 에이전트 위임 관점에서 검토한 결과,
넘기기 전에 사람이 막아야 하는 결함이 나왔다. 아래는 전부 실측이다.

| # | 결함 | 근거 |
|---|---|---|
| ① | 강제 상속 대상인 스텁과 계획서의 실행 계약이 다르다 | `agent/src/AbstractSettleTasklet.cs:71-80` vs `agent/common/00-architecture.md:33-48` |
| ② | `batch`·`batch_shadow` 스키마 객체 **67종**을 SQL이 참조하는데 만드는 회차가 없다 | `agent/` 전체 grep (`batch.` 45종, `batch_shadow.` 22종), `agent/task-00-bootstrap.md`에 언급 없음 |
| ③ | S17이 존재하지 않는 테이블을 게시 대상으로 지목한다 | `agent/steps/S17.md:3,92-95`의 `dbo.TSettleSummary` — `raw/ddl/` 55종에 없음 |
| ④ | 자가 검증용 단위 테스트가 빈 껍데기다 | `agent/tests/StepLogicTests.cs` 전체 18줄, 본문이 `// Arrange` 세 줄 |
| ⑤ | 아키텍처 테스트가 단일 어셈블리만 스캔한다 | `agent/tests/ArchitectureTests.cs:23` `typeof(...).Assembly` |
| ⑦ | 계획서 자신이 생략 지시 주석을 시범 보인다 | `agent/common/01-step-contract.md:214,347`, `agent/steps/S06.md:41,46,53` 등 |

(⑥ SQL 배치 위치의 이중성과 S01 목차 공백도 함께 관찰됐다. 프롬프트 영역이라 이 설계의
범위 밖이다. 아래 "제외" 참조.)

### ①이 왜 가장 무거운가

`MigrationInstructions.md`는 규칙 9로 *"모든 Tasklet은 `src/AbstractSettleTasklet.cs`를
강제로 상속하고 임의의 구조를 만들지 마라"* 고 못 박는다. 그런데 그 스텁이 제공하는 것과
단계 문서가 요구하는 것이 다르다.

| | 스텁 (수정 금지·상속 강제) | `common/`·`steps/` 전체 |
|---|---|---|
| 실행 | 동기 `Execute(SettleContext)` | 비동기 `ExecuteAsync(context)` |
| 컨텍스트 | `Ymd`, `BypassPreCheck`, 팩토리 4, `Checkpoint` | `RunId`, `InputHash`, `SourceSnapshotId`, `StepVersion`, 날짜 6종 |
| 결과 | `StepResult{Code,Message,SourceProcName}` | `SettlementStepResult` 15필드 + `StepExecutionStatus` |
| 체크포인트 | `IsStepCompleted(stepName, ymd)` | `GetAsync/MarkRunningAsync/MarkSucceededAsync(RunId)` |

단계 문서 18개 중 스텁을 언급하는 문서는 **0개**다. 유일한 접점이
`common/02-data-access-boundary.md:23`의 `RunBusinessSteps` 한 줄이다.

특히 `SettleContext`에 `RunId`가 없는데, 계획서는 Shadow 이름 규칙
(`batch_shadow.<Table>_<RunId>_<StepCode>`), 체크포인트 키, 오류 로그, 게시 Manifest를
전부 RunId 기반으로 설계했다. 회차마다 다른 에이전트가 각자 다르게 우회하고, 회차 간
코드가 어긋난다.

### 이것은 문서 결함이 아니라 프로그램 결함이다

`output/Jobs/` 아래는 손으로 고치는 파일이 아니다. ReSet 실행이 만든다. 결함을 문서에서
고치면 다음 Job에서 같은 것이 그대로 재발한다. 그래서 각 결함이 태어나는 코드 지점을
찾아 거기서 막는다.

### 재료는 이미 있다

두 가지가 착수 비용을 크게 낮춘다.

- **③의 카탈로그 배선이 불필요하다.** `VerificationPipelineOrchestrator.cs:1683`의
  `RunConsolidatedPipelineAsync`가 이미 `IReadOnlyList<SpDefinition>? definitions`를 받는다.
  번들 시점의 DDL 카탈로그(`InstructionBundleWriter.cs:503`
  `WriteDependencySchemasAsync`)도 같은 `SpDefs.SelectMany(sp => sp.Dependencies)`에서 나온다.
  두 소비자가 같은 원천을 쓰므로 새 배선 없이 검사 시점을 앞당길 수 있다.
- **같은 모양의 선례가 있다.** `VerificationPipelineOrchestrator.cs:2833`
  `FindUncoveredProcedures`는 "목차가 명세서를 다 덮는가"를 정적 메서드로 검사하고
  `VerificationBanner.UncoveredProcedures`로 표면화한다. ③·⑦은 비교 대상만 바뀐 같은 형태다.

## 범위

**포함**: ①②③④⑤⑦. 고정 자산(스텁·테스트·회차 지시문)의 내용 수정과, 조립된 계획서를
대상으로 하는 L1 검사 두 종.

**제외**:

- **프롬프트 수정 일체.** `AiService`의 골격·단계 섹션·목차 프롬프트는 건드리지 않는다.
  ⑥(SQL을 C# 인라인에 둘지 새 SP에 둘지)과 S01의 빈 `TargetTables`가 여기 걸린다.
  프롬프트는 재생성 결과가 비결정적이라 별도 설계로 다룬다.
- **이미 만들어진 산출물의 소급 수정.** `POQSettleProc9`을 포함해 기존 Job은 다시 돌려야
  새 규칙을 받는다.
- **`batch.*` 모듈의 본문 생성.** ②는 "무엇을 만들어야 하는지 목록을 주는" 데까지다.
  `batch.S06_ApplyCardPromotion` 같은 모듈의 실제 UPDATE 본문은 여전히 에이전트가
  `Spec.md`에서 재구성한다.

---

## 설계 1 — 계약의 권위를 하나로 (①)

두 방향이 가능했다. 스텁을 계획서에 맞춰 비동기·15필드로 키우거나, 스텁을 권위로 삼고
계획서 쪽 표현을 설계 의도로 격하하거나. **후자를 택한다.**

스텁을 키우면 C#·Java 양쪽 8개 파일이 함께 커지고, 단순한 Job에도 같은 무게가 강제된다.
계획서의 풍성한 타입은 Job마다 다르게 생성되므로 스텁이 그것을 따라갈 수도 없다.

### 1.1 스텁에 실행 식별자 세 개만 더한다

계획서가 **이름으로 쓰는 값**만 최소로 넣는다. Shadow 테이블 이름과 체크포인트 키가
그것 없이는 성립하지 않기 때문이다.

```csharp
public class SettleContext
{
    public string Ymd { get; set; }
    public bool BypassPreCheck { get; set; }

    // 계획서가 Shadow 이름(batch_shadow.<Table>_<RunId>_<StepCode>)과
    // 체크포인트 키로 쓰는 값. 이것이 없으면 회차마다 다른 우회가 생긴다.
    public Guid RunId { get; set; }
    public string? InputHash { get; set; }
    public string? SourceSnapshotId { get; set; }

    public IDbConnectionFactory MainDb { get; set; }
    // ... 나머지 팩토리·Checkpoint 동일
}
```

`ExecuteAsync`·`SettlementStepResult`·`StepExecutionStatus`는 **넣지 않는다.** 그것은
설계 의도의 표현이고, 실행 계약은 동기 `Execute` 하나로 유지한다.

Java 스텁(`SettleContext.java`)도 같은 세 필드를 받는다. 두 언어가 다른 계약을 말하면
`AgentContractStubTests`가 이미 고정한 "두 언어 동수" 원칙이 깨진다.

### 1.2 스텁의 거처를 옮긴다

`AbstractSettleTasklet` 스텁은 현재 `MetadataExporter.cs:449~532`에 인라인 문자열로 박혀
있다. `ArchitectureTests`(`DataAccessPolicy.cs:104`)와
`SettleContracts`(`DataAccessPolicy.RepositoryContractStub`)는 `DataAccessPolicy`에 있고
`AgentContractStubTests`가 그 둘을 검사한다.

**즉 `AbstractSettleTasklet`은 테스트가 없는 유일한 계약 자산이다.** 1.1에서 어차피 이
문자열을 건드리므로, 같은 변경에서 `DataAccessPolicy`로 옮겨 나머지 스텁과 한자리에 둔다.
그래야 세 필드에 테스트를 붙일 수 있다.

`MetadataExporter`는 `DataAccessPolicy.AbstractTaskletStub(targetLanguage)`를 호출해
파일로 쓰는 역할만 남긴다. 경계 주석(`stubWithBoundary`) 부착 로직은 그대로 둔다.

### 1.3 진입점에 권위 순서를 명문화한다

`InstructionEntryPointComposer.cs:244` 인근, 규칙 9 바로 뒤에 규칙 10을 넣는다.

> 10. **[중요]** 계획서 본문에 등장하는 `ExecuteAsync`·`SettlementStepResult`·
>     `StepExecutionStatus` 등의 타입은 **설계 의도 설명**입니다. 실제 구현 계약은
>     `src/AbstractSettleTasklet.cs`이며, 둘이 충돌하면 **스텁이 이깁니다.** 스텁에 없는
>     타입이 필요하면 Tasklet 내부에 두고, `common/`이 정의한 공통 계약 파일은 수정하지
>     마십시오.

Java 분기도 같은 문장을 파일명만 바꿔 넣는다.

---

## 설계 2 — 인프라 객체 수집기 (②)

### 2.1 왜 목록을 도구가 줘야 하는가

회차 0은 읽기 계약상 step 파일을 읽을 수 없다(`task-00-bootstrap.md`의 "단계 상세 문서를
읽지 마십시오"). 그래서 "계획서가 참조하는 모든 batch 객체를 만들라"는 **문장만으로는
회차 0이 목록을 알 방법이 없다.** 실명 목록을 박아 줘야 한다.

### 2.2 `BatchInfraObjectCollector` (신규)

조립된 계획서 마크다운을 받아 `batch.` / `batch_shadow.` 식별자를 수집한다.

```csharp
public static IReadOnlyList<string> Collect(string? planMarkdown)
```

규칙:

- 인식 대상은 `batch.<Name>`과 `batch_shadow.<Name>`. 대소문자 무시, 중복 제거, 정렬.
- **Shadow 이름은 정규화한다.** 실측 산출물에 `TSettleMst_RunId_S06`, `TSettleMst_Run_S07`,
  `TSettleMst_Run_S03`가 섞여 있다. 규칙(`<Table>_<RunId>_<StepCode>`)의 자리표시자가
  리터럴로 굳은 것이므로, `_RunId_`/`_Run_` 구간을 자리표시자로 되돌려 한 항목으로 접는다.
  접힌 원문은 함께 보고해 사람이 규칙 위반을 볼 수 있게 한다.
- 코드펜스 안팎을 모두 본다. `EXEC batch.X`(펜스 안)와 산문 언급(`batch.SwitchPublishedPartition`은
  `steps/S17.md:17` 산문에 있다)이 둘 다 실재한다.
- 영어 산문의 "batch" 단어는 `.`이 뒤따르지 않으므로 자연히 걸러진다.

### 2.3 회차 0 산출물에 반영

`InstructionBundleWriter`가 계획서를 이미 손에 들고 있으므로(`slices`), 거기서 수집해
`TaskFileInputs`에 새 필드로 싣는다. `TaskFileComposer.Compose`는 `StageKind.Bootstrap`일
때만 절을 렌더한다.

```markdown
## 이번 회차에서 만들 인프라 스키마 객체

계획서의 SQL이 아래 객체를 참조합니다. 이 회차에서 DDL과 모듈 본문의 골격을 만드십시오.
단계별 모듈(`batch.S06_Apply*` 등)의 업무 로직 본문은 해당 단계 회차가 채웁니다.

- `batch.POQSettleRun`
- `batch.POQSettleCheckpoint`
- ...
```

**목록이 비면 절 자체를 렌더하지 않는다.** 빈 제목만 남는 것은 "만들 것이 없다"가 아니라
"수집이 실패했다"로도 읽히므로, 아예 없애는 편이 정직하다.

---

## 설계 3 — 카탈로그에 없는 테이블 차단 (③)

### 3.1 검사 위치

`MechanicalValidator.ValidateBatchStep`(`:174`) 안. 문서 전체가 아니라 **단계 섹션 단위**로
잡아, 걸린 섹션만 재생성한다. 재생성 비용이 가장 싸고 피드백이 구체적이다.

### 3.2 판정 규칙

계획서 섹션에서 식별자를 뽑아, 알려진 것 어디에도 없으면 결함으로 본다.

- **식별자 추출 범위**: 백틱 인용과 SQL 코드펜스 안. 맨 산문의 테이블명은 보지 않는다.
  오탐을 막기 위한 의도적 제한이다.
- **모양 조건**: 2부(`dbo.T*`) 또는 3부(`PaymentDB.dbo.T*`) 식별자.
- **알려진 것 1 — 레거시 카탈로그**: `definitions.SelectMany(sp => sp.Dependencies)`에서
  나온 이름. 비교는 `MechanicalValidator.BareObjectName`을 재사용한다
  (`FindUncoveredProcedures`가 이미 같은 규칙을 쓴다 — 별도 구현하면 두 로직이 갈라진다).
- **알려진 것 2 — 신규 인프라**: `batch.*`·`batch_shadow.*`는 **당연히 카탈로그에 없다.**
  `BatchInfraObjectCollector`가 인식하는 접두사를 그대로 제외 목록으로 쓴다. 수집기와
  검사기가 같은 접두사 정의를 공유해야 한다 — 두 곳에서 따로 판단하면 한쪽이 신규 접두사를
  놓쳤을 때 전부 오탐이 된다.

걸리면 `result.Errors`에 넣는다. **`PlanDefects`가 아니다** — 그쪽은 정반대 뜻이다.

`PlanDefects`는 "목차가 원인이라 단계 본문을 다시 생성해도 사라지지 않는 결함"을 담고,
`RegenerationCanFix => Errors.Count > PlanDefects.Count`가 그것으로 재시도 여부를 가른다.
유령 테이블을 거기 넣으면 그 단계는 재생성되지 않고 `Unverifiable`로 건너뛰어져, §3.1이
말한 "걸린 섹션만 재생성한다"가 성립하지 않는다. 이 검사가 잡는 것은 본문이 잘못 쓴
이름이고, 그것은 다시 쓰면 고쳐진다.

(이 문단의 초판은 `PlanDefects`라고 적었다. 실행 중 T56 리뷰어가 잡았고 구현은 처음부터
`Errors`로 되어 있었다 — 계획서의 Task 5 코드와 테스트가 옳았고 이 설계 산문만 틀렸다.)

메시지는 재생성이 실제로 고칠 수 있도록 구체적으로 쓴다.

```
S17: `dbo.TSettleSummary`는 이 작업의 스키마 카탈로그(55종)에도, 이 계획서가 만드는
batch 스키마 객체에도 없습니다. 실재하는 대상 테이블로 바꾸거나, 신규 객체라면
batch 스키마에 두십시오.
```

### 3.3 배선과 소프트 스킵

`VerificationPipelineOrchestrator.cs:3109` 호출부가 카탈로그를 넘긴다.

`definitions`가 null인 경로(오프라인 스냅숏 등)에서는 **검사를 실행하지 않고 결함 0건으로
지나간다.** AGENTS.md 범주 2의 소프트 페일 정책이다. 카탈로그가 없다는 사실은 로그로
남기되, 그것 때문에 계획서 생성이 죽어서는 안 된다.

---

## 설계 4 — 생략 지시 주석 배너 (⑦)

### 4.1 왜 차단이 아니라 배너인가

`MigrationInstructions.md` 규칙 7은 에이전트에게 `// TODO` 같은 자리표시자를 금지한다.
그런데 계획서 자신이 그 형태를 시범 보인다.

```sql
-- 나머지 실제 컬럼도 원본 순서가 아닌 명시적 이름으로 모두 기술   (steps/S06.md:46)
-- 원본 자기조인과 상태 조건을 유지한 집계 INSERT 수행           (common/01-step-contract.md:347)
```

다만 이것은 정도 문제다. 같은 자리에 `-- 원본 필터 YMD = @pi_strYMD AND USESTATE = 2를
모두 유지한다`(`steps/S13.md:167`)처럼 **생략이 아니라 지시**인 주석도 있다. 기계가 둘을
완벽히 가르기 어렵다. 재생성을 걸면 모델이 표현만 바꿔 우회하며 재시도만 소모할 위험이 크다.

그래서 배너로 남겨 사람이 판단하게 한다.

### 4.2 왜 기존 배열에 끼워 넣지 않는가

`MechanicalValidator.cs:970`의 `forbiddenShortcuts`는 `ValidateConsolidated`의 `Errors`
경로다. 거기 추가하면 자동으로 차단이 된다. 배너로 가려면 별도 정적 검사와
`VerificationBanner`의 새 종류가 필요하다.

### 4.3 탐지 패턴

코드펜스 **안**의 주석 줄(`--`, `//`)만 본다. 좁게 시작한다.

- `나머지 …도` + (`기술`|`적용`|`같은`)
- `… 모두 기술`
- `위 … 동일한`

`유지한다`·`보존한다`로 끝나는 지시 주석은 잡지 않는다. 오탐 비용은 낮지만(배너일 뿐),
배너가 잦으면 사람이 읽지 않게 되므로 좁게 유지한다.

**이 제외 규칙을 고정하는 테스트는 패턴에 걸리는 입력을 써야 한다.** 어느 패턴에도 걸리지
않는 문장으로 "배너 없음"을 단언하면 제외 규칙을 통째로 지워도 초록이다 — 실패할 수 없는
테스트는 방어가 아니다. 걸리는 입력은 예컨대 `-- 나머지 컬럼도 같은 방식으로 유지한다`처럼
`같은`으로 패턴에 걸리면서 `유지한다`를 함께 담은 것이다.

(이 설계의 초판과 계획서가 정확히 그 실수를 했다. 실행 중 T7 구현자가 발견하고 리뷰어가
제외 규칙을 지워 재현했다. 이 설계가 ④에서 고치려는 결함 — 실패할 수 없는 테스트가 방어로
계산되는 것 — 이 설계 자신 안에서 재발한 셈이다.)

---

## 설계 5 — 테스트 자산 (④⑤)

### 5.1 `StepLogicTests` — 스캐폴드로 바꾼다

현재 스텁은 본문이 주석 세 줄이라 아무것도 보장하지 않는다. 그런데 규칙 6은 *"제공된
자가 검증용 단위 테스트를 통과(PASS)시키라"* 고 말한다. **빈 테스트를 방어로 착각하는
구조다.**

이 저장소는 같은 결함을 이미 한 번 고쳤다. `AgentContractStubTests`의
`ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut`에 *"이전 스텁은 본문이 전부 주석이라
통과해도 아무것도 보장하지 않았다"* 고 적혀 있다. `StepLogicTests`에만 적용이 안 됐다.

스텁을 **회차가 채우도록 요구하는 형태**로 바꾼다. 빈 채로 통과하지 않도록, 스캐폴드
자신이 미구현 상태를 실패로 만든다.

```csharp
public class StepLogicTests
{
    // 이 회차의 단계가 실제로 무엇을 하는지 검증하는 테스트를 여기에 추가하십시오.
    // 최소 한 개: PreCheck 차단 경로 또는 RunBusinessSteps의 대표 분기.
    [Fact]
    public void Step_ShouldHaveAtLeastOneBehaviourTest()
    {
        Assert.Fail("이 회차의 단계 테스트가 아직 없습니다. 이 Fact를 실제 테스트로 교체하십시오.");
    }
}
```

`TaskFileComposer`의 Step 회차 완료 조건에 "이 단계의 동작 테스트가 최소 한 개 통과한다"를
넣는다. 회차 0의 완료 조건에는 넣지 않는다 — 그 시점에는 단계가 없다.

**회차별 테스트 파일 이름은 단계 코드로 시작해서는 안 된다.** `FileMappingService.cs:72`는
`name.StartsWith(MappedName)`으로 그 회차의 산출물을 찾는다. 테스트 파일을
`S08LogicTests.cs`로 만들면 **Tasklet이 없어도 이름 게이트가 통과한다** — 회차가 테스트만
쓰고 구현을 빼먹어도 초록으로 보인다. 접미사 형태 `LogicTests_S08.cs`를 지시한다. 이
제약은 스캐폴드 주석과 Step 회차 task 파일 양쪽에 적는다.

### 5.2 `ArchitectureTests` — 스캔 범위를 넓힌다

현재 `typeof(ReSet.Batch.Core.ISettleStep).Assembly` 하나만 본다
(`agent/tests/ArchitectureTests.cs:23`). 회차 0이 지시받은 헥사고날 구조를 다중 프로젝트로
만들면 Tasklet과 Domain 타입이 다른 어셈블리에 있게 되어 **규칙 1·2·3·4가 대상 0건으로
조용히 통과한다.** 아키텍처 지시와 검사 방식이 서로를 무력화한다.

스캔 대상을 단일 어셈블리에서 **로드된 어셈블리 집합**으로 넓힌다. NetArchTest의
`Types.InCurrentDomain()`을 쓰고, 프로젝트 접두사(`ReSet.Batch`)로 걸러 서드파티
어셈블리까지 훑지 않게 한다. 리플렉션 기반 규칙(규칙 1·4)도 같은 집합을 순회한다.

0건 판정(대상이 하나도 없으면 실패)은 **조립 회차에서만** 켠다. 회차 0에는 Tasklet이 0개인
것이 정상이므로, 무조건 켜면 부트스트랩이 부당하게 실패한다.

스텁은 자신이 몇 회차에 놓이는지 알 수 없다. 그래서 같은 파일에 조건부 Fact를 두는 대신
**파일을 나눈다**: `tests/AssemblyCompletenessTests.cs`를 별도로 생성하고, 그 파일을
프로젝트에 배치하라는 지시는 **`task-99-assembly.md`에만** 넣는다. 회차 0의 배치 목록에는
없으므로 부트스트랩에서는 존재하지 않는다. 회차별 배치 지시가 곧 활성화 스위치다.

---

## 오류 처리

이 설계가 추가하는 모든 경로는 AGENTS.md 범주 2를 따른다.

| 상황 | 처리 |
|---|---|
| `definitions`가 null | ③ 검사 미실행, 결함 0건, 경고 로그. 생성은 계속 |
| 계획서 파싱 실패 | ② 수집 결과 빈 목록. 회차 0에 해당 절을 렌더하지 않음 |
| 수집기·검사기 자체 예외 | try-catch로 격리, 소프트 패스. 파이프라인을 죽이지 않음 |
| 취소 | `OperationCanceledException`은 소프트 페일 대상이 아님. `when (ex is not OperationCanceledException)` 필터 필수 (`CancellationPolicyTests`가 Roslyn으로 자동 검사) |

---

## 테스트

이 저장소의 관행 세 가지를 그대로 쓴다: 스텁 내용 고정 테스트(`AgentContractStubTests`),
Roslyn 배선 스캐너(`SpecExpectationsWiringPolicyScanner`), 정책 테스트(`CancellationPolicyTests`).

### 스텁 (①④⑤)

`AgentContractStubTests`를 확장한다.

- `SettleContext` 스텁에 `RunId`·`InputHash`·`SourceSnapshotId`가 있다 — C#·Java 각각.
- 스텁이 `ExecuteAsync`를 **선언하지 않는다** — 1.1의 "최소 확장" 결정을 고정한다. 나중에
  누군가 계획서를 보고 비동기를 끼워 넣으면 이 테스트가 결정을 상기시킨다.
- `StepLogicTests` 스텁이 전부 주석이 아니다 — 기존
  `ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut`의 짝. 미구현 상태가 실패로
  드러나는지(`Assert.Fail` 상당 표현이 있는지)까지 본다.
- `StepLogicTests` 스텁이 회차별 파일명을 **접미사 형태**로 지시한다 — `S<코드>`로 시작하는
  예시를 담지 않는다. `FileMappingService`의 StartsWith 규칙과 충돌하기 때문이다.
- `ArchitectureTests` 스텁이 단일 어셈블리 표현(`typeof(...).Assembly`)만으로 스캔하지 않는다.
- `AssemblyCompletenessTests` 스텁이 생성되고, 0건 판정을 담고 있다.
- 두 언어의 규칙 수가 같다 — 기존 `ShouldExposeTheSameRuleCount_ForBothLanguages` 갱신.

### 진입점·회차 파일 (①②④⑤)

- 규칙 10 문구와 스텁 파일명이 진입점 산출물에 **함께** 나온다 (`InstructionBundleWriterTests`).
- 수집 목록이 회차 0 본문에 렌더된다 / **목록이 비면 절 자체가 없다** (`TaskFileComposerTests`).
- Step 회차 완료 조건에 단계 테스트 요구가 있고, 회차 0 완료 조건에는 없다.
- `AssemblyCompletenessTests` 배치 지시가 **조립 회차에만** 있고 회차 0에는 없다 — 이것이
  0건 판정의 활성화 스위치이므로, 회차 0에 새면 부트스트랩이 부당하게 실패한다.

### 수집기 (②)

신규 `BatchInfraObjectCollectorTests`.

- `EXEC batch.X`(펜스 안) + `batch.Y`(산문) 혼재 → 둘 다 수집.
- `TSettleMst_RunId_S06` / `TSettleMst_Run_S07` → 한 항목으로 접히고, 접힌 원문이 보고된다.
- 영어 산문 `"the batch job"` → 수집되지 않는다.
- 빈 입력·null → 빈 목록, 예외 없음.

### 미지 테이블 검사 (③)

`MechanicalValidator` 테스트.

- 카탈로그에 없는 `dbo.TSettleSummary` → `Errors` 1건이고 `RegenerationCanFix`가 참.
- `batch.POQSettleRun` → 0건 (신규 인프라이므로).
- 카탈로그에 있는 `dbo.TSettleMst` → 0건.
- 맨 산문의 테이블명 → 0건 (추출 범위 제한 고정).
- 카탈로그가 비어 있음 → 0건 (소프트 스킵).

### 배선 (③) — 이 설계에서 가장 중요한 테스트

신규 `KnownTableWiringPolicyScanner` + Tests. `SpecExpectationsWiringPolicyScanner`와 같은
모양이다.

`ValidateBatchStep(...)` 호출이 카탈로그 인자 없이 떨어지면 실패한다. 이 스캐너가 없으면,
호출부가 하나만 인자를 빠뜨려도 **그 경로에서만 검사가 조용히 꺼진다** — 이 저장소가
`_validator.Validate`에서 이미 겪은 실패 모드다.

### 배너 (⑦)

`VerificationBannerTests` 확장.

- `-- 나머지 실제 컬럼도 … 모두 기술` → 배너.
- `-- 나머지 컬럼도 같은 방식으로 유지한다` → **배너 없음.** 패턴에 걸리면서 제외어를
  담은 입력이라, 제외 규칙을 지우면 이 케이스가 붉어진다 — 경계를 실제로 고정하는 것은 이것뿐이다.
- `-- 원본 필터 … 를 모두 유지한다` → 배너 없음(예시로 남기되, 어느 패턴에도 걸리지 않아
  경계를 고정하지는 못한다).
- 코드펜스 밖의 같은 문장 → 배너 없음.

---

## 완료 기준

1. `dotnet test`가 전부 통과한다.
2. 신규 Job을 한 번 생성해 아래를 눈으로 확인한다.
   - `agent/src/` 스텁에 세 필드가 있다.
   - `agent/MigrationInstructions.md`에 규칙 10이 있다.
   - `agent/task-00-bootstrap.md`에 인프라 객체 목록이 있다(그 Job이 batch 객체를 쓴다면).
   - `agent/tests/StepLogicTests`가 빈 껍데기가 아니다.
3. `POQSettleProc9`을 재생성해 `dbo.TSettleSummary`가 사라졌는지 확인한다. 남아 있으면
   ③의 추출 범위가 좁았다는 뜻이므로 규칙을 재검토한다.

---

## 사람이 직접 확인해야 하는 것

- **`RunId`를 실제로 채우는 것은 회차 0의 몫이다.** 스텁은 자리를 만들 뿐이고, DI가 그것을
  채우는지는 이 설계가 강제하지 않는다. 첫 재생성에서 회차 0 산출물을 봐야 한다.
- **②의 목록이 과하거나 모자란지.** 67종은 POQSettleProc9의 수치다. 다른 Job에서 수집기가
  무엇을 놓치고 무엇을 과하게 잡는지는 실측이 필요하다.
- **⑦ 배너의 빈도.** 배너가 매 Job마다 수십 건 뜨면 사람이 읽지 않게 된다. 그때는 패턴을
  더 좁히거나, 반대로 차단으로 승격할지 다시 판단해야 한다.
- **③이 재생성으로 실제로 고쳐지는지.** 결함 메시지를 받은 모델이 유령 테이블을 실재하는
  이름으로 바꾸는지, 아니면 이름만 바꿔 다른 유령을 만드는지는 돌려 봐야 안다.

## 남은 후속

- ⑥ SQL 배치 위치의 이중성 — 계획서가 C# 인라인 SQL과 신규 저장 프로시저를 섞어 말한다.
  프롬프트 설계 사안.
- S01의 빈 `TargetTables` — 목차 생성 프롬프트 사안. `PlanStructureEnricher`가 채우지
  못하는 단계가 왜 생기는지부터 봐야 한다.
- `batch.S06_Apply*` 같은 모듈의 본문 — 계획서가 이름만 주고 본문을 주지 않는다. 규칙 7과
  충돌하지만, 해결하려면 단계 섹션 프롬프트를 고쳐야 한다.
