> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S19 (S19 본문이 `BatchRunLock`에 쓰는데 그 표가 목차에 없습니다 - 목차가 선언한 것은 BatchCheckpoint, BatchRun, BatchStepJournal뿐입니다. 목차가 권한과 DDL 을 끌고 가므로, 승인된 표만 보고 권한을 잡으면 이 표에 권한이 없어 실행이 실패합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S19 | 최종 게시 및 잠금 해제

**목적과 실행 조건**

- S18이 성공한 뒤 순차 실행하며 다른 단계와 병렬 실행하지 않는다.
- 레거시 원본이 없는 제어 단계이므로 출력 파라미터는 없고, 실행 컨텍스트의 `runId`와 `batchYmd`를 사용한다. 재시작·우회 플래그는 받지 않는다.
- `runId`는 S02에서 `batch.BatchRun.RunId`의 IDENTITY로 발급된 값을 그대로 사용하고 계산하거나 재발급하지 않는다.
- S01~S18의 `batch.BatchCheckpoint`가 모두 `Succeeded`이고, `batch.BatchRun`이 게시 가능한 상태이며, 해당 실행이 `batch.BatchRunLock`을 보유한 경우에만 최종 게시한다.
- 단계의 모든 데이터베이스 경계는 **SNAPSHOT 격리**로 실행해야 한다.
- `Chunkable = False`이며 최종 게시 DML 전체를 하나의 트랜잭션으로 처리한다. 실패 시 해당 트랜잭션을 롤백하므로 Shadow Table과 보상 DELETE를 사용하지 않는다.
- 단계 로컬 오류 변수는 `INT` 값 `0`으로 초기화한다. 일반 실패 코드는 **-9190**이며, 실패 저널에는 정확한 실패 문장명과 관찰된 오류를 함께 기록한다.

**시작 제어 행 등록**

S19가 호출되면 최종 게시 트랜잭션을 열기 전에 자기 소유의 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint` 행을 반드시 INSERT한다.

```sql
-- SQL_S19_JOURNAL_START
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
    N'S19',
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
-- SQL_S19_CHECKPOINT_START
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
    N'S19',
    N'Pending',
    NULL
);
```

**무조건 수행하는 게시 전 검증**

애플리케이션은 다음 조회 결과의 세 플래그가 모두 `1`인지 확인한다. 하나라도 `0`이면 최종 게시 트랜잭션을 시작하지 않고 S19를 **-9190**으로 실패 처리한다.

```sql
-- SQL_S19_PUBLISH_PRECONDITION
SELECT
    CASE
        WHEN NOT EXISTS
        (
            SELECT 1
              FROM
              (
                  VALUES
                      (N'S01'), (N'S02'), (N'S03'), (N'S04'), (N'S05'),
                      (N'S06'), (N'S07'), (N'S08'), (N'S09'), (N'S10'),
                      (N'S11'), (N'S12'), (N'S13'), (N'S14'), (N'S15'),
                      (N'S16'), (N'S17'), (N'S18')
              ) AS RequiredStep(StepCode)
             WHERE NOT EXISTS
             (
                 SELECT 1
                   FROM batch.BatchCheckpoint AS C
                  WHERE C.RunId = @p_runId
                    AND C.StepCode = RequiredStep.StepCode
                    AND C.CheckpointStatus = N'Succeeded'
             )
        )
        THEN 1 ELSE 0
    END AS AllPriorStepsSucceeded,
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM batch.BatchRun AS R
             WHERE R.RunId = @p_runId
               AND R.JobName = N'POQSettleBatch13'
               AND R.BatchYmd = @p_batchYmd
               AND R.RunStatus IN (N'Running', N'Restarting')
        )
        THEN 1 ELSE 0
    END AS RunIsPublishable,
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM batch.BatchRunLock AS L
             WHERE L.JobName = N'POQSettleBatch13'
               AND L.BatchYmd = @p_batchYmd
               AND L.OwnerRunId = @p_runId
               AND L.LockStatus = N'Held'
        )
        THEN 1 ELSE 0
    END AS RunOwnsLock;
```

**최종 게시 트랜잭션 SQL**

`batch.BatchRun`을 성공으로 게시하고, 같은 트랜잭션 안에서 실행 잠금을 해제한 뒤 S19 자신의 `batch.BatchCheckpoint`와 `batch.BatchStepJournal`을 성공으로 전환한다.

```sql
-- SQL_S19_PUBLISH_RUN
UPDATE batch.BatchRun
   SET RunStatus = N'Succeeded',
       ResumeFromStepCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND RunStatus IN (N'Running', N'Restarting');
```

```sql
-- SQL_S19_RELEASE_RUN_LOCK
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = SYSUTCDATETIME()
 WHERE JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';
```

```sql
-- SQL_S19_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S19'
   AND CheckpointStatus = N'Pending';
```

```sql
-- SQL_S19_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = N'S19'
   AND StepStatus = N'Running';
```

**C# 배치 애플리케이션 제어 흐름**

```pseudocode
// 실행 컨텍스트
// runId: S02에서 IDENTITY로 발급된 batch.BatchRun.RunId
// batchYmd: S01에서 검증된 YYYYMMDD
currentStepErrorCode = 0
currentStatementName = null

conn = connectionFactory.open()
ensure every S19 database boundary runs under SNAPSHOT isolation

TRY:
    // S19 자기 제어 행을 먼저 생성한다.
    currentStepErrorCode = -9190
    currentStatementName = "SQL_S19_JOURNAL_START"
    conn.execute(
        SQL_S19_JOURNAL_START,
        { p_runId: runId }
    )

    currentStepErrorCode = -9190
    currentStatementName = "SQL_S19_CHECKPOINT_START"
    conn.execute(
        SQL_S19_CHECKPOINT_START,
        { p_runId: runId }
    )

    // 선행 단계, 실행 상태, 잠금 소유권 검증은 매 호출마다 수행한다.
    currentStepErrorCode = -9190
    currentStatementName = "SQL_S19_PUBLISH_PRECONDITION"
    publishState = conn.queryRow(
        SQL_S19_PUBLISH_PRECONDITION,
        { p_runId: runId, p_batchYmd: batchYmd }
    )

    IF publishState.AllPriorStepsSucceeded != 1
       OR publishState.RunIsPublishable != 1
       OR publishState.RunOwnsLock != 1:
        fail with statement name "SQL_S19_PUBLISH_PRECONDITION"

    finalTx = conn.beginTransaction()

    TRY:
        currentStepErrorCode = -9190
        currentStatementName = "SQL_S19_PUBLISH_RUN"
        affected = conn.execute(
            SQL_S19_PUBLISH_RUN,
            { p_runId: runId, p_batchYmd: batchYmd },
            finalTx
        )
        IF affected != 1:
            fail with statement name "SQL_S19_PUBLISH_RUN"

        currentStepErrorCode = -9190
        currentStatementName = "SQL_S19_RELEASE_RUN_LOCK"
        affected = conn.execute(
            SQL_S19_RELEASE_RUN_LOCK,
            { p_runId: runId, p_batchYmd: batchYmd },
            finalTx
        )
        IF affected != 1:
            fail with statement name "SQL_S19_RELEASE_RUN_LOCK"

        currentStepErrorCode = -9190
        currentStatementName = "SQL_S19_CHECKPOINT_SUCCEEDED"
        affected = conn.execute(
            SQL_S19_CHECKPOINT_SUCCEEDED,
            { p_runId: runId },
            finalTx
        )
        IF affected != 1:
            fail with statement name "SQL_S19_CHECKPOINT_SUCCEEDED"

        currentStepErrorCode = -9190
        currentStatementName = "SQL_S19_JOURNAL_SUCCEEDED"
        affected = conn.execute(
            SQL_S19_JOURNAL_SUCCEEDED,
            { p_runId: runId },
            finalTx
        )
        IF affected != 1:
            fail with statement name "SQL_S19_JOURNAL_SUCCEEDED"

        finalTx.commit()

    CATCH error:
        finalTx.rollbackIfOpen()
        rethrow error

CATCH error:
    errorMessage = currentStatementName + ": " + exact observed error text

    // finalTx가 열렸다면 이미 롤백되어 성공 게시와 잠금 해제는 남지 않는다.
    // 별도의 제어 기록 경계에서 실행 실패와 잠금 정리를 기록한다.
    failureTx = conn.beginTransaction()

    TRY:
        conn.execute(
            SQL_S19_RUN_FAILED,
            {
                p_runId: runId,
                p_batchYmd: batchYmd,
                p_errorMessage: errorMessage
            },
            failureTx
        )

        conn.execute(
            SQL_S19_RELEASE_LOCK_ON_FAILURE,
            { p_runId: runId, p_batchYmd: batchYmd },
            failureTx
        )

        conn.execute(
            SQL_S19_JOURNAL_FAILED,
            {
                p_runId: runId,
                p_legacyReturnCode: -9190,
                p_errorMessage: errorMessage
            },
            failureTx
        )

        failureTx.commit()
    CATCH cleanupError:
        failureTx.rollbackIfOpen()
        emit an operational alert containing both error and cleanupError

    // batch.BatchCheckpoint의 S19 행은 Pending으로 유지한다.
    stop the pipeline
```

**실패 제어 SQL**

최종 게시 실패 시 `batch.BatchRun`은 `Failed`로 기록하고, 재실행을 막는 고아 잠금이 남지 않도록 소유 잠금을 해제한다. S19 체크포인트는 `Pending`으로 유지한다.

```sql
-- SQL_S19_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       ResumeFromStepCode = N'S19',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND RunStatus IN (N'Running', N'Restarting');
```

```sql
-- SQL_S19_RELEASE_LOCK_ON_FAILURE
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = SYSUTCDATETIME()
 WHERE JobName = N'POQSettleBatch13'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';
```

```sql
-- SQL_S19_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = N'S19'
   AND StepStatus = N'Running';
```