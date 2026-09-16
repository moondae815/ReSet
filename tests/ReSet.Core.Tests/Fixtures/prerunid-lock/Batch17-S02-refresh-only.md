### S02 — 영업일 실행 잠금 획득

(실물 절에서 `SQL_REFRESH_OWN_LOCK` 펜스 하나만 오려 온 것 — README 참고)

```sql
-- SQL_REFRESH_OWN_LOCK
UPDATE batch.BatchRunLock
   SET HeartbeatAtUtc = SYSUTCDATETIME()
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_ownerRunId
   AND LockStatus = N'Held';
```
