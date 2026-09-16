```sql
-- SQL_VALIDATE_EXCEPTION_RULE_CONTROLS
WITH RequiredRules AS
(
    SELECT V.RuleCode
      FROM
      (
          VALUES
              (N'UPDATE01'), (N'UPDATE02'), (N'UPDATE03'),
              (N'UPDATE04'), (N'UPDATE05'), (N'UPDATE06'),
              (N'UPDATE07'), (N'UPDATE08'), (N'UPDATE09'),
              (N'UPDATE10'), (N'UPDATE11'), (N'UPDATE12'),
              (N'UPDATE13'), (N'UPDATE14'), (N'UPDATE15'),
              (N'UPDATE16'), (N'UPDATE17'), (N'UPDATE18')
      ) AS V(RuleCode)
)
SELECT R.RuleCode
  FROM RequiredRules AS R
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchControlTotal AS C
            WHERE C.RunId = @p_runId
              AND C.StepCode = N'S07'
              AND C.ControlName = N'Rule_' + R.RuleCode + N'_Rows'
       );
```
