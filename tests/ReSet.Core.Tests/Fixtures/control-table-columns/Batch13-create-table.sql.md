```sql
CREATE TABLE batch.BatchReconciliation
(
    RunId                 BIGINT         NOT NULL,
    StepCode              NVARCHAR(10)   NOT NULL,
    ReconciliationName    NVARCHAR(64)   NOT NULL,
    ExpectedValue         DECIMAL(38,4)  NOT NULL,
    ActualValue           DECIMAL(38,4)  NOT NULL,
    DifferenceValue       DECIMAL(38,4)  NOT NULL,
    IsMatched              BIT            NOT NULL,
    CheckedAtUtc           DATETIME2(3)   NOT NULL,
    CONSTRAINT PK_BatchReconciliation
        PRIMARY KEY (RunId, StepCode, ReconciliationName, CheckedAtUtc)
);
```
