### S11 — 정산 사실 동결 배리어

S03~S10의 완료 상태를 확인한 뒤 `SETTLE_POQ_DB.dbo.TSettleMst`의 기준일 정산 사실에 대한 통제 합계를 `batch.BatchControlTotal`에 고정한다. 이후 단계는 이 값을 S18 정합성 검증의 기준선으로 사용한다.

- 애플리케이션 인터페이스: `runId bigint -> p_runId`, `batchYmd varchar(8) -> p_ymd`
- 단계 코드는 입력값이 아니라 상수 `N'S11' -> p_stepCode`로 사용한다.
- 호출된 경우 재시작 우회 없이 S03~S10 선행 단계 검증을 항상 수행한다.
- 시작 등록과 동결 처리 트랜잭션은 모두 **SNAPSHOT 격리 수준**에서 실행해야 한다.
- `Chunkable = False`이며 통제 합계 등록과 성공 상태 전환은 하나의 원자적 트랜잭션으로 처리한다.
- `NOLOCK` 힌트는 사용하지 않는다.
- 단일 트랜잭션 롤백으로 `batch.BatchControlTotal`, `batch.BatchCheckpoint`, `batch.BatchStepJournal`의 부분 완료를 방지하며 Shadow Table이나 보상 DELETE를 사용하지 않는다.

#### 오류 코드 배정

S11은 레거시 프로시저가 없는 제어 단계이므로 예약 블록 `-9110..-9119`를 사용한다.

| 코드 | 실패 지점 |
|---:|---|
| `-9110` | 일반 실패 또는 S03~S10 동결 선행조건 불충족 |
| `-9111` | `batch.BatchStepJournal` 시작 행 등록 실패 |
| `-9112` | `batch.BatchCheckpoint` Pending 행 등록 실패 |
| `-9113` | `batch.BatchControlTotal` 동결 통제 합계 등록 실패 |
| `-9114` | `batch.BatchStepJournal` 성공 상태 전환 실패 |
| `-9115` | `batch.BatchCheckpoint` 성공 상태 전환 실패 |
| `-9116` | 실패 저널 기록 자체의 실패 |

```pseudocode
// S11에는 레거시 출력 파라미터가 없다.
// 예약 오류 코드는 애플리케이션의 단계 로컬 정수 상태로 관리한다.
currentStepErrorCode = 0
currentStatementName = null

conn = connectionFactory.open()
ensure S11 runs under SNAPSHOT isolation

// 시작 제어 행은 함께 등록하여 둘 중 하나만 남지 않게 한다.
startTx = conn.beginTransaction()

TRY:
    currentStepErrorCode = -9111
    currentStatementName = "SQL_STEP_JOURNAL_START"
    conn.execute(
        SQL_STEP_JOURNAL_START,
        { p_runId: runId, p_stepCode: "S11" },
        startTx
    )

    currentStepErrorCode = -9112
    currentStatementName = "SQL_STEP_CHECKPOINT_START"
    conn.execute(
        SQL_STEP_CHECKPOINT_START,
        { p_runId: runId, p_stepCode: "S11" },
        startTx
    )

    startTx.commit()

CATCH startError:
    startTx.rollbackIfOpen()
    report step failure to the orchestrator with:
        LegacyReturnCode = currentStepErrorCode
        ErrorMessage = currentStatementName plus observed error text
    stop the dependent pipeline

// 통제 합계와 단계 완료 상태를 하나의 동결 트랜잭션에서 확정한다.
freezeTx = conn.beginTransaction()

TRY:
    currentStepErrorCode = -9110
    currentStatementName = "SQL_BARRIER_PREREQUISITES"

    prerequisiteRows = conn.queryRows(
        SQL_BARRIER_PREREQUISITES,
        { p_runId: runId },
        freezeTx
    )

    // SQL이 아니라 애플리케이션이 결과를 판정한다.
    // S03~S10 각각에 대해 Checkpoint와 Journal 행이 정확히 하나이며
    // 양쪽 상태가 모두 Succeeded여야 한다.
    IF prerequisiteRows do not contain exactly one successful row for every step S03 through S10:
        throw barrier failure containing every missing, duplicate, or non-succeeded step

    currentStepErrorCode = -9113
    currentStatementName = "SQL_INSERT_CONTROL_TOTALS"
    insertedControlCount = conn.execute(
        SQL_INSERT_CONTROL_TOTALS,
        { p_runId: runId, p_ymd: batchYmd, p_stepCode: "S11" },
        freezeTx
    )
    IF insertedControlCount is not 6:
        throw control-total cardinality failure

    currentStepErrorCode = -9114
    currentStatementName = "SQL_STEP_JOURNAL_SUCCEEDED"
    updatedJournalCount = conn.execute(
        SQL_STEP_JOURNAL_SUCCEEDED,
        {
            p_runId: runId,
            p_stepCode: "S11",
            p_legacyReturnCode: null
        },
        freezeTx
    )
    IF updatedJournalCount is not 1:
        throw journal state transition failure

    currentStepErrorCode = -9115
    currentStatementName = "SQL_STEP_CHECKPOINT_SUCCEEDED"
    updatedCheckpointCount = conn.execute(
        SQL_STEP_CHECKPOINT_SUCCEEDED,
        { p_runId: runId, p_stepCode: "S11" },
        freezeTx
    )
    IF updatedCheckpointCount is not 1:
        throw checkpoint state transition failure

    freezeTx.commit()

CATCH freezeError:
    failedStatementCode = currentStepErrorCode
    failedStatementName = currentStatementName
    freezeTx.rollbackIfOpen()

    failureJournalTx = conn.beginTransaction()
    TRY:
        currentStepErrorCode = -9116
        currentStatementName = "SQL_STEP_JOURNAL_FAILED"
        conn.execute(
            SQL_STEP_JOURNAL_FAILED,
            {
                p_runId: runId,
                p_stepCode: "S11",
                p_legacyReturnCode: failedStatementCode,
                p_errorMessage: failedStatementName plus observed error text
            },
            failureJournalTx
        )
        failureJournalTx.commit()
    CATCH journalError:
        failureJournalTx.rollbackIfOpen()
        report -9116 and the SQL_STEP_JOURNAL_FAILED error to the orchestrator

    // batch.BatchCheckpoint는 Pending으로 유지한다.
    stop the dependent pipeline
```

#### 애플리케이션 전송 SQL

이 단계에는 레거시 DML 범위 문장이 없으므로 레거시 문장 앵커를 새로 만들지 않는다.

```sql
-- SQL_STEP_JOURNAL_START
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

-- SQL_STEP_CHECKPOINT_START
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

-- SQL_BARRIER_PREREQUISITES
WITH RequiredStep AS
(
    SELECT StepCode
      FROM
      (
          VALUES
              (N'S03'), (N'S04'), (N'S05'), (N'S06'),
              (N'S07'), (N'S08'), (N'S09'), (N'S10')
      ) AS R(StepCode)
),
CheckpointState AS
(
    SELECT StepCode,
           COUNT_BIG(*) AS TotalRows,
           SUM(CASE WHEN CheckpointStatus = N'Succeeded' THEN 1 ELSE 0 END) AS SucceededRows
      FROM batch.BatchCheckpoint
     WHERE RunId = @p_runId
       AND StepCode IN
           (N'S03', N'S04', N'S05', N'S06', N'S07', N'S08', N'S09', N'S10')
     GROUP BY StepCode
),
JournalState AS
(
    SELECT StepCode,
           COUNT_BIG(*) AS TotalRows,
           SUM(CASE WHEN StepStatus = N'Succeeded' THEN 1 ELSE 0 END) AS SucceededRows
      FROM batch.BatchStepJournal
     WHERE RunId = @p_runId
       AND StepCode IN
           (N'S03', N'S04', N'S05', N'S06', N'S07', N'S08', N'S09', N'S10')
     GROUP BY StepCode
)
SELECT R.StepCode,
       ISNULL(C.TotalRows, 0) AS CheckpointRows,
       ISNULL(C.SucceededRows, 0) AS SucceededCheckpointRows,
       ISNULL(J.TotalRows, 0) AS JournalRows,
       ISNULL(J.SucceededRows, 0) AS SucceededJournalRows
  FROM RequiredStep AS R
  LEFT JOIN CheckpointState AS C
    ON C.StepCode = R.StepCode
  LEFT JOIN JournalState AS J
    ON J.StepCode = R.StepCode
 ORDER BY R.StepCode;

-- SQL_INSERT_CONTROL_TOTALS
WITH FactTotal AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS FactRowCount,
        COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), CAST(0 AS DECIMAL(38,4))) AS TxAmt,
        COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), CAST(0 AS DECIMAL(38,4))) AS CLTotal,
        COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), CAST(0 AS DECIMAL(38,4))) AS PGTotal,
        COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), CAST(0 AS DECIMAL(38,4))) AS POQIncome,
        COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), CAST(0 AS DECIMAL(38,4))) AS ExtraTxAmt
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
SELECT @p_runId, @p_stepCode, N'SettleFactRowCount', FactRowCount, SYSUTCDATETIME()
  FROM FactTotal
UNION ALL
SELECT @p_runId, @p_stepCode, N'SettleFactTxAmt', TxAmt, SYSUTCDATETIME()
  FROM FactTotal
UNION ALL
SELECT @p_runId, @p_stepCode, N'SettleFactCLTotal', CLTotal, SYSUTCDATETIME()
  FROM FactTotal
UNION ALL
SELECT @p_runId, @p_stepCode, N'SettleFactPGTotal', PGTotal, SYSUTCDATETIME()
  FROM FactTotal
UNION ALL
SELECT @p_runId, @p_stepCode, N'SettleFactPOQIncome', POQIncome, SYSUTCDATETIME()
  FROM FactTotal
UNION ALL
SELECT @p_runId, @p_stepCode, N'SettleFactExtraTxAmt', ExtraTxAmt, SYSUTCDATETIME()
  FROM FactTotal;

-- SQL_STEP_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';

-- SQL_STEP_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND CheckpointStatus = N'Pending';

-- SQL_STEP_JOURNAL_FAILED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';
```

#### 완료 조건 및 재시작

- S03~S10 각각의 `batch.BatchCheckpoint.CheckpointStatus`와 `batch.BatchStepJournal.StepStatus`가 정확히 한 건씩 `Succeeded`여야 한다.
- `batch.BatchControlTotal`에는 S11 기준 통제 합계 6건이 등록되어야 한다.
- 성공 시 S11의 `LegacyReturnCode`는 `NULL`이며 `batch.BatchCheckpoint`는 `Succeeded`가 된다.
- 선행 단계 불충족이나 동결 트랜잭션 실패 시 통제 합계와 성공 상태 전환은 모두 롤백되고, S11 체크포인트는 `Pending`으로 남는다.
- 재시작 시 S11 체크포인트가 이미 `Succeeded`이면 오케스트레이터가 단계를 호출하지 않는다. 호출된 경우에는 선행 단계 검증과 통제 합계 생성을 생략하지 않는다.