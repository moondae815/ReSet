> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S02 (S02 본문이 `BatchRunLock`에 쓰는데 그 표가 목차에 없습니다 - 목차가 선언한 것은 BatchRun, BatchStepJournal뿐입니다. 목차가 권한과 DDL 을 끌고 가므로, 승인된 표만 보고 권한을 잡으면 이 표에 권한이 없어 실행이 실패합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S02 전역 잠금 및 실행 등록

이 단계는 `POQSettleBatch13`과 정산기준일 조합에 대한 단일 실행권을 확보하고 신규 실행을 `batch.BatchRun`에 등록한다. 단계 실행 이력은 `batch.BatchStepJournal`에 기록하며, 재시작 판단을 위해 `batch.BatchCheckpoint`를 함께 생성한다. 잠금 소유권의 영속 상태는 `batch.BatchRunLock`에 기록한다.

- 입력 인터페이스: `batchYmd` → `@p_batchYmd VARCHAR(8)`
- 출력 인터페이스: `batch.BatchRun.RunId`에서 발급된 `runId`
- `JobName`은 입력으로 받지 않고 `N'POQSettleBatch13'`으로 고정한다.
- 재시작·우회·잠금 무시용 입력은 두지 않는다.
- 모든 연결의 SQL 실행은 SNAPSHOT 격리 수준을 보장해야 한다.
- `Chunkable = False`이므로 페이징하지 않는다. 업무 데이터 재구축이 없는 제어 단계이므로 Shadow Table도 사용하지 않는다.
- 비레거시 단계의 일반 실패 코드는 **-9020**이다. `currentStepErrorCode`는 `INT 0`으로 초기화하고, 각 실패 가능 문장 직전에 `-9020`을 대입한다.
- 세션 잠금은 S19가 완료될 때까지 동일한 전용 잠금 연결에서 유지한다. 프로세스가 비정상 종료되면 세션 잠금은 해제되며, 다음 실행은 `batch.BatchRunLock`의 기존 행을 새 `OwnerRunId`로 갱신한다.

```pseudocode
// S02에는 레거시 프로시저 인터페이스가 없다.
currentStepErrorCode = 0
currentStatementName = NULL
runId = NULL
journalStarted = false
persistentLockRecorded = false
sessionLockAcquired = false

conn = connectionFactory.open()
lockConn = connectionFactory.open()
ensure conn and lockConn execute this step under SNAPSHOT isolation

TRY:
    // RunId는 IDENTITY가 발급하며 애플리케이션이 계산하지 않는다.
    currentStepErrorCode = -9020
    currentStatementName = "SQL_BATCH_RUN_INSERT"

    registrationTx = conn.beginTransaction()
    runId = conn.queryScalar(
        SQL_BATCH_RUN_INSERT,
        { p_batchYmd: batchYmd },
        registrationTx
    )
    registrationTx.commit()

    // 이후 실패를 정확히 기록할 수 있도록 실행 시작 이력을 내구화한다.
    journalTx = conn.beginTransaction()

    currentStepErrorCode = -9020
    currentStatementName = "SQL_STEP_JOURNAL_START"
    conn.execute(
        SQL_STEP_JOURNAL_START,
        { p_runId: runId, p_stepCode: "S02" },
        journalTx
    )

    currentStepErrorCode = -9020
    currentStatementName = "SQL_STEP_CHECKPOINT_START"
    conn.execute(
        SQL_STEP_CHECKPOINT_START,
        { p_runId: runId, p_stepCode: "S02" },
        journalTx
    )

    journalTx.commit()
    journalStarted = true

    // 데이터베이스 세션 잠금이 실제 동시 실행 차단 수단이다.
    currentStepErrorCode = -9020
    currentStatementName = "SQL_ACQUIRE_GLOBAL_LOCK"
    lockResult = lockConn.queryScalar(
        SQL_ACQUIRE_GLOBAL_LOCK,
        { p_batchYmd: batchYmd }
    )

    IF lockResult < 0:
        raise step failure identifying SQL_ACQUIRE_GLOBAL_LOCK

    sessionLockAcquired = true

    // 세션 잠금이 확보된 상태이므로 기존 잠금 메타데이터의 소유자를 안전하게 교체한다.
    lockTx = conn.beginTransaction()

    currentStepErrorCode = -9020
    currentStatementName = "SQL_GLOBAL_LOCK_UPDATE"
    affected = conn.execute(
        SQL_GLOBAL_LOCK_UPDATE,
        { p_runId: runId, p_batchYmd: batchYmd },
        lockTx
    )

    IF affected == 0:
        currentStepErrorCode = -9020
        currentStatementName = "SQL_GLOBAL_LOCK_INSERT"
        conn.execute(
            SQL_GLOBAL_LOCK_INSERT,
            { p_runId: runId, p_batchYmd: batchYmd },
            lockTx
        )

    lockTx.commit()
    persistentLockRecorded = true

    currentStepErrorCode = -9020
    currentStatementName = "SQL_VERIFY_LOCK_OWNER"
    lockOwner = conn.queryRow(
        SQL_VERIFY_LOCK_OWNER,
        { p_runId: runId, p_batchYmd: batchYmd }
    )

    IF lockOwner is missing:
        raise step failure identifying SQL_VERIFY_LOCK_OWNER

    // 잠금과 실행 등록이 확정된 후 단계 성공 상태를 별도 제어 경계에서 기록한다.
    completionTx = conn.beginTransaction()

    currentStepErrorCode = -9020
    currentStatementName = "SQL_STEP_JOURNAL_SUCCEEDED"
    conn.execute(
        SQL_STEP_JOURNAL_SUCCEEDED,
        {
            p_runId: runId,
            p_stepCode: "S02",
            p_legacyReturnCode: NULL
        },
        completionTx
    )

    currentStepErrorCode = -9020
    currentStatementName = "SQL_STEP_CHECKPOINT_SUCCEEDED"
    conn.execute(
        SQL_STEP_CHECKPOINT_SUCCEEDED,
        { p_runId: runId, p_stepCode: "S02" },
        completionTx
    )

    completionTx.commit()

    // lockConn은 닫지 않고 오케스트레이션 실행 컨텍스트에 보관한다.
    return runId

CATCH error:
    registrationTx.rollbackIfOpen()
    journalTx.rollbackIfOpen()
    lockTx.rollbackIfOpen()
    completionTx.rollbackIfOpen()

    IF persistentLockRecorded:
        cleanupTx = conn.beginTransaction()
        conn.execute(
            SQL_GLOBAL_LOCK_RELEASE_METADATA,
            { p_runId: runId, p_batchYmd: batchYmd },
            cleanupTx
        )
        cleanupTx.commit()

    IF sessionLockAcquired:
        lockConn.execute(
            SQL_RELEASE_GLOBAL_LOCK,
            { p_batchYmd: batchYmd }
        )

    IF runId IS NOT NULL:
        failureTx = conn.beginTransaction()

        IF journalStarted:
            conn.execute(
                SQL_STEP_JOURNAL_FAILED,
                {
                    p_runId: runId,
                    p_stepCode: "S02",
                    p_legacyReturnCode: currentStepErrorCode,
                    p_errorMessage: currentStatementName + ": " + error.message
                },
                failureTx
            )
        ELSE:
            conn.execute(
                SQL_STEP_JOURNAL_FAILED_BOOTSTRAP,
                {
                    p_runId: runId,
                    p_stepCode: "S02",
                    p_legacyReturnCode: currentStepErrorCode,
                    p_errorMessage: currentStatementName + ": " + error.message
                },
                failureTx
            )

        conn.execute(
            SQL_BATCH_RUN_FAILED,
            {
                p_runId: runId,
                p_errorMessage: currentStatementName + ": " + error.message
            },
            failureTx
        )

        failureTx.commit()

    close lockConn only on failure
    stop dependent pipeline
```

애플리케이션이 전송할 SQL은 다음과 같다. 제어 흐름은 SQL에 넣지 않으며, 잠금 반환값과 영향 행 수는 애플리케이션이 관찰한다.

```sql
-- SQL_BATCH_RUN_INSERT
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
    N'POQSettleBatch13',
    @p_batchYmd,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT) AS RunId;


-- SQL_STEP_JOURNAL_START
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


-- SQL_STEP_CHECKPOINT_START
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


-- SQL_ACQUIRE_GLOBAL_LOCK
DECLARE @v_lockResult INT;

EXEC @v_lockResult = sys.sp_getapplock
    @Resource = CONCAT(N'POQSettleBatch13:', @p_batchYmd),
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 0;

SELECT @v_lockResult AS LockResult;


-- SQL_GLOBAL_LOCK_UPDATE
UPDATE batch.BatchRunLock
   SET OwnerRunId = @p_runId,
       LockStatus = N'Held',
       AcquiredAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
 WHERE JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd;


-- SQL_GLOBAL_LOCK_INSERT
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
    N'POQSettleBatch13',
    @p_batchYmd,
    @p_runId,
    N'Held',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    NULL
);


-- SQL_VERIFY_LOCK_OWNER
SELECT OwnerRunId,
       LockStatus
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';


-- SQL_STEP_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';


-- SQL_STEP_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND CheckpointStatus = N'Pending';


-- SQL_GLOBAL_LOCK_RELEASE_METADATA
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = SYSUTCDATETIME()
 WHERE JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';


-- SQL_RELEASE_GLOBAL_LOCK
DECLARE @v_releaseResult INT;

EXEC @v_releaseResult = sys.sp_releaseapplock
    @Resource = CONCAT(N'POQSettleBatch13:', @p_batchYmd),
    @LockOwner = N'Session';

SELECT @v_releaseResult AS ReleaseResult;


-- SQL_STEP_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';


-- SQL_STEP_JOURNAL_FAILED_BOOTSTRAP
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


-- SQL_BATCH_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND RunStatus = N'Running';
```

| 오류 코드 | 적용 범위 | 저널 기록 |
|---:|---|---|
| -9020 | `batch.BatchRun` 등록, `batch.BatchStepJournal`·`batch.BatchCheckpoint` 시작/완료 기록, 전역 세션 잠금, `batch.BatchRunLock` 소유권 기록 또는 검증 실패 | 실패한 정확한 SQL 블록명을 `ErrorMessage`에 포함하고 `LegacyReturnCode = -9020`으로 기록 |