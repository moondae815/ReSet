## 통합 데이터 정합성 검증 SQL 세트

### 검증 원칙

- 모든 검증 SQL은 SNAPSHOT 격리 수준에서 실행한다.
- 모든 `NOLOCK` 힌트를 제거한다.
- `@p_businessYmd`는 원본 `CHAR(8)` 또는 `VARCHAR(8)` 계약에 맞게 전달한다.
- `@p_runId`는 S02에서 IDENTITY로 발급된 값을 사용한다.
- 업무 테이블명은 동적으로 조립하지 않는다.
- 집계 비교는 각 측을 독립 CTE 또는 독립 서브쿼리로 계산한다.
- 두 집계를 카티션 곱으로 결합하지 않는다.
- 체크섬은 변경 탐지 보조 수단이며 재무 정합성의 최종 증명으로 사용하지 않는다.
- S11과 S18은 동일한 사실 지문 SQL을 사용한다.
- 검증 결과가 `FAIL`이면 S19 성공 게시를 금지한다.

### V01 배치 전용 객체 존재 검증

```sql
SELECT R.RequiredObject
FROM
(
    VALUES
        (N'batch.BatchRun'),
        (N'batch.BatchRunLock'),
        (N'batch.BatchStepJournal'),
        (N'batch.BatchCheckpoint'),
        (N'batch.BatchValidationIssue'),
        (N'batch.BatchControlTotal'),
        (N'batch.BatchReconciliation')
) AS R(RequiredObject)
WHERE OBJECT_ID(R.RequiredObject, N'U') IS NULL;
```

결과가 한 행이라도 있으면 업무 DML을 시작하지 않는다.

### V02 일일 요율 스냅샷 행 수

```sql
SELECT N'TPGSettleRate' AS ObjectName, COUNT_BIG(*) AS RowCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate
WHERE YMD = @p_businessYmd

UNION ALL

SELECT N'TClientSettleRate', COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate
WHERE YMD = @p_businessYmd

UNION ALL

SELECT N'TPGSettleRate4Extra', COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
WHERE YMD = @p_businessYmd

UNION ALL

SELECT N'TClientSettleRate4Extra', COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
WHERE YMD = @p_businessYmd

UNION ALL

SELECT N'TClientSettleRate4MobileCo', COUNT_BIG(*)
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
WHERE YMD = @p_businessYmd;
```

### V03 요율 스냅샷 후보 키 중복 탐지

실제 PK 또는 유니크 키는 배포 전 대상 DDL과 대조해야 한다. 다음 쿼리는 명세에 확인된 업무 식별 열을 이용한 후보 중복 탐지다.

```sql
SELECT
    YMD,
    PGNAME,
    MALLID,
    COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TPGSettleRate
WHERE YMD = @p_businessYmd
GROUP BY YMD, PGNAME, MALLID
HAVING COUNT_BIG(*) > 1;

SELECT
    YMD,
    CLIENTID,
    PGNAME,
    MALLID,
    COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TClientSettleRate
WHERE YMD = @p_businessYmd
GROUP BY YMD, CLIENTID, PGNAME, MALLID
HAVING COUNT_BIG(*) > 1;

SELECT
    YMD,
    ClientID,
    PGName,
    MallID,
    COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
WHERE YMD = @p_businessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1;

SELECT
    YMD,
    ClientID,
    PGName,
    MallID,
    COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
WHERE YMD = @p_businessYmd
GROUP BY YMD, ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1;
```

레거시 계약상 버전별 복수 행이 허용되는 테이블은 DDL의 실제 키 열을 추가해 오탐을 제거한다.

### V04 TSettleMst 동결 지문

S11과 S18에서 동일하게 실행한다.

```sql
SELECT
    COUNT_BIG(*) AS RowCount,
    CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS RowChecksum,
    SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))) AS TxAmt,
    SUM(CAST(ISNULL(CLCOMM, 0) AS DECIMAL(38,4))) AS CLComm,
    SUM(CAST(ISNULL(CLVT, 0) AS DECIMAL(38,4))) AS CLVat,
    SUM(CAST(ISNULL(PGCOMM, 0) AS DECIMAL(38,4))) AS PGComm,
    SUM(CAST(ISNULL(PGVT, 0) AS DECIMAL(38,4))) AS PGVat,
    SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))) AS CLTotal,
    SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))) AS PGTotal,
    SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))) AS POQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd;
```

S11 기록값과 S18 현재값 비교는 통제명별로 수행한다.

```sql
WITH Recorded AS
(
    SELECT
        ControlName,
        MAX(ControlValue) AS ControlValue
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId
      AND StepCode = N'S11'
    GROUP BY ControlName
),
CurrentValue AS
(
    SELECT N'TSettleMst.RowCount' AS ControlName,
           CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS ControlValue
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'TSettleMst.TxAmt',
           ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))), 0)
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'TSettleMst.CLTotal',
           ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))), 0)
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT N'TSettleMst.PGTotal',
           ISNULL(SUM(CAST(PGTOTAL AS DECIMAL(38,4))), 0)
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
)
SELECT
    COALESCE(R.ControlName, C.ControlName) AS ControlName,
    R.ControlValue AS FrozenValue,
    C.ControlValue AS CurrentValue
FROM Recorded AS R
FULL OUTER JOIN CurrentValue AS C
  ON C.ControlName = R.ControlName
WHERE ISNULL(R.ControlValue, -1) <> ISNULL(C.ControlValue, -1);
```

### V05 거래 유형별 통제 합계

```sql
SELECT
    USESTATE,
    COUNT_BIG(*) AS RowCount,
    SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))) AS TxAmt,
    SUM(CAST(ISNULL(CLCOMM, 0) AS DECIMAL(38,4))) AS CLComm,
    SUM(CAST(ISNULL(CLVT, 0) AS DECIMAL(38,4))) AS CLVat,
    SUM(CAST(ISNULL(PGCOMM, 0) AS DECIMAL(38,4))) AS PGComm,
    SUM(CAST(ISNULL(PGVT, 0) AS DECIMAL(38,4))) AS PGVat,
    SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))) AS CLTotal,
    SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))) AS PGTotal
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
GROUP BY USESTATE
ORDER BY USESTATE;
```

`USESTATE`는 최소한 다음 분기 의미를 유지해야 한다.

- `0`: 정상거래
- `1`: 취소
- `2`: 부분취소
- `3`: 환불

### V06 원거래 연결 누락

```sql
SELECT
    C.YMD,
    C.PLTID,
    C.CLIENTID,
    C.PGNAME,
    C.MALLID,
    C.USESTATE
FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
WHERE C.YMD = @p_businessYmd
  AND C.USESTATE IN (1, 2, 3)
  AND NOT EXISTS
  (
      SELECT 1
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS O
      WHERE O.PLTID = C.PLTID
        AND O.USESTATE = 0
  );
```

환불 원천이 별도 거래번호 정책을 사용하는 경우 S04 원본 분기의 실제 연결 키를 추가 적용한다.

### V07 금액 구성요소 불변식

```sql
SELECT
    ID,
    YMD,
    PLTID,
    CLIENTID,
    PGNAME,
    MALLID,
    CLCOMM,
    CLVT,
    CLETC,
    CLINTCOMM,
    CLTOTAL,
    PGCOMM,
    PGVT,
    PGETC,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    PGTOTAL,
    POQINCOME
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
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

VAT 포함 재분해 대상은 레거시와 동일한 개별 `CAST(... AS INT)`를 사용해 별도로 검증한다.

```sql
SELECT
    ID,
    YMD,
    PLTID,
    CLComm,
    CLVT,
    CLTotal,
    PGTotal,
    POQIncome
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND CLVTType = 1
  AND (UseState <> 1 OR (UseState = 1 AND YMD = AYMD))
  AND
  (
      CLComm <>
          CAST((CLComm + CLVT) / CAST(1.1 AS DECIMAL(2,1)) AS INT)
      OR
      POQIncome <> CLTotal - PGTotal
  );
```

단계별 상세 검증에서는 원본 UPDATE 13의 갱신 전 구성요소를 사용해야 하므로 S07 실행 전 통제값 또는 별도 승인된 검증 스테이징과 비교한다.

### V08 입출금 상태 및 날짜 위반

```sql
SELECT
    ID,
    YMD,
    PLTID,
    InState,
    InYMD,
    OutState,
    OutYMD,
    EDIReqYmd
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_businessYmd
  AND
  (
      (InState = 1 AND NULLIF(InYMD, '') IS NULL)
      OR
      (OutState IN (1, 2, 5) AND NULLIF(OutYMD, '') IS NULL)
      OR
      (OutState = 1 AND OutYMD IS NULL)
  );
```

수기매입 대상의 요청일 누락은 계약 테이블과 독립적으로 검증한다.

```sql
SELECT
    A.ID,
    A.YMD,
    A.PLTID,
    A.CLIENTID,
    A.PGNAME,
    A.OutState,
    A.OutYMD,
    A.EDIReqYmd
FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
INNER JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt AS C
        ON C.ClientID = A.ClientID
WHERE A.OutState IN (2, 9)
  AND C.AcqType = 1
  AND A.EDIReqYmd = @p_businessYmd
  AND NULLIF(A.OutYMD, '') IS NULL;
```

### V09 PG 수납 통계 재산출 비교

```sql
WITH SourceRows AS
(
    SELECT
        INYMD,
        LOWER(CLIENTID) AS CLIENTID,
        LOWER(PGNAME) AS PGNAME,
        LOWER(MALLID) AS MALLID,
        SUM(TXAMT - PGTOTAL) AS COLLECTAMT,
        SUM(PGCOMM + PGETC + ISNULL(PGINTREALCOMM, 0)) AS PGCOMM,
        SUM(PGVT) AS PGVT,
        SUM
        (
            CASE
                WHEN PGNAME IN ('WOWCOIN', 'WOW_ARS', 'WOW_1588')
                THEN CASE WHEN OUTYMD >= @p_businessYmd THEN TXAMT END
                ELSE TXAMT
            END
        ) AS SETTLEWILLAMT,
        SUM
        (
            CASE
                WHEN OUTYMD < @p_businessYmd AND OUTSTATE = 1
                THEN CLCOMM + CLETC + ISNULL(CLINTCOMM, 0)
                ELSE 0
            END
        ) AS AHEADSALESCOMM,
        SUM
        (
            CASE
                WHEN OUTYMD < @p_businessYmd AND OUTSTATE = 1
                THEN CLVT
                ELSE 0
            END
        ) AS AHEADSALESVT,
        SUM
        (
            CASE
                WHEN OUTYMD < @p_businessYmd AND OUTSTATE = 1
                THEN TXAMT - CLTOTAL
                ELSE 0
            END
        ) AS AHEADSETTLEAMT
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE INYMD = @p_businessYmd
      AND INSTATE = 1
    GROUP BY INYMD, CLIENTID, PGNAME, MALLID

    UNION ALL

    SELECT
        COLLECTYMD,
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
    WHERE COLLECTYMD = @p_businessYmd
    GROUP BY COLLECTYMD, CLIENTID, PGNAME, MALLID

    UNION ALL

    SELECT
        COLLECTYMD,
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
    WHERE COLLECTYMD = @p_businessYmd
    GROUP BY COLLECTYMD, CLIENTID, PGNAME, MALLID
),
Expected AS
(
    SELECT
        INYMD,
        CLIENTID,
        PGNAME,
        MALLID,
        ISNULL(SUM(COLLECTAMT), 0) AS COLLECTAMT,
        ISNULL(SUM(PGCOMM), 0) AS PGCOMM,
        ISNULL(SUM(PGVT), 0) AS PGVT,
        ISNULL(SUM(COLLECTAMT + PGCOMM + PGVT), 0) AS LEFTSUMAMT,
        ISNULL(SUM(SETTLEWILLAMT), 0) AS SETTLEWILLAMT,
        ISNULL(SUM(AHEADSALESCOMM), 0) AS AHEADSALESCOMM,
        ISNULL(SUM(AHEADSALESVT), 0) AS AHEADSALESVT,
        ISNULL(SUM(AHEADSETTLEAMT), 0) AS AHEADSETTLEAMT,
        ISNULL(SUM(SETTLEWILLAMT), 0)
          + ISNULL(SUM(AHEADSALESCOMM), 0)
          + ISNULL(SUM(AHEADSALESVT), 0)
          + ISNULL(SUM(AHEADSETTLEAMT), 0) AS RIGHTSUMAMT
    FROM SourceRows
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
    WHERE INYMD = @p_businessYmd
)
SELECT
    COALESCE(E.INYMD, A.INYMD) AS INYMD,
    COALESCE(E.CLIENTID, A.CLIENTID) AS CLIENTID,
    COALESCE(E.PGNAME, A.PGNAME) AS PGNAME,
    COALESCE(E.MALLID, A.MALLID) AS MALLID,
    E.COLLECTAMT AS ExpectedCollectAmt,
    A.COLLECTAMT AS ActualCollectAmt,
    E.LEFTSUMAMT AS ExpectedLeftSumAmt,
    A.LEFTSUMAMT AS ActualLeftSumAmt,
    E.RIGHTSUMAMT AS ExpectedRightSumAmt,
    A.RIGHTSUMAMT AS ActualRightSumAmt
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.INYMD = E.INYMD
 AND A.CLIENTID = E.CLIENTID
 AND A.PGNAME = E.PGNAME
 AND A.MALLID = E.MALLID
WHERE E.INYMD IS NULL
   OR A.INYMD IS NULL
   OR ISNULL(E.COLLECTAMT, 0) <> ISNULL(A.COLLECTAMT, 0)
   OR ISNULL(E.PGCOMM, 0) <> ISNULL(A.PGCOMM, 0)
   OR ISNULL(E.PGVT, 0) <> ISNULL(A.PGVT, 0)
   OR ISNULL(E.LEFTSUMAMT, 0) <> ISNULL(A.LEFTSUMAMT, 0)
   OR ISNULL(E.SETTLEWILLAMT, 0) <> ISNULL(A.SETTLEWILLAMT, 0)
   OR ISNULL(E.AHEADSALESCOMM, 0) <> ISNULL(A.AHEADSALESCOMM, 0)
   OR ISNULL(E.AHEADSALESVT, 0) <> ISNULL(A.AHEADSALESVT, 0)
   OR ISNULL(E.AHEADSETTLEAMT, 0) <> ISNULL(A.AHEADSETTLEAMT, 0)
   OR ISNULL(E.RIGHTSUMAMT, 0) <> ISNULL(A.RIGHTSUMAMT, 0);
```

### V10 TSettleMiss ID 및 그룹 중복

```sql
SELECT
    ID,
    COUNT_BIG(*) AS DuplicateCount
FROM SETTLE_POQ_DB.dbo.TSettleMiss
GROUP BY ID
HAVING COUNT_BIG(*) > 1;

SELECT
    ClientID,
    OutYMD,
    IssueType,
    OutState,
    COUNT_BIG(*) AS GroupCount
FROM SETTLE_POQ_DB.dbo.TSettleMiss
WHERE IssueType = 15
  AND OutState = 2
GROUP BY ClientID, OutYMD, IssueType, OutState
HAVING COUNT_BIG(*) > 1;
```

### V11 후취정산 원천·대상 금액 비교

```sql
WITH Expected AS
(
    SELECT
        A.ClientID,
        A.OutYMD,
        SUM(CAST(A.CLTotal AS MONEY)) AS CLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
            ON B.YMD = A.YMD
           AND B.ClientID = A.ClientID
           AND B.PGName = A.PGName
           AND B.MallID = A.MallID
    INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
            ON C.ClientID = A.ClientID
    WHERE A.YMD = @p_businessYmd
      AND A.OutState = 2
      AND ISNULL(B.TaxFGBill, 2) = 1
    GROUP BY A.ClientID, A.OutYMD
),
Actual AS
(
    SELECT
        ClientID,
        OutYMD,
        SUM(CAST(CLSettleAmt AS MONEY)) AS CLSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE OutState = 2
      AND ISNULL(IssueType, 0) = 15
    GROUP BY ClientID, OutYMD
)
SELECT
    COALESCE(E.ClientID, A.ClientID) AS ClientID,
    COALESCE(E.OutYMD, A.OutYMD) AS OutYMD,
    E.CLSettleAmt AS ExpectedAmount,
    A.CLSettleAmt AS ActualAmount
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.ClientID = E.ClientID
 AND A.OutYMD = E.OutYMD
WHERE E.ClientID IS NULL
   OR A.ClientID IS NULL
   OR ISNULL(E.CLSettleAmt, 0) <> ISNULL(A.CLSettleAmt, 0);
```

### V12 요약 테이블 금액 보존 비교

이 검증은 일반 요약의 기본 범위를 확인한다. S15의 수기매입 범위, S16의 `ProcYMD` 및 요청일 범위, S17의 사후취소 그룹은 각각 해당 단계에서 산출한 영향 키 집합으로 추가 검증해야 한다.

```sql
WITH Expected AS
(
    SELECT
        N'TSettleByTX' AS ObjectName,
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))) AS TxAmt,
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))) AS CLTotal,
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))) AS PGTotal
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT
        N'TPartialCancelByTX',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND USESTATE = 2

    UNION ALL

    SELECT
        N'TSettleByIN',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND INSTATE = 1

    UNION ALL

    SELECT
        N'TSettleByOUT',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_businessYmd
      AND OUTSTATE IN (2, 9)
),
Actual AS
(
    SELECT
        N'TSettleByTX' AS ObjectName,
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))) AS TxAmt,
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))) AS CLTotal,
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))) AS PGTotal
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT
        N'TPartialCancelByTX',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT
        N'TSettleByIN',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TSettleByIN
    WHERE YMD = @p_businessYmd

    UNION ALL

    SELECT
        N'TSettleByOUT',
        SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))),
        SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4)))
    FROM SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE YMD = @p_businessYmd
      AND OUTSTATE IN (2, 9)
)
SELECT
    COALESCE(E.ObjectName, A.ObjectName) AS ObjectName,
    E.TxAmt AS ExpectedTxAmt,
    A.TxAmt AS ActualTxAmt,
    E.CLTotal AS ExpectedCLTotal,
    A.CLTotal AS ActualCLTotal,
    E.PGTotal AS ExpectedPGTotal,
    A.PGTotal AS ActualPGTotal
FROM Expected AS E
FULL OUTER JOIN Actual AS A
  ON A.ObjectName = E.ObjectName
WHERE E.ObjectName IS NULL
   OR A.ObjectName IS NULL
   OR ISNULL(E.TxAmt, 0) <> ISNULL(A.TxAmt, 0)
   OR ISNULL(E.CLTotal, 0) <> ISNULL(A.CLTotal, 0)
   OR ISNULL(E.PGTotal, 0) <> ISNULL(A.PGTotal, 0);
```

### V13 단계 저널 및 체크포인트 커버리지

```sql
WITH RequiredSteps AS
(
    SELECT StepCode
    FROM
    (
        VALUES
            (N'S01'), (N'S02'), (N'S03'), (N'S04'), (N'S05'),
            (N'S06'), (N'S07'), (N'S08'), (N'S09'), (N'S10'),
            (N'S11'), (N'S12'), (N'S13'), (N'S14'), (N'S15'),
            (N'S16'), (N'S17'), (N'S18'), (N'S19')
    ) AS S(StepCode)
),
SucceededJournal AS
(
    SELECT DISTINCT StepCode
    FROM batch.BatchStepJournal
    WHERE RunId = @p_runId
      AND StepStatus = N'Succeeded'
),
SucceededCheckpoint AS
(
    SELECT DISTINCT StepCode
    FROM batch.BatchCheckpoint
    WHERE RunId = @p_runId
      AND CheckpointStatus = N'Succeeded'
)
SELECT
    R.StepCode,
    CASE WHEN J.StepCode IS NULL THEN N'MissingJournal' END AS JournalIssue,
    CASE WHEN C.StepCode IS NULL THEN N'MissingCheckpoint' END AS CheckpointIssue
FROM RequiredSteps AS R
LEFT JOIN SucceededJournal AS J
       ON J.StepCode = R.StepCode
LEFT JOIN SucceededCheckpoint AS C
       ON C.StepCode = R.StepCode
WHERE J.StepCode IS NULL
   OR C.StepCode IS NULL;
```

S19 실행 전 검증에서는 S19 자체가 아직 성공하지 않았으므로 S01~S18만 요구하도록 호출 시점을 구분한다.

### V14 실패 저널 존재 검증

```sql
SELECT
    StepCode,
    StepStatus,
    LegacyReturnCode,
    ErrorMessage,
    StartedAtUtc,
    CompletedAtUtc
FROM batch.BatchStepJournal
WHERE RunId = @p_runId
  AND StepStatus = N'Failed';
```

결과가 한 행이라도 존재하면 `batch.BatchRun.RunStatus`를 `Succeeded`로 게시하지 않는다.

### V15 필수 정합성 결과 판정

```sql
SELECT
    CheckCode,
    CheckName,
    ResultStatus,
    DifferenceCount,
    DetailMessage
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND ResultStatus = N'FAIL';
```

승인되지 않은 경고도 별도로 확인한다.

```sql
SELECT
    CheckCode,
    CheckName,
    ResultStatus,
    DifferenceCount,
    DetailMessage
FROM batch.BatchReconciliation
WHERE RunId = @p_runId
  AND ResultStatus = N'WARN';
```

두 결과가 모두 0행이고 S01~S18의 필수 저널과 체크포인트가 `Succeeded`일 때만 S19가 최종 성공을 게시한다.

### V16 최종 실행 상태와 잠금 상태

```sql
SELECT
    R.RunId,
    R.JobName,
    R.BatchYmd,
    R.RunStatus,
    R.ResumeFromStepCode,
    R.StartedAtUtc,
    R.CompletedAtUtc,
    R.ErrorMessage,
    L.LockStatus,
    L.OwnerRunId,
    L.AcquiredAtUtc,
    L.HeartbeatAtUtc,
    L.ReleasedAtUtc
FROM batch.BatchRun AS R
LEFT JOIN batch.BatchRunLock AS L
       ON L.JobName = R.JobName
      AND L.BatchYmd = R.BatchYmd
      AND L.OwnerRunId = R.RunId
WHERE R.RunId = @p_runId;
```

성공 종료의 최종 조건은 다음과 같다.

- `batch.BatchRun.RunStatus = N'Succeeded'`
- `batch.BatchRun.CompletedAtUtc IS NOT NULL`
- S01~S19 필수 단계 저널이 `Succeeded`
- S01~S19 필수 체크포인트가 `Succeeded`
- `batch.BatchReconciliation`에 `FAIL` 없음
- 승인되지 않은 `WARN` 없음
- `batch.BatchRunLock.LockStatus = N'Released'`
- `batch.BatchRunLock.ReleasedAtUtc IS NOT NULL`