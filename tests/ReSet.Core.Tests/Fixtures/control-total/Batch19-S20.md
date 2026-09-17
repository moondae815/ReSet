### S20 | 통합 정합성 검증

#### 목적과 실행 계약

S12에서 동결한 원장 제어합계와 S13～S19 처리 결과를 비교하여 다음 정합성을 검증한다.

- S12 원장 동결 이후 `SETTLE_POQ_DB.dbo.TSettleMst`가 변경되지 않았는지 검증한다.
- `SETTLE_POQ_DB.dbo.TSettleByTX`, `SETTLE_POQ_DB.dbo.TPartialCancelByTX`, `SETTLE_POQ_DB.dbo.TSettleByIN`, `SETTLE_POQ_DB.dbo.TSettleByOUT`의 건수·금액 합계가 원장과 일치하는지 검증한다.
- `SETTLE_POQ_DB.dbo.TStatPGCollect`의 차변·대변 균형을 검증한다.
- `SETTLE_POQ_DB.dbo.TSettleMiss`의 후취정산 금액이 원장 기준 계산액과 일치하는지 검증한다.
- 검증 결과와 현재 제어합계는 각각 `batch.BatchReconciliation`, `batch.BatchControlTotal`에 저장한다.
- 단계 시작 시 `batch.BatchStepJournal`, `batch.BatchCheckpoint`에 S20 자신의 행을 반드시 INSERT하고, 성공 시 그 행만 갱신한다.

S20은 S19 성공 후, S21 실행 전에 단독으로 호출한다. 앞선 단계와 병렬 실행하지 않으며 모든 업무 조회와 제어 DML은 SNAPSHOT 격리에서 수행한다. 원본에 존재하던 `NOLOCK` 계열 힌트는 검증 SQL에서 전부 제거한다.

이 단계는 `Chunkable=false`이다. 전체 원장·요약 집계 간 스칼라 비교이므로 청크 키를 추가하지 않는다. 업무 처리는 하나의 트랜잭션으로 완료하며 Shadow Table이나 롤백 후 보상 DELETE를 사용하지 않는다.

#### 입력 및 오류 계약

레거시 프로시저를 대체하지 않는 제어 단계이므로 레거시 입출력 파라미터는 없다. 오케스트레이션 실행 문맥에서 다음 값을 사용한다.

- `RunId BIGINT -> p_runId`
- `BatchYmd VARCHAR(8) -> p_batchYmd`
- 단계 식별자는 상수 `N'S20'`이다.
- 재시작·건너뛰기·우회 플래그는 받지 않는다. 호출 여부는 오케스트레이터가 `batch.BatchCheckpoint`를 읽어 외부에서 결정한다.

단계 로컬 상태는 `INT currentStepErrorCode = 0`으로 시작한다. S20의 일반 실패 코드는 **-9200**이며, 제어합계 수집, 정합성 결과 저장, 불일치 게이트, 성공 상태 확정 중 어느 지점에서 실패하더라도 `batch.BatchStepJournal.LegacyReturnCode`에 **-9200**을 기록한다. 정상 완료 시에는 `0`을 기록한다.

| 상태 | `LegacyReturnCode` | 처리 |
|---|---:|---|
| 정상 | 0 | 모든 검증이 `Matched`이고 S20 체크포인트가 성공으로 확정됨 |
| 실패 | -9200 | SQL 실행 실패, 필수 S12 제어합계 누락, 정합성 불일치 또는 상태 확정 실패 |

#### `batch.BatchReconciliation` 객체 계약

`batch.BatchReconciliation`은 배치 부트스트랩에서 한 번 생성하며 S20 실행 중에는 DDL을 수행하지 않는다.

```sql
CREATE TABLE batch.BatchReconciliation
(
    RunId                 BIGINT          NOT NULL,
    StepCode              NVARCHAR(10)    NOT NULL,
    ReconciliationName    NVARCHAR(64)    NOT NULL,
    ScopeKey              NVARCHAR(200)   NOT NULL,
    ExpectedValue         DECIMAL(38,4)   NULL,
    ActualValue           DECIMAL(38,4)   NULL,
    DifferenceValue       DECIMAL(38,4)   NULL,
    ReconciliationStatus  NVARCHAR(20)    NOT NULL,
    DetectedAtUtc         DATETIME2(3)    NOT NULL,
    CONSTRAINT PK_BatchReconciliation
        PRIMARY KEY (RunId, StepCode, ReconciliationName, ScopeKey),
    CONSTRAINT CK_BatchReconciliation_Status
        CHECK (ReconciliationStatus IN (N'Matched', N'Mismatched'))
);
```

S12는 같은 `RunId`에 대해 다음 `ControlName`을 `batch.BatchControlTotal`에 기록해 두어야 한다.

- `Ledger.RowCount`
- `Ledger.TxAmt`
- `Ledger.CLTotal`
- `Ledger.PGTotal`
- `Ledger.POQIncome`

#### 애플리케이션 실행 흐름

논리 구성요소의 책임은 다음과 같다.

- **단계 상태 기록기**: S20 전용 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint` 행 생성과 종료 상태 갱신
- **제어합계 수집기**: 원장·요약·통계·후취정산 현재값을 `batch.BatchControlTotal`에 저장
- **정합성 평가기**: S12 기대값과 S20 실제값을 비교하여 `batch.BatchReconciliation`에 저장
- **검증 게이트**: `Mismatched` 결과가 한 건이라도 있으면 업무 트랜잭션을 롤백하고 파이프라인 중단

```pseudocode
stepCode = "S20"
currentStepErrorCode = 0
currentStatementId = null
conn = connectionFactory.open()

// 시작 행은 업무 트랜잭션보다 먼저 별도 SNAPSHOT 제어 트랜잭션에서 확정한다.
// 따라서 이후 업무 롤백이 발생해도 실패 상태로 갱신할 S20 자신의 행이 남아 있다.
startTx = conn.beginTransaction()
TRY:
    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_INSERT_JOURNAL"
    repository.execute(conn, startTx, SQL_S20_INSERT_JOURNAL, {
        p_runId: runId,
        p_stepCode: stepCode
    })

    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_INSERT_CHECKPOINT"
    repository.execute(conn, startTx, SQL_S20_INSERT_CHECKPOINT, {
        p_runId: runId,
        p_stepCode: stepCode
    })

    startTx.commit()
CATCH error:
    startTx.rollbackIfOpen()
    // 시작 행 생성 자체가 실패했으므로 가능한 범위의 제어 오류를 상위 오케스트레이터에 전달한다.
    stop pipeline with code -9200
    close connection
    return

tx = conn.beginTransaction()

TRY:
    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_CAPTURE_CONTROL_TOTALS"
    repository.execute(conn, tx, SQL_S20_CAPTURE_CONTROL_TOTALS, {
        p_runId: runId,
        p_batchYmd: batchYmd
    })

    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_INSERT_RECONCILIATION"
    repository.execute(conn, tx, SQL_S20_INSERT_RECONCILIATION, {
        p_runId: runId,
        p_batchYmd: batchYmd
    })

    currentStatementId = "SQL_S20_FIND_MISMATCH"
    mismatches = repository.queryRows(conn, tx, SQL_S20_FIND_MISMATCH, {
        p_runId: runId
    })

    IF mismatches is not empty:
        currentStepErrorCode = -9200
        currentStatementId = "S20_VALIDATION_GATE"
        throw validationFailure(mismatches)

    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_MARK_JOURNAL_SUCCEEDED"
    repository.execute(conn, tx, SQL_S20_MARK_JOURNAL_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    })

    currentStepErrorCode = -9200
    currentStatementId = "SQL_S20_MARK_CHECKPOINT_SUCCEEDED"
    repository.execute(conn, tx, SQL_S20_MARK_CHECKPOINT_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    })

    tx.commit()
    currentStepErrorCode = 0

CATCH error:
    tx.rollbackIfOpen()

    // 롤백으로 batch.BatchControlTotal과 batch.BatchReconciliation의 S20 신규 행도 제거된다.
    // 업무 데이터에 별도 보상 DELETE를 실행하지 않는다.
    failureTx = conn.beginTransaction()
    TRY:
        currentStepErrorCode = -9200
        currentStatementId = "SQL_S20_MARK_JOURNAL_FAILED"
        repository.execute(conn, failureTx, SQL_S20_MARK_JOURNAL_FAILED, {
            p_runId: runId,
            p_stepCode: stepCode,
            p_legacyReturnCode: -9200,
            p_errorMessage: formatError(currentStatementId, error)
        })
        failureTx.commit()
    CATCH journalError:
        failureTx.rollbackIfOpen()
        propagate both original error and journalError

    stop dependent pipeline
FINALLY:
    conn.close()
```

#### 단계 시작 및 종료 SQL

```sql
-- SQL_S20_INSERT_JOURNAL
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
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
-- SQL_S20_INSERT_CHECKPOINT
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
    @p_stepCode,
    N'Pending',
    NULL
);
```

```sql
-- SQL_S20_MARK_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = 0,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';
```

```sql
-- SQL_S20_MARK_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND CheckpointStatus = N'Pending';
```

```sql
-- SQL_S20_MARK_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';
```

실패 시 `batch.BatchCheckpoint`는 허용 상태값인 `Pending`으로 유지한다. 실패 상태를 나타내기 위해 허용되지 않은 체크포인트 상태값을 추가하지 않는다.

#### 현재 제어합계 수집 SQL

아래 한 문장이 S20의 현재값을 `batch.BatchControlTotal`에 INSERT한다. 각 비교 모집단은 독립적으로 집계되며, 집계 간 `CROSS JOIN`을 사용하지 않는다.

```sql
-- SQL_S20_CAPTURE_CONTROL_TOTALS
;WITH
TouchedPostBilling AS
(
    SELECT DISTINCT
           A.ClientID,
           A.OutYMD
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON B.YMD = A.YMD
       AND B.ClientID = A.ClientID
       AND B.PGName = A.PGName
       AND B.MallID = A.MallID
      JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON C.ClientID = A.ClientID
     WHERE A.YMD = @p_batchYmd
       AND A.OutState = 2
       AND ISNULL(B.TaxFGBill, 2) = 1
),
PostBillingSource AS
(
    SELECT ISNULL(SUM(CAST(A.CLTotal AS DECIMAL(38,4))), 0) AS ControlValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON B.YMD = A.YMD
       AND B.ClientID = A.ClientID
       AND B.PGName = A.PGName
       AND B.MallID = A.MallID
      JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON C.ClientID = A.ClientID
      JOIN TouchedPostBilling AS T
        ON T.ClientID = A.ClientID
       AND T.OutYMD = A.OutYMD
     WHERE A.OutState = 2
       AND ISNULL(B.TaxFGBill, 2) = 1
),
PostBillingMiss AS
(
    SELECT ISNULL(SUM(CAST(M.CLSettleAmt AS DECIMAL(38,4))), 0) AS ControlValue
      FROM SETTLE_POQ_DB.dbo.TSettleMiss AS M
      JOIN TouchedPostBilling AS T
        ON T.ClientID = M.ClientID
       AND T.OutYMD = M.OutYMD
     WHERE M.OutState = 2
       AND ISNULL(M.IssueType, 0) = 15
),
Controls AS
(
    SELECT N'Ledger.RowCount' AS ControlName,
           CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS ControlValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.CLTotal',
           ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.PGTotal',
           ISNULL(SUM(CAST(PGTotal AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.POQIncome',
           ISNULL(SUM(CAST(POQIncome AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.Tx.RowCount',
           ISNULL(SUM(CAST(TxCnt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.Tx.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.Tx.CLTotal',
           ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.Tx.PGTotal',
           ISNULL(SUM(CAST(PGTotal AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.Tx.POQIncome',
           ISNULL(SUM(CAST(POQIncome AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.Partial.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND UseState = 2

    UNION ALL
    SELECT N'Ledger.Partial.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND UseState = 2

    UNION ALL
    SELECT N'Summary.Partial.RowCount',
           ISNULL(SUM(CAST(TxCnt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @p_batchYmd
       AND UseState = 2

    UNION ALL
    SELECT N'Summary.Partial.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @p_batchYmd
       AND UseState = 2

    UNION ALL
    SELECT N'Ledger.In.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND InState = 1

    UNION ALL
    SELECT N'Ledger.In.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND InState = 1

    UNION ALL
    SELECT N'Summary.In.RowCount',
           ISNULL(SUM(CAST(InCnt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Summary.In.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @p_batchYmd

    UNION ALL
    SELECT N'Ledger.Out.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND OutState IN (2, 9)

    UNION ALL
    SELECT N'Ledger.Out.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND OutState IN (2, 9)

    UNION ALL
    SELECT N'Summary.Out.RowCount',
           ISNULL(SUM(CAST(OutCnt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @p_batchYmd
       AND OutState IN (2, 9)

    UNION ALL
    SELECT N'Summary.Out.TxAmt',
           ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @p_batchYmd
       AND OutState IN (2, 9)

    UNION ALL
    SELECT N'StatPGCollect.UnbalancedRowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE InYMD = @p_batchYmd
       AND ISNULL(LeftSumAmt, 0) <> ISNULL(RightSumAmt, 0)

    UNION ALL
    SELECT N'StatPGCollect.BalanceDifference',
           ISNULL(
               SUM(
                   CAST(ISNULL(LeftSumAmt, 0) AS DECIMAL(38,4))
                 - CAST(ISNULL(RightSumAmt, 0) AS DECIMAL(38,4))
               ),
               0
           )
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE InYMD = @p_batchYmd

    UNION ALL
    SELECT N'PostBilling.SourceAmount', ControlValue
      FROM PostBillingSource

    UNION ALL
    SELECT N'PostBilling.MissAmount', ControlValue
      FROM PostBillingMiss

    UNION ALL
    SELECT N'Expected.Zero', CAST(0 AS DECIMAL(38,4))
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
    N'S20',
    ControlName,
    ControlValue,
    SYSUTCDATETIME()
FROM Controls;
```

#### 정합성 결과 저장 SQL

각 기대값과 실제값은 `batch.BatchControlTotal`에서 독립 스칼라 하위질의로 집계한다. 비교를 위해 두 집계 결과를 `CROSS JOIN`하지 않는다. S12 값이 누락된 경우에도 비교 행을 생성하고 상태를 `Mismatched`로 기록한다.

```sql
-- SQL_S20_INSERT_RECONCILIATION
;WITH PairDefinition AS
(
    SELECT N'FrozenLedger.RowCount' AS ReconciliationName,
           N'S12' AS ExpectedStepCode, N'Ledger.RowCount' AS ExpectedControlName,
           N'S20' AS ActualStepCode,   N'Ledger.RowCount' AS ActualControlName
    UNION ALL
    SELECT N'FrozenLedger.TxAmt',
           N'S12', N'Ledger.TxAmt',
           N'S20', N'Ledger.TxAmt'
    UNION ALL
    SELECT N'FrozenLedger.CLTotal',
           N'S12', N'Ledger.CLTotal',
           N'S20', N'Ledger.CLTotal'
    UNION ALL
    SELECT N'FrozenLedger.PGTotal',
           N'S12', N'Ledger.PGTotal',
           N'S20', N'Ledger.PGTotal'
    UNION ALL
    SELECT N'FrozenLedger.POQIncome',
           N'S12', N'Ledger.POQIncome',
           N'S20', N'Ledger.POQIncome'

    UNION ALL
    SELECT N'SummaryTx.RowCount',
           N'S20', N'Ledger.RowCount',
           N'S20', N'Summary.Tx.RowCount'
    UNION ALL
    SELECT N'SummaryTx.TxAmt',
           N'S20', N'Ledger.TxAmt',
           N'S20', N'Summary.Tx.TxAmt'
    UNION ALL
    SELECT N'SummaryTx.CLTotal',
           N'S20', N'Ledger.CLTotal',
           N'S20', N'Summary.Tx.CLTotal'
    UNION ALL
    SELECT N'SummaryTx.PGTotal',
           N'S20', N'Ledger.PGTotal',
           N'S20', N'Summary.Tx.PGTotal'
    UNION ALL
    SELECT N'SummaryTx.POQIncome',
           N'S20', N'Ledger.POQIncome',
           N'S20', N'Summary.Tx.POQIncome'

    UNION ALL
    SELECT N'PartialSummary.RowCount',
           N'S20', N'Ledger.Partial.RowCount',
           N'S20', N'Summary.Partial.RowCount'
    UNION ALL
    SELECT N'PartialSummary.TxAmt',
           N'S20', N'Ledger.Partial.TxAmt',
           N'S20', N'Summary.Partial.TxAmt'

    UNION ALL
    SELECT N'InSummary.RowCount',
           N'S20', N'Ledger.In.RowCount',
           N'S20', N'Summary.In.RowCount'
    UNION ALL
    SELECT N'InSummary.TxAmt',
           N'S20', N'Ledger.In.TxAmt',
           N'S20', N'Summary.In.TxAmt'

    UNION ALL
    SELECT N'OutSummary.RowCount',
           N'S20', N'Ledger.Out.RowCount',
           N'S20', N'Summary.Out.RowCount'
    UNION ALL
    SELECT N'OutSummary.TxAmt',
           N'S20', N'Ledger.Out.TxAmt',
           N'S20', N'Summary.Out.TxAmt'

    UNION ALL
    SELECT N'StatPGCollect.UnbalancedRows',
           N'S20', N'Expected.Zero',
           N'S20', N'StatPGCollect.UnbalancedRowCount'
    UNION ALL
    SELECT N'StatPGCollect.Balance',
           N'S20', N'Expected.Zero',
           N'S20', N'StatPGCollect.BalanceDifference'

    UNION ALL
    SELECT N'PostBilling.Amount',
           N'S20', N'PostBilling.SourceAmount',
           N'S20', N'PostBilling.MissAmount'
),
Evaluated AS
(
    SELECT
        P.ReconciliationName,
        (
            SELECT MAX(E.ControlValue)
              FROM batch.BatchControlTotal AS E
             WHERE E.RunId = @p_runId
               AND E.StepCode = P.ExpectedStepCode
               AND E.ControlName = P.ExpectedControlName
        ) AS ExpectedValue,
        (
            SELECT MAX(A.ControlValue)
              FROM batch.BatchControlTotal AS A
             WHERE A.RunId = @p_runId
               AND A.StepCode = P.ActualStepCode
               AND A.ControlName = P.ActualControlName
        ) AS ActualValue
    FROM PairDefinition AS P
)
INSERT INTO batch.BatchReconciliation
(
    RunId,
    StepCode,
    ReconciliationName,
    ScopeKey,
    ExpectedValue,
    ActualValue,
    DifferenceValue,
    ReconciliationStatus,
    DetectedAtUtc
)
SELECT
    @p_runId,
    N'S20',
    ReconciliationName,
    @p_batchYmd,
    ExpectedValue,
    ActualValue,
    CASE
        WHEN ExpectedValue IS NULL OR ActualValue IS NULL THEN NULL
        ELSE ActualValue - ExpectedValue
    END,
    CASE
        WHEN ExpectedValue IS NOT NULL
         AND ActualValue IS NOT NULL
         AND ExpectedValue = ActualValue
        THEN N'Matched'
        ELSE N'Mismatched'
    END,
    SYSUTCDATETIME()
FROM Evaluated;
```

#### 불일치 게이트 SQL

```sql
-- SQL_S20_FIND_MISMATCH
SELECT
    ReconciliationName,
    ScopeKey,
    ExpectedValue,
    ActualValue,
    DifferenceValue
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND StepCode = N'S20'
  AND ReconciliationStatus = N'Mismatched'
ORDER BY ReconciliationName, ScopeKey;
```

조회 결과가 한 건 이상이면 애플리케이션은 `currentStepErrorCode = -9200`, `currentStatementId = "S20_VALIDATION_GATE"`를 유지한 채 업무 트랜잭션을 롤백한다. 오류 메시지에는 최소한 `ReconciliationName`, `ExpectedValue`, `ActualValue`, `DifferenceValue`를 포함한다. 롤백 후 `batch.BatchStepJournal`의 S20 행만 `Failed`로 갱신하고 `batch.BatchCheckpoint`의 S20 행은 `Pending`으로 남긴다.