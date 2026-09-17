### S12 | 정산원장 동결

#### 목적 및 인터페이스

S04~S11이 순차적으로 구축·보정한 정산원장을 논리적으로 동결하고, 이후 통계·요약 단계가 사용할 기준 금액을 `batch.BatchControlTotal`에 저장한다. 이 단계는 `SETTLE_POQ_DB.dbo.TSettleMst`를 변경하지 않으며, 동결은 다음 상태를 원자적으로 확정하는 논리적 경계이다.

- S04~S11의 체크포인트가 모두 `Succeeded`인지 확인한다.
- 기준일 원장과 당일 처리된 Extra 원장의 건수·금액을 동일한 SNAPSHOT 시점에서 집계한다.
- `batch.BatchControlTotal`에 통제 합계를 INSERT한다.
- 동일 트랜잭션에서 S12의 `batch.BatchStepJournal`과 `batch.BatchCheckpoint`를 `Succeeded`로 전환한다.
- S13 이후에는 정산원장 자체를 변경하지 않고 후처리·통계·요약만 수행한다.

레거시 프로시저를 대체하지 않는 제어 단계이므로 원본 프로시저 파라미터나 OUTPUT 파라미터는 없다. 애플리케이션 실행 문맥은 다음 타입을 보장한다.

| 실행 문맥 값 | 바인딩 이름 | 타입 |
|---|---|---|
| 실행 식별자 | `p_runId` | `bigint` |
| 정산 기준일 | `p_businessYmd` | `varchar(8)` |
| 단계 코드 | `p_stepCode` | `nvarchar(10)`, 값 `S12` |
| 동결 시각 | `p_frozenAtUtc` | `datetime2(3)` |

대상 객체는 `batch.BatchStepJournal`, `batch.BatchCheckpoint`, `batch.BatchControlTotal`이다. 첫 실행 시 S12가 자신의 저널과 체크포인트 행을 반드시 INSERT한 후에만 완료 UPDATE를 수행한다. 재시작 시에는 최초 실행에서 S12가 생성한 `Failed`/`Pending` 행만 재개하며, `Succeeded` 체크포인트의 건너뛰기는 단계 외부 오케스트레이터가 처리한다.

#### 트랜잭션 및 오류 계약

- 모든 조회와 DML은 SNAPSHOT 격리 의무를 만족해야 한다.
- 청킹하지 않으며 통제 합계 INSERT와 성공 상태 UPDATE를 하나의 트랜잭션으로 처리한다.
- 단일 트랜잭션이므로 섀도 테이블과 보상 DELETE를 사용하지 않는다.
- `currentStepErrorCode`는 `INT`이고 초기값은 `0`이다.
- 실행할 SQL 직전에 `currentStatementName`과 일반 실패 코드 `-9120`을 설정한다.
- 이 단계에서 사용하는 실패 코드는 승인된 원본 없는 제어 단계 코드 `-9120`뿐이다.
- 실패한 트랜잭션을 롤백한 뒤 `batch.BatchStepJournal.LegacyReturnCode`에 `-9120`을 기록한다. `batch.BatchCheckpoint`는 `Pending`으로 남겨 재시작 대상임을 나타낸다.
- 성공 상태와 통제 합계가 동일 트랜잭션에서 커밋되므로, 커밋 전 장애는 전부 롤백되고 커밋 후 응답 유실은 `Succeeded` 체크포인트로 식별된다.

```pseudocode
currentStatementName = NULL
currentStepErrorCode = 0
startRowsCommitted = false
freezeTx = noTransaction

conn = connectionFactory.open()
// 이 연결에서 수행하는 모든 읽기와 쓰기는 SNAPSHOT 격리 의무를 만족한다.

currentStatementName = "SQL_CONTROL_STATE"
currentStepErrorCode = -9120
controlState = repository.queryRow(
    conn,
    noTransaction,
    SQL_CONTROL_STATE,
    { p_runId: runId, p_stepCode: N"S12" })

IF controlState is absent:
    startTx = conn.beginTransaction()

    currentStatementName = "SQL_S12_JOURNAL_START"
    currentStepErrorCode = -9120
    repository.execute(
        conn,
        startTx,
        SQL_S12_JOURNAL_START,
        {
            p_runId: runId,
            p_stepCode: N"S12",
            p_startedAtUtc: utcNow
        })

    currentStatementName = "SQL_S12_CHECKPOINT_START"
    currentStepErrorCode = -9120
    repository.execute(
        conn,
        startTx,
        SQL_S12_CHECKPOINT_START,
        {
            p_runId: runId,
            p_stepCode: N"S12"
        })

    startTx.commit()
    startRowsCommitted = true

ELSE IF controlState.CheckpointStatus equals Succeeded:
    reject the call because the orchestrator must skip an already succeeded S12

ELSE IF controlState.CheckpointStatus equals Pending
     AND controlState.StepStatus is Failed or Running:
    // 이 행들은 이전 S12 호출이 시작 시 직접 INSERT한 자기 행이다.
    reopenTx = conn.beginTransaction()

    currentStatementName = "SQL_S12_JOURNAL_REOPEN"
    currentStepErrorCode = -9120
    repository.execute(
        conn,
        reopenTx,
        SQL_S12_JOURNAL_REOPEN,
        {
            p_runId: runId,
            p_stepCode: N"S12",
            p_startedAtUtc: utcNow
        })

    reopenTx.commit()
    startRowsCommitted = true

ELSE:
    fail with control-state inconsistency

currentStatementName = "SQL_S12_PREDECESSOR_GATE"
currentStepErrorCode = -9120
incompleteSteps = repository.queryRows(
    conn,
    noTransaction,
    SQL_S12_PREDECESSOR_GATE,
    { p_runId: runId })

IF incompleteSteps is not empty:
    fail because S04 through S11 have not all succeeded

currentStatementName = "SQL_S12_LOCK_OWNERSHIP"
currentStepErrorCode = -9120
lockState = repository.queryRow(
    conn,
    noTransaction,
    SQL_S12_LOCK_OWNERSHIP,
    {
        p_jobName: N"POQSettleBatch21",
        p_businessYmd: businessYmd
    })

IF lockState is absent
   OR lockState.LockStatus is not Held
   OR lockState.OwnerRunId is not runId:
    fail because the active run does not own the batch lock

freezeTx = conn.beginTransaction()

currentStatementName = "SQL_S12_CAPTURE_CONTROL_TOTALS"
currentStepErrorCode = -9120
repository.execute(
    conn,
    freezeTx,
    SQL_S12_CAPTURE_CONTROL_TOTALS,
    {
        p_runId: runId,
        p_stepCode: N"S12",
        p_businessYmd: businessYmd,
        p_frozenAtUtc: utcNow
    })

currentStatementName = "SQL_S12_JOURNAL_SUCCESS"
currentStepErrorCode = -9120
repository.execute(
    conn,
    freezeTx,
    SQL_S12_JOURNAL_SUCCESS,
    {
        p_runId: runId,
        p_stepCode: N"S12",
        p_completedAtUtc: utcNow
    })

currentStatementName = "SQL_S12_CHECKPOINT_SUCCESS"
currentStepErrorCode = -9120
repository.execute(
    conn,
    freezeTx,
    SQL_S12_CHECKPOINT_SUCCESS,
    {
        p_runId: runId,
        p_stepCode: N"S12",
        p_completedAtUtc: utcNow
    })

freezeTx.commit()

ON FAILURE observed by the application:
    rollback freezeTx if open
    rollback startTx or reopenTx if open

    IF startRowsCommitted:
        failureTx = conn.beginTransaction()

        repository.execute(
            conn,
            failureTx,
            SQL_S12_JOURNAL_FAILURE,
            {
                p_runId: runId,
                p_stepCode: N"S12",
                p_legacyReturnCode: currentStepErrorCode,
                p_errorMessage: currentStatementName plus observed error details,
                p_completedAtUtc: utcNow
            })

        failureTx.commit()

    invoke the shared run-failure recording with ResumeFromStepCode N"S12"
    stop the pipeline and invoke S20 failure publication
```

#### 시작 행 생성과 재시작 SQL

`batch.BatchStepJournal`과 `batch.BatchCheckpoint`의 S12 행은 첫 호출의 시작 트랜잭션에서 함께 생성한다. 둘 중 하나라도 실패하면 시작 트랜잭션 전체를 롤백한다.

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

-- SQL_S12_CHECKPOINT_START
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

실패 후 같은 실행을 재개할 때는 S12가 이전 호출에서 직접 생성한 저널 행만 다시 `Running`으로 연다. 체크포인트는 이미 `Pending`이므로 새 행을 중복 INSERT하거나 다른 상태값을 만들지 않는다.

```sql
-- SQL_S12_JOURNAL_REOPEN
UPDATE batch.BatchStepJournal
SET StepStatus = N'Running',
    LegacyReturnCode = NULL,
    StartedAtUtc = @p_startedAtUtc,
    CompletedAtUtc = NULL,
    ErrorMessage = NULL
WHERE RunId = @p_runId
  AND StepCode = @p_stepCode
  AND StepStatus IN (N'Failed', N'Running')
  AND EXISTS
      (
          SELECT 1
          FROM batch.BatchCheckpoint AS C
          WHERE C.RunId = @p_runId
            AND C.StepCode = @p_stepCode
            AND C.CheckpointStatus = N'Pending'
      );
```

#### 선행 단계 및 실행 잠금 확인

선행 완료 검사는 S04~S11을 정확히 대상으로 한다. 결과 행이 하나라도 있으면 애플리케이션이 `-9120` 실패로 처리하며 통제 합계를 생성하지 않는다.

```sql
-- SQL_S12_PREDECESSOR_GATE
WITH RequiredStep AS
(
    SELECT V.StepCode
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
    ) AS V(StepCode)
)
SELECT R.StepCode,
       C.CheckpointStatus
FROM RequiredStep AS R
LEFT JOIN batch.BatchCheckpoint AS C
  ON C.RunId = @p_runId
 AND C.StepCode = R.StepCode
WHERE C.RunId IS NULL
   OR C.CheckpointStatus <> N'Succeeded';

-- SQL_S12_LOCK_OWNERSHIP
SELECT OwnerRunId,
       LockStatus
FROM batch.BatchRunLock
WHERE JobName = @p_jobName
  AND BatchYmd = @p_businessYmd;
```

#### 동결 통제 합계 적재

기준일 일반 원장은 `YMD = @p_businessYmd`, Extra 원장은 `ProcYMD = @p_businessYmd AND ExtraSettleFlag = 1` 범위로 각각 집계한다. 두 범위가 겹칠 수 있으므로 서로 합산하지 않고 별도 `ControlName`으로 저장한다. 모든 원천 조회에서 `NOLOCK`을 제거하고 SNAPSHOT 읽기를 사용한다.

```sql
-- SQL_S12_CAPTURE_CONTROL_TOTALS
WITH BaseLedger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmtValue,
        COALESCE(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS CLTotalValue,
        COALESCE(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS PGTotalValue,
        COALESCE(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS POQIncomeValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
),
ExtraLedger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCountValue,
        COALESCE(SUM(CAST(ExtraTxAmt AS DECIMAL(38,4))), 0) AS ExtraTxAmtValue,
        COALESCE(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS CLTotalValue,
        COALESCE(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS PGTotalValue,
        COALESCE(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS POQIncomeValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE ProcYMD = @p_businessYmd
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
SELECT @p_runId, @p_stepCode, N'BaseRowCount', RowCountValue, @p_frozenAtUtc
FROM BaseLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'BaseTxAmt', TxAmtValue, @p_frozenAtUtc
FROM BaseLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'BaseCLTotal', CLTotalValue, @p_frozenAtUtc
FROM BaseLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'BasePGTotal', PGTotalValue, @p_frozenAtUtc
FROM BaseLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'BasePOQIncome', POQIncomeValue, @p_frozenAtUtc
FROM BaseLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'ExtraRowCount', RowCountValue, @p_frozenAtUtc
FROM ExtraLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'ExtraTxAmt', ExtraTxAmtValue, @p_frozenAtUtc
FROM ExtraLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'ExtraCLTotal', CLTotalValue, @p_frozenAtUtc
FROM ExtraLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'ExtraPGTotal', PGTotalValue, @p_frozenAtUtc
FROM ExtraLedger
UNION ALL
SELECT @p_runId, @p_stepCode, N'ExtraPOQIncome', POQIncomeValue, @p_frozenAtUtc
FROM ExtraLedger;
```

#### 성공 및 실패 상태 기록

통제 합계 INSERT와 아래 성공 UPDATE는 같은 트랜잭션에 포함한다. 성공 시 레거시 반환값이 없으므로 `LegacyReturnCode`는 `NULL`로 유지한다.

```sql
-- SQL_S12_JOURNAL_SUCCESS
UPDATE batch.BatchStepJournal
SET StepStatus = N'Succeeded',
    LegacyReturnCode = NULL,
    CompletedAtUtc = @p_completedAtUtc,
    ErrorMessage = NULL
WHERE RunId = @p_runId
  AND StepCode = @p_stepCode
  AND StepStatus = N'Running';

-- SQL_S12_CHECKPOINT_SUCCESS
UPDATE batch.BatchCheckpoint
SET CheckpointStatus = N'Succeeded',
    CompletedAtUtc = @p_completedAtUtc
WHERE RunId = @p_runId
  AND StepCode = @p_stepCode
  AND CheckpointStatus = N'Pending';
```

실패 시 열린 업무 트랜잭션을 먼저 롤백하고, 별도 제어 트랜잭션에서 S12가 생성한 저널 행을 실패로 갱신한다.

```sql
-- SQL_S12_JOURNAL_FAILURE
UPDATE batch.BatchStepJournal
SET StepStatus = N'Failed',
    LegacyReturnCode = @p_legacyReturnCode,
    CompletedAtUtc = @p_completedAtUtc,
    ErrorMessage = @p_errorMessage
WHERE RunId = @p_runId
  AND StepCode = @p_stepCode
  AND StepStatus = N'Running';
```

#### 완료 불변조건

S12 성공 커밋 후 다음 조건이 모두 성립해야 한다.

- `batch.BatchStepJournal`에 해당 `RunId`, `StepCode = N'S12'`, `StepStatus = N'Succeeded'`인 S12 자기 행이 존재한다.
- `batch.BatchCheckpoint`에 해당 `RunId`, `StepCode = N'S12'`, `CheckpointStatus = N'Succeeded'`인 S12 자기 행이 존재한다.
- `batch.BatchControlTotal`에 S12의 일반 원장 통제 합계 5건과 Extra 원장 통제 합계 5건이 존재한다.
- 통제 합계와 성공 상태는 동일 SNAPSHOT 트랜잭션에서 함께 커밋되었으므로 어느 한쪽만 남는 부분 완료 상태가 없다.
- 실패 시 `LegacyReturnCode`는 정확히 `-9120`이며, `ErrorMessage`에는 `currentStatementName`과 관측된 오류 상세가 함께 기록된다.