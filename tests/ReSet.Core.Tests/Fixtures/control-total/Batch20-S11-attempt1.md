### S11 — 정산원장 동결 및 기준합계 기록

#### 목적 및 인터페이스

S03~S10이 수정한 `SETTLE_POQ_DB.dbo.TSettleMst`의 실행 범위를 논리적으로 동결하고, 이후 S18에서 변경 여부를 검증할 기준합계를 기록한다. S11 성공 이후 오케스트레이터는 `SETTLE_POQ_DB.dbo.TSettleMst`를 수정하는 단계를 다시 호출하지 않아야 한다.

- `runId BIGINT -> p_runId`
- `businessYmd VARCHAR(8) -> p_businessYmd`
- 레거시 출력 파라미터는 없다.
- `batch.BatchStepJournal`: S11 시작, 성공 또는 실패 상태와 `LegacyReturnCode`를 기록한다.
- `batch.BatchControlTotal`: 동결 시점의 원장 건수와 금액 기준합계를 INSERT한다.
- 원장 범위는 기본 정산행과 추가정산행을 모두 포함하도록 `YMD = @p_businessYmd OR ProcYMD = @p_businessYmd`로 고정한다.
- 단계 전체는 **SNAPSHOT 격리수준의 단일 트랜잭션**으로 실행한다. 청킹과 물리적 Shadow Table은 사용하지 않는다.

#### 선행조건

S11 호출 시점에 다음 조건을 모두 만족해야 한다.

1. `batch.BatchRunLock`에 `POQSettleBatch20`, 업무일자, 현재 RunId 조합의 `Held` 잠금이 존재한다.
2. 원장을 수정하는 S03~S10의 `batch.BatchCheckpoint`가 모두 `Succeeded`이다.
3. 기준합계 기록과 동일 트랜잭션 안에서 재조회한 값이 `batch.BatchControlTotal`의 값과 정확히 일치한다.

```sql
-- SQL_VERIFY_S11_RUN_LOCK
SELECT CASE
           WHEN EXISTS
           (
               SELECT 1
                 FROM batch.BatchRunLock
                WHERE JobName = N'POQSettleBatch20'
                  AND BatchYmd = @p_businessYmd
                  AND OwnerRunId = @p_runId
                  AND LockStatus = N'Held'
           )
           THEN 1
           ELSE 0
       END;

-- SQL_COUNT_INCOMPLETE_LEDGER_STEPS
WITH RequiredStep AS
(
    SELECT StepCode
      FROM
      (
          VALUES
              (N'S03'), (N'S04'), (N'S05'), (N'S06'),
              (N'S07'), (N'S08'), (N'S09'), (N'S10')
      ) AS V(StepCode)
)
SELECT COUNT_BIG(*)
  FROM RequiredStep AS R
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchCheckpoint AS C
            WHERE C.RunId = @p_runId
              AND C.StepCode = R.StepCode
              AND C.CheckpointStatus = N'Succeeded'
       );
```

#### 기준합계 기록 SQL

재시작 시 이미 커밋된 동일 RunId의 기준합계가 있을 수 있으므로 `RunId + StepCode + ControlName`이 없는 항목만 INSERT한다. 이전 시도의 단일 INSERT가 커밋되었다면 기존 행을 갱신하거나 삭제하지 않고 후속 대조로 동일성을 확인한다.

```sql
-- SQL_INSERT_FROZEN_LEDGER_TOTALS
DECLARE @v_capturedAt DATETIME2(3) = SYSUTCDATETIME();

WITH Ledger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        COALESCE(SUM(CAST(ISNULL(M.TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS CLTotalSum,
        COALESCE(SUM(CAST(ISNULL(M.PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS PGTotalSum,
        COALESCE(SUM(CAST(ISNULL(M.POQINCOME, 0) AS DECIMAL(38,4))), 0) AS POQIncomeSum,
        COALESCE(SUM(CAST(ISNULL(M.NonSettleAmt, 0) AS DECIMAL(38,4))), 0) AS NonSettleAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.SeperateAmt, 0) AS DECIMAL(38,4))), 0) AS SeperateAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0) AS ForeignSettleAmtSum
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS M
     WHERE M.YMD = @p_businessYmd
        OR M.ProcYMD = @p_businessYmd
),
ControlValueSet AS
(
    SELECT N'LedgerRowCount' AS ControlName, LedgerRowCount AS ControlValue FROM Ledger
    UNION ALL
    SELECT N'TxAmtSum', TxAmtSum FROM Ledger
    UNION ALL
    SELECT N'CLTotalSum', CLTotalSum FROM Ledger
    UNION ALL
    SELECT N'PGTotalSum', PGTotalSum FROM Ledger
    UNION ALL
    SELECT N'POQIncomeSum', POQIncomeSum FROM Ledger
    UNION ALL
    SELECT N'NonSettleAmtSum', NonSettleAmtSum FROM Ledger
    UNION ALL
    SELECT N'SeperateAmtSum', SeperateAmtSum FROM Ledger
    UNION ALL
    SELECT N'ForeignSettleAmtSum', ForeignSettleAmtSum FROM Ledger
)
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT
    @p_runId,
    N'S11',
    C.ControlName,
    C.ControlValue,
    @v_capturedAt
  FROM ControlValueSet AS C
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchControlTotal AS B
            WHERE B.RunId = @p_runId
              AND B.StepCode = N'S11'
              AND B.ControlName = C.ControlName
       );
```

#### 애플리케이션 제어 흐름

```pseudocode
// S11은 레거시 기원이 없는 제어 단계이다.
currentStepErrorCode = 0
currentStatementName = NULL

recordStepStart(runId, "S11")
// 위 공통 기록으로 batch.BatchStepJournal의 S11 행을 소유한다.

conn = connectionFactory.open()
tx = conn.beginTransaction()  // 이 트랜잭션은 반드시 SNAPSHOT 격리수준이어야 한다.

TRY:
    currentStepErrorCode = -9110
    currentStatementName = "SQL_VERIFY_S11_RUN_LOCK"
    lockHeld = repository.queryScalar(conn, tx, SQL_VERIFY_S11_RUN_LOCK, {
        p_runId: runId,
        p_businessYmd: businessYmd
    })

    IF lockHeld != 1:
        fail the step with the tracked code and statement name

    currentStepErrorCode = -9110
    currentStatementName = "SQL_COUNT_INCOMPLETE_LEDGER_STEPS"
    incompleteCount = repository.queryScalar(
        conn,
        tx,
        SQL_COUNT_INCOMPLETE_LEDGER_STEPS,
        {
            p_runId: runId
        }
    )

    IF incompleteCount != 0:
        fail the step with the tracked code and statement name

    // DML 실패 시점에 이미 정확한 제어 오류 코드가 설정되어 있어야 한다.
    currentStepErrorCode = -9110
    currentStatementName = "SQL_INSERT_FROZEN_LEDGER_TOTALS"
    repository.execute(conn, tx, SQL_INSERT_FROZEN_LEDGER_TOTALS, {
        p_runId: runId,
        p_businessYmd: businessYmd
    })

    currentStepErrorCode = -9110
    currentStatementName = "SQL_FIND_FROZEN_TOTAL_MISMATCH"
    mismatches = repository.queryRows(conn, tx, SQL_FIND_FROZEN_TOTAL_MISMATCH, {
        p_runId: runId,
        p_businessYmd: businessYmd
    })

    IF mismatches is not empty:
        fail the step with the tracked code and statement name

    tx.commit()

    // S11의 Succeeded 저널 행이 정산원장 논리적 동결 표식이다.
    // 제어 단계 정상 코드는 0이며, 실패 코드 -9110과 구분한다.
    recordStepSuccess(runId, "S11", LegacyReturnCode: 0)

ON FAILURE observed by the application:
    tx.rollback()

    recordStepFailure(
        runId,
        "S11",
        LegacyReturnCode: currentStepErrorCode,
        StatementName: currentStatementName,
        Diagnostics: observedSqlDiagnostics
    )

    stop the pipeline
```

#### 기록값 재대조

다음 조회는 저장된 각 기준합계가 정확히 한 행이고, 동일 SNAPSHOT에서 다시 계산한 원장 합계와 일치하는지 확인한다. 독립 집계 결과끼리 카티션 곱을 만들지 않으며 `ControlName`으로만 결합한다.

```sql
-- SQL_FIND_FROZEN_TOTAL_MISMATCH
WITH Ledger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        COALESCE(SUM(CAST(ISNULL(M.TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS CLTotalSum,
        COALESCE(SUM(CAST(ISNULL(M.PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS PGTotalSum,
        COALESCE(SUM(CAST(ISNULL(M.POQINCOME, 0) AS DECIMAL(38,4))), 0) AS POQIncomeSum,
        COALESCE(SUM(CAST(ISNULL(M.NonSettleAmt, 0) AS DECIMAL(38,4))), 0) AS NonSettleAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.SeperateAmt, 0) AS DECIMAL(38,4))), 0) AS SeperateAmtSum,
        COALESCE(SUM(CAST(ISNULL(M.ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0) AS ForeignSettleAmtSum
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS M
     WHERE M.YMD = @p_businessYmd
        OR M.ProcYMD = @p_businessYmd
),
LiveControl AS
(
    SELECT N'LedgerRowCount' AS ControlName, LedgerRowCount AS ControlValue FROM Ledger
    UNION ALL SELECT N'TxAmtSum', TxAmtSum FROM Ledger
    UNION ALL SELECT N'CLTotalSum', CLTotalSum FROM Ledger
    UNION ALL SELECT N'PGTotalSum', PGTotalSum FROM Ledger
    UNION ALL SELECT N'POQIncomeSum', POQIncomeSum FROM Ledger
    UNION ALL SELECT N'NonSettleAmtSum', NonSettleAmtSum FROM Ledger
    UNION ALL SELECT N'SeperateAmtSum', SeperateAmtSum FROM Ledger
    UNION ALL SELECT N'ForeignSettleAmtSum', ForeignSettleAmtSum FROM Ledger
),
StoredControl AS
(
    SELECT
        ControlName,
        COUNT_BIG(*) AS StoredRowCount,
        MIN(ControlValue) AS MinStoredValue,
        MAX(ControlValue) AS MaxStoredValue
      FROM batch.BatchControlTotal
     WHERE RunId = @p_runId
       AND StepCode = N'S11'
     GROUP BY ControlName
)
SELECT
    COALESCE(L.ControlName, S.ControlName) AS ControlName,
    L.ControlValue AS ExpectedValue,
    S.MinStoredValue AS ActualValue,
    S.StoredRowCount
  FROM LiveControl AS L
  FULL OUTER JOIN StoredControl AS S
    ON S.ControlName = L.ControlName
 WHERE L.ControlName IS NULL
    OR S.ControlName IS NULL
    OR S.StoredRowCount <> 1
    OR S.MinStoredValue <> L.ControlValue
    OR S.MaxStoredValue <> L.ControlValue;
```

#### 오류 및 재시작 정책

| 코드 | 적용 범위 | 처리 |
|---:|---|---|
| `-9110` | 실행 잠금 상실, S03~S10 미완료, `batch.BatchControlTotal` 기록 실패, 기록값 재대조 실패 또는 S11 제어 실패 | 열린 트랜잭션을 롤백하고 `batch.BatchStepJournal.LegacyReturnCode`에 기록한 뒤 파이프라인 중단 |
| `0` | 기준합계 기록과 재대조가 모두 성공한 경우 | `batch.BatchStepJournal`의 S11 행을 `Succeeded`로 확정 |

트랜잭션 커밋 후 성공 저널 기록 전에 프로세스가 중단된 경우에도 재호출은 기존 `batch.BatchControlTotal` 행을 변경하지 않는다. 누락된 항목만 INSERT하고 전체 기준합계를 다시 대조하므로 중복 기준합계나 부분 기준합계를 성공으로 승인하지 않는다.