# 배치 제어 계약의 공백 — `BatchControlTotal`과 `BatchRunLock`을 정본에 싣는다

> 근거 감사: [`docs/audit-reports/2026-08-24-POQSettleBatch1-축B.md`](../../audit-reports/2026-08-24-POQSettleBatch1-축B.md)
> 층위 분류: [`2026-08-24-axis-b-46-triage-design.md`](./2026-08-24-axis-b-46-triage-design.md) — 이 설계는 그 문서의 **C층 단위**다
> 관측 시점: 2026-08-24, 커밋 `4b07453` 이후

## 1. 문제

`BatchControlContract.Tables`가 고정하는 것은 `batch.BatchRun`·`BatchStepJournal`·
`BatchCheckpoint`·`BatchValidationIssue` 넷뿐이다. 그런데 생성 번들은 계약 밖의 `batch.*` 표를
일상적으로 쓴다. 기준값이 없으니 컬럼 수·값 수 대조가 성립하지 않고, 그래서 **POQSettleBatch1의
신설 단계 4개(S01·S02·S03·S16)가 감사에서 `검증 불가`로 남았다.**

감사가 이 뿌리로 든 결함은 다섯이다 — 분류표의 #10·#11·#24·#25·#36. 대표적으로 S01이
`INSERT INTO batch.ControlTotal VALUES /* RunId, S01, BatchYmd, … */`처럼 컬럼 목록과 값 목록을
주석 자리표시자로만 남긴다. 대조할 기준값이 없으므로 L1이 통과시킨다.

이것은 계약이 존재하는 이유 그 자체가 계약 밖에서 재현된 것이다. 계약 주석이 든 사례는
*"S01은 `StepStatus`를, S02는 `ExecutionStatus`를, S17은 `StepState`를 썼다"* 였다. 같은 병이
계약이 덮지 않는 개념에서 그대로 반복되고 있다.

## 2. 관측

`output/Jobs`의 번들 20개 · 단계 문서 326개에서 `batch.` 한정 토큰을 전수로 뽑았다.
계약 검사가 들어온 것은 2026-08-18이므로, 그 뒤에 생성된 번들 5개(POQSettleBatch1 08-24,
POQSettlePrco20·Proc17·Proc18·Proc19 08-19)를 **현재 동작**으로 따로 본다. 계약 밖 이름은
그 코호트에서도 그대로 나온다 — 과거 유물이 아니다.

### 2-1. 두 개념의 병이 서로 다르다

**통제합계 — 이름과 컬럼이 둘 다 갈린다.**

이름은 `batch.BatchControlTotal`(64회)과 `batch.ControlTotal`(16회)로 둘. 컬럼은 넷으로 갈린다.

| 관측된 컬럼 집합 | 성격 |
| :--- | :--- |
| `RunId, StepCode, ControlName, ExpectedValue, ActualValue, IsMatched, MeasuredAtUtc` | 대조 결과까지 담음 |
| `RunId, StepCode, ControlName, ControlValue, RecordedAt` | 기준값만 담음 |
| `RunId, StepCode, ControlName, RowCount, Amount1, Amount2, Amount3, CreatedAt` | 지표를 컬럼으로 펼침 |
| `RunId, StepCode, BatchYMD, MetricName, MetricValue, CreatedAt` | 이름만 다른 기준값형 |

`RunId`·`StepCode`는 넷 다 갖는다. 갈리는 것은 지표 이름·값·시각의 표현이다.
셋째 변이는 `RowCount`를 컬럼명으로 쓰는데 T-SQL 예약어라 대괄호 없이는 구문 오류다 —
감사 5-1절 (나) 부류와 같은 함정이 계약 밖에서 재현된 것이다.

**실행 잠금 — 이름은 이미 수렴했고 컬럼만 갈린다.**

`batch.BatchRunLock` 125회, 다섯 코호트 전부 단일 이름. 동의어는 관측되지 않았다.

| 축 | 관측된 변이 |
| :--- | :--- |
| 잠금 키 날짜 | `BatchYmd` · `ProcessingYmd` · `BusinessDate` · `BatchDate` |
| 소유자 | `RunId` · `OwnerRunId` · `LockOwnerRunId` |
| 상태 | `LockStatus` · `LockState` |
| 획득 시각 | `AcquiredAt` · `AcquiredAtUtc` · `AcquiredUtc` |
| 하트비트 | `LastHeartbeatAt` · `HeartbeatUtc` · `HeartbeatAtUtc` |

### 2-2. 결정을 가른 실물 확인 둘

**하트비트는 실물이다 — 다만 증거가 최근 코호트에 얇다.** 번들 7개(Proc9·10·11·14·15·16·19)가
하트비트를 언급하고, 그중 여럿이 `UPDATE batch.BatchRunLock … HeartbeatUtc = SYSUTCDATETIME()`
형태로 실제로 **갱신**한다 — 선언만 있고 소비되지 않는 컬럼이 아니다. 다만 계약 검사 이후
코호트 5개 중에서는 `Proc19` 하나뿐이다. 그래서 이 컬럼은 `NULL` 허용으로 두어 쓰지 않는
Job이 비워 둘 수 있게 한다. 빼면 하트비트 기반 잠금 회수를 쓰는 Job이 어휘 검사에 걸려
그 설계 자체가 막히므로, 넣되 강제하지 않는 쪽을 고른다.

**해시 통제값은 실물이 아니다.** `S03.md`의 자리표시자 주석 한 줄
(`건수·금액·해시 통제값을 기록한다`)이 전부이고 값을 기록하는 자리가 없다. 건수·금액만
실질이므로 값 컬럼은 숫자 하나로 족하다.

**기준값과 대조 결과는 산출물에서 이미 분리돼 있다.** S16은 `batch.ControlTotal`을
*"단계별 기준값"* 으로 읽고, 대조 결과(*"검증명, 기대값, 실제값, 일치 여부"*)는
`batch.ValidationResult`에 쓴다(`S16.md:3,72`). 통제합계 표가 대조 결과까지 담을 이유가 없다.

## 3. 결정

**계약에 완전히 고정한다** — 기존 네 표와 같은 방식으로 컬럼·타입·허용값·행 출처·기본 키를
`ControlTable` 레코드에 박는다. 프롬프트 표·부트스트랩 DDL·어휘 검사가 모두 `Tables`를
순회하므로 소비처는 대부분 따라온다.

골격만 고정하고 Job이 컬럼을 더하게 하는 안은 버렸다. `CheckBatchControlVocabulary`가
*"쓰기 자리에 나온 이름은 정의상 그 표의 것이어야 하므로, known에 없으면 그 자체로 위반"* 으로
짜여 있어서, 확장을 허용하려면 계약 레코드에 축을 하나 더해야 하고 그 순간 이 검사의 단순함이
깨진다. 이름만 고정하고 컬럼을 계획서에 맡기는 안(`BatchSourceWatermark` 방식)은 사실상 현상
유지라 목적에 미달한다.

**정본 이름은 `batch.BatchControlTotal`이다.** 기존 네 표가 전부 `Batch` 접두사를 쓰고
(`BatchRun`·`BatchStepJournal`·`BatchCheckpoint`·`BatchValidationIssue`), 빈도도 64 대 16으로
우세하다.

**`BatchControlTotal`은 기준값 저장소로 좁힌다.** `ExpectedValue`·`ActualValue`·`IsMatched`를
담는 변이는 계약에 이미 있는 `BatchValidationIssue`(`ExpectedValue`·`ActualValue`·`Severity`)와
역할이 겹친다. 2-2절이 확인한 대로 산출물도 이미 그렇게 나뉘어 있다. 이 논거가 네 변이 중
하나를 고를 근거를 만든다 — 나머지 셋은 근거가 빈도뿐이다.

## 4. 두 표의 정의

### `batch.BatchControlTotal`

| 컬럼 | 타입 | Null | 근거 |
| :--- | :--- | :--- | :--- |
| `RunId` | `bigint` | NOT NULL | 관측된 변이 넷 전부에 있다 |
| `StepCode` | `nvarchar(10)` | NOT NULL | 넷 전부에 있다. 기존 계약과 같은 폭 |
| `ControlName` | `nvarchar(64)` | NOT NULL | `ControlName`이 `MetricName`보다 우세 |
| `ControlValue` | `decimal(38,4)` | NOT NULL | 건수와 금액을 함께 담는다 |
| `CapturedAtUtc` | `datetime2(3)` | NOT NULL | 기존 계약의 `~AtUtc` 규약 |

- `Origin` = `ProducerInsertsOnly` — 생산 단계가 INSERT만 하고 전이가 없다.
- `StatusColumn` = 없음.
- `PrimaryKey` = `(RunId, StepCode, ControlName)`.

**값 타입이 `nvarchar(200)`이 아닌 이유.** 통제합계는 숫자 비교가 본질이다. 문자열로 두면
S16의 합계 대조가 문자열 비교가 되어 조용히 틀린다. `BatchValidationIssue.ExpectedValue`가
`nvarchar(200)`인 것과 갈리지만, 그쪽은 사람이 읽는 오류 기록이고 이쪽은 기계가 비교하는
기준값이라 역할이 다르다.

**기본 키를 두는 것은 계약 주석의 규칙에 대한 예외다.** 주석은 `ProducerInsertsOnly` 표에
PK를 두지 않는다고 적고 이유를 *"한 단계가 같은 `IssueCode`를 여러 번 낼 수 있어 자연 키가
없다"* 로 들었다. 통제합계는 다르다 — 같은 실행의 같은 단계가 같은 지표를 두 번 낼 이유가
없고, 두 번 나면 S16이 어느 행을 기준으로 삼을지 모른다. PK가 그것을 막는다.

### `batch.BatchRunLock`

| 컬럼 | 타입 | Null | 근거 |
| :--- | :--- | :--- | :--- |
| `JobName` | `nvarchar(128)` | NOT NULL | `BatchRun.JobName`과 같은 폭 |
| `BatchYmd` | `varchar(8)` | NOT NULL | `BatchRun.BatchYmd`와 같다. 변이 넷 중 이것 |
| `OwnerRunId` | `bigint` | NOT NULL | PK가 아닌 소유자 참조라 `RunId`와 이름을 갈라야 혼동이 없다 |
| `LockStatus` | `nvarchar(20)` | NOT NULL | 허용값 `Held` / `Released` |
| `AcquiredAtUtc` | `datetime2(3)` | NOT NULL | |
| `HeartbeatAtUtc` | `datetime2(3)` | NULL | 2-2절에서 실물 확인 |
| `ReleasedAtUtc` | `datetime2(3)` | NULL | |

- `Origin` = `FirstStepInserts` — 첫 단계가 획득(INSERT)하고 뒤 단계가 해제(UPDATE)한다.
- `StatusColumn` = `LockStatus`.
- `PrimaryKey` = `(JobName, BatchYmd)` — 같은 Job·같은 영업일에 잠금이 둘일 수 없다는 것이
  이 표의 존재 이유이므로 PK가 그것을 강제한다.

**`RenderPromptTable`의 `FirstStepInserts` 문구를 함께 고쳐야 한다.** 현재 문구가
*"RunId is issued by IDENTITY, so read it back with SCOPE_IDENTITY()"* 를 무조건 싣는다.
`BatchRunLock`은 같은 행 출처 모양이지만 IDENTITY 컬럼이 없어, 그대로 두면 **프롬프트에 거짓
지시가 실린다.** `Origin` enum을 늘리는 대신 그 문장을 `table.Columns.Any(c => c.IsIdentity)`일
때만 붙인다. `BatchRun`은 `RunId.IsIdentity = true`라 문구가 그대로 나오고 `BatchRunLock`에서만
빠진다.

## 5. 동의어 축

`ControlTable`에 `Aliases`를 더한다. 이번 범위의 항목은 하나다 —
`batch.BatchControlTotal`의 별칭 `ControlTotal`.

**필요한 이유.** 정본을 정해도 남은 `batch.ControlTotal` 16회가 **아무 검사에도 걸리지 않는다.**

- `CheckNonCanonicalBatchSchema`는 스키마 이름(`batch`/`batch_shadow`)만 본다. 표 이름은 안 본다.
- `CheckUnknownTableReferences`는 `IsInfraObject`가 `batch.*`를 후보 단계에서 걸러내므로
  `batch.무엇이든`이 통과한다.
- `CheckBatchControlVocabulary`는 `Find()`가 맨이름으로 매칭하는데 `ControlTotal`과
  `BatchControlTotal`은 다른 맨이름이라 `null`을 받고 그 표를 건너뛴다.

즉 계약만 늘리면 정본 이름을 쓴 단계의 컬럼만 고쳐지고 동의어를 쓴 단계는 침묵한다.
계약 위반이 아니라 침묵이라 다음 감사가 같은 자리를 또 든다.

**`Find()`는 정본만 계속 매칭한다.** 별칭까지 매칭하면 `CheckBatchControlVocabulary`가
`batch.ControlTotal`을 정본으로 착각해 컬럼만 검사하고 틀린 이름을 조용히 승인한다. 별칭은
받아들일 것이 아니라 보고할 것이므로 별도 조회(`FindAlias`)와 별도 검사로 가른다. 순서도
그것이 맞다 — 이름을 먼저 정본으로 바꾸게 하고, 그다음 회차에 컬럼 검사가 걸린다.

기존 네 표에도 동의어가 여섯 관측된다(`BatchRunStep`·`BatchStepRun`·`BatchStageRun`·
`BatchTaskRun`·`BatchExecutionJournal`·`BatchStepExecution`이 모두 `BatchStepJournal` 자리다).
**이번 설계에 넣지 않는다** — 카탈로그 B2 부류(제어 테이블 어휘 불일치)의 재확인 과제로
분리한다. 여섯을 함께 조이면 코퍼스 검출량이 크게 늘어 스윕 검증이 이 설계의 몇 배가 된다.

## 6. 소비처 파급

| 소비처 | 변화 |
| :--- | :--- |
| `AiService.cs:4045` 프롬프트 표 | 12행 추가 |
| `TaskFileComposer.cs:210` 부트스트랩 DDL | `CREATE TABLE` 둘 추가(PK·CHECK 포함) |
| `CheckBatchControlVocabulary`(`MechanicalValidator.cs:653`) | 19 + 19단계에서 새로 켜진다(전체 326 중) |
| `CheckAnchoredStatementExtras`(`:5813`, 검사 C) | 예외 목록에 컬럼 이름 8개 추가 |
| `CheckBatchRunRowCreation`(`:6051`) | `BatchRunLock`을 언급하면서 만드는 지점이 없는 계획서를 잡는다 |
| `CheckBatchControlRowOrigin`(`:1376`) | 영향 없음 — `EachStepInserts` 표만 보는데 둘 다 아니다 |

**검사 C의 예외 목록 확대는 무해하다.** 검사 C가 계약의 모든 컬럼 이름을 "명세서가 인정한
이름"으로 취급하므로 새 이름이 레거시 업무 컬럼과 겹치면 검출력이 조용히 떨어진다. 실측하니
`ControlName`·`ControlValue`·`CapturedAtUtc`·`OwnerRunId`·`LockStatus`·`AcquiredAtUtc`·
`HeartbeatAtUtc`·`ReleasedAtUtc` 여덟 개가 `output/Procedures/*/docs/Spec.md` 전체에 **0건**이다.
기존 계약 이름 넷(`RunId`·`StepCode`·`JobName`·`BatchYmd`)도 0건으로 같은 패턴이다.
잃는 검출이 없다.

**`CheckBatchRunRowCreation`의 부수 효과는 의도한 것이다.** 잠금을 해제(UPDATE)만 하고 아무도
획득(INSERT)하지 않는 계획서가 걸린다. 누군가는 잠금을 잡아야 한다.

## 7. 구현 경계

이 설계의 검사 부분은 `MechanicalValidator.cs`에 들어가는데, 그 파일은 병렬로 진행 중인
축 B 단계 검사 회차(계획서 `2026-08-24-axis-b-step-check.md`의 Task 6·7)가 쥐고 있다.
그래서 작업을 자른다.

| 단계 | 파일 | 시점 |
| :--- | :--- | :--- |
| 두 표 정의 · `Aliases` 축 · `FindAlias` · `RenderPromptTable`의 IDENTITY 문구 수정 | `BatchControlContract.cs` | 지금 |
| 계약 단위 테스트 | `BatchControlContractTests.cs` | 지금 |
| 별칭 검사 본체와 `ValidateBatchStep` 배선 | `MechanicalValidator.cs` | Task 6·7 종료 후 |
| 검사 테스트 | **새 파일** | 같음 |

계약 쪽에 판정 함수까지 완성해 두면 배선은 한 줄이다. `MechanicalValidatorTests.cs`는
건드리지 않는다.

**병합은 Task 10(POQSettleBatch1 재생성 실측)이 끝난 뒤다.** 계약 표가 늘면 프롬프트 표가
바뀌어 16단계가 다르게 생성되고, 그 회차가 재려는 "🔴🟠 9건 소멸" 측정에 잡음이 섞인다.

## 8. 검증

**단위 테스트**(`BatchControlContractTests.cs`)

- 두 표가 `Tables`에 있고 컬럼 집합·타입·Null 여부가 4절과 같다.
- `RenderDdl`이 두 표의 PK 제약과 `LockStatus`의 CHECK 제약을 낸다.
- `RenderPromptTable`이 `BatchRunLock` 행에 `SCOPE_IDENTITY` 문장을 싣지 **않고**,
  `BatchRun` 행에는 싣는다.
- `FindAlias("batch.ControlTotal")`가 `BatchControlTotal`을 돌려주고, 같은 이름으로
  `Find()`를 부르면 `null`이다.
- 한정자 유무·대소문자가 달라도 위 둘이 같게 동작한다(기존 `Find`의 계약).

**코퍼스 스윕** — 검사 배선 후에 단계 326개로 돌려 검출량을 잰다. 예상 대상은
`BatchControlTotal` 19단계 · `BatchRunLock` 19단계 · 별칭 16회이고, 검출은 오탐이 아니라 실제
계약 위반일 것이다. **다만 수를 재기 전에는 단언하지 않는다.** 표본으로 실제 결함인지
확인하고, 오탐이 하나라도 있으면 그 원인을 반영한 뒤 다시 돈다.

## 9. 담지 않는 것

- **`batch.ValidationResult`와 `batch.SourceSnapshot`.** 분류표 #11(스냅샷 계약)과 S16의 대조
  결과 표가 그 자리다. `SourceSnapshot`은 이름이 셋으로 갈려(`SourceSnapshot`·`SourceCutoff`·
  `POQSettleSourceSnapshot`) 어느 형상이 정본인지 근거가 약하고, 계약 주석이 `BatchSourceWatermark`를
  *"어느 원천을 워터마킹하는지에 따라 컬럼이 달라지는 Job 형상 객체"* 라며 제외한 것과 같은
  부류일 수 있다. 그 판단이 선행해야 한다.
- **기존 네 표의 동의어 여섯.** 5절 끝에 적은 대로 B2 부류 재확인 과제로 분리한다.
- **분류표 #25(`CompletedAtUtc` 미기록).** 종결 상태 전이 시 완료 시각을 필수로 두는 규칙이
  필요한데, 그것은 `BatchRun` 계약을 건드리는 별개 축이다. 이 설계로 닫히는 것은
  **#10·#24·#36 셋**이고 #11·#25는 남는다.
- **신설 단계 4개의 `검증 불가`가 이 설계만으로 전부 풀리지는 않는다.** `ControlTotal`에
  기준값이 생기면 S01·S16의 그 자리는 대조가 성립하지만, S03은 `SourceSnapshot` 쪽이 남는다.
