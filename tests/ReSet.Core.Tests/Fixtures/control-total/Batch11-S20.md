### S20 | 통합 정합성 검증

#### 목적 및 실행 계약

S19 완료 후 S21 게시 전에 실행하며 다른 단계와 병렬 실행하지 않는다. S13에서 동결한 원장 통제 합계와 현재 원장, 핵심 요약, 수기·추가 정산 요약, PG 수납 통계 및 미정산 후처리 결과를 상호 대조한다.

- 논리 컴포넌트 `S20ReconciliationStep`은 검증 트랜잭션, 결과 판정 및 실패 저널 기록을 조정한다.
- `ControlTotalReader`는 `batch.ControlTotal`의 S13 동결값과 S20 재측정값을 제공한다.
- `ReconciliationWriter`는 검증별 기대값·실제값·차이를 `batch.ReconciliationResult`에 기록한다.
- 실행 인터페이스는 `Execute(runId: long, batchYmd: string)`이며 `batchYmd`는 SQL 바인딩 `@p_ymd CHAR(8)`로 전달한다. 레거시 프로시저가 없는 제어 단계이므로 출력 파라미터는 없다.
- 모든 조회와 DML은 SNAPSHOT 격리 의무를 만족하는 하나의 검증 트랜잭션에서 수행한다.
- 비청크 단일 트랜잭션이므로 섀도 테이블과 보상 DELETE를 사용하지 않는다.
- `batch.ControlTotal`은 `(RunId, StepCode, ControlName)`을, `batch.ReconciliationResult`는 `(RunId, StepCode, CheckCode)`를 실행별 유일 키로 관리한다.
- S13은 `LEDGER_ROW_COUNT`, `LEDGER_TX_AMT`, `LEDGER_CL_TOTAL`, `LEDGER_PG_TOTAL`, `LEDGER_POQ_INCOME`을 `batch.ControlTotal`에 기록해야 한다.
- 제어 단계 실패 코드는 **-9200**이다. 상태 변수는 `INT 0`으로 시작하고 각 데이터베이스 작업 직전에 -9200을 대입한다. 구체적인 실패 문장은 별도의 `statementName`으로 식별한다.
- SQL 실행 예외는 전체 검증 트랜잭션을 롤백하여 두 대상 테이블을 원상 복구한다. 정합성 불일치는 SQL 오류가 아니므로 완성된 진단 결과를 커밋한 뒤 단계 저널을 `Failed`로 기록하고 체크포인트는 `Pending`으로 유지한다.

#### C# 배치 제어 의사코드

```pseudocode
void ExecuteS20(long runId, string batchYmd)
{
    const string stepCode = "S20";

    int currentStepErrorCode = 0;
    string statementName = null;
    bool validationCommitted = false;

    conn = connectionFactory.open();
    tx = conn.beginTransaction(); // SNAPSHOT 격리 의무

    try
    {
        // 성공 커밋 후 체크포인트 갱신에 실패한 재호출도 동일 결과를 만들도록
        // 이번 실행의 S20 산출물만 제거한다. S13 동결값은 삭제하지 않는다.
        statementName = "SQL_DELETE_PREVIOUS_RESULTS";
        currentStepErrorCode = -9200;
        conn.execute(SQL_DELETE_PREVIOUS_RESULTS, {
            p_runId: runId,
            p_stepCode: stepCode
        });

        statementName = "SQL_DELETE_PREVIOUS_TOTALS";
        currentStepErrorCode = -9200;
        conn.execute(SQL_DELETE_PREVIOUS_TOTALS, {
            p_runId: runId,
            p_stepCode: stepCode
        });

        statementName = "SQL_CAPTURE_CURRENT_TOTALS";
        currentStepErrorCode = -9200;
        conn.execute(SQL_CAPTURE_CURRENT_TOTALS, {
            p_runId: runId,
            p_ymd: bindAsChar8(batchYmd)
        });

        statementName = "SQL_COMPARE_FROZEN_LEDGER";
        currentStepErrorCode = -9200;
        conn.execute(SQL_COMPARE_FROZEN_LEDGER, {
            p_runId: runId
        });

        statementName = "SQL_COMPARE_SUMMARIES";
        currentStepErrorCode = -9200;
        conn.execute(SQL_COMPARE_SUMMARIES, {
            p_runId: runId,
            p_ymd: bindAsChar8(batchYmd)
        });

        statementName = "SQL_COMPARE_AUXILIARY_RESULTS";
        currentStepErrorCode = -9200;
        conn.execute(SQL_COMPARE_AUXILIARY_RESULTS, {
            p_runId: runId,
            p_ymd: bindAsChar8(batchYmd)
        });

        statementName = "SQL_COUNT_MISMATCHES";
        currentStepErrorCode = -9200;
        mismatchCount = conn.queryScalar(SQL_COUNT_MISMATCHES, {
            p_runId: runId
        });

        if (mismatchCount > 0)
        {
            statementName = "SQL_READ_MISMATCHES";
            currentStepErrorCode = -9200;
            mismatchContext = conn.query(SQL_READ_MISMATCHES, {
                p_runId: runId
            });

            // 모든 검증 결과가 완전하게 생성되었으므로 진단 증적을 원자적으로 보존한다.
            tx.commit();
            validationCommitted = true;

            writeStepJournal(
                runId,
                stepCode,
                status: "Failed",
                LegacyReturnCode: -9200,
                errorMessage: formatMismatchContext(mismatchContext)
            );

            // 실패 체크포인트 상태는 정의되어 있지 않으므로 Pending을 유지한다.
            stopPipeline();
            return;
        }

        tx.commit();
        validationCommitted = true;

        writeStepSuccess(
            runId,
            stepCode,
            LegacyReturnCode: null,
            checkpointStatus: "Succeeded"
        );
    }
    catch (error)
    {
        if (!validationCommitted)
            tx.rollbackIfOpen();

        writeStepJournal(
            runId,
            stepCode,
            status: "Failed",
            LegacyReturnCode: currentStepErrorCode,
            errorMessage: errorContext.messageWithStatement(statementName, error)
        );

        stopPipeline();
    }
}
```

#### 검증 결과 재작성 및 현재 원장 통제 합계

```sql
-- SQL_DELETE_PREVIOUS_RESULTS
DELETE FROM batch.ReconciliationResult
 WHERE RunId = @p_runId
   AND StepCode = N'S20';

-- SQL_DELETE_PREVIOUS_TOTALS
DELETE FROM batch.ControlTotal
 WHERE RunId = @p_runId
   AND StepCode = N'S20';

-- SQL_CAPTURE_CURRENT_TOTALS
INSERT INTO batch.ControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId,
       N'S20',
       N'LEDGER_ROW_COUNT',
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd

UNION ALL

SELECT @p_runId,
       N'S20',
       N'LEDGER_TX_AMT',
       CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd

UNION ALL

SELECT @p_runId,
       N'S20',
       N'LEDGER_CL_TOTAL',
       CAST(ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd

UNION ALL

SELECT @p_runId,
       N'S20',
       N'LEDGER_PG_TOTAL',
       CAST(ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd

UNION ALL

SELECT @p_runId,
       N'S20',
       N'LEDGER_POQ_INCOME',
       CAST(ISNULL(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;
```

모든 원천 조회에서 레거시 `NOLOCK` 힌트를 제거한다. SNAPSHOT 읽기 시점이 트랜잭션 전체에서 유지되므로 S13 동결값, 현재 원장 및 요약 결과가 서로 다른 커밋 시점을 읽지 않아야 한다.

#### S13 동결 원장과 현재 원장 비교

두 집계 결과를 `CROSS JOIN`하지 않고 동일 통제명별 조건부 집계로 각각 스칼라화한다. S13 또는 S20 값이 누락된 경우에도 통과시키지 않는다.

```sql
-- SQL_COMPARE_FROZEN_LEDGER
WITH FrozenAndCurrent AS
(
    SELECT ControlName,
           MAX(CASE WHEN StepCode = N'S13' THEN ControlValue END) AS ExpectedValue,
           MAX(CASE WHEN StepCode = N'S20' THEN ControlValue END) AS ActualValue
      FROM batch.ControlTotal
     WHERE RunId = @p_runId
       AND StepCode IN (N'S13', N'S20')
       AND ControlName IN
           (
               N'LEDGER_ROW_COUNT',
               N'LEDGER_TX_AMT',
               N'LEDGER_CL_TOTAL',
               N'LEDGER_PG_TOTAL',
               N'LEDGER_POQ_INCOME'
           )
     GROUP BY ControlName
)
INSERT INTO batch.ReconciliationResult
(
    RunId,
    StepCode,
    CheckCode,
    ExpectedValue,
    ActualValue,
    DifferenceValue,
    IsMatched,
    CheckedAtUtc
)
SELECT @p_runId,
       N'S20',
       N'FREEZE_' + ControlName,
       ExpectedValue,
       ActualValue,
       CASE
           WHEN ExpectedValue IS NULL OR ActualValue IS NULL THEN NULL
           ELSE ActualValue - ExpectedValue
       END,
       CAST
       (
           CASE
               WHEN ExpectedValue IS NOT NULL
                AND ActualValue IS NOT NULL
                AND ExpectedValue = ActualValue
               THEN 1 ELSE 0
           END
           AS BIT
       ),
       SYSUTCDATETIME()
  FROM FrozenAndCurrent;
```

#### 원장과 핵심 요약 테이블 비교

`SETTLE_POQ_DB.dbo.TSettleMst`와 각 요약 테이블의 집계를 독립 CTE에서 계산한 후 스칼라 하위질의로 비교한다. 다음 대상별 원본 비즈니스 필터를 그대로 유지한다.

- `SETTLE_POQ_DB.dbo.TSettleByTX`: `YMD = @p_ymd`
- `SETTLE_POQ_DB.dbo.TPartialCancelByTX`: 원장 `USESTATE = 2`
- `SETTLE_POQ_DB.dbo.TSettleByIN`: 원장 `INSTATE = 1`
- `SETTLE_POQ_DB.dbo.TSettleByOUT`: 원장과 요약 모두 `OUTSTATE IN (2,9)`

```sql
-- SQL_COMPARE_SUMMARIES
WITH
LedgerAll AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
),
SummaryTx AS
(
    SELECT CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_ymd
),
LedgerPartial AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
       AND USESTATE = 2
),
SummaryPartial AS
(
    SELECT CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @p_ymd
       AND USESTATE = 2
),
LedgerIn AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
       AND INSTATE = 1
),
SummaryIn AS
(
    SELECT CAST(ISNULL(SUM(CAST(INCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @p_ymd
),
LedgerOut AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
       AND OUTSTATE IN (2,9)
),
SummaryOut AS
(
    SELECT CAST(ISNULL(SUM(CAST(OUTCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @p_ymd
       AND OUTSTATE IN (2,9)
),
Checks AS
(
    SELECT N'TX_ROW_COUNT' AS CheckCode,
           (SELECT RowCount FROM LedgerAll) AS ExpectedValue,
           (SELECT RowCount FROM SummaryTx) AS ActualValue
    UNION ALL
    SELECT N'TX_AMOUNT',
           (SELECT TxAmt FROM LedgerAll),
           (SELECT TxAmt FROM SummaryTx)
    UNION ALL
    SELECT N'PARTIAL_ROW_COUNT',
           (SELECT RowCount FROM LedgerPartial),
           (SELECT RowCount FROM SummaryPartial)
    UNION ALL
    SELECT N'PARTIAL_AMOUNT',
           (SELECT TxAmt FROM LedgerPartial),
           (SELECT TxAmt FROM SummaryPartial)
    UNION ALL
    SELECT N'IN_ROW_COUNT',
           (SELECT RowCount FROM LedgerIn),
           (SELECT RowCount FROM SummaryIn)
    UNION ALL
    SELECT N'IN_AMOUNT',
           (SELECT TxAmt FROM LedgerIn),
           (SELECT TxAmt FROM SummaryIn)
    UNION ALL
    SELECT N'OUT_ROW_COUNT',
           (SELECT RowCount FROM LedgerOut),
           (SELECT RowCount FROM SummaryOut)
    UNION ALL
    SELECT N'OUT_AMOUNT',
           (SELECT TxAmt FROM LedgerOut),
           (SELECT TxAmt FROM SummaryOut)
)
INSERT INTO batch.ReconciliationResult
(
    RunId,
    StepCode,
    CheckCode,
    ExpectedValue,
    ActualValue,
    DifferenceValue,
    IsMatched,
    CheckedAtUtc
)
SELECT @p_runId,
       N'S20',
       CheckCode,
       ExpectedValue,
       ActualValue,
       ActualValue - ExpectedValue,
       CAST(CASE WHEN ActualValue = ExpectedValue THEN 1 ELSE 0 END AS BIT),
       SYSUTCDATETIME()
  FROM Checks;
```

#### PG 수납 통계 및 후취정산 비교

PG 수납 통계는 `SETTLE_POQ_DB.dbo.TStatPGCollect`의 차변계와 대변계 차이가 0인지 확인한다. 후취정산은 S19와 동일한 `TSettleMst`·`TClientSettleRate`·`TClient` 조인 및 `TaxFGBill` 조건을 보존하여 `SETTLE_POQ_DB.dbo.TSettleMiss` 반영액과 비교한다.

```sql
-- SQL_COMPARE_AUXILIARY_RESULTS
WITH
PGCollectBalance AS
(
    SELECT CAST
           (
               ISNULL
               (
                   SUM
                   (
                       CAST(LEFTSUMAMT AS DECIMAL(38,4))
                       - CAST(RIGHTSUMAMT AS DECIMAL(38,4))
                   ),
                   0
               )
               AS DECIMAL(38,4)
           ) AS DifferenceValue
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE INYMD = @p_ymd
),
PostSettleSource AS
(
    SELECT CAST
           (
               ISNULL(SUM(CAST(A.CLTotal AS DECIMAL(38,4))), 0)
               AS DECIMAL(38,4)
           ) AS ExpectedValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON A.YMD = B.YMD
       AND A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
      JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON A.ClientID = C.ClientID
     WHERE A.YMD = @p_ymd
       AND A.OutState = 2
       AND ISNULL(B.TaxFGBill, 2) = 1
),
PostSettleApplied AS
(
    SELECT CAST
           (
               ISNULL(SUM(CAST(CLSettleAmt AS DECIMAL(38,4))), 0)
               AS DECIMAL(38,4)
           ) AS ActualValue
      FROM SETTLE_POQ_DB.dbo.TSettleMiss
     WHERE YMD = @p_ymd
       AND OutState = 2
       AND ISNULL(IssueType, 0) = 15
),
Checks AS
(
    SELECT N'PG_COLLECT_BALANCE' AS CheckCode,
           CAST(0 AS DECIMAL(38,4)) AS ExpectedValue,
           (SELECT DifferenceValue FROM PGCollectBalance) AS ActualValue
    UNION ALL
    SELECT N'POST_SETTLE_AMOUNT',
           (SELECT ExpectedValue FROM PostSettleSource),
           (SELECT ActualValue FROM PostSettleApplied)
)
INSERT INTO batch.ReconciliationResult
(
    RunId,
    StepCode,
    CheckCode,
    ExpectedValue,
    ActualValue,
    DifferenceValue,
    IsMatched,
    CheckedAtUtc
)
SELECT @p_runId,
       N'S20',
       CheckCode,
       ExpectedValue,
       ActualValue,
       ActualValue - ExpectedValue,
       CAST(CASE WHEN ActualValue = ExpectedValue THEN 1 ELSE 0 END AS BIT),
       SYSUTCDATETIME()
  FROM Checks;

-- SQL_COUNT_MISMATCHES
SELECT COUNT_BIG(*)
  FROM batch.ReconciliationResult
 WHERE RunId = @p_runId
   AND StepCode = N'S20'
   AND IsMatched = 0;

-- SQL_READ_MISMATCHES
SELECT CheckCode,
       ExpectedValue,
       ActualValue,
       DifferenceValue
  FROM batch.ReconciliationResult
 WHERE RunId = @p_runId
   AND StepCode = N'S20'
   AND IsMatched = 0
 ORDER BY CheckCode;
```

#### 재시작 및 판정 기준

- 재호출되면 `batch.ReconciliationResult`와 `batch.ControlTotal`에서 해당 `RunId`, `StepCode = N'S20'` 범위만 삭제한 뒤 전체 검증을 다시 수행한다.
- S13의 `batch.ControlTotal` 행은 삭제하거나 갱신하지 않는다.
- 모든 `IsMatched`가 1일 때만 S20 체크포인트를 `Succeeded`로 확정한다.
- 한 건이라도 불일치하면 완성된 검증 증적을 커밋하고 `batch.BatchStepJournal.LegacyReturnCode`에 **-9200**을 기록한 뒤 파이프라인을 중단한다.
- SQL 실행 실패 시에는 열린 검증 트랜잭션을 롤백하고, `statementName`과 **-9200**을 함께 기록하여 실패 문장을 정확히 식별한다.