### S03 정산율 스냅샷 재구축

#### 목적 및 실행 계약

`dbo.UP_Util_PG_Client_CMRate_Ins`를 애플리케이션 단계로 치환하여 다음 5개 정산율 스냅샷을 기준일 단위로 순차 재구축한다.

- `SETTLE_POQ_DB.dbo.TPGSettleRate`
- `SETTLE_POQ_DB.dbo.TClientSettleRate`
- `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra`
- `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra`
- `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo`

S02 완료 후 S04보다 먼저 실행하며, 다섯 대상 테이블의 `DELETE → INSERT`를 원본 순서 그대로 수행한다. 전체 작업은 **SNAPSHOT 격리 수준의 단일 업무 트랜잭션**으로 처리한다. 청크 커밋, shadow 테이블 및 롤백 후 보상 `DELETE`는 사용하지 않는다. 실패하면 단일 트랜잭션을 롤백하여 다섯 대상 테이블을 모두 실행 전 상태로 복원한다.

원본 조회에 있던 모든 `NOLOCK` 및 `WITH (NOLOCK)` 힌트는 제거한다. S02가 확보한 동일 작업명·기준일 실행 잠금을 유지한 상태에서 수행하며, 동일 대상 테이블의 DML을 병렬 실행하지 않는다.

#### 인터페이스 매핑

- `@pi_strYMD CHAR(8) -> p_ymd`
  - 실행 바인딩에서도 반드시 `CHAR(8)`로 전달한다.
- `@po_intRetVal INT OUTPUT -> legacyOutputValue`
  - SQL 바인딩 파라미터로 전달하지 않는다.
  - 실패 시 추적된 원본 코드를 `legacyOutputValue`와 `batch.BatchStepJournal.LegacyReturnCode`에 기록한다.
  - 원본 정상 경로는 출력값을 명시적으로 설정하지 않으므로 성공 시 `legacyOutputValue`와 `LegacyReturnCode`는 `NULL`로 유지한다.

재시작·우회·기지급 검사 생략 파라미터는 추가하지 않는다. 실행 식별자는 S02가 발급한 실행 컨텍스트에서 사용하며 원본 프로시저 입력 인터페이스에 포함시키지 않는다.

#### 애플리케이션 제어 흐름

```csharp
const string stepCode = "S03";
int? currentStepErrorCode = null;
int? legacyOutputValue = null;
string currentStatement = null;

conn = connectionFactory.open();

// 공통 시작 저널과 Pending 체크포인트를 별도 트랜잭션으로 기록한다.
writeStepStartJournal(conn, runId, stepCode);

try
{
    workTx = conn.beginTransaction(); // SNAPSHOT 격리 의무

    // 호출될 때마다 무조건 수행한다. 재시작 여부로 우회하지 않는다.
    currentStatement = "기지급 사전 검증";
    currentStepErrorCode = null;
    bool alreadySettled = repository.queryScalar(
        conn,
        workTx,
        SQL_PRECHECK_SETTLED,
        { p_ymd: typedChar8(batchYmd) });

    if (alreadySettled)
    {
        // 원본 사전 검증 반환 코드
        currentStepErrorCode = -9;
        legacyOutputValue = -9;
        throw controlledFailure("기지급 정산 행이 존재함");
    }

    currentStatement = "DELETE 1: TPGSettleRate 기준일 삭제";
    currentStepErrorCode = -1;
    repository.execute(conn, workTx, SQL_DELETE_1, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "INSERT 1: TPGSettleRate 재적재";
    currentStepErrorCode = -2;
    repository.execute(conn, workTx, SQL_INSERT_1, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "DELETE 2: TClientSettleRate 기준일 삭제";
    currentStepErrorCode = -3;
    repository.execute(conn, workTx, SQL_DELETE_2, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "INSERT 2: TClientSettleRate 재적재";
    currentStepErrorCode = -4;
    repository.execute(conn, workTx, SQL_INSERT_2, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "DELETE 3: TPGSettleRate4Extra 기준일 삭제";
    currentStepErrorCode = -5;
    repository.execute(conn, workTx, SQL_DELETE_3, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "INSERT 3: TPGSettleRate4Extra 재적재";
    currentStepErrorCode = -6;
    repository.execute(conn, workTx, SQL_INSERT_3, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "DELETE 4: TClientSettleRate4Extra 기준일 삭제";
    currentStepErrorCode = -7;
    repository.execute(conn, workTx, SQL_DELETE_4, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "INSERT 4: TClientSettleRate4Extra 재적재";
    currentStepErrorCode = -8;
    repository.execute(conn, workTx, SQL_INSERT_4, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "DELETE 5: TClientSettleRate4MobileCo 기준일 삭제";
    currentStepErrorCode = -9;
    repository.execute(conn, workTx, SQL_DELETE_5, {
        p_ymd: typedChar8(batchYmd)
    });

    currentStatement = "INSERT 5: TClientSettleRate4MobileCo 재적재";
    currentStepErrorCode = -10;
    repository.execute(conn, workTx, SQL_INSERT_5, {
        p_ymd: typedChar8(batchYmd)
    });

    // 이 검증은 이행 과정에서 추가된 문장이므로 원본 오류 코드를 부여하지 않는다.
    currentStatement = "S03 적재 건수 검증";
    currentStepErrorCode = null;
    validationRows = repository.queryRows(conn, workTx, SQL_VALIDATE_COUNTS, {
        p_ymd: typedChar8(batchYmd)
    });

    if (validationRows.any(row => row.ExpectedCount != row.ActualCount))
        throw controlledFailure("정산율 스냅샷 원천·대상 건수 불일치");

    legacyOutputValue = null;

    currentStatement = "S03 성공 저널 갱신";
    currentStepErrorCode = null;
    markStepSucceeded(
        conn,
        workTx,
        runId,
        stepCode,
        legacyReturnCode: null);

    workTx.commit();
}
catch (failure)
{
    if (workTx is open)
        workTx.rollback();

    legacyOutputValue = currentStepErrorCode;

    // -9는 사전 검증과 DELETE 5가 공유하므로 currentStatement를 함께 기록한다.
    writeStepFailureJournal(
        conn,
        runId,
        stepCode,
        legacyReturnCode: currentStepErrorCode,
        errorMessage: currentStatement + ": " + failure.message);

    stopPipeline();
}
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_PRECHECK_SETTLED
SELECT CASE
           WHEN EXISTS
           (
               SELECT 1
                 FROM SETTLE_POQ_DB.dbo.TSettleMst
                WHERE PLTID = 1
                  AND YMD = @p_ymd
                  AND OutState IN (1, 5)
                  AND OutYMD IS NOT NULL
           )
           THEN CAST(1 AS BIT)
           ELSE CAST(0 AS BIT)
       END;
```

```sql
-- SQL_DELETE_1
/* DELETE 1: TPGSettleRate 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_1
/* INSERT 1: PG사 정산율 스냅샷 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate
(
    YMD,
    PGNAME,
    MALLID,
    VERSION,
    INCVTAX,
    COMMISSIONTYPE,
    COMMISSIONRATE,
    COMMISSIONFOREIGNRATE,
    COMMISSIONAMT,
    ETCAMT,
    COMMISSIONCANCELFLAG,
    COMMISSIONCANCELAMT,
    COMMISSIONMINAMT,
    UnCollectImpose,
    CollectPeriodID,
    ETCAmtNH
)
SELECT
    @p_ymd,
    PGNAME,
    MALLID,
    VERSION,
    INCVTAX,
    COMMISSIONTYPE,
    COMMISSIONRATE,
    COMMISSIONFOREIGNRATE,
    COMMISSIONAMT,
    ETCAMT,
    COMMISSIONCANCELFLAG,
    COMMISSIONCANCELAMT,
    COMMISSIONMINAMT,
    UnCollectImpose,
    CollectPeriodID,
    ETCAmtNH
FROM SETTLE_POQ_DB.dbo.TPGCMRate
WHERE USESTATE = 0;
```

```sql
-- SQL_DELETE_2
/* DELETE 2: TClientSettleRate 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_2
/* INSERT 2: 고객사 정산율 스냅샷 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate
(
    YMD,
    CLIENTID,
    PGNAME,
    MALLID,
    VERSION,
    INCVTAX,
    COMMISSIONTYPE,
    COMMISSIONRATE,
    COMMISSIONFOREIGNRATE,
    COMMISSIONAMT,
    MINCOMMISSIONAMT,
    ETCAMT,
    SETTLEPERIOD,
    UNCOLLECTIMPOSE,
    USESTATE,
    COMMISSIONCANCELFLAG,
    COMMISSIONCANCELAMT,
    SettlePeriodDD,
    SettleCurrency,
    SettleYMDType,
    SettleBasicSeq,
    ModifyType,
    ModifyCommType,
    ModifyCommRate,
    ModifyCommAmt,
    PartnerCommType,
    PartnerCommRate,
    PartnerCommAmt,
    PartnerMinCommAmt,
    PartnerCommCancelFlag,
    PartnerCommCancelAmt,
    RefundFlag,
    RefundFeeType,
    RefundFee,
    AuthSettleType,
    MinimumUnitCnt,
    SeperateFlag,
    SeperateStandard,
    SeperateType,
    SeperateRate,
    SeperateTarget,
    SettlePeriodFlag,
    TaxFGBill,
    SettlePeriodID
)
SELECT
    @p_ymd,
    B.CLIENTID,
    B.PGNAME,
    B.MALLID,
    B.VERSION,
    B.INCVTAX,
    B.COMMISSIONTYPE,
    B.COMMISSIONRATE,
    B.COMMISSIONFOREIGNRATE,
    B.COMMISSIONAMT,
    B.MINCOMMISSIONAMT,
    B.ETCAMT,
    B.SETTLEPERIOD,
    B.UNCOLLECTIMPOSE,
    B.USESTATE,
    B.COMMISSIONCANCELFLAG,
    B.COMMISSIONCANCELAMT,
    B.SettlePeriodDD,
    A.SettleCurrency,
    A.SettleYMDType,
    A.SettleBasicSeq,
    A.ModifyType,
    A.ModifyCommType,
    A.ModifyCommRate,
    A.ModifyCommAmt,
    B.PartnerCommType,
    B.PartnerCommRate,
    B.PartnerCommAmt,
    B.PartnerMinCommAmt,
    B.PartnerCommCancelFlag,
    B.PartnerCommCancelAmt,
    B.RefundFlag,
    B.RefundFeeType,
    B.RefundFee,
    B.AuthSettleType,
    B.MinimumUnitCnt,
    B.SeperateFlag,
    B.SeperateStandard,
    B.SeperateType,
    B.SeperateRate,
    B.SeperateTarget,
    B.SettlePeriodFlag,
    A.TaxFGBill,
    B.SettlePeriodID
FROM SETTLE_POQ_DB.dbo.TClientContract AS A
JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
  ON A.CLIENTID = B.CLIENTID
WHERE A.USESTATE IN (0, 4, 5, 6)
  AND B.USESTATE IN (0, 4)

UNION ALL

SELECT
    @p_ymd,
    B.CLIENTID,
    B.PGNAME,
    B.MALLID,
    B.VERSION,
    B.INCVTAX,
    B.COMMISSIONTYPE,
    B.COMMISSIONRATE,
    B.COMMISSIONFOREIGNRATE,
    B.COMMISSIONAMT,
    B.MINCOMMISSIONAMT,
    B.ETCAMT,
    B.SETTLEPERIOD,
    B.UNCOLLECTIMPOSE,
    B.USESTATE,
    B.COMMISSIONCANCELFLAG,
    B.COMMISSIONCANCELAMT,
    B.SettlePeriodDD,
    A.SettleCurrency,
    A.SettleYMDType,
    A.SettleBasicSeq,
    A.ModifyType,
    A.ModifyCommType,
    A.ModifyCommRate,
    A.ModifyCommAmt,
    B.PartnerCommType,
    B.PartnerCommRate,
    B.PartnerCommAmt,
    B.PartnerMinCommAmt,
    B.PartnerCommCancelFlag,
    B.PartnerCommCancelAmt,
    B.RefundFlag,
    B.RefundFeeType,
    B.RefundFee,
    B.AuthSettleType,
    B.MinimumUnitCnt,
    B.SeperateFlag,
    B.SeperateStandard,
    B.SeperateType,
    B.SeperateRate,
    B.SeperateTarget,
    B.SettlePeriodFlag,
    A.TaxFGBill,
    B.SettlePeriodID
FROM SETTLE_POQ_DB.dbo.TClientContract AS A
JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
  ON A.CLIENTID = B.CLIENTID
WHERE B.USESTATE = 5
  AND
  (
      A.ContractCancelYMD = @p_ymd
      OR B.ContractCancelYMD = @p_ymd
  );
```

```sql
-- SQL_DELETE_3
/* DELETE 3: TPGSettleRate4Extra 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_3
/* INSERT 3: PG사 차액정산율 스냅샷 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
(
    YMD,
    PGName,
    MallID,
    incVTax,
    CommissionRate,
    CommRate0,
    CommRate1,
    CommRate2,
    CommRate3
)
SELECT
    @p_ymd,
    PGName,
    MallID,
    incVTax,
    CommissionRate,
    CommRate0,
    CommRate1,
    CommRate2,
    CommRate3
FROM SETTLE_POQ_DB.dbo.TPGCMRate
WHERE USESTATE = 0
  AND PGName IN
      (
          'allthegate',
          'dacomcard',
          'tosscard',
          'nicecard'
      );
```

```sql
-- SQL_DELETE_4
/* DELETE 4: TClientSettleRate4Extra 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_4
/* INSERT 4: 고객사 차액정산율 스냅샷 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
(
    YMD,
    ClientID,
    PGName,
    MallID,
    SettlePeriod,
    CompanySalesType,
    ExtraSettleFlag,
    incVTax,
    CommissionRate,
    CommRate0,
    CommRate1,
    CommRate2,
    CommRate3,
    PartnerCommRate,
    PartnerCommRate0,
    PartnerCommRate1,
    PartnerCommRate2,
    PartnerCommRate3,
    ExtraCommFlag,
    ExtraCommTarget,
    SettlePeriodID
)
SELECT
    @p_ymd,
    D.ClientID,
    D.PGName,
    D.MallID,
    ISNULL(B.SettlePeriod, 0),
    C.CompanySalesType,
    C.ExtraSettleFlag,
    B.incVTax,
    B.CommissionRate,
    D.CommRate0,
    D.CommRate1,
    D.CommRate2,
    D.CommRate3,
    ISNULL(B.PartnerCommRate, 0),
    D.PartnerCommRate0,
    D.PartnerCommRate1,
    D.PartnerCommRate2,
    D.PartnerCommRate3,
    B.ExtraCommFlag,
    B.ExtraCommTarget,
    B.SettlePeriodID
FROM SETTLE_POQ_DB.dbo.TClientContract AS A
JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
  ON A.ClientID = B.ClientID
JOIN SETTLE_POQ_DB.dbo.TClient AS C
  ON A.ClientID = C.ClientID
JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra AS D
  ON B.ClientID = D.ClientID
 AND B.PGName = D.PGName
 AND B.MallID = D.MallID
WHERE A.UseState IN (0, 4, 5, 6)
  AND B.UseState IN (0, 4)

UNION ALL

SELECT
    @p_ymd,
    D.ClientID,
    D.PGName,
    D.MallID,
    ISNULL(B.SettlePeriod, 0),
    C.CompanySalesType,
    C.ExtraSettleFlag,
    B.incVTax,
    B.CommissionRate,
    D.CommRate0,
    D.CommRate1,
    D.CommRate2,
    D.CommRate3,
    ISNULL(B.PartnerCommRate, 0),
    D.PartnerCommRate0,
    D.PartnerCommRate1,
    D.PartnerCommRate2,
    D.PartnerCommRate3,
    B.ExtraCommFlag,
    B.ExtraCommTarget,
    B.SettlePeriodID
FROM SETTLE_POQ_DB.dbo.TClientContract AS A
JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
  ON A.ClientID = B.ClientID
JOIN SETTLE_POQ_DB.dbo.TClient AS C
  ON A.ClientID = C.ClientID
JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra AS D
  ON B.ClientID = D.ClientID
 AND B.PGName = D.PGName
 AND B.MallID = D.MallID
WHERE B.USESTATE = 5
  AND B.ContractCancelYMD = @p_ymd;
```

```sql
-- SQL_DELETE_5
/* DELETE 5: TClientSettleRate4MobileCo 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_5
/* INSERT 5: 통신사별 고객사 정산율 스냅샷 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
(
    YMD,
    ClientID,
    PGName,
    MallID,
    MobileCoCommApply,
    MobileCo1,
    MobileCo2,
    MobileCo3,
    MobileCo4,
    MobileCo5,
    MobileCo6
)
SELECT
    @p_ymd,
    A.ClientID,
    A.PGName,
    A.MallID,
    A.MobileCoCommApply,
    B.MobileCo1,
    B.MobileCo2,
    B.MobileCo3,
    B.MobileCo4,
    B.MobileCo5,
    B.MobileCo6
FROM SETTLE_POQ_DB.dbo.TClientCMRate AS A
JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo AS B
  ON A.ClientID = B.ClientID
 AND A.PGName = B.PGName
 AND A.MallID = B.MallID;
```

#### 커밋 전 건수 검증

원천과 대상 집계는 각각 독립 CTE로 계산하고 스칼라 하위질의로 비교한다. 카티션 곱을 유발하는 `CROSS JOIN`은 사용하지 않는다. 하나라도 불일치하면 원본에 없는 오류 코드를 만들지 않고 `LegacyReturnCode = NULL`로 실패 저널을 기록한 뒤 업무 트랜잭션을 롤백한다.

```sql
-- SQL_VALIDATE_COUNTS
WITH
PGExpected AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TPGCMRate
     WHERE USESTATE = 0
),
PGActual AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TPGSettleRate
     WHERE YMD = @p_ymd
),
ClientExpected AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM
      (
          SELECT B.CLIENTID
            FROM SETTLE_POQ_DB.dbo.TClientContract AS A
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
              ON A.CLIENTID = B.CLIENTID
           WHERE A.USESTATE IN (0, 4, 5, 6)
             AND B.USESTATE IN (0, 4)

          UNION ALL

          SELECT B.CLIENTID
            FROM SETTLE_POQ_DB.dbo.TClientContract AS A
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
              ON A.CLIENTID = B.CLIENTID
           WHERE B.USESTATE = 5
             AND
             (
                 A.ContractCancelYMD = @p_ymd
                 OR B.ContractCancelYMD = @p_ymd
             )
      ) AS SourceRows
),
ClientActual AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TClientSettleRate
     WHERE YMD = @p_ymd
),
PGExtraExpected AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TPGCMRate
     WHERE USESTATE = 0
       AND PGName IN
           (
               'allthegate',
               'dacomcard',
               'tosscard',
               'nicecard'
           )
),
PGExtraActual AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
     WHERE YMD = @p_ymd
),
ClientExtraExpected AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM
      (
          SELECT D.ClientID, D.PGName, D.MallID
            FROM SETTLE_POQ_DB.dbo.TClientContract AS A
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
              ON A.ClientID = B.ClientID
            JOIN SETTLE_POQ_DB.dbo.TClient AS C
              ON A.ClientID = C.ClientID
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra AS D
              ON B.ClientID = D.ClientID
             AND B.PGName = D.PGName
             AND B.MallID = D.MallID
           WHERE A.UseState IN (0, 4, 5, 6)
             AND B.UseState IN (0, 4)

          UNION ALL

          SELECT D.ClientID, D.PGName, D.MallID
            FROM SETTLE_POQ_DB.dbo.TClientContract AS A
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
              ON A.ClientID = B.ClientID
            JOIN SETTLE_POQ_DB.dbo.TClient AS C
              ON A.ClientID = C.ClientID
            JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra AS D
              ON B.ClientID = D.ClientID
             AND B.PGName = D.PGName
             AND B.MallID = D.MallID
           WHERE B.USESTATE = 5
             AND B.ContractCancelYMD = @p_ymd
      ) AS SourceRows
),
ClientExtraActual AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
     WHERE YMD = @p_ymd
),
MobileExpected AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TClientCMRate AS A
      JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo AS B
        ON A.ClientID = B.ClientID
       AND A.PGName = B.PGName
       AND A.MallID = B.MallID
),
MobileActual AS
(
    SELECT COUNT_BIG(*) AS Cnt
      FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
     WHERE YMD = @p_ymd
)
SELECT
    N'TPGSettleRate' AS TargetName,
    (SELECT Cnt FROM PGExpected) AS ExpectedCount,
    (SELECT Cnt FROM PGActual) AS ActualCount

UNION ALL

SELECT
    N'TClientSettleRate',
    (SELECT Cnt FROM ClientExpected),
    (SELECT Cnt FROM ClientActual)

UNION ALL

SELECT
    N'TPGSettleRate4Extra',
    (SELECT Cnt FROM PGExtraExpected),
    (SELECT Cnt FROM PGExtraActual)

UNION ALL

SELECT
    N'TClientSettleRate4Extra',
    (SELECT Cnt FROM ClientExtraExpected),
    (SELECT Cnt FROM ClientExtraActual)

UNION ALL

SELECT
    N'TClientSettleRate4MobileCo',
    (SELECT Cnt FROM MobileExpected),
    (SELECT Cnt FROM MobileActual);
```

#### 원본 오류 코드 보존

| 실패 지점 | 원본 오류 코드 | 처리 |
|---|---:|---|
| 기지급 사전 검증 중단 | `-9` | 업무 DML을 시작하지 않고 실패 저널 기록 |
| DELETE 1 | `-1` | 전체 업무 트랜잭션 롤백 |
| INSERT 1 | `-2` | 전체 업무 트랜잭션 롤백 |
| DELETE 2 | `-3` | 전체 업무 트랜잭션 롤백 |
| INSERT 2 | `-4` | 전체 업무 트랜잭션 롤백 |
| DELETE 3 | `-5` | 전체 업무 트랜잭션 롤백 |
| INSERT 3 | `-6` | 전체 업무 트랜잭션 롤백 |
| DELETE 4 | `-7` | 전체 업무 트랜잭션 롤백 |
| INSERT 4 | `-8` | 전체 업무 트랜잭션 롤백 |
| DELETE 5 | `-9` | 전체 업무 트랜잭션 롤백 |
| INSERT 5 | `-10` | 전체 업무 트랜잭션 롤백 |

`-9`는 기지급 사전 검증과 `DELETE 5` 실패가 공유하므로 변경하거나 재채번하지 않는다. 두 원인은 `batch.BatchStepJournal.ErrorMessage`에 기록되는 문장 식별자로 구분한다. 원본 코드가 없는 사전 조회 자체의 SQL 실패, 커밋 전 검증 실패 및 제어 테이블 갱신 실패에는 새 오류 코드를 부여하지 않고 `LegacyReturnCode`를 `NULL`로 기록한다.