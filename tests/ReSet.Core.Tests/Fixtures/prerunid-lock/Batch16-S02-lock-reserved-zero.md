### S02 — 정산일 실행 잠금 획득

#### 목적 및 인터페이스

`POQSettleBatch16`의 동일 정산일 실행을 하나로 직렬화한다. 이 단계는 `S03`보다 먼저 실행되므로 아직 `RunId`가 없으며, `batch.BatchStepJournal`과 `batch.BatchCheckpoint`에는 행을 기록하지 않는다.

- 호출 형태: `executeS02(batchYmd)`
- `batchYmd`: `S01`에서 검증된 8자리 정산일
- `JobName`: 애플리케이션 상수 `POQSettleBatch16`
- 재시작·우회·강제 잠금 탈취 입력은 추가하지 않는다.
- 대상 테이블: `batch.BatchRunLock`
- 일반 실패 코드: `-9020`

`batch.BatchRunLock.OwnerRunId`는 `NOT NULL`이지만 실제 `RunId`는 `S03`에서만 발급된다. 따라서 잠금 획득 시에는 실제 실행 ID로 해석하지 않는 예약값 `0`을 사용하고, `S03`이 `batch.BatchRun`의 IDENTITY 값을 발급받은 직후 실제 `RunId`로 소유권을 승계해야 한다. `0`을 실행 ID로 계산하거나 게시해서는 안 된다.

#### 동시성 및 트랜잭션 정책

- 전체 잠금 획득은 하나의 트랜잭션에서 수행하고 해당 트랜잭션은 **SNAPSHOT 격리**로 실행되어야 한다.
- `(JobName, BatchYmd)` 조합은 물리적으로 유일해야 한다.
- 행이 없으면 `Held` 상태로 INSERT한다.
- 기존 행이 `Released`이면 같은 행을 `Held`로 갱신한다.
- 기존 행이 `Held`이면 소유자를 덮어쓰거나 자동 탈취하지 않고 `-9020`으로 중단한다.
- 동시 INSERT의 유일성 충돌이나 동시 UPDATE의 SNAPSHOT 쓰기 충돌도 `-9020`으로 처리한다.
- 모든 SQL에서 `NOLOCK` 및 `WITH (NOLOCK)`을 제거하며 사용하지 않는다.
- 단일 트랜잭션으로 완료되므로 Shadow Table, 분할 커밋, 실패 후 보상 DELETE를 사용하지 않는다.

```sql
-- SQL_REACQUIRE_RELEASED_LOCK
UPDATE batch.BatchRunLock
   SET OwnerRunId = CAST(0 AS BIGINT),
       LockStatus = N'Held',
       AcquiredAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
OUTPUT inserted.JobName,
       inserted.BatchYmd,
       inserted.OwnerRunId,
       inserted.LockStatus
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_ymd
   AND LockStatus = N'Released';

-- SQL_READ_CURRENT_LOCK
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc,
       ReleasedAtUtc
  FROM batch.BatchRunLock
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_ymd;

-- SQL_INSERT_NEW_LOCK
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
OUTPUT inserted.JobName,
       inserted.BatchYmd,
       inserted.OwnerRunId,
       inserted.LockStatus
VALUES
(
    @p_jobName,
    @p_ymd,
    CAST(0 AS BIGINT),
    N'Held',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    NULL
);

-- SQL_VALIDATE_ACQUIRED_LOCK
SELECT COUNT_BIG(*) AS LockCount
  FROM batch.BatchRunLock
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_ymd
   AND OwnerRunId = CAST(0 AS BIGINT)
   AND LockStatus = N'Held';
```

#### C# 실행 의사코드

SQL 결과에 따른 제어 흐름은 애플리케이션이 담당한다. 각 DML 직전에 단계 오류 상태를 `-9020`으로 설정하며, SQL 문장 안에는 결과 분기나 오류 처리 구문을 넣지 않는다.

```csharp
const string jobName = "POQSettleBatch16";
const string stepCode = "S02";

int currentControlErrorCode = 0;
var conn = connectionFactory.open();

conn.beginTransaction(); // SNAPSHOT 격리 보장 의무

try
{
    currentControlErrorCode = -9020;
    var acquired = conn.queryRows(SQL_REACQUIRE_RELEASED_LOCK, new
    {
        p_jobName = jobName,
        p_ymd = batchYmd
    });

    if (acquired.Count == 0)
    {
        var existing = conn.queryRow(SQL_READ_CURRENT_LOCK, new
        {
            p_jobName = jobName,
            p_ymd = batchYmd
        });

        if (existing != null)
        {
            // Held 행은 덮어쓰거나 자동 탈취하지 않는다.
            throwFailure(
                code: currentControlErrorCode,
                message: "동일 정산일의 실행 잠금이 이미 Held 상태임"
            );
        }

        currentControlErrorCode = -9020;
        acquired = conn.queryRows(SQL_INSERT_NEW_LOCK, new
        {
            p_jobName = jobName,
            p_ymd = batchYmd
        });
    }

    if (acquired.Count != 1)
    {
        throwFailure(
            code: currentControlErrorCode,
            message: "정산일 실행 잠금 행을 정확히 한 건 확보하지 못함"
        );
    }

    var lockCount = conn.queryScalar(SQL_VALIDATE_ACQUIRED_LOCK, new
    {
        p_jobName = jobName,
        p_ymd = batchYmd
    });

    if (lockCount != 1)
    {
        throwFailure(
            code: currentControlErrorCode,
            message: "잠금 획득 후 상태 검증 실패"
        );
    }

    conn.commit();
}
catch (Exception failure)
{
    conn.rollback();

    // S03 이전이므로 RunId와 단계 저널 행이 없다.
    // 호출 결과와 운영 로그에 -9020 및 정제된 오류 내용을 전달한다.
    publishPreRunFailure(
        stepCode: stepCode,
        errorCode: currentControlErrorCode == 0 ? -9020 : currentControlErrorCode,
        errorMessage: sanitize(failure)
    );

    throw;
}
```

#### 재시작 및 장애 복구

- 잠금 획득 트랜잭션 자체가 실패하면 롤백만 수행하므로 `batch.BatchRunLock`은 변경 전 상태로 복원된다.
- 잠금 획득 후 프로세스가 중단되면 `Held` 행을 자동으로 탈취하지 않는다. `HeartbeatAtUtc`와 실제 실행 프로세스 부재를 운영 절차로 확인한 뒤 승인된 잠금 해제 흐름을 수행한다.
- `S03`이 실행 ID를 발급하기 전에 실패한 경우 잠금 소유 예약값은 `0`이다. 이 경우에도 임의 DELETE가 아니라 `JobName`, `BatchYmd`, `LockStatus`, `OwnerRunId`를 모두 확인하는 승인된 해제 절차를 사용한다.
- 정상 파이프라인에서는 `S21`이 `batch.BatchRunLock`을 `Released`로 변경할 때까지 같은 정산일의 후속 실행을 허용하지 않는다.
