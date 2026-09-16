## 통합 데이터 정합성 검증 SQL 세트

### 검증 공통 계약

모든 검증 SQL은 읽기 시 SNAPSHOT 격리를 보장하고 `NOLOCK`을 사용하지 않는다. 공통 매개변수는 다음과 같다.

- `@p_runId BIGINT`
- `@p_businessYmd VARCHAR(8)`

각 검증 결과는 가능한 경우 다음 열로 정규화한다.

| 열 | 의미 |
|---|---|
| `CheckCode` | 검증 식별자 |
| `CheckName` | 검증명 |
| `Severity` | `Info`, `Warning`, `Error`, `Critical` |
| `ExpectedValue` | 기대값 |
| `ActualValue` | 실제값 |
| `DeltaValue` | 수치 차이 |
| `Passed` | 통과 여부 |
| `SampleBusinessKey` | 대표 업무키 |
| `DetailMessage` | 상세 설명 |

두 집계값은 각각 독립된 변수 또는 독립 CTE에서 계산한다. 비교 과정에서 양쪽 원본 행이 서로 증식하지 않도록 한다.

### V00 실행 전제 검증

```sql
-- V00-01 필수 데이터베이스 접근성
SELECT N'V00-01' AS CheckCode,
       N'필수 데이터베이스 접근성' AS CheckName,
       N'Critical' AS Severity,
       N'3' AS ExpectedValue,
       CONVERT(NVARCHAR(200),
           ISNULL(HAS_DBACCESS(N'PaymentDB'), 0)
         + ISNULL(HAS_DBACCESS(N'PLCardDB'), 0)
         + ISNULL(HAS_DBACCESS(N'SETTLE_CARD_DB'), 0)) AS ActualValue,
       CONVERT(DECIMAL(38,4),
           3 -
          (ISNULL(HAS_DBACCESS(N'PaymentDB'), 0)
         + ISNULL(HAS_DBACCESS(N'PLCardDB'), 0)
         + ISNULL(HAS_DBACCESS(N'SETTLE_CARD_DB'), 0))) AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN ISNULL(HAS_DBACCESS(N'PaymentDB'), 0) = 1
                  AND ISNULL(HAS_DBACCESS(N'PLCardDB'), 0) = 1
                  AND ISNULL(HAS_DBACCESS(N'SETTLE_CARD_DB'), 0) = 1
                THEN 1 ELSE 0 END) AS Passed,
       NULL AS SampleBusinessKey,
       N'세 교차 데이터베이스 모두 접근 가능해야 한다' AS DetailMessage;
```

```sql
-- V00-02 영업일 형식과 달력 유효성
SELECT N'V00-02' AS CheckCode,
       N'영업일 유효성' AS CheckName,
       N'Critical' AS Severity,
       N'유효한 YYYYMMDD' AS ExpectedValue,
       CONVERT(NVARCHAR(200), @p_businessYmd) AS ActualValue,
       NULL AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN LEN(@p_businessYmd) = 8
                  AND @p_businessYmd NOT LIKE '%[^0-9]%'
                  AND TRY_CONVERT(DATE, @p_businessYmd, 112) IS NOT NULL
                THEN 1 ELSE 0 END) AS Passed,
       @p_businessYmd AS SampleBusinessKey,
       N'배치 영업일은 실행 중 변경되지 않는다' AS DetailMessage;
```

```sql
-- V00-03 필수 객체 존재 여부
WITH RequiredObject AS
(
    SELECT *
      FROM
      (
          VALUES
              (N'SETTLE_POQ_DB.dbo.TSettleMst'),
              (N'SETTLE_POQ_DB.dbo.TPGSettleRate'),
              (N'SETTLE_POQ_DB.dbo.TClientSettleRate'),
              (N'SETTLE_POQ_DB.dbo.TStatPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TSettleByTX'),
              (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX'),
              (N'SETTLE_POQ_DB.dbo.TSettleByIN'),
              (N'SETTLE_POQ_DB.dbo.TSettleByOUT'),
              (N'SETTLE_POQ_DB.dbo.TSettleMiss'),
              (N'PaymentDB.dbo.TTxMst'),
              (N'PLCardDB.dbo.TPLCardTxMst'),
              (N'SETTLE_CARD_DB.dbo.TClientCardContractMgmt')
      ) AS V(ObjectName)
)
SELECT N'V00-03' AS CheckCode,
       N'필수 객체 존재 여부' AS CheckName,
       N'Critical' AS Severity,
       ObjectName AS ExpectedValue,
       N'누락' AS ActualValue,
       NULL AS DeltaValue,
       CONVERT(BIT, 0) AS Passed,
       ObjectName AS SampleBusinessKey,
       N'필수 테이블이 존재하지 않는다' AS DetailMessage
  FROM RequiredObject
 WHERE OBJECT_ID(ObjectName) IS NULL;
```

```sql
-- V00-04 일반 우대와 PLCard 우대 분류 중첩
WITH GeneralExtraPg AS
(
    SELECT PGName
      FROM
      (
          VALUES
              (N'allthegate'),
              (N'dacomcard'),
              (N'tosscard'),
              (N'nicecard')
      ) AS V(PGName)
),
PlCardExtraPg AS
(
    SELECT DISTINCT LOWER(PGName) AS PGName
      FROM SETTLE_POQ_DB.dbo.TPGProperty
     WHERE ExtraType IN (2, 3)
)
SELECT N'V00-04' AS CheckCode,
       N'우대 분류 중첩' AS CheckName,
       N'Critical' AS Severity,
       N'중첩 없음' AS ExpectedValue,
       G.PGName AS ActualValue,
       NULL AS DeltaValue,
       CONVERT(BIT, 0) AS Passed,
       G.PGName AS SampleBusinessKey,
       N'S10과 S11의 적용 범위가 겹친다' AS DetailMessage
  FROM GeneralExtraPg AS G
  INNER JOIN PlCardExtraPg AS P
    ON P.PGName = G.PGName;
```

### V01 수수료율 스냅샷 검증

```sql
-- V01-01 다섯 스냅샷 행 수
SELECT N'TPGSettleRate' AS ControlName, COUNT_BIG(*) AS RowCount
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

```sql
-- V01-02 자연키 중복
SELECT N'TPGSettleRate' AS TableName,
       CONCAT(PGNAME, N'|', MALLID, N'|', VERSION) AS SampleBusinessKey,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_businessYmd
 GROUP BY PGNAME, MALLID, VERSION
HAVING COUNT_BIG(*) > 1
UNION ALL
SELECT N'TClientSettleRate',
       CONCAT(CLIENTID, N'|', PGNAME, N'|', MALLID, N'|', VERSION),
       COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_businessYmd
 GROUP BY CLIENTID, PGNAME, MALLID, VERSION
HAVING COUNT_BIG(*) > 1
UNION ALL
SELECT N'TPGSettleRate4Extra',
       CONCAT(PGName, N'|', MallID),
       COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_businessYmd
 GROUP BY PGName, MallID
HAVING COUNT_BIG(*) > 1
UNION ALL
SELECT N'TClientSettleRate4Extra',
       CONCAT(ClientID, N'|', PGName, N'|', MallID),
       COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_businessYmd
 GROUP BY ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1
UNION ALL
SELECT N'TClientSettleRate4MobileCo',
       CONCAT(ClientID, N'|', PGName, N'|', MallID),
       COUNT_BIG(*)
  FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_businessYmd
 GROUP BY ClientID, PGName, MallID
HAVING COUNT_BIG(*) > 1;
```

```sql
-- V01-03 원본 지급 완료 보호 조건
SELECT N'V01-03' AS CheckCode,
       COUNT_BIG(*) AS ProtectedLedgerRows
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_businessYmd
   AND OutState IN (1, 5)
   AND OutYMD IS NOT NULL;
```

### V02 정산 원장 구조 검증

```sql
-- V02-01 상태별 행 수와 금액
SELECT UseState,
       InState,
       OutState,
       ISNULL(ExtraSettleFlag, 9) AS ExtraSettleFlag,
       COUNT_BIG(*) AS RowCount,
       SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))) AS TxAmount,
       SUM(CONVERT(DECIMAL(38,4), ISNULL(CLTOTAL, 0))) AS ClientTotal,
       SUM(CONVERT(DECIMAL(38,4), ISNULL(PGTOTAL, 0))) AS PgTotal
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_businessYmd
 GROUP BY UseState,
          InState,
          OutState,
          ISNULL(ExtraSettleFlag, 9);
```

```sql
-- V02-02 원거래 없는 취소
SELECT C.PLTID,
       C.CLIENTID,
       C.PGNAME,
       C.MALLID,
       COUNT_BIG(*) AS CancellationRows
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS C
 WHERE C.YMD = @p_businessYmd
   AND C.USESTATE = 1
   AND NOT EXISTS
       (
           SELECT 1
             FROM SETTLE_POQ_DB.dbo.TSettleMst AS N
            WHERE N.PLTID = C.PLTID
              AND N.USESTATE = 0
       )
 GROUP BY C.PLTID,
          C.CLIENTID,
          C.PGNAME,
          C.MALLID;
```

```sql
-- V02-03 원거래별 정상 부분취소 환불 합계
SELECT PLTID,
       SUM(CASE WHEN USESTATE = 0 THEN ISNULL(TXAMT, 0) ELSE 0 END) AS NormalAmount,
       SUM(CASE WHEN USESTATE = 1 THEN ISNULL(TXAMT, 0) ELSE 0 END) AS CancelAmount,
       SUM(CASE WHEN USESTATE = 2 THEN ISNULL(TXAMT, 0) ELSE 0 END) AS PartialCancelAmount,
       SUM(CASE WHEN USESTATE = 3 THEN ISNULL(TXAMT, 0) ELSE 0 END) AS RefundAmount,
       SUM(ISNULL(TXAMT, 0)) AS NetAmount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_businessYmd
    OR AYMD = @p_businessYmd
 GROUP BY PLTID;
```

### V03 수수료와 VAT 산식 검증

```sql
-- V03-01 일반 부가세 유형의 합계 항등식
SELECT ID,
       YMD,
       PLTID,
       CLIENTID,
       CLTOTAL AS ActualClientTotal,
       CLCOMM + CLVT + CLETC + CLINTCOMM AS ExpectedClientTotal,
       PGTOTAL AS ActualPgTotal,
       PGCOMM + PGVT + PGETC
         + CASE WHEN PGINTREALCOMM = 0
                THEN PGINTEXPCOMM
                ELSE PGINTREALCOMM
           END AS ExpectedPgTotal,
       POQINCOME AS ActualPoqIncome,
       CLTOTAL - PGTOTAL AS ExpectedPoqIncome
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_businessYmd
   AND ISNULL(CLVTType, 0) <> 1
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
            + CASE WHEN ISNULL(PGINTREALCOMM, 0) = 0
                   THEN ISNULL(PGINTEXPCOMM, 0)
                   ELSE ISNULL(PGINTREALCOMM, 0)
              END
       OR ISNULL(POQINCOME, 0) <> ISNULL(CLTOTAL, 0) - ISNULL(PGTOTAL, 0)
   );
```

```sql
-- V03-02 SQL 숫자 절삭과 ROUND 옵션 진단
SELECT CAST(CONVERT(DECIMAL(18,4), 12.9000) AS INT) AS PositiveTruncate,
       CAST(CONVERT(DECIMAL(18,4), -12.9000) AS INT) AS NegativeTruncate,
       ROUND(CONVERT(DECIMAL(18,4), 12.5500), 0, 0) AS RoundedValue,
       ROUND(CONVERT(DECIMAL(18,4), 12.5500), 0, 1) AS TruncatedValue;
```

```sql
-- V03-03 카카오 계열 PGVT 재계산 표본
SELECT A.ID,
       A.PGNAME,
       A.PGCOMM,
       A.PGVT AS ActualPgVat,
       CAST
       (
           ROUND
           (
               A.PGCOMM * CONVERT(DECIMAL(2,1), 0.1),
               0,
               SETTLE_POQ_DB.dbo.UF_GET_PGCommOption(A.PGNAME, 5)
           )
           AS INT
       ) AS ExpectedPgVat
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 WHERE A.YMD = @p_businessYmd
   AND A.PGNAME IN (N'kakaopay', N'KakaoMoney')
   AND A.TID = A.CID
   AND
   (
       A.UseState <> 1
       OR
       (
           A.UseState = 1
           AND A.YMD = A.AYMD
       )
   )
   AND A.PGVT <>
       CAST
       (
           ROUND
           (
               A.PGCOMM * CONVERT(DECIMAL(2,1), 0.1),
               0,
               SETTLE_POQ_DB.dbo.UF_GET_PGCommOption(A.PGNAME, 5)
           )
           AS INT
       );
```

### V04 상태 및 예정일 검증

```sql
-- V04-01 수납 및 지급 상태와 일자 조합
SELECT ID,
       YMD,
       PLTID,
       InState,
       INYMD,
       OutState,
       OutYMD,
       CASE
           WHEN InState = 1 AND ISNULL(INYMD, '') = ''
               THEN N'수납완료이나 INYMD 없음'
           WHEN InState = 0 AND ISNULL(INYMD, '') <> ''
               THEN N'미수납이나 INYMD 존재'
           WHEN OutState IN (1, 2, 5) AND ISNULL(OutYMD, '') = ''
               THEN N'지급상태이나 OutYMD 없음'
           WHEN OutState = 0 AND ISNULL(OutYMD, '') <> ''
               THEN N'미지급이나 OutYMD 존재'
           ELSE N'기타 상태 모순'
       END AS IssueReason
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_businessYmd
   AND
   (
       (InState = 1 AND ISNULL(INYMD, '') = '')
       OR (InState = 0 AND ISNULL(INYMD, '') <> '')
       OR (OutState IN (1, 2, 5) AND ISNULL(OutYMD, '') = '')
       OR (OutState = 0 AND ISNULL(OutYMD, '') <> '')
       OR InState NOT IN (0, 1)
       OR OutState NOT IN (0, 1, 2, 5, 9)
   );
```

```sql
-- V04-02 수동매입 요청일 누락
SELECT A.ID,
       A.PLTID,
       A.CLIENTID,
       A.PGNAME,
       A.EDIReqYmd,
       A.OutState,
       A.OutYMD
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
  INNER JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt AS C
    ON C.ClientID = A.ClientID
 WHERE C.AcqType = 1
   AND A.OutState IN (2, 9)
   AND ISNULL(A.EDIReqYmd, '') = '';
```

### V05 우대 정산 검증

```sql
-- V05-01 우대 정산 기본 구조
SELECT ID,
       ProcYMD,
       YMD,
       AYMD,
       CLIENTID,
       PGNAME,
       MALLID,
       USESTATE,
       TXAMT,
       ExtraTxAmt,
       ExtraSettleFlag,
       OutState,
       OutYMD,
       CLCOMM,
       PGCOMM,
       CLTOTAL,
       PGTOTAL
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_businessYmd
   AND ExtraSettleFlag = 1
   AND
   (
       ISNULL(TXAMT, 0) <> 0
       OR ISNULL(CompanySalesType, 4) NOT IN (0, 1, 2, 3)
       OR PGNAME NOT IN
          (
              N'allthegate',
              N'dacomcard',
              N'tosscard',
              N'nicecard',
              N'PLCard',
              N'SamSungPay',
              N'NaverCard',
              N'ApplePay',
              N'TossCardAuth'
          )
   );
```

```sql
-- V05-02 일반 우대와 PLCard 우대 원장 중복 후보
SELECT PLTID,
       CLIENTID,
       AYMD,
       CompanySalesType,
       COUNT_BIG(*) AS RowCount,
       COUNT(DISTINCT PGNAME) AS PgCount
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_businessYmd
   AND ExtraSettleFlag = 1
 GROUP BY PLTID,
          CLIENTID,
          AYMD,
          CompanySalesType
HAVING COUNT_BIG(*) > 1;
```

```sql
-- V05-03 일반 우대 최소 요청일 대응
DECLARE @v_expectedMinReqYmd VARCHAR(8) =
(
    SELECT MIN(ReqYMD)
      FROM PaymentDB.dbo.TExtraSettleIn
     WHERE ResYMD = @p_businessYmd
       AND ResultCode = '00'
       AND RefundTxType <> 1
);

DECLARE @v_actualMinLedgerYmd VARCHAR(8) =
(
    SELECT MIN(YMD)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE ProcYMD = @p_businessYmd
       AND ExtraSettleFlag = 1
       AND PGNAME IN
           (
               N'allthegate',
               N'dacomcard',
               N'tosscard',
               N'nicecard'
           )
);

SELECT N'V05-03' AS CheckCode,
       N'우대 최소 요청일 대응' AS CheckName,
       N'Error' AS Severity,
       CONVERT(NVARCHAR(200), @v_expectedMinReqYmd) AS ExpectedValue,
       CONVERT(NVARCHAR(200), @v_actualMinLedgerYmd) AS ActualValue,
       NULL AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN ISNULL(@v_expectedMinReqYmd, '')
                       = ISNULL(@v_actualMinLedgerYmd, '')
                THEN 1 ELSE 0 END) AS Passed,
       @p_businessYmd AS SampleBusinessKey,
       N'원천 MIN ReqYMD와 우대 원장 최소 YMD 비교' AS DetailMessage;
```

### V06 PG 수납 통계 검증

세 원천 분기를 모두 유지하여 독립적으로 기대값을 계산한다.

```sql
-- V06-01 세 UNION ALL 분기의 회수금액 합계 대사
DECLARE @v_expectedCollectAmount DECIMAL(38,4) =
(
    SELECT ISNULL(SUM(CONVERT(DECIMAL(38,4), U.CollectAmount)), 0)
      FROM
      (
          SELECT SUM(ISNULL(TXAMT, 0) - ISNULL(PGTOTAL, 0)) AS CollectAmount
            FROM SETTLE_POQ_DB.dbo.TSettleMst
           WHERE INYMD = @p_businessYmd
             AND INSTATE = 1

          UNION ALL

          SELECT SUM(ISNULL(CLCOLLECTAMT, 0)) AS CollectAmount
            FROM SETTLE_POQ_DB.dbo.TTArsPGCollect
           WHERE COLLECTYMD = @p_businessYmd

          UNION ALL

          SELECT SUM(ISNULL(CLCOLLECTAMT, 0)) AS CollectAmount
            FROM SETTLE_POQ_DB.dbo.TBArsPGCollect
           WHERE COLLECTYMD = @p_businessYmd
      ) AS U
);

DECLARE @v_actualCollectAmount DECIMAL(38,4) =
(
    SELECT ISNULL(SUM(CONVERT(DECIMAL(38,4), COLLECTAMT)), 0)
      FROM SETTLE_POQ_DB.dbo.TStatPGCollect
     WHERE INYMD = @p_businessYmd
);

SELECT N'V06-01' AS CheckCode,
       N'원장 대 PG 수납 통계 합계' AS CheckName,
       N'Critical' AS Severity,
       CONVERT(NVARCHAR(200), @v_expectedCollectAmount) AS ExpectedValue,
       CONVERT(NVARCHAR(200), @v_actualCollectAmount) AS ActualValue,
       @v_actualCollectAmount - @v_expectedCollectAmount AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN @v_actualCollectAmount = @v_expectedCollectAmount
                THEN 1 ELSE 0 END) AS Passed,
       @p_businessYmd AS SampleBusinessKey,
       N'TSettleMst와 두 ARS 원천의 합을 통계와 비교' AS DetailMessage;
```

```sql
-- V06-02 통계 차변과 대변 항등식
SELECT INYMD,
       CLIENTID,
       PGNAME,
       MALLID,
       LEFTSUMAMT,
       RIGHTSUMAMT,
       COLLECTAMT + PGCOMM + PGVT AS ExpectedLeftAmount,
       SETTLEWILLAMT + AHEADSALESCOMM + AHEADSALESVT + AHEADSETTLEAMT
           AS ExpectedRightAmount
  FROM SETTLE_POQ_DB.dbo.TStatPGCollect
 WHERE INYMD = @p_businessYmd
   AND
   (
       ISNULL(LEFTSUMAMT, 0)
           <> ISNULL(COLLECTAMT, 0)
            + ISNULL(PGCOMM, 0)
            + ISNULL(PGVT, 0)
       OR
       ISNULL(RIGHTSUMAMT, 0)
           <> ISNULL(SETTLEWILLAMT, 0)
            + ISNULL(AHEADSALESCOMM, 0)
            + ISNULL(AHEADSALESVT, 0)
            + ISNULL(AHEADSETTLEAMT, 0)
   );
```

### V07 기타 정산 누락 검증

```sql
-- V07-01 TSettleMst와 TSettleMiss 누적 금액 비교
WITH TouchedGroup AS
(
    SELECT DISTINCT A.ClientID,
           A.OutYMD
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
),
LedgerAmount AS
(
    SELECT A.ClientID,
           A.OutYMD,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(A.CLTotal, 0))) AS ExpectedAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
      INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
        ON B.YMD = A.YMD
       AND B.ClientID = A.ClientID
       AND B.PGName = A.PGName
       AND B.MallID = A.MallID
      INNER JOIN SETTLE_POQ_DB.dbo.TClient AS C
        ON C.ClientID = A.ClientID
      INNER JOIN TouchedGroup AS T
        ON T.ClientID = A.ClientID
       AND T.OutYMD = A.OutYMD
     WHERE A.OutState = 2
       AND ISNULL(B.TaxFGBill, 2) = 1
     GROUP BY A.ClientID,
              A.OutYMD
),
MissAmount AS
(
    SELECT M.ClientID,
           M.OutYMD,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(M.CLSettleAmt, 0))) AS ActualAmount
      FROM SETTLE_POQ_DB.dbo.TSettleMiss AS M
      INNER JOIN TouchedGroup AS T
        ON T.ClientID = M.ClientID
       AND T.OutYMD = M.OutYMD
     WHERE M.OutState = 2
       AND ISNULL(M.IssueType, 0) = 15
     GROUP BY M.ClientID,
              M.OutYMD
)
SELECT COALESCE(L.ClientID, M.ClientID) AS ClientID,
       COALESCE(L.OutYMD, M.OutYMD) AS OutYMD,
       ISNULL(L.ExpectedAmount, 0) AS ExpectedAmount,
       ISNULL(M.ActualAmount, 0) AS ActualAmount,
       ISNULL(M.ActualAmount, 0) - ISNULL(L.ExpectedAmount, 0) AS DeltaAmount
  FROM LedgerAmount AS L
  FULL OUTER JOIN MissAmount AS M
    ON M.ClientID = L.ClientID
   AND M.OutYMD = L.OutYMD
 WHERE ISNULL(L.ExpectedAmount, 0) <> ISNULL(M.ActualAmount, 0);
```

```sql
-- V07-02 TSettleMiss ID 충돌
SELECT ID,
       COUNT_BIG(*) AS DuplicateCount
  FROM SETTLE_POQ_DB.dbo.TSettleMiss
 GROUP BY ID
HAVING COUNT_BIG(*) > 1;
```

### V08 정산 요약 검증

```sql
-- V08-01 TSettleByTX 상태별 대사
WITH LedgerSummary AS
(
    SELECT YMD,
           USESTATE,
           ISNULL(ExtraSettleFlag, 9) AS ExtraSettleFlag,
           COUNT_BIG(*) AS ExpectedCount,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))) AS ExpectedTxAmount,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(CLTOTAL, 0))) AS ExpectedClientTotal,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(PGTOTAL, 0))) AS ExpectedPgTotal
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
     GROUP BY YMD,
              USESTATE,
              ISNULL(ExtraSettleFlag, 9)
),
TargetSummary AS
(
    SELECT YMD,
           USESTATE,
           ISNULL(ExtraSettleFlag, 9) AS ExtraSettleFlag,
           SUM(CONVERT(BIGINT, ISNULL(TXCNT, 0))) AS ActualCount,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))) AS ActualTxAmount,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(CLTOTAL, 0))) AS ActualClientTotal,
           SUM(CONVERT(DECIMAL(38,4), ISNULL(PGTOTAL, 0))) AS ActualPgTotal
      FROM SETTLE_POQ_DB.dbo.TSettleByTX
     WHERE YMD = @p_businessYmd
     GROUP BY YMD,
              USESTATE,
              ISNULL(ExtraSettleFlag, 9)
)
SELECT COALESCE(L.YMD, T.YMD) AS YMD,
       COALESCE(L.USESTATE, T.USESTATE) AS USESTATE,
       COALESCE(L.ExtraSettleFlag, T.ExtraSettleFlag) AS ExtraSettleFlag,
       ISNULL(L.ExpectedCount, 0) AS ExpectedCount,
       ISNULL(T.ActualCount, 0) AS ActualCount,
       ISNULL(L.ExpectedTxAmount, 0) AS ExpectedTxAmount,
       ISNULL(T.ActualTxAmount, 0) AS ActualTxAmount,
       ISNULL(L.ExpectedClientTotal, 0) AS ExpectedClientTotal,
       ISNULL(T.ActualClientTotal, 0) AS ActualClientTotal,
       ISNULL(L.ExpectedPgTotal, 0) AS ExpectedPgTotal,
       ISNULL(T.ActualPgTotal, 0) AS ActualPgTotal
  FROM LedgerSummary AS L
  FULL OUTER JOIN TargetSummary AS T
    ON T.YMD = L.YMD
   AND T.USESTATE = L.USESTATE
   AND T.ExtraSettleFlag = L.ExtraSettleFlag
 WHERE ISNULL(L.ExpectedCount, 0) <> ISNULL(T.ActualCount, 0)
    OR ISNULL(L.ExpectedTxAmount, 0) <> ISNULL(T.ActualTxAmount, 0)
    OR ISNULL(L.ExpectedClientTotal, 0) <> ISNULL(T.ActualClientTotal, 0)
    OR ISNULL(L.ExpectedPgTotal, 0) <> ISNULL(T.ActualPgTotal, 0);
```

```sql
-- V08-02 부분취소 회수 지급 요약 스칼라 대사
DECLARE @v_expectedPartialCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
       AND USESTATE = 2
);

DECLARE @v_actualPartialCount BIGINT =
(
    SELECT ISNULL(SUM(CONVERT(BIGINT, TXCNT)), 0)
      FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
     WHERE YMD = @p_businessYmd
);

DECLARE @v_expectedInCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
       AND INSTATE = 1
);

DECLARE @v_actualInCount BIGINT =
(
    SELECT ISNULL(SUM(CONVERT(BIGINT, INCNT)), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByIN
     WHERE YMD = @p_businessYmd
);

DECLARE @v_expectedOutCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
       AND OUTSTATE IN (2, 9)
);

DECLARE @v_actualOutCount BIGINT =
(
    SELECT ISNULL(SUM(CONVERT(BIGINT, OUTCNT)), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleByOUT
     WHERE YMD = @p_businessYmd
       AND OUTSTATE IN (2, 9)
);

SELECT N'V08-02A' AS CheckCode,
       N'부분취소 요약 건수' AS CheckName,
       @v_expectedPartialCount AS ExpectedValue,
       @v_actualPartialCount AS ActualValue,
       @v_actualPartialCount - @v_expectedPartialCount AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN @v_expectedPartialCount = @v_actualPartialCount
                THEN 1 ELSE 0 END) AS Passed
UNION ALL
SELECT N'V08-02B',
       N'수납 요약 건수',
       @v_expectedInCount,
       @v_actualInCount,
       @v_actualInCount - @v_expectedInCount,
       CONVERT(BIT,
           CASE WHEN @v_expectedInCount = @v_actualInCount
                THEN 1 ELSE 0 END)
UNION ALL
SELECT N'V08-02C',
       N'지급 요약 건수',
       @v_expectedOutCount,
       @v_actualOutCount,
       @v_actualOutCount - @v_expectedOutCount,
       CONVERT(BIT,
           CASE WHEN @v_expectedOutCount = @v_actualOutCount
                THEN 1 ELSE 0 END);
```

```sql
-- V08-03 우대 요약 반영 누락
SELECT M.PLTID,
       M.YMD,
       M.CLIENTID,
       M.PGNAME,
       M.ExtraSettleFlag
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS M
 WHERE M.ProcYMD = @p_businessYmd
   AND M.ExtraSettleFlag = 1
   AND NOT EXISTS
       (
           SELECT 1
             FROM SETTLE_POQ_DB.dbo.TSettleByTX AS T
            WHERE T.YMD = M.YMD
              AND T.CLIENTID = M.CLIENTID
              AND T.PGNAME = M.PGNAME
              AND T.MALLID = M.MALLID
              AND ISNULL(T.ExtraSettleFlag, 9)
                    = ISNULL(M.ExtraSettleFlag, 9)
       );
```

### V09 최종 통합 대사 및 게시 승인

```sql
-- V09-01 S12 원장 동결값 재확인
DECLARE @v_frozenRowCount DECIMAL(38,4) =
(
    SELECT MAX(ControlValue)
      FROM batch.BatchControlTotal
     WHERE RunId = @p_runId
       AND StepCode = N'S12'
       AND ControlName = N'Ledger.RowCount'
);

DECLARE @v_currentRowCount DECIMAL(38,4) =
(
    SELECT CONVERT(DECIMAL(38,4), COUNT_BIG(*))
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
);

DECLARE @v_frozenTxAmount DECIMAL(38,4) =
(
    SELECT MAX(ControlValue)
      FROM batch.BatchControlTotal
     WHERE RunId = @p_runId
       AND StepCode = N'S12'
       AND ControlName = N'Ledger.TxAmount'
);

DECLARE @v_currentTxAmount DECIMAL(38,4) =
(
    SELECT ISNULL(SUM(CONVERT(DECIMAL(38,4), ISNULL(TXAMT, 0))), 0)
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_businessYmd
);

SELECT N'V09-01A' AS CheckCode,
       N'원장 동결 행 수' AS CheckName,
       N'Critical' AS Severity,
       CONVERT(NVARCHAR(200), @v_frozenRowCount) AS ExpectedValue,
       CONVERT(NVARCHAR(200), @v_currentRowCount) AS ActualValue,
       @v_currentRowCount - ISNULL(@v_frozenRowCount, 0) AS DeltaValue,
       CONVERT(BIT,
           CASE WHEN @v_frozenRowCount = @v_currentRowCount
                THEN 1 ELSE 0 END) AS Passed,
       @p_businessYmd AS SampleBusinessKey,
       N'S12 이후 원장 행 수 변경 여부' AS DetailMessage
UNION ALL
SELECT N'V09-01B',
       N'원장 동결 거래금액',
       N'Critical',
       CONVERT(NVARCHAR(200), @v_frozenTxAmount),
       CONVERT(NVARCHAR(200), @v_currentTxAmount),
       @v_currentTxAmount - ISNULL(@v_frozenTxAmount, 0),
       CONVERT(BIT,
           CASE WHEN @v_frozenTxAmount = @v_currentTxAmount
                THEN 1 ELSE 0 END),
       @p_businessYmd,
       N'S12 이후 원장 거래금액 변경 여부';
```

```sql
-- V09-02 필수 체크포인트 완전성
WITH RequiredStep AS
(
    SELECT StepCode
      FROM
      (
          VALUES
              (N'S03'),
              (N'S04'),
              (N'S05'),
              (N'S06'),
              (N'S07'),
              (N'S08'),
              (N'S09'),
              (N'S10'),
              (N'S11'),
              (N'S12'),
              (N'S13'),
              (N'S14'),
              (N'S15'),
              (N'S16'),
              (N'S17'),
              (N'S18'),
              (N'S19')
      ) AS V(StepCode)
)
SELECT R.StepCode
  FROM RequiredStep AS R
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
-- V09-03 치명 이슈 집계
DECLARE @v_validationCriticalCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchValidationIssue
     WHERE RunId = @p_runId
       AND Severity IN (N'Error', N'Critical')
);

DECLARE @v_reconciliationCriticalCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchReconciliationIssue
     WHERE RunId = @p_runId
       AND Severity IN (N'Error', N'Critical')
);

SELECT N'V09-03' AS CheckCode,
       N'미해결 오류 이슈 수' AS CheckName,
       N'Critical' AS Severity,
       N'0' AS ExpectedValue,
       CONVERT
       (
           NVARCHAR(200),
           @v_validationCriticalCount + @v_reconciliationCriticalCount
       ) AS ActualValue,
       CONVERT
       (
           DECIMAL(38,4),
           @v_validationCriticalCount + @v_reconciliationCriticalCount
       ) AS DeltaValue,
       CONVERT
       (
           BIT,
           CASE WHEN @v_validationCriticalCount = 0
                  AND @v_reconciliationCriticalCount = 0
                THEN 1 ELSE 0 END
       ) AS Passed,
       CONVERT(NVARCHAR(200), @p_runId) AS SampleBusinessKey,
       N'Error 또는 Critical 이슈가 있으면 성공 게시를 금지한다' AS DetailMessage;
```

```sql
-- V09-04 최종 게시 승인 판정
DECLARE @v_missingCheckpointCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM
      (
          VALUES
              (N'S03'), (N'S04'), (N'S05'), (N'S06'), (N'S07'),
              (N'S08'), (N'S09'), (N'S10'), (N'S11'), (N'S12'),
              (N'S13'), (N'S14'), (N'S15'), (N'S16'), (N'S17'),
              (N'S18'), (N'S19')
      ) AS R(StepCode)
     WHERE NOT EXISTS
           (
               SELECT 1
                 FROM batch.BatchCheckpoint AS C
                WHERE C.RunId = @p_runId
                  AND C.StepCode = R.StepCode
                  AND C.CheckpointStatus = N'Succeeded'
           )
);

DECLARE @v_failedJournalCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchStepJournal
     WHERE RunId = @p_runId
       AND StepStatus = N'Failed'
);

DECLARE @v_criticalIssueCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchValidationIssue
     WHERE RunId = @p_runId
       AND Severity IN (N'Error', N'Critical')
);

DECLARE @v_criticalReconCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchReconciliationIssue
     WHERE RunId = @p_runId
       AND Severity IN (N'Error', N'Critical')
);

DECLARE @v_lockHeldCount BIGINT =
(
    SELECT COUNT_BIG(*)
      FROM batch.BatchRunLock
     WHERE JobName = N'POQSettleBatch17'
       AND BatchYmd = @p_businessYmd
       AND OwnerRunId = @p_runId
       AND LockStatus = N'Held'
);

SELECT N'V09-04' AS CheckCode,
       N'최종 게시 승인' AS CheckName,
       N'Critical' AS Severity,
       N'승인' AS ExpectedValue,
       CASE
           WHEN @v_missingCheckpointCount = 0
            AND @v_failedJournalCount = 0
            AND @v_criticalIssueCount = 0
            AND @v_criticalReconCount = 0
            AND @v_lockHeldCount = 1
               THEN N'승인'
           ELSE N'거부'
       END AS ActualValue,
       NULL AS DeltaValue,
       CONVERT
       (
           BIT,
           CASE
               WHEN @v_missingCheckpointCount = 0
                AND @v_failedJournalCount = 0
                AND @v_criticalIssueCount = 0
                AND @v_criticalReconCount = 0
                AND @v_lockHeldCount = 1
                   THEN 1
               ELSE 0
           END
       ) AS Passed,
       CONVERT(NVARCHAR(200), @p_runId) AS SampleBusinessKey,
       CONCAT
       (
           N'누락 체크포인트 ',
           @v_missingCheckpointCount,
           N', 실패 저널 ',
           @v_failedJournalCount,
           N', 검증 이슈 ',
           @v_criticalIssueCount,
           N', 대사 이슈 ',
           @v_criticalReconCount,
           N', 보유 잠금 ',
           @v_lockHeldCount
       ) AS DetailMessage;
```

### 검증 결과 저장 템플릿

애플리케이션은 검증 SQL 결과를 판정한 후 값 전체를 매개변수로 전달한다. SQL 텍스트에 실행값을 연결하지 않는다.

```sql
-- SQL_CAPTURE_CONTROL_TOTAL
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

```sql
-- SQL_CAPTURE_RECONCILIATION_ISSUE
INSERT INTO batch.BatchReconciliationIssue
(
    RunId,
    StepCode,
    IssueCode,
    Severity,
    ExpectedValue,
    ActualValue,
    DeltaValue,
    SampleBusinessKey,
    DetectedAtUtc
)
VALUES
(
    @p_runId,
    N'S20',
    @p_issueCode,
    @p_severity,
    @p_expectedValue,
    @p_actualValue,
    @p_deltaValue,
    @p_sampleBusinessKey,
    SYSUTCDATETIME()
);
```

### 검증 추적성

| 검증 세트 | 선행 또는 사후 단계 | 게시 차단 기준 |
|---|---|---|
| V00 | S01 사전조건 | 접근성, 날짜, 객체, 우대 분류 중첩 실패 |
| V01 | S04 사후조건 | 스냅샷 누락 또는 자연키 중복 |
| V02 | S05∼S11 사후조건 | 원거래 없는 취소, 비정상 중복, 상태별 구조 오류 |
| V03 | S07∼S11 사후조건 | 수수료·VAT·총액 항등식 불일치 |
| V04 | S09 사후조건 | 수납·지급 상태와 일자 모순 |
| V05 | S10∼S11 사후조건 | 우대 범위, 부호, 원천 요청일 또는 중복 오류 |
| V06 | S13 사후조건 | 세 원천 합계와 통계 불일치 |
| V07 | S14 사후조건 | `TSettleMiss` 누적 금액 또는 ID 충돌 |
| V08 | S15∼S18 사후조건 | 네 요약 테이블의 건수·금액 불일치 |
| V09 | S20 및 S21 사전조건 | 동결값 변경, 체크포인트 누락, 실패 저널, 치명 이슈 존재 |
