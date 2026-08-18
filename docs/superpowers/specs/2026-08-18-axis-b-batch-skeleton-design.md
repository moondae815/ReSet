# 축 B 배치 골격 계약 설계 — `Spec.md` ↔ 단계 지시서

**작성일**: 2026-08-18
**상태**: 설계 확정

## 목표

단계 지시서 18개가 **하나의 배치 골격을 말하도록** 생성기를 고친다. 지금은 프롬프트가
원본에 없는 파라미터를 발명하라고 명령하고, Few-Shot 예시가 자기 규칙과 반대되는 코드를
가르치고, 제어 테이블의 컬럼명·상태 어휘·행 생성 지점을 아무도 정하지 않는다.

산출물을 손으로 수선하지 않는다. **다음 회차부터 같은 결함이 생기지 않게** 하는 것이 목표다.

## 배경 — POQSettleProc16 정합성 감사 실측

[`output/Jobs/POQSettleProc16/consistency/ConsistencyReport.md`](../../../output/Jobs/POQSettleProc16/consistency/ConsistencyReport.md)가
단계 18개를 전수 대조해 축 B에서 124건을 냈다(🔴 9 · 🟠 37 · 🟡 48 · ⚪ 30). 18개 **전부**
`결함` 판정이다.

축 A와 무게중심이 다르다. 축 A의 🔴은 1건이고 나머지는 표기·추적성에 몰려 있었지만,
축 B는 🔴 9 · 🟠 37로 무거운 쪽에 쏠려 있다. **결함이 명세서가 아니라 명세서에서 단계
지시서로 가는 구간에 있다.**

### 이 설계가 덮는 범위 — Spec 무관 부류

축 B 결함은 기준값 앵커에 따라 둘로 갈린다.

| | 기준값이 `Spec.md` | 기준값이 배치 골격 자신 |
|---|---|---|
| §5 개별 표 62건 | 37 | 25 |
| §5-1 공통 패턴 | B4 일부 | B1 · B2 · B3 · B6 · B7 · B8 |
| 🔴 9건 중 | 3 | **6** |

`Spec.md` 14개는 2026-08-17 20:17 ~ 2026-08-18 00:03에 축 A 대응으로 전수 재생성됐고,
Job 산출물은 2026-08-16 23:55이다. **Spec 앵커 37건의 기준값은 이미 존재하지 않는다** —
그 부류는 축 A가 수렴한 뒤에 다시 잰다. 이 설계는 재생성이 건드리지 않는 부류만 덮는다.

### 이것은 문서 결함이 아니라 프로그램 결함이다

여섯 패턴을 코드 근거까지 되짚으면 **세 뿌리**로 갈린다. 축 A와 같은 분류다.

| 뿌리 | 성격 | 해당 결함 | 코드 근거 |
|---|---|---|---|
| ① | 프롬프트가 거짓을 심었다 | B1 7건(🔴 1) · B7 5건 | `AiService.cs:1038`, `:1094` |
| ② | 프롬프트가 요구한 적 없다 | B2 9건(🔴 1) · B3 6건(🔴 1) | `ConsolidatedPlanRules`에 제어 테이블 계약 부재 |
| ③ | 지시는 있으나 강제가 없다 | B6 6건(🔴 1) · B8 4건 | 축 B에 골격 층 L1 검사 부재 |

#### ① 프롬프트가 발명한 `@pi_bypassPreCheck`

```
AiService.cs:1038 — ConsolidatedPlanRules 규칙 5
5. [Idempotency & Restartability] You MUST design a Checkpoint-based Step Skip logic.
   ... Provide a `@pi_bypassPreCheck` parameter or explicit skip logic in your pseudocode
   so that completed steps are safely skipped upon restart.
```

원본 SP 여럿은 지급 확정 원장(`OutState IN (1,5) AND OutYMD IS NOT NULL`)이 하나라도 있으면
트랜잭션 시작 전 `-9`로 무조건 중단하며 **우회 수단이 없다**. 단계 지시서들은 그 검사를
`IF @pi_bypassPreCheck = 0` 안에 넣어 조건부로 만들었고, S02가 재시작 모드에서 실행 컨텍스트
전체에 `PiBypassPreCheck = true`를 고정하므로 재개 시작 단계와 그 이후 전부가 `1`을 받는다.
결과: **재시작 경로에서 하드 스톱이 통째로 사라져 지급 확정 정산 원장이 삭제·재생성된다**(🔴).

산출물이 원본에 없는 입력을 지어낸 것이 아니다. **프롬프트가 그 이름까지 적어 명령했다.**

#### ① Few-Shot이 자기 규칙을 이긴다

```
AiService.cs:1040 — 규칙 6-1
   ... update it before each DML with the exact original error code ...,
   and RETURN THAT VARIABLE in the CATCH block to preserve the exact point of failure.

AiService.cs:1089-1095 — Few-Shot "Shadow Table Restore in CATCH block"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    DELETE FROM dbo.TargetTable WHERE BatchDate = @BatchDate;
    INSERT INTO dbo.TargetTable SELECT * FROM batch_shadow....;
    THROW;              ← 규칙 6-1과 정면 충돌
END CATCH
```

규칙 13(`AiService.cs:1049`)은 출력 파라미터를 누락 없이 매핑하라고까지 요구한다. 그런데
예시는 `THROW`로 끝나 `@po_intRetVal` 반환 경로를 없앤다. **B7 5건은 예시 한 줄이 만들었다.**
모델은 산문 규칙보다 코드 예시를 따른다.

#### ② 아무도 정하지 않은 제어 테이블

`BatchObjectSchemaRule`(`AiService.cs:1024`)이 **스키마**(`batch`·`batch_shadow`)는 못박지만
**컬럼명·상태 어휘·행 생성 지점**은 한 마디도 없다. 그리고 결정적으로 —

```
AiService.cs:2726  GenerateBatchStepSectionAsync(step, allSteps, sharedConventions, ...)
```

**단계 18개는 각각 독립된 LLM 호출**이고, 공유되는 `sharedConventions`는
`BatchPlanAssembler.ExtractSharedConventions`가 AI 생성 골격에서 뽑아낸 산문이다. 기계 확정
사실이 아니다. 18번의 호출이 각자 컬럼명을 지어내는 것이 이 구조의 정상 동작이다.

실측 결과가 그대로다 — 같은 `batch.BatchStepJournal`에 대해 S01은 `StepStatus`에
`'Succeeded'`, S02는 `ExecutionStatus`에 `'Completed'`, S03은 `StepStatus`에 `'Completed'`,
S17은 `StepState`를, `integrity-sql.md`는 `j.Status`를 쓴다. **어느 쪽으로 DDL을 만들어도
반대편 단계가 컴파일되지 않는다.**

행 생성 지점도 같다. `INSERT INTO batch.BatchRun`이 번들 전체에 **0건**이고, S03·S06·S17은
자기 저널·체크포인트 행을 만드는 지점 없이 `UPDATE`만 한다. `@@ROWCOUNT` 검사가 있는 곳은
상시 실패하고, 없는 곳은 0행 갱신을 오류 없이 지나간다.

`TaskFileComposer.AppendBootstrap`(`TaskFileComposer.cs:174`)이 회차 0 문서를 쓰는데
**객체 이름만 나열하고 DDL은 없다.** 정의를 단계 문서에 위임하는데, 단계마다 다르게 쓴다.

#### ③ 강제가 없는 그림자 규칙

규칙 4(`AiService.cs:1036`)는 그림자를 넓게 권하고 규칙 11(`:1047`)만 INSERT-only를 좁힌다.
생성 위치·복원 범위·동적 SQL 변수 스코프는 규정도 검사도 없다. 실측 🔴(S04)은 **원본이
`ROLLBACK TRAN` 하나로 복구되는 단계에 그림자를 덧붙였고, 그 덧붙임이 데이터를 파괴한다** —
`BEGIN TRAN` 안에서 만든 `SELECT INTO` 그림자가 롤백과 함께 소멸한 뒤, `CATCH`의 `DELETE`가
자동 커밋으로 이미 복원된 행을 다시 지우고 복원 `INSERT`는 객체 없음 오류로 실패한다.
실패 1회에 기준일 수수료율 5개 테이블이 빈다.

---

## §0 지배 계약

> **재료 하나가 사실을 내고, 프롬프트와 L1이 같은 사실을 소비한다.
> 규칙만 있고 물리는 기계 검사가 없으면 그 규칙은 없는 것과 같다.**

축 A(`2026-08-17-axis-a-spec-fidelity-design.md` §0)를 그대로 계승한다. 다른 점 하나 —
축 A의 재료는 원본 DDL에서 **추출한** 사실이지만, 축 B 재료의 절반은 ReSet이 **정하는**
사실이다. 배치 골격에는 원본이 없다(신설 6단계가 감사에서 `검증 불가`로 출발한 이유).

정하는 쪽이라고 해서 계약이 아닌 것은 아니다. 오히려 정하지 않았기 때문에 15건이 나왔다.

## §1 재료 두 종

| 재료 | 소유 | 내용 | 닫는 결함 |
|---|---|---|---|
| **M1** `BatchControlContract` | ReSet이 정한다 (신규 고정 자산) | 제어 테이블 4종의 컬럼·상태 어휘·행 생성 소유권 | B2 9건 · B3 6건 |
| **M2** `StepInterfaceFacts` | `SqlStaticParser`가 추출한다 (기존 재료 배선) | 단계별 원본 SP 파라미터 = 인터페이스 정본 | B1 7건 · B8 4건 |

### 1.1 M1 — `BatchControlContract`

`DataAccessPolicy`가 생성 번들의 계약 자산을 단독 소유하는 것과 같은 패턴이다
(AGENTS.md 범주 6). 계약 문구를 조립 코드에서 다시 쓰지 않는다.

**대상 테이블 4종.** 모든 Job이 공통으로 갖는 실행 제어 척추이며, B2·B3 15건이 전부
여기 있다.

| 테이블 | 역할 | 행 생성 소유권 |
|---|---|---|
| `batch.BatchRun` | 실행 1건 | **단계 목록의 첫 단계가 `INSERT`**하며 `RunId`를 발급한다 |
| `batch.BatchStepJournal` | 단계 실행 이력 | **각 단계가 시작 시 자기 행을 `INSERT`**한 뒤 종료 시 `UPDATE` |
| `batch.BatchCheckpoint` | 단계 완료 표시 | 같음 |
| `batch.BatchValidationIssue` | 정합성 위반 적재 | 검증 단계가 `INSERT`만 한다 |

**정본 어휘.** 규칙을 하나로 만든다 — 상태 컬럼은 `<대상>Status`, 성공 종료는 `Succeeded`
하나다. `Completed`는 쓰지 않는다(감사에서 `Succeeded`/`Completed` 혼용이 재시작 판정을
상시 차단시켰다).

| 테이블 | 상태 컬럼 | 허용 값 |
|---|---|---|
| `BatchRun` | `RunStatus` | `Running` · `Succeeded` · `Failed` · `Restarting` |
| `BatchStepJournal` | `StepStatus` | `Running` · `Succeeded` · `Failed` · `Skipped` |
| `BatchCheckpoint` | `CheckpointStatus` | `Pending` · `Succeeded` |

시각 컬럼은 `StartedAtUtc` · `CompletedAtUtc`(시간대 모호성 제거), 실패 사유는
`ErrorMessage` 하나, 작업명은 `JobName`(테이블명이 이미 `Batch` 접두라 컬럼에 다시 붙이지
않는다).

**세 소비 지점.** 하나의 사실을 세 곳이 읽는다.

```
BatchControlContract
   ├── RenderDdl()          → TaskFileComposer.AppendBootstrap  (회차 0 문서에 실제 DDL)
   ├── RenderPromptTable()  → AiService.AppendSharedStepContext (18개 단계 프롬프트)
   └── Tables / 조회 API    → MechanicalValidator                (L1 대조)
```

`RenderDdl`이 B3의 절반을 닫는다 — §6-4가 지적한 "번들 어디에도 DDL이 없다"가 사라진다.

### 1.2 M2 — `StepInterfaceFacts`

`SqlStaticParser`가 `ProcedureParameters`로 **이미 확정하고 있다**
(`SqlStaticParser.cs:1130`). 새 추출기를 만들지 않는다. 문제는 이 사실이 Job 단계
프롬프트에 실리지 않는다는 것뿐이다 — `AppendSharedStepContext`(`AiService.cs:2797`)는
jobName · targetLanguage · specs · conventions만 나른다.

18번의 호출이 원본 인터페이스에 대한 기계 사실을 하나도 못 받은 채 규칙 5는 파라미터를
지어내라고 명령한다. **B1과 B8은 같은 뿌리다.**

표의 형태:

| 단계 | 원본 프로시저 | 파라미터 (이것이 전부다) |
|---|---|---|
| S05 | `dbo.UP_UTIL_SETTLE_INS` | `@pi_strYMD varchar(8)` · `@po_intRetVal int OUTPUT` |

레거시 대응이 없는 신설 단계는 원본이 없으므로 이 표에 행이 없다. 그 단계에 대해서는
L1이 인터페이스 검사를 실행하지 않고, **실행하지 않았다는 사실을 로그로 남긴다** —
"대조해서 깨끗함"과 "대조할 것이 없었음"을 결과에서 구별한다(`ValidateBatchStep`의
`ErrorCodes` 선례와 같은 규칙).

## §2 프롬프트 수술 — `ConsolidatedPlanRules`

| 규칙 | 지금 | 바꿀 것 | 닫는 결함 |
|---|---|---|---|
| 5 (`:1038`) | "`@pi_bypassPreCheck` 파라미터를 **제공하라**" | 파라미터 발명 지시 삭제. "단계 인터페이스는 M2 표가 전부다. 재시작을 위해 입력을 추가하지 마라. 이미 완료된 단계는 오케스트레이터가 체크포인트를 보고 **호출하지 않는다**. 업무 보호 검사는 원본 그대로 항상 수행한다" | B1 · B8 |
| 4 (`:1036`) · 11 (`:1047`) | 4는 넓게 권하고 11만 좁힘 | 판정 트리로 통합(§2.1) | B6 |
| Few-Shot (`:1094`) | `THROW;`로 종료 | 상태 변수를 출력 파라미터에 넣고 `RETURN`. `THROW`는 반환 경로를 갖춘 뒤에만 | B7 |
| 신규 | — | M1 계약 표 + "이 컬럼명·상태값 외의 것을 쓰지 마라" | B2 |
| 신규 | — | M1 행 생성 소유권 + "자기 소유 행은 `INSERT`한 뒤 전이하라. `UPDATE`만 하지 마라" | B3 |
| 2 (`:1028`) | H2 형식만 지정 | 집계 비교 검증식에 `CROSS JOIN` 금지 + 독립 부질의/CTE 비교 예시 | B4 🔴 |

### 2.1 그림자 판정 트리

```
단일 트랜잭션으로 끝나는가?
  ├─ 예   → ROLLBACK TRAN만. 그림자 금지. CATCH에서 삭제·복원 금지.
  └─ 아니오(청크 커밋 / 집계 재구축)
        → 그림자 허용. 단, 셋 다 강제:
           (a) BEGIN TRAN 앞에서 생성한다 — 트랜잭션 안의 SELECT INTO는 롤백과 함께 소멸한다
           (b) 복원 DELETE 범위 = 원래 삭제 범위 — WHERE 없는 전량 삭제 금지
           (c) EXEC() 안에서 바깥 배치의 변수를 참조하지 않는다 — sp_executesql 매개변수로 전달
```

(a)가 S04 🔴을, (b)가 S12 🟠(당일 외 거래일까지 되돌림)을, (c)가 S11 🟠(스칼라 변수 미선언
오류로 차액정산 행이 하나도 생성되지 않음)을 닫는다.

### 2.2 B4의 취급

B4 11건 중 **카티전 곱 1건(🔴)만** 이번 설계에 넣는다. `TSettleMst CROSS JOIN TSettleByTX`
뒤 `HAVING SUM(M.TXAMT) <> SUM(T.TXAMT)`는 양변이 각각 상대 건수배가 되어 정상 데이터에서
항상 불일치하며, 그 결과가 S16 → S17 공개 상시 차단으로 이어진다. **`Spec.md` 없이 판정되는
순수 기계 결함**이므로 축 A 수렴을 기다릴 이유가 없다.

나머지 10건("레거시가 정상적으로 만드는 상태를 위반으로 판정")은 레거시 불변식 여부를
`Spec.md`로 판정해야 하므로 대기한다.

## §3 L1 검사 6종 — `MechanicalValidator`

`ValidateBatchStep`(`MechanicalValidator.cs:198`)에 5종, `ValidateConsolidated`(`:144`)에 1종.

| 검사 | 결과가 실리는 곳 | 잡는 것 |
|---|---|---|
| 단계 인터페이스 | `StepValidationResult.Errors` | 본문이 M2에 없는 파라미터를 선언 (B1 · B8) |
| 제어 어휘 | `StepValidationResult.Errors` | 제어 테이블에 M1 밖의 컬럼명·상태값 (B2) |
| 제어 행 출처 | `StepValidationResult.Errors` | 자기 소유 제어 행을 `UPDATE`만 하고 `INSERT`가 없음 (B3) |
| 그림자 계약 | `StepValidationResult.Errors` | TRAN 안 `SELECT INTO` · `WHERE` 없는 복원 삭제 · `EXEC()` 바깥 변수 (B6) |
| 반환 경로 | `StepValidationResult.Errors` | `CATCH`가 출력 파라미터 설정 없이 `THROW`로 종료 (B7) |
| 검증식 | `ErrorType.VerificationCartesianComparison` | 검증 SQL의 `CROSS JOIN` + 양측 `SUM` 비교 (B4 🔴) |

앞의 다섯은 단계 본문이 대상이라 `StepValidationResult.Errors`(문자열 목록)에 실린다 —
`ErrorType`은 `ValidationResult.DetailedErrors` 쪽 어휘라 여기서는 쓰지 않는다. `Errors`에
실으면 `SuggestedPromptFix`가 그대로 재생성 프롬프트의 `[Previous Attempt Rejected]`로
넘겨 주므로, 검사가 곧 교정 지시가 된다.

여섯 번째는 계획서 전체가 대상이다. 검증 SQL 슬라이스(`## 통합 데이터 정합성 검증 SQL 세트`,
`InstructionBundleWriter.cs:102`가 `integrity-sql.md`로 내보내는 것)는 단계 본문이 아니라
계획서 본문에 있으므로 `ValidateConsolidated`가 보고, 여기서는 `ErrorType`을 하나 늘린다.

**교차 단계 검사를 만들지 않는다.** M1이 정본이므로 각 단계를 정본과 개별 대조하면 서로
간의 일치는 따라온다. 18개 문서를 한꺼번에 읽는 기계는 필요 없고, 기존 per-step 재생성
루프(`VerificationPipelineOrchestrator.cs:3182`)에 그대로 얹힌다. 재생성으로 고칠 수 있는
결함이므로 `PlanDefects`가 아니라 `Errors`로 든다.

## §4 캐시 제약

새 표 둘은 **반드시 `AppendSharedStepContext`(공유 접두사)**에 들어간다. M2는 단계별로
다르지만 **전 단계 표를 통째로** 실어 18호출에 대해 바이트 동일하게 유지한다.

단계별로 자기 것만 실으면 접두사가 매 호출 달라져 프롬프트 캐시가 전부 미스가 되고,
입력 토큰이 1배에서 18배로 뛴다. **산출물은 그대로라 코드만 봐서는 알 수 없는 종류의
실패다**(`architecture.md §4.13`, AGENTS.md의 `GenerateBySplitAsync` 워밍 선례와 같은 함정).

`AppendSharedStepContext`의 `<summary>`에 이미 같은 경고가 있다. 그 계약을 깨지 않는다.

## §5 파일 구조

| 파일 | 책임 | 변경 |
|---|---|---|
| `src/ReSet.Core/Services/BatchControlContract.cs` | M1 정본 — 테이블·컬럼·상태 어휘·행 소유권, `RenderDdl`/`RenderPromptTable` | **신규** |
| `src/ReSet.Core/Services/StepInterfaceFacts.cs` | M2 — `BatchStepPlan` × `ProcedureParameters` → 단계별 인터페이스 표 | **신규** |
| `src/ReSet.Core/Services/AiService.cs` | `ConsolidatedPlanRules` 수술, `AppendSharedStepContext`에 표 둘 배선 | 수정 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 검사 6종 + `ErrorType` 6개 | 수정 |
| `src/ReSet.Core/Services/TaskFileComposer.cs` | `AppendBootstrap`에 `RenderDdl()` 주입 | 수정 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | M2를 `ValidateBatchStep`에 전달, 검증 슬라이스 검사 배선 | 수정 |
| `tests/ReSet.Core.Tests/BatchControlContractTests.cs` | M1 단위 테스트 | **신규** |
| `tests/ReSet.Core.Tests/StepInterfaceFactsTests.cs` | M2 단위 테스트 | **신규** |
| `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs` | 실물 코퍼스 골든 케이스 | **신규** |
| `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs` | 검사 6종 | 수정 |
| `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` | 프롬프트 렌더·캐시 불변성 | 수정 |

## §6 테스트 전략

**레드-그린 필수.** 모든 검사·재료 변경은 되돌렸을 때 실제로 실패해야 한다.

**골든 케이스.** `AxisAGoldenCaseTests`(206줄) 대응으로 `AxisBGoldenCaseTests`를 둔다.
감사가 실측한 결함 본문을 코퍼스로 박아 6종 검사가 그것들을 실제로 잡는지 고정한다.
최소한 이 여섯은 코퍼스에 들어간다.

| 코퍼스 | 출처 | 기대 |
|---|---|---|
| `IF @pi_bypassPreCheck = 0 AND EXISTS(...)` 가드 | S10 🟠 | `StepInterfaceMismatch` |
| `SET StepStatus = N'Completed'` | S03 🟡 | `BatchControlVocabularyMismatch` |
| `UPDATE batch.BatchStepJournal ... WHERE StepCode='S03'` 단독 | S03 🟠 | `BatchControlRowOriginMissing` |
| `BEGIN TRAN` 뒤의 `SELECT INTO batch_shadow.*` | S04 🔴 | `ShadowBackupContractViolation` |
| `THROW;`로 끝나는 `CATCH` | B7 | `CatchDiscardsReturnCode` |
| `CROSS JOIN` + `HAVING SUM(...) <> SUM(...)` | S16 🔴 | `VerificationCartesianComparison` |

**캐시 불변성 테스트.** 서로 다른 두 단계에 대해 `AppendSharedStepContext` 산출이
바이트 동일한지 단언한다. §4의 함정은 테스트 없이는 조용히 되살아난다.

**경고 기준선 9개.** `dotnet build --no-incremental` 결과가 9를 넘으면 안 된다.

## §7 이 설계가 하지 않는 것

- **§5 표의 Spec 앵커 37건** — 기준값(`Spec.md`)이 축 A 재생성으로 이미 바뀌었다.
  축 A 수렴 후 재감사해서 다시 잰다.
- **B5 `NOLOCK` 전면 제거 7건** — 전부 ⚪이고 "배치가 단독 실행되고 원천에 동시 커밋이
  없다"는 전제부 판단이다. 계약 결함이 아니다.
- **B4 나머지 10건** — 레거시 불변식 여부를 `Spec.md`로 판정해야 한다(§2.2).
- **`BatchSourceWatermark` · `BatchImmutableLedgerBaseline`의 컬럼 확정** — 이 둘은 어느
  원천을 워터마킹하고 어느 원장을 기준선으로 잡는지에 따라 컬럼이 달라지는 **Job 형상**
  객체다. ReSet이 정할 수 있는 사실이 아니므로 스키마·명명 규칙만 적용하고 DDL은 계획서에
  맡긴다. §6-4가 이 둘을 함께 지목했으나 그 부분은 이 설계가 닫지 않는다.
- **Job 재생성** — 축 A가 안정된 뒤 1회만 돌린다. 그 1회에 두 축의 수정이 함께 반영된다.
  이번 작업은 생성기만 고친다.
