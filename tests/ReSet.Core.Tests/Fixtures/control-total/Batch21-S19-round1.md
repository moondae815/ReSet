### S19 | 통합 정합성 검증

#### 목적 및 실행 계약

S18이 성공한 뒤 S20보다 먼저 실행하며, 다른 단계와 병렬 실행하지 않는다. 다음 대상에 검증 기준값과 결과를 기록한다.

- `batch.BatchStepJournal`
- `batch.BatchCheckpoint`
- `batch.BatchControlTotal`
- `batch.BatchReconciliation`

이 단계는 레거시 프로시저를 대체하지 않는 제어 단계다. 일반 실패 코드는 **-9190**이며, 단계 로컬 `currentStepErrorCode`는 `INT` 형식의 `0`으로 초기화한다. 입력은 오케스트레이션 컨텍스트의 `RunId bigint`와 `BatchYmd varchar(8)`이며 별도 출력 파라미터는 없다.

모든 읽기와 쓰기는 **SNAPSHOT 격리**를 만족하는 하나의 업무 트랜잭션에서 실행한다. 청킹, 섀도 테이블, 실패 후 보상 DELETE는 사용하지 않는다. 검증 불일치나 SQL 오류가 발생하면 업무 트랜잭션 전체를 롤백하므로 `batch.BatchControlTotal`과 `batch.BatchReconciliation`에는 부분 결과가 남지 않는다. 모든 SQL에서 `NOLOCK` 힌트를 제거한다.

S12는 다음 이름으로 동결 기준값을 `batch.BatchControlTotal`에 기록해야 한다.

- `Ledger.RowCount`
- `Ledger.TxAmt`
- `Ledger.CLTotal`
- `Ledger.PGTotal`
- `Ledger.POQIncome`

`batch.BatchReconciliation`은 배치 부트스트랩에서 다음 형태로 생성한다.

```sql
CREATE TABLE batch.BatchReconciliation
(
    RunId               bigint          NOT NULL,
    StepCode             nvarchar(10)    NOT NULL,
    ReconciliationName   nvarchar(128)   NOT NULL,
    ExpectedValue        decimal(38,4)   NULL,
    ActualValue          decimal(38,4)   NULL,
    DifferenceValue      decimal(38,4)   NULL,
    IsMatched             bit             NOT NULL,
    CapturedAtUtc         datetime2(3)     NOT NULL,
    CONSTRAINT PK_BatchReconciliation
        PRIMARY KEY (RunId, StepCode, ReconciliationName)
);
```

#### 애플리케이션 제어 흐름

```pseudocode
component S19IntegratedReconciliationStep

inputs:
    runId: bigint
    businessYmd: varchar(8)

currentStatementName = NULL
currentStepErrorCode: INT = 0
conn = connectionFactory.open()

// batch.BatchStepJournal 및 batch.BatchCheckpoint의 S19 시작 행은
// 공통 제어 트랜잭션 패턴으로 먼저 INSERT한다.

TRY:
    businessTx = conn.beginTransaction()   // SNAPSHOT 격리 의무

    currentStatementName = "SQL_INSERT_PREREQUISITE_RECONCILIATION"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_PREREQUISITE_RECONCILIATION,
        {
            p_runId: runId,
            p_stepCode: N"S19",
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_INSERT_CURRENT_CONTROL_TOTALS"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_CURRENT_CONTROL_TOTALS,
        {
            p_runId: runId,
            p_businessYmd: businessYmd,
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_INSERT_FREEZE_RECONCILIATION"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_FREEZE_RECONCILIATION,
        {
            p_runId: runId,
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_INSERT_SUMMARY_RECONCILIATION"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_SUMMARY_RECONCILIATION,
        {
            p_runId: runId,
            p_businessYmd: businessYmd,
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_INSERT_STAT_RECONCILIATION"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_STAT_RECONCILIATION,
        {
            p_runId: runId,
            p_businessYmd: businessYmd,
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_INSERT_POST_SETTLE_RECONCILIATION"
    currentStepErrorCode = -9190
    repository.execute(
        conn,
        businessTx,
        SQL_INSERT_POST_SETTLE_RECONCILIATION,
        {
            p_runId: runId,
            p_businessYmd: businessYmd,
            p_capturedAtUtc: utcNow
        })

    currentStatementName = "SQL_READ_RECONCILIATION_FAILURES"
    currentStepErrorCode = -9190
    mismatches = repository.queryRows(
        conn,
        businessTx,
        SQL_READ_RECONCILIATION_FAILURES,
        { p_runId: runId })

    IF mismatches is not empty:
        // SQL 결과에 따른 제어 분기는 애플리케이션이 수행한다.
        // 조회한 상세는 롤백 전에 메모리에 보존하여 ErrorMessage에 기록한다.
        validationDetails = serialize reconciliation names and expected/actual values
        raise application validation failure

    businessTx.commit()

    // 별도 제어 트랜잭션에서 이 단계가 시작하며 INSERT한 행만 갱신한다.
    // batch.BatchStepJournal.StepStatus = Succeeded
    // batch.BatchCheckpoint.CheckpointStatus = Succeeded
    // 레거시 성공 코드가 없으므로 LegacyReturnCode는 NULL이다.

ON FAILURE observed by the application:
    rollback businessTx if open

    // currentStatementName과 -9190을 별도 제어 트랜잭션으로 기록한다.
    // batch.BatchStepJournal.LegacyReturnCode = -9190
    // batch.BatchCheckpoint는 Pending 상태로 유지한다.
    // batch.BatchRun.ResumeFromStepCode = N"S19"
    record failure using the common journal and run-failure statements
    stop the pipeline and invoke S20 failure publication
```

#### 선행 단계 완료 검증

RunId가 존재하지 않는 S01은 완료 게이트에서 제외한다. S02부터 S18까지 각각 `batch.BatchCheckpoint.CheckpointStatus = N'Succeeded'`와 `batch.BatchStepJournal.StepStatus = N'Succeeded'`가 모두 존재해야 한다. `EXISTS`를 사용하여 중복 저널 행이 집계를 증폭시키지 않도록 한다.

```sql
-- SQL_INSERT_PREREQUISITE_RECONCILIATION
WITH RequiredStep AS
(
    SELECT StepCode
    FROM
    (
        VALUES
            (N'S02'), (N'S03'), (N'S04'), (N'S05'), (N'S06'),
            (N'S07'), (N'S08'), (N'S09'), (N'S10'), (N'S11'),
            (N'S12'), (N'S13'), (N'S14'), (N'S15'), (N'S16'),
            (N'S17'), (N'S18')
    ) AS V(StepCode)
),
ValidationResult AS
(
    SELECT SUM
    (
        CASE
            WHEN EXISTS
                 (
                     SELECT 1
                     FROM batch.BatchCheckpoint AS C
                     WHERE C.RunId = @p_runId
                       AND C.StepCode = R.StepCode
                       AND C.CheckpointStatus = N'Succeeded'
                 )
             AND EXISTS
                 (
                     SELECT 1
                     FROM batch.BatchStepJournal AS J
                     WHERE J.RunId = @p_runId
                       AND J.StepCode = R.StepCode
                       AND J.StepStatus = N'Succeeded'
                 )
            THEN 0
            ELSE 1
        END
    ) AS MismatchCount
    FROM RequiredStep AS R
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
    CapturedAtUtc
)
SELECT @p_runId,
       @p_stepCode,
       N'Prerequisite.S02-S18.SuccessMismatchCount',
       CAST(0 AS decimal(38,4)),
       CAST(MismatchCount AS decimal(38,4)),
       CAST(MismatchCount AS decimal(38,4)),
       IIF(MismatchCount = 0, 1, 0),
       @p_capturedAtUtc
FROM ValidationResult;
```

#### 현재 통제 합계 수집

각 집계는 독립적으로 계산하며, 서로 다른 집계 결과를 `CROSS JOIN`으로 결합하지 않는다.

```sql
-- SQL_INSERT_CURRENT_CONTROL_TOTALS
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId, N'S19', N'Ledger.RowCount',
       CAST(COUNT_BIG(*) AS decimal(38,4)), @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Ledger.TxAmt',
       CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Ledger.CLTotal',
       CAST(COALESCE(SUM(CAST(CLTotal AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Ledger.PGTotal',
       CAST(COALESCE(SUM(CAST(PGTotal AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Ledger.POQIncome',
       CAST(COALESCE(SUM(CAST(POQIncome AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Summary.TSettleByTX.TxCnt',
       CAST(COALESCE(SUM(CAST(TxCnt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleByTX
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Summary.TPartialCancelByTX.TxCnt',
       CAST(COALESCE(SUM(CAST(TxCnt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Summary.TSettleByIN.InCnt',
       CAST(COALESCE(SUM(CAST(InCnt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleByIN
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Summary.TSettleByOUT.OutCnt',
       CAST(COALESCE(SUM(CAST(OutCnt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleByOUT
WHERE YMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Stat.TStatPGCollect.RowCount',
       CAST(COUNT_BIG(*) AS decimal(38,4)), @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TStatPGCollect
WHERE INYMD = @p_businessYmd

UNION ALL

SELECT @p_runId, N'S19', N'Post.TSettleMiss.CLSettleAmt',
       CAST(COALESCE(SUM(CAST(CLSettleAmt AS decimal(38,4))), 0) AS decimal(38,4)),
       @p_capturedAtUtc
FROM SETTLE_POQ_DB.dbo.TSettleMiss
WHERE YMD = @p_businessYmd
  AND OutState = 2
  AND ISNULL(IssueType, 0) = 15;
```

#### S12 동결값 대조

S12와 S19의 통제 합계는 `ControlName`으로 결합한다. 어느 한쪽이 누락된 경우도 실패로 판정한다.

```sql
-- SQL_INSERT_FREEZE_RECONCILIATION
WITH Frozen AS
(
    SELECT ControlName,
           MAX(ControlValue) AS ControlValue
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId
      AND StepCode = N'S12'
      AND ControlName IN
          (
              N'Ledger.RowCount',
              N'Ledger.TxAmt',
              N'Ledger.CLTotal',
              N'Ledger.PGTotal',
              N'Ledger.POQIncome'
          )
    GROUP BY ControlName
),
CurrentValue AS
(
    SELECT ControlName,
           MAX(ControlValue) AS ControlValue
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId
      AND StepCode = N'S19'
      AND ControlName IN
          (
              N'Ledger.RowCount',
              N'Ledger.TxAmt',
              N'Ledger.CLTotal',
              N'Ledger.PGTotal',
              N'Ledger.POQIncome'
          )
    GROUP BY ControlName
),
Comparison AS
(
    SELECT COALESCE(F.ControlName, C.ControlName) AS ControlName,
           F.ControlValue AS ExpectedValue,
           C.ControlValue AS ActualValue
    FROM Frozen AS F
    FULL OUTER JOIN CurrentValue AS C
      ON C.ControlName = F.ControlName
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
    CapturedAtUtc
)
SELECT @p_runId,
       N'S19',
       N'Freeze.' + ControlName,
       ExpectedValue,
       ActualValue,
       CASE
           WHEN ExpectedValue IS NULL OR ActualValue IS NULL THEN NULL
           ELSE ActualValue - ExpectedValue
       END,
       IIF(ExpectedValue IS NOT NULL
           AND ActualValue IS NOT NULL
           AND ExpectedValue = ActualValue, 1, 0),
       @p_capturedAtUtc
FROM Comparison;
```

#### 원장과 요약 테이블 대조

`SETTLE_POQ_DB.dbo.TSettleMst`의 필터와 각 요약 테이블의 대상 범위를 동일하게 유지한다. 기대값과 실제값은 각각 독립 CTE에서 집계한 뒤 이름으로 결합한다.

```sql
-- SQL_INSERT_SUMMARY_RECONCILIATION
WITH ExpectedValue AS
(
    SELECT N'Summary.TSettleByTX.RowCount' AS ReconciliationName,
           CAST(COUNT_BIG(*) AS decimal(38,4)) AS ControlValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TSettleByTX.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TPartialCancelByTX.RowCount',
           CAST(COUNT_BIG(*) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND UseState = 2

    UNION ALL

    SELECT N'Summary.TPartialCancelByTX.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND UseState = 2

    UNION ALL

    SELECT N'Summary.TSettleByIN.RowCount',
           CAST(COUNT_BIG(*) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND InState = 1

    UNION ALL

    SELECT N'Summary.TSettleByIN.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND InState = 1

    UNION ALL

    SELECT N'Summary.TSettleByOUT.RowCount',
           CAST(COUNT_BIG(*) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND OutState IN (2, 9)

    UNION ALL

    SELECT N'Summary.TSettleByOUT.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND OutState IN (2, 9)
),
ActualValue AS
(
    SELECT N'Summary.TSettleByTX.RowCount' AS ReconciliationName,
           CAST(COALESCE(SUM(CAST(TxCnt AS decimal(38,4))), 0) AS decimal(38,4))
               AS ControlValue
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TSettleByTX.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TPartialCancelByTX.RowCount',
           CAST(COALESCE(SUM(CAST(TxCnt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TPartialCancelByTX.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TSettleByIN.RowCount',
           CAST(COALESCE(SUM(CAST(InCnt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TSettleByIN.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'Summary.TSettleByOUT.RowCount',
           CAST(COALESCE(SUM(CAST(OutCnt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_businessYmd
      AND OutState IN (2, 9)

    UNION ALL

    SELECT N'Summary.TSettleByOUT.TxAmt',
           CAST(COALESCE(SUM(CAST(TxAmt AS decimal(38,4))), 0) AS decimal(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_businessYmd
      AND OutState IN (2, 9)
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
    CapturedAtUtc
)
SELECT @p_runId,
       N'S19',
       E.ReconciliationName,
       E.ControlValue,
       A.ControlValue,
       A.ControlValue - E.ControlValue,
       IIF(A.ControlValue = E.ControlValue, 1, 0),
       @p_capturedAtUtc
FROM ExpectedValue AS E
INNER JOIN ActualValue AS A
  ON A.ReconciliationName = E.ReconciliationName;
```

#### PG 수납통계 내부 등식 검증

```sql
-- SQL_INSERT_STAT_RECONCILIATION
WITH ValidationResult AS
(
    SELECT SUM
    (
        CASE
            WHEN ISNULL(LeftSumAmt, 0)
                 <> ISNULL(CollectAmt, 0)
                  + ISNULL(PGComm, 0)
                  + ISNULL(PGVT, 0)
              OR ISNULL(RightSumAmt, 0)
                 <> ISNULL(SettleWillAmt, 0)
                  + ISNULL(AheadSalesComm, 0)
                  + ISNULL(AheadSalesVT, 0)
                  + ISNULL(AheadSettleAmt, 0)
            THEN 1
            ELSE 0
        END
    ) AS MismatchCount
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_businessYmd
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
    CapturedAtUtc
)
SELECT @p_runId,
       N'S19',
       N'Stat.TStatPGCollect.BalanceMismatchCount',
       CAST(0 AS decimal(38,4)),
       CAST(ISNULL(MismatchCount, 0) AS decimal(38,4)),
       CAST(ISNULL(MismatchCount, 0) AS decimal(38,4)),
       IIF(ISNULL(MismatchCount, 0) = 0, 1, 0),
       @p_capturedAtUtc
FROM ValidationResult;
```

#### 후취정산 정합성 검증

S13이 처리한 기준일의 고객사·지급일 조합을 먼저 구한 뒤, 원장 전체의 청구 대상 금액과 `SETTLE_POQ_DB.dbo.TSettleMiss`의 `IssueType = 15` 반영액을 비교한다.

```sql
-- SQL_INSERT_POST_SETTLE_RECONCILIATION
WITH AffectedGroup AS
(
    SELECT DISTINCT A.ClientID,
                    A.OutYMD
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
      ON B.YMD = A.YMD
     AND B.ClientID = A.ClientID
     AND B.PGName = A.PGName
     AND B.MallID = A.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
      ON C.ClientID = A.ClientID
    WHERE A.YMD = @p_businessYmd
      AND ISNULL(B.TaxFGBill, 2) = 1
      AND A.OutState = 2
),
ExpectedByGroup AS
(
    SELECT A.ClientID,
           A.OutYMD,
           CAST(COALESCE(SUM(CAST(A.CLTotal AS decimal(38,4))), 0)
                AS decimal(38,4)) AS ExpectedValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
      ON B.YMD = A.YMD
     AND B.ClientID = A.ClientID
     AND B.PGName = A.PGName
     AND B.MallID = A.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
      ON C.ClientID = A.ClientID
    INNER JOIN AffectedGroup AS G
      ON G.ClientID = A.ClientID
     AND G.OutYMD = A.OutYMD
    WHERE ISNULL(B.TaxFGBill, 2) = 1
      AND A.OutState = 2
    GROUP BY A.ClientID, A.OutYMD
),
ActualByGroup AS
(
    SELECT M.ClientID,
           M.OutYMD,
           CAST(COALESCE(SUM(CAST(M.CLSettleAmt AS decimal(38,4))), 0)
                AS decimal(38,4)) AS ActualValue
    FROM SETTLE_POQ_DB.dbo.TSettleMiss AS M
    INNER JOIN AffectedGroup AS G
      ON G.ClientID = M.ClientID
     AND G.OutYMD = M.OutYMD
    WHERE M.OutState = 2
      AND ISNULL(M.IssueType, 0) = 15
    GROUP BY M.ClientID, M.OutYMD
),
ComparedGroup AS
(
    SELECT COALESCE(E.ClientID, A.ClientID) AS ClientID,
           COALESCE(E.OutYMD, A.OutYMD) AS OutYMD,
           E.ExpectedValue,
           A.ActualValue
    FROM ExpectedByGroup AS E
    FULL OUTER JOIN ActualByGroup AS A
      ON A.ClientID = E.ClientID
     AND A.OutYMD = E.OutYMD
),
ValidationResult AS
(
    SELECT SUM
    (
        CASE
            WHEN ExpectedValue IS NULL
              OR ActualValue IS NULL
              OR ExpectedValue <> ActualValue
            THEN 1
            ELSE 0
        END
    ) AS MismatchCount
    FROM ComparedGroup
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
    CapturedAtUtc
)
SELECT @p_runId,
       N'S19',
       N'Post.TSettleMiss.GroupMismatchCount',
       CAST(0 AS decimal(38,4)),
       CAST(ISNULL(MismatchCount, 0) AS decimal(38,4)),
       CAST(ISNULL(MismatchCount, 0) AS decimal(38,4)),
       IIF(ISNULL(MismatchCount, 0) = 0, 1, 0),
       @p_capturedAtUtc
FROM ValidationResult;
```

#### 최종 판정 및 재시작

```sql
-- SQL_READ_RECONCILIATION_FAILURES
SELECT ReconciliationName,
       ExpectedValue,
       ActualValue,
       DifferenceValue
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND StepCode = N'S19'
  AND IsMatched = 0
ORDER BY ReconciliationName;
```

불일치가 한 건이라도 있으면 애플리케이션은 업무 트랜잭션을 롤백하고 `batch.BatchStepJournal.LegacyReturnCode`에 **-9190**을 기록한다. `batch.BatchCheckpoint`는 `Pending`으로 유지하고 재시작 지점을 `S19`로 설정한다. 재호출 시 S02~S18 선행 검증부터 전체 검증을 다시 수행하며, 성공한 S19의 재호출 차단은 오케스트레이터가 담당한다.