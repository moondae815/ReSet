### S18 — 통합 정합성 검증

#### 목적과 실행 위치

S02부터 S17까지의 실행 상태와 정산 원장·요약·통계 간 통합 정합성을 검증하고 검증 기준값을 `batch.BatchControlTotal`에 저장한다. S17 성공 후, S19 완료 게시 전에 순차 실행하며 다른 단계와 병렬 실행하지 않는다.

승인 목록의 `batch.BatchStep`은 고정 제어 테이블 계약의 단계 실행 이력을 뜻하는 논리 표기이다. 실제 SQL은 존재하지 않는 `batch.BatchStep`을 참조하지 않고 고정 물리 테이블인 `batch.BatchStepJournal`을 사용한다. 이 단계가 직접 사용하거나 공통 실행 구성으로 갱신하는 대상은 다음과 같다.

- `batch.BatchRun`: 실행 ID, 작업명, 기준일 및 실행 상태 검증
- `batch.BatchStep` 논리 대상의 물리 테이블 `batch.BatchStepJournal`: 선행 단계 성공 여부 및 S18 실행 결과 기록
- `batch.BatchCheckpoint`: S02～S17 체크포인트 성공 여부 검증 및 S18 체크포인트 기록
- `batch.BatchControlTotal`: 검증 기준값 재생성

#### 인터페이스와 오류 코드

레거시 프로시저를 대체하지 않는 제어 단계이므로 레거시 입력·출력 파라미터는 없다. C# 실행 컨텍스트에서 S02가 발급한 `RunId`와 배치 기준일을 사용하며, 재시작·건너뛰기·검증 우회 입력은 추가하지 않는다.

| 항목 | 매핑 |
|---|---|
| 실행 ID | `context.RunId -> p_runId` |
| 기준일 | `context.BatchYmd -> p_ymd` |
| 작업명 | 상수 `POQSettleBatch15 -> p_jobName` |
| 단계 코드 | 상수 `S18 -> p_stepCode` |
| 일반 실패 코드 | `-9180` |

단계 로컬 실패 지점 변수는 `INT` 값 `0`으로 초기화한다. 실행 컨텍스트 확인, 선행 단계 확인, 통제 합계 재생성 또는 정합성 비교에서 실패할 수 있는 작업 직전에 `-9180`을 대입한다. 성공 시 `LegacyReturnCode`는 `NULL`이며, 별도의 성공 오류 코드를 발명하지 않는다.

#### 트랜잭션 및 재시작 정책

- 단계 전용 연결은 반드시 SNAPSHOT 격리 의무를 충족해야 한다.
- 모든 검증 조회와 `batch.BatchControlTotal` 재생성은 하나의 트랜잭션에서 수행한다.
- 검증 불일치도 애플리케이션 실패로 처리하여 트랜잭션을 롤백한다.
- 청크 커밋, shadow table 및 롤백 후 보상 DELETE를 사용하지 않는다.
- 재시작 시 커밋과 체크포인트 기록 사이의 장애를 허용하기 위해, 같은 `RunId`와 `S18`의 기존 통제 합계를 트랜잭션 안에서 삭제한 후 재삽입한다.
- 모든 조회에서 `NOLOCK`을 제거하며 SNAPSHOT 읽기 일관성을 유지한다.

```pseudocode
executeS18(context):
    currentStepErrorCode = 0
    activeStatementId = null
    conn = connectionFactory.open("POQSettleBatch15Validation")

    TRY:
        requireSnapshotIsolation(conn)
        tx = conn.beginTransaction()

        currentStepErrorCode = -9180
        activeStatementId = "SQL_VALIDATE_RUN"
        run = conn.querySingle(SQL_VALIDATE_RUN, {
            p_runId: context.RunId,
            p_jobName: "POQSettleBatch15",
            p_ymd: context.BatchYmd
        }, tx)

        IF run is missing:
            failValidation("S18_RUN_CONTEXT", "Running 또는 Restarting 실행 행이 없음")

        currentStepErrorCode = -9180
        activeStatementId = "SQL_VALIDATE_PREDECESSORS"
        incompleteSteps = conn.query(SQL_VALIDATE_PREDECESSORS, {
            p_runId: context.RunId
        }, tx)

        IF incompleteSteps is not empty:
            failValidation(
                "S18_PREDECESSOR_INCOMPLETE",
                joinStepCodes(incompleteSteps)
            )

        currentStepErrorCode = -9180
        activeStatementId = "SQL_DELETE_CONTROL_TOTALS"
        conn.execute(SQL_DELETE_CONTROL_TOTALS, {
            p_runId: context.RunId,
            p_stepCode: "S18"
        }, tx)

        currentStepErrorCode = -9180
        activeStatementId = "SQL_CAPTURE_CONTROL_TOTALS"
        conn.execute(SQL_CAPTURE_CONTROL_TOTALS, {
            p_runId: context.RunId,
            p_stepCode: "S18",
            p_ymd: context.BatchYmd
        }, tx)

        currentStepErrorCode = -9180
        activeStatementId = "SQL_VALIDATE_SUMMARIES"
        summaryIssues = conn.query(SQL_VALIDATE_SUMMARIES, {
            p_ymd: context.BatchYmd
        }, tx)

        IF summaryIssues is not empty:
            failValidation(
                "S18_SUMMARY_MISMATCH",
                formatValidationIssues(summaryIssues)
            )

        currentStepErrorCode = -9180
        activeStatementId = "SQL_VALIDATE_STAT_FORMULAS"
        statisticIssues = conn.query(SQL_VALIDATE_STAT_FORMULAS, {
            p_ymd: context.BatchYmd
        }, tx)

        IF statisticIssues is not empty:
            failValidation(
                "S18_STAT_FORMULA_MISMATCH",
                formatValidationIssues(statisticIssues)
            )

        tx.commit()

        // 공통 실행 구성이 S18 저널과 체크포인트를 Succeeded로 기록한다.
        // LegacyReturnCode는 레거시 성공 코드가 없으므로 NULL이다.

    ON FAILURE error:
        rollbackIfOpen(tx)

        // 공통 실패 기록에 아래 값을 전달한다.
        // LegacyReturnCode = currentStepErrorCode
        // ErrorMessage = activeStatementId와 검증 이슈 또는 DB 오류를 함께 기록
        propagate failure
```

#### 실행 컨텍스트 및 선행 단계 검증 SQL

S01은 RunId 생성 전에 실행되는 사전 검증 단계이므로 완료 게이트에 포함하지 않는다. S02부터 S17까지는 체크포인트와 적어도 한 번의 성공 저널이 모두 존재해야 한다.

```sql
-- SQL_VALIDATE_RUN
SELECT RunId,
       JobName,
       BatchYmd,
       RunStatus
FROM batch.BatchRun
WHERE RunId = @p_runId
  AND JobName = @p_jobName
  AND BatchYmd = @p_ymd
  AND RunStatus IN (N'Running', N'Restarting');
```

```sql
-- SQL_VALIDATE_PREDECESSORS
WITH RequiredSteps AS
(
    SELECT StepCode
    FROM
    (
        VALUES
            (N'S02'), (N'S03'), (N'S04'), (N'S05'),
            (N'S06'), (N'S07'), (N'S08'), (N'S09'),
            (N'S10'), (N'S11'), (N'S12'), (N'S13'),
            (N'S14'), (N'S15'), (N'S16'), (N'S17')
    ) AS V(StepCode)
),
CheckpointState AS
(
    SELECT StepCode,
           MAX(CASE WHEN CheckpointStatus = N'Succeeded' THEN 1 ELSE 0 END) AS HasSucceeded
    FROM batch.BatchCheckpoint
    WHERE RunId = @p_runId
    GROUP BY StepCode
),
JournalState AS
(
    SELECT StepCode,
           MAX(CASE WHEN StepStatus = N'Succeeded' THEN 1 ELSE 0 END) AS HasSucceeded
    FROM batch.BatchStepJournal
    WHERE RunId = @p_runId
    GROUP BY StepCode
)
SELECT R.StepCode
FROM RequiredSteps AS R
LEFT JOIN CheckpointState AS C
  ON C.StepCode = R.StepCode
LEFT JOIN JournalState AS J
  ON J.StepCode = R.StepCode
WHERE ISNULL(C.HasSucceeded, 0) <> 1
   OR ISNULL(J.HasSucceeded, 0) <> 1
ORDER BY R.StepCode;
```

#### 통제 합계 재생성 SQL

통제 합계는 각 원천을 독립적으로 집계한다. 서로 다른 집계 결과를 `CROSS JOIN`으로 결합하지 않는다.

```sql
-- SQL_DELETE_CONTROL_TOTALS
DELETE FROM batch.BatchControlTotal
WHERE RunId = @p_runId
  AND StepCode = @p_stepCode;
```

```sql
-- SQL_CAPTURE_CONTROL_TOTALS
WITH ControlRows AS
(
    SELECT N'Predecessor.SucceededCount' AS ControlName,
           CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS ControlValue
    FROM batch.BatchCheckpoint
    WHERE RunId = @p_runId
      AND StepCode IN
      (
          N'S02', N'S03', N'S04', N'S05',
          N'S06', N'S07', N'S08', N'S09',
          N'S10', N'S11', N'S12', N'S13',
          N'S14', N'S15', N'S16', N'S17'
      )
      AND CheckpointStatus = N'Succeeded'

    UNION ALL
    SELECT N'Ledger.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'Ledger.TxAmt',
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'Ledger.CLTotal',
           CAST(ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'Ledger.PGTotal',
           CAST(ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'Ledger.POQIncome',
           CAST(ISNULL(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SettleMiss.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SettleMiss.CLSettleAmt',
           CAST(ISNULL(SUM(CAST(CLSettleAmt AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'StatPGCollect.RowCount',
           CAST(COUNT_BIG(*) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_ymd

    UNION ALL
    SELECT N'StatPGCollect.LeftSumAmt',
           CAST(ISNULL(SUM(CAST(LEFTSUMAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_ymd

    UNION ALL
    SELECT N'StatPGCollect.RightSumAmt',
           CAST(ISNULL(SUM(CAST(RIGHTSUMAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_ymd

    UNION ALL
    SELECT N'SummaryTx.RowCount',
           CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SummaryTx.TxAmt',
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SummaryPartial.RowCount',
           CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SummaryIn.RowCount',
           CAST(ISNULL(SUM(CAST(INCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_ymd

    UNION ALL
    SELECT N'SummaryOut.RowCount',
           CAST(ISNULL(SUM(CAST(OUTCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2, 9)

    UNION ALL
    SELECT N'SummaryOut.ForeignSettleAmt',
           CAST(ISNULL(SUM(CAST(ForeignSettleAmt AS DECIMAL(38,4))), 0) AS DECIMAL(38,4))
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2, 9)
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
       @p_stepCode,
       ControlName,
       ControlValue,
       SYSUTCDATETIME()
FROM ControlRows;
```

#### 원장과 요약 테이블 비교 SQL

각 원천과 대상 집계는 별도 CTE에서 단일 스칼라로 계산한 뒤 비교한다. 결과 행이 한 건이라도 반환되면 애플리케이션은 검증 실패로 처리한다.

```sql
-- SQL_VALIDATE_SUMMARIES
WITH LedgerTotals AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt,
           CAST(ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS CLTotal,
           CAST(ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS PGTotal,
           CAST(ISNULL(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS POQIncome
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
),
TxSummaryTotals AS
(
    SELECT CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt,
           CAST(ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS CLTotal,
           CAST(ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS PGTotal,
           CAST(ISNULL(SUM(CAST(POQINCOME AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS POQIncome
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_ymd
),
PartialLedgerTotals AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND USESTATE = 2
),
PartialSummaryTotals AS
(
    SELECT CAST(ISNULL(SUM(CAST(TXCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_ymd
      AND USESTATE = 2
),
InLedgerTotals AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND INSTATE = 1
),
InSummaryTotals AS
(
    SELECT CAST(ISNULL(SUM(CAST(INCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_ymd
),
OutLedgerTotals AS
(
    SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt,
           CAST(ISNULL(SUM(CAST(ForeignSettleAmt AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2, 9)
),
OutSummaryTotals AS
(
    SELECT CAST(ISNULL(SUM(CAST(OUTCNT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS RowCount,
           CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS TxAmt,
           CAST(ISNULL(SUM(CAST(ForeignSettleAmt AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_ymd
      AND OUTSTATE IN (2, 9)
)
SELECT N'TX_ROW_COUNT' AS IssueCode,
       (SELECT RowCount FROM LedgerTotals) AS ExpectedValue,
       (SELECT RowCount FROM TxSummaryTotals) AS ActualValue
WHERE (SELECT RowCount FROM LedgerTotals)
   <> (SELECT RowCount FROM TxSummaryTotals)

UNION ALL
SELECT N'TX_AMOUNT',
       (SELECT TxAmt FROM LedgerTotals),
       (SELECT TxAmt FROM TxSummaryTotals)
WHERE (SELECT TxAmt FROM LedgerTotals)
   <> (SELECT TxAmt FROM TxSummaryTotals)

UNION ALL
SELECT N'TX_CLTOTAL',
       (SELECT CLTotal FROM LedgerTotals),
       (SELECT CLTotal FROM TxSummaryTotals)
WHERE (SELECT CLTotal FROM LedgerTotals)
   <> (SELECT CLTotal FROM TxSummaryTotals)

UNION ALL
SELECT N'TX_PGTOTAL',
       (SELECT PGTotal FROM LedgerTotals),
       (SELECT PGTotal FROM TxSummaryTotals)
WHERE (SELECT PGTotal FROM LedgerTotals)
   <> (SELECT PGTotal FROM TxSummaryTotals)

UNION ALL
SELECT N'TX_POQINCOME',
       (SELECT POQIncome FROM LedgerTotals),
       (SELECT POQIncome FROM TxSummaryTotals)
WHERE (SELECT POQIncome FROM LedgerTotals)
   <> (SELECT POQIncome FROM TxSummaryTotals)

UNION ALL
SELECT N'PARTIAL_ROW_COUNT',
       (SELECT RowCount FROM PartialLedgerTotals),
       (SELECT RowCount FROM PartialSummaryTotals)
WHERE (SELECT RowCount FROM PartialLedgerTotals)
   <> (SELECT RowCount FROM PartialSummaryTotals)

UNION ALL
SELECT N'PARTIAL_AMOUNT',
       (SELECT TxAmt FROM PartialLedgerTotals),
       (SELECT TxAmt FROM PartialSummaryTotals)
WHERE (SELECT TxAmt FROM PartialLedgerTotals)
   <> (SELECT TxAmt FROM PartialSummaryTotals)

UNION ALL
SELECT N'IN_ROW_COUNT',
       (SELECT RowCount FROM InLedgerTotals),
       (SELECT RowCount FROM InSummaryTotals)
WHERE (SELECT RowCount FROM InLedgerTotals)
   <> (SELECT RowCount FROM InSummaryTotals)

UNION ALL
SELECT N'IN_AMOUNT',
       (SELECT TxAmt FROM InLedgerTotals),
       (SELECT TxAmt FROM InSummaryTotals)
WHERE (SELECT TxAmt FROM InLedgerTotals)
   <> (SELECT TxAmt FROM InSummaryTotals)

UNION ALL
SELECT N'OUT_ROW_COUNT',
       (SELECT RowCount FROM OutLedgerTotals),
       (SELECT RowCount FROM OutSummaryTotals)
WHERE (SELECT RowCount FROM OutLedgerTotals)
   <> (SELECT RowCount FROM OutSummaryTotals)

UNION ALL
SELECT N'OUT_AMOUNT',
       (SELECT TxAmt FROM OutLedgerTotals),
       (SELECT TxAmt FROM OutSummaryTotals)
WHERE (SELECT TxAmt FROM OutLedgerTotals)
   <> (SELECT TxAmt FROM OutSummaryTotals)

UNION ALL
SELECT N'OUT_FOREIGN_AMOUNT',
       (SELECT ForeignSettleAmt FROM OutLedgerTotals),
       (SELECT ForeignSettleAmt FROM OutSummaryTotals)
WHERE (SELECT ForeignSettleAmt FROM OutLedgerTotals)
   <> (SELECT ForeignSettleAmt FROM OutSummaryTotals);
```

#### PG 수납 통계 산식 검증 SQL

`TStatPGCollect`의 차변계와 대변계가 각 구성 컬럼의 합계와 일치하는지 행 단위로 검증한다. 반환 행은 최대 100건으로 제한하되, 한 건이라도 존재하면 전체 단계는 실패한다.

```sql
-- SQL_VALIDATE_STAT_FORMULAS
SELECT TOP (100)
       N'STAT_PG_COLLECT_FORMULA' AS IssueCode,
       INYMD,
       CLIENTID,
       PGNAME,
       MALLID,
       CAST(
           ISNULL(COLLECTAMT, 0)
         + ISNULL(PGCOMM, 0)
         + ISNULL(PGVT, 0)
           AS DECIMAL(38,4)
       ) AS ExpectedLeftValue,
       CAST(ISNULL(LEFTSUMAMT, 0) AS DECIMAL(38,4)) AS ActualLeftValue,
       CAST(
           ISNULL(SETTLEWILLAMT, 0)
         + ISNULL(AHEADSALESCOMM, 0)
         + ISNULL(AHEADSALESVT, 0)
         + ISNULL(AHEADSETTLEAMT, 0)
           AS DECIMAL(38,4)
       ) AS ExpectedRightValue,
       CAST(ISNULL(RIGHTSUMAMT, 0) AS DECIMAL(38,4)) AS ActualRightValue
FROM SETTLE_POQ_DB.dbo.TStatPGCollect
WHERE INYMD = @p_ymd
  AND
  (
      ISNULL(LEFTSUMAMT, 0)
      <>
      ISNULL(COLLECTAMT, 0)
    + ISNULL(PGCOMM, 0)
    + ISNULL(PGVT, 0)

      OR

      ISNULL(RIGHTSUMAMT, 0)
      <>
      ISNULL(SETTLEWILLAMT, 0)
    + ISNULL(AHEADSALESCOMM, 0)
    + ISNULL(AHEADSALESVT, 0)
    + ISNULL(AHEADSETTLEAMT, 0)
  )
ORDER BY CLIENTID, PGNAME, MALLID;
```

검증 SQL 실행 오류 또는 반환된 정합성 이슈는 모두 `LegacyReturnCode = -9180`으로 기록한다. `ErrorMessage`에는 `SQL_VALIDATE_RUN`, `SQL_VALIDATE_PREDECESSORS`, `SQL_CAPTURE_CONTROL_TOTALS`, `SQL_VALIDATE_SUMMARIES`, `SQL_VALIDATE_STAT_FORMULAS` 중 실제 실패 지점과 불일치 코드·기대값·실제값을 함께 남긴다.