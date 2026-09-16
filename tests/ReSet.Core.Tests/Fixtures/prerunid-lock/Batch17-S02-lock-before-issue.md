### S02 — 영업일 실행 잠금 획득

**대상 테이블:** `batch.BatchRunLock`  
**일반 실패 코드:** `-9020`

#### 실행 전제와 인터페이스

이 단계는 레거시 프로시저를 대체하지 않는 제어 단계이며 입력은 다음으로 한정한다.

- `p_jobName`: `NVARCHAR(128)`, 고정값 `N'POQSettleBatch17'`
- `p_batchYmd`: `VARCHAR(8)`, S01에서 검증한 영업일
- `p_ownerRunId`: `BIGINT`, `batch.BatchRun.RunId`가 발급한 현재 실행 식별자

재시작·건너뛰기·잠금 우회를 위한 입력은 추가하지 않는다. 동일한 `(JobName, BatchYmd)`에는 하나의 잠금 행만 존재하도록 `batch.BatchRunLock`의 유일성이 보장되어야 한다.

> **선행 설계 차단 사항:** 현재 승인 순서는 S02 다음 S03에서 `batch.BatchRun`을 최초 INSERT하도록 정의되어 있으므로, S02 실행 시점에는 유효한 `RunId`가 존재하지 않는다. 그러나 `batch.BatchRunLock.OwnerRunId`는 `BIGINT NOT NULL`이며 임의값이나 예상 ID를 사용할 수 없다. 따라서 현재 순서 그대로는 S02를 구현할 수 없다. `batch.BatchRun`의 IDENTITY 발급을 S02보다 먼저 완료하도록 제어 단계 순서를 조정한 뒤 아래 잠금 로직을 활성화해야 한다. 조정 전에는 `0`, `-1`, `IDENT_CURRENT`, 예상 ID 등을 `OwnerRunId`로 저장하지 않고 S02를 `-9020`으로 중단한다.

#### C# 실행 절차

전체 단계는 **SNAPSHOT 격리 수준의 단일 트랜잭션**으로 실행한다. 모든 조회에서 `NOLOCK`을 제거하며, 성공한 잠금 획득과 소유권 검증을 같은 트랜잭션에서 완료한다.

```csharp
void acquireBusinessDayLock(context)
{
    int currentStepErrorCode = 0;

    if (context.RunId == null)
    {
        // 현재 승인 순서의 계약 충돌이다. 가짜 OwnerRunId를 만들지 않는다.
        recordPreRunControlFailure(
            stepCode: "S02",
            legacyReturnCode: -9020,
            message: "batch.BatchRun.RunId가 발급되기 전에 실행 잠금을 획득할 수 없습니다.");
        throw controlFailure(-9020);
    }

    var conn = connectionFactory.open();
    var tx = conn.beginTransaction(); // SNAPSHOT 의무 충족

    try
    {
        var lockRow = repository.queryRow(
            conn,
            SQL_READ_RUN_LOCK,
            {
                p_jobName: "POQSettleBatch17",
                p_batchYmd: context.BusinessYmd
            });

        if (lockRow == null)
        {
            currentStepErrorCode = -9020;
            var result = repository.execute(
                conn,
                SQL_INSERT_RUN_LOCK,
                {
                    p_jobName: "POQSettleBatch17",
                    p_batchYmd: context.BusinessYmd,
                    p_ownerRunId: context.RunId
                });

            if (result.affectedRows != 1)
                throw controlFailure(-9020);
        }
        else if (lockRow.LockStatus == "Released")
        {
            currentStepErrorCode = -9020;
            var result = repository.execute(
                conn,
                SQL_REACQUIRE_RELEASED_LOCK,
                {
                    p_jobName: "POQSettleBatch17",
                    p_batchYmd: context.BusinessYmd,
                    p_ownerRunId: context.RunId
                });

            if (result.affectedRows != 1)
                throw controlFailure(-9020);
        }
        else if (lockRow.LockStatus == "Held"
                 && lockRow.OwnerRunId == context.RunId)
        {
            // 잠금 커밋 후 상태 게시 전에 재진입한 동일 실행을 멱등하게 수용한다.
            currentStepErrorCode = -9020;
            var result = repository.execute(
                conn,
                SQL_REFRESH_OWN_LOCK,
                {
                    p_jobName: "POQSettleBatch17",
                    p_batchYmd: context.BusinessYmd,
                    p_ownerRunId: context.RunId
                });

            if (result.affectedRows != 1)
                throw controlFailure(-9020);
        }
        else
        {
            // 다른 실행이 보유 중인 잠금은 탈취하지 않는다.
            throw controlFailure(-9020);
        }

        var owned = repository.queryScalar(
            conn,
            SQL_VERIFY_RUN_LOCK,
            {
                p_jobName: "POQSettleBatch17",
                p_batchYmd: context.BusinessYmd,
                p_ownerRunId: context.RunId
            });

        if (!owned)
            throw controlFailure(-9020);

        tx.commit();
    }
    catch (Exception error)
    {
        tx.rollback();

        int journalCode =
            currentStepErrorCode == 0
                ? -9020
                : currentStepErrorCode;

        recordControlFailure("S02", journalCode, error);
        throw;
    }
}
```

신규 행을 동시에 삽입하려는 실행은 `(JobName, BatchYmd)` 유일성 충돌 또는 SNAPSHOT 쓰기 충돌 중 하나만 성공하도록 처리한다. 충돌한 실행은 트랜잭션을 롤백하고 `-9020`으로 종료하며, 기존 잠금의 `OwnerRunId`를 변경하지 않는다.

```sql
-- SQL_READ_RUN_LOCK
SELECT OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc,
       ReleasedAtUtc
  FROM batch.BatchRunLock
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
    @p_ownerRunId,
    N'Held',
    SYSUTCDATETIME(),
    SYSUTCDATETIME(),
    NULL
);
```

```sql
-- SQL_REACQUIRE_RELEASED_LOCK
UPDATE batch.BatchRunLock
   SET OwnerRunId = @p_ownerRunId,
       LockStatus = N'Held',
       AcquiredAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = NULL
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND LockStatus = N'Released';
```

```sql
-- SQL_REFRESH_OWN_LOCK
UPDATE batch.BatchRunLock
   SET HeartbeatAtUtc = SYSUTCDATETIME()
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_ownerRunId
   AND LockStatus = N'Held';
```

```sql
-- SQL_VERIFY_RUN_LOCK
SELECT CASE
           WHEN EXISTS
                (
                    SELECT 1
                      FROM batch.BatchRunLock
                     WHERE JobName = @p_jobName
                       AND BatchYmd = @p_batchYmd
                       AND OwnerRunId = @p_ownerRunId
                       AND LockStatus = N'Held'
                )
           THEN CAST(1 AS BIT)
           ELSE CAST(0 AS BIT)
       END;
```

#### 실패 및 재시작 처리

- 다른 실행이 같은 영업일 잠금을 보유하면 `-9020`으로 실패시키고 파이프라인을 중단한다.
- INSERT·재획득 UPDATE·하트비트 UPDATE 직전에 `currentStepErrorCode = -9020`을 설정한다.
- 잠금 DML 또는 소유권 검증 실패 시 열린 트랜잭션만 롤백한다. 롤백 후 보상 DELETE를 수행하지 않는다.
- 유효한 `RunId`가 아직 없는 현재 승인 순서에서는 `batch.BatchStepJournal`이나 `batch.BatchCheckpoint`에 행을 기록하지 않는다. 순서가 교정되어 실행 ID가 선발급된 이후에는 공통 제어 단계 저널 규약을 적용한다.
- 동일 `OwnerRunId`가 이미 `Held` 상태라면 잠금을 새로 INSERT하지 않고 하트비트만 갱신하여 재진입을 멱등하게 처리한다.
- 다른 `OwnerRunId`가 보유한 `Held` 행은 시간 경과만으로 탈취하지 않는다. 비정상 잠금 해제는 별도 운영 승인 후 S22 계약으로 처리한다.
