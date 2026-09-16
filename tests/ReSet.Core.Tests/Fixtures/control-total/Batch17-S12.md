### S12 | 정산 원장 동결

#### 목적 및 실행 계약

S04∼S11에서 구축한 `SETTLE_POQ_DB.dbo.TSettleMst`의 건수·금액·상태별 통제 합계를 `batch.BatchControlTotal`에 기록하고, 이후 단계가 동일한 원장 기준으로 통계와 요약을 생성하도록 논리적으로 동결한다.

- 선행 단계 S11이 성공한 뒤 순차 실행한다.
- 실행 컨텍스트의 `RunId`와 `BusinessYmd`를 사용하며 재시작·우회용 입력은 추가하지 않는다.
- 이 단계 이후 S13∼S22는 `SETTLE_POQ_DB.dbo.TSettleMst`를 변경하지 않는다.
- 모든 데이터베이스 경계는 **SNAPSHOT 격리 수준**을 충족해야 한다.
- 청크를 사용하지 않으며 통제 합계 적재, 검증, `batch.BatchCheckpoint` 성공 전환, `batch.BatchStepJournal` 성공 전환을 하나의 트랜잭션으로 처리한다.
- 일반 실패 코드는 **-9120**이다. 단계 지역 오류 변수는 `0`으로 초기화하고 각 실패 가능 문장 직전에 `-9120`을 대입한다.
- 성공 시 별도의 레거시 반환 코드는 없으므로 `batch.BatchStepJournal.LegacyReturnCode`는 `NULL`로 기록한다.

#### C# 실행 의사코드

```csharp
void executeS12(batchContext context)
{
    const string stepCode = "S12";
    int currentStepErrorCode = 0;
    string failedStatementName = null;
    var startedAtUtc = clock.utcNow();

    var controlConn = connectionFactory.open();

    // 시작 저널은 실패한 본 트랜잭션과 함께 사라지지 않도록 먼저 확정한다.
    {
        var startTx = controlConn.beginTransaction(); // SNAPSHOT 의무
        try
        {
            currentStepErrorCode = -9120;
            failedStatementName = "SQL_S12_JOURNAL_START";
            repository.execute(
                controlConn,
                SQL_S12_JOURNAL_START,
                {
                    p_runId: context.RunId,
                    p_stepCode: stepCode,
                    p_startedAtUtc: startedAtUtc
                });

            startTx.commit();
        }
        catch
        {
            startTx.rollback();
            throw;
        }
    }

    // 실패 후 재호출될 때 기존 Pending 행을 중복 생성하지 않는다.
    {
        var checkpointTx = controlConn.beginTransaction(); // SNAPSHOT 의무
        try
        {
            currentStepErrorCode = -9120;
            failedStatementName = "SQL_S12_CHECKPOINT_PENDING";
            repository.execute(
                controlConn,
                SQL_S12_CHECKPOINT_PENDING,
                {
                    p_runId: context.RunId,
                    p_stepCode: stepCode
                });

            checkpointTx.commit();
        }
        catch (Exception error)
        {
            checkpointTx.rollback();
            recordS12Failure(
                controlConn,
                context.RunId,
                stepCode,
                startedAtUtc,
                currentStepErrorCode,
                failedStatementName,
                error);
            throw;
        }
    }

    var conn = connectionFactory.open();
    var tx = conn.beginTransaction(); // SNAPSHOT 의무

    try
    {
        currentStepErrorCode = -9120;
        failedStatementName = "SQL_S12_CAPTURE_CONTROL_TOTALS";
        repository.execute(
            conn,
            SQL_S12_CAPTURE_CONTROL_TOTALS,
            {
                p_runId: context.RunId,
                p_stepCode: stepCode,
                p_ymd: context.BusinessYmd
            });

        failedStatementName = "SQL_S12_READ_FRESH_TOTALS";
        var freshTotals = repository.queryRows(
            conn,
            SQL_S12_READ_FRESH_TOTALS,
            { p_ymd: context.BusinessYmd });

        failedStatementName = "SQL_S12_READ_CAPTURED_TOTALS";
        var capturedTotals = repository.queryRows(
            conn,
            SQL_S12_READ_CAPTURED_TOTALS,
            {
                p_runId: context.RunId,
                p_stepCode: stepCode
            });

        // 이름 집합과 decimal(38,4) 값을 정확히 비교한다.
        // 누락, 중복 또는 값 불일치가 하나라도 있으면 예외로 처리한다.
        assertExactControlTotals(freshTotals, capturedTotals);

        currentStepErrorCode = -9120;
        failedStatementName = "SQL_S12_CHECKPOINT_SUCCESS";
        repository.execute(
            conn,
            SQL_S12_CHECKPOINT_SUCCESS,
            {
                p_runId: context.RunId,
                p_stepCode: stepCode
            });

        currentStepErrorCode = -9120;
        failedStatementName = "SQL_S12_JOURNAL_SUCCESS";
        repository.execute(
            conn,
            SQL_S12_JOURNAL_SUCCESS,
            {
                p_runId: context.RunId,
                p_stepCode: stepCode,
                p_startedAtUtc: startedAtUtc
            });

        tx.commit();
    }
    catch (Exception error)
    {
        tx.rollback();

        recordS12Failure(
            controlConn,
            context.RunId,
            stepCode,
            startedAtUtc,
            currentStepErrorCode == 0 ? -9120 : currentStepErrorCode,
            failedStatementName,
            error);

        throw;
    }
}
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_S12_JOURNAL_START
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
    @p_stepCode,
    N'Running',
    NULL,
    @p_startedAtUtc,
    NULL,
    NULL
);
```

```sql
-- SQL_S12_CHECKPOINT_PENDING
INSERT INTO batch.BatchCheckpoint
(
    RunId,
    StepCode,
    CheckpointStatus,
    CompletedAtUtc
)
SELECT @p_runId,
       @p_stepCode,
       N'Pending',
       NULL
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchCheckpoint
            WHERE RunId = @p_runId
              AND StepCode = @p_stepCode
       );
```

```sql
-- SQL_S12_CAPTURE_CONTROL_TOTALS
;WITH LedgerTotals AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        COALESCE(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmt,
        COALESCE(SUM(CAST(CLCOMM AS DECIMAL(38,4))), 0) AS CLComm,
        COALESCE(SUM(CAST(CLVT AS DECIMAL(38,4))), 0) AS CLVT,
        COALESCE(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS CLTotal,
        COALESCE(SUM(CAST(PGCOMM AS DECIMAL(38,4))), 0) AS PGComm,
        COALESCE(SUM(CAST(PGVT AS DECIMAL(38,4))), 0) AS PGVT,
        COALESCE(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS PGTotal,
        COALESCE(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS POQIncome,
        CAST(SUM(CASE WHEN InState = 1 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
            AS InCompletedRowCount,
        CAST(SUM(CASE WHEN OutState = 2 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
            AS OutScheduledRowCount,
        CAST(SUM(CASE WHEN OutState = 9 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
            AS OutBlockedRowCount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
)
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId, @p_stepCode, N'LedgerRowCount', LedgerRowCount, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'TxAmt', TxAmt, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'CLComm', CLComm, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'CLVT', CLVT, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'CLTotal', CLTotal, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'PGComm', PGComm, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'PGVT', PGVT, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'PGTotal', PGTotal, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'POQIncome', POQIncome, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'InCompletedRowCount', InCompletedRowCount, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'OutScheduledRowCount', OutScheduledRowCount, SYSUTCDATETIME()
  FROM LedgerTotals
UNION ALL
SELECT @p_runId, @p_stepCode, N'OutBlockedRowCount', OutBlockedRowCount, SYSUTCDATETIME()
  FROM LedgerTotals;
```

```sql
-- SQL_S12_READ_FRESH_TOTALS
SELECT N'LedgerRowCount' AS ControlName,
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS ControlValue
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'TxAmt',
       COALESCE(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'CLComm',
       COALESCE(SUM(CAST(CLCOMM AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'CLVT',
       COALESCE(SUM(CAST(CLVT AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'CLTotal',
       COALESCE(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'PGComm',
       COALESCE(SUM(CAST(PGCOMM AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'PGVT',
       COALESCE(SUM(CAST(PGVT AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'PGTotal',
       COALESCE(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'POQIncome',
       COALESCE(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'InCompletedRowCount',
       CAST(SUM(CASE WHEN InState = 1 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'OutScheduledRowCount',
       CAST(SUM(CASE WHEN OutState = 2 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'OutBlockedRowCount',
       CAST(SUM(CASE WHEN OutState = 9 THEN 1 ELSE 0 END) AS DECIMAL(38,4))
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_S12_READ_CAPTURED_TOTALS
SELECT ControlName,
       ControlValue
  FROM batch.BatchControlTotal
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode;
```

```sql
-- SQL_S12_CHECKPOINT_SUCCESS
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode;
```

```sql
-- SQL_S12_JOURNAL_SUCCESS
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StartedAtUtc = @p_startedAtUtc;
```

실패 저널에는 `failedStatementName`과 원본 SQL 오류 상세를 함께 저장한다.

```sql
-- SQL_S12_JOURNAL_FAILURE
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StartedAtUtc = @p_startedAtUtc;
```

#### 실패 및 재시작 처리

- 본 트랜잭션이 실패하면 `batch.BatchControlTotal` 적재와 `batch.BatchCheckpoint`·`batch.BatchStepJournal` 성공 전환이 모두 롤백된다.
- 시작 시 생성된 체크포인트는 `Pending`으로 남고 해당 시도의 저널은 `Failed`, `LegacyReturnCode = -9120`으로 전환된다.
- 재호출 시 통제 합계를 처음부터 다시 계산하며, 성공 체크포인트가 없으므로 전체 동결 절차를 수행한다.
- 단일 트랜잭션 롤백으로 대상 상태가 복구되므로 shadow 테이블이나 보상 DELETE를 사용하지 않는다.
