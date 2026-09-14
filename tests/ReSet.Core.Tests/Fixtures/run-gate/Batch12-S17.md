### S17 — 통합 정합성 검증

#### 목적과 실행 조건

S16 완료 후 S18 게시 단계 전에 실행하며 병렬 실행하지 않는다. 다음 항목을 하나의 일관된 시점에서 검증한다.

- S01~S16의 `batch.BatchStepJournal`, `batch.BatchCheckpoint` 완료 상태
- `SETTLE_POQ_DB.dbo.TSettleMst` 수수료 합계 산식
- `SETTLE_POQ_DB.dbo.TSettleByTX`, `TPartialCancelByTX`, `TSettleByIN`, `TSettleByOUT`과 원장의 건수·거래금액 합계
- `SETTLE_POQ_DB.dbo.TStatPGCollect` 차변·대변 구성 산식
- 검증 지표의 `batch.BatchControlTotal` 기록

실행 컨텍스트 매핑은 `RunId BIGINT -> p_runId`, `BatchYmd VARCHAR(8) -> p_batchYmd`이다. 재시작·검증 우회 입력은 두지 않는다.

모든 읽기와 쓰기는 SNAPSHOT 격리에서 수행해야 하며 `NOLOCK` 힌트를 사용하지 않는다. 청크 실행은 적용하지 않는다. 업무 트랜잭션 하나로 제어 합계 기록과 S17 성공 저널·체크포인트를 함께 커밋하며, 실패하면 전부 롤백한다. 따라서 shadow 테이블이나 롤백 후 보상 DELETE는 사용하지 않는다.

#### 검증 및 제어 흐름

S17은 레거시 기원이 없는 제어 단계다. 상태 변수는 `0`으로 초기화하고, 검증 불일치 또는 단계 DML 실패에는 일반 실패 코드 `-9170`을 기록한다. 읽기 실패는 `currentStatement`로 조회 문장을 식별하고, 불일치를 발견한 경우에도 상세 지표를 오류 메시지에 포함한다.

```csharp
void ExecuteS17(long runId, string batchYmd)
{
    const string stepCode = "S17";
    int currentStepErrorCode = 0;
    string currentStatement = "";
    var conn = connectionFactory.open();

    // 시작 저널과 Pending 체크포인트도 SNAPSHOT 격리 의무를 따른다.
    var journalTx = conn.beginTransaction();

    try
    {
        currentStatement = "batch.BatchStepJournal S17 시작 등록";
        currentStepErrorCode = -9170;
        repository.execute(conn, journalTx, SQL_JOURNAL_START_INSERT, new {
            p_runId = runId,
            p_stepCode = stepCode
        });

        currentStatement = "batch.BatchCheckpoint S17 Pending 등록";
        currentStepErrorCode = -9170;
        repository.execute(conn, journalTx, SQL_CHECKPOINT_PENDING_INSERT, new {
            p_runId = runId,
            p_stepCode = stepCode
        });

        journalTx.commit();
    }
    catch
    {
        journalTx.rollback();
        throw;
    }

    var workTx = conn.beginTransaction(); // SNAPSHOT 의무

    try
    {
        currentStatement = "S17 통합 검증 지표 조회";
        var metrics = repository.queryRows(
            conn,
            workTx,
            SQL_S17_VALIDATION_METRICS,
            new {
                p_runId = runId,             // BIGINT
                p_batchYmd = batchYmd        // VARCHAR(8)
            });

        // 소량의 고정 검증 지표 기록이며 청크 페이징이 아니다.
        foreach (var metric in metrics)
        {
            foreach (var control in new[] {
                (metric.MetricCode + ".Expected", metric.ExpectedValue),
                (metric.MetricCode + ".Actual", metric.ActualValue),
                (metric.MetricCode + ".Difference", metric.DifferenceValue),
                (metric.MetricCode + ".FailureCount", metric.FailureCount)
            })
            {
                currentStatement =
                    "batch.BatchControlTotal 기록 " + control.Item1;
                currentStepErrorCode = -9170;

                repository.execute(conn, workTx, SQL_S17_INSERT_CONTROL_TOTAL, new {
                    p_runId = runId,
                    p_stepCode = stepCode,
                    p_controlName = control.Item1,
                    p_controlValue = control.Item2
                });
            }
        }

        var failedMetrics = metrics.Where(x => x.FailureCount != 0).ToList();

        if (failedMetrics.Count != 0)
        {
            currentStatement = "S17 통합 정합성 불일치";
            currentStepErrorCode = -9170;

            var details = failedMetrics.Select(x =>
                x.MetricCode
                + ": expected=" + x.ExpectedValue
                + ", actual=" + x.ActualValue
                + ", difference=" + x.DifferenceValue
                + ", failures=" + x.FailureCount);

            raise controlledFailure(string.Join("; ", details));
        }

        currentStatement = "batch.BatchStepJournal S17 성공 갱신";
        currentStepErrorCode = -9170;
        repository.execute(conn, workTx, SQL_JOURNAL_SUCCEEDED, new {
            p_runId = runId,
            p_stepCode = stepCode,
            p_legacyReturnCode = null
        });

        currentStatement = "batch.BatchCheckpoint S17 성공 갱신";
        currentStepErrorCode = -9170;
        repository.execute(conn, workTx, SQL_CHECKPOINT_SUCCEEDED, new {
            p_runId = runId,
            p_stepCode = stepCode
        });

        workTx.commit();
    }
    catch (failure)
    {
        if (workTx is open)
            workTx.rollback();

        int failureCode =
            currentStepErrorCode == 0 ? -9170 : currentStepErrorCode;

        var failureTx = conn.beginTransaction(); // SNAPSHOT 의무
        repository.execute(conn, failureTx, SQL_JOURNAL_FAILED, new {
            p_runId = runId,
            p_stepCode = stepCode,
            p_legacyReturnCode = failureCode,
            p_errorMessage = currentStatement + ": " + failure.message
        });
        failureTx.commit();

        stop pipeline; // S18은 호출하지 않는다.
    }
}
```

#### 통합 검증 SQL

각 비교 대상은 독립 CTE 또는 스칼라 하위 질의로 먼저 집계한다. 두 집계를 `CROSS JOIN`하여 비교하지 않는다.

```sql
-- SQL_S17_VALIDATION_METRICS
;WITH ExpectedSteps AS
(
    SELECT V.StepCode
      FROM
      (
          VALUES
              (N'S01'), (N'S02'), (N'S03'), (N'S04'),
              (N'S05'), (N'S06'), (N'S07'), (N'S08'),
              (N'S09'), (N'S10'), (N'S11'), (N'S12'),
              (N'S13'), (N'S14'), (N'S15'), (N'S16')
      ) AS V(StepCode)
),
LedgerCheck AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS RowCountValue,
        CONVERT
        (
            DECIMAL(38,4),
            SUM
            (
                CASE
                    WHEN ISNULL(CLTOTAL, 0)
                           <> ISNULL(CLCOMM, 0)
                            + ISNULL(CLVT, 0)
                            + ISNULL(CLETC, 0)
                            + ISNULL(CLINTCOMM, 0)
                      OR ISNULL(PGTOTAL, 0)
                           <> ISNULL(PGCOMM, 0)
                            + ISNULL(PGVT, 0)
                            + ISNULL(PGETC, 0)
                            + CASE
                                  WHEN ISNULL(PGINTREALCOMM, 0) = 0
                                      THEN ISNULL(PGINTEXPCOMM, 0)
                                  ELSE ISNULL(PGINTREALCOMM, 0)
                              END
                      OR ISNULL(POQINCOME, 0)
                           <> ISNULL(CLTOTAL, 0) - ISNULL(PGTOTAL, 0)
                    THEN 1
                    ELSE 0
                END
            )
        ) AS MismatchCount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
),
SourceTx AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
),
TargetTx AS
(
    SELECT
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXCNT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_batchYmd
),
SourcePartial AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND USESTATE = 2
),
TargetPartial AS
(
    SELECT
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXCNT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @p_batchYmd
       AND USESTATE = 2
),
SourceIn AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND INSTATE = 1
),
TargetIn AS
(
    SELECT
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(INCNT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @p_batchYmd
),
SourceOut AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd
       AND OUTSTATE IN (2, 9)
),
TargetOut AS
(
    SELECT
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(OUTCNT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxCount,
        COALESCE
        (
            SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))),
            CONVERT(DECIMAL(38,4), 0)
        ) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @p_batchYmd
       AND OUTSTATE IN (2, 9)
),
StatCheck AS
(
    SELECT
        CONVERT(DECIMAL(38,4), COUNT_BIG(*)) AS RowCountValue,
        CONVERT
        (
            DECIMAL(38,4),
            SUM
            (
                CASE
                    WHEN ISNULL(LEFTSUMAMT, 0)
                           <> ISNULL(COLLECTAMT, 0)
                            + ISNULL(PGCOMM, 0)
                            + ISNULL(PGVT, 0)
                      OR ISNULL(RIGHTSUMAMT, 0)
                           <> ISNULL(SETTLEWILLAMT, 0)
                            + ISNULL(AHEADSALESCOMM, 0)
                            + ISNULL(AHEADSALESVT, 0)
                            + ISNULL(AHEADSETTLEAMT, 0)
                    THEN 1
                    ELSE 0
                END
            )
        ) AS MismatchCount
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE INYMD = @p_batchYmd
)
SELECT
    N'PRIOR_CHECKPOINT' AS MetricCode,
    CONVERT(DECIMAL(38,4), 16) AS ExpectedValue,
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM batch.BatchCheckpoint AS C
              JOIN ExpectedSteps AS E
                ON E.StepCode = C.StepCode
             WHERE C.RunId = @p_runId
               AND C.CheckpointStatus = N'Succeeded'
        )
    ) AS ActualValue,
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM batch.BatchCheckpoint AS C
              JOIN ExpectedSteps AS E
                ON E.StepCode = C.StepCode
             WHERE C.RunId = @p_runId
               AND C.CheckpointStatus = N'Succeeded'
        ) - 16
    ) AS DifferenceValue,
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM ExpectedSteps AS E
             WHERE
                 (
                     SELECT COUNT_BIG(*)
                       FROM batch.BatchCheckpoint AS C
                      WHERE C.RunId = @p_runId
                        AND C.StepCode = E.StepCode
                 ) <> 1
                OR
                 (
                     SELECT COUNT_BIG(*)
                       FROM batch.BatchCheckpoint AS C
                      WHERE C.RunId = @p_runId
                        AND C.StepCode = E.StepCode
                        AND C.CheckpointStatus = N'Succeeded'
                 ) <> 1
        )
    ) AS FailureCount

UNION ALL

SELECT
    N'PRIOR_JOURNAL',
    CONVERT(DECIMAL(38,4), 16),
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM batch.BatchStepJournal AS J
              JOIN ExpectedSteps AS E
                ON E.StepCode = J.StepCode
             WHERE J.RunId = @p_runId
               AND J.StepStatus = N'Succeeded'
        )
    ),
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM batch.BatchStepJournal AS J
              JOIN ExpectedSteps AS E
                ON E.StepCode = J.StepCode
             WHERE J.RunId = @p_runId
               AND J.StepStatus = N'Succeeded'
        ) - 16
    ),
    CONVERT
    (
        DECIMAL(38,4),
        (
            SELECT COUNT_BIG(*)
              FROM ExpectedSteps AS E
             WHERE
                 (
                     SELECT COUNT_BIG(*)
                       FROM batch.BatchStepJournal AS J
                      WHERE J.RunId = @p_runId
                        AND J.StepCode = E.StepCode
                 ) <> 1
                OR
                 (
                     SELECT COUNT_BIG(*)
                       FROM batch.BatchStepJournal AS J
                      WHERE J.RunId = @p_runId
                        AND J.StepCode = E.StepCode
                        AND J.StepStatus = N'Succeeded'
                 ) <> 1
        )
    )

UNION ALL

SELECT
    N'LEDGER_ROW_COUNT',
    (SELECT RowCountValue FROM LedgerCheck),
    (SELECT RowCountValue FROM LedgerCheck),
    CONVERT(DECIMAL(38,4), 0),
    CONVERT(DECIMAL(38,4), 0)

UNION ALL

SELECT
    N'LEDGER_ARITHMETIC',
    CONVERT(DECIMAL(38,4), 0),
    (SELECT MismatchCount FROM LedgerCheck),
    (SELECT MismatchCount FROM LedgerCheck),
    (SELECT MismatchCount FROM LedgerCheck)

UNION ALL

SELECT
    N'SUMMARY_TX_COUNT',
    (SELECT TxCount FROM SourceTx),
    (SELECT TxCount FROM TargetTx),
    (SELECT TxCount FROM TargetTx) - (SELECT TxCount FROM SourceTx),
    CASE
        WHEN (SELECT TxCount FROM SourceTx) = (SELECT TxCount FROM TargetTx)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_TX_AMOUNT',
    (SELECT TxAmount FROM SourceTx),
    (SELECT TxAmount FROM TargetTx),
    (SELECT TxAmount FROM TargetTx) - (SELECT TxAmount FROM SourceTx),
    CASE
        WHEN (SELECT TxAmount FROM SourceTx) = (SELECT TxAmount FROM TargetTx)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_PARTIAL_COUNT',
    (SELECT TxCount FROM SourcePartial),
    (SELECT TxCount FROM TargetPartial),
    (SELECT TxCount FROM TargetPartial) - (SELECT TxCount FROM SourcePartial),
    CASE
        WHEN (SELECT TxCount FROM SourcePartial) = (SELECT TxCount FROM TargetPartial)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_PARTIAL_AMOUNT',
    (SELECT TxAmount FROM SourcePartial),
    (SELECT TxAmount FROM TargetPartial),
    (SELECT TxAmount FROM TargetPartial) - (SELECT TxAmount FROM SourcePartial),
    CASE
        WHEN (SELECT TxAmount FROM SourcePartial) = (SELECT TxAmount FROM TargetPartial)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_IN_COUNT',
    (SELECT TxCount FROM SourceIn),
    (SELECT TxCount FROM TargetIn),
    (SELECT TxCount FROM TargetIn) - (SELECT TxCount FROM SourceIn),
    CASE
        WHEN (SELECT TxCount FROM SourceIn) = (SELECT TxCount FROM TargetIn)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_IN_AMOUNT',
    (SELECT TxAmount FROM SourceIn),
    (SELECT TxAmount FROM TargetIn),
    (SELECT TxAmount FROM TargetIn) - (SELECT TxAmount FROM SourceIn),
    CASE
        WHEN (SELECT TxAmount FROM SourceIn) = (SELECT TxAmount FROM TargetIn)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_OUT_COUNT',
    (SELECT TxCount FROM SourceOut),
    (SELECT TxCount FROM TargetOut),
    (SELECT TxCount FROM TargetOut) - (SELECT TxCount FROM SourceOut),
    CASE
        WHEN (SELECT TxCount FROM SourceOut) = (SELECT TxCount FROM TargetOut)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'SUMMARY_OUT_AMOUNT',
    (SELECT TxAmount FROM SourceOut),
    (SELECT TxAmount FROM TargetOut),
    (SELECT TxAmount FROM TargetOut) - (SELECT TxAmount FROM SourceOut),
    CASE
        WHEN (SELECT TxAmount FROM SourceOut) = (SELECT TxAmount FROM TargetOut)
            THEN CONVERT(DECIMAL(38,4), 0)
        ELSE CONVERT(DECIMAL(38,4), 1)
    END

UNION ALL

SELECT
    N'PGCOLLECT_ROW_COUNT',
    (SELECT RowCountValue FROM StatCheck),
    (SELECT RowCountValue FROM StatCheck),
    CONVERT(DECIMAL(38,4), 0),
    CONVERT(DECIMAL(38,4), 0)

UNION ALL

SELECT
    N'PGCOLLECT_BALANCE',
    CONVERT(DECIMAL(38,4), 0),
    (SELECT MismatchCount FROM StatCheck),
    (SELECT MismatchCount FROM StatCheck),
    (SELECT MismatchCount FROM StatCheck);
```

검증 결과는 값 연결 없이 모두 바인딩하여 기록한다.

```sql
-- SQL_S17_INSERT_CONTROL_TOTAL
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
VALUES
(
    @p_runId,
    @p_stepCode,
    @p_controlName,
    @p_controlValue,
    SYSUTCDATETIME()
);
```

#### 성공·실패 및 재시작 규칙

- 모든 `FailureCount`가 0일 때만 `batch.BatchStepJournal.StepStatus`와 `batch.BatchCheckpoint.CheckpointStatus`를 `Succeeded`로 갱신한다.
- 성공 시 `batch.BatchStepJournal.LegacyReturnCode`는 레거시 성공 코드를 발명하지 않고 `NULL`로 둔다.
- 불일치, 조회 실패, `batch.BatchControlTotal` 기록 실패, 성공 상태 갱신 실패는 모두 `-9170`으로 실패 처리하되 `ErrorMessage`에 정확한 문장명 또는 실패 지표를 기록한다.
- 실패 시 업무 트랜잭션이 롤백되므로 해당 실행에서 기록하던 `batch.BatchControlTotal`도 남지 않는다. `batch.BatchCheckpoint`는 `Pending`으로 유지되어 다음 실행에서 S17 전체 검증이 다시 수행된다.
- S17 실패 후 S18은 실행하지 않으며, 게시 상태나 실행 잠금을 성공 상태로 변경하지 않는다.