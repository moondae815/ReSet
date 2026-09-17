```sql
-- SQL_CREATE_AND_CAPTURE_SHADOW
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.<Table>_' + CAST(@p_runId AS NVARCHAR(20)) + N'_<StepCode>';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM <SchemaTable> WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N' SELECT * FROM <SchemaTable> WHERE <RangeColumn> = @p_batchYmd;';
EXEC sp_executesql @v_sql, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;
```
