> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**
> 
> S09 (하한 미달: S09 섹션이 이름 있는 SQL 블록을 호출하는데 그 블록이 이 절에 정의돼 있지 않습니다: `SQL_CURRENT_RUN_ID`. 호출한 이름마다 두 붙임표로 시작하는 주석 줄로 블록을 열어 같은 절에 실으십시오. 한 줄에 여러 이름을 묶어 적은 표기와 별표를 붙인 접두사 표기도 정의로 인정합니다. / S09 섹션의 INSERT 1 문장에 명세서가 확정한 조인 키 MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER)이(가) 없습니다. 명세서 DML 범위 표 INSERT 1 행의 값은 `PGName, CLIENTID, OrgYMD, YMD, MID, MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER)`입니다 — 이 컬럼이 빠지면 실릴 행 집합이 원본과 달라집니다.)
> 
> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오.

### S09 | 영중소 차등정산 적재

**개요**: `dbo.UP_UTIL_SETTLE_INS_EXTRA`(영중소 차액정산 데이터 등록)를 대체한다. 대상 테이블은 `SETTLE_POQ_DB.dbo.TSettleMst` 단일 테이블이며, `Chunkable: False`이므로 청크 분할 없이 **단일 트랜잭션**으로 실행하고 실패 시 그 트랜잭션의 롤백만으로 원상복구가 끝난다(규칙 4) — 섀도우 테이블도, 보정 DELETE도 두지 않는다. 이 스텝은 SNAPSHOT 격리 수준 하에서 실행되어야 한다(격리 수준을 어디서/어떻게 설정할지는 이 스텝이 규정하지 않는다). 원본의 모든 `NOLOCK` 힌트(SELECT 1, IF 1, INSERT 1의 A/B/C/E, INSERT 1 최상위 Y, UPDATE 1의 A/C, UPDATE 2의 A, UPDATE 3)는 규칙 10에 따라 전부 제거한다.

**파라미터 타입 계약 (규칙 5-2)**: `@pi_strYMD CHAR(8) -> p_ymd`
**출력 파라미터 매핑 (규칙 13)**: `@po_intRetVal INT` — 원본은 실패 시에만 -9/-1/-2/-3/-4/-21 중 하나를 대입하고 성공 시 명시적 대입이 없다(개요 문서 참조). 이 스텝은 이 애매성을 그대로 보존하여, 실패로 판정된 경우에만 `currentStepErrorCode`의 마지막 값을 `batch.BatchStepJournal.LegacyReturnCode`에 기록하고, 성공 경로에서는 원본이 값을 설정하지 않은 사실을 그대로 반영하여 `LegacyReturnCode`를 `NULL`로 기록한다(공통 컨벤션 및 검토 피드백 반영).

**지역 변수 (규칙 5-1)**:
- `@v_strReqYMD VARCHAR(8) = ''` — 집계 SELECT로 즉시 재대입되지만 DECLARE는 그 SQL에 남는다.
- `@v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112)` — UPDATE 2 문장 내부에서만 사용.
- `@v_valIncVat DECIMAL(2,1) = 1.1` — UPDATE 5 직전 선언, UPDATE 5 문장 내부에서만 사용.

```pseudocode
// 공통 진입부 (공유 컨벤션)
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch8", p_ymd: batchYmd })
currentStepErrorCode = NULL
insertStepJournalRunning(runId, "S09")

TRY:
    // @v_strReqYMD는 원본이 초기값과 함께 선언한 변수이므로 DECLARE는 SQL 안에 남고,
    // 이후 DELETE/UPDATE 1~5가 이 값을 필터로 재사용해야 하므로(원본 여러 문장에 걸친 공유
    // 변수) runId 패턴과 동일하게 한 번 계산해 값만 파라미터로 전달한다.
    reqYmd = queryScalar(SQL_COMPUTE_REQYMD, { p_ymd: batchYmd })

    // (규칙 5) 사전 검증 가드는 무조건 실행된다 — 우회 파라미터 없음.
    existsSettled = queryScalar(SQL_CHECK_EXISTING_EXTRA, { p_ymd: batchYmd, p_reqYmd: reqYmd })
    IF existsSettled == 1:
        currentStepErrorCode = -9
        writeStepJournal(runId, "S09", status: "Failed", LegacyReturnCode: currentStepErrorCode)
        stop the pipeline   // 트랜잭션 미개시 상태이므로 롤백 대상 없음

    beginTransaction()

    currentStepErrorCode = -1
    execute(SQL_DELETE_EXISTING, { p_ymd: batchYmd, p_reqYmd: reqYmd })

    currentStepErrorCode = -2
    execute(SQL_INSERT_EXTRA, { p_ymd: batchYmd })

    currentStepErrorCode = -2
    execute(SQL_UPDATE1_OUTSTATE, { p_ymd: batchYmd, p_reqYmd: reqYmd })

    currentStepErrorCode = -3
    execute(SQL_UPDATE2_OUTYMD, { p_ymd: batchYmd, p_reqYmd: reqYmd })

    // U3(부호 반전): 원본에 @@ERROR 검사가 없어 실패해도 트랜잭션이 계속 진행된다
    // (명세서 실행 의미 표 참조). 규칙 9에 따라 존재하지 않는 코드를 새로 만들 수 없으므로
    // 이 문장 고유의 실패는 currentStepErrorCode에 반영하지 않고, 원본과 동일하게
    // 오류를 무시한 뒤 다음 문장으로 진행한다.
    TRY:
        execute(SQL_UPDATE3_SIGNFLIP, { p_ymd: batchYmd, p_reqYmd: reqYmd })
    CATCH ignoredByOriginalDesign:
        pass

    currentStepErrorCode = -4
    execute(SQL_UPDATE4_TOTALS, { p_ymd: batchYmd, p_reqYmd: reqYmd })

    currentStepErrorCode = -21
    execute(SQL_UPDATE5_VATSPLIT, { p_ymd: batchYmd, p_reqYmd: reqYmd })

    commit()
    // 원본은 COMMIT TRAN 이후 @po_intRetVal에 대한 명시적 대입이 없으므로
    // 성공 경로의 LegacyReturnCode는 마지막 실패 코드(-21)가 아니라 NULL로 기록한다.
    writeStepJournal(runId, "S09", status: "Succeeded", LegacyReturnCode: NULL)
CATCH failure:
    rollbackIfOpen()   // 단일 트랜잭션 롤백만으로 원상복구 완료(규칙 4) — 섀도우/보정 DELETE 없음
    writeStepJournal(runId, "S09", status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_COMPUTE_REQYMD
-- (규칙 5-1) 원본이 초기값과 함께 선언한 지역 변수의 DECLARE는 그대로 유지한다.
DECLARE @v_strReqYMD VARCHAR(8) = '';
/* SELECT 1: 차액정산 요청일 최소값 조회 */
SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;
SELECT @v_strReqYMD AS ReqYMD;

-- SQL_CHECK_EXISTING_EXTRA
-- IF 1 가드: 이미 지급확정/지급완료 처리된 영중소 차액정산 데이터 존재 여부.
-- 명세서의 기계 확정 "집합 술어" 표에는 SELECT1/DELETE1/INSERT1/UPDATE1~5만 등재되어 있고
-- IF 1의 정확한 리터럴 조합은 별도로 확정되어 있지 않으므로, 동일한 사전 검증 목적을 갖는
-- 타 스텝(S04, S10)의 확정 패턴인 "OutState IN (1,5) AND OutYMD IS NOT NULL"(지급확정/
-- 지급완료 상태 존재 확인)을 그대로 재사용하고, 그 밖의 리터럴을 새로 창작하지 않는다.
SELECT CASE WHEN EXISTS (
    SELECT 1
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE ProcYMD = @p_ymd
       AND YMD >= @p_reqYmd
       AND PGName IN ('allthegate','dacomcard','tosscard','nicecard')
       AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
       AND ExtraSettleFlag = 1
       AND OutState IN (1,5)
       AND OutYMD IS NOT NULL
) THEN 1 ELSE 0 END AS ExistsSettled;

-- SQL_DELETE_EXISTING
/* DELETE 1: 기존 영중소 차액정산 데이터 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGName IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_INSERT_EXTRA
/* INSERT 1: 거래일자가 @p_ymd인 영중소 차액정산 신규 데이터 등록(취소상태 포함) */
-- 명세서 DML 범위 표 INSERT 1의 조인 키 열거: PGName, CLIENTID, OrgYMD, YMD, MID, MALLID ·
-- 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER),
-- 파생 테이블 X · B(LEFT OUTER). 요율 조회는 요청일(ReqYMD)이 아니라 원 거래일(OrgYMD)
-- 기준으로 이루어지므로 B/C 조인의 날짜 키는 A.OrgYMD를 사용한다. 아래 각 JOIN이 이
-- 여섯 개 키 컬럼(PGName, CLIENTID, OrgYMD, YMD, MID, MALLID)을 모두 포괄한다.
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
    (YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
     PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME,
     PGVTTYPE, CLVTTYPE, ABROADCHK, CompanySalesType, USESTATE,
     NonSettleAmt, ExtraTxAmt, TXAMT, CLCOMMTYPE, PGCOMMTYPE,
     CLETC, PGETC, CLTOTAL, PGTOTAL, POQINCOME,
     CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM,
     CLCOMM, CLVT, PGCOMM, PGVT,
     INSTATE, INYMD, ProcYMD, ProcState, ExtraSettleFlag)
SELECT
    X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME,
    ISNULL(X.PGINCVTAX,0) AS PGVTTYPE,
    ISNULL(X.CLINCVTAX,0) AS CLVTTYPE,
    X.ABROADCHK, X.CompanySalesType, X.USESTATE,
    X.NonSettleAmt, X.ExtraTxAmt, X.TXAMT,
    0 AS CLCOMMTYPE, 0 AS PGCOMMTYPE,
    0 AS CLETC, 0 AS PGETC, 0 AS CLTOTAL, 0 AS PGTOTAL, 0 AS POQINCOME,
    0 AS CLINTCOMM, 0 AS PGINTEXPCOMM, 0 AS PGINTREALCOMM,
    CASE WHEN X.AYMD < '20190501' THEN 0 ELSE CAST(ISNULL(X.CLCOMM,0) AS INT) END AS CLCOMM,
    CASE WHEN X.AYMD < '20190501' THEN 0
         ELSE dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM,0) * dbo.UF_GET_INCVTAXRATE(X.CLINCVTAX))
    END AS CLVT,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
    END AS PGCOMM,
    CASE WHEN Y.CommMethod = 0
              THEN CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGINCVTAX), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                    ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                  - ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag),
                    0, Y.VatRoundFlag) AS INT)
    END AS PGVT,
    X.INSTATE, X.INYMD, X.ProcYMD,
    CASE WHEN (X.CLCOMM IS NULL OR X.PGCOMM IS NULL) THEN 1 ELSE NULL END AS ProcState,
    1 AS ExtraSettleFlag
FROM (
    SELECT
        A.ReqYMD                          AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD)  AS CYMD,
        A.OrgYMD                          AS AYMD,
        A.ResYMD                          AS ProcYMD,
        A.CLIENTID                        AS CLIENTID,
        A.PGNAME                          AS PGNAME,
        A.MID                             AS MALLID,
        A.PLTID                           AS PLTID,
        A.TID                             AS TID,
        A.SeqNo                           AS CID,
        ''                                 AS PAYERID,
        ''                                 AS PAYERNAME,
        ''                                 AS SERVICENAME,
        N'영중소차액정산'                  AS PRODUCTNAME,
        ISNULL(B.incVTax,0)                AS PGINCVTAX,
        ISNULL(C.incVTax,0)                AS CLINCVTAX,
        0                                   AS ABROADCHK,
        A.CompanySalesType                AS CompanySalesType,
        A.USESTATE                        AS USESTATE,
        0                                   AS NonSettleAmt,
        0                                   AS TXAMT,
        A.TxAmt                            AS ExtraTxAmt,
        ISNULL(
            CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0) AS INT)
                 WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0) AS INT)
                 ELSE 0 END, 0) * IIF(C.ExtraSettleFlag = 'Y', 1, 0)   AS CLCOMM,
        ISNULL(
            CASE WHEN A.PGName = 'dacomcard' OR A.PGName = 'tosscard' THEN
                CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * ((B.CommissionRate*1.1)/100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate0*1.1)/100.0)) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * ((B.CommissionRate*1.1)/100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate1*1.1)/100.0)) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * ((B.CommissionRate*1.1)/100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate2*1.1)/100.0)) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * ((B.CommissionRate*1.1)/100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate3*1.1)/100.0)) AS INT)
                     ELSE 0 END
            ELSE
                CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate0) / 100.0) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate1) / 100.0) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate2) / 100.0) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate3) / 100.0) AS INT)
                     ELSE 0 END
            END, 0)                                                                     AS PGCOMM,
        1                AS INSTATE,
        A.ExtraSettleYMD AS INYMD
    FROM PaymentDB.dbo.TExtraSettleIn AS A
    -- B/C 조인은 원 거래일(A.OrgYMD) 기준 요율표를 참조한다(OrgYMD, YMD, PGName, MID/MALLID, CLIENTID 키 포괄)
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGSettleRate4Extra AS B
        ON B.YMD = A.OrgYMD AND B.PGNAME = A.PGNAME AND B.MALLID = A.MID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4Extra AS C
        ON C.YMD = A.OrgYMD AND C.PGNAME = A.PGNAME AND C.MALLID = A.MID AND C.CLIENTID = A.CLIENTID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientContract AS E
        ON E.CLIENTID = A.CLIENTID
    WHERE A.ResYMD = @p_ymd
      AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
      AND A.ResultCode = '00'
      AND A.CompanySalesType IN (0,1,2,3)
      AND A.RefundTxType = 0
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty AS Y
    ON Y.PGName = X.PGNAME;

-- SQL_UPDATE1_OUTSTATE
/* U1: 정산일(OutState/OutYMD) 갱신 */
UPDATE A
   SET A.OutState = 2,
       A.OutYMD = (SELECT S.OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID) AS S)
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS C
     ON C.PGNAME = A.PGNAME AND C.MALLID = A.MALLID AND C.CLIENTID = A.CLIENTID
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(A.CompanySalesType,4) IN (0,1,2,3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;

-- SQL_UPDATE2_OUTYMD
/* U2: 당일 이전 정산일 보정 */
DECLARE @v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112);
UPDATE A
   SET A.OutYMD = dbo.UF_Get_WorkDay2(@v_strCurrYMD, 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(A.CompanySalesType,4) IN (0,1,2,3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.OutState = 2
   AND A.OutYMD <= @v_strCurrYMD;

-- SQL_UPDATE3_SIGNFLIP
/* U3: 영중소 차액정산 고객사/PG 항목 부호 반전(원본 @@ERROR 검사 없음) */
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
   AND PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND USESTATE IN (0)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_UPDATE4_TOTALS
/* U4: CLTOTAL/PGTOTAL/POQINCOME 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL   = (CLCOMM + CLVT + CLETC + CLINTCOMM),
       PGTOTAL   = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)),
       POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM)
                 - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;

-- SQL_UPDATE5_VATSPLIT
/* U5: 부가세포함 계산방식 재계산 */
-- (규칙 5-1) 원본이 초기값과 함께 선언한 지역 변수
DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal = (CLComm + CLEtc + CLIntComm),
       CLComm  = (CAST(CLComm/@v_valIncVat AS INT) + CAST(CLEtc/@v_valIncVat AS INT) + CAST(CLIntComm/@v_valIncVat AS INT)),
       CLVT    = (CLComm + CLEtc + CLIntComm)
               - (CAST(CLComm/@v_valIncVat AS INT) + CAST(CLEtc/@v_valIncVat AS INT) + CAST(CLIntComm/@v_valIncVat AS INT)),
       POQIncome = (CLComm + CLEtc + CLIntComm) - PGTotal
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND CLVTType = 1
   AND ExtraSettleFlag = 1;
```

**오류 코드 매핑**: `-9`(IF 1 사전 검증 실패, 트랜잭션 미개시), `-1`(DELETE 1 실패), `-2`(INSERT 1 또는 UPDATE 1 실패 구간 — 원본이 INSERT 1에 별도 검사가 없어 UPDATE 1의 `-2`가 사실상 이 구간 전체를 커버), `-3`(UPDATE 2 실패), `-4`(UPDATE 4 실패), `-21`(UPDATE 5 실패). U3은 원본에 대응 코드가 없으므로 규칙 9에 따라 새 코드를 만들지 않고 실패를 무시한 채 다음 문장으로 진행한다. `COMMIT TRAN`까지 정상 도달한 성공 경로에서는 원본이 `@po_intRetVal`을 설정하지 않으므로 `batch.BatchStepJournal.LegacyReturnCode`에 `NULL`을 기록하며, 위 코드들은 실패로 판정된 경우에만 기록 대상이 된다.