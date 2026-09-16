### S02 — 영업일 실행 잠금 획득

(B17 S22 의 `SQL_RELEASE_RUN_LOCK`·`SQL_VERIFY_RUN_LOCK_RELEASE` 펜스를 **발급 전 절 자리에 놓은 것** — SQL 은 실물이고 자리만 옮겼다. README 참고)

```sql
-- SQL_RELEASE_RUN_LOCK
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = COALESCE(ReleasedAtUtc, SYSUTCDATETIME())
 WHERE JobName = N'POQSettleBatch17'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_ownerRunId
   AND LockStatus IN (N'Held', N'Released');

-- SQL_VERIFY_RUN_LOCK_RELEASE
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus,
       ReleasedAtUtc
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch17'
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_ownerRunId;
```
