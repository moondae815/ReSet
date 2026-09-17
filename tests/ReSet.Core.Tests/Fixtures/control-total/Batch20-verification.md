## 통합 데이터 정합성 검증 SQL 세트

### 공통 검증 규칙

- 모든 검증은 SNAPSHOT 격리 의무 아래 실행한다.
- 검증 SQL에서도 `NOLOCK`을 사용하지 않는다.
- 공통 값은 `@RunId BIGINT`, `@BusinessYmd CHAR(8)`로 전달한다.
- 테이블명은 리터럴로 적고 값은 모두 파라미터로 전달한다.
- 비교할 두 집계는 각각 독립 CTE 또는 독립 스칼라 서브쿼리에서 계산한다.
- 두 집계를 `CROSS JOIN`으로 결합하지 않는다.
- 그룹 대사는 동일한 비즈니스 키를 사용한 `FULL OUTER JOIN`으로 수행한다.
- 정수 금액은 허용 오차 없이 완전 일치해야 한다.
- 환율 기반 decimal 값만 원본 스케일에 근거한 명시적 허용 오차를 둘 수 있다.
- 검증 실패는 `batch.BatchValidationIssue`에 기록하고 `batch.BatchRun.RunStatus=Failed`로 표현한다.

### V01 업무일자 잠금 소유권 검증

```sql
SELECT
    N'V01_RUN_LOCK' AS MetricCode,
    CAST(CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END AS INT) AS PassFlag,
    CAST(1 AS DECIMAL(38,4)) AS ExpectedValue,
    CAST(COUNT(*) AS DECIMAL(38,4)) AS ActualValue,
    CAST(COUNT(*) - 1 AS DECIMAL(38,4)) AS Difference
FROM batch.BatchRunLock
WHERE JobName = N'POQSettleBatch20'
  AND BatchYmd = @BusinessYmd
  AND OwnerRunId = @RunId
  AND LockStatus = N'Held';
```

동일 일자의 다른 활성 소유자도 별도로 검출한다.

```sql
SELECT
    OwnerRunId,
    LockStatus,
    AcquiredAtUtc,
    HeartbeatAtUtc
FROM batch.BatchRunLock
WHERE JobName = N'POQSettleBatch20'
  AND BatchYmd = @BusinessYmd
  AND LockStatus = N'Held'
  AND OwnerRunId <> @RunId;
```

### V02 원본 지급 완료 보호 조건 검증

S03과 S04의 선행 보호 조건은 동일하지만 각 단계가 호출될 때 독립적으로 실행해야 한다.

```sql
SELECT
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM SETTLE_POQ_DB.dbo.TSettleMst
        WHERE YMD = @BusinessYmd
          AND OutState IN (1,5)
          AND OutYMD IS NOT NULL
    )
    THEN 0 ELSE 1 END AS PassFlag;
```

S09는 최소 요청일을 먼저 계산한 뒤 원본 범위를 그대로 검사한다.

```sql
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
FROM PaymentDB.dbo.TExtraSettleIn
WHERE ResYMD = @BusinessYmd
  AND ResultCode = '00'
  AND RefundTxType <> 1;

SELECT
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM SETTLE_POQ_DB.dbo.TSettleMst
        WHERE ProcYMD = @BusinessYmd
          AND YMD >= @v_strReqYMD
          AND OutState IN (1,5)
          AND OutYMD IS NOT NULL
          AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
          AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
          AND ExtraSettleFlag = 1
    )
    THEN 0 ELSE 1 END AS PassFlag;
```

### V10 일별 요율 스냅샷 중복 검사

```sql
SELECT N'TPGSettleRate' AS TargetTable, YMD, PGNAME, MALLID, VERSION, COUNT(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate
WHERE YMD = @BusinessYmd
GROUP BY YMD, PGNAME, MALLID, VERSION
HAVING COUNT(*) > 1

UNION ALL

SELECT N'TClientSettleRate', YMD, CLIENTID, PGNAME, MALLID, VERSION, COUNT(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate
WHERE YMD = @BusinessYmd
GROUP BY YMD, CLIENTID, PGNAME, MALLID, VERSION
HAVING COUNT(*) > 1;
```

추가정산 요율과 통신사 요율은 각 테이블의 실제 계약 식별키로 검사한다.

```sql
SELECT N'TPGSettleRate4Extra' AS TargetTable, YMD, PGName, MallID, COUNT(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
WHERE YMD = @BusinessYmd
GROUP BY YMD, PGName, MallID
HAVING COUNT(*) > 1

UNION ALL

SELECT N'TClientSettleRate4Extra', YMD, ClientID, PGName, MallID, COUNT(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
WHERE YMD = @BusinessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT(*) > 1

UNION ALL

SELECT N'TClientSettleRate4MobileCo', YMD, ClientID, PGName, MallID, COUNT(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
WHERE YMD = @BusinessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT(*) > 1;
```

### V20 정산원장 모집단별 통제 합계

```sql
SELECT
    CASE
        WHEN ISNULL(ExtraSettleFlag,0) = 1 AND PGName = 'PLCard'
            THEN N'PLCARD_EXTRA'
        WHEN ISNULL(ExtraSettleFlag,0) = 1
            THEN N'GENERAL_EXTRA'
        WHEN UseState = 0
            THEN N'NORMAL'
        WHEN UseState = 1
            THEN N'FULL_CANCEL'
        WHEN UseState = 2
            THEN N'PARTIAL_CANCEL'
        WHEN UseState = 3
            THEN N'REFUND'
        ELSE N'OTHER'
    END AS PopulationCode,
    COUNT_BIG(*) AS RowCount,
    SUM(CAST(ISNULL(TxAmt,0) AS DECIMAL(38,4))) AS TxAmt,
    SUM(CAST(ISNULL(ExtraTxAmt,0) AS DECIMAL(38,4))) AS ExtraTxAmt,
    SUM(CAST(ISNULL(CLComm,0) AS DECIMAL(38,4))) AS CLComm,
    SUM(CAST(ISNULL(CLVT,0) AS DECIMAL(38,4))) AS CLVT,
    SUM(CAST(ISNULL(CLTotal,0) AS DECIMAL(38,4))) AS CLTotal,
    SUM(CAST(ISNULL(PGComm,0) AS DECIMAL(38,4))) AS PGComm,
    SUM(CAST(ISNULL(PGVT,0) AS DECIMAL(38,4))) AS PGVT,
    SUM(CAST(ISNULL(PGTotal,0) AS DECIMAL(38,4))) AS PGTotal,
    SUM(CAST(ISNULL(POQIncome,0) AS DECIMAL(38,4))) AS POQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @BusinessYmd
   OR ProcYMD = @BusinessYmd
GROUP BY
    CASE
        WHEN ISNULL(ExtraSettleFlag,0) = 1 AND PGName = 'PLCard'
            THEN N'PLCARD_EXTRA'
        WHEN ISNULL(ExtraSettleFlag,0) = 1
            THEN N'GENERAL_EXTRA'
        WHEN UseState = 0
            THEN N'NORMAL'
        WHEN UseState = 1
            THEN N'FULL_CANCEL'
        WHEN UseState = 2
            THEN N'PARTIAL_CANCEL'
        WHEN UseState = 3
            THEN N'REFUND'
        ELSE N'OTHER'
    END;
```

### V23 정산원장 회계식 검사

```sql
SELECT
    ID,
    YMD,
    ClientID,
    PGName,
    MallID,
    CLTotal,
    CLComm + CLVT + CLEtc + CLIntComm AS ExpectedCLTotal,
    PGTotal,
    PGComm + PGVT + PGEtc
        + CASE WHEN PGIntRealComm = 0 THEN PGIntExpComm ELSE PGIntRealComm END AS ExpectedPGTotal,
    POQIncome,
    CLTotal - PGTotal AS ExpectedPOQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @BusinessYmd
  AND
  (
       ISNULL(CLTotal,0)
           <> ISNULL(CLComm,0) + ISNULL(CLVT,0) + ISNULL(CLEtc,0) + ISNULL(CLIntComm,0)
    OR ISNULL(PGTotal,0)
           <> ISNULL(PGComm,0) + ISNULL(PGVT,0) + ISNULL(PGEtc,0)
              + CASE
                    WHEN ISNULL(PGIntRealComm,0) = 0
                    THEN ISNULL(PGIntExpComm,0)
                    ELSE ISNULL(PGIntRealComm,0)
                END
    OR ISNULL(POQIncome,0) <> ISNULL(CLTotal,0) - ISNULL(PGTotal,0)
  );
```

### V24 수금 및 지급 상태 날짜 검사

```sql
SELECT
    ID,
    YMD,
    InState,
    InYMD,
    OutState,
    OutYMD,
    EDIReqYmd,
    UseState,
    PGName
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @BusinessYmd
  AND
  (
       (InState = 1 AND ISNULL(InYMD,'') = '')
    OR (OutState IN (1,2,5) AND ISNULL(OutYMD,'') = '')
    OR (UseState = 3 AND InState <> 0)
  );
```

### V25 S11 원장 동결 합계 비교

각 집계는 독립적으로 계산하고 `ControlName`으로 비교한다.

```sql
WITH CurrentLedger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        SUM(CAST(ISNULL(TxAmt,0) AS DECIMAL(38,4))) AS LedgerTxAmt,
        SUM(CAST(ISNULL(CLTotal,0) AS DECIMAL(38,4))) AS LedgerCLTotal,
        SUM(CAST(ISNULL(PGTotal,0) AS DECIMAL(38,4))) AS LedgerPGTotal,
        SUM(CAST(ISNULL(POQIncome,0) AS DECIMAL(38,4))) AS LedgerPOQIncome
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @BusinessYmd
       OR ProcYMD = @BusinessYmd
),
CurrentMetrics AS
(
    SELECT N'LedgerRowCount' AS ControlName,
           (SELECT LedgerRowCount FROM CurrentLedger) AS ControlValue
    UNION ALL
    SELECT N'LedgerTxAmt',
           (SELECT LedgerTxAmt FROM CurrentLedger)
    UNION ALL
    SELECT N'LedgerCLTotal',
           (SELECT LedgerCLTotal FROM CurrentLedger)
    UNION ALL
    SELECT N'LedgerPGTotal',
           (SELECT LedgerPGTotal FROM CurrentLedger)
    UNION ALL
    SELECT N'LedgerPOQIncome',
           (SELECT LedgerPOQIncome FROM CurrentLedger)
),
FrozenMetrics AS
(
    SELECT ControlName, ControlValue
    FROM batch.BatchControlTotal
    WHERE RunId = @RunId
      AND StepCode = N'S11'
)
SELECT
    COALESCE(F.ControlName, C.ControlName) AS MetricCode,
    F.ControlValue AS ExpectedValue,
    C.ControlValue AS ActualValue,
    ISNULL(C.ControlValue,0) - ISNULL(F.ControlValue,0) AS Difference,
    CASE WHEN F.ControlValue = C.ControlValue THEN 1 ELSE 0 END AS PassFlag
FROM FrozenMetrics AS F
FULL OUTER JOIN CurrentMetrics AS C
  ON C.ControlName = F.ControlName
WHERE F.ControlValue IS NULL
   OR C.ControlValue IS NULL
   OR F.ControlValue <> C.ControlValue;
```

### V30 TSettleMiss 식별자 및 자연키 중복 검사

```sql
SELECT
    ID,
    COUNT(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TSettleMiss
GROUP BY ID
HAVING COUNT(*) > 1;
```

```sql
SELECT
    ClientID,
    OutYMD,
    IssueType,
    OutState,
    COUNT(*) AS DuplicateCount,
    SUM(CAST(ISNULL(CLSettleAmt,0) AS DECIMAL(38,4))) AS CLSettleAmt
FROM SETTLE_POQ_DB.dbo.TSettleMiss
WHERE IssueType = 15
GROUP BY ClientID, OutYMD, IssueType, OutState
HAVING COUNT(*) > 1;
```

사후 청구 원장과 오정산 테이블의 합계는 독립 집계 후 그룹 키로 대사한다.

```sql
WITH Expected AS
(
    SELECT
        A.ClientID,
        A.OutYMD,
        SUM(CAST(ISNULL(A.CLTotal,0) AS DECIMAL(38,4))) AS CLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
      ON B.YMD = A.YMD
     AND B.ClientID = A.ClientID
     AND B.PGName = A.PGName
     AND B.MallID = A.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
      ON C.ClientID = A.ClientID
    WHERE A.YMD = @BusinessYmd
      AND A.OutState = 2
      AND ISNULL(B.TaxFGBill,2) = 1
    GROUP BY A.ClientID, A.OutYMD
),
Actual AS
(
    SELECT
        ClientID,
        OutYMD,
        SUM(CAST(ISNULL(CLSettleAmt,0) AS DECIMAL(38,4))) AS CLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE OutState = 2
      AND ISNULL(IssueType,0) = 15
    GROUP BY ClientID, OutYMD
)
SELECT
    COALESCE(E.ClientID, A.ClientID) AS ClientID,
    COALESCE(E.OutYMD, A.OutYMD) AS OutYMD,
    E.CLSettleAmt AS ExpectedValue,
    A.CLSettleAmt AS ActualValue,
    ISNULL(A.CLSettleAmt,0) - ISNULL(E.CLSettleAmt,0) AS Difference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.ClientID = E.ClientID
 AND A.OutYMD = E.OutYMD
WHERE E.CLSettleAmt IS NULL
   OR A.CLSettleAmt IS NULL
   OR E.CLSettleAmt <> A.CLSettleAmt;
```

### V31 PG 수금통계 대사

세 원천 브랜치의 컬럼 목록과 순서를 동일하게 유지한다.

```sql
WITH SourceBranches AS
(
    SELECT
        INYMD,
        LOWER(CLIENTID) AS CLIENTID,
        LOWER(PGNAME) AS PGNAME,
        LOWER(MALLID) AS MALLID,
        SUM(TXAMT - PGTOTAL) AS COLLECTAMT,
        SUM(PGCOMM + PGETC + ISNULL(PGINTREALCOMM,0)) AS PGCOMM,
        SUM(PGVT) AS PGVT,
        SUM
        (
            CASE
                WHEN PGNAME IN ('WOWCOIN','WOW_ARS','WOW_1588')
                THEN CASE WHEN OUTYMD >= @BusinessYmd THEN TXAMT END
                ELSE TXAMT
            END
        ) AS SETTLEWILLAMT,
        SUM
        (
            CASE WHEN OUTYMD < @BusinessYmd AND OUTSTATE = 1
                 THEN CLCOMM + CLETC + ISNULL(CLINTCOMM,0)
                 ELSE 0
            END
        ) AS AHEADSALESCOMM,
        SUM
        (
            CASE WHEN OUTYMD < @BusinessYmd AND OUTSTATE = 1
                 THEN CLVT ELSE 0
            END
        ) AS AHEADSALESVT,
        SUM
        (
            CASE WHEN OUTYMD < @BusinessYmd AND OUTSTATE = 1
                 THEN TXAMT - CLTOTAL ELSE 0
            END
        ) AS AHEADSETTLEAMT
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE INYMD = @BusinessYmd
      AND INSTATE = 1
    GROUP BY INYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)

    UNION ALL

    SELECT
        COLLECTYMD AS INYMD,
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
    WHERE COLLECTYMD = @BusinessYmd
    GROUP BY COLLECTYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)

    UNION ALL

    SELECT
        COLLECTYMD AS INYMD,
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
    WHERE COLLECTYMD = @BusinessYmd
    GROUP BY COLLECTYMD, LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID)
),
Expected AS
(
    SELECT
        INYMD,
        CLIENTID,
        PGNAME,
        MALLID,
        ISNULL(SUM(COLLECTAMT),0) AS COLLECTAMT,
        ISNULL(SUM(PGCOMM),0) AS PGCOMM,
        ISNULL(SUM(PGVT),0) AS PGVT,
        ISNULL(SUM(COLLECTAMT + PGCOMM + PGVT),0) AS LEFTSUMAMT,
        ISNULL(SUM(SETTLEWILLAMT),0) AS SETTLEWILLAMT,
        ISNULL(SUM(AHEADSALESCOMM),0) AS AHEADSALESCOMM,
        ISNULL(SUM(AHEADSALESVT),0) AS AHEADSALESVT,
        ISNULL(SUM(AHEADSETTLEAMT),0) AS AHEADSETTLEAMT,
        ISNULL(SUM(SETTLEWILLAMT),0)
          + ISNULL(SUM(AHEADSALESCOMM),0)
          + ISNULL(SUM(AHEADSALESVT),0)
          + ISNULL(SUM(AHEADSETTLEAMT),0) AS RIGHTSUMAMT
    FROM SourceBranches
    GROUP BY INYMD, CLIENTID, PGNAME, MALLID
),
Actual AS
(
    SELECT
        INYMD,
        LOWER(CLIENTID) AS CLIENTID,
        LOWER(PGNAME) AS PGNAME,
        LOWER(MALLID) AS MALLID,
        COLLECTAMT,
        PGCOMM,
        PGVT,
        LEFTSUMAMT,
        SETTLEWILLAMT,
        AHEADSALESCOMM,
        AHEADSALESVT,
        AHEADSETTLEAMT,
        RIGHTSUMAMT
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @BusinessYmd
)
SELECT
    COALESCE(E.INYMD, A.INYMD) AS INYMD,
    COALESCE(E.CLIENTID, A.CLIENTID) AS CLIENTID,
    COALESCE(E.PGNAME, A.PGNAME) AS PGNAME,
    COALESCE(E.MALLID, A.MALLID) AS MALLID,
    E.COLLECTAMT AS ExpectedCollectAmt,
    A.COLLECTAMT AS ActualCollectAmt,
    ISNULL(A.COLLECTAMT,0) - ISNULL(E.COLLECTAMT,0) AS CollectDifference,
    E.RIGHTSUMAMT AS ExpectedRightSumAmt,
    A.RIGHTSUMAMT AS ActualRightSumAmt,
    ISNULL(A.RIGHTSUMAMT,0) - ISNULL(E.RIGHTSUMAMT,0) AS RightDifference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.INYMD = E.INYMD
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
WHERE E.INYMD IS NULL
   OR A.INYMD IS NULL
   OR ISNULL(E.COLLECTAMT,0) <> ISNULL(A.COLLECTAMT,0)
   OR ISNULL(E.PGCOMM,0) <> ISNULL(A.PGCOMM,0)
   OR ISNULL(E.PGVT,0) <> ISNULL(A.PGVT,0)
   OR ISNULL(E.LEFTSUMAMT,0) <> ISNULL(A.LEFTSUMAMT,0)
   OR ISNULL(E.RIGHTSUMAMT,0) <> ISNULL(A.RIGHTSUMAMT,0);
```

### V40 거래 기준 요약 대사

```sql
WITH Expected AS
(
    SELECT
        YMD,
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
        SUM(CAST(ISNULL(TXAMT,0) AS DECIMAL(38,4))) AS TXAMT,
        SUM(CAST(ISNULL(CLTOTAL,0) AS DECIMAL(38,4))) AS CLTOTAL,
        SUM(CAST(ISNULL(PGTOTAL,0) AS DECIMAL(38,4))) AS PGTOTAL,
        SUM(CAST(ISNULL(POQINCOME,0) AS DECIMAL(38,4))) AS POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @BusinessYmd
    GROUP BY
        YMD, AYMD, CLIENTID, PGNAME, MALLID,
        SERVICENAME, PRODUCTNAME, USESTATE,
        CompanySalesType, ProcYMD, ExtraSettleFlag
),
Actual AS
(
    SELECT
        YMD,
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
        CAST(TXAMT AS DECIMAL(38,4)) AS TXAMT,
        CAST(CLTOTAL AS DECIMAL(38,4)) AS CLTOTAL,
        CAST(PGTOTAL AS DECIMAL(38,4)) AS PGTOTAL,
        CAST(POQINCOME AS DECIMAL(38,4)) AS POQINCOME
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @BusinessYmd
)
SELECT
    COALESCE(E.YMD, A.YMD) AS YMD,
    COALESCE(E.CLIENTID, A.CLIENTID) AS CLIENTID,
    COALESCE(E.PGNAME, A.PGNAME) AS PGNAME,
    E.TXCNT AS ExpectedCount,
    A.TXCNT AS ActualCount,
    ISNULL(A.TXCNT,0) - ISNULL(E.TXCNT,0) AS CountDifference,
    ISNULL(A.TXAMT,0) - ISNULL(E.TXAMT,0) AS TxAmtDifference,
    ISNULL(A.CLTOTAL,0) - ISNULL(E.CLTOTAL,0) AS CLTotalDifference,
    ISNULL(A.PGTOTAL,0) - ISNULL(E.PGTOTAL,0) AS PGTotalDifference,
    ISNULL(A.POQINCOME,0) - ISNULL(E.POQINCOME,0) AS POQIncomeDifference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.YMD = E.YMD
 AND A.AYMD = E.AYMD
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
 AND A.SERVICENAME = E.SERVICENAME
 AND A.PRODUCTNAME = E.PRODUCTNAME
 AND A.USESTATE = E.USESTATE
 AND ISNULL(A.CompanySalesType,4) = ISNULL(E.CompanySalesType,4)
 AND ISNULL(A.ProcYMD,'') = ISNULL(E.ProcYMD,'')
 AND ISNULL(A.ExtraSettleFlag,9) = ISNULL(E.ExtraSettleFlag,9)
WHERE E.YMD IS NULL
   OR A.YMD IS NULL
   OR E.TXCNT <> A.TXCNT
   OR E.TXAMT <> A.TXAMT
   OR E.CLTOTAL <> A.CLTOTAL
   OR E.PGTOTAL <> A.PGTOTAL
   OR E.POQINCOME <> A.POQINCOME;
```

### V41 부분취소 요약 대사

```sql
WITH Expected AS
(
    SELECT
        YMD,
        AYMD,
        PLTID,
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
        SUM(CAST(ISNULL(TXAMT,0) AS DECIMAL(38,4))) AS TXAMT,
        SUM(CAST(ISNULL(CLTOTAL,0) AS DECIMAL(38,4))) AS CLTOTAL,
        SUM(CAST(ISNULL(PGTOTAL,0) AS DECIMAL(38,4))) AS PGTOTAL
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @BusinessYmd
      AND USESTATE = 2
    GROUP BY
        YMD, AYMD, PLTID, CLIENTID, PGNAME, MALLID,
        SERVICENAME, PRODUCTNAME, USESTATE,
        CompanySalesType, ProcYMD, ExtraSettleFlag
),
Actual AS
(
    SELECT
        YMD,
        AYMD,
        PLTID,
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
        CAST(TXAMT AS DECIMAL(38,4)) AS TXAMT,
        CAST(CLTOTAL AS DECIMAL(38,4)) AS CLTOTAL,
        CAST(PGTOTAL AS DECIMAL(38,4)) AS PGTOTAL
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @BusinessYmd
      AND USESTATE = 2
)
SELECT
    COALESCE(E.PLTID, A.PLTID) AS PLTID,
    E.TXCNT AS ExpectedCount,
    A.TXCNT AS ActualCount,
    ISNULL(A.TXAMT,0) - ISNULL(E.TXAMT,0) AS TxAmtDifference,
    ISNULL(A.CLTOTAL,0) - ISNULL(E.CLTOTAL,0) AS CLTotalDifference,
    ISNULL(A.PGTOTAL,0) - ISNULL(E.PGTOTAL,0) AS PGTotalDifference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.YMD = E.YMD
 AND A.AYMD = E.AYMD
 AND A.PLTID = E.PLTID
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
 AND A.SERVICENAME = E.SERVICENAME
 AND A.PRODUCTNAME = E.PRODUCTNAME
 AND A.USESTATE = E.USESTATE
 AND ISNULL(A.CompanySalesType,4) = ISNULL(E.CompanySalesType,4)
 AND ISNULL(A.ProcYMD,'') = ISNULL(E.ProcYMD,'')
 AND ISNULL(A.ExtraSettleFlag,9) = ISNULL(E.ExtraSettleFlag,9)
WHERE E.PLTID IS NULL
   OR A.PLTID IS NULL
   OR E.TXCNT <> A.TXCNT
   OR E.TXAMT <> A.TXAMT
   OR E.CLTOTAL <> A.CLTOTAL
   OR E.PGTOTAL <> A.PGTOTAL;
```

### V42 입금 기준 요약 대사

```sql
WITH Expected AS
(
    SELECT
        YMD,
        AYMD,
        INYMD,
        CLIENTID,
        PGNAME,
        MALLID,
        SERVICENAME,
        PRODUCTNAME,
        USESTATE,
        CompanySalesType,
        ProcYMD,
        ExtraSettleFlag,
        COUNT_BIG(*) AS INCNT,
        SUM(CAST(ISNULL(TXAMT,0) AS DECIMAL(38,4))) AS TXAMT,
        SUM(CAST(ISNULL(CLTOTAL,0) AS DECIMAL(38,4))) AS CLTOTAL,
        SUM(CAST(ISNULL(PGTOTAL,0) AS DECIMAL(38,4))) AS PGTOTAL
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @BusinessYmd
      AND INSTATE = 1
    GROUP BY
        YMD, AYMD, INYMD, CLIENTID, PGNAME, MALLID,
        SERVICENAME, PRODUCTNAME, USESTATE,
        CompanySalesType, ProcYMD, ExtraSettleFlag
),
Actual AS
(
    SELECT
        YMD,
        AYMD,
        INYMD,
        CLIENTID,
        PGNAME,
        MALLID,
        SERVICENAME,
        PRODUCTNAME,
        USESTATE,
        CompanySalesType,
        ProcYMD,
        ExtraSettleFlag,
        CAST(INCNT AS BIGINT) AS INCNT,
        CAST(TXAMT AS DECIMAL(38,4)) AS TXAMT,
        CAST(CLTOTAL AS DECIMAL(38,4)) AS CLTOTAL,
        CAST(PGTOTAL AS DECIMAL(38,4)) AS PGTOTAL
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @BusinessYmd
)
SELECT
    COALESCE(E.INYMD, A.INYMD) AS INYMD,
    COALESCE(E.CLIENTID, A.CLIENTID) AS CLIENTID,
    COALESCE(E.PGNAME, A.PGNAME) AS PGNAME,
    E.INCNT AS ExpectedCount,
    A.INCNT AS ActualCount,
    ISNULL(A.TXAMT,0) - ISNULL(E.TXAMT,0) AS TxAmtDifference,
    ISNULL(A.CLTOTAL,0) - ISNULL(E.CLTOTAL,0) AS CLTotalDifference,
    ISNULL(A.PGTOTAL,0) - ISNULL(E.PGTOTAL,0) AS PGTotalDifference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.YMD = E.YMD
 AND A.AYMD = E.AYMD
 AND A.INYMD = E.INYMD
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
 AND A.SERVICENAME = E.SERVICENAME
 AND A.PRODUCTNAME = E.PRODUCTNAME
 AND A.USESTATE = E.USESTATE
 AND ISNULL(A.CompanySalesType,4) = ISNULL(E.CompanySalesType,4)
 AND ISNULL(A.ProcYMD,'') = ISNULL(E.ProcYMD,'')
 AND ISNULL(A.ExtraSettleFlag,9) = ISNULL(E.ExtraSettleFlag,9)
WHERE E.INYMD IS NULL
   OR A.INYMD IS NULL
   OR E.INCNT <> A.INCNT
   OR E.TXAMT <> A.TXAMT
   OR E.CLTOTAL <> A.CLTOTAL
   OR E.PGTOTAL <> A.PGTOTAL;
```

### V43 출금 기준 최종 요약 대사

S14 기본 요약, S15 수동매입 교체, S16 추가정산 교체, S17 수금후 취소 보정이 모두 반영된 최종 상태를 원장으로부터 다시 집계한다.

```sql
WITH Expected AS
(
    SELECT
        YMD,
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
        SUM(CAST(ISNULL(TXAMT,0) AS DECIMAL(38,4))) AS TXAMT,
        SUM(CAST(ISNULL(CLTOTAL,0) AS DECIMAL(38,4))) AS CLTOTAL,
        SUM(CAST(ISNULL(PGTOTAL,0) AS DECIMAL(38,4))) AS PGTOTAL,
        SUM(CAST(ISNULL(POQINCOME,0) AS DECIMAL(38,4))) AS POQINCOME,
        SUM(CAST(ISNULL(ForeignSettleAmt,0) AS DECIMAL(38,4))) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @BusinessYmd
      AND OUTSTATE IN (2,9)
    GROUP BY
        YMD, AYMD, INYMD, OUTYMD, CLIENTID, PGNAME, MALLID,
        SERVICENAME, PRODUCTNAME, USESTATE, OUTSTATE,
        SettleCurrency, CompanySalesType, ProcYMD, ExtraSettleFlag
),
Actual AS
(
    SELECT
        YMD,
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
        CAST(TXAMT AS DECIMAL(38,4)) AS TXAMT,
        CAST(CLTOTAL AS DECIMAL(38,4)) AS CLTOTAL,
        CAST(PGTOTAL AS DECIMAL(38,4)) AS PGTOTAL,
        CAST(POQINCOME AS DECIMAL(38,4)) AS POQINCOME,
        CAST(ForeignSettleAmt AS DECIMAL(38,4)) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @BusinessYmd
      AND OUTSTATE IN (2,9)
)
SELECT
    COALESCE(E.OUTYMD, A.OUTYMD) AS OUTYMD,
    COALESCE(E.CLIENTID, A.CLIENTID) AS CLIENTID,
    COALESCE(E.PGNAME, A.PGNAME) AS PGNAME,
    E.OUTCNT AS ExpectedCount,
    A.OUTCNT AS ActualCount,
    ISNULL(A.TXAMT,0) - ISNULL(E.TXAMT,0) AS TxAmtDifference,
    ISNULL(A.CLTOTAL,0) - ISNULL(E.CLTOTAL,0) AS CLTotalDifference,
    ISNULL(A.PGTOTAL,0) - ISNULL(E.PGTOTAL,0) AS PGTotalDifference,
    ISNULL(A.POQINCOME,0) - ISNULL(E.POQINCOME,0) AS POQIncomeDifference,
    ISNULL(A.ForeignSettleAmt,0) - ISNULL(E.ForeignSettleAmt,0) AS ForeignDifference
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.YMD = E.YMD
 AND A.AYMD = E.AYMD
 AND ISNULL(A.INYMD,'') = ISNULL(E.INYMD,'')
 AND ISNULL(A.OUTYMD,'') = ISNULL(E.OUTYMD,'')
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
 AND A.SERVICENAME = E.SERVICENAME
 AND A.PRODUCTNAME = E.PRODUCTNAME
 AND A.USESTATE = E.USESTATE
 AND A.OUTSTATE = E.OUTSTATE
 AND ISNULL(A.SettleCurrency,'') = ISNULL(E.SettleCurrency,'')
 AND ISNULL(A.CompanySalesType,4) = ISNULL(E.CompanySalesType,4)
 AND ISNULL(A.ProcYMD,'') = ISNULL(E.ProcYMD,'')
 AND ISNULL(A.ExtraSettleFlag,9) = ISNULL(E.ExtraSettleFlag,9)
WHERE E.YMD IS NULL
   OR A.YMD IS NULL
   OR E.OUTCNT <> A.OUTCNT
   OR E.TXAMT <> A.TXAMT
   OR E.CLTOTAL <> A.CLTOTAL
   OR E.PGTOTAL <> A.PGTOTAL
   OR E.POQINCOME <> A.POQINCOME
   OR E.ForeignSettleAmt <> A.ForeignSettleAmt;
```

### V50 SQL 반올림 및 절삭 회귀 검증

```sql
WITH TestValues AS
(
    SELECT CAST(12.5 AS DECIMAL(10,2)) AS TestValue
    UNION ALL
    SELECT CAST(-12.5 AS DECIMAL(10,2))
    UNION ALL
    SELECT CAST(12.49 AS DECIMAL(10,2))
    UNION ALL
    SELECT CAST(-12.49 AS DECIMAL(10,2))
)
SELECT
    TestValue,
    CAST(TestValue AS INT) AS CastToInt,
    ROUND(TestValue,0) AS SqlRounded,
    ROUND(TestValue,0,0) AS ModeZero,
    ROUND(TestValue,0,1) AS ModeNonZero
FROM TestValues;
```

C# 호환 계산 결과는 별도 파라미터 또는 테스트 결과 테이블로 전달하여 각 열과 직접 비교한다. 전체 합계를 먼저 나눈 결과가 아니라 구성요소별 절삭 후 합계를 비교한다.

```sql
SELECT
    CAST(@Component1 / CAST(1.1 AS DECIMAL(2,1)) AS INT)
  + CAST(@Component2 / CAST(1.1 AS DECIMAL(2,1)) AS INT)
  + CAST(@Component3 / CAST(1.1 AS DECIMAL(2,1)) AS INT)
    AS ExpectedComponentWiseValue;
```

### V90 최종 게시 게이트

S01은 RunId 이전 단계이므로 필수 체크포인트 목록에서 제외한다. S19는 이 게이트를 수행하는 단계이므로 선행 성공 목록은 S02~S18이다.

```sql
WITH RequiredSteps AS
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
CheckpointResult AS
(
    SELECT
        R.StepCode,
        CASE WHEN EXISTS
        (
            SELECT 1
            FROM batch.BatchCheckpoint AS C
            WHERE C.RunId = @RunId
              AND C.StepCode = R.StepCode
              AND C.CheckpointStatus = N'Succeeded'
        )
        THEN 1 ELSE 0 END AS IsSucceeded
    FROM RequiredSteps AS R
),
CriticalIssues AS
(
    SELECT COUNT_BIG(*) AS IssueCount
    FROM batch.BatchValidationIssue
    WHERE RunId = @RunId
      AND Severity IN (N'Error', N'Critical')
),
LockOwnership AS
(
    SELECT COUNT_BIG(*) AS LockCount
    FROM batch.BatchRunLock
    WHERE JobName = N'POQSettleBatch20'
      AND BatchYmd = @BusinessYmd
      AND OwnerRunId = @RunId
      AND LockStatus = N'Held'
)
SELECT
    N'V90_PUBLISH_GATE' AS MetricCode,
    CASE
        WHEN (SELECT COUNT(*) FROM CheckpointResult WHERE IsSucceeded = 0) = 0
         AND (SELECT IssueCount FROM CriticalIssues) = 0
         AND (SELECT LockCount FROM LockOwnership) = 1
        THEN 1
        ELSE 0
    END AS PassFlag,
    CAST(0 AS DECIMAL(38,4)) AS ExpectedValue,
    CAST
    (
        (SELECT COUNT(*) FROM CheckpointResult WHERE IsSucceeded = 0)
      + (SELECT IssueCount FROM CriticalIssues)
      + CASE WHEN (SELECT LockCount FROM LockOwnership) = 1 THEN 0 ELSE 1 END
        AS DECIMAL(38,4)
    ) AS ActualValue;
```