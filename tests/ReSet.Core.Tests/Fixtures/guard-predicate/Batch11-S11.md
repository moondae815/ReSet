### S11 | 일반 추가 정산

#### 책임 및 인터페이스

`dbo.UP_UTIL_SETTLE_INS_EXTRA`를 대체하여 영중소 차액정산 데이터를 `SETTLE_POQ_DB.dbo.TSettleMst`에 재구축한다. S10 완료 후 실행하며, 같은 대상 테이블을 변경하는 S06~S12의 순서를 유지한다.

- 입력 매핑: `@pi_strYMD CHAR(8) -> p_ymd`
- 출력 매핑: `@po_intRetVal INT OUT -> result.legacyReturnCode`
  - 호출 어댑터는 레거시 호출 관례대로 초기값을 `0`으로 설정한다.
  - 성공 시 원본 프로시저처럼 별도 성공값을 대입하지 않아 초기값 `0`을 유지한다.
  - 실패 시 `-9`, `-1`, `-2`, `-3`, `-4`, `-21` 중 해당 코드로 변경한다.
  - 원본 오류 코드가 없는 `INSERT 1`과 `UPDATE 3`의 실행 오류는 새 코드를 만들지 않고 `NULL`로 기록한다.
- 대상 테이블: `SETTLE_POQ_DB.dbo.TSettleMst`
- 실행 격리 수준: 단계의 사전 검증과 모든 DML은 하나의 SNAPSHOT 격리 트랜잭션에서 실행해야 한다.
- 청크 처리: 교차 데이터베이스 조인, 순차 UPDATE 및 날짜 범위 재구축을 하나의 원자적 단위로 보존해야 하므로 적용하지 않는다.
- 복구: 단일 트랜잭션 롤백만 사용한다. 섀도 테이블과 롤백 후 보상 DELETE를 사용하지 않는다.
- 원본의 모든 `NOLOCK` 및 `WITH (NOLOCK)` 힌트는 제거한다.

#### 실행 제어 및 오류 추적

구현 컴포넌트 `S11GeneralExtraSettlementStep`은 다음 순서를 보장한다. 지역 변수는 원본 선언 타입과 초기값을 SQL에 유지한다. 각 SQL 실행은 별도 배치이므로 `@v_strReqYMD`는 동일 SNAPSHOT 트랜잭션 안에서 다시 산출한다. 따라서 최초 조회와 같은 원천 버전을 읽는다.

`@v_strCurrYMD`는 최초 가드 SQL에서 캡처하고 UPDATE 2가 실제 사용한 값을 결과로 회수한다. 두 값이 다르면 실행일자 경계를 넘은 것이므로 UPDATE 2 이후라도 커밋하지 않고 전체 트랜잭션을 롤백한 뒤 S11 전체를 재실행한다. 이 값은 SQL 바인딩으로 전달하지 않는다.

```pseudocode
component S11GeneralExtraSettlementStep
{
    // 원본 출력 파라미터 계약
    result.legacyReturnCode = 0

    currentStepErrorCode = NULL
    statementName = NULL

    conn = connectionFactory.open()
    tx = conn.beginTransaction()  // SNAPSHOT 격리 의무

    try
    {
        statementName = "SELECT 1 및 지급확정 사전 검증"
        guard = conn.querySingle(SQL_LOAD_CONTEXT_AND_GUARD, {
            p_ymd: batchYmd
        })

        initialCurrYmd = guard.CurrYmd

        // 호출될 때마다 우회 없이 반드시 수행하는 레거시 -9 가드
        if (guard.IsBlocked == 1)
        {
            tx.rollback()
            result.legacyReturnCode = -9
            currentStepErrorCode = -9

            writeStepJournal(
                runId,
                "S11",
                status: "Failed",
                LegacyReturnCode: -9,
                errorMessage: "지급확정 또는 지급완료된 일반 추가 정산 원장이 존재함"
            )
            stopPipeline()
        }

        statementName = "DELETE 1"
        currentStepErrorCode = -1
        conn.execute(SQL_DELETE_EXISTING, {
            p_ymd: batchYmd
        })

        // INSERT 1에는 원본 오류 코드가 없다.
        statementName = "INSERT 1"
        currentStepErrorCode = NULL
        conn.execute(SQL_INSERT_EXTRA_SETTLEMENT, {
            p_ymd: batchYmd
        })

        statementName = "UPDATE 1"
        currentStepErrorCode = -2
        conn.execute(SQL_UPDATE_EXPECTED_OUT_DATE, {
            p_ymd: batchYmd
        })

        statementName = "UPDATE 2"
        currentStepErrorCode = -3
        update2Result = conn.querySingle(SQL_ADJUST_PAST_OUT_DATE, {
            p_ymd: batchYmd
        })

        if (update2Result.UsedCurrYmd != initialCurrYmd)
        {
            // 원본은 프로시저 시작 시점의 실행일자를 계속 사용하므로
            // 날짜 경계를 넘은 실행은 커밋하지 않는다.
            currentStepErrorCode = -3
            throw stepFailure("UPDATE 2 실행 중 데이터베이스 업무일자가 변경됨")
        }

        // UPDATE 3에는 원본 오류 코드가 없다.
        statementName = "UPDATE 3"
        currentStepErrorCode = NULL
        conn.execute(SQL_REVERSE_EXTRA_COMMISSION_SIGN, {
            p_ymd: batchYmd
        })

        statementName = "UPDATE 4"
        currentStepErrorCode = -4
        conn.execute(SQL_RECALCULATE_TOTALS, {
            p_ymd: batchYmd
        })

        statementName = "UPDATE 5"
        currentStepErrorCode = -21
        conn.execute(SQL_RECALCULATE_INCLUDED_VAT, {
            p_ymd: batchYmd
        })

        statementName = "COMMIT"
        currentStepErrorCode = NULL
        tx.commit()

        // 원본은 성공 시 @po_intRetVal을 변경하지 않는다.
        result.legacyReturnCode = 0
        writeStepSuccess(runId, "S11", LegacyReturnCode: result.legacyReturnCode)
    }
    catch (error)
    {
        tx.rollbackIfOpen()

        if (currentStepErrorCode != NULL)
            result.legacyReturnCode = currentStepErrorCode

        writeStepJournal(
            runId,
            "S11",
            status: "Failed",
            LegacyReturnCode: currentStepErrorCode,
            errorMessage: errorContext.messageWithStatement(statementName)
        )
        stopPipeline()
    }
}
```

#### 사전 조회 및 무조건 실행 가드

`OutState IN (1,5)`이고 `OutYMD`가 설정된 기존 원장은 지급확정 또는 지급완료 상태로 간주한다. 이 검증은 호출될 때마다 반드시 실행하며 재시작·우회 입력으로 비활성화하지 않는다.

```sql
-- SQL_LOAD_CONTEXT_AND_GUARD
DECLARE @v_strReqYMD VARCHAR(8) = '';
DECLARE @v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112);

/* SELECT 1: 차액정산 요청일 조회 */
SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

SELECT
    @v_strReqYMD AS ReqYmd,
    @v_strCurrYMD AS CurrYmd,
    CASE
        WHEN EXISTS
        (
            SELECT 1
              FROM SETTLE_POQ_DB.dbo.TSettleMst
             WHERE ProcYMD = @p_ymd
               AND YMD >= @v_strReqYMD
               AND OutState IN (1, 5)
               AND ISNULL(OutYMD, '') <> ''
               AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
               AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
               AND ExtraSettleFlag = 1
        )
        THEN 1
        ELSE 0
    END AS IsBlocked;
```

#### 원장 삭제 및 재등록 SQL

```sql
-- SQL_DELETE_EXISTING
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* DELETE 1: 기존 일반 추가 정산 데이터 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @v_strReqYMD
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;
```

```sql
-- SQL_INSERT_EXTRA_SETTLEMENT
/* INSERT 1: 일반 영중소 차액정산 원장 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
(
    YMD,
    CYMD,
    AYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    PLTID,
    TID,
    CID,
    PAYERID,
    PAYERNAME,
    SERVICENAME,
    PRODUCTNAME,
    PGVTTYPE,
    CLVTTYPE,
    ABROADCHK,
    CompanySalesType,
    USESTATE,
    NonSettleAmt,
    ExtraTxAmt,
    TXAMT,
    CLCOMMTYPE,
    PGCOMMTYPE,
    CLETC,
    PGETC,
    CLTOTAL,
    PGTOTAL,
    POQINCOME,
    CLINTCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    CLCOMM,
    CLVT,
    PGCOMM,
    PGVT,
    INSTATE,
    INYMD,
    ProcYMD,
    ProcState,
    ExtraSettleFlag
)
SELECT
    X.YMD,
    X.CYMD,
    X.AYMD,
    X.CLIENTID,
    X.PGNAME,
    X.MALLID,
    X.PLTID,
    X.TID,
    X.CID,
    X.PAYERID,
    X.PAYERNAME,
    X.SERVICENAME,
    X.PRODUCTNAME,
    X.PGINCVTAX,
    X.CLINCVTAX,
    X.ABROADCHK,
    X.CompanySalesType,
    X.USESTATE,
    X.NonSettleAmt,
    X.ExtraTxAmt,
    X.TXAMT,
    0 AS CLCOMMTYPE,
    0 AS PGCOMMTYPE,
    0 AS CLETC,
    0 AS PGETC,
    0 AS CLTOTAL,
    0 AS PGTOTAL,
    0 AS POQINCOME,
    0 AS CLINTCOMM,
    0 AS PGINTEXPCOMM,
    0 AS PGINTREALCOMM,
    CASE
        WHEN X.AYMD < '20190501' THEN 0
        ELSE CAST(ISNULL(X.CLCOMM, 0) AS INT)
    END AS CLCOMM,
    CASE
        WHEN X.AYMD < '20190501' THEN 0
        ELSE dbo.UF_GET_ROUND4VAT
        (
            ISNULL(X.CLCOMM, 0) *
            dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)
        )
    END AS CLVT,
    CASE
        WHEN Y.CommMethod = 0
            THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
        ELSE CAST
        (
            ROUND
            (
                ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1,
                0,
                Y.CommRoundFlag
            ) AS INT
        )
    END AS PGCOMM,
    CASE
        WHEN Y.CommMethod = 0
            THEN CAST
            (
                ROUND
                (
                    X.PGCOMM * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax),
                    0,
                    Y.VatRoundFlag
                ) AS INT
            )
        ELSE CAST
        (
            ROUND
            (
                ROUND
                (
                    ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag),
                    0,
                    Y.CommRoundFlag
                )
                -
                ROUND
                (
                    ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1,
                    0,
                    Y.CommRoundFlag
                ),
                0,
                Y.VatRoundFlag
            ) AS INT
        )
    END AS PGVT,
    X.INSTATE,
    X.INYMD,
    X.ProcYMD,
    CASE
        WHEN X.CLCOMM IS NULL OR X.PGCOMM IS NULL THEN 1
        ELSE NULL
    END AS ProcState,
    1 AS ExtraSettleFlag
FROM
(
    SELECT
        A.ReqYMD AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD) AS CYMD,
        A.OrgYMD AS AYMD,
        A.ResYMD AS ProcYMD,
        A.CLIENTID,
        A.PGNAME,
        A.MID AS MALLID,
        A.PLTID,
        A.TID,
        A.SeqNo AS CID,
        '' AS PAYERID,
        '' AS PAYERNAME,
        '' AS SERVICENAME,
        '영중소차액정산' AS PRODUCTNAME,
        ISNULL(B.incVTax, 0) AS PGINCVTAX,
        ISNULL(C.incVTax, 0) AS CLINCVTAX,
        0 AS ABROADCHK,
        A.CompanySalesType,
        A.USESTATE,
        0 AS NonSettleAmt,
        0 AS TXAMT,
        A.TxAmt AS ExtraTxAmt,
        ISNULL
        (
            CASE
                WHEN ISNULL(A.CompanySalesType, 4) = 0
                    THEN CAST
                    (
                        A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0
                        AS INT
                    )
                WHEN ISNULL(A.CompanySalesType, 4) = 1
                    THEN CAST
                    (
                        A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0
                        AS INT
                    )
                WHEN ISNULL(A.CompanySalesType, 4) = 2
                    THEN CAST
                    (
                        A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0
                        AS INT
                    )
                WHEN ISNULL(A.CompanySalesType, 4) = 3
                    THEN CAST
                    (
                        A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0
                        AS INT
                    )
                ELSE 0
            END,
            0
        ) * IIF(C.ExtraSettleFlag = 'Y', 1, 0) AS CLComm,
        ISNULL
        (
            CASE
                WHEN A.PGName = 'dacomcard' OR A.PGName = 'tosscard'
                THEN
                    CASE
                        WHEN ISNULL(A.CompanySalesType, 4) = 0
                            THEN
                                CAST
                                (
                                    A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                    AS INT
                                )
                                -
                                CAST
                                (
                                    A.TxAmt * ((B.CommRate0 * 1.1) / 100.0)
                                    AS INT
                                )
                        WHEN ISNULL(A.CompanySalesType, 4) = 1
                            THEN
                                CAST
                                (
                                    A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                    AS INT
                                )
                                -
                                CAST
                                (
                                    A.TxAmt * ((B.CommRate1 * 1.1) / 100.0)
                                    AS INT
                                )
                        WHEN ISNULL(A.CompanySalesType, 4) = 2
                            THEN
                                CAST
                                (
                                    A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                    AS INT
                                )
                                -
                                CAST
                                (
                                    A.TxAmt * ((B.CommRate2 * 1.1) / 100.0)
                                    AS INT
                                )
                        WHEN ISNULL(A.CompanySalesType, 4) = 3
                            THEN
                                CAST
                                (
                                    A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                    AS INT
                                )
                                -
                                CAST
                                (
                                    A.TxAmt * ((B.CommRate3 * 1.1) / 100.0)
                                    AS INT
                                )
                        ELSE 0
                    END
                ELSE
                    CASE
                        WHEN ISNULL(A.CompanySalesType, 4) = 0
                            THEN CAST
                            (
                                A.TxAmt *
                                (B.CommissionRate - B.CommRate0) / 100.0
                                AS INT
                            )
                        WHEN ISNULL(A.CompanySalesType, 4) = 1
                            THEN CAST
                            (
                                A.TxAmt *
                                (B.CommissionRate - B.CommRate1) / 100.0
                                AS INT
                            )
                        WHEN ISNULL(A.CompanySalesType, 4) = 2
                            THEN CAST
                            (
                                A.TxAmt *
                                (B.CommissionRate - B.CommRate2) / 100.0
                                AS INT
                            )
                        WHEN ISNULL(A.CompanySalesType, 4) = 3
                            THEN CAST
                            (
                                A.TxAmt *
                                (B.CommissionRate - B.CommRate3) / 100.0
                                AS INT
                            )
                        ELSE 0
                    END
            END,
            0
        ) AS PGComm,
        1 AS INSTATE,
        A.ExtraSettleYMD AS INYMD
    FROM PaymentDB.dbo.TExtraSettleIn AS A
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGSettleRate4Extra AS B
      ON B.YMD = A.OrgYMD
     AND B.PGNAME = A.PGNAME
     AND B.MALLID = A.MID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4Extra AS C
      ON C.YMD = A.OrgYMD
     AND C.PGNAME = A.PGNAME
     AND C.MALLID = A.MID
     AND C.CLIENTID = A.CLIENTID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientContract AS E
      ON E.CLIENTID = A.CLIENTID
    WHERE A.ResYMD = @p_ymd
      AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
      AND A.ResultCode = '00'
      AND A.CompanySalesType IN (0, 1, 2, 3)
      AND A.RefundTxType = 0
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty AS Y
  ON Y.PGName = X.PGNAME;
```

#### 순차 후처리 SQL

UPDATE 1의 지급일 계산은 원본의 스칼라 하위질의를 그대로 유지한다. 이를 `CROSS APPLY`로 변경하면 함수 결과가 없는 행 자체가 갱신 대상에서 빠지므로 금지한다.

```sql
-- SQL_UPDATE_EXPECTED_OUT_DATE
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* UPDATE 1: 지급예정 상태와 지급일자 계산 */
UPDATE A
   SET A.OutState = 2,
       A.OutYMD =
       (
           SELECT OutYMD
             FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID)
       )
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS C
    ON C.PGNAME = A.PGNAME
   AND C.MALLID = A.MALLID
   AND C.CLIENTID = A.CLIENTID
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @v_strReqYMD
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;
```

```sql
-- SQL_ADJUST_PAST_OUT_DATE
DECLARE @v_strReqYMD VARCHAR(8) = '';
DECLARE @v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112);

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* UPDATE 2: 당일 이전 지급예정일 보정 */
UPDATE A
   SET A.OutYMD = dbo.UF_Get_WorkDay2(@v_strCurrYMD, 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @v_strReqYMD
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.OutState = 2
   AND A.OutYMD <= @v_strCurrYMD;

SELECT @v_strCurrYMD AS UsedCurrYmd;
```

UPDATE 3의 아홉 개 우변은 모두 갱신 전 행 값을 기준으로 동시에 평가되어야 한다. 컬럼별 UPDATE로 분리하지 않는다.

```sql
-- SQL_REVERSE_EXTRA_COMMISSION_SIGN
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* UPDATE 3: 정상 거래의 고객사 및 PG 차액 수수료 부호 반전 */
UPDATE A
   SET A.CLCOMM = A.CLCOMM * (-1),
       A.CLINTCOMM = A.CLINTCOMM * (-1),
       A.CLVT = A.CLVT * (-1),
       A.CLETC = A.CLETC * (-1),
       A.PGCOMM = A.PGCOMM * (-1),
       A.PGINTEXPCOMM = A.PGINTEXPCOMM * (-1),
       A.PGINTREALCOMM = A.PGINTREALCOMM * (-1),
       A.PGVT = A.PGVT * (-1),
       A.PGETC = A.PGETC * (-1)
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @v_strReqYMD
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.USESTATE IN (0)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;
```

```sql
-- SQL_RECALCULATE_TOTALS
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* UPDATE 4: 고객사 총액 PG 총액 및 POQ 수익 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL =
       CLCOMM + CLVT + CLETC + CLINTCOMM,
       PGTOTAL =
       PGCOMM + PGVT + PGETC
       + CASE PGINTREALCOMM
             WHEN 0 THEN PGINTEXPCOMM
             ELSE PGINTREALCOMM
         END,
       POQINCOME =
       (CLCOMM + CLVT + CLETC + CLINTCOMM)
       -
       (
           PGCOMM + PGVT + PGETC
           + CASE PGINTREALCOMM
                 WHEN 0 THEN PGINTEXPCOMM
                 ELSE PGINTREALCOMM
             END
       )
 WHERE ProcYMD = @p_ymd
   AND YMD >= @v_strReqYMD
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;
```

UPDATE 5 역시 모든 우변을 갱신 전 값으로 동시에 평가한다. `@v_valIncVat`는 바인딩하지 않고 원본의 `DECIMAL(2,1)` 선언을 SQL에 유지한다.

```sql
-- SQL_RECALCULATE_INCLUDED_VAT
DECLARE @v_strReqYMD VARCHAR(8) = '';
DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

/* UPDATE 5: 부가세 포함 고객사 수수료 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal =
       CLComm + CLEtc + CLIntComm,
       CLComm =
       CAST(CLComm / @v_valIncVat AS INT)
       + CAST(CLEtc / @v_valIncVat AS INT)
       + CAST(CLIntComm / @v_valIncVat AS INT),
       CLVT =
       (CLComm + CLEtc + CLIntComm)
       -
       (
           CAST(CLComm / @v_valIncVat AS INT)
           + CAST(CLEtc / @v_valIncVat AS INT)
           + CAST(CLIntComm / @v_valIncVat AS INT)
       ),
       POQIncome =
       (CLComm + CLEtc + CLIntComm) - PGTotal
 WHERE ProcYMD = @p_ymd
   AND YMD >= @v_strReqYMD
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND CLVTType = 1
   AND ExtraSettleFlag = 1;
```

#### 오류 코드 및 재시작 의미

| 실패 지점 | 기록할 `LegacyReturnCode` | 처리 |
|---|---:|---|
| 지급확정·지급완료 원장 사전 검증 | `-9` | DML 없이 종료 |
| `DELETE 1` | `-1` | 전체 트랜잭션 롤백 |
| `INSERT 1` | `NULL` | 원본 코드가 없으므로 새 코드 없이 전체 롤백 |
| `UPDATE 1` | `-2` | 전체 트랜잭션 롤백 |
| `UPDATE 2` | `-3` | 전체 트랜잭션 롤백 |
| `UPDATE 3` | `NULL` | 원본 코드가 없으므로 새 코드 없이 전체 롤백 |
| `UPDATE 4` | `-4` | 전체 트랜잭션 롤백 |
| `UPDATE 5` | `-21` | 전체 트랜잭션 롤백 |

실패 시 `SETTLE_POQ_DB.dbo.TSettleMst` 변경은 전부 롤백된다. 체크포인트가 `Succeeded`가 아니므로 재시작 오케스트레이터는 S11을 다시 호출하고, S11은 `-9` 가드를 포함한 전체 로직을 처음부터 수행한다.

#### 단계 완료 전 정합성 확인

다음 검증은 같은 트랜잭션에서 UPDATE 5 이후 커밋 전에 수행한다. 결과 행이 하나라도 있으면 해당 검증 문장명을 오류 문맥에 기록하고 전체 트랜잭션을 롤백한다.

```sql
-- SQL_VALIDATE_S11_TOTALS
DECLARE @v_strReqYMD VARCHAR(8) = '';

SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

SELECT
    PLTID,
    YMD,
    CLIENTID,
    PGNAME,
    CLTOTAL,
    PGTOTAL,
    POQINCOME
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @v_strReqYMD
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1
   AND
   (
       PGTOTAL <>
       (
           PGCOMM + PGVT + PGETC
           + CASE PGINTREALCOMM
                 WHEN 0 THEN PGINTEXPCOMM
                 ELSE PGINTREALCOMM
             END
       )
       OR
       (
           CLVTType <> 1
           AND CLTOTAL <> CLCOMM + CLVT + CLETC + CLINTCOMM
       )
       OR
       (
           CLVTType <> 1
           AND POQINCOME <> CLTOTAL - PGTOTAL
       )
       OR
       (
           CLVTType = 1
           AND CLTOTAL <> CLCOMM + CLVT
       )
       OR
       (
           CLVTType = 1
           AND POQINCOME <> CLTOTAL - PGTOTAL
       )
   );
```