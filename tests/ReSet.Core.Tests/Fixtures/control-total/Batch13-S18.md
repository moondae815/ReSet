### S18 — 통합 정합성 검증

**목적과 실행 계약**

- S17까지 확정된 정산 원장·요율·통계·요약 결과를 동일한 SNAPSHOT 격리 시점에서 비교한다.
- 호출 컨텍스트는 `RunId BIGINT -> p_runId`, `BatchYmd VARCHAR(8) -> p_ymd`이며 재시작·우회 입력은 두지 않는다.
- 검증 결과는 `batch.BatchControlTotal`, `batch.BatchReconciliation`에 기록하고, 단계 상태와 실패 원인은 `batch.BatchStepJournal`에 기록한다.
- 단계 전체를 단일 트랜잭션으로 실행한다. 불일치 또는 SQL 오류가 발생하면 모든 검증 기록을 롤백하고, 불일치 지표명과 기대값·실제값을 `batch.BatchStepJournal.ErrorMessage`에 남긴다.
- 청크 페이징은 사용하지 않는다.

`batch.BatchReconciliation`은 배치 부트스트랩에서 다음 계약으로 미리 생성한다. 이 DDL은 S18 런타임 트랜잭션에서 실행하지 않는다.

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

**검증 범위**

| 검증명 | 기대값 원천 | 실제값 원천 |
|---|---|---|
| `SummaryTx.*` | `SETTLE_POQ_DB.dbo.TSettleMst`의 기준일 원장 | `SETTLE_POQ_DB.dbo.TSettleByTX` |
| `Partial.*` | 기준일 `USESTATE = 2` 원장 | `SETTLE_POQ_DB.dbo.TPartialCancelByTX` |
| `CollectIn.*` | 기준일 `INSTATE = 1` 원장 | `SETTLE_POQ_DB.dbo.TSettleByIN` |
| `SettleOut.*` | 기준일 `OUTSTATE IN (2,9)` 원장 | `SETTLE_POQ_DB.dbo.TSettleByOUT` |
| `RateSnapshot.*` | 누락 허용값 0 | `TPGSettleRate`, `TClientSettleRate` 미매칭 원장 수 |
| `PGCollect.Balance` | `TStatPGCollect.LEFTSUMAMT` | `TStatPGCollect.RIGHTSUMAMT` |
| `SettleMiss.CLSettleAmt` | 후취정산 대상 원장의 `CLTotal` | `SETTLE_POQ_DB.dbo.TSettleMiss.CLSettleAmt` |

검증값은 카티전 결합 없이 각 원천을 독립적으로 집계한다.

```sql
-- SQL_CAPTURE_CONTROL_TOTALS
WITH
LedgerAgg AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue,
        COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS CLTotalValue,
        COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS PGTotalValue,
        COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0) AS POQIncomeValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
),
TxSummaryAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(TXCNT, 0) AS DECIMAL(38,4))), 0) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue,
        COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS CLTotalValue,
        COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS PGTotalValue,
        COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0) AS POQIncomeValue
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_ymd
),
PartialSourceAgg AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND USESTATE = 2
),
PartialTargetAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(TXCNT, 0) AS DECIMAL(38,4))), 0) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_ymd
      AND USESTATE = 2
),
InSourceAgg AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND INSTATE = 1
),
InTargetAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(INCNT, 0) AS DECIMAL(38,4))), 0) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_ymd
),
OutSourceAgg AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue,
        COALESCE(SUM(CAST(ISNULL(ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0)
            AS ForeignSettleAmtValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2,9)
),
OutTargetAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(OUTCNT, 0) AS DECIMAL(38,4))), 0) AS RowCountValue,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmtValue,
        COALESCE(SUM(CAST(ISNULL(ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0)
            AS ForeignSettleAmtValue
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2,9)
),
RateOrphanAgg AS
(
    SELECT
        CAST(SUM
        (
            CASE WHEN NOT EXISTS
            (
                SELECT 1
                FROM SETTLE_POQ_DB.dbo.TPGSettleRate AS R
                WHERE R.YMD = M.YMD
                  AND R.PGNAME = M.PGNAME
                  AND R.MALLID = M.MALLID
            ) THEN 1 ELSE 0 END
        ) AS DECIMAL(38,4)) AS MissingPgRateCount,
        CAST(SUM
        (
            CASE WHEN NOT EXISTS
            (
                SELECT 1
                FROM SETTLE_POQ_DB.dbo.TClientSettleRate AS R
                WHERE R.YMD = M.YMD
                  AND R.CLIENTID = M.CLIENTID
                  AND R.PGNAME = M.PGNAME
                  AND R.MALLID = M.MALLID
            ) THEN 1 ELSE 0 END
        ) AS DECIMAL(38,4)) AS MissingClientRateCount
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS M
    WHERE M.YMD = @p_ymd
      AND ISNULL(M.ExtraSettleFlag, 0) = 0
),
PGCollectAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(LEFTSUMAMT, 0) AS DECIMAL(38,4))), 0) AS LeftValue,
        COALESCE(SUM(CAST(ISNULL(RIGHTSUMAMT, 0) AS DECIMAL(38,4))), 0) AS RightValue
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_ymd
),
ImpactedPostSettle AS
(
    SELECT DISTINCT A.ClientID, A.OutYMD
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON A.YMD = B.YMD
       AND A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON A.ClientID = C.ClientID
    WHERE A.YMD = @p_ymd
      AND A.OutState = 2
      AND ISNULL(B.TaxFGBill, 2) = 1
),
PostSettleSourceAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(A.CLTotal, 0) AS DECIMAL(38,4))), 0) AS AmountValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON A.YMD = B.YMD
       AND A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON A.ClientID = C.ClientID
    INNER JOIN ImpactedPostSettle AS I
        ON A.ClientID = I.ClientID
       AND A.OutYMD = I.OutYMD
    WHERE A.OutState = 2
      AND ISNULL(B.TaxFGBill, 2) = 1
),
PostSettleTargetAgg AS
(
    SELECT
        COALESCE(SUM(CAST(ISNULL(M.CLSettleAmt, 0) AS DECIMAL(38,4))), 0) AS AmountValue
    FROM SETTLE_POQ_DB.dbo.TSettleMiss AS M
    INNER JOIN ImpactedPostSettle AS I
        ON M.ClientID = I.ClientID
       AND M.OutYMD = I.OutYMD
    WHERE M.OutState = 2
      AND ISNULL(M.IssueType, 0) = 15
),
Metrics AS
(
    SELECT N'SummaryTx.RowCount' AS MetricName,
           (SELECT RowCountValue FROM LedgerAgg) AS ExpectedValue,
           (SELECT RowCountValue FROM TxSummaryAgg) AS ActualValue
    UNION ALL
    SELECT N'SummaryTx.TxAmt',
           (SELECT TxAmtValue FROM LedgerAgg),
           (SELECT TxAmtValue FROM TxSummaryAgg)
    UNION ALL
    SELECT N'SummaryTx.CLTotal',
           (SELECT CLTotalValue FROM LedgerAgg),
           (SELECT CLTotalValue FROM TxSummaryAgg)
    UNION ALL
    SELECT N'SummaryTx.PGTotal',
           (SELECT PGTotalValue FROM LedgerAgg),
           (SELECT PGTotalValue FROM TxSummaryAgg)
    UNION ALL
    SELECT N'SummaryTx.POQIncome',
           (SELECT POQIncomeValue FROM LedgerAgg),
           (SELECT POQIncomeValue FROM TxSummaryAgg)
    UNION ALL
    SELECT N'Partial.RowCount',
           (SELECT RowCountValue FROM PartialSourceAgg),
           (SELECT RowCountValue FROM PartialTargetAgg)
    UNION ALL
    SELECT N'Partial.TxAmt',
           (SELECT TxAmtValue FROM PartialSourceAgg),
           (SELECT TxAmtValue FROM PartialTargetAgg)
    UNION ALL
    SELECT N'CollectIn.RowCount',
           (SELECT RowCountValue FROM InSourceAgg),
           (SELECT RowCountValue FROM InTargetAgg)
    UNION ALL
    SELECT N'CollectIn.TxAmt',
           (SELECT TxAmtValue FROM InSourceAgg),
           (SELECT TxAmtValue FROM InTargetAgg)
    UNION ALL
    SELECT N'SettleOut.RowCount',
           (SELECT RowCountValue FROM OutSourceAgg),
           (SELECT RowCountValue FROM OutTargetAgg)
    UNION ALL
    SELECT N'SettleOut.TxAmt',
           (SELECT TxAmtValue FROM OutSourceAgg),
           (SELECT TxAmtValue FROM OutTargetAgg)
    UNION ALL
    SELECT N'SettleOut.ForeignSettleAmt',
           (SELECT ForeignSettleAmtValue FROM OutSourceAgg),
           (SELECT ForeignSettleAmtValue FROM OutTargetAgg)
    UNION ALL
    SELECT N'RateSnapshot.MissingPG',
           CAST(0 AS DECIMAL(38,4)),
           COALESCE((SELECT MissingPgRateCount FROM RateOrphanAgg), 0)
    UNION ALL
    SELECT N'RateSnapshot.MissingClient',
           CAST(0 AS DECIMAL(38,4)),
           COALESCE((SELECT MissingClientRateCount FROM RateOrphanAgg), 0)
    UNION ALL
    SELECT N'PGCollect.Balance',
           (SELECT LeftValue FROM PGCollectAgg),
           (SELECT RightValue FROM PGCollectAgg)
    UNION ALL
    SELECT N'SettleMiss.CLSettleAmt',
           (SELECT AmountValue FROM PostSettleSourceAgg),
           (SELECT AmountValue FROM PostSettleTargetAgg)
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
    N'S18',
    MetricName + N'.Expected',
    ExpectedValue,
    @p_captureAtUtc
FROM Metrics
UNION ALL
SELECT
    @p_runId,
    N'S18',
    MetricName + N'.Actual',
    ActualValue,
    @p_captureAtUtc
FROM Metrics;
```

동일 캡처 시각의 기대값과 실제값만 비교하여 재시작 시 이전 캡처와 혼합되지 않도록 한다.

```sql
-- SQL_WRITE_RECONCILIATION
WITH PairNames AS
(
    SELECT DISTINCT
        LEFT(ControlName, LEN(ControlName) - LEN(N'.Expected')) AS ReconciliationName
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId
      AND StepCode = N'S18'
      AND CapturedAtUtc = @p_captureAtUtc
      AND ControlName LIKE N'%.Expected'
),
Evaluated AS
(
    SELECT
        P.ReconciliationName,
        COALESCE
        (
            (
                SELECT MAX(C.ControlValue)
                FROM batch.BatchControlTotal AS C
                WHERE C.RunId = @p_runId
                  AND C.StepCode = N'S18'
                  AND C.CapturedAtUtc = @p_captureAtUtc
                  AND C.ControlName = P.ReconciliationName + N'.Expected'
            ),
            0
        ) AS ExpectedValue,
        COALESCE
        (
            (
                SELECT MAX(C.ControlValue)
                FROM batch.BatchControlTotal AS C
                WHERE C.RunId = @p_runId
                  AND C.StepCode = N'S18'
                  AND C.CapturedAtUtc = @p_captureAtUtc
                  AND C.ControlName = P.ReconciliationName + N'.Actual'
            ),
            0
        ) AS ActualValue
    FROM PairNames AS P
)
INSERT INTO batch.BatchReconciliation
(
    RunId,
    StepCode,
    ReconciliationName,
    ExpectedValue,
    ActualValue,
    DifferenceValue,
    IsMatched,
    CheckedAtUtc
)
SELECT
    @p_runId,
    N'S18',
    ReconciliationName,
    ExpectedValue,
    ActualValue,
    ActualValue - ExpectedValue,
    CASE WHEN ActualValue = ExpectedValue
         THEN CONVERT(BIT, 1)
         ELSE CONVERT(BIT, 0)
    END,
    @p_captureAtUtc
FROM Evaluated;
```

애플리케이션은 같은 트랜잭션 안에서 불일치 행을 읽고 성공 여부를 결정한다. 이 조회 결과에 따른 분기는 SQL 문장이 아니라 애플리케이션이 수행한다.

```sql
-- SQL_READ_RECONCILIATION_FAILURES
SELECT
    ReconciliationName,
    ExpectedValue,
    ActualValue,
    DifferenceValue
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND StepCode = N'S18'
  AND CheckedAtUtc = @p_captureAtUtc
  AND IsMatched = 0
ORDER BY ReconciliationName;
```

```pseudocode
// 비레거시 단계이므로 원본 프로시저 출력 파라미터는 없다.
runId = batchContext.runId
batchYmd = batchContext.batchYmd
captureAtUtc = readCurrentDatabaseUtcWithMillisecondPrecision()

currentStepErrorCode = 0
currentStatementName = null

conn = connectionFactory.open()
ensure S18 runs under SNAPSHOT isolation

write S18 Running row to batch.BatchStepJournal
write S18 Pending checkpoint

tx = conn.beginTransaction()

TRY:
    currentStepErrorCode = -9180
    currentStatementName = "SQL_CAPTURE_CONTROL_TOTALS"
    conn.execute(
        SQL_CAPTURE_CONTROL_TOTALS,
        {
            p_runId: runId,
            p_ymd: batchYmd,
            p_captureAtUtc: captureAtUtc
        },
        tx
    )

    currentStepErrorCode = -9180
    currentStatementName = "SQL_WRITE_RECONCILIATION"
    conn.execute(
        SQL_WRITE_RECONCILIATION,
        {
            p_runId: runId,
            p_captureAtUtc: captureAtUtc
        },
        tx
    )

    currentStepErrorCode = -9180
    currentStatementName = "SQL_READ_RECONCILIATION_FAILURES"
    mismatches = conn.queryRows(
        SQL_READ_RECONCILIATION_FAILURES,
        {
            p_runId: runId,
            p_captureAtUtc: captureAtUtc
        },
        tx
    )

    IF mismatches is not empty:
        mismatchMessage = format every mismatch as:
            reconciliation name, expected value, actual value, difference
        raise validation failure with mismatchMessage

    tx.commit()

    update S18 row in batch.BatchStepJournal to Succeeded
        with LegacyReturnCode = NULL
    update S18 checkpoint to Succeeded

CATCH error:
    tx.rollbackIfOpen()

    update S18 row in batch.BatchStepJournal to Failed with:
        LegacyReturnCode =
            NULL when currentStepErrorCode = 0
            otherwise currentStepErrorCode
        ErrorMessage =
            currentStatementName
            plus exact database or reconciliation failure text

    leave S18 checkpoint Pending
    stop S19
```

**오류 코드**

| 코드 | 적용 범위 | 처리 |
|---:|---|---|
| `-9180` | `batch.BatchControlTotal` 기록 실패, `batch.BatchReconciliation` 기록 실패, 검증 조회 실패 또는 하나 이상의 정합성 불일치 | 트랜잭션 롤백 후 `batch.BatchStepJournal.LegacyReturnCode`에 기록하고 S19 실행을 차단한다. |