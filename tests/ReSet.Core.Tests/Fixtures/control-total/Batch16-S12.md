### S12 — 최종 정산원장 장벽 확정

`S11` 완료 후 `S13` 실행 전에 동기식으로 수행한다. `S04`~`S11`과 병렬 실행하지 않으며, 모든 선행 단계의 저널과 체크포인트가 `Succeeded`인지 확인한 뒤 최종 정산원장 통제 합계를 고정한다.

#### 인터페이스 및 실행 조건

- `runId BIGINT -> p_runId`
- `batchYmd VARCHAR(8) -> p_batchYmd`
- 단계 코드는 애플리케이션 상수 `S12`로 사용한다.
- 레거시 프로시저와 출력 파라미터는 없다. 성공 시 `batch.BatchStepJournal.LegacyReturnCode`는 `NULL`, 실패 시 일반 오류 코드 `-9120`을 기록한다.
- 선행 단계 확인은 호출될 때마다 무조건 수행하며 재시작 우회 또는 검증 생략 입력은 두지 않는다.
- 시작 기록, 업무 처리, 실패 기록을 포함한 모든 트랜잭션은 SNAPSHOT 격리에서 실행되어야 한다. 모든 조회에서 `NOLOCK`과 `WITH (NOLOCK)`은 제거한다.
- 청크 처리는 적용하지 않는다. 선행 상태와 원장 통제 합계가 하나의 일관된 스냅샷에서 확정되어야 하기 때문이다.

#### 처리 의사코드

```csharp
void executeS12(long runId, string batchYmd)
{
    const string stepCode = "S12";

    // 제어 단계 오류 변수는 반드시 0으로 시작한다.
    int currentControlErrorCode = 0;
    string currentStatementName = null;
    bool startRowsCommitted = false;

    var conn = connectionFactory.open();

    try
    {
        // 이 트랜잭션도 SNAPSHOT 격리 의무가 있다.
        conn.beginTransaction();

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_INSERT_S12_JOURNAL_START";
        conn.execute(SQL_INSERT_S12_JOURNAL_START, new
        {
            p_runId = runId
        });

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_INSERT_S12_CHECKPOINT_START";
        conn.execute(SQL_INSERT_S12_CHECKPOINT_START, new
        {
            p_runId = runId
        });

        conn.commit();
        startRowsCommitted = true;

        // 장벽 확인, 통제 합계 적재, 성공 상태 게시를 하나의 트랜잭션으로 확정한다.
        conn.beginTransaction(); // SNAPSHOT 격리 의무

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_VERIFY_PREDECESSORS";
        var incompleteSteps = conn.queryRows(SQL_VERIFY_PREDECESSORS, new
        {
            p_runId = runId
        });

        if (incompleteSteps.Count != 0)
        {
            throw failure(
                "선행 단계 미완료: " +
                joinStepCodes(incompleteSteps)
            );
        }

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_INSERT_S12_CONTROL_TOTALS";
        conn.execute(SQL_INSERT_S12_CONTROL_TOTALS, new
        {
            p_runId = runId,
            p_batchYmd = batchYmd
        });

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_MARK_S12_JOURNAL_SUCCEEDED";
        conn.execute(SQL_MARK_S12_JOURNAL_SUCCEEDED, new
        {
            p_runId = runId
        });

        currentControlErrorCode = -9120;
        currentStatementName = "SQL_MARK_S12_CHECKPOINT_SUCCEEDED";
        conn.execute(SQL_MARK_S12_CHECKPOINT_SUCCEEDED, new
        {
            p_runId = runId
        });

        conn.commit();
    }
    catch (Exception failure)
    {
        conn.rollbackIfOpen();

        if (startRowsCommitted)
        {
            conn.beginTransaction(); // 실패 기록도 SNAPSHOT 격리 의무

            conn.execute(SQL_MARK_S12_FAILED, new
            {
                p_runId = runId,
                p_legacyReturnCode = currentControlErrorCode,
                p_errorMessage = sanitize(
                    currentStatementName + ": " + failure.Message
                )
            });

            conn.commit();
        }

        throw;
    }
}
```

단계 시작 행은 업무 트랜잭션보다 먼저 확정한다.

```sql
-- SQL_INSERT_S12_JOURNAL_START
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
    N'S12',
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
-- SQL_INSERT_S12_CHECKPOINT_START
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
    N'S12',
    N'Pending',
    NULL
);
```

선행 장벽은 `S04`~`S11` 각각에 대해 `batch.BatchCheckpoint`와 `batch.BatchStepJournal` 양쪽의 성공 상태를 확인한다. SQL은 미완료 단계 목록만 반환하고, 중단 여부는 애플리케이션이 결정한다.

```sql
-- SQL_VERIFY_PREDECESSORS
WITH ExpectedSteps AS
(
    SELECT StepCode
      FROM
      (
          VALUES
              (N'S04'),
              (N'S05'),
              (N'S06'),
              (N'S07'),
              (N'S08'),
              (N'S09'),
              (N'S10'),
              (N'S11')
      ) AS E(StepCode)
)
SELECT E.StepCode
  FROM ExpectedSteps AS E
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchCheckpoint AS C
            WHERE C.RunId = @p_runId
              AND C.StepCode = E.StepCode
              AND C.CheckpointStatus = N'Succeeded'
       )
    OR NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchStepJournal AS J
            WHERE J.RunId = @p_runId
              AND J.StepCode = E.StepCode
              AND J.StepStatus = N'Succeeded'
       )
 ORDER BY E.StepCode;
```

장벽 통과 후 `batch.BatchControlTotal`에 선행 성공 단계 수와 최종 `SETTLE_POQ_DB.dbo.TSettleMst`의 건수·금액 통제를 INSERT한다. 일반 정산원장은 `YMD = @p_batchYmd`, 추가정산 원장은 `ProcYMD = @p_batchYmd AND ExtraSettleFlag = 1` 범위로 구분한다.

```sql
-- SQL_INSERT_S12_CONTROL_TOTALS
WITH PredecessorTotal AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS ControlValue
      FROM
      (
          VALUES
              (N'S04'),
              (N'S05'),
              (N'S06'),
              (N'S07'),
              (N'S08'),
              (N'S09'),
              (N'S10'),
              (N'S11')
      ) AS E(StepCode)
     WHERE EXISTS
           (
               SELECT 1
                 FROM batch.BatchCheckpoint AS C
                WHERE C.RunId = @p_runId
                  AND C.StepCode = E.StepCode
                  AND C.CheckpointStatus = N'Succeeded'
           )
),
LedgerTotal AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
           CAST(COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0)
                AS DECIMAL(38,4)) AS TxAmtValue,
           CAST(COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0)
                AS DECIMAL(38,4)) AS CLTotalValue,
           CAST(COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0)
                AS DECIMAL(38,4)) AS PGTotalValue,
           CAST(COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0)
                AS DECIMAL(38,4)) AS POQIncomeValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
),
ExtraLedgerTotal AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE ProcYMD = @p_batchYmd
       AND ExtraSettleFlag = 1
)
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId,
       N'S12',
       N'PredecessorSucceededCount',
       ControlValue,
       SYSUTCDATETIME()
  FROM PredecessorTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerRowCount',
       RowCountValue,
       SYSUTCDATETIME()
  FROM LedgerTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerTxAmt',
       TxAmtValue,
       SYSUTCDATETIME()
  FROM LedgerTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerCLTotal',
       CLTotalValue,
       SYSUTCDATETIME()
  FROM LedgerTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerPGTotal',
       PGTotalValue,
       SYSUTCDATETIME()
  FROM LedgerTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerPOQIncome',
       POQIncomeValue,
       SYSUTCDATETIME()
  FROM LedgerTotal
UNION ALL
SELECT @p_runId,
       N'S12',
       N'ExtraLedgerRowCount',
       RowCountValue,
       SYSUTCDATETIME()
  FROM ExtraLedgerTotal;
```

업무 통제 합계와 단계 성공 상태는 같은 트랜잭션에서 확정한다.

```sql
-- SQL_MARK_S12_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = N'S12';
```

```sql
-- SQL_MARK_S12_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S12';
```

실패 시 업무 트랜잭션을 롤백하므로 `batch.BatchControlTotal`에는 이 실행의 부분 통제가 남지 않는다. 시작 시 확정된 체크포인트는 `Pending`으로 유지하고, 실패 지점의 SQL 이름과 `-9120`을 저널에 기록한다.

```sql
-- SQL_MARK_S12_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = N'S12';
```

이 단계는 분할 커밋이나 집계 테이블 교체를 수행하지 않는다. 따라서 Shadow Table과 롤백 후 보상 DELETE를 사용하지 않으며, 실패 시 단일 업무 트랜잭션 롤백으로 `batch.BatchControlTotal`, `batch.BatchStepJournal`, `batch.BatchCheckpoint`의 장벽 확정 변경을 원상 복구한다.
