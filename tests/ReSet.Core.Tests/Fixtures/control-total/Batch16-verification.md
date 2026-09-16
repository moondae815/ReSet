## 통합 데이터 정합성 검증 SQL 세트

### 공통 검증 계약

모든 검증은 최종 원장 장벽 이후 안정된 `SNAPSHOT` 읽기로 실행한다. 검증 SQL에는 `NOLOCK`을 사용하지 않는다.

공통 바인딩은 다음과 같다.

- `@p_runId BIGINT`
- `@p_ymd VARCHAR(8)`
- 병행 대사용 `@p_referenceRunId BIGINT`
- 신규 실행 대사용 `@p_candidateRunId BIGINT`

표준 결과 컬럼은 다음과 같다.

| 컬럼 | 의미 |
|---|---|
| `CheckId` | 검증 항목 식별자 |
| `RunId` | 배치 실행 식별자 |
| `SettlementYmd` | 정산기준일 |
| `Severity` | `Info`, `Warning`, `Error`, `Critical` |
| `MismatchCount` | 불일치 건수 |
| `ExpectedAmount` | 예상 금액 |
| `ActualAmount` | 실제 금액 |
| `DifferenceAmount` | 차이 |
| `CheckedAtUtc` | 검증 UTC 시각 |

금액 집계 비교는 각 원천을 독립된 CTE 또는 스칼라 하위 질의로 계산한 뒤 비교한다. 두 집계의 행 집합을 카티전 곱으로 결합하지 않는다.

### V01 — 실행 컨텍스트 및 기준일 검증

```sql
-- SQL_VALIDATE_RUN_CONTEXT
WITH RunData AS
(
    SELECT RunId,
           BatchYmd,
           RunStatus
      FROM batch.BatchRun
     WHERE RunId = @p_runId
),
InvalidJournal AS
(
    SELECT COUNT_BIG(*) AS MismatchCount
      FROM batch.BatchStepJournal AS J
     WHERE J.RunId = @p_runId
       AND NOT EXISTS
           (
               SELECT 1
                 FROM RunData AS R
                WHERE R.RunId = J.RunId
                  AND R.BatchYmd = @p_ymd
           )
)
SELECT N'V01' AS CheckId,
       @p_runId AS RunId,
       @p_ymd AS SettlementYmd,
       N'Error' AS Severity,
       CASE
           WHEN TRY_CONVERT(date, @p_ymd, 112) IS NULL THEN 1
           WHEN CONVERT(varchar(8), TRY_CONVERT(date, @p_ymd, 112), 112) <> @p_ymd THEN 1
           WHEN NOT EXISTS
                (
                    SELECT 1
                      FROM RunData
                     WHERE BatchYmd = @p_ymd
                ) THEN 1
           ELSE (SELECT MismatchCount FROM InvalidJournal)
       END AS MismatchCount,
       CAST(NULL AS decimal(38,4)) AS ExpectedAmount,
       CAST(NULL AS decimal(38,4)) AS ActualAmount,
       CAST(NULL AS decimal(38,4)) AS DifferenceAmount,
       SYSUTCDATETIME() AS CheckedAtUtc;
```

### V02 — 수수료율 스냅샷 완전성 검증

```sql
-- SQL_VALIDATE_RATE_SNAPSHOT_COUNTS
SELECT N'TPGSettleRate' AS SnapshotName, COUNT_BIG(*) AS RowCount
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'TClientSettleRate', COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'TPGSettleRate4Extra', COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'TClientSettleRate4Extra', COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_ymd
UNION ALL
SELECT N'TClientSettleRate4MobileCo', COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_VALIDATE_PAID_SETTLEMENT_GUARD
SELECT N'V02_PAID_GUARD' AS CheckId,
       @p_runId AS RunId,
       @p_ymd AS SettlementYmd,
       N'Critical' AS Severity,
       COUNT_BIG(*) AS MismatchCount,
       CAST(0 AS decimal(38,4)) AS ExpectedAmount,
       CAST(COUNT_BIG(*) AS decimal(38,4)) AS ActualAmount,
       CAST(COUNT_BIG(*) AS decimal(38,4)) AS DifferenceAmount,
       SYSUTCDATETIME() AS CheckedAtUtc
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND OutState IN (1,5)
   AND OutYMD IS NOT NULL;
```

```sql
-- SQL_VALIDATE_CLIENT_RATE_DUPLICATES
SELECT YMD,
       CLIENTID,
       PGNAME,
       MALLID,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd
 GROUP BY YMD, CLIENTID, PGNAME, MALLID
HAVING COUNT_BIG(*) > 1;
```

### V03 — 기본 거래 대비 정산원장 포괄성 검증

세 원천 분기는 동일한 컬럼 순서로 투영한다. 전체거래는 `USESTATE = 0`, 부분취소는 `USESTATE = 2`, 환불은 `USESTATE = 3`을 유지한다.

```sql
-- SQL_VALIDATE_LEDGER_BRANCH_COUNTS
WITH SourceBranches AS
(
    SELECT N'Full' AS BranchName,
           0 AS UseState,
           COUNT_BIG(*) AS RowCount,
           SUM(CAST(A.TXAMT AS decimal(38,4))) AS TxAmount
      FROM PaymentDB.dbo.TTxMst AS A
      JOIN SETTLE_POQ_DB.dbo.TPGSettleRate AS B
        ON A.YMD = B.YMD
       AND A.PGNAME = B.PGNAME
       AND A.MALLID = B.MALLID
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS C
        ON A.YMD = C.YMD
       AND A.CLIENTID = C.CLIENTID
       AND A.PGNAME = C.PGNAME
       AND A.MALLID = C.MALLID
      JOIN SETTLE_POQ_DB.dbo.TPGCMRate AS D
        ON A.PGNAME = D.PGNAME
       AND A.MALLID = D.MALLID
     WHERE A.YMD = @p_ymd
       AND D.TAXEXEMPTIONFLAG = 0

    UNION ALL

    SELECT N'PartialCancel',
           2,
           COUNT_BIG(*),
           SUM(CAST(E.TXAMT AS decimal(38,4)))
      FROM PaymentDB.dbo.TTxMst AS A
      JOIN SETTLE_POQ_DB.dbo.TPGSettleRate AS B
        ON A.YMD = B.YMD
       AND A.PGNAME = B.PGNAME
       AND A.MALLID = B.MALLID
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS C
        ON A.YMD = C.YMD
       AND A.CLIENTID = C.CLIENTID
       AND A.PGNAME = C.PGNAME
       AND A.MALLID = C.MALLID
      JOIN SETTLE_POQ_DB.dbo.TPGCMRate AS D
        ON A.PGNAME = D.PGNAME
       AND A.MALLID = D.MALLID
      JOIN PaymentDB.dbo.TPartialCancelTxMst AS E
        ON A.PLTID = E.PLTID
     WHERE E.YMD = @p_ymd
       AND E.SettleState = 1
       AND D.TAXEXEMPTIONFLAG = 0

    UNION ALL

    SELECT N'Refund',
           3,
           COUNT_BIG(*),
           SUM(CAST(E.REFUNDREQAMT AS decimal(38,4)))
      FROM PaymentDB.dbo.TTxMst AS A
      JOIN SETTLE_POQ_DB.dbo.TPGSettleRate AS B
        ON A.YMD = B.YMD
       AND A.PGNAME = B.PGNAME
       AND A.MALLID = B.MALLID
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS C
        ON A.YMD = C.YMD
       AND A.CLIENTID = C.CLIENTID
       AND A.PGNAME = C.PGNAME
       AND A.MALLID = C.MALLID
      JOIN SETTLE_POQ_DB.dbo.TPGCMRate AS D
        ON A.PGNAME = D.PGNAME
       AND A.MALLID = D.MALLID
      JOIN PaymentDB.dbo.TRefundMst AS E
        ON A.PLTID = E.PLTID
      JOIN PaymentDB.dbo.TRefundClient AS F
        ON A.CLIENTID = F.CLIENTID
       AND A.PGNAME = F.PGNAME
       AND A.MALLID = F.MALLID
     WHERE E.REQYMD = @p_ymd
       AND D.TAXEXEMPTIONFLAG = 0
),
ActualLedger AS
(
    SELECT USESTATE AS UseState,
           COUNT_BIG(*) AS RowCount,
           SUM(CAST(TXAMT AS decimal(38,4))) AS TxAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
       AND ISNULL(ExtraSettleFlag, 0) = 0
     GROUP BY USESTATE
)
SELECT S.BranchName,
       S.UseState,
       S.RowCount AS ExpectedRowCount,
       ISNULL(A.RowCount, 0) AS ActualRowCount,
       S.TxAmount AS ExpectedAmount,
       ISNULL(A.TxAmount, 0) AS ActualAmount
  FROM SourceBranches AS S
  LEFT JOIN ActualLedger AS A
    ON A.UseState = S.UseState;
```

### V04 — 취소행과 정상 원행 연결 검증

```sql
-- SQL_VALIDATE_CANCEL_ORPHANS
SELECT C.PLTID,
       C.CLIENTID,
       C.PGNAME,
       C.MALLID,
       C.YMD,
       C.CYMD
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
 WHERE C.YMD = @p_ymd
   AND C.USESTATE = 1
   AND NOT EXISTS
       (
           SELECT 1
             FROM SETTLE_POQ_DB.dbo.TSettleMst AS O
            WHERE O.PLTID = C.PLTID
              AND O.USESTATE = 0
       );
```

```sql
-- SQL_VALIDATE_CANCEL_SOURCE_MULTIPLICITY
SELECT A.PLTID,
       COUNT_BIG(*) AS MatchedLedgerRows
  FROM PaymentDB.dbo.TTxMst AS A
  JOIN SETTLE_POQ_DB.dbo.TSettleMst AS B
    ON A.PLTID = B.PLTID
 WHERE A.YMDCANCEL = @p_ymd
   AND B.USESTATE = 0
   AND ISNULL(B.CompanySalesType, 4) NOT IN (0,1,2,3)
 GROUP BY A.PLTID
HAVING COUNT_BIG(*) > 1;
```

### V05 — 예외 처리 순서 검증

S07의 18개 문장별 대상 건수와 금액 차이는 `batch.BatchControlTotal`에 각각 기록한다. 누락된 규칙 통제합계는 오류로 처리한다.

```sql
-- SQL_VALIDATE_EXCEPTION_RULE_CONTROLS
WITH RequiredRules AS
(
    SELECT V.RuleCode
      FROM
      (
          VALUES
              (N'UPDATE01'), (N'UPDATE02'), (N'UPDATE03'),
              (N'UPDATE04'), (N'UPDATE05'), (N'UPDATE06'),
              (N'UPDATE07'), (N'UPDATE08'), (N'UPDATE09'),
              (N'UPDATE10'), (N'UPDATE11'), (N'UPDATE12'),
              (N'UPDATE13'), (N'UPDATE14'), (N'UPDATE15'),
              (N'UPDATE16'), (N'UPDATE17'), (N'UPDATE18')
      ) AS V(RuleCode)
)
SELECT R.RuleCode
  FROM RequiredRules AS R
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchControlTotal AS C
            WHERE C.RunId = @p_runId
              AND C.StepCode = N'S07'
              AND C.ControlName = N'Rule_' + R.RuleCode + N'_Rows'
       );
```

### V06 — 수수료, VAT 및 총액 산식 검증

```sql
-- SQL_VALIDATE_LEDGER_TOTAL_FORMULAS
SELECT ID,
       YMD,
       CLIENTID,
       PGNAME,
       MALLID,
       CLTOTAL,
       CLCOMM + CLVT + CLETC + CLINTCOMM AS ExpectedCLTotal,
       PGTOTAL,
       PGCOMM + PGVT + PGETC
           + CASE
                 WHEN PGINTREALCOMM = 0 THEN PGINTEXPCOMM
                 ELSE PGINTREALCOMM
             END AS ExpectedPGTotal,
       POQINCOME,
       CLCOMM + CLVT + CLETC + CLINTCOMM
           - (
                PGCOMM + PGVT + PGETC
                + CASE
                      WHEN PGINTREALCOMM = 0 THEN PGINTEXPCOMM
                      ELSE PGINTREALCOMM
                  END
             ) AS ExpectedPOQIncome
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND
   (
       CLTOTAL <> CLCOMM + CLVT + CLETC + CLINTCOMM
       OR PGTOTAL <> PGCOMM + PGVT + PGETC
            + CASE
                  WHEN PGINTREALCOMM = 0 THEN PGINTEXPCOMM
                  ELSE PGINTREALCOMM
              END
       OR POQINCOME <> CLCOMM + CLVT + CLETC + CLINTCOMM
            - (
                 PGCOMM + PGVT + PGETC
                 + CASE
                       WHEN PGINTREALCOMM = 0 THEN PGINTEXPCOMM
                       ELSE PGINTREALCOMM
                   END
              )
   );
```

```sql
-- SQL_DIAGNOSE_COMPONENT_VAT_TRUNCATION
SELECT ID,
       CAST(CLComm / CAST(1.1 AS decimal(2,1)) AS int)
       + CAST(CLEtc / CAST(1.1 AS decimal(2,1)) AS int)
       + CAST(CLIntComm / CAST(1.1 AS decimal(2,1)) AS int)
           AS ComponentWiseSupplyAmount,
       CAST((CLComm + CLEtc + CLIntComm) / CAST(1.1 AS decimal(2,1)) AS int)
           AS AggregateSupplyAmount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND CLVTType = 1;
```

### V07 — 수금 및 지급예정 상태 검증

```sql
-- SQL_VALIDATE_STATE_DATE_PAIRS
SELECT ID,
       YMD,
       InState,
       INYMD,
       OutState,
       OUTYMD,
       EDIReqYmd
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND
   (
       (InState = 1 AND ISNULL(INYMD, '') = '')
       OR (InState = 0 AND ISNULL(INYMD, '') <> '' AND USESTATE = 3)
       OR (OutState IN (1,2,5) AND ISNULL(OUTYMD, '') = '')
       OR (OutState = 0 AND OUTYMD IS NOT NULL)
   );
```

```sql
-- SQL_VALIDATE_MANUAL_ACQUISITION_EDI_DATE
SELECT A.ID,
       A.PLTID,
       A.CLIENTID,
       A.PGNAME,
       A.EDIReqYmd
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
  JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt AS C
    ON A.CLIENTID = C.CLIENTID
 WHERE A.YMD = @p_ymd
   AND C.AcqType = 1
   AND A.OutState IN (2,9)
   AND ISNULL(A.EDIReqYmd, '') = '';
```

### V08 — 추가정산 범위 및 중복 검증

```sql
-- SQL_RECALCULATE_GENERIC_EXTRA_MIN_REQUEST_DATE
SELECT MIN(ReqYMD) AS MinimumRequestYmd
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;
```

```sql
-- SQL_VALIDATE_EXTRA_LEDGER_DUPLICATES
SELECT ProcYMD,
       YMD,
       CLIENTID,
       PGNAME,
       MALLID,
       PLTID,
       USESTATE,
       ExtraSettleFlag,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND ExtraSettleFlag = 1
 GROUP BY ProcYMD,
          YMD,
          CLIENTID,
          PGNAME,
          MALLID,
          PLTID,
          USESTATE,
          ExtraSettleFlag
HAVING COUNT_BIG(*) > 1;
```

### V09 — 최종 정산원장 장벽 통제합계

```sql
-- SQL_CAPTURE_FINAL_LEDGER_CONTROL_TOTALS
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId,
       N'S12',
       N'LedgerRowCount',
       CAST(COUNT_BIG(*) AS decimal(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerTxAmount',
       ISNULL(SUM(CAST(TXAMT AS decimal(38,4))), 0),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerCLTotal',
       ISNULL(SUM(CAST(CLTOTAL AS decimal(38,4))), 0),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId,
       N'S12',
       N'LedgerPGTotal',
       ISNULL(SUM(CAST(PGTOTAL AS decimal(38,4))), 0),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_VALIDATE_LEDGER_BARRIER
WITH Captured AS
(
    SELECT ControlName,
           ControlValue
      FROM batch.BatchControlTotal
     WHERE RunId = @p_runId
       AND StepCode = N'S12'
       AND ControlName IN
           (
               N'LedgerRowCount',
               N'LedgerTxAmount',
               N'LedgerCLTotal',
               N'LedgerPGTotal'
           )
),
CurrentValue AS
(
    SELECT N'LedgerRowCount' AS ControlName,
           CAST(COUNT_BIG(*) AS decimal(38,4)) AS ControlValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
    UNION ALL
    SELECT N'LedgerTxAmount',
           ISNULL(SUM(CAST(TXAMT AS decimal(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
    UNION ALL
    SELECT N'LedgerCLTotal',
           ISNULL(SUM(CAST(CLTOTAL AS decimal(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
    UNION ALL
    SELECT N'LedgerPGTotal',
           ISNULL(SUM(CAST(PGTOTAL AS decimal(38,4))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
)
SELECT C.ControlName,
       C.ControlValue AS CapturedValue,
       V.ControlValue AS CurrentValue
  FROM Captured AS C
  FULL OUTER JOIN CurrentValue AS V
    ON V.ControlName = C.ControlName
 WHERE ISNULL(C.ControlValue, 0) <> ISNULL(V.ControlValue, 0);
```

### V10 — PG 수금통계 재집계 비교

```sql
-- SQL_VALIDATE_PG_COLLECT_STATISTICS
WITH SourceStreams AS
(
    SELECT INYMD,
           LOWER(CLIENTID) AS CLIENTID,
           LOWER(PGNAME) AS PGNAME,
           LOWER(MALLID) AS MALLID,
           SUM(TXAMT - PGTOTAL) AS COLLECTAMT,
           SUM(PGCOMM + PGETC + ISNULL(PGINTREALCOMM, 0)) AS PGCOMM,
           SUM(PGVT) AS PGVT,
           SUM
           (
               CASE
                   WHEN PGNAME IN ('WOWCOIN','WOW_ARS','WOW_1588')
                       THEN CASE WHEN OUTYMD >= @p_ymd THEN TXAMT END
                   ELSE TXAMT
               END
           ) AS SETTLEWILLAMT,
           SUM
           (
               CASE
                   WHEN OUTYMD < @p_ymd AND OUTSTATE = 1
                       THEN CLCOMM + CLETC + ISNULL(CLINTCOMM, 0)
                   ELSE 0
               END
           ) AS AHEADSALESCOMM,
           SUM
           (
               CASE
                   WHEN OUTYMD < @p_ymd AND OUTSTATE = 1 THEN CLVT
                   ELSE 0
               END
           ) AS AHEADSALESVT,
           SUM
           (
               CASE
                   WHEN OUTYMD < @p_ymd AND OUTSTATE = 1
                       THEN TXAMT - CLTOTAL
                   ELSE 0
               END
           ) AS AHEADSETTLEAMT
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE INYMD = @p_ymd
       AND INSTATE = 1
     GROUP BY INYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)

    UNION ALL

    SELECT COLLECTYMD,
           LOWER(CLIENTID),
           LOWER(PGNAME),
           LOWER(MALLID),
           SUM(CLCOLLECTAMT),
           SUM(CAST(PGCOMM / 1.1 AS INT)),
           SUM(PGCOMM - CAST(PGCOMM / 1.1 AS INT)),
           SUM(CLRATETOTXAMT),
           0,
           0,
           0
      FROM SETTLE_POQ_DB.dbo.TTArsPGCollect
     WHERE COLLECTYMD = @p_ymd
     GROUP BY COLLECTYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)

    UNION ALL

    SELECT COLLECTYMD,
           LOWER(CLIENTID),
           LOWER(PGNAME),
           LOWER(MALLID),
           SUM(CLCOLLECTAMT),
           SUM(CAST(PGCOMM / 1.1 AS INT)),
           SUM(PGCOMM - CAST(PGCOMM / 1.1 AS INT)),
           SUM(CLRATETOTXAMT),
           0,
           0,
           0
      FROM SETTLE_POQ_DB.dbo.TBArsPGCollect
     WHERE COLLECTYMD = @p_ymd
     GROUP BY COLLECTYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)
),
Expected AS
(
    SELECT INYMD,
           LOWER(CLIENTID) AS CLIENTID,
           LOWER(PGNAME) AS PGNAME,
           LOWER(MALLID) AS MALLID,
           ISNULL(SUM(COLLECTAMT), 0) AS COLLECTAMT,
           ISNULL(SUM(PGCOMM), 0) AS PGCOMM,
           ISNULL(SUM(PGVT), 0) AS PGVT,
           ISNULL(SUM(COLLECTAMT + PGCOMM + PGVT), 0) AS LEFTSUMAMT,
           ISNULL(SUM(SETTLEWILLAMT), 0) AS SETTLEWILLAMT,
           ISNULL(SUM(AHEADSALESCOMM), 0) AS AHEADSALESCOMM,
           ISNULL(SUM(AHEADSALESVT), 0) AS AHEADSALESVT,
           ISNULL(SUM(AHEADSETTLEAMT), 0) AS AHEADSETTLEAMT
      FROM SourceStreams
     GROUP BY INYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)
),
Actual AS
(
    SELECT INYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           COLLECTAMT,
           PGCOMM,
           PGVT,
           LEFTSUMAMT,
           SETTLEWILLAMT,
           AHEADSALESCOMM,
           AHEADSALESVT,
           AHEADSETTLEAMT
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE INYMD = @p_ymd
)
SELECT *
  FROM Expected
EXCEPT
SELECT *
  FROM Actual;
```

역방향 초과 데이터 검증은 동일 CTE에서 `Actual EXCEPT Expected`로 실행한다.

### V11 — 기본 및 추가 요약 양방향 비교

```sql
-- SQL_VALIDATE_SETTLE_BY_TX
WITH Expected AS
(
    SELECT YMD,
           AYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           COUNT_BIG(*) AS TXCNT,
           SUM(ISNULL(TXAMT, 0)) AS TXAMT,
           SUM(ISNULL(CLCOMM, 0)) AS CLCOMM,
           SUM(ISNULL(CLINTCOMM, 0)) AS CLINTCOMM,
           SUM(ISNULL(CLETC, 0)) AS CLETC,
           SUM(ISNULL(CLVT, 0)) AS CLVT,
           SUM(ISNULL(CLTOTAL, 0)) AS CLTOTAL,
           SUM(ISNULL(PGCOMM, 0)) AS PGCOMM,
           SUM(ISNULL(PGINTEXPCOMM, 0)) AS PGINTEXPCOMM,
           SUM(ISNULL(PGINTREALCOMM, 0)) AS PGINTREALCOMM,
           SUM(ISNULL(PGETC, 0)) AS PGETC,
           SUM(ISNULL(PGVT, 0)) AS PGVT,
           SUM(ISNULL(PGTOTAL, 0)) AS PGTOTAL,
           SUM(ISNULL(POQINCOME, 0)) AS POQINCOME,
           USESTATE,
           CompanySalesType,
           SUM(ISNULL(ExtraTxAmt, 0)) AS ExtraTxAmt,
           ProcYMD,
           SUM(ISNULL(SeperateAmt, 0)) AS SeperateAmt,
           ExtraSettleFlag
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
     GROUP BY YMD,
              AYMD,
              CLIENTID,
              PGNAME,
              MALLID,
              SERVICENAME,
              PRODUCTNAME,
              USESTATE,
              CompanySalesType,
              ProcYMD,
              ExtraSettleFlag
),
Actual AS
(
    SELECT YMD,
           AYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           TXCNT,
           TXAMT,
           CLCOMM,
           CLINTCOMM,
           CLETC,
           CLVT,
           CLTOTAL,
           PGCOMM,
           PGINTEXPCOMM,
           PGINTREALCOMM,
           PGETC,
           PGVT,
           PGTOTAL,
           POQINCOME,
           USESTATE,
           CompanySalesType,
           ExtraTxAmt,
           ProcYMD,
           SeperateAmt,
           ExtraSettleFlag
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_ymd
)
SELECT *
  FROM Expected
EXCEPT
SELECT *
  FROM Actual;
```

동일한 양방향 패턴을 다음 원본 필터와 그룹 키로 적용한다.

| 대상 | 추가 필터 |
|---|---|
| `TPartialCancelByTX` | `YMD = @p_ymd AND USESTATE = 2`, 그룹 키에 `PLTID` 포함 |
| `TSettleByIN` | `YMD = @p_ymd AND INSTATE = 1`, 그룹 키에 `INYMD` 포함 |
| `TSettleByOUT` | `YMD = @p_ymd AND OUTSTATE IN (2,9)`, 그룹 키에 `INYMD`, `OUTYMD`, `OUTSTATE`, `SettleCurrency` 포함 |

### V12 — 수기매입 및 기타 지급요약 보정 검증

```sql
-- SQL_VALIDATE_MANUAL_ACQUISITION_SUMMARY
WITH ManualGroups AS
(
    SELECT A.OutYMD,
           A.ClientID,
           A.PGName
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt AS B
        ON A.ClientID = B.ClientID
     WHERE ISNULL(A.EDIReqYmd, '') = @p_ymd
       AND B.AcqType = 1
       AND A.OutState IN (2,9)
     GROUP BY A.OutYMD, A.ClientID, A.PGName
)
SELECT M.OutYMD,
       M.ClientID,
       M.PGName
  FROM ManualGroups AS M
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM SETTLE_POQ_DB.dbo.TSettleByOUT AS O
            WHERE O.OutYMD = M.OutYMD
              AND O.ClientID = M.ClientID
              AND O.PGName = M.PGName
       );
```

```sql
-- SQL_VALIDATE_SUMMARY_ETC_TARGETS
SELECT DISTINCT
       A.OUTYMD,
       A.YMD,
       A.AYMD,
       A.INYMD,
       A.CLIENTID,
       A.PGNAME,
       A.MALLID,
       A.SERVICENAME,
       A.PRODUCTNAME,
       A.USESTATE,
       A.ProcYMD,
       A.CompanySalesType,
       A.ExtraSettleFlag
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
  JOIN SETTLE_POQ_DB.dbo.TSettleMst AS B
    ON A.PLTID = B.PLTID
 WHERE B.YMD = @p_ymd
   AND B.OUTSTATE = 9
   AND B.USESTATE = 1
   AND A.OUTSTATE = 9
   AND A.USESTATE = 0
   AND A.OUTYMD IS NOT NULL;
```

### V13 — 미정산 누적 검증

```sql
-- SQL_VALIDATE_SETTLEMENT_MISS_AMOUNTS
WITH LedgerAmount AS
(
    SELECT A.ClientID,
           A.OutYMD,
           SUM(CAST(A.CLTotal AS money)) AS ExpectedAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON A.YMD = B.YMD
       AND A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
      JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON A.ClientID = C.ClientID
     WHERE ISNULL(B.TaxFGBill, 2) = 1
       AND A.OutState = 2
     GROUP BY A.ClientID, A.OutYMD
),
MissAmount AS
(
    SELECT ClientID,
           OutYMD,
           SUM(CAST(CLSettleAmt AS money)) AS ActualAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMiss
     WHERE OutState = 2
       AND ISNULL(IssueType, 0) = 15
     GROUP BY ClientID, OutYMD
)
SELECT COALESCE(L.ClientID, M.ClientID) AS ClientID,
       COALESCE(L.OutYMD, M.OutYMD) AS OutYMD,
       ISNULL(L.ExpectedAmount, 0) AS ExpectedAmount,
       ISNULL(M.ActualAmount, 0) AS ActualAmount
  FROM LedgerAmount AS L
  FULL OUTER JOIN MissAmount AS M
    ON M.ClientID = L.ClientID
   AND M.OutYMD = L.OutYMD
 WHERE ISNULL(L.ExpectedAmount, 0) <> ISNULL(M.ActualAmount, 0);
```

```sql
-- SQL_VALIDATE_SETTLEMENT_MISS_ID_DUPLICATES
SELECT ID,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TSettleMiss
 GROUP BY ID
HAVING COUNT_BIG(*) > 1;
```

### V14 — 트랜잭션 원자성과 잔여 데이터 검증

```sql
-- SQL_VALIDATE_JOURNAL_CHECKPOINT_ATOMICITY
SELECT J.RunId,
       J.StepCode,
       J.StepStatus,
       C.CheckpointStatus
  FROM batch.BatchStepJournal AS J
  LEFT JOIN batch.BatchCheckpoint AS C
    ON C.RunId = J.RunId
   AND C.StepCode = J.StepCode
 WHERE J.RunId = @p_runId
   AND
   (
       (J.StepStatus = N'Succeeded'
        AND ISNULL(C.CheckpointStatus, N'Pending') <> N'Succeeded')
       OR
       (J.StepStatus = N'Failed'
        AND C.CheckpointStatus = N'Succeeded')
   );
```

```sql
-- SQL_VALIDATE_SUMMARY_COMMIT_GROUP
WITH SummaryStatus AS
(
    SELECT StepCode,
           StepStatus
      FROM batch.BatchStepJournal
     WHERE RunId = @p_runId
       AND StepCode IN (N'S14', N'S15', N'S16')
)
SELECT COUNT_BIG(*) AS MismatchCount
  FROM SummaryStatus
 WHERE StepStatus <> N'Succeeded'
HAVING
    (
        SELECT COUNT_BIG(*)
          FROM SummaryStatus
         WHERE StepStatus = N'Succeeded'
    ) BETWEEN 1 AND 2;
```

### V15 — 저널, 체크포인트 및 잠금 일관성 검증

`S01`과 `S02`는 RunId 이전 단계이므로 완료 게이트에서 제외한다.

```sql
-- SQL_VALIDATE_REQUIRED_STEP_COMPLETION
WITH RequiredSteps AS
(
    SELECT V.StepCode
      FROM
      (
          VALUES
              (N'S03'), (N'S04'), (N'S05'), (N'S06'), (N'S07'),
              (N'S08'), (N'S09'), (N'S10'), (N'S11'), (N'S12'),
              (N'S13'), (N'S14'), (N'S15'), (N'S16'), (N'S17'),
              (N'S18'), (N'S19')
      ) AS V(StepCode)
)
SELECT R.StepCode
  FROM RequiredSteps AS R
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchCheckpoint AS C
            WHERE C.RunId = @p_runId
              AND C.StepCode = R.StepCode
              AND C.CheckpointStatus = N'Succeeded'
       );
```

```sql
-- SQL_VALIDATE_NO_WORK_AFTER_FAILURE
SELECT LaterStep.RunId,
       FailedStep.StepCode AS FailedStepCode,
       LaterStep.StepCode AS UnexpectedLaterStepCode,
       LaterStep.StepStatus
  FROM batch.BatchStepJournal AS FailedStep
  JOIN batch.BatchStepJournal AS LaterStep
    ON LaterStep.RunId = FailedStep.RunId
   AND LaterStep.StepCode > FailedStep.StepCode
 WHERE FailedStep.RunId = @p_runId
   AND FailedStep.StepStatus = N'Failed'
   AND LaterStep.StepStatus IN (N'Running', N'Succeeded');
```

```sql
-- SQL_VALIDATE_RELEASED_LOCK
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc,
       ReleasedAtUtc
  FROM batch.BatchRunLock
 WHERE JobName = N'POQSettleBatch16'
   AND BatchYmd = @p_ymd
   AND OwnerRunId = @p_runId
   AND LockStatus <> N'Released';
```

### V16 — 레거시와 신규 실행 병행 대사

병행 실행의 통제합계는 동일한 `ControlName`으로 저장한 뒤 두 실행을 키 기준으로 비교한다.

```sql
-- SQL_COMPARE_REFERENCE_AND_CANDIDATE_CONTROL_TOTALS
WITH ReferenceTotals AS
(
    SELECT StepCode,
           ControlName,
           ControlValue
      FROM batch.BatchControlTotal
     WHERE RunId = @p_referenceRunId
),
CandidateTotals AS
(
    SELECT StepCode,
           ControlName,
           ControlValue
      FROM batch.BatchControlTotal
     WHERE RunId = @p_candidateRunId
)
SELECT COALESCE(R.StepCode, C.StepCode) AS StepCode,
       COALESCE(R.ControlName, C.ControlName) AS ControlName,
       R.ControlValue AS ReferenceValue,
       C.ControlValue AS CandidateValue,
       ISNULL(C.ControlValue, 0) - ISNULL(R.ControlValue, 0) AS DifferenceValue
  FROM ReferenceTotals AS R
  FULL OUTER JOIN CandidateTotals AS C
    ON C.StepCode = R.StepCode
   AND C.ControlName = R.ControlName
 WHERE ISNULL(R.ControlValue, 0) <> ISNULL(C.ControlValue, 0);
```

대사 결과는 다음 범주로 분류한다.

1. 업무 로직 이행 결함
2. `NOLOCK` 제거와 `SNAPSHOT` 적용에 따른 관찰 시점 차이
3. 원천 데이터 동시 변경
4. 레거시의 비결정적 중복 조인
5. 의도된 오류 탐지 강화
6. SQL과 C# 숫자 의미 차이
7. 검증 SQL 집계 단위 오류

승인된 예외를 제외하고 `Error` 또는 `Critical` 검증의 `MismatchCount`가 하나라도 0보다 크면 `S20`을 실행하지 않는다.
