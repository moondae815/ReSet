### S02 — 실행 등록 잠금

#### 목적 및 인터페이스

C# 배치의 실행 등록 컴포넌트가 `POQSettleBatch19` 실행 행을 생성하고 동일 기준일의 중복 실행을 차단한다.

- 입력: S01에서 검증한 `batchYmd`
- 고정값: `jobName = "POQSettleBatch19"`
- 출력: `batch.BatchRun.RunId`
- 레거시 원본이 없는 제어 단계이므로 레거시 입력·출력 파라미터는 없다. 재시작·우회 플래그도 추가하지 않는다.
- `RunId`는 `batch.BatchRun`의 IDENTITY가 발급한 값을 `SCOPE_IDENTITY()`로 읽으며 애플리케이션에서 계산하지 않는다.
- 성공 상태는 `batch.BatchRun.RunStatus = N'Running'` 및 `batch.BatchRunLock.LockStatus = N'Held'`이고, `OwnerRunId`가 발급된 `RunId`와 일치하는 상태이다.
- S02의 예약 오류 블록은 `-9020..-9029`이며, 현재 정의된 일반 실패 코드 **-9020**만 사용한다. `currentStepErrorCode`는 `INT 0`으로 초기화하고 SQL 실행 직전에 `-9020`을 대입한다.
- 단계의 모든 읽기와 쓰기는 **SNAPSHOT 격리** 의무를 준수한다. 잠금 소유권 조회만 동일 업무키에 대한 경쟁을 직렬화해야 하며, 모든 `NOLOCK` 힌트는 제거한다.
- `batch.BatchRun` 등록과 `batch.BatchRunLock` 획득은 하나의 트랜잭션에서 처리한다. 예상하지 못한 SQL 실패는 전체 롤백하여 두 테이블을 실행 전 상태로 되돌린다.
- 이미 다른 실행이 잠금을 보유한 경우는 제어된 중복 실행 거절이다. 새 `batch.BatchRun` 행을 `Failed`로 확정한 후 트랜잭션을 커밋하고 파이프라인을 중단한다.
- 단일 트랜잭션 제어 단계이므로 청크 처리, Shadow Table 및 롤백 후 보상 DELETE를 사용하지 않는다.

#### 애플리케이션 제어 흐름

```pseudocode
stepCode = "S02"
jobName = "POQSettleBatch19"
currentStepErrorCode = 0
currentStatementId = null
runId = null
conn = connectionFactory.open()

// 이 트랜잭션은 SNAPSHOT 격리 의무를 충족해야 한다.
tx = conn.beginTransaction()

TRY:
    currentStepErrorCode = -9020
    currentStatementId = "SQL_REGISTER_RUN"
    runId = repository.queryScalar(conn, tx, SQL_REGISTER_RUN, {
        p_jobName: jobName,
        p_batchYmd: batchYmd
    })

    currentStepErrorCode = -9020
    currentStatementId = "SQL_READ_RUN_LOCK_FOR_OWNERSHIP"
    lockRow = repository.queryRow(conn, tx, SQL_READ_RUN_LOCK_FOR_OWNERSHIP, {
        p_jobName: jobName,
        p_batchYmd: batchYmd
    })

    IF lockRow is null:
        currentStepErrorCode = -9020
        currentStatementId = "SQL_INSERT_RUN_LOCK"
        repository.execute(conn, tx, SQL_INSERT_RUN_LOCK, {
            p_jobName: jobName,
            p_batchYmd: batchYmd,
            p_runId: runId
        })
    ELSE IF lockRow.LockStatus == "Released":
        currentStepErrorCode = -9020
        currentStatementId = "SQL_REACQUIRE_RELEASED_RUN_LOCK"
        repository.execute(conn, tx, SQL_REACQUIRE_RELEASED_RUN_LOCK, {
            p_jobName: jobName,
            p_batchYmd: batchYmd,
            p_runId: runId
        })
    ELSE:
        // 다른 RunId가 Held 상태인 경우 잠금을 탈취하지 않는다.
        currentStepErrorCode = -9020
        currentStatementId = "SQL_MARK_DUPLICATE_RUN_FAILED"
        repository.execute(conn, tx, SQL_MARK_RUN_FAILED, {
            p_runId: runId,
            p_errorMessage: "S02 -9020: another run holds the execution lock"
        })
        tx.commit()
        stop pipeline with error -9020

    currentStepErrorCode = -9020
    currentStatementId = "SQL_VERIFY_RUN_LOCK"
    ownedLock = repository.queryRow(conn, tx, SQL_VERIFY_RUN_LOCK, {
        p_jobName: jobName,
        p_batchYmd: batchYmd,
        p_runId: runId
    })

    IF ownedLock is null:
        currentStepErrorCode = -9020
        currentStatementId = "SQL_MARK_LOCK_VERIFICATION_FAILED"
        repository.execute(conn, tx, SQL_MARK_RUN_FAILED, {
            p_runId: runId,
            p_errorMessage: "S02 -9020: execution lock ownership verification failed"
        })
        tx.commit()
        stop pipeline with error -9020

    tx.commit()
    return runId

CATCH error:
    tx.rollbackIfOpen()

    // 롤백으로 등록 행까지 사라졌다면 갱신할 RunId가 없으므로 외부 실행 로그에
    // currentStepErrorCode, currentStatementId와 엔진 오류를 남긴다.
    // 제어된 중복 실행 경로는 위에서 Failed 상태를 커밋했으므로 이 경로로 오지 않는다.
    stop pipeline with {
        code: currentStepErrorCode,
        statement: currentStatementId,
        error: error
    }
FINALLY:
    conn.close()
```

#### 애플리케이션이 전송할 SQL

`batch.BatchRunLock`의 논리 업무키인 `(JobName, BatchYmd)`에 대한 잠금 조회는 경쟁 실행을 직렬화해야 한다. 기존 행이 없을 때도 같은 키 범위를 보호한 상태에서 INSERT가 수행되어야 한다.

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
    @p_jobName,
    @p_batchYmd,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
```

```sql
-- SQL_READ_RUN_LOCK_FOR_OWNERSHIP
SELECT
    JobName,
    BatchYmd,
    OwnerRunId,
    LockStatus,
    AcquiredAtUtc,
    HeartbeatAtUtc,
    ReleasedAtUtc
FROM batch.BatchRunLock WITH (UPDLOCK, HOLDLOCK)
WHERE JobName = @p_jobName
  AND BatchYmd = @p_batchYmd;
```

```sql
-- SQL_INSERT_RUN_LOCK
INSERT INTO batch.BatchRunLock
(
    JobName,
    BatchYmd,
    OwnerRunId,
    LockStatus,
    AcquiredAtUtc,
    HeartbeatAtUtc,
    ReleasedAtUtc
)
VALUES
(
    @p_jobName,
    @p_batchYmd,
    @p_runId,
    N'Held',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    NULL
);
```

```sql
-- SQL_REACQUIRE_RELEASED_RUN_LOCK
UPDATE batch.BatchRunLock
   SET OwnerRunId = @p_runId,
       LockStatus = N'Held',
       AcquiredAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND LockStatus = N'Released';
```

```sql
-- SQL_VERIFY_RUN_LOCK
SELECT
    JobName,
    BatchYmd,
    OwnerRunId,
    LockStatus
FROM batch.BatchRunLock
WHERE JobName = @p_jobName
  AND BatchYmd = @p_batchYmd
  AND OwnerRunId = @p_runId
  AND LockStatus = N'Held';
```

```sql
-- SQL_MARK_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND RunStatus = N'Running';
```

#### 완료 검증

커밋 직전 검증은 다음 불변조건을 보장해야 한다.

1. `batch.BatchRun`에 발급된 `RunId`의 행이 정확히 하나 존재한다.
2. 해당 행의 `JobName`, `BatchYmd`, `RunStatus`가 각각 `POQSettleBatch19`, 입력 기준일, `Running`이다.
3. `batch.BatchRunLock`의 동일 업무키 행이 `Held`이고 `OwnerRunId`가 발급된 `RunId`와 같다.
4. 다른 `RunId`가 동일 `(JobName, BatchYmd)`의 `Held` 잠금을 소유하지 않는다.

```sql
-- SQL_VALIDATE_S02
SELECT
    R.RunId,
    R.JobName,
    R.BatchYmd,
    R.RunStatus,
    L.OwnerRunId,
    L.LockStatus
FROM batch.BatchRun AS R
INNER JOIN batch.BatchRunLock AS L
        ON L.JobName = R.JobName
       AND L.BatchYmd = R.BatchYmd
WHERE R.RunId = @p_runId
  AND R.JobName = @p_jobName
  AND R.BatchYmd = @p_batchYmd
  AND R.RunStatus = N'Running'
  AND L.OwnerRunId = R.RunId
  AND L.LockStatus = N'Held';
```
