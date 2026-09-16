### S03 | 업무일자 실행 잠금

**목표 테이블:** `batch.BatchRunLock`

- 실행 인터페이스는 오케스트레이션 컨텍스트의 `runId BIGINT`, `batchYmd VARCHAR(8)`만 사용한다. 재시작·건너뛰기·잠금 강제 탈취용 입력은 추가하지 않는다.
- `JobName`은 입력값이 아니라 `N'POQSettleBatch11'` 상수로 고정한다.
- `(JobName, BatchYmd)` 조합은 물리적으로 유일해야 한다. 신규 실행은 행을 삽입하고, 이미 `Released`인 행은 원자적 조건부 갱신으로 재사용한다.
- 모든 조회와 DML은 **SNAPSHOT 격리**를 만족하는 하나의 트랜잭션에서 수행한다. 다른 실행이 보유한 `Held` 잠금은 만료시간을 추정하여 탈취하지 않는다.
- 실패 코드는 예약 블록의 일반 코드 **`-9030`**만 사용한다. 성공 시 `LegacyReturnCode`는 `NULL`이다.

```pseudocode
// 공통 시작 저널과 Pending 체크포인트가 기록된 뒤 호출된다.
executeS03(runId, batchYmd)
{
    jobName = "POQSettleBatch11"
    currentStepErrorCode = 0
    statementName = NULL

    try
    {
        statementName = "S03 업무일자 잠금 연결 및 트랜잭션 시작"
        currentStepErrorCode = -9030

        conn = connectionFactory.open()
        tx = conn.beginTransaction()  // 전체 작업은 SNAPSHOT 격리 의무를 만족한다.

        statementName = "SQL_READ_CURRENT_LOCK"
        currentStepErrorCode = -9030
        lockRow = conn.querySingleOrNone(SQL_READ_CURRENT_LOCK, {
            p_batchYmd: batchYmd
        })

        if (lockRow is NULL)
        {
            statementName = "SQL_INSERT_LOCK"
            currentStepErrorCode = -9030

            affected = conn.execute(SQL_INSERT_LOCK, {
                p_batchYmd: batchYmd,
                p_runId: runId
            })

            if (affected != 1)
                throw controlledFailure("업무일자 잠금 행을 생성하지 못함")
        }
        else if (lockRow.LockStatus == "Released")
        {
            statementName = "SQL_REACQUIRE_RELEASED_LOCK"
            currentStepErrorCode = -9030

            affected = conn.execute(SQL_REACQUIRE_RELEASED_LOCK, {
                p_batchYmd: batchYmd,
                p_runId: runId
            })

            // 동시 실행이 먼저 재획득했거나 SNAPSHOT 쓰기 충돌이 발생하면 실패한다.
            if (affected != 1)
                throw controlledFailure("해제된 업무일자 잠금의 재획득 경쟁에서 실패")
        }
        else if (lockRow.LockStatus == "Held" &&
                 lockRow.OwnerRunId == runId)
        {
            // 잠금 커밋 후 성공 저널 기록 전에 장애가 발생한 동일 RunId 재호출을 수용한다.
            statementName = "SQL_REFRESH_OWNED_LOCK"
            currentStepErrorCode = -9030

            affected = conn.execute(SQL_REFRESH_OWNED_LOCK, {
                p_batchYmd: batchYmd,
                p_runId: runId
            })

            if (affected != 1)
                throw controlledFailure("동일 실행 소유 잠금의 확인 또는 갱신 실패")
        }
        else
        {
            statementName = "S03_LOCK_CONFLICT"
            currentStepErrorCode = -9030

            throw controlledFailure(
                "다른 실행이 업무일자 잠금을 보유함",
                ownerRunId: lockRow.OwnerRunId,
                lockStatus: lockRow.LockStatus)
        }

        tx.commit()

        // 공통 성공 처리로 S03 저널과 체크포인트를 Succeeded로 확정한다.
        // 레거시 원본이 없는 제어 단계이므로 성공 LegacyReturnCode는 NULL이다.
        completeStepSuccess(legacyReturnCode: NULL)
    }
    catch (error)
    {
        tx.rollbackIfOpen()

        // 단일 트랜잭션 롤백으로 batch.BatchRunLock 변경이 원상 복구된다.
        // 별도 섀도 또는 보상 DELETE를 실행하지 않는다.
        recordStepFailure(
            legacyReturnCode: currentStepErrorCode,
            errorMessage: errorContext.messageWithStatement(
                statementName,
                error))
        stopPipeline()
    }
}
```

```sql
-- SQL_READ_CURRENT_LOCK
SELECT OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc,
       ReleasedAtUtc
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch11'
   AND BatchYmd = @p_batchYmd;

-- SQL_INSERT_LOCK
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
    N'POQSettleBatch11',
    @p_batchYmd,
    @p_runId,
    N'Held',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    NULL
);

-- SQL_REACQUIRE_RELEASED_LOCK
UPDATE batch.BatchRunLock
   SET OwnerRunId = @p_runId,
       LockStatus = N'Held',
       AcquiredAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
 WHERE JobName = N'POQSettleBatch11'
   AND BatchYmd = @p_batchYmd
   AND LockStatus = N'Released';

-- SQL_REFRESH_OWNED_LOCK
UPDATE batch.BatchRunLock
   SET HeartbeatAtUtc = SYSUTCDATETIME()
 WHERE JobName = N'POQSettleBatch11'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';
```

잠금 트랜잭션 커밋 직후 다음 검증 결과가 정확히 한 행이어야 하며, `OwnerRunId=@p_runId`, `LockStatus=N'Held'`가 아니면 단계 성공으로 확정하지 않는다.

```sql
-- SQL_VALIDATE_ACQUIRED_LOCK
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch11'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';
```
