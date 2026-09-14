## 통합 데이터 정합성 검증 SQL 세트

### 공통 검증 규격

모든 검증은 업무 단계가 완료된 후 `SNAPSHOT` 격리에서 수행한다. 검증 SQL에도 읽기 비일관성 힌트를 사용하지 않는다.

공통 입력은 다음과 같다.

- `@p_runId BIGINT`
- `@p_businessYmd VARCHAR(8)`
- 필요한 검증에 한해 단계에서 캡처한 처리일

검증 결과는 다음 원칙으로 처리한다.

- 제어 합계는 `batch.BatchControlTotal`에 INSERT한다.
- 상세 불일치는 `batch.BatchValidationIssue`에 INSERT한다.
- 기본 승인 기준은 불일치 0건이다.
- 승인되지 않은 금액 차이, 중복, 원천 누락 또는 체크포인트 불일치는 `Error` 이상으로 기록한다.
- `Error` 또는 `Critical`이 한 건이라도 존재하면 S17은 실패한다.
- 검증 SQL은 업무 테이블 이름을 동적으로 조립하지 않는다.
- 두 집계 스칼라는 각각 독립 CTE 또는 독립 스칼라 하위 쿼리에서 계산한다.

### SQL-00 — 실행 컨텍스트 및 잠금 검증

```sql
DECLARE @v_ymd VARCHAR(8) = @p_businessYmd;
DECLARE @v_runId BIGINT = @p_runId;

SELECT
    CASE
        WHEN LEN(@v_ymd) = 8
         AND TRY_CONVERT(DATE, @v_ymd, 112) IS NOT NULL
        THEN 0
        ELSE 1
    END AS InvalidBusinessYmd,
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM batch.BatchRun
             WHERE RunId = @v_runId
               AND JobName = N'POQSettleBatch12'
               AND BatchYmd = @v_ymd
               AND RunStatus IN (N'Running', N'Restarting')
        )
        THEN 0
        ELSE 1
    END AS InvalidRunContext,
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM batch.BatchRunLock
             WHERE JobName = N'POQSettleBatch12'
               AND BatchYmd = @v_ymd
               AND OwnerRunId = @v_runId
               AND LockStatus = N'Held'
        )
        THEN 0
        ELSE 1
    END AS InvalidLockOwner;
```

### SQL-01 — 배치 제어 객체 일관성

```sql
DECLARE @v_runId BIGINT = @p_runId;

SELECT RunId, StepCode, COUNT_BIG(*) AS DuplicateCount
  FROM batch.BatchStepJournal
 WHERE RunId = @v_runId
 GROUP BY RunId, StepCode
HAVING COUNT_BIG(*) > 1;

SELECT RunId, StepCode, COUNT_BIG(*) AS DuplicateCount
  FROM batch.BatchCheckpoint
 WHERE RunId = @v_runId
 GROUP BY RunId, StepCode
HAVING COUNT_BIG(*) > 1;

SELECT J.RunId,
       J.StepCode,
       J.StepStatus,
       C.CheckpointStatus
  FROM batch.BatchStepJournal AS J
  JOIN batch.BatchCheckpoint AS C
    ON C.RunId = J.RunId
   AND C.StepCode = J.StepCode
 WHERE J.RunId = @v_runId
   AND
   (
       J.StepStatus = N'Succeeded'
       AND C.CheckpointStatus <> N'Succeeded'
       OR
       J.StepStatus = N'Failed'
       AND C.CheckpointStatus = N'Succeeded'
   );

SELECT StepCode, StepStatus, StartedAtUtc, CompletedAtUtc
  FROM batch.BatchStepJournal
 WHERE RunId = @v_runId
   AND CompletedAtUtc IS NOT NULL
   AND CompletedAtUtc < StartedAtUtc;
```

S12부터 S14까지의 부분 성공도 별도로 검사한다.

```sql
DECLARE @v_runId BIGINT = @p_runId;

SELECT COUNT_BIG(*) AS InvalidCompositeCheckpointCount
  FROM batch.BatchCheckpoint
 WHERE RunId = @v_runId
   AND StepCode IN (N'S12', N'S13', N'S14')
   AND CheckpointStatus = N'Succeeded'
HAVING COUNT_BIG(*) NOT IN (0, 3);
```

### SQL-02 — 정산율 스냅샷 범위와 행 수

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT N'TPGSettleRate' AS TargetObject,
       COUNT_BIG(*) AS RowCount,
       SUM(CASE WHEN YMD <> @v_ymd THEN 1 ELSE 0 END) AS InvalidDateCount
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @v_ymd
UNION ALL
SELECT N'TClientSettleRate',
       COUNT_BIG(*),
       SUM(CASE WHEN YMD <> @v_ymd THEN 1 ELSE 0 END)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @v_ymd
UNION ALL
SELECT N'TPGSettleRate4Extra',
       COUNT_BIG(*),
       SUM(CASE WHEN YMD <> @v_ymd THEN 1 ELSE 0 END)
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @v_ymd
UNION ALL
SELECT N'TClientSettleRate4Extra',
       COUNT_BIG(*),
       SUM(CASE WHEN YMD <> @v_ymd THEN 1 ELSE 0 END)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @v_ymd
UNION ALL
SELECT N'TClientSettleRate4MobileCo',
       COUNT_BIG(*),
       SUM(CASE WHEN YMD <> @v_ymd THEN 1 ELSE 0 END)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @v_ymd;
```

대표 업무 키 중복 검증은 다음과 같다.

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT YMD, PGNAME, MALLID, COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @v_ymd
 GROUP BY YMD, PGNAME, MALLID
HAVING COUNT_BIG(*) > 1;

SELECT YMD, CLIENTID, PGNAME, MALLID, COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @v_ymd
 GROUP BY YMD, CLIENTID, PGNAME, MALLID
HAVING COUNT_BIG(*) > 1;
```

### SQL-03 — 정상·취소 원장 연결 및 중복

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT YMD, PLTID, USESTATE, CID, COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
 GROUP BY YMD, PLTID, USESTATE, CID
HAVING COUNT_BIG(*) > 1;

SELECT C.YMD,
       C.PLTID,
       C.CLIENTID,
       C.PGNAME,
       C.MALLID
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
 WHERE C.YMD = @v_ymd
   AND C.USESTATE = 1
   AND NOT EXISTS
   (
       SELECT 1
         FROM SETTLE_POQ_DB.dbo.TSettleMst AS O
        WHERE O.PLTID = C.PLTID
          AND O.USESTATE = 0
   );

SELECT YMD, USESTATE,
       COUNT_BIG(*) AS RowCount,
       SUM(ISNULL(TXAMT, 0)) AS TxAmount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
 GROUP BY YMD, USESTATE;
```

### SQL-04 — 수수료 총액 불변식

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT ID,
       YMD,
       PLTID,
       CLTOTAL,
       CLCOMM + CLVT + CLETC + CLINTCOMM AS ExpectedCLTotal,
       PGTOTAL,
       PGCOMM + PGVT + PGETC
       + CASE
             WHEN PGINTREALCOMM = 0 THEN PGINTEXPCOMM
             ELSE PGINTREALCOMM
         END AS ExpectedPGTotal,
       POQINCOME
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
   AND
   (
       ISNULL(CLTOTAL, 0)
       <> ISNULL(CLCOMM, 0)
        + ISNULL(CLVT, 0)
        + ISNULL(CLETC, 0)
        + ISNULL(CLINTCOMM, 0)
       OR
       ISNULL(PGTOTAL, 0)
       <> ISNULL(PGCOMM, 0)
        + ISNULL(PGVT, 0)
        + ISNULL(PGETC, 0)
        + CASE
              WHEN ISNULL(PGINTREALCOMM, 0) = 0
              THEN ISNULL(PGINTEXPCOMM, 0)
              ELSE ISNULL(PGINTREALCOMM, 0)
          END
       OR
       ISNULL(POQINCOME, 0)
       <> ISNULL(CLTOTAL, 0) - ISNULL(PGTOTAL, 0)
   );
```

### SQL-05 — 수금·지급 상태와 날짜 조합

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT ID,
       PLTID,
       INSTATE,
       INYMD,
       OUTSTATE,
       OUTYMD
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
   AND
   (
       INSTATE = 1
       AND ISNULL(INYMD, '') = ''
       OR
       OUTSTATE IN (1, 2, 5)
       AND ISNULL(OUTYMD, '') = ''
       OR
       OUTSTATE = 0
       AND OUTYMD IS NOT NULL
   );

SELECT ID, PLTID, AYMD, CYMD, INYMD, OUTYMD
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
   AND
   (
       CYMD IS NOT NULL
       AND CYMD < AYMD
       OR
       INYMD IS NOT NULL
       AND LEN(INYMD) <> 8
       OR
       OUTYMD IS NOT NULL
       AND LEN(OUTYMD) <> 8
   );
```

### SQL-06 — 후취정산 누락분 비교

현재 원장에는 S10과 S11의 추가 행이 포함되어 있으므로 S09 당시 모집단을 재현할 때 차액정산 행을 제외한다.

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

WITH Expected AS
(
    SELECT A.ClientID,
           A.OutYMD,
           SUM(CAST(A.CLTotal AS MONEY)) AS ExpectedAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON A.YMD = B.YMD
       AND A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
      JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON A.ClientID = C.ClientID
     WHERE A.YMD = @v_ymd
       AND A.OutState = 2
       AND ISNULL(B.TaxFGBill, 2) = 1
       AND ISNULL(A.ExtraSettleFlag, 0) = 0
     GROUP BY A.ClientID, A.OutYMD
),
Actual AS
(
    SELECT ClientID,
           OutYMD,
           SUM(CAST(CLSettleAmt AS MONEY)) AS ActualAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMiss
     WHERE OutState = 2
       AND ISNULL(IssueType, 0) = 15
     GROUP BY ClientID, OutYMD
)
SELECT COALESCE(E.ClientID, A.ClientID) AS ClientID,
       COALESCE(E.OutYMD, A.OutYMD) AS OutYMD,
       ISNULL(E.ExpectedAmount, 0) AS ExpectedAmount,
       ISNULL(A.ActualAmount, 0) AS ActualAmount
  FROM Expected AS E
  FULL OUTER JOIN Actual AS A
    ON A.ClientID = E.ClientID
   AND A.OutYMD = E.OutYMD
 WHERE ISNULL(E.ExpectedAmount, 0) <> ISNULL(A.ActualAmount, 0);
```

### SQL-07 — 차액정산 모집단 중복과 동결 제어 합계

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

SELECT PLTID,
       CLIENTID,
       PGNAME,
       MALLID,
       ProcYMD,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @v_ymd
   AND ExtraSettleFlag = 1
 GROUP BY PLTID, CLIENTID, PGNAME, MALLID, ProcYMD
HAVING COUNT_BIG(*) > 1;
```

```sql
DECLARE @v_runId BIGINT = @p_runId;
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @v_runId,
       N'S17',
       N'FrozenLedgerRowCount',
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
    OR ProcYMD = @v_ymd;

INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @v_runId,
       N'S17',
       N'FrozenLedgerTxAmount',
       CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @v_ymd
    OR ProcYMD = @v_ymd;
```

### SQL-08 — 요약 테이블 스칼라 합계 비교

각 측의 집계를 독립 CTE로 계산하고 스칼라 하위 쿼리로 비교한다.

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

WITH Expected AS
(
    SELECT COUNT_BIG(*) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount,
           ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS CLTotal,
           ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS PGTotal
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @v_ymd
),
Actual AS
(
    SELECT ISNULL(SUM(CAST(TXCNT AS BIGINT)), 0) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount,
           ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0) AS CLTotal,
           ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0) AS PGTotal
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @v_ymd
)
SELECT (SELECT RowCount FROM Expected) AS ExpectedRowCount,
       (SELECT RowCount FROM Actual) AS ActualRowCount,
       (SELECT TxAmount FROM Expected) AS ExpectedTxAmount,
       (SELECT TxAmount FROM Actual) AS ActualTxAmount,
       (SELECT CLTotal FROM Expected) AS ExpectedCLTotal,
       (SELECT CLTotal FROM Actual) AS ActualCLTotal,
       (SELECT PGTotal FROM Expected) AS ExpectedPGTotal,
       (SELECT PGTotal FROM Actual) AS ActualPGTotal;
```

부분취소·회수·지급 요약도 각각의 원본 필터를 유지하여 별도 SQL로 실행한다.

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

WITH ExpectedPartial AS
(
    SELECT COUNT_BIG(*) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @v_ymd
       AND USESTATE = 2
),
ActualPartial AS
(
    SELECT ISNULL(SUM(CAST(TXCNT AS BIGINT)), 0) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @v_ymd
),
ExpectedIn AS
(
    SELECT COUNT_BIG(*) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @v_ymd
       AND INSTATE = 1
),
ActualIn AS
(
    SELECT ISNULL(SUM(CAST(INCNT AS BIGINT)), 0) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @v_ymd
),
ExpectedOut AS
(
    SELECT COUNT_BIG(*) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @v_ymd
       AND OUTSTATE IN (2, 9)
),
ActualOut AS
(
    SELECT ISNULL(SUM(CAST(OUTCNT AS BIGINT)), 0) AS RowCount,
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @v_ymd
       AND OUTSTATE IN (2, 9)
)
SELECT
    (SELECT RowCount FROM ExpectedPartial) AS ExpectedPartialRows,
    (SELECT RowCount FROM ActualPartial) AS ActualPartialRows,
    (SELECT TxAmount FROM ExpectedPartial) AS ExpectedPartialAmount,
    (SELECT TxAmount FROM ActualPartial) AS ActualPartialAmount,
    (SELECT RowCount FROM ExpectedIn) AS ExpectedInRows,
    (SELECT RowCount FROM ActualIn) AS ActualInRows,
    (SELECT TxAmount FROM ExpectedIn) AS ExpectedInAmount,
    (SELECT TxAmount FROM ActualIn) AS ActualInAmount,
    (SELECT RowCount FROM ExpectedOut) AS ExpectedOutRows,
    (SELECT RowCount FROM ActualOut) AS ActualOutRows,
    (SELECT TxAmount FROM ExpectedOut) AS ExpectedOutAmount,
    (SELECT TxAmount FROM ActualOut) AS ActualOutAmount;
```

### SQL-09 — 지급요약 그룹 단위 대조

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

WITH Expected AS
(
    SELECT YMD,
           AYMD,
           INYMD,
           OUTYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           USESTATE,
           OUTSTATE,
           SettleCurrency,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           COUNT_BIG(*) AS OUTCNT,
           SUM(ISNULL(TXAMT, 0)) AS TXAMT,
           SUM(ISNULL(CLTOTAL, 0)) AS CLTOTAL,
           SUM(ISNULL(PGTOTAL, 0)) AS PGTOTAL,
           SUM(ISNULL(POQINCOME, 0)) AS POQINCOME
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @v_ymd
       AND OUTSTATE IN (2, 9)
     GROUP BY YMD, AYMD, INYMD, OUTYMD, CLIENTID, PGNAME, MALLID,
              SERVICENAME, PRODUCTNAME, USESTATE, OUTSTATE,
              SettleCurrency, CompanySalesType, ProcYMD, ExtraSettleFlag
),
Actual AS
(
    SELECT YMD,
           AYMD,
           INYMD,
           OUTYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           USESTATE,
           OUTSTATE,
           SettleCurrency,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           CAST(OUTCNT AS BIGINT) AS OUTCNT,
           TXAMT,
           CLTOTAL,
           PGTOTAL,
           POQINCOME
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @v_ymd
       AND OUTSTATE IN (2, 9)
),
ExpectedOnly AS
(
    SELECT * FROM Expected
    EXCEPT
    SELECT * FROM Actual
),
ActualOnly AS
(
    SELECT * FROM Actual
    EXCEPT
    SELECT * FROM Expected
)
SELECT N'ExpectedOnly' AS DifferenceType, *
  FROM ExpectedOnly
UNION ALL
SELECT N'ActualOnly', *
  FROM ActualOnly;
```

### SQL-10 — PG 수금 통계 재집계 비교

세 원천 브랜치는 동일한 컬럼 목록과 순서를 사용한다.

```sql
DECLARE @v_ymd CHAR(8) = @p_businessYmd;

WITH SourceRows AS
(
    SELECT INYMD,
           LOWER(CLIENTID) AS CLIENTID,
           LOWER(PGNAME) AS PGNAME,
           LOWER(MALLID) AS MALLID,
           TXAMT - PGTOTAL AS COLLECTAMT,
           PGCOMM + PGETC + ISNULL(PGINTREALCOMM, 0) AS PGCOMM,
           PGVT AS PGVT
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE INYMD = @v_ymd
       AND INSTATE = 1

    UNION ALL

    SELECT COLLECTYMD,
           LOWER(CLIENTID),
           LOWER(PGNAME),
           LOWER(MALLID),
           CLCOLLECTAMT,
           CAST(PGCOMM / 1.1 AS INT),
           PGCOMM - CAST(PGCOMM / 1.1 AS INT)
      FROM SETTLE_POQ_DB.dbo.TTArsPGCollect
     WHERE COLLECTYMD = @v_ymd

    UNION ALL

    SELECT COLLECTYMD,
           LOWER(CLIENTID),
           LOWER(PGNAME),
           LOWER(MALLID),
           CLCOLLECTAMT,
           CAST(PGCOMM / 1.1 AS INT),
           PGCOMM - CAST(PGCOMM / 1.1 AS INT)
      FROM SETTLE_POQ_DB.dbo.TBArsPGCollect
     WHERE COLLECTYMD = @v_ymd
),
Expected AS
(
    SELECT ISNULL(SUM(CAST(COLLECTAMT AS DECIMAL(38,4))), 0) AS COLLECTAMT,
           ISNULL(SUM(CAST(PGCOMM AS DECIMAL(38,4))), 0) AS PGCOMM,
           ISNULL(SUM(CAST(PGVT AS DECIMAL(38,4))), 0) AS PGVT
      FROM SourceRows
),
Actual AS
(
    SELECT ISNULL(SUM(CAST(COLLECTAMT AS DECIMAL(38,4))), 0) AS COLLECTAMT,
           ISNULL(SUM(CAST(PGCOMM AS DECIMAL(38,4))), 0) AS PGCOMM,
           ISNULL(SUM(CAST(PGVT AS DECIMAL(38,4))), 0) AS PGVT
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE INYMD = @v_ymd
)
SELECT (SELECT COLLECTAMT FROM Expected) AS ExpectedCollectAmount,
       (SELECT COLLECTAMT FROM Actual) AS ActualCollectAmount,
       (SELECT PGCOMM FROM Expected) AS ExpectedPGComm,
       (SELECT PGCOMM FROM Actual) AS ActualPGComm,
       (SELECT PGVT FROM Expected) AS ExpectedPGVT,
       (SELECT PGVT FROM Actual) AS ActualPGVT;
```

### SQL-11 — 검증 이슈 및 제어 합계 저장

```sql
DECLARE @v_runId BIGINT = @p_runId;

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
    @v_runId,
    N'S17',
    @p_controlName,
    @p_controlValue,
    SYSUTCDATETIME()
);
```

```sql
DECLARE @v_runId BIGINT = @p_runId;

INSERT INTO batch.BatchValidationIssue
(
    RunId,
    StepCode,
    IssueCode,
    Severity,
    ExpectedValue,
    ActualValue,
    DetectedAtUtc
)
VALUES
(
    @v_runId,
    N'S17',
    @p_issueCode,
    @p_severity,
    @p_expectedValue,
    @p_actualValue,
    SYSUTCDATETIME()
);
```

S17 최종 판정은 다음 SQL로 수행한다.

```sql
DECLARE @v_runId BIGINT = @p_runId;

SELECT COUNT_BIG(*) AS BlockingIssueCount
  FROM batch.BatchValidationIssue
 WHERE RunId = @v_runId
   AND StepCode = N'S17'
   AND Severity IN (N'Error', N'Critical');
```

### SQL-12 — 재시작 및 최종 게시 전 검증

```sql
DECLARE @v_runId BIGINT = @p_runId;

SELECT C.StepCode,
       C.CheckpointStatus,
       J.StepStatus,
       J.LegacyReturnCode
  FROM batch.BatchCheckpoint AS C
  LEFT JOIN batch.BatchStepJournal AS J
    ON J.RunId = C.RunId
   AND J.StepCode = C.StepCode
 WHERE C.RunId = @v_runId
 ORDER BY C.StepCode;
```

```sql
DECLARE @v_runId BIGINT = @p_runId;

SELECT CASE
           WHEN EXISTS
           (
               SELECT 1
                 FROM batch.BatchValidationIssue
                WHERE RunId = @v_runId
                  AND Severity IN (N'Error', N'Critical')
           )
           THEN 1
           ELSE 0
       END AS HasBlockingIssue,
       CASE
           WHEN
           (
               SELECT COUNT_BIG(*)
                 FROM batch.BatchCheckpoint
                WHERE RunId = @v_runId
                  AND CheckpointStatus = N'Succeeded'
                  AND StepCode BETWEEN N'S01' AND N'S17'
           ) = 17
           THEN 0
           ELSE 1
       END AS HasIncompleteCheckpoint;
```

최종 게시 전 두 결과가 모두 0이어야 한다. 성공 시 S18은 `batch.BatchRun.RunStatus`를 `Succeeded`로 갱신하고, 실패 시 `Failed`로 갱신한 후 현재 `RunId`가 소유한 `batch.BatchRunLock`만 `Released`로 변경한다.