### S04 — 일별 수수료율 스냅샷 재구축

`dbo.UP_Util_PG_Client_CMRate_Ins`를 대체하며, `S03` 성공 후 `S05`보다 먼저 순차 실행한다. 다음 5개 대상 테이블을 동일한 업무 트랜잭션에서 원본 순서대로 재구축하며 병렬 실행하지 않는다.

1. `SETTLE_POQ_DB.dbo.TPGSettleRate`
2. `SETTLE_POQ_DB.dbo.TClientSettleRate`
3. `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra`
4. `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra`
5. `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo`

#### 인터페이스 및 반환값 매핑

| 원본 인터페이스 | 배치 애플리케이션 매핑 | 처리 방식 |
|---|---|---|
| `@pi_strYMD CHAR(8)` | `p_ymd` | SQL에 반드시 `CHAR(8)`로 바인딩한다. |
| `@po_intRetVal INT OUTPUT` | `legacyReturnCode` 및 `batch.BatchStepJournal.LegacyReturnCode` | 출력 매개변수로 SQL에 바인딩하지 않는다. 실패 시 원본 코드를 기록하며, 성공 시 원본이 값을 명시적으로 설정하지 않으므로 `NULL`로 유지한다. |

`runId`와 `stepCode = N'S04'`는 오케스트레이션 실행 컨텍스트이며 원본 프로시저 입력 매개변수에 추가하지 않는다. 재시작 여부도 단계 입력으로 받지 않는다.

#### 트랜잭션, 잠금 및 복구

- 업무 트랜잭션 전체는 **SNAPSHOT 격리**에서 실행한다.
- 기지급 내역 가드는 호출될 때마다 무조건 실행하며 우회 옵션을 두지 않는다.
- 5개의 DELETE-INSERT 쌍은 원본 순서를 엄격히 유지한다.
- 일자 전체 재구축과 복합 조인을 포함하므로 청크 키를 추가하지 않는다.
- 모든 변경은 단일 트랜잭션에서 커밋되므로 Shadow Table과 롤백 후 보상 DELETE를 사용하지 않는다.
- 실패 시 단일 롤백으로 다섯 대상 테이블을 모두 실행 전 상태로 복원한다.
- 원본의 모든 `NOLOCK` 및 `WITH (NOLOCK)` 힌트는 제거한다. SNAPSHOT 읽기 일관성을 훼손하는 힌트를 다시 추가해서는 안 된다.
- 가드의 `-9`와 `DELETE 5`의 `-9`는 원본에서 중복된 코드이므로 변경하지 않는다. 대신 실패 메시지에 `Guard` 또는 `DELETE 5` 문장 식별자를 기록해 원인을 구분한다.

```csharp
var conn = connectionFactory.open();
const string stepCode = "S04";

int? currentStepErrorCode = null;
string currentStatement = "STEP_START";
int? legacyReturnCode = null;

// 단계 시작 행은 업무 트랜잭션과 분리하여 먼저 확정한다.
conn.beginTransaction(); // SNAPSHOT 격리 의무
try
{
    conn.execute(SQL_INSERT_STEP_START, new
    {
        p_runId = runId,
        p_stepCode = stepCode
    });
    conn.commit();
}
catch
{
    conn.rollback();
    throw;
}

conn.beginTransaction(); // S04 업무 전체의 단일 SNAPSHOT 트랜잭션

try
{
    currentStepErrorCode = null;
    currentStatement = "GUARD_TSETTLEMST";
    var settledExists = conn.queryScalar(SQL_CHECK_SETTLED_LEDGER, new
    {
        p_ymd = batchYmd
    });

    if (settledExists == 1)
    {
        // 원본 사전 검증 반환코드이며 DELETE 5 코드와 중복된다.
        currentStepErrorCode = -9;
        legacyReturnCode = -9;
        currentStatement = "GUARD_TSETTLEMST_ALREADY_SETTLED";
        raiseStepFailure("기지급 정산행이 존재하여 S04를 중단한다.");
    }

    currentStepErrorCode = -1;
    currentStatement = "DELETE 1 SETTLE_POQ_DB.dbo.TPGSettleRate";
    conn.execute(SQL_DELETE_PG_SETTLE_RATE, new { p_ymd = batchYmd });

    currentStepErrorCode = -2;
    currentStatement = "INSERT 1 SETTLE_POQ_DB.dbo.TPGSettleRate";
    conn.execute(SQL_INSERT_PG_SETTLE_RATE, new { p_ymd = batchYmd });

    currentStepErrorCode = -3;
    currentStatement = "DELETE 2 SETTLE_POQ_DB.dbo.TClientSettleRate";
    conn.execute(SQL_DELETE_CLIENT_SETTLE_RATE, new { p_ymd = batchYmd });

    currentStepErrorCode = -4;
    currentStatement = "INSERT 2 SETTLE_POQ_DB.dbo.TClientSettleRate";
    conn.execute(SQL_INSERT_CLIENT_SETTLE_RATE, new { p_ymd = batchYmd });

    currentStepErrorCode = -5;
    currentStatement = "DELETE 3 SETTLE_POQ_DB.dbo.TPGSettleRate4Extra";
    conn.execute(SQL_DELETE_PG_SETTLE_RATE_EXTRA, new { p_ymd = batchYmd });

    currentStepErrorCode = -6;
    currentStatement = "INSERT 3 SETTLE_POQ_DB.dbo.TPGSettleRate4Extra";
    conn.execute(SQL_INSERT_PG_SETTLE_RATE_EXTRA, new { p_ymd = batchYmd });

    currentStepErrorCode = -7;
    currentStatement = "DELETE 4 SETTLE_POQ_DB.dbo.TClientSettleRate4Extra";
    conn.execute(SQL_DELETE_CLIENT_SETTLE_RATE_EXTRA, new { p_ymd = batchYmd });

    currentStepErrorCode = -8;
    currentStatement = "INSERT 4 SETTLE_POQ_DB.dbo.TClientSettleRate4Extra";
    conn.execute(SQL_INSERT_CLIENT_SETTLE_RATE_EXTRA, new { p_ymd = batchYmd });

    currentStepErrorCode = -9;
    currentStatement = "DELETE 5 SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo";
    conn.execute(SQL_DELETE_CLIENT_SETTLE_RATE_MOBILE, new { p_ymd = batchYmd });

    currentStepErrorCode = -10;
    currentStatement = "INSERT 5 SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo";
    conn.execute(SQL_INSERT_CLIENT_SETTLE_RATE_MOBILE, new { p_ymd = batchYmd });

    currentStepErrorCode = null;
    currentStatement = "VALIDATE_S04_COUNTS";
    var validationRows = conn.queryRows(SQL_VALIDATE_S04_COUNTS, new
    {
        p_ymd = batchYmd
    });

    if (validationRows.any(row => row.ExpectedCount != row.ActualCount))
    {
        raiseStepFailure("S04 원천 및 대상 건수가 일치하지 않는다.");
    }

    // 원본 성공 경로는 @po_intRetVal을 명시적으로 설정하지 않는다.
    legacyReturnCode = null;
    currentStepErrorCode = null;
    currentStatement = "MARK_STEP_SUCCEEDED";

    conn.execute(SQL_MARK_STEP_SUCCEEDED, new
    {
        p_runId = runId,
        p_stepCode = stepCode,
        p_legacyReturnCode = legacyReturnCode
    });

    conn.commit();
}
catch (Exception failure)
{
    conn.rollback();

    conn.beginTransaction(); // 실패 저널 기록도 SNAPSHOT 격리 의무
    try
    {
        conn.execute(SQL_MARK_STEP_FAILED, new
        {
            p_runId = runId,
            p_stepCode = stepCode,
            p_legacyReturnCode = currentStepErrorCode,
            p_errorMessage = sanitize(currentStatement + ": " + failure.Message)
        });
        conn.commit();
    }
    catch
    {
        conn.rollback();
        throw;
    }

    legacyReturnCode = currentStepErrorCode;
    throw;
}
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_CHECK_SETTLED_LEDGER
SELECT CASE
           WHEN EXISTS
           (
               SELECT PLTID
                 FROM SETTLE_POQ_DB.dbo.TSettleMst
                WHERE YMD = @p_ymd
                  AND OutState IN (1,5)
                  AND OutYMD IS NOT NULL
           )
           THEN 1
           ELSE 0
       END AS SettledExists;
```

```sql
-- SQL_DELETE_PG_SETTLE_RATE
/* DELETE 1: 정산기준일 PG 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_PG_SETTLE_RATE
/* INSERT 1: 정상 상태 PG 수수료율 등록 */
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
SELECT @p_ymd,
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
-- SQL_DELETE_CLIENT_SETTLE_RATE
/* DELETE 2: 정산기준일 고객사 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_SETTLE_RATE
/* INSERT 2: 활성 및 당일 해지 고객사 수수료율 등록 */
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
SELECT @p_ymd,
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
 WHERE A.USESTATE IN (0,4,5,6)
   AND B.USESTATE IN (0,4)

UNION ALL

SELECT @p_ymd,
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
-- SQL_DELETE_PG_SETTLE_RATE_EXTRA
/* DELETE 3: 정산기준일 PG 차액정산 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_PG_SETTLE_RATE_EXTRA
/* INSERT 3: 지정 PG사 차액정산 수수료율 등록 */
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
SELECT @p_ymd,
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
-- SQL_DELETE_CLIENT_SETTLE_RATE_EXTRA
/* DELETE 4: 정산기준일 고객사 차액정산 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_SETTLE_RATE_EXTRA
/* INSERT 4: 활성 및 당일 해지 고객사 차액정산 수수료율 등록 */
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
SELECT @p_ymd,
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
 WHERE A.UseState IN (0,4,5,6)
   AND B.UseState IN (0,4)

UNION ALL

SELECT @p_ymd,
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
-- SQL_DELETE_CLIENT_SETTLE_RATE_MOBILE
/* DELETE 5: 정산기준일 통신사별 고객사 수수료율 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_INSERT_CLIENT_SETTLE_RATE_MOBILE
/* INSERT 5: 통신사별 고객사 수수료율 등록 */
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
SELECT @p_ymd,
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

#### 원본 오류 코드 보존

| 실패 지점 | 원본 코드 | 통합 배치 처리 |
|---|---:|---|
| 기지급 내역 가드 충족 | `-9` | 업무 DML을 수행하지 않고 롤백한 후 실패 저널에 기록 |
| `DELETE 1` | `-1` | `SETTLE_POQ_DB.dbo.TPGSettleRate` 삭제 실패 |
| `INSERT 1` | `-2` | `SETTLE_POQ_DB.dbo.TPGSettleRate` 적재 실패 |
| `DELETE 2` | `-3` | `SETTLE_POQ_DB.dbo.TClientSettleRate` 삭제 실패 |
| `INSERT 2` | `-4` | `SETTLE_POQ_DB.dbo.TClientSettleRate` 적재 실패 |
| `DELETE 3` | `-5` | `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra` 삭제 실패 |
| `INSERT 3` | `-6` | `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra` 적재 실패 |
| `DELETE 4` | `-7` | `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra` 삭제 실패 |
| `INSERT 4` | `-8` | `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra` 적재 실패 |
| `DELETE 5` | `-9` | `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo` 삭제 실패 |
| `INSERT 5` | `-10` | `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo` 적재 실패 |
| 정상 완료 | 명시적 설정 없음 | `@po_intRetVal` 대응값과 `LegacyReturnCode`를 `NULL`로 유지 |

#### 정합성 검증 SQL

검증은 업무 트랜잭션 안에서 DML과 동일한 SNAPSHOT을 사용한다. 각 원천과 대상을 독립 스칼라 하위질의로 집계하며 집계 결과 간 `CROSS JOIN`을 사용하지 않는다.

```sql
-- SQL_VALIDATE_S04_COUNTS
SELECT N'SETTLE_POQ_DB.dbo.TPGSettleRate' AS TargetTable,
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TPGCMRate
            WHERE USESTATE = 0
       ) AS ExpectedCount,
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TPGSettleRate
            WHERE YMD = @p_ymd
       ) AS ActualCount

UNION ALL

SELECT N'SETTLE_POQ_DB.dbo.TClientSettleRate',
       (
           SELECT COUNT_BIG(*)
             FROM
             (
                 SELECT B.CLIENTID
                   FROM SETTLE_POQ_DB.dbo.TClientContract AS A
                   JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
                     ON A.CLIENTID = B.CLIENTID
                  WHERE A.USESTATE IN (0,4,5,6)
                    AND B.USESTATE IN (0,4)

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
             ) AS ExpectedClientRate
       ),
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TClientSettleRate
            WHERE YMD = @p_ymd
       )

UNION ALL

SELECT N'SETTLE_POQ_DB.dbo.TPGSettleRate4Extra',
       (
           SELECT COUNT_BIG(*)
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
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
            WHERE YMD = @p_ymd
       )

UNION ALL

SELECT N'SETTLE_POQ_DB.dbo.TClientSettleRate4Extra',
       (
           SELECT COUNT_BIG(*)
             FROM
             (
                 SELECT D.ClientID
                   FROM SETTLE_POQ_DB.dbo.TClientContract AS A
                   JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B
                     ON A.ClientID = B.ClientID
                   JOIN SETTLE_POQ_DB.dbo.TClient AS C
                     ON A.ClientID = C.ClientID
                   JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra AS D
                     ON B.ClientID = D.ClientID
                    AND B.PGName = D.PGName
                    AND B.MallID = D.MallID
                  WHERE A.UseState IN (0,4,5,6)
                    AND B.UseState IN (0,4)

                 UNION ALL

                 SELECT D.ClientID
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
             ) AS ExpectedClientExtraRate
       ),
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
            WHERE YMD = @p_ymd
       )

UNION ALL

SELECT N'SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo',
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TClientCMRate AS A
             JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo AS B
               ON A.ClientID = B.ClientID
              AND A.PGName = B.PGName
              AND A.MallID = B.MallID
       ),
       (
           SELECT COUNT_BIG(*)
             FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
            WHERE YMD = @p_ymd
       );
```