### S05 | 추가정산 원장 적재

`UP_UTIL_SETTLE_INS_EXTRA`를 대체하는 단계로, 영중소 차액정산 데이터를 `SETTLE_POQ_DB.dbo.TSettleMst`에 등록한다. 원본 인터페이스는 `@pi_strYMD CHAR(8)`(입력, 처리 기준일) · `@po_intRetVal INT`(출력, 성공/실패 코드) 두 개뿐이며, 배치 애플리케이션은 이 파라미터 목록 이외의 입력(재시작/스킵용 플래그 등)을 추가하지 않는다. `@po_intRetVal`은 `batch.BatchStepJournal.LegacyReturnCode`로 매핑되어 기록된다.

원본은 `DELETE → INSERT → UPDATE(지급상태) → UPDATE(지급예정일 보정) → UPDATE(부호반전) → UPDATE(합계재계산) → UPDATE(부가세포함보정)` 순으로 하나의 트랜잭션 안에서 실행되지만, 대상 데이터량이 크므로 본 단계는 `TSettleMst.YMD`(원본 DELETE·UPDATE1~5 모두의 최상위 WHERE 술어 컬럼에 이미 존재하는 컬럼)를 청크 키로 사용해 청크 단위로 커밋한다. **원본에 없는 컬럼(예: CLIENTID)은 어떤 문장의 WHERE에도 추가하지 않는다** — UPDATE1에서 `CLIENTID`는 오직 `TClientCMRate` 조인 키(`A.PGNAME=C.PGNAME AND A.MALLID=C.MALLID AND A.CLIENTID=C.CLIENTID`)로만 등장하며 원본 그대로 유지하고, UPDATE2~5의 필터 컬럼 목록에는 CLIENTID가 없으므로 절대 추가하지 않는다. INSERT 1은 최상위 WHERE 술어가 없고 파생 테이블 X 안에서만 조건이 걸리므로, `X.YMD = A.ReqYMD` 매핑을 이용해 `A.ReqYMD` 범위를 청크 경계로 사용한다(원본 컬럼 매핑을 그대로 활용한 것이며 새 컬럼을 도입한 것이 아니다).

청크 단위 커밋이므로 규칙 4에 따라 섀도우가 필요하다(공유 컨벤션: S02~S06은 섀도우 사용). 대상 테이블이 `SETTLE_POQ_DB.dbo.TSettleMst` 하나뿐이므로 섀도우도 이 테이블 하나만 포괄하면 충분하다. 모든 SELECT/JOIN은 SNAPSHOT 격리 수준 하에서 수행되어야 하며, 원본의 `WITH(NOLOCK)` 힌트는 전부 제거한다(규칙 10). `UPDATE 1`의 `OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))` 스칼라 하위질의 형태는 `CROSS APPLY`로 바꾸지 않고 원본 그대로 유지한다 — 이 자리는 함수 결과가 없을 때 `NULL`이 대입되는 자리이기 때문이다. 사전 가드(EXISTS 검사, `-9`)는 매 호출마다 무조건 실행되며 우회 파라미터를 두지 않는다(규칙 5).

```pseudocode
// (rule 6-1) 상태 변수는 NULL로 시작한다
currentStepErrorCode = NULL
StepCode = "S05"

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, StepCode, status: "Running", legacyReturnCode: NULL)

// --- 원본 라인 19~26: 성능상 이유로 요청일 최솟값을 먼저 조회 ---
v_strReqYMD = queryScalar(SQL_MIN_REQ_YMD, { p_ymd: batchYmd })
v_strCurrYMD = currentDateYyyymmdd()
v_valIncVat = 1.1   // 원본 지역 변수 @v_valIncVat(부가세 포함 나눗셈 계수), 값 그대로 유지

// --- 원본 라인 30~39: 사전 가드는 우회 불가, 매 호출 무조건 실행 ---
currentStepErrorCode = -9
guardHit = queryScalar(SQL_PRECHECK_SETTLED_LEDGER, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD })
IF guardHit == 1:
    writeStepJournal(runId, StepCode, status: "Failed", legacyReturnCode: -9)
    stop the pipeline

// --- 섀도우는 롤백 가능한 트랜잭션이 열리기 전에 캡처(rule 4a) ---
execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_batchYmd: batchYmd, p_reqYmd: v_strReqYMD })
shadowCaptured = true

FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD }, size: 10000):
    beginTransaction()
    try:
        // DELETE 1 (line 47) — INSERT 1(line 63)이 같은 다음 체크포인트(-2)를 공유하므로
        // 원본 흐름과 동일하게 DELETE만 자신의 고유 코드 -1을 갖는다.
        currentStepErrorCode = -1
        execute(SQL_DELETE_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_from: chunk.from, p_to: chunk.to })

        // INSERT 1 (line 63) — 원본에서 별도 @@ERROR 체크가 없어 다음 UPDATE1의 -2 체크포인트에 귀속
        currentStepErrorCode = -2
        execute(SQL_INSERT_CHUNK, { p_ymd: batchYmd, p_from: chunk.from, p_to: chunk.to })

        // UPDATE 1 / 갱신 1 (line 211) — 지급상태·지급예정일
        currentStepErrorCode = -2
        execute(SQL_UPDATE1_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_from: chunk.from, p_to: chunk.to })

        // UPDATE 2 / 갱신 2 (line 233) — 지급예정일 보정
        currentStepErrorCode = -3
        execute(SQL_UPDATE2_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_currYmd: v_strCurrYMD, p_from: chunk.from, p_to: chunk.to })

        // UPDATE 3 / 갱신 3 (line 254) — 부호 반전. 원본에 별도 체크 없어 다음 -4 체크포인트에 귀속
        currentStepErrorCode = -4
        execute(SQL_UPDATE3_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_from: chunk.from, p_to: chunk.to })

        // UPDATE 4 / 갱신 4 (line 276) — 합계 재계산
        currentStepErrorCode = -4
        execute(SQL_UPDATE4_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_from: chunk.from, p_to: chunk.to })

        // UPDATE 5 / 갱신 5 (line 300) — 부가세 포함 보정
        currentStepErrorCode = -21
        execute(SQL_UPDATE5_CHUNK, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD, p_valIncVat: v_valIncVat, p_from: chunk.from, p_to: chunk.to })

        commit()
    catch failure:
        rollbackIfOpen()   // 이 청크만 롤백. 이전 청크는 이미 커밋되어 남아 있음(rule 8-1)

        beginTransaction()
        // (rule 4b) 이 단계가 삭제했던 것과 정확히 동일한 범위만 복구
        execute(SQL_RESTORE_DELETE, { p_ymd: batchYmd, p_reqYmd: v_strReqYMD })
        execute(SQL_RESTORE_INSERT, { p_runId: runId, p_batchYmd: batchYmd })
        commit()

        writeStepJournal(runId, StepCode, status: "Failed", legacyReturnCode: currentStepErrorCode)
        stop the pipeline

writeStepJournal(runId, StepCode, status: "Succeeded", legacyReturnCode: 0)
writeCheckpoint(runId, StepCode, status: "Succeeded")
// (rule 4e) batch_shadow.TSettleMst_<runId>_S05 는 24시간 후 부트스트랩 정리 작업이 자동 삭제
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_MIN_REQ_YMD (원본 라인 21~25, 집계 SELECT는 무결과여도 NULL을 반환)
SELECT MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

-- SQL_PRECHECK_SETTLED_LEDGER (원본 라인 30~39, -9 가드, 무조건 실행)
SELECT CASE WHEN EXISTS (
    SELECT PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE ProcYMD = @p_ymd
       AND YMD >= @p_reqYmd
       AND OutState IN (1,5)
       AND OutYMD IS NOT NULL
       AND PGName IN ('allthegate','dacomcard','tosscard','nicecard')
       AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
       AND ExtraSettleFlag = 1
) THEN 1 ELSE 0 END;

-- SQL_CREATE_AND_CAPTURE_SHADOW (rule 4a, 4c: 값은 전부 파라미터, 테이블명만 조립)
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S05';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N'
  SELECT * FROM SETTLE_POQ_DB.dbo.TSettleMst
   WHERE ProcYMD = @p_ymd AND YMD >= @p_reqYmd
     AND PGName IN (''allthegate'',''dacomcard'',''tosscard'',''nicecard'')
     AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
     AND TxAmt = 0 AND ExtraSettleFlag = 1;';
EXEC sp_executesql @v_sql, N'@p_ymd CHAR(8), @p_reqYmd CHAR(8)',
    @p_ymd = @p_batchYmd, @p_reqYmd = @p_reqYmd;

-- SQL_CHUNK_BOUNDS (원본 업무 필터를 경계 조회에도 포함, A.ReqYMD 기준)
SELECT MIN(ReqYMD), MAX(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

-- SQL_DELETE_CHUNK  (DELETE 1, 원본 라인 47~53 필터 + 청크 범위 AND 결합)
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGName IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1
   AND YMD >= @p_from AND YMD < @p_to;

-- SQL_INSERT_CHUNK  (INSERT 1, 원본 라인 63~209 파생 테이블 X 전체 보존 + A.ReqYMD 청크 범위)
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
    (YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
     PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME, PGVTTYPE, CLVTTYPE, ABROADCHK,
     CompanySalesType, USESTATE, NonSettleAmt, ExtraTxAmt, TXAMT,
     CLCOMMTYPE, PGCOMMTYPE, CLETC, PGETC, CLTOTAL, PGTOTAL, POQINCOME,
     CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM, CLCOMM, CLVT, PGCOMM, PGVT,
     INSTATE, INYMD, ProcYMD, ProcState, ExtraSettleFlag)
SELECT
    X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME, X.PGINCVTAX, X.CLINCVTAX, X.ABROADCHK,
    X.CompanySalesType, X.USESTATE, X.NonSettleAmt, X.ExtraTxAmt, X.TXAMT,
    0, 0, 0, 0, 0, 0, 0,
    0, 0, 0,
    CASE WHEN X.AYMD < '20190501' THEN 0 ELSE CAST(ISNULL(X.CLCOMM,0) AS INT) END,
    CASE WHEN X.AYMD < '20190501' THEN 0 ELSE dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM,0) * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)) END,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT) END,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag),0,Y.CommRoundFlag)
                    -(ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag)/1.1,0,Y.CommRoundFlag)),0,Y.VatRoundFlag) AS INT) END,
    1, X.INYMD, @p_ymd,
    CASE WHEN (X.CLCOMM IS NULL OR X.PGCOMM IS NULL) THEN 1 ELSE NULL END,
    1
FROM (
    SELECT
        A.ReqYMD AS YMD,
        IIF(A.UseState=0, NULL, A.YMD) AS CYMD,
        A.OrgYMD AS AYMD,
        A.CLIENTID, A.PGNAME, A.MID AS MALLID, A.PLTID, A.TID, A.SeqNo AS CID,
        '' AS PAYERID, '' AS PAYERNAME, '' AS SERVICENAME, N'영중소차액정산' AS PRODUCTNAME,
        ISNULL(B.incVTax,0) AS PGINCVTAX, ISNULL(C.incVTax,0) AS CLINCVTAX,
        0 AS ABROADCHK, A.CompanySalesType, A.USESTATE, 0 AS NonSettleAmt,
        A.TxAmt AS ExtraTxAmt, 0 AS TXAMT, A.ExtraSettleYMD AS INYMD,
        B.incVTax AS PGIncVTax, C.incVTax AS CLIncVTax,
        ISNULL(CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0 ) AS INT)
                    WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0 ) AS INT)
                    WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0 ) AS INT)
                    WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0 ) AS INT)
                    ELSE 0 END,0) * IIF(C.ExtraSettleFlag='Y',1,0) AS CLCOMM,
        ISNULL(CASE WHEN A.PGName='dacomcard' OR A.PGName='tosscard' THEN
                CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT) - CAST((A.TxAmt * ((B.CommRate0 * 1.1) / 100.0) ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT) - CAST((A.TxAmt * ((B.CommRate1 * 1.1) / 100.0) ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT) - CAST((A.TxAmt * ((B.CommRate2 * 1.1) / 100.0) ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT) - CAST((A.TxAmt * ((B.CommRate3 * 1.1) / 100.0) ) AS INT)
                     ELSE 0 END
             ELSE
                CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate0) / 100.0 ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate1) / 100.0 ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate2) / 100.0 ) AS INT)
                     WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate3) / 100.0 ) AS INT)
                     ELSE 0 END END,0) AS PGCOMM
    FROM PaymentDB.dbo.TExtraSettleIn A
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGSettleRate4Extra B
           ON B.YMD = A.YMD AND B.PGNAME = A.PGNAME AND B.MALLID = A.MID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4Extra C
           ON C.YMD = A.YMD AND C.PGNAME = A.PGNAME AND C.MALLID = A.MID AND C.CLIENTID = A.CLIENTID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientContract E ON E.CLIENTID = A.CLIENTID
    WHERE A.ResYMD = @p_ymd
      AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
      AND A.ResultCode = '00'
      AND A.CompanySalesType IN (0,1,2,3)
      AND A.RefundTxType = 0
      AND A.ReqYMD >= @p_from AND A.ReqYMD < @p_to   -- 청크 경계(X.YMD = A.ReqYMD 매핑 활용)
) X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y ON Y.PGName = X.PGNAME;

-- SQL_UPDATE1_CHUNK  (갱신 1, 원본 라인 211~225. CLIENTID는 조인 키로만 사용, WHERE 필터에 추가하지 않음)
UPDATE A
   SET OutState = 2,
       OutYMD = (SELECT OutYMD FROM SETTLE_POQ_DB.dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate C
    ON A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(A.CompanySalesType,4) IN (0,1,2,3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.YMD >= @p_from AND A.YMD < @p_to;

-- SQL_UPDATE2_CHUNK  (갱신 2, 원본 라인 233~243)
UPDATE A
   SET OutYMD = dbo.UF_Get_WorkDay2(@p_currYmd, 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(A.CompanySalesType,4) IN (0,1,2,3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.OutState = 2
   AND A.OutYMD <= @p_currYmd
   AND A.YMD >= @p_from AND A.YMD < @p_to;

-- SQL_UPDATE3_CHUNK  (갱신 3, 원본 라인 254~271. SET 우변은 갱신 이전 값 기준 동시 평가 — 단일 UPDATE 문으로 그대로 유지)
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
   AND ExtraSettleFlag = 1
   AND YMD >= @p_from AND YMD < @p_to;

-- SQL_UPDATE4_CHUNK  (갱신 4, 원본 라인 276~285)
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL = (CLCOMM + CLVT + CLETC + CLINTCOMM),
       PGTOTAL = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)),
       POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM)
                 - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1
   AND YMD >= @p_from AND YMD < @p_to;

-- SQL_UPDATE5_CHUNK  (갱신 5, 원본 라인 300~311. CLComm 자기참조 동시 평가 — 단일 UPDATE 문 유지)
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal = (CLComm + CLEtc + CLIntComm),
       CLComm  = (CAST(CLComm/@p_valIncVat AS INT) + CAST(CLEtc/@p_valIncVat AS INT) + CAST(CLIntComm/@p_valIncVat AS INT)),
       CLVT    = (CLComm + CLEtc + CLIntComm)
               - (CAST(CLComm/@p_valIncVat AS INT) + CAST(CLEtc/@p_valIncVat AS INT) + CAST(CLIntComm/@p_valIncVat AS INT)),
       POQIncome = (CLComm + CLEtc + CLIntComm) - PGTotal
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND TxAmt = 0
   AND CLVTType = 1
   AND ExtraSettleFlag = 1
   AND YMD >= @p_from AND YMD < @p_to;

-- SQL_RESTORE_DELETE (rule 4b, 삭제했던 범위와 정확히 동일하게 복구)
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGName IN ('allthegate','dacomcard','tosscard','nicecard')
   AND ISNULL(CompanySalesType,4) IN (0,1,2,3)
   AND ExtraSettleFlag = 1;

-- SQL_RESTORE_INSERT (동일 조립 이름의 섀도우에서 통째로 복원)
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S05';
DECLARE @v_sql NVARCHAR(MAX) =
    N'INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst SELECT * FROM ' + @v_shadow + N';';
EXEC sp_executesql @v_sql;
```

원본 오류 코드 `-9`(가드 위반), `-1`(DELETE 1), `-2`(INSERT 1·UPDATE 1 공통 체크포인트), `-3`(UPDATE 2), `-4`(UPDATE 3·UPDATE 4 공통 체크포인트), `-21`(UPDATE 5)은 그대로 재사용되며, `batch.BatchStepJournal.LegacyReturnCode`에 실패 시점의 `currentStepErrorCode` 값이 그대로 기록된다. 재시작 시 오케스트레이터는 `batch.BatchCheckpoint`에서 `S05`가 `Succeeded`인지 확인해 스킵 여부를 결정하며, 이 단계 자체는 스킵/재시작을 위한 어떤 파라미터도 받지 않는다.