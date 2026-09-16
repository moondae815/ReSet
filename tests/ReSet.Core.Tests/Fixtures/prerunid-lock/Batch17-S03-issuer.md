### S03 실행 및 저널 초기화

**목적 및 인터페이스**

- `batchYmd VARCHAR(8) -> p_batchYmd`
- 성공 시 `batch.BatchRun`의 IDENTITY로 발급된 `RunId BIGINT`를 `context.RunId`에 저장하여 후속 단계에 전달한다.
- 작업명은 `POQSettleBatch17`, 단계 코드는 `S03`으로 고정한다.
- 원본 프로시저가 없는 제어 단계이므로 일반 실패 코드는 **-9030**이며, 정확한 실패 지점은 예약 블록 `-9030..-9039` 안에서 구분한다.
- 모든 제어 트랜잭션은 **SNAPSHOT 격리** 의무를 만족해야 한다. 청크와 shadow table은 사용하지 않는다.

| 코드 | 실패 지점 |
|---:|---|
| -9030 | DML 진입 전 또는 식별되지 않은 일반 실패 |
| -9031 | `batch.BatchRun` 실행 행 생성 |
| -9032 | `batch.BatchStepJournal` 시작 행 생성 |
| -9033 | `batch.BatchCheckpoint` Pending 행 생성 |
| -9034 | `batch.BatchStepJournal` 성공 전환 |
| -9035 | `batch.BatchCheckpoint` 성공 전환 |
| -9036 | `batch.BatchRun.ResumeFromStepCode`를 `S04`로 이동 |
| -9037 | 커밋 전 초기화 결과 검증 |

```csharp
long initializeRun(string batchYmd)
{
    int currentStepErrorCode = 0;
    long? runId = null;
    var startedAtUtc = clock.utcNow();

    var conn = connectionFactory.open();
    var tx = conn.beginTransaction(); // SNAPSHOT 격리 의무

    try
    {
        currentStepErrorCode = -9031;
        runId = repository.queryScalar(
            conn,
            SQL_INSERT_RUN_AND_GET_ID,
            {
                p_batchYmd: batchYmd,
                p_startedAtUtc: startedAtUtc
            });

        currentStepErrorCode = -9032;
        repository.execute(
            conn,
            SQL_INSERT_S03_JOURNAL,
            {
                p_runId: runId,
                p_startedAtUtc: startedAtUtc
            });

        currentStepErrorCode = -9033;
        repository.execute(
            conn,
            SQL_INSERT_S03_CHECKPOINT,
            { p_runId: runId });

        currentStepErrorCode = -9034;
        repository.execute(
            conn,
            SQL_MARK_S03_JOURNAL_SUCCEEDED,
            {
                p_runId: runId,
                p_startedAtUtc: startedAtUtc
            });

        currentStepErrorCode = -9035;
        repository.execute(
            conn,
            SQL_MARK_S03_CHECKPOINT_SUCCEEDED,
            { p_runId: runId });

        currentStepErrorCode = -9036;
        repository.execute(
            conn,
            SQL_ADVANCE_RUN_TO_S04,
            { p_runId: runId });

        currentStepErrorCode = -9037;
        var validation = repository.queryRows(
            conn,
            SQL_VALIDATE_S03_INITIALIZATION,
            { p_runId: runId });

        if (validation.RunCount != 1
            || validation.JournalCount != 1
            || validation.CheckpointCount != 1)
        {
            throw validationFailure("S03 초기화 상태가 완전하지 않음");
        }

        tx.commit();
        context.RunId = runId.Value;
        return runId.Value;
    }
    catch (Exception error)
    {
        tx.rollback();

        int journalCode =
            currentStepErrorCode == 0
                ? -9030
                : currentStepErrorCode;

        // 롤백된 초기화 행을 보상 DELETE하지 않는다.
        // 사용 가능한 RunId가 남지 않으므로 별도의 SNAPSHOT 제어 경계에서
        // 완전한 Failed 실행 행, 실패 저널, Pending 체크포인트를 함께 게시한다.
        long failedRunId = publishFailedS03Run(
            batchYmd,
            startedAtUtc,
            journalCode,
            preserveSqlFailureDetail(error));

        context.RunId = failedRunId;
        throw;
    }
}
```

`publishFailedS03Run`은 세 실패 메타데이터 문장을 하나의 별도 SNAPSHOT 트랜잭션으로 커밋한다. 이 기록은 롤백된 성공 초기화의 부분 결과가 아니라 완결된 실패 실행 기록이다. 실패 실행은 `ResumeFromStepCode = N'S03'`, `CheckpointStatus = N'Pending'`으로 남으며, 후속 시도는 새 실행으로 시작한다.

```sql
-- SQL_INSERT_RUN_AND_GET_ID
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
    N'POQSettleBatch17',
    @p_batchYmd,
    N'Running',
    N'S03',
    @p_startedAtUtc,
    NULL,
    NULL
);

SELECT CONVERT(BIGINT, SCOPE_IDENTITY()) AS RunId;
```

```sql
-- SQL_INSERT_S03_JOURNAL
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
    N'S03',
    N'Running',
    NULL,
    @p_startedAtUtc,
    NULL,
    NULL
);

-- SQL_INSERT_S03_CHECKPOINT
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
    N'S03',
    N'Pending',
    NULL
);
```

```sql
-- SQL_MARK_S03_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = N'S03'
   AND StartedAtUtc = @p_startedAtUtc;

-- SQL_MARK_S03_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S03';

-- SQL_ADVANCE_RUN_TO_S04
UPDATE batch.BatchRun
   SET ResumeFromStepCode = N'S04',
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND RunStatus = N'Running';
```

```sql
-- SQL_VALIDATE_S03_INITIALIZATION
SELECT
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchRun
         WHERE RunId = @p_runId
           AND JobName = N'POQSettleBatch17'
           AND RunStatus = N'Running'
           AND ResumeFromStepCode = N'S04'
    ) AS RunCount,
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchStepJournal
         WHERE RunId = @p_runId
           AND StepCode = N'S03'
           AND StepStatus = N'Succeeded'
    ) AS JournalCount,
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchCheckpoint
         WHERE RunId = @p_runId
           AND StepCode = N'S03'
           AND CheckpointStatus = N'Succeeded'
    ) AS CheckpointCount;
```

```sql
-- SQL_INSERT_FAILED_RUN_AND_GET_ID
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
    N'POQSettleBatch17',
    @p_batchYmd,
    N'Failed',
    N'S03',
    @p_startedAtUtc,
    SYSUTCDATETIME(),
    @p_errorMessage
);

SELECT CONVERT(BIGINT, SCOPE_IDENTITY()) AS RunId;

-- SQL_INSERT_FAILED_S03_JOURNAL
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
    N'S03',
    N'Failed',
    @p_legacyReturnCode,
    @p_startedAtUtc,
    SYSUTCDATETIME(),
    @p_errorMessage
);

-- SQL_INSERT_FAILED_S03_CHECKPOINT
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
    N'S03',
    N'Pending',
    NULL
);
```

S03은 자신이 시작한 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint` 행만 생성·전환한다. S04 이후 단계의 행은 미리 만들지 않으며, 각 후속 단계가 실제 시작할 때 자신의 행을 생성한다. 성공한 S03은 오케스트레이터가 `batch.BatchCheckpoint`의 `Succeeded` 상태를 확인하여 재호출하지 않는다.
