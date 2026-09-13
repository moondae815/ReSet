> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**
> 
> S08 (하한 미달: 파생 테이블 `X`의 출력 컬럼 이름이 중복됩니다: `PGINCVTAX`. T-SQL은 파생 테이블·CTE의 컬럼 이름이 유일할 것을 요구하므로 이 SQL은 실행되지 않습니다(식별자는 대소문자를 구분하지 않습니다). 한 쪽을 지우거나 다른 이름을 주십시오. / 파생 테이블 `X`의 출력 컬럼 이름이 중복됩니다: `CLINCVTAX`. T-SQL은 파생 테이블·CTE의 컬럼 이름이 유일할 것을 요구하므로 이 SQL은 실행되지 않습니다(식별자는 대소문자를 구분하지 않습니다). 한 쪽을 지우거나 다른 이름을 주십시오. / S08 섹션이 이름 있는 SQL 블록을 호출하는데 그 블록이 이 절에 정의돼 있지 않습니다: `SQL_CURRENT_RUN_ID`. 호출한 이름마다 두 붙임표로 시작하는 주석 줄로 블록을 열어 같은 절에 실으십시오. 한 줄에 여러 이름을 묶어 적은 표기와 별표를 붙인 접두사 표기도 정의로 인정합니다.)
> 
> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오.

### S08 | 영중소 추가 정산 삽입(일반)

**개요**: 본 단계는 `dbo.UP_UTIL_SETTLE_INS_EXTRA`(영중소 차액정산 데이터 등록)를 대체한다. 대상 테이블은 `SETTLE_POQ_DB.dbo.TSettleMst` 단일 테이블이며, `Chunkable: False`이므로 청킹을 적용하지 않고 **단일 트랜잭션 롤백** 정책(공통 컨벤션 참조)을 따른다 — 트랜잭션이 하나로 끝나므로 Shadow 테이블도 보정 DELETE도 두지 않는다. 모든 SQL은 SNAPSHOT 격리수준 하에서 실행되어야 한다는 의무를 가진다(설정 위치는 구현 라운드 결정).

**파라미터 매핑(rule 5-2)**: `@pi_strYMD CHAR(8) -> p_ymd`. `@po_intRetVal INT`는 원본의 출력 파라미터로, 바인딩 대상이 아니며(rule 13) 본 단계에서는 `currentStepErrorCode` 및 `batch.BatchStepJournal.LegacyReturnCode`로 그 의미가 대체 전달된다 — 성공 시 원본처럼 별도의 성공 코드 대입이 없으므로 `StepStatus = 'Succeeded'`가 유일한 성공 판정 기준이다(공통 컨벤션과 동일).

**지역 변수(rule 5-1, 원본 DECLARE 그대로 유지)**: `@v_strReqYMD VARCHAR(8) = ''`, `@v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112)`, `@v_valIncVat DECIMAL(2,1) = 1.1`. 이 값들은 `execute(...)`의 바인딩 목록에 넣지 않고 각 SQL 블록 안에 `DECLARE`로 남긴다.

**원본 오류 검사 공백의 보존(중요)**: 명세서에 따르면 INSERT 1과 UPDATE 3 직후에는 `@@ERROR` 검사가 없다 — 이 두 문장이 실패해도 원본은 롤백 없이 다음 문장으로 진행한다. 본 단계는 이 의미를 그대로 이행하기 위해 이 두 문장만 예외를 관찰하되 흡수(swallow)하고 계속 진행하며, `currentStepErrorCode`도 갱신하지 않는다. DELETE 1·UPDATE 1·UPDATE 2·UPDATE 4·UPDATE 5는 원본 오류 코드(rule 9, 재매핑 금지)를 그대로 사용해 실패 시 즉시 롤백한다.

```pseudocode
// 공통 진입부
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch10", p_ymd: batchYmd })
currentStepErrorCode = NULL
writeStepJournal(runId, "S08", status: "Running", legacyReturnCode: NULL)

// 사전 조건은 트랜잭션 시작 전, 매 호출마다 무조건 수행(rule 5) — 캐시/스킵 불가
reqYmd = queryScalar(SQL_AGG_MIN_REQYMD, { p_ymd: batchYmd })   // @v_strReqYMD 재대입, 무결과면 NULL
existCnt = queryScalar(SQL_CHECK_ALREADY_SETTLED, { p_ymd: batchYmd, p_reqYmd: reqYmd })

IF existCnt > 0:
    // 원본 라인 39: @po_intRetVal = -9, 트랜잭션 시작 전 즉시 RETURN
    currentStepErrorCode = -9
    writeStepJournal(runId, "S08", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline

conn.beginTransaction()

currentStepErrorCode = -1
repository.execute(SQL_DELETE_EXTRA_SETTLE, { p_ymd: batchYmd, p_reqYmd: reqYmd })

// INSERT 1: 원본에 @@ERROR 검사가 없으므로 실패를 흡수하고 계속 진행한다.
// currentStepErrorCode는 갱신하지 않는다 — 이 문장에는 원본이 부여한 오류코드가 없다.
TRY:
    repository.execute(SQL_INSERT_EXTRA_SETTLE, { p_ymd: batchYmd })
CATCH (ex):
    // 원본 동작 그대로: 실패해도 롤백하지 않고 다음 문장(UPDATE 1)으로 진행
    logOnly(ex)

currentStepErrorCode = -2
repository.execute(SQL_UPDATE_OUT_STATE, { p_ymd: batchYmd, p_reqYmd: reqYmd })

currentStepErrorCode = -3
repository.execute(SQL_UPDATE_OUT_YMD_WORKDAY, { p_ymd: batchYmd, p_reqYmd: reqYmd })

// UPDATE 3: 원본에 @@ERROR 검사가 없으므로 실패를 흡수하고 계속 진행한다.
TRY:
    repository.execute(SQL_UPDATE_SIGN_REVERSE, { p_ymd: batchYmd, p_reqYmd: reqYmd })
CATCH (ex):
    logOnly(ex)

currentStepErrorCode = -4
repository.execute(SQL_UPDATE_RECALC_TOTAL, { p_ymd: batchYmd, p_reqYmd: reqYmd })

currentStepErrorCode = -21
repository.execute(SQL_UPDATE_INCVAT_RECALC, { p_ymd: batchYmd, p_reqYmd: reqYmd })

conn.commit()
writeStepJournal(runId, "S08", status: "Succeeded", legacyReturnCode: currentStepErrorCode)

ON FAILURE observed by the application (DELETE 1 / UPDATE 1 / UPDATE 2 / UPDATE 4 / UPDATE 5):
    conn.rollbackIfOpen()   // 단일 트랜잭션이므로 롤백만으로 원상 복구 완료 — Shadow/보정 DELETE 없음(rule 4)
    writeStepJournal(runId, "S08", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_AGG_MIN_REQYMD
/* SELECT 1: 차액정산 요청일 최소값 조회 */
DECLARE @v_strReqYMD VARCHAR(8) = '';
DECLARE @v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112);
SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

-- SQL_CHECK_ALREADY_SETTLED
-- 원본 IF 1(라인 30~38) 사전조건 가드의 이행이다. 명세서 DML 범위 표에 이 문장의 행이 없는
-- 제어용 EXISTS 조회이므로 앵커를 달지 않는다(문장 앵커 규칙 참조).
SELECT COUNT(1) AS ExistCnt
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE PLTID IS NOT NULL
   AND ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND OutState IN (3, 4)
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND ExtraSettleFlag = 1;

-- SQL_DELETE_EXTRA_SETTLE
/* DELETE 1: 당일분 영중소 차액정산 데이터 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_INSERT_EXTRA_SETTLE
/* INSERT 1: 영중소 차액정산 신규 데이터 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
(
    YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
    PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME,
    PGVTTYPE, CLVTTYPE, ABROADCHK, CompanySalesType, USESTATE,
    NonSettleAmt, ExtraTxAmt, TXAMT,
    CLCOMMTYPE, PGCOMMTYPE, CLETC, PGETC, CLTOTAL, PGTOTAL, POQINCOME,
    CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM,
    CLCOMM, CLVT, PGCOMM, PGVT,
    INSTATE, INYMD, ProcYMD, ProcState, ExtraSettleFlag
)
SELECT
    X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME,
    X.PGINCVTAX, X.CLINCVTAX, X.ABROADCHK, X.CompanySalesType, X.USESTATE,
    X.NonSettleAmt, X.ExtraTxAmt, X.TXAMT,
    0 AS CLCOMMTYPE, 0 AS PGCOMMTYPE, 0 AS CLETC, 0 AS PGETC,
    0 AS CLTOTAL, 0 AS PGTOTAL, 0 AS POQINCOME,
    0 AS CLINTCOMM, 0 AS PGINTEXPCOMM, 0 AS PGINTREALCOMM,
    CASE WHEN X.AYMD < '20190501' THEN 0 ELSE CAST(ISNULL(X.CLCOMM, 0) AS INT) END AS CLCOMM,
    CASE WHEN X.AYMD < '20190501' THEN 0
         ELSE dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM, 0) * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)) END AS CLVT,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT) END AS PGCOMM,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                       ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                       - (ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)),
                       0, Y.VatRoundFlag) AS INT) END AS PGVT,
    X.INSTATE, X.INYMD, X.ProcYMD,
    CASE WHEN (X.CLCOMM IS NULL OR X.PGCOMM IS NULL) THEN 1 ELSE NULL END AS ProcState,
    1 AS ExtraSettleFlag
FROM (
    SELECT
        A.ReqYMD                       AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD) AS CYMD,
        A.OrgYMD                       AS AYMD,
        A.ResYMD                       AS ProcYMD,
        A.CLIENTID                     AS CLIENTID,
        A.PGNAME                       AS PGNAME,
        A.MID                          AS MALLID,
        A.PLTID                        AS PLTID,
        A.TID                          AS TID,
        A.SeqNo                        AS CID,
        ''                              AS PAYERID,
        ''                              AS PAYERNAME,
        ''                              AS SERVICENAME,
        N'영중소차액정산'                AS PRODUCTNAME,
        ISNULL(B.incVTax, 0)           AS PGINCVTAX,
        ISNULL(C.incVTax, 0)           AS CLINCVTAX,
        0                               AS ABROADCHK,
        A.CompanySalesType             AS CompanySalesType,
        A.USESTATE                     AS USESTATE,
        0                               AS NonSettleAmt,
        A.TxAmt                        AS ExtraTxAmt,
        0                               AS TXAMT,
        B.incVTax                      AS PGIncVTax,
        C.incVTax                      AS CLIncVTax,
        ISNULL(CASE
                 WHEN ISNULL(A.CompanySalesType, 4) = 0 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType, 4) = 1 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType, 4) = 2 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType, 4) = 3 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0) AS INT)
                 ELSE 0
               END, 0) * IIF(C.ExtraSettleFlag = 'Y', 1, 0) AS CLCOMM,
        ISNULL(CASE
                 WHEN A.PGName = 'dacomcard' OR A.PGName = 'tosscard' THEN
                     CASE
                       WHEN ISNULL(A.CompanySalesType, 4) = 0 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate0 * 1.1) / 100.0)) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 1 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate1 * 1.1) / 100.0)) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 2 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate2 * 1.1) / 100.0)) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 3 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate3 * 1.1) / 100.0)) AS INT)
                       ELSE 0
                     END
                 ELSE
                     CASE
                       WHEN ISNULL(A.CompanySalesType, 4) = 0 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate0) / 100.0) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 1 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate1) / 100.0) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 2 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate2) / 100.0) AS INT)
                       WHEN ISNULL(A.CompanySalesType, 4) = 3 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate3) / 100.0) AS INT)
                       ELSE 0
                     END
               END, 0)                  AS PGCOMM,
        1                               AS INSTATE,
        A.ExtraSettleYMD                AS INYMD
    FROM PaymentDB.dbo.TExtraSettleIn A
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGSettleRate4Extra B
        ON B.YMD = A.OrgYMD AND B.PGNAME = A.PGNAME AND B.MALLID = A.MID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4Extra C
        ON C.YMD = A.OrgYMD AND C.PGNAME = A.PGNAME AND C.MALLID = A.MID AND C.CLIENTID = A.CLIENTID
       AND C.ExtraSettleFlag = 'Y'
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientContract E
        ON E.CLIENTID = A.CLIENTID
    WHERE A.ResYMD = @p_ymd
      AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
      AND A.ResultCode = '00'
      AND A.CompanySalesType IN (0, 1, 2, 3)
      AND A.RefundTxType = 0
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y
    ON Y.PGName = X.PGNAME;

-- SQL_UPDATE_OUT_STATE
/* U1: 지급상태(OutState) 및 지급일(OutYMD) 갱신 */
UPDATE A
   SET A.OutState = 2,
       A.OutYMD = (SELECT S.OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID) S)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate C
     ON C.PGNAME = A.PGNAME AND C.MALLID = A.MALLID AND C.CLIENTID = A.CLIENTID
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;

-- SQL_UPDATE_OUT_YMD_WORKDAY
/* U2: 당일 이전 정산일 보정 */
UPDATE A
   SET A.OutYMD = dbo.UF_Get_WorkDay2(@v_strCurrYMD, 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.OutState = 2
   AND A.OutYMD <= @v_strCurrYMD;

-- SQL_UPDATE_SIGN_REVERSE
/* U3: 영중소 차액정산 고객사/PG 항목 부호 반전 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLCOMM        = CLCOMM        * (-1),
       CLINTCOMM     = CLINTCOMM     * (-1),
       CLVT          = CLVT          * (-1),
       CLETC         = CLETC         * (-1),
       PGCOMM        = PGCOMM        * (-1),
       PGINTEXPCOMM  = PGINTEXPCOMM  * (-1),
       PGINTREALCOMM = PGINTREALCOMM * (-1),
       PGVT          = PGVT          * (-1),
       PGETC         = PGETC         * (-1)
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND USESTATE IN (0)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_UPDATE_RECALC_TOTAL
/* U4: 고객사/PG 총액 및 POQ순이익 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL   = (CLCOMM + CLVT + CLETC + CLINTCOMM),
       PGTOTAL   = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)),
       POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM)
                   - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_UPDATE_INCVAT_RECALC
/* U5: 부가세포함 계산방식 재계산 */
DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal   = (CLComm + CLEtc + CLIntComm),
       CLComm    = (CAST(CLComm / @v_valIncVat AS INT) + CAST(CLEtc / @v_valIncVat AS INT) + CAST(CLIntComm / @v_valIncVat AS INT)),
       CLVT      = (CLComm + CLEtc + CLIntComm)
                   - (CAST(CLComm / @v_valIncVat AS INT) + CAST(CLEtc / @v_valIncVat AS INT) + CAST(CLIntComm / @v_valIncVat AS INT)),
       POQIncome = (CLComm + CLEtc + CLIntComm) - PGTotal
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND CLVTType = 1
   AND ExtraSettleFlag = 1;
```

**오류 코드 요약**: 사전조건 가드 위반 `-9`, DELETE 1 실패 `-1`, UPDATE 1 실패 `-2`, UPDATE 2 실패 `-3`, UPDATE 4 실패 `-4`, UPDATE 5 실패 `-21`. INSERT 1과 UPDATE 3은 원본과 동일하게 실패를 관찰만 하고 `currentStepErrorCode`를 갱신하지 않으며 파이프라인을 중단시키지 않는다. 이 값들은 원본 `dbo.UP_UTIL_SETTLE_INS_EXTRA`가 부여한 코드를 그대로 재사용한 것으로, rule 9에 따라 재매핑하지 않았다.