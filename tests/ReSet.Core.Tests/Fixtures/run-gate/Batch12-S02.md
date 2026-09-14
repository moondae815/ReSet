### S02 실행 등록 잠금 및 저널 초기화

#### 목적과 실행 계약

S01에서 확정한 기준일로 `POQSettleBatch12` 실행을 등록하고, 동일 기준일의 중복 실행을 차단한 뒤 S02의 저널과 체크포인트를 초기화한다. 잠금 획득과 S02 성공 체크포인트 확정이 완료되어야 S03을 호출할 수 있으며, 이 단계는 다른 업무 단계와 병렬 실행하지 않는다.

- 입력: `batchYmd` — `batch.BatchRun.BatchYmd`의 `varchar(8)` 계약으로 바인딩한다.
- 고정값: `POQSettleBatch12` — `batch.BatchRun.JobName` 및 `batch.BatchRunLock.JobName`에 사용한다.
- 출력: `runId` — `batch.BatchRun.RunId`의 IDENTITY 값을 `SCOPE_IDENTITY()`로 읽어 이후 모든 단계에 전달한다. 호출 입력으로 받거나 계산하지 않는다.
- 재시작·건너뛰기 입력은 두지 않는다.
- 레거시 원본이 없는 제어 단계이므로 상태 변수는 `INT`의 `0`으로 시작하며 일반 실패 코드는 **-9020**이다.
- 모든 트랜잭션은 SNAPSHOT 격리 의무를 따른다. 모든 조회와 DML에서 `NOLOCK`을 사용하지 않는다.
- 업무 데이터 shadow 또는 보상 DELETE는 사용하지 않는다. 잠금 획득 트랜잭션이 실패하면 해당 트랜잭션을 롤백하여 `batch.BatchRunLock` 변경과 성공 체크포인트 변경을 함께 제거한다.

#### 트랜잭션 구성

1. `batch.BatchRun`에 실행을 등록하고 발급된 `RunId`를 확정한다.
2. 별도 초기화 트랜잭션에서 S02 소유의 `batch.BatchStepJournal`과 `batch.BatchCheckpoint` 행을 생성한다.
3. 잠금 트랜잭션에서 `batch.BatchRunLock`을 획득하고, 같은 트랜잭션 안에서 S02 저널과 체크포인트를 `Succeeded`로 전환한다.
4. 잠금 충돌 또는 성공 상태 전환 실패 시 3번 트랜잭션 전체를 롤백한다. 등록된 실행은 실패 감사 기록으로 유지하며 `batch.BatchRun.RunStatus`와 S02 저널을 `Failed`로 갱신한다. 체크포인트는 `Pending`으로 남겨 재호출 대상임을 보존한다.
5. 동일 `JobName`과 `BatchYmd`에 대한 잠금 INSERT는 하나만 성공해야 한다. 중복 키 또는 잠금 획득 실패는 애플리케이션이 관찰하여 -9020으로 처리하며, SQL 문장 자체가 결과에 따라 분기하지 않는다.

```csharp
const string stepCode = "S02";
const string jobName = "POQSettleBatch12";

int currentStepErrorCode = 0;
string currentStatement = "S02 시작 전";
long? runId = null;
bool metadataInitialized = false;

conn = connectionFactory.open();

try
{
    // 실행 등록은 이후 실패를 RunId 기준으로 기록할 수 있도록 먼저 확정한다.
    runTx = conn.beginTransaction(); // SNAPSHOT 의무
    currentStatement = "SQL_INSERT_BATCH_RUN";
    currentStepErrorCode = -9020;
    runId = repository.queryScalar(conn, runTx, SQL_INSERT_BATCH_RUN, {
        p_jobName: jobName,       // nvarchar(128)
        p_batchYmd: batchYmd      // varchar(8)
    });
    runTx.commit();

    // S02가 소유하는 저널 및 체크포인트 행을 생성한다.
    initTx = conn.beginTransaction(); // SNAPSHOT 의무

    currentStatement = "SQL_INSERT_STEP_JOURNAL";
    currentStepErrorCode = -9020;
    repository.execute(conn, initTx, SQL_INSERT_STEP_JOURNAL, {
        p_runId: runId,
        p_stepCode: stepCode
    });

    currentStatement = "SQL_INSERT_STEP_CHECKPOINT";
    currentStepErrorCode = -9020;
    repository.execute(conn, initTx, SQL_INSERT_STEP_CHECKPOINT, {
        p_runId: runId,
        p_stepCode: stepCode
    });

    initTx.commit();
    metadataInitialized = true;

    // 잠금과 S02 성공 상태는 하나의 원자적 단위다.
    lockTx = conn.beginTransaction(); // SNAPSHOT 의무

    currentStatement = "SQL_ACQUIRE_RUN_LOCK";
    currentStepErrorCode = -9020;
    repository.execute(conn, lockTx, SQL_ACQUIRE_RUN_LOCK, {
        p_jobName: jobName,
        p_batchYmd: batchYmd,
        p_runId: runId
    });

    currentStatement = "SQL_MARK_STEP_JOURNAL_SUCCEEDED";
    currentStepErrorCode = -9020;
    repository.execute(conn, lockTx, SQL_MARK_STEP_JOURNAL_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    });

    currentStatement = "SQL_MARK_STEP_CHECKPOINT_SUCCEEDED";
    currentStepErrorCode = -9020;
    repository.execute(conn, lockTx, SQL_MARK_STEP_CHECKPOINT_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    });

    lockTx.commit();
    return runId;
}
catch (failure)
{
    rollbackIfOpen(runTx);
    rollbackIfOpen(initTx);
    rollbackIfOpen(lockTx);

    int failureCode =
        currentStepErrorCode == 0
            ? -9020
            : currentStepErrorCode;

    string failedStatement = currentStatement;
    string failureMessage = failedStatement + ": " + failure.message;

    if (runId != null)
    {
        failureTx = conn.beginTransaction(); // SNAPSHOT 의무

        if (metadataInitialized)
        {
            currentStatement = "SQL_MARK_STEP_JOURNAL_FAILED";
            currentStepErrorCode = -9020;
            repository.execute(conn, failureTx, SQL_MARK_STEP_JOURNAL_FAILED, {
                p_runId: runId,
                p_stepCode: stepCode,
                p_legacyReturnCode: failureCode,
                p_errorMessage: failureMessage
            });
        }
        else
        {
            // 초기화 트랜잭션이 롤백된 경우 실패 저널과 Pending 체크포인트를 생성한다.
            currentStatement = "SQL_INSERT_FAILED_STEP_JOURNAL";
            currentStepErrorCode = -9020;
            repository.execute(conn, failureTx, SQL_INSERT_FAILED_STEP_JOURNAL, {
                p_runId: runId,
                p_stepCode: stepCode,
                p_legacyReturnCode: failureCode,
                p_errorMessage: failureMessage
            });

            currentStatement = "SQL_INSERT_PENDING_CHECKPOINT_AFTER_FAILURE";
            currentStepErrorCode = -9020;
            repository.execute(conn, failureTx, SQL_INSERT_PENDING_CHECKPOINT_AFTER_FAILURE, {
                p_runId: runId,
                p_stepCode: stepCode
            });
        }

        currentStatement = "SQL_MARK_BATCH_RUN_FAILED";
        currentStepErrorCode = -9020;
        repository.execute(conn, failureTx, SQL_MARK_BATCH_RUN_FAILED, {
            p_runId: runId,
            p_errorMessage: failureMessage
        });

        failureTx.commit();
    }

    stop pipeline;
}
```

#### 애플리케이션 전송 SQL

아래 제어 DML은 레거시 DML 범위 표에서 유래한 문장이 아니므로 레거시 문장 앵커를 부여하지 않는다.

```sql
-- SQL_INSERT_BATCH_RUN
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

SELECT CAST(SCOPE_IDENTITY() AS BIGINT) AS RunId;
```

```sql
-- SQL_INSERT_STEP_JOURNAL
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
    @p_stepCode,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

-- SQL_INSERT_STEP_CHECKPOINT
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
    @p_stepCode,
    N'Pending',
    NULL
);
```

```sql
-- SQL_ACQUIRE_RUN_LOCK
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

-- SQL_MARK_STEP_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';

-- SQL_MARK_STEP_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND CheckpointStatus = N'Pending';
```

```sql
-- SQL_MARK_STEP_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';

-- SQL_INSERT_FAILED_STEP_JOURNAL
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
    @p_stepCode,
    N'Failed',
    @p_legacyReturnCode,
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    @p_errorMessage
);

-- SQL_INSERT_PENDING_CHECKPOINT_AFTER_FAILURE
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
    @p_stepCode,
    N'Pending',
    NULL
);

-- SQL_MARK_BATCH_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND RunStatus = N'Running';
```

#### 실패 및 재시작 판정

| 실패 지점 | 코드 | 처리 |
|---|---:|---|
| 실행 등록, 저널·체크포인트 초기화, 잠금 획득 또는 성공 상태 전환 실패 | -9020 | 열린 트랜잭션을 롤백하고 정확한 SQL 식별자를 `batch.BatchStepJournal.ErrorMessage`에 기록한다. |
| 동일 `POQSettleBatch12` 및 기준일 잠금 충돌 | -9020 | 잠금 트랜잭션을 롤백하고 `batch.BatchRun`과 S02 저널을 `Failed`로 전환한다. |
| 성공 | 해당 없음 | `batch.BatchRun`은 `Running`, `batch.BatchRunLock`은 `Held`, S02의 `batch.BatchStepJournal`과 `batch.BatchCheckpoint`는 `Succeeded` 상태가 된다. |

재시작 시 S02 체크포인트가 이미 `Succeeded`이면 외부 오케스트레이터가 이 단계를 호출하지 않는다. S02가 실패한 실행은 체크포인트가 `Pending`이므로 기존 실패 실행을 임의로 성공 처리하지 않고, 운영 재시작 정책이 선택한 실행 컨텍스트에서 잠금 소유권과 실패 원인을 확인한 후 다시 수행한다.