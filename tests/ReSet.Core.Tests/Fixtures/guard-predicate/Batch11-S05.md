### S05 — 일별 요율 스냅샷

**대체 레거시:** `dbo.UP_Util_PG_Client_CMRate_Ins`

#### 인터페이스 및 대상

- 입력 바인딩: `@pi_strYMD CHAR(8) -> p_ymd`
  - 실행 계층은 `p_ymd`를 반드시 `CHAR(8)`로 바인딩한다.
- 출력 매핑: `@po_intRetVal INT OUTPUT -> po_intRetVal`
  - 실패 시 원본 오류 코드를 `po_intRetVal`과 `batch.BatchStepJournal.LegacyReturnCode`에 동일하게 기록한다.
  - 정상 종료 시 원본 프로시저가 출력값을 명시적으로 설정하지 않으므로 `po_intRetVal`과 `LegacyReturnCode`는 `NULL`로 유지한다.
- 재시작 또는 사전 검증 우회를 위한 입력은 추가하지 않는다.

대상 테이블은 다음 다섯 개이며, 동일 기준일의 스냅샷을 하나의 원자적 작업으로 교체한다.

1. `SETTLE_POQ_DB.dbo.TPGSettleRate`
2. `SETTLE_POQ_DB.dbo.TClientSettleRate`
3. `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra`
4. `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra`
5. `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo`

#### 실행 및 트랜잭션 설계

- C# 단계 핸들러는 사전 검증과 열 개의 원본 DML을 명세 순서대로 실행한다.
- 전체 업무 처리는 **단일 트랜잭션**이며 모든 문장은 **SNAPSHOT 격리** 의무를 만족해야 한다.
- 다섯 대상 테이블의 같은 `YMD` 스냅샷이 함께 성공하거나 함께 롤백되어야 하므로 청크 처리하지 않는다.
- 원본의 모든 `NOLOCK` 및 `WITH (NOLOCK)` 힌트는 제거한다. 원천 테이블은 동일 SNAPSHOT 시점으로 읽는다.
- 단일 트랜잭션 롤백으로 다섯 대상 테이블이 모두 복구되므로 섀도 테이블과 실패 후 보상 `DELETE`를 사용하지 않는다.
- 사전 검증은 매 호출마다 무조건 수행한다. `TSettleMst`에 기지급 행이 있으면 원본과 동일한 `-9`로 실패 처리하며 어떤 대상 DML도 실행하지 않는다.

```pseudocode
function executeS05(p_ymd, output po_intRetVal)
{
    // p_ymd는 @pi_strYMD CHAR(8)에 대응한다.
    po_intRetVal = UNSET
    currentStepErrorCode = NULL
    statementName = NULL

    conn = connectionFactory.open()
    tx = conn.beginTransaction()
    // 이 트랜잭션의 모든 데이터베이스 문장은 SNAPSHOT 격리 의무를 충족해야 한다.

    try
    {
        statementName = "IF 1 기지급 내역 사전 검증"
        alreadySettled = conn.queryScalar(SQL_PRECHECK_SETTLED, {
            p_ymd: p_ymd
        })

        if (alreadySettled == 1)
        {
            currentStepErrorCode = -9
            po_intRetVal = -9
            tx.rollbackIfOpen()

            writeStepJournal(
                status: "Failed",
                LegacyReturnCode: currentStepErrorCode,
                errorMessage: errorContext.messageWithStatement(statementName)
            )
            stopPipeline()
        }

        statementName = "DELETE 1 TPGSettleRate"
        currentStepErrorCode = -1
        conn.execute(SQL_DELETE_PG_RATE, { p_ymd: p_ymd })

        statementName = "INSERT 1 TPGSettleRate"
        currentStepErrorCode = -2
        conn.execute(SQL_INSERT_PG_RATE, { p_ymd: p_ymd })

        statementName = "DELETE 2 TClientSettleRate"
        currentStepErrorCode = -3
        conn.execute(SQL_DELETE_CLIENT_RATE, { p_ymd: p_ymd })

        statementName = "INSERT 2 TClientSettleRate"
        currentStepErrorCode = -4
        conn.execute(SQL_INSERT_CLIENT_RATE, { p_ymd: p_ymd })

        statementName = "DELETE 3 TPGSettleRate4Extra"
        currentStepErrorCode = -5
        conn.execute(SQL_DELETE_PG_EXTRA_RATE, { p_ymd: p_ymd })

        statementName = "INSERT 3 TPGSettleRate4Extra"
        currentStepErrorCode = -6
        conn.execute(SQL_INSERT_PG_EXTRA_RATE, { p_ymd: p_ymd })

        statementName = "DELETE 4 TClientSettleRate4Extra"
        currentStepErrorCode = -7
        conn.execute(SQL_DELETE_CLIENT_EXTRA_RATE, { p_ymd: p_ymd })

        statementName = "INSERT 4 TClientSettleRate4Extra"
        currentStepErrorCode = -8
        conn.execute(SQL_INSERT_CLIENT_EXTRA_RATE, { p_ymd: p_ymd })

        statementName = "DELETE 5 TClientSettleRate4MobileCo"
        currentStepErrorCode = -9
        conn.execute(SQL_DELETE_CLIENT_MOBILE_RATE, { p_ymd: p_ymd })

        statementName = "INSERT 5 TClientSettleRate4MobileCo"
        currentStepErrorCode = -10
        conn.execute(SQL_INSERT_CLIENT_MOBILE_RATE, { p_ymd: p_ymd })

        tx.commit()

        // 원본 정상 경로는 @po_intRetVal을 설정하지 않는다.
        po_intRetVal = UNSET
        writeStepSuccess(LegacyReturnCode: NULL)
    }
    catch
    {
        tx.rollbackIfOpen()
        po_intRetVal = currentStepErrorCode

        writeStepJournal(
            status: "Failed",
            LegacyReturnCode: currentStepErrorCode,
            errorMessage: errorContext.messageWithStatement(statementName)
        )
        stopPipeline()
    }
}
```

각 `SQL_*` 이름은 애플리케이션이 개별적으로 전송하는 독립 SQL 문장을 나타낸다.

```sql
-- SQL_PRECHECK_SETTLED
SELECT
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM SETTLE_POQ_DB.dbo.TSettleMst
             WHERE PLTID = 'POQ'
               AND YMD = @p_ymd
               AND OutState IN (1, 5)
               AND OutYMD IS NOT NULL
        )
        THEN 1
        ELSE 0
    END;
```

```sql
-- SQL_DELETE_PG_RATE
/* DELETE 1: 기준일 PG사 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_PG_RATE
/* INSERT 1: PG사 수수료율 스냅샷 등록 */
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
-- SQL_DELETE_CLIENT_RATE
/* DELETE 2: 기준일 고객사 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_RATE
/* INSERT 2: 고객사 수수료율 스냅샷 등록 */
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
-- SQL_DELETE_PG_EXTRA_RATE
/* DELETE 3: 기준일 PG사 차액정산 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_PG_EXTRA_RATE
/* INSERT 3: PG사 차액정산 수수료율 스냅샷 등록 */
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
-- SQL_DELETE_CLIENT_EXTRA_RATE
/* DELETE 4: 기준일 고객사 차액정산 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_EXTRA_RATE
/* INSERT 4: 고객사 차액정산 수수료율 스냅샷 등록 */
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
-- SQL_DELETE_CLIENT_MOBILE_RATE
/* DELETE 5: 기준일 통신사별 고객사 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_MOBILE_RATE
/* INSERT 5: 통신사별 고객사 수수료율 스냅샷 등록 */
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

#### 오류 코드 및 복구 기준

| 실패 지점 | 원본 오류 코드 | 처리 |
|---|---:|---|
| 기지급 내역 사전 검증 결과 존재 | `-9` | DML 실행 없이 트랜잭션 롤백 후 중단 |
| `DELETE 1` — `TPGSettleRate` | `-1` | 전체 업무 트랜잭션 롤백 |
| `INSERT 1` — `TPGSettleRate` | `-2` | 전체 업무 트랜잭션 롤백 |
| `DELETE 2` — `TClientSettleRate` | `-3` | 전체 업무 트랜잭션 롤백 |
| `INSERT 2` — `TClientSettleRate` | `-4` | 전체 업무 트랜잭션 롤백 |
| `DELETE 3` — `TPGSettleRate4Extra` | `-5` | 전체 업무 트랜잭션 롤백 |
| `INSERT 3` — `TPGSettleRate4Extra` | `-6` | 전체 업무 트랜잭션 롤백 |
| `DELETE 4` — `TClientSettleRate4Extra` | `-7` | 전체 업무 트랜잭션 롤백 |
| `INSERT 4` — `TClientSettleRate4Extra` | `-8` | 전체 업무 트랜잭션 롤백 |
| `DELETE 5` — `TClientSettleRate4MobileCo` | `-9` | 전체 업무 트랜잭션 롤백 |
| `INSERT 5` — `TClientSettleRate4MobileCo` | `-10` | 전체 업무 트랜잭션 롤백 |

`-9`는 원본에서 기지급 사전 검증과 `DELETE 5` 실패에 중복 배정되어 있다. 코드는 이를 변경하거나 재매핑하지 않으며, `statementName`과 `ErrorMessage`에 각각 `IF 1 기지급 내역 사전 검증` 또는 `DELETE 5 TClientSettleRate4MobileCo`를 기록하여 실패 지점만 구분한다. 실패 시 열린 단일 트랜잭션을 롤백하면 다섯 대상 테이블 모두 실행 전 상태로 복원되며 부분 커밋은 남지 않는다.