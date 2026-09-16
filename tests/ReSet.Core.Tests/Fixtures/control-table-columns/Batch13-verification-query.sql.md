```sql
SELECT
    CheckCode,
    CheckName,
    ResultStatus,
    DifferenceCount,
    DetailMessage
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND ResultStatus = N'FAIL';
```
