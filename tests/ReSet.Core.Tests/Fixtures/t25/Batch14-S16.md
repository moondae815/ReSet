### S16 게시 체크포인트 잠금해제

#### 목적 및 실행 계약

S01~S15가 모두 성공한 실행만 최종 게시한다. `batch.BatchRun`, `batch.BatchStepJournal`, `batch.BatchCheckpoint`, `batch.BatchRunLock`의 최종 상태 전환은 하나의 트랜잭션에서 처리하여 다음 상태가 동시에 커밋되도록 한다.

- `batch.BatchRun.RunStatus = N'Succeeded'`
- S16의 `batch.BatchStepJournal.StepStatus = N'Succeeded'`
- S16의 `batch.BatchCheckpoint.CheckpointStatus = N'Succeeded'`
- `batch.BatchRunLock.LockStatus = N'Released'`

이 단계에는 레거시 프로시저 인터페이스와 OUTPUT 파라미터가 없다. 실행 컨텍스트의 `runId BIGINT -> @p_runId`만 사용하며, 재시작·건너뛰기·가드 우회 입력은 받지 않는다. 작업명과 단계 코드는 각각 `N'POQSettleBatch14'`, `N'S16'` 상수로 사용한다.

시작 등록과 최종 게시에 사용하는 모든 트랜잭션은 SNAPSHOT 격리 의무가 충족된 실행 컨텍스트에서 수행한다. 비청크 단일 트랜잭션 단계이므로 Shadow Table과 실패 후 보상 `DELETE`를 사용하지 않는다.

#### 시작 행 등록

S16이 호출되면 다른 행을 인계받아 갱신하지 않고 자기 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint` 행을 먼저 삽입한다. 두 INSERT는 하나의 짧은 시작 등록 트랜잭션에서 함께 커밋한다.

```sql
-- SQL_S16_JOURNAL_START
INSERT INTO batch.BatchStepJournal
(
    RunId,
    StepCode,
    StepStatus,
    LegacyReturnCode,
    StartedAtUtc,
    CompletedAtUtc,
    ErrorMessage
)
VALUES
(
    @p_runId,
    N'S16',
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
-- SQL_S16_CHECKPOINT_START
INSERT INTO batch.BatchCheckpoint
(
    RunId,
    StepCode,
    CheckpointStatus,
    CompletedAtUtc
)
VALUES
(
    @p_runId,
    N'S16',
    N'Pending',
    NULL
);
```

시작 등록 중 실패하면 해당 시작 트랜잭션 전체를 롤백한다. 연결이 복구된 뒤 위 두 행을 다시 자기 행으로 등록하고 `Failed` 저널 기록을 남기기 전에는 실행을 정상 종료로 보고하지 않는다.

#### 무조건 수행할 게시 전 검증

호출된 S16은 다음 조건을 우회 없이 검사한다.

1. `batch.BatchRun`에 `RunId = @p_runId`, `JobName = N'POQSettleBatch14'`인 행이 정확히 하나 존재한다.
2. 실행 상태는 `Running` 또는 `Restarting`이다.
3. 같은 `JobName`, `BatchYmd`의 `batch.BatchRunLock`이 `Held` 상태이고 `OwnerRunId = @p_runId`이다.
4. S01~S15의 `batch.BatchCheckpoint`가 각각 정확히 하나 존재하며 모두 `Succeeded`이다.
5. S16 체크포인트는 이 호출이 삽입한 단일 `Pending` 행이다.

```sql
-- SQL_S16_RUN_LOCK_STATE
SELECT
    R.RunId,
    R.JobName,
    R.BatchYmd,
    R.RunStatus,
    R.CompletedAtUtc,
    L.OwnerRunId,
    L.LockStatus
FROM batch.BatchRun AS R
INNER JOIN batch.BatchRunLock AS L
        ON L.JobName = R.JobName
       AND L.BatchYmd = R.BatchYmd
WHERE R.RunId = @p_runId
  AND R.JobName = N'POQSettleBatch14';
```

```sql
-- SQL_S16_CHECKPOINT_STATE
SELECT
    StepCode,
    CheckpointStatus
FROM batch.BatchCheckpoint
WHERE RunId = @p_runId
  AND StepCode IN
  (
      N'S01', N'S02', N'S03', N'S04',
      N'S05', N'S06', N'S07', N'S08',
      N'S09', N'S10', N'S11', N'S12',
      N'S13', N'S14', N'S15', N'S16'
  );
```

검증 결과의 행 수와 상태 판정은 애플리케이션이 수행한다. 누락, 중복, 미완료 단계, 잠금 소유권 불일치는 게시 실패로 처리한다.

#### 원자적 게시 SQL

애플리케이션은 아래 DML을 표시된 순서로 실행하고 각 문장의 영향 행 수가 정확히 1인지 확인한다. 어느 문장이든 실행 오류 또는 영향 행 수 불일치가 발생하면 전체 게시 트랜잭션을 롤백한다.

```sql
-- SQL_S16_RUN_SUCCEEDED
UPDATE batch.BatchRun
   SET RunStatus = N'Succeeded',
       ResumeFromStepCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch14'
   AND RunStatus IN (N'Running', N'Restarting')
   AND CompletedAtUtc IS NULL;
```

```sql
-- SQL_S16_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = 0,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND StepStatus = N'Running';
```

```sql
-- SQL_S16_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND CheckpointStatus = N'Pending';
```

잠금 해제는 마지막 DML로 수행한다. 앞선 상태 전환과 같은 트랜잭션에 있으므로 다른 실행은 성공 게시가 커밋되기 전에 해제된 잠금을 관찰할 수 없다.

```sql
-- SQL_S16_LOCK_RELEASED
UPDATE L
   SET L.LockStatus = N'Released',
       L.HeartbeatAtUtc = SYSUTCDATETIME(),
       L.ReleasedAtUtc = SYSUTCDATETIME()
FROM batch.BatchRunLock AS L
INNER JOIN batch.BatchRun AS R
        ON R.JobName = L.JobName
       AND R.BatchYmd = L.BatchYmd
WHERE R.RunId = @p_runId
  AND R.JobName = N'POQSettleBatch14'
  AND L.OwnerRunId = @p_runId
  AND L.LockStatus = N'Held';
```

커밋 직전에는 같은 트랜잭션에서 네 상태를 다시 읽고 애플리케이션이 모두 최종값인지 확인한다.

```sql
-- SQL_S16_POSTCONDITION
SELECT
    R.RunStatus,
    R.CompletedAtUtc,
    J.StepStatus,
    J.LegacyReturnCode,
    C.CheckpointStatus,
    L.LockStatus,
    L.OwnerRunId,
    L.ReleasedAtUtc
FROM batch.BatchRun AS R
INNER JOIN batch.BatchStepJournal AS J
        ON J.RunId = R.RunId
       AND J.StepCode = N'S16'
INNER JOIN batch.BatchCheckpoint AS C
        ON C.RunId = R.RunId
       AND C.StepCode = N'S16'
INNER JOIN batch.BatchRunLock AS L
        ON L.JobName = R.JobName
       AND L.BatchYmd = R.BatchYmd
WHERE R.RunId = @p_runId
  AND R.JobName = N'POQSettleBatch14';
```

#### C# 실행 의사코드

S16은 신규 제어 단계이므로 상태 변수는 `0`에서 시작한다. 일반 실패 코드는 **-9160**이며, 정확한 실패 문장은 `currentStatementName`으로 함께 기록한다.

```pseudocode
executeS16(controlContext):
    runId = controlContext.runId
    currentStepErrorCode = 0
    currentStatementName = null

    conn = connectionFactory.open()
    assertSnapshotExecutionIsGuaranteed(conn)

    // S16 자기 저널과 체크포인트를 먼저 생성한다.
    startTx = conn.beginTransaction()
    TRY:
        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_JOURNAL_START"
        affected = repository.execute(
            startTx,
            SQL_S16_JOURNAL_START,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_CHECKPOINT_START"
        affected = repository.execute(
            startTx,
            SQL_S16_CHECKPOINT_START,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        startTx.commit()
    ON FAILURE error:
        startTx.rollback()
        persistS16StartFailureAfterConnectionRecovery(
            runId,
            legacyReturnCode: -9160,
            statementName: currentStatementName,
            error: error
        )
        stop pipeline

    publishTx = conn.beginTransaction()
    TRY:
        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_RUN_LOCK_STATE"
        runLockState = repository.queryRows(
            publishTx,
            SQL_S16_RUN_LOCK_STATE,
            { p_runId: runId }
        )
        requireExactlyOneOwnedHeldLock(runLockState, runId)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_CHECKPOINT_STATE"
        checkpoints = repository.queryRows(
            publishTx,
            SQL_S16_CHECKPOINT_STATE,
            { p_runId: runId }
        )
        requireS01ThroughS15SucceededAndS16Pending(checkpoints)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_RUN_SUCCEEDED"
        affected = repository.execute(
            publishTx,
            SQL_S16_RUN_SUCCEEDED,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_JOURNAL_SUCCEEDED"
        affected = repository.execute(
            publishTx,
            SQL_S16_JOURNAL_SUCCEEDED,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_CHECKPOINT_SUCCEEDED"
        affected = repository.execute(
            publishTx,
            SQL_S16_CHECKPOINT_SUCCEEDED,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_LOCK_RELEASED"
        affected = repository.execute(
            publishTx,
            SQL_S16_LOCK_RELEASED,
            { p_runId: runId }
        )
        requireAffectedRows(affected, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_POSTCONDITION"
        finalState = repository.queryRows(
            publishTx,
            SQL_S16_POSTCONDITION,
            { p_runId: runId }
        )
        requireAtomicPublishedState(finalState, ownerRunId: runId)

        publishTx.commit()

    ON FAILURE error:
        publishTx.rollback()

        // 롤백으로 Run 성공, S16 성공, 체크포인트 성공, 잠금 해제가 모두 취소된다.
        failureMessage = currentStatementName plus exact error details
        recordS16Failure(
            runId,
            legacyReturnCode: currentStepErrorCode,
            errorMessage: failureMessage
        )
        stop pipeline
```

#### 실패 기록 및 재시작 상태

게시 실패 후에는 별도의 SNAPSHOT 제어 트랜잭션에서 S16이 삽입한 저널 행만 `Failed`로 전환하고 실행을 `Failed`로 표시한다. `batch.BatchCheckpoint`의 S16 행은 `Pending`으로 유지한다. `batch.BatchRunLock`은 게시 실패 시 해제하지 않는다. 게시 트랜잭션의 롤백으로 원래 `Held` 상태가 보존되어 실패한 실행이 잠금을 조기에 양도하지 않도록 한다.

```sql
-- SQL_S16_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND StepStatus = N'Running';
```

```sql
-- SQL_S16_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       ResumeFromStepCode = N'S16',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch14'
   AND RunStatus IN (N'Running', N'Restarting');
```

```pseudocode
recordS16Failure(runId, legacyReturnCode, errorMessage):
    failureTx = conn.beginTransaction()
    TRY:
        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_JOURNAL_FAILED"
        journalRows = repository.execute(
            failureTx,
            SQL_S16_JOURNAL_FAILED,
            {
                p_runId: runId,
                p_legacyReturnCode: legacyReturnCode,
                p_errorMessage: errorMessage
            }
        )
        requireAffectedRows(journalRows, 1, currentStatementName)

        currentStepErrorCode = -9160
        currentStatementName = "SQL_S16_RUN_FAILED"
        runRows = repository.execute(
            failureTx,
            SQL_S16_RUN_FAILED,
            {
                p_runId: runId,
                p_errorMessage: errorMessage
            }
        )
        requireAffectedRows(runRows, 1, currentStatementName)

        failureTx.commit()
    ON FAILURE loggingError:
        failureTx.rollback()
        retry control-state persistence without releasing batch.BatchRunLock
        emit critical operational telemetry
```

재시작 시 오케스트레이터는 성공한 이전 단계의 체크포인트를 외부에서 건너뛰고, `Pending`인 S16을 재개 대상으로 판정한다. S16 자체에는 재시작 또는 가드 우회 파라미터를 추가하지 않는다.