### S02 — 정산일 실행 잠금 획득

(B16 S03 의 `SQL_CLAIM_RUN_LOCK` 펜스를 **발급 전 절 자리에 놓은 것** — SQL 은 실물이고 자리만 옮겼다. README 참고)

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
