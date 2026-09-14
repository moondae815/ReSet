## 통합 데이터 정합성 검증 SQL 세트

### 1. 공통 검증 규약

모든 검증 SQL은 데이터 조회를 포함해 SNAPSHOT 격리 의무를 만족해야 하며 `NOLOCK`을 사용하지 않는다.

공통 바인딩은 다음과 같다.

- `@p_runId BIGINT`
- `@p_businessYmd CHAR(8)`
- `@p_stepCode NVARCHAR(10)`
- 필요한 경우 `@p_fromYmd CHAR(8)`, `@p_toYmd CHAR(8)`

판정 규칙은 다음과 같다.

- 이상 조회형 SQL은 정상일 때 0건을 반환한다.
- 집계 검증은 기대 집계와 실제 집계를 각각 독립 CTE 또는 독립 스칼라 서브쿼리로 계산한다.
- 두 집계를 `CROSS JOIN`으로 비교하지 않는다.
- 금액은 `DECIMAL(38,4)` 또는 원본 컬럼 타입에 맞춰 정확 비교한다.
- 체크섬만으로 데이터 동등성을 확정하지 않는다.
- 필수 불일치는 `batch.BatchValidationIssue`에 `Error` 또는 `Critical`로 기록한다.
- 검증 결과 외에는 비즈니스 테이블을 변경하지 않는다.

### 2. V00 — 필수 데이터베이스와 객체 검증

```sql
SELECT N'DATABASE' AS ObjectType, V.ObjectName
FROM
(
    VALUES
        (N'SETTLE_POQ_DB'),
        (N'PaymentDB'),
        (N'SETTLE_CARD_DB'),
        (N'PLCardDB')
) AS V(ObjectName)
WHERE DB_ID(V.ObjectName) IS NULL

UNION ALL

SELECT N'BATCH_TABLE', V.ObjectName
FROM
(
    VALUES
        (N'batch.BatchRun'),
        (N'batch.BatchRunLock'),
        (N'batch.BatchStepJournal'),
        (N'batch.BatchCheckpoint'),
        (N'batch.BatchValidationIssue'),
        (N'batch.BatchControlTotal')
) AS V(ObjectName)
WHERE OBJECT_ID(V.ObjectName, N'U') IS NULL;
```

```sql
SELECT V.ObjectName
FROM
(
    VALUES
        (N'SETTLE_POQ_DB.dbo.TSettleMst'),
        (N'SETTLE_POQ_DB.dbo.TPGSettleRate'),
        (N'SETTLE_POQ_DB.dbo.TClientSettleRate'),
        (N'SETTLE_POQ_DB.dbo.TSettleByTX'),
        (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX'),
        (N'SETTLE_POQ_DB.dbo.TSettleByIN'),
        (N'SETTLE_POQ_DB.dbo.TSettleByOUT'),
        (N'SETTLE_POQ_DB.dbo.TStatPGCollect'),
        (N'SETTLE_POQ_DB.dbo.TSettleMiss')
) AS V(ObjectName)
WHERE OBJECT_ID(V.ObjectName, N'U') IS NULL;
```

### 3. V01 — 현재 실행 잠금 소유권 검증

정상일 때 1행이 반환되어야 한다.

```sql
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus,
       AcquiredAtUtc,
       HeartbeatAtUtc
FROM batch.BatchRunLock
WHERE JobName = N'POQSettleBatch11'
  AND BatchYmd = @p_businessYmd
  AND OwnerRunId = @p_runId
  AND LockStatus = N'Held';
```

다른 실행이 보유한 잠금은 정상일 때 0건이어야 한다.

```sql
SELECT JobName,
       BatchYmd,
       OwnerRunId,
       LockStatus
FROM batch.BatchRunLock
WHERE JobName = N'POQSettleBatch11'
  AND BatchYmd = @p_businessYmd
  AND OwnerRunId <> @p_runId
  AND LockStatus = N'Held';
```

### 4. V10 — 요율 스냅샷 중복 키 검증

```sql
SELECT N'TPGSettleRate' AS TargetName,
       YMD,
       PGNAME,
       MALLID,
       VERSION,
       COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate
WHERE YMD = @p_businessYmd
GROUP BY YMD, PGNAME, MALLID, VERSION
HAVING COUNT_BIG(*) > 1

UNION ALL

SELECT N'TClientSettleRate',
       YMD,
       CLIENTID,
       PGNAME,
       MALLID,
       VERSION,
       COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate
WHERE YMD = @p_businessYmd
GROUP BY YMD, CLIENTID, PGNAME, MALLID, VERSION
HAVING COUNT_BIG(*) > 1;
```

특화 요율 테이블도 실제 DDL에서 확인된 키 조합으로 같은 검증을 수행한다.

```sql
SELECT N'TPGSettleRate4Extra' AS TargetName,
       YMD,
       PGName,
       MallID,
       COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
WHERE YMD = @p_businessYmd
GROUP BY YMD, PGName, MallID
HAVING COUNT_BIG(*) > 1

UNION ALL

SELECT N'TClientSettleRate4Extra',
       YMD,
       ClientID + N'|' + PGName,
       MallID,
       COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
WHERE YMD = @p_businessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1

UNION ALL

SELECT N'TClientSettleRate4MobileCo',
       YMD,
       ClientID + N'|' + PGName,
       MallID,
       COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
WHERE YMD = @p_businessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1;
```

### 5. V11 — 원장 입력의 요율 누락 검증

```sql
SELECT A.CLIENTID,
       A.PGNAME,
       A.MALLID,
       COUNT_BIG(*) AS MissingRateSourceCount
FROM PaymentDB.dbo.TTxMst AS A
LEFT JOIN SETTLE_POQ_DB.dbo.TPGSettleRate AS B
  ON B.YMD = A.YMD
 AND B.PGNAME = A.PGNAME
 AND B.MALLID = A.MALLID
LEFT JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS C
  ON C.YMD = A.YMD
 AND C.CLIENTID = A.CLIENTID
 AND C.PGNAME = A.PGNAME
 AND C.MALLID = A.MALLID
WHERE A.YMD = @p_businessYmd
  AND (B.PGNAME IS NULL OR C.CLIENTID IS NULL)
GROUP BY A.CLIENTID, A.PGNAME, A.MALLID;
```

### 6. V20 — 원장 상태별 통제 합계

```sql
SELECT USESTATE,
       COUNT_BIG(*) AS LedgerCount,
       CAST(SUM(ISNULL(TXAMT, 0)) AS DECIMAL(38,4)) AS TxAmt,
       CAST(SUM(ISNULL(CLCOMM, 0)) AS DECIMAL(38,4)) AS CLComm,
       CAST(SUM(ISNULL(CLVT, 0)) AS DECIMAL(38,4)) AS CLVat,
       CAST(SUM(ISNULL(PGCOMM, 0)) AS DECIMAL(38,4)) AS PGComm,
       CAST(SUM(ISNULL(PGVT, 0)) AS DECIMAL(38,4)) AS PGVat,
       CAST(SUM(ISNULL(POQINCOME, 0)) AS DECIMAL(38,4)) AS POQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
GROUP BY USESTATE
ORDER BY USESTATE;
```

S06의 세 `UNION ALL` 브랜치는 최소한 다음 상태값과 대조한다.

- 전체 거래: `USESTATE=0`
- 부분취소: `USESTATE=2`
- 환불: `USESTATE=3`
- S07 전체취소: `USESTATE=1`

### 7. V21 — 전체 취소 원거래 연결 검증

```sql
SELECT C.PLTID,
       C.CLIENTID,
       C.PGNAME,
       C.MALLID,
       C.YMD AS CancelYmd
FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
WHERE C.YMD = @p_businessYmd
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
SELECT C.PLTID,
       O.CLIENTID AS OriginalClientID,
       C.CLIENTID AS CancelClientID,
       O.PGNAME AS OriginalPGName,
       C.PGNAME AS CancelPGName,
       O.MALLID AS OriginalMallID,
       C.MALLID AS CancelMallID
FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
JOIN SETTLE_POQ_DB.dbo.TSettleMst AS O
  ON O.PLTID = C.PLTID
 AND O.USESTATE = 0
WHERE C.YMD = @p_businessYmd
  AND C.USESTATE = 1
  AND
  (
      O.CLIENTID <> C.CLIENTID
      OR O.PGNAME <> C.PGNAME
      OR O.MALLID <> C.MALLID
  );
```

### 8. V30 — 정산 금지 상태 전파 검증

```sql
SELECT O.PLTID,
       O.ID,
       O.OUTSTATE
FROM SETTLE_POQ_DB.dbo.TSettleMst AS O
WHERE O.USESTATE = 0
  AND EXISTS
      (
          SELECT 1
          FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
          WHERE C.YMD = @p_businessYmd
            AND C.PLTID = O.PLTID
            AND C.USESTATE = 1
            AND C.OUTSTATE = 9
      )
  AND O.OUTSTATE <> 9;
```

### 9. V31 — 고객 총액 산식 검증

```sql
SELECT ID,
       YMD,
       CLIENTID,
       PGNAME,
       MALLID,
       CLTOTAL,
       ISNULL(CLCOMM, 0)
       + ISNULL(CLVT, 0)
       + ISNULL(CLETC, 0)
       + ISNULL(CLINTCOMM, 0) AS ExpectedCLTotal
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND ISNULL(CLTOTAL, 0) <>
      ISNULL(CLCOMM, 0)
      + ISNULL(CLVT, 0)
      + ISNULL(CLETC, 0)
      + ISNULL(CLINTCOMM, 0);
```

### 10. V32 — PG 총액과 POQ 수익 검증

```sql
SELECT ID,
       YMD,
       PGNAME,
       PGTOTAL,
       ISNULL(PGCOMM, 0)
       + ISNULL(PGVT, 0)
       + ISNULL(PGETC, 0)
       + CASE
             WHEN ISNULL(PGINTREALCOMM, 0) = 0
             THEN ISNULL(PGINTEXPCOMM, 0)
             ELSE ISNULL(PGINTREALCOMM, 0)
         END AS ExpectedPGTotal,
       POQINCOME,
       ISNULL(CLTOTAL, 0) - ISNULL(PGTOTAL, 0) AS ExpectedPOQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND
  (
      ISNULL(PGTOTAL, 0) <>
          ISNULL(PGCOMM, 0)
          + ISNULL(PGVT, 0)
          + ISNULL(PGETC, 0)
          + CASE
                WHEN ISNULL(PGINTREALCOMM, 0) = 0
                THEN ISNULL(PGINTEXPCOMM, 0)
                ELSE ISNULL(PGINTREALCOMM, 0)
            END
      OR ISNULL(POQINCOME, 0) <>
         ISNULL(CLTOTAL, 0) - ISNULL(PGTOTAL, 0)
  );
```

### 11. V33 — SQL 반올림 및 절사 경계값 검증

```sql
WITH Cases AS
(
    SELECT CAST(12.5 AS DECIMAL(10,2)) AS InputValue
    UNION ALL SELECT CAST(-12.5 AS DECIMAL(10,2))
    UNION ALL SELECT CAST(12.9 AS DECIMAL(10,2))
    UNION ALL SELECT CAST(-12.9 AS DECIMAL(10,2))
)
SELECT InputValue,
       ROUND(InputValue, 0, 0) AS SqlRounded,
       ROUND(InputValue, 0, 1) AS SqlTruncated,
       CAST(InputValue AS INT) AS SqlIntCast
FROM Cases;
```

이 결과를 C# 호환 금액 정책의 고정 테스트 결과와 비교한다. 음수 `CAST` 결과가 0 방향 절사인지와 `.5` 반올림이 은행가 반올림으로 바뀌지 않았는지를 필수 확인한다.

### 12. V40 — 수납 상태 및 일자 검증

```sql
SELECT ID,
       CLIENTID,
       PGNAME,
       MALLID,
       INSTATE,
       INYMD
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND
  (
      INSTATE = 1 AND ISNULL(INYMD, '') = ''
      OR INSTATE = 0 AND ISNULL(INYMD, '') <> ''
  );
```

### 13. V41 — 지급 상태 및 일자 검증

```sql
SELECT ID,
       CLIENTID,
       PGNAME,
       MALLID,
       OUTSTATE,
       OUTYMD,
       EDIReqYmd
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND
  (
      OUTSTATE = 2 AND ISNULL(OUTYMD, '') = ''
      OR OUTSTATE = 9 AND ISNULL(OUTYMD, '') <> ''
  );
```

### 14. V50 — 일반 추가 정산 범위 검증

```sql
WITH RequestRange AS
(
    SELECT MIN(ReqYMD) AS MinReqYmd
    FROM PaymentDB.dbo.TExtraSettleIn
    WHERE ResYMD = @p_businessYmd
      AND ResultCode = '00'
      AND RefundTxType <> 1
)
SELECT
    (SELECT MinReqYmd FROM RequestRange) AS ExpectedMinReqYmd,
    MIN(YMD) AS ActualMinLedgerYmd,
    MAX(YMD) AS ActualMaxLedgerYmd,
    COUNT_BIG(*) AS ActualExtraLedgerCount
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE ProcYMD = @p_businessYmd
  AND ExtraSettleFlag = 1;
```

```sql
SELECT ProcYMD,
       ExtraSettleFlag,
       COUNT_BIG(*) AS LedgerCount,
       SUM(ISNULL(ExtraTxAmt, 0)) AS ExtraTxAmt,
       SUM(ISNULL(CLTOTAL, 0)) AS CLTotal,
       SUM(ISNULL(PGTOTAL, 0)) AS PGTotal
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE ProcYMD = @p_businessYmd
  AND ExtraSettleFlag = 1
GROUP BY ProcYMD, ExtraSettleFlag;
```

### 15. V51 — S11과 S12 중복 후보 검증

```sql
SELECT E.PLTID,
       E.CLIENTID,
       E.PGNAME,
       E.YMD,
       COUNT_BIG(*) AS LedgerDuplicateCount
FROM SETTLE_POQ_DB.dbo.TSettleMst AS E
WHERE E.ProcYMD = @p_businessYmd
  AND E.ExtraSettleFlag = 1
GROUP BY E.PLTID, E.CLIENTID, E.PGNAME, E.YMD
HAVING COUNT_BIG(*) > 1;
```

### 16. V60 — 원장 대 핵심 거래 요약 양방향 검증

```sql
WITH Expected AS
(
    SELECT YMD,
           AYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           USESTATE,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           COUNT_BIG(*) AS TXCNT,
           SUM(ISNULL(TXAMT, 0)) AS TXAMT,
           SUM(ISNULL(CLCOMM, 0)) AS CLCOMM,
           SUM(ISNULL(CLVT, 0)) AS CLVT,
           SUM(ISNULL(CLTOTAL, 0)) AS CLTOTAL,
           SUM(ISNULL(PGCOMM, 0)) AS PGCOMM,
           SUM(ISNULL(PGVT, 0)) AS PGVT,
           SUM(ISNULL(PGTOTAL, 0)) AS PGTOTAL,
           SUM(ISNULL(POQINCOME, 0)) AS POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
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
           USESTATE,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           CAST(TXCNT AS BIGINT) AS TXCNT,
           TXAMT,
           CLCOMM,
           CLVT,
           CLTOTAL,
           PGCOMM,
           PGVT,
           PGTOTAL,
           POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_businessYmd
)
SELECT N'ExpectedMinusActual' AS DifferenceSide, *
FROM Expected
EXCEPT
SELECT N'ExpectedMinusActual', *
FROM Actual;
```

역방향도 별도로 실행한다.

```sql
WITH Expected AS
(
    SELECT YMD,
           AYMD,
           CLIENTID,
           PGNAME,
           MALLID,
           SERVICENAME,
           PRODUCTNAME,
           USESTATE,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           COUNT_BIG(*) AS TXCNT,
           SUM(ISNULL(TXAMT, 0)) AS TXAMT,
           SUM(ISNULL(CLCOMM, 0)) AS CLCOMM,
           SUM(ISNULL(CLVT, 0)) AS CLVT,
           SUM(ISNULL(CLTOTAL, 0)) AS CLTOTAL,
           SUM(ISNULL(PGCOMM, 0)) AS PGCOMM,
           SUM(ISNULL(PGVT, 0)) AS PGVT,
           SUM(ISNULL(PGTOTAL, 0)) AS PGTOTAL,
           SUM(ISNULL(POQINCOME, 0)) AS POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
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
           USESTATE,
           CompanySalesType,
           ProcYMD,
           ExtraSettleFlag,
           CAST(TXCNT AS BIGINT) AS TXCNT,
           TXAMT,
           CLCOMM,
           CLVT,
           CLTOTAL,
           PGCOMM,
           PGVT,
           PGTOTAL,
           POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_businessYmd
)
SELECT N'ActualMinusExpected' AS DifferenceSide, *
FROM Actual
EXCEPT
SELECT N'ActualMinusExpected', *
FROM Expected;
```

같은 방식으로 다음 필터와 그룹 키를 적용한다.

- `TPartialCancelByTX`: `YMD=@p_businessYmd AND USESTATE=2`, 그룹 키에 `PLTID` 포함
- `TSettleByIN`: `YMD=@p_businessYmd AND INSTATE=1`, 그룹 키에 `INYMD` 포함
- `TSettleByOUT`: `YMD=@p_businessYmd AND OUTSTATE IN (2,9)`, 그룹 키에 `INYMD`, `OUTYMD`, `OUTSTATE`, `SettleCurrency` 포함

### 17. V61 — 수기 매입 요약 누락 검증

```sql
WITH ExpectedGroups AS
(
    SELECT A.OutYMD,
           A.ClientID,
           A.PGName
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt AS B
      ON A.ClientID = B.ClientID
    WHERE ISNULL(A.EDIReqYmd, '') = @p_businessYmd
      AND B.AcqType = 1
      AND A.OutState IN (2,9)
    GROUP BY A.OutYMD, A.ClientID, A.PGName
)
SELECT E.OutYMD,
       E.ClientID,
       E.PGName
FROM ExpectedGroups AS E
WHERE NOT EXISTS
      (
          SELECT 1
          FROM SETTLE_POQ_DB.dbo.TSettleByOUT AS O
          WHERE O.OutYMD = E.OutYMD
            AND O.ClientID = E.ClientID
            AND O.PGName = E.PGName
      );
```

### 18. V62 — 추가 정산 요약 통제 합계 비교

집계는 각 측에서 독립적으로 수행하고 스칼라 서브쿼리로 비교한다.

```sql
SELECT
    CAST
    (
        (
            SELECT ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
            FROM SETTLE_POQ_DB.dbo.TSettleMst
            WHERE ProcYMD = @p_businessYmd
              AND ExtraSettleFlag = 1
        )
        AS DECIMAL(38,4)
    ) AS ExpectedTxAmt,
    CAST
    (
        (
            SELECT ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
            FROM SETTLE_POQ_DB.dbo.TSettleByTX
            WHERE ProcYMD = @p_businessYmd
              AND ExtraSettleFlag = 1
        )
        AS DECIMAL(38,4)
    ) AS ActualTxAmt,
    CASE
        WHEN
        (
            SELECT ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
            FROM SETTLE_POQ_DB.dbo.TSettleMst
            WHERE ProcYMD = @p_businessYmd
              AND ExtraSettleFlag = 1
        )
        =
        (
            SELECT ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
            FROM SETTLE_POQ_DB.dbo.TSettleByTX
            WHERE ProcYMD = @p_businessYmd
              AND ExtraSettleFlag = 1
        )
        THEN 1 ELSE 0
    END AS PassFlag;
```

### 19. V63 — 사후 취소 요약 중복 검증

```sql
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
       COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TSettleByOUT
WHERE YMD = @p_businessYmd
GROUP BY YMD,
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
         ExtraSettleFlag
HAVING COUNT_BIG(*) > 1;
```

### 20. V70 — PG 수납 통계 전체 합계 검증

양쪽 집계를 독립적으로 계산한다.

```sql
SELECT
    (
        SELECT ISNULL(SUM(CAST(TXAMT - PGTOTAL AS DECIMAL(38,4))), 0)
        FROM SETTLE_POQ_DB.dbo.TSettleMst
        WHERE INYMD = @p_businessYmd
          AND INSTATE = 1
    )
    +
    (
        SELECT ISNULL(SUM(CAST(CLCOLLECTAMT AS DECIMAL(38,4))), 0)
        FROM SETTLE_POQ_DB.dbo.TTArsPGCollect
        WHERE COLLECTYMD = @p_businessYmd
    )
    +
    (
        SELECT ISNULL(SUM(CAST(CLCOLLECTAMT AS DECIMAL(38,4))), 0)
        FROM SETTLE_POQ_DB.dbo.TBArsPGCollect
        WHERE COLLECTYMD = @p_businessYmd
    ) AS ExpectedCollectAmt,
    (
        SELECT ISNULL(SUM(CAST(COLLECTAMT AS DECIMAL(38,4))), 0)
        FROM SETTLE_POQ_DB.dbo.TStatPGCollect
        WHERE INYMD = @p_businessYmd
    ) AS ActualCollectAmt;
```

그룹별 비교는 `INYMD`, 소문자 `CLIENTID`, `PGNAME`, `MALLID`를 공통 키로 집계한 뒤 양방향 `EXCEPT`로 수행한다.

### 21. V71 — 미정산 원장 대조

```sql
WITH Expected AS
(
    SELECT A.ClientID,
           A.OutYMD,
           CAST(SUM(A.CLTotal) AS DECIMAL(38,4)) AS ExpectedCLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
      ON A.YMD = B.YMD
     AND A.ClientID = B.ClientID
     AND A.PGName = B.PGName
     AND A.MallID = B.MallID
    JOIN SETTLE_POQ_DB.dbo.TClient AS C
      ON A.ClientID = C.ClientID
    WHERE A.YMD = @p_businessYmd
      AND A.OutState = 2
      AND ISNULL(B.TaxFGBill, 2) = 1
    GROUP BY A.ClientID, A.OutYMD
),
Actual AS
(
    SELECT ClientID,
           OutYMD,
           CAST(SUM(CLSettleAmt) AS DECIMAL(38,4)) AS ActualCLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE OutState = 2
      AND ISNULL(IssueType, 0) = 15
    GROUP BY ClientID, OutYMD
)
SELECT COALESCE(E.ClientID, A.ClientID) AS ClientID,
       COALESCE(E.OutYMD, A.OutYMD) AS OutYMD,
       ISNULL(E.ExpectedCLSettleAmt, 0) AS ExpectedValue,
       ISNULL(A.ActualCLSettleAmt, 0) AS ActualValue,
       ISNULL(E.ExpectedCLSettleAmt, 0)
       - ISNULL(A.ActualCLSettleAmt, 0) AS Difference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.ClientID = E.ClientID
 AND A.OutYMD = E.OutYMD
WHERE ISNULL(E.ExpectedCLSettleAmt, 0)
   <> ISNULL(A.ActualCLSettleAmt, 0);
```

ID 충돌 검증은 정상일 때 0건이다.

```sql
SELECT ID,
       COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TSettleMiss
GROUP BY ID
HAVING COUNT_BIG(*) > 1;
```

### 22. V80 — 체크포인트와 저널 일관성 검증

```sql
SELECT C.RunId,
       C.StepCode,
       C.CheckpointStatus,
       J.StepStatus,
       C.CompletedAtUtc AS CheckpointCompletedAtUtc,
       J.CompletedAtUtc AS JournalCompletedAtUtc
FROM batch.BatchCheckpoint AS C
LEFT JOIN batch.BatchStepJournal AS J
  ON J.RunId = C.RunId
 AND J.StepCode = C.StepCode
WHERE C.RunId = @p_runId
  AND
  (
      C.CheckpointStatus = N'Succeeded'
      AND
      (
          J.StepStatus IS NULL
          OR J.StepStatus <> N'Succeeded'
          OR J.CompletedAtUtc IS NULL
      )
  );
```

S14~S16은 정상일 때 모두 같은 완료 상태여야 한다.

```sql
SELECT
    SUM(CASE WHEN StepCode IN (N'S14', N'S15', N'S16')
                  AND CheckpointStatus = N'Succeeded'
             THEN 1 ELSE 0 END) AS SucceededCount,
    SUM(CASE WHEN StepCode IN (N'S14', N'S15', N'S16')
             THEN 1 ELSE 0 END) AS ExistingCount
FROM batch.BatchCheckpoint
WHERE RunId = @p_runId
HAVING SUM(CASE WHEN StepCode IN (N'S14', N'S15', N'S16')
                     AND CheckpointStatus = N'Succeeded'
                THEN 1 ELSE 0 END) NOT IN (0, 3);
```

### 23. V81 — 실패 주입 검증 지점

테스트 환경에서는 다음 지점에서 애플리케이션이 의도적으로 실패를 발생시키고 대상 데이터와 체크포인트를 검증한다.

- S05의 다섯 번째 요율 대상 실행 직전
- S06의 날짜 범위 삭제 후 삽입 전
- S08의 18개 UPDATE 중간
- S11과 S12의 삭제 후 삽입 전
- S15 성공 후 S16 시작 전
- S19의 `TSettleMiss` 일부 변경 후

각 테스트에서 확인할 조건은 다음과 같다.

- 현재 논리 트랜잭션의 대상 데이터가 모두 롤백되었다.
- 이전 단계의 커밋 데이터는 유지되었다.
- 실패 단계 체크포인트는 `Pending`이다.
- 실패 저널에 정확한 문장 식별자와 원본 오류 코드가 기록되었다.
- S14~S16 실패 시 세 단계 중 어느 것도 `Succeeded` 체크포인트가 아니다.
- 단일 트랜잭션 롤백 단계에는 섀도 또는 추가 보상 DELETE가 실행되지 않았다.

### 24. V90 — 최종 게시 게이트

```sql
WITH RequiredSteps AS
(
    SELECT StepCode
    FROM
    (
        VALUES
            (N'S05'), (N'S06'), (N'S07'), (N'S08'), (N'S09'),
            (N'S10'), (N'S11'), (N'S12'), (N'S13'), (N'S14'),
            (N'S15'), (N'S16'), (N'S17'), (N'S18'), (N'S19'),
            (N'S20')
    ) AS S(StepCode)
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

다음 SQL은 필수 검증 오류가 없는지 확인한다. 정상일 때 0건이다.

```sql
SELECT RunId,
       StepCode,
       IssueCode,
       Severity,
       ExpectedValue,
       ActualValue,
       DetectedAtUtc
FROM batch.BatchValidationIssue
WHERE RunId = @p_runId
  AND Severity IN (N'Error', N'Critical');
```

성공 게시 허용 여부는 두 검증 모두 통과하고 S14~S16 체크포인트가 모두 `Succeeded`일 때만 참이다.

### 25. V91 — 동결 통제 합계 대 최종 원장 비교

각 통제값은 독립 스칼라 집계로 비교한다.

```sql
SELECT
    (
        SELECT ControlValue
        FROM batch.BatchControlTotal
        WHERE RunId = @p_runId
          AND StepCode = N'S13'
          AND ControlName = N'LedgerRowCount'
    ) AS ExpectedValue,
    CAST
    (
        (
            SELECT COUNT_BIG(*)
            FROM SETTLE_POQ_DB.dbo.TSettleMst
            WHERE YMD = @p_businessYmd
               OR ProcYMD = @p_businessYmd
        )
        AS DECIMAL(38,4)
    ) AS ActualValue;
```

```sql
SELECT
    (
        SELECT ControlValue
        FROM batch.BatchControlTotal
        WHERE RunId = @p_runId
          AND StepCode = N'S13'
          AND ControlName = N'LedgerPOQIncome'
    ) AS ExpectedValue,
    (
        SELECT CAST(ISNULL(SUM(POQINCOME), 0) AS DECIMAL(38,4))
        FROM SETTLE_POQ_DB.dbo.TSettleMst
        WHERE YMD = @p_businessYmd
           OR ProcYMD = @p_businessYmd
    ) AS ActualValue;
```

같은 패턴으로 다음 통제값을 비교한다.

- 원장 행 수
- `TXAMT`
- `ExtraTxAmt`
- `CLCOMM`, `CLVT`, `CLTOTAL`
- `PGCOMM`, `PGVT`, `PGTOTAL`
- `POQINCOME`
- 정상·취소·부분취소·환불·추가 정산별 행 수와 금액

### 26. 검증 이슈 기록 템플릿

검증 애플리케이션이 실패를 판정한 경우에만 실행한다.

```sql
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
    @p_runId,
    @p_stepCode,
    @p_issueCode,
    @p_severity,
    @p_expectedValue,
    @p_actualValue,
    SYSUTCDATETIME()
);
```

### 27. 최종 운영 보고 항목

최종 보고는 다음 데이터를 고정 형식으로 제공한다.

- `RunId`, `JobName`, `BatchYmd`, `RunStatus`
- 시작·종료 시각과 단계별 소요 시간
- 단계별 `StepStatus`와 `LegacyReturnCode`
- 정상·취소·부분취소·환불·추가 정산별 원장 행 수와 금액
- 네 요약 테이블의 행 수와 주요 금액
- PG 수납 통계 합계
- `TSettleMiss` 후취정산 합계
- S13 동결 합계와 S20 최종 합계 차이
- `batch.BatchValidationIssue`의 심각도별 건수
- 재시작 여부와 건너뛴 단계
- S14~S16 복합 트랜잭션의 원자적 완료 여부
- S22 잠금 해제 결과