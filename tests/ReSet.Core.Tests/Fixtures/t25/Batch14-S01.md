> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**
> 
> S01 (batch.BatchRun의 RunStatus에 계약이 확정한 상태값 `Running`를 이 Job 어느 단계도 기록하지 않습니다 - 같은 값을 StepStatus에는 기록하므로 어휘를 모르는 것이 아닙니다. 그 상태로 끝난 실행이 종료 상태로 가지 못해 다음 회차가 재시작 대상으로 집어 듭니다.)
> 
> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오.

### S01 입력 검증 및 실행 등록

이 단계는 `POQSettleBatch14`의 신규 실행을 등록하고 이후 단계가 사용할 `RunId`를 발급한다. 입력 검증이 성공한 경우에만 `batch.BatchRun`을 생성하며, S02 이후 단계보다 반드시 먼저 단독 실행한다. 동시 실행 배제는 다음 단계인 S02의 `batch.BatchRunLock` 획득으로 처리하므로 이 단계에서 중복 실행 여부를 별도로 추정하지 않는다.

#### 인터페이스 및 입력 계약

- 입력: `batchYmd string -> @p_batchYmd VARCHAR(8)`
- 출력: 데이터베이스가 발급한 `runId long`
- 고정값: `JobName = N'POQSettleBatch14'`
- 재시작·건너뛰기·검증 우회 입력은 받지 않는다.
- `batchYmd`는 공백 제거 또는 자동 보정 없이 다음 조건을 모두 만족해야 한다.
  - NULL이 아니다.
  - 길이가 정확히 8이다.
  - 모든 문자가 ASCII 숫자이다.
  - `yyyyMMdd` 형식의 실제 달력 날짜이며 같은 형식으로 왕복 변환된다.

입력 검증 실패와 실행 등록 실패에는 S01 예약 블록의 일반 실패 코드 `-9010`을 사용한다. `currentStepErrorCode`는 `INT` 의미의 `0`으로 초기화하고 실제 실패 가능 지점 직전에만 `-9010`을 대입한다.

#### C# 배치 제어 의사코드

S01은 SNAPSHOT 격리가 보장된 실행 컨텍스트에서 단일 트랜잭션으로 처리한다. `RunId`는 반드시 `batch.BatchRun`의 IDENTITY와 `SCOPE_IDENTITY()`로 취득하며 애플리케이션에서 계산하거나 재사용하지 않는다.

```pseudocode
S01Result executeS01(string batchYmd):
    currentStepErrorCode = 0
    exactStatementName = N"INPUT_VALIDATE_BATCH_YMD"

    IF batchYmd is null
       OR length(batchYmd) != 8
       OR batchYmd contains a non-ASCII-digit
       OR isValidCalendarYmd(batchYmd) is false:
        currentStepErrorCode = -9010
        fail invocation with:
            statusCode = currentStepErrorCode
            statementName = exactStatementName

    conn = connectionFactory.open()
    assertSnapshotExecutionIsGuaranteed(conn)

    tx = conn.beginTransaction()
    runId = null

    TRY:
        exactStatementName = N"SQL_REGISTER_RUN"
        currentStepErrorCode = -9010
        runId = repository.queryScalar(
            tx,
            SQL_REGISTER_RUN,
            { p_batchYmd: batchYmd encoded as VARCHAR(8) }
        )

        IF runId is null OR runId <= 0:
            fail with exactStatementName

        // S01은 RunId가 INSERT 후에야 존재하므로 같은 트랜잭션 안에서
        // 발급된 값을 사용하여 공통 제어 행을 생성한다.
        exactStatementName = N"SQL_STEP_JOURNAL_START"
        currentStepErrorCode = -9010
        repository.execute(
            tx,
            SQL_STEP_JOURNAL_START,
            { p_runId: runId, p_stepCode: N"S01" }
        )

        exactStatementName = N"SQL_CHECKPOINT_START"
        currentStepErrorCode = -9010
        repository.execute(
            tx,
            SQL_CHECKPOINT_START,
            { p_runId: runId, p_stepCode: N"S01" }
        )

        exactStatementName = N"SQL_STEP_JOURNAL_SUCCESS"
        currentStepErrorCode = -9010
        repository.execute(
            tx,
            SQL_STEP_JOURNAL_SUCCESS,
            {
                p_runId: runId,
                p_stepCode: N"S01",
                p_legacyReturnCode: 0
            }
        )

        exactStatementName = N"SQL_CHECKPOINT_SUCCESS"
        currentStepErrorCode = -9010
        repository.execute(
            tx,
            SQL_CHECKPOINT_SUCCESS,
            { p_runId: runId, p_stepCode: N"S01" }
        )

        tx.commit()
        return S01Result(runId: runId, statusCode: 0)

    ON FAILURE error:
        tx.rollback()

        // 롤백으로 batch.BatchRun과 같은 트랜잭션의 제어 행이 모두 복원된다.
        // 별도 보상 DELETE나 Shadow Table 복구를 실행하지 않는다.
        record bootstrap diagnostic with:
            stepCode = N"S01"
            legacyReturnCode = currentStepErrorCode
            statementName = exactStatementName
            errorDetails = error

        stop pipeline
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_REGISTER_RUN
INSERT INTO batch.BatchRun
(
    JobName,
    BatchYmd,
    RunStatus,
    ResumeFromStepCode,
    StartedAtUtc,
    CompletedAtUtc,
    ErrorMessage
)
VALUES
(
    N'POQSettleBatch14',
    @p_batchYmd,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT) AS RunId;
```

등록 성공 후 `batch.BatchRun.RunStatus`는 `Running`으로 유지하며 최종 성공 전환과 `CompletedAtUtc` 설정은 S16에서 수행한다. 커밋 성공이 확인되기 전에는 `RunId`를 후속 단계에 게시하지 않는다.

#### 트랜잭션·복구 및 재시작 기준

- 비청크 단계이며 페이징 키나 청크 범위를 추가하지 않는다.
- 물리 Shadow Table을 생성하지 않는다.
- 실패 시 단일 트랜잭션을 롤백하며 `batch.BatchRun`에 보상 `DELETE`를 실행하지 않는다.
- 커밋 결과가 불확실한 통신 장애가 발생하면 반환받은 `RunId`로 `batch.BatchRun`과 S01 체크포인트의 존재를 확인한다. S01 체크포인트가 `Succeeded`이면 동일 `RunId`를 사용하고, 존재하지 않으면 신규 호출로 다시 등록한다.
- 실패 진단에는 정확한 `exactStatementName`과 `-9010`을 함께 기록한다. S01은 레거시 프로시저를 대체하지 않는 제어 단계이므로 다른 단계의 오류 코드를 사용하지 않는다.