> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S03 (S03 본문이 `BatchRunLock`에 쓰는데 그 표가 목차에 없습니다 - 목차가 선언한 것은 BatchRun, BatchStepJournal뿐입니다. 목차가 권한과 DDL 을 끌고 가므로, 승인된 표만 보고 권한을 잡으면 이 표에 권한이 없어 실행이 실패합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S03 — 배치 실행 등록

#### 책임과 인터페이스

S02의 실행 잠금 획득이 성공한 직후 실행하며 이후 단계와 병렬 실행하지 않는다. `batch.BatchRun`에 `POQSettleBatch16` 실행을 등록하고, 발급된 `RunId`로 S02가 예약값 `0`으로 보유한 `batch.BatchRunLock.OwnerRunId`를 승계한 뒤 S03의 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint` 행을 생성한다.

- 입력: `batchYmd` — `batch.BatchRun.BatchYmd VARCHAR(8)`에 매핑
- 고정값: `JobName = N'POQSettleBatch16'`, `StepCode = N'S03'`
- 출력: `batch.BatchRun.RunId`의 `IDENTITY` 발급값
- 잠금 승계: `batch.BatchRunLock`의 `OwnerRunId = 0`, `LockStatus = N'Held'`인 정확히 한 행을 발급된 `RunId`로 갱신
- 레거시 프로시저와 출력 파라미터는 없다. 재시작·건너뛰기·우회 입력도 추가하지 않는다.
- 모든 트랜잭션은 SNAPSHOT 격리에서 실행해야 한다.
- 제어 단계 일반 실패 코드는 **-9030**이다. `currentControlErrorCode`는 `0`으로 초기화하고 각 DML 직전에 `-9030`을 대입한다.
- 조회 및 DML에 `NOLOCK`을 사용하지 않는다.
- 청크 처리와 Shadow Table을 사용하지 않으며, 실패 후 보상 DELETE도 실행하지 않는다.

#### C# 실행 흐름

등록 트랜잭션은 `batch.BatchRun`, `batch.BatchRunLock`, `batch.BatchStepJournal`, `batch.BatchCheckpoint`의 등록·잠금 승계·시작 행을 함께 확정한다. 완료 상태 게시가 실패하면 해당 트랜잭션만 롤백한 뒤 시작 시 생성한 저널을 `Failed`로 전환하고 `batch.BatchRun`도 실패 처리한다.

```csharp
var conn = connectionFactory.open();

int currentControlErrorCode = 0;
string? currentStatementName = null;
long? runId = null;
bool registrationCommitted = false;

try
{
    conn.beginTransaction(); // SNAPSHOT 격리 의무

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_INSERT_BATCH_RUN";
    runId = conn.queryScalar<long>(SQL_INSERT_BATCH_RUN, new
    {
        p_batchYmd = batchYmd
    });

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_CLAIM_RUN_LOCK";
    var claimedLockRows = conn.execute(SQL_CLAIM_RUN_LOCK, new
    {
        p_runId = runId,
        p_batchYmd = batchYmd
    });

    if (claimedLockRows != 1)
    {
        throw new Exception(
            "SQL_CLAIM_RUN_LOCK affected " + claimedLockRows + " rows; expected exactly 1.");
    }

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_INSERT_S03_JOURNAL";
    conn.execute(SQL_INSERT_S03_JOURNAL, new
    {
        p_runId = runId
    });

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_INSERT_S03_CHECKPOINT";
    conn.execute(SQL_INSERT_S03_CHECKPOINT, new
    {
        p_runId = runId
    });

    conn.commit();
    registrationCommitted = true;

    conn.beginTransaction(); // SNAPSHOT 격리 의무

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_MARK_S03_JOURNAL_SUCCEEDED";
    conn.execute(SQL_MARK_S03_JOURNAL_SUCCEEDED, new
    {
        p_runId = runId
    });

    currentControlErrorCode = -9030;
    currentStatementName = "SQL_MARK_S03_CHECKPOINT_SUCCEEDED";
    conn.execute(SQL_MARK_S03_CHECKPOINT_SUCCEEDED, new
    {
        p_runId = runId
    });

    conn.commit();
    return runId.Value;
}
catch (Exception failure)
{
    conn.rollbackIfOpen();

    var errorMessage =
        sanitize(currentStatementName + ": " + failure.Message);

    if (registrationCommitted && runId != null)
    {
        conn.beginTransaction(); // 실패 기록도 SNAPSHOT 격리 의무

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_MARK_S03_JOURNAL_FAILED";
        conn.execute(SQL_MARK_S03_JOURNAL_FAILED, new
        {
            p_runId = runId,
            p_legacyReturnCode = currentControlErrorCode,
            p_errorMessage = errorMessage
        });

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_MARK_BATCH_RUN_FAILED";
        conn.execute(SQL_MARK_BATCH_RUN_FAILED, new
        {
            p_runId = runId,
            p_errorMessage = errorMessage
        });

        conn.commit();
    }
    else
    {
        // RunId 등록 트랜잭션이 롤백되면 S02의 예약 잠금은 OwnerRunId=0으로 복원된다.
        // 승인된 예약 잠금 해제 SQL을 실행하고 영향 행 수가 정확히 1인지 확인한다.
        conn.beginTransaction(); // SNAPSHOT 격리 의무

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_RELEASE_RESERVED_RUN_LOCK";
        var releasedLockRows = conn.execute(SQL_RELEASE_RESERVED_RUN_LOCK, new
        {
            p_batchYmd = batchYmd
        });

        if (releasedLockRows != 1)
        {
            throw new Exception(
                "SQL_RELEASE_RESERVED_RUN_LOCK affected " + releasedLockRows +
                " rows; expected exactly 1.");
        }

        conn.commit();

        // 등록 트랜잭션이 롤백되면 그 트랜잭션에서 발급된 IDENTITY 값은
        // 유효한 RunId로 재사용하지 않는다. 실패 감사용 RunId를 새로 발급한다.
        conn.beginTransaction(); // SNAPSHOT 격리 의무

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_INSERT_FAILED_BATCH_RUN";
        var failedRunId = conn.queryScalar<long>(SQL_INSERT_FAILED_BATCH_RUN, new
        {
            p_batchYmd = batchYmd,
            p_errorMessage = errorMessage
        });

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_INSERT_FAILED_S03_JOURNAL";
        conn.execute(SQL_INSERT_FAILED_S03_JOURNAL, new
        {
            p_runId = failedRunId,
            p_legacyReturnCode = currentControlErrorCode,
            p_errorMessage = errorMessage
        });

        currentControlErrorCode = -9030;
        currentStatementName = "SQL_INSERT_FAILED_S03_CHECKPOINT";
        conn.execute(SQL_INSERT_FAILED_S03_CHECKPOINT, new
        {
            p_runId = failedRunId
        });

        conn.commit();
    }

    throw;
}
```

#### 애플리케이션 전송 SQL

S03은 레거시 DML을 대체하지 않는 제어 단계이므로 문장 앵커를 부여하지 않는다.

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
    N'POQSettleBatch16',
    @p_batchYmd,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
```

`RunId` 발급 직후 같은 등록 트랜잭션에서 S02의 예약 잠금을 실제 실행 소유권으로 승계한다. 애플리케이션은 영향 행 수가 정확히 `1`임을 확인해야 하며, `0` 또는 복수 행이면 등록 트랜잭션을 커밋하지 않는다.

```sql
-- SQL_CLAIM_RUN_LOCK
UPDATE batch.BatchRunLock
   SET OwnerRunId = @p_runId,
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
 WHERE JobName = N'POQSettleBatch16'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = CAST(0 AS BIGINT)
   AND LockStatus = N'Held';
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
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
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
   AND StepCode = N'S03';
```

```sql
-- SQL_MARK_S03_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S03';
```

```sql
-- SQL_MARK_S03_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = N'S03';
```

```sql
-- SQL_MARK_BATCH_RUN_FAILED
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       ResumeFromStepCode = N'S03',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch16';
```

등록 트랜잭션이 `RunId` 확정 전에 실패하면 `OwnerRunId = 0`인 예약 잠금이 남는다. 승인된 해제 절차는 아래 SQL만 사용하며, 동일 작업·정산일에 `Running` 실행이 없고 예약 잠금 한 행만 존재할 때 `Released`로 전환한다. 애플리케이션은 영향 행 수가 정확히 `1`인지 확인하고, 연결 장애로 자동 복구가 불가능하면 운영 복구 실행이 새 연결에서 동일 SQL을 재실행한다.

```sql
-- SQL_RELEASE_RESERVED_RUN_LOCK
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = SYSUTCDATETIME()
 WHERE JobName = N'POQSettleBatch16'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = CAST(0 AS BIGINT)
   AND LockStatus = N'Held'
   AND NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchRun
            WHERE JobName = N'POQSettleBatch16'
              AND BatchYmd = @p_batchYmd
              AND RunStatus = N'Running'
       );
```

등록 트랜잭션 자체가 실패한 경우에도 실패 이력을 남길 수 있도록 새 `RunId`를 발급한다. 롤백된 트랜잭션에서 관찰한 IDENTITY 값은 재사용하지 않는다.

```sql
-- SQL_INSERT_FAILED_BATCH_RUN
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
    N'POQSettleBatch16',
    @p_batchYmd,
    N'Failed',
    N'S03',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    @p_errorMessage
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
```

```sql
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
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    @p_errorMessage
);
```

```sql
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

#### 완료 조건

다음 조회가 각각 한 행을 반환하고 상태가 일치해야 S04에 `RunId`를 전달한다.

```sql
-- SQL_VERIFY_S03_REGISTRATION
SELECT RunId,
       JobName,
       BatchYmd,
       RunStatus
  FROM batch.BatchRun
 WHERE RunId = @p_runId
   AND JobName = N'POQSettleBatch16'
   AND BatchYmd = @p_batchYmd
   AND RunStatus = N'Running';

SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch16'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';

SELECT RunId,
       StepCode,
       StepStatus,
       LegacyReturnCode
  FROM batch.BatchStepJournal
 WHERE RunId = @p_runId
   AND StepCode = N'S03'
   AND StepStatus = N'Succeeded';

SELECT RunId,
       StepCode,
       CheckpointStatus
  FROM batch.BatchCheckpoint
 WHERE RunId = @p_runId
   AND StepCode = N'S03'
   AND CheckpointStatus = N'Succeeded';
```