### S03 | 기본 정산 원장 적재(정상/부분취소/환불)

**레거시 원본**: `dbo.UP_UTIL_SETTLE_INS` (파라미터: `@pi_strYMD CHAR(8)`, `@po_intRetVal INT`)
**대상 테이블**: `SETTLE_POQ_DB.dbo.TSettleMst`
**원본 오류 코드**: `-9`(기정산 완료건 존재), `-1`(DELETE 실패), `-2`(INSERT 실패)

#### 아키텍처 판단

본 단계는 하루(`@pi_strYMD`) 단위로 `TSettleMst`를 삭제-재적재하는 패턴이다. `DELETE`는 명세서 DML 범위 표상 최상위 술어 컬럼이 `YMD` 하나뿐이므로 별도의 청크 키를 추가하지 않고 **단 한 번, 단일 트랜잭션**으로 수행한다(원본 그대로 보존). 반면 `INSERT`는 `TTxMst(A)·TPGSettleRate(B)·TClientSettleRate(C)·TPGCMRate(D)·TPartialCancelTxMst/TRefundMst/TRefundClient(E,F)`를 묶는 3-분기 `UNION ALL` 파생 테이블이며 이후 `TPGProperty(Y)`와 `LEFT OUTER JOIN`하는 복합 크로스-DB 조인이다. 이 INSERT는 각 분기 모두 `PaymentDB.dbo.TTxMst A`를 포함하고 `A.CLIENTID`가 `TSettleMst.CLIENTID`로 그대로 전사(轉寫)되므로, `A.CLIENTID` 범위를 기존 업무 필터에 `AND`로 결합하여 청크로 나눌 수 있다(규칙 8, 12).

DELETE(1회, 전량 삭제)와 INSERT(청크 커밋)가 분리되어 있고 INSERT 도중 실패 시 DELETE로 지워진 범위를 롤백만으로 복구할 수 없으므로, 본 단계는 **섀도우 테이블을 사용하는 Shadow Table Pattern**을 채택한다(규칙 4). 원본의 `IF 2`(동일 YMD 데이터 존재 확인 후에만 DELETE)는 DELETE가 조건 없이도 무해하게 0건 삭제로 끝나므로 생략하고 매 호출마다 무조건 DELETE를 수행한다 — 단, `IF 1`의 기정산 완료건 가드(`-9`)는 규칙 5에 따라 매 호출마다 무조건, 우회 불가능하게 실행한다. 본 단계는 SNAPSHOT 격리 수준에서 실행되어야 한다. 원본의 모든 `WITH(NOLOCK)` 힌트는 제거한다(규칙 10).

#### 의사코드 (배치 애플리케이션 계층)

```pseudocode
currentStepErrorCode = NULL

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, "S03", status: "Running", legacyReturnCode: NULL)

// (규칙 5) 기정산 완료건 가드는 매 호출마다 무조건 실행 — 우회 파라미터 없음
currentStepErrorCode = -9
settledExists = queryScalar(SQL_PRECHECK_SETTLED_EXISTS, { p_batchYmd: batchYmd })
IF settledExists == 1:
    // 원본은 BEGIN TRAN 이전이므로 롤백 대상 트랜잭션이 없다
    writeStepJournal(runId, "S03", status: "Failed", legacyReturnCode: -9)
    stop the pipeline

// (규칙 4a) 섀도우는 롤백 가능한 트랜잭션이 열리기 전에, 그 밖에서 캡처한다
execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_batchYmd: batchYmd })
shadowCaptured = true

try:
    beginTransaction()
    currentStepErrorCode = -1
    execute(SQL_DELETE_RANGE, { p_batchYmd: batchYmd })
    commit()

    FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, {}, size: 10000):
        beginTransaction()
        currentStepErrorCode = -2
        execute(SQL_INSERT_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
        commit()

    writeStepJournal(runId, "S03", status: "Succeeded", legacyReturnCode: 0)
    writeCheckpoint(runId, "S03", status: "Succeeded")

catch failure:
    rollbackIfOpen()
    IF shadowCaptured:
        beginTransaction()
        execute(SQL_RESTORE_DELETE, { p_batchYmd: batchYmd })
        execute(SQL_RESTORE_INSERT, { p_runId: runId })
        commit()
    writeStepJournal(runId, "S03", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

#### 실행 SQL

```sql
-- SQL_PRECHECK_SETTLED_EXISTS : 원본 IF 1(라인 26) 가드. 실패 시 -9
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_batchYmd AND OutState IN (1, 5) AND OutYMD IS NOT NULL
) THEN 1 ELSE 0 END AS SettledExists;

-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_CREATE_AND_CAPTURE_SHADOW : 삭제될 TSettleMst 원본 행을 통째로 백업(SELECT *)
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S03';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N' SELECT * FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_batchYmd;';
EXEC sp_executesql @v_sql, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;

-- SQL_DELETE_RANGE
/* DELETE 1: 동일 정산 기준일(YMD)의 기존 정산 데이터 삭제. 명세서 DML 범위 표 최상위 술어 컬럼은 YMD 뿐이므로 다른 술어를 추가하지 않는다 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_batchYmd;

-- SQL_CHUNK_BOUNDS : CLIENTID는 세 분기 모두에서 A.CLIENTID로부터 파생되어 TSettleMst.CLIENTID로 적재되므로
-- 청크 키로 유효하다(규칙 12). 부분취소/환불 분기는 A.YMD가 @p_batchYmd와 다를 수 있어 날짜로 경계를
-- 좁히지 않고 전체 CLIENTID 범위를 사용한다(비어있는 청크는 무해하게 0건 처리된다).
SELECT MIN(CLIENTID), MAX(CLIENTID) FROM PaymentDB.dbo.TTxMst;

-- SQL_INSERT_CHUNK
/* INSERT 1: 정산 데이터 등록(전체거래건 USESTATE=0 / 부분취소 USESTATE=2 / 환불 USESTATE=3),
   CLIENTID 청크 범위를 각 분기의 원본 업무 필터에 AND로 결합 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
(
    YMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID, PAYERID, PAYERNAME,
    SERVICENAME, PRODUCTNAME, TXAMT, CLCOMMTYPE, CLVTTYPE, CLCOMM, CLVT, CLETC,
    PGCOMMTYPE, PGVTTYPE, PGCOMM, PGVT, PGETC, USESTATE, CLTOTAL, PGTOTAL,
    POQINCOME, CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM, ABROADCHK, NonSettleAmt,
    SeperateAmt, AllotPeriod, DiscountAmt, DiscountFlag, PointAmt, MPLTID,
    MobileCo, CardAmt, CouponAmt, MoneyAmt
)
SELECT
    X.YMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID, X.PAYERID, X.PAYERNAME,
    X.SERVICENAME, X.PRODUCTNAME, X.TXAMT, X.CLCOMMTYPE, X.CLINCVTAX AS CLVTTYPE,
    CAST(X.CLCOMM AS INT) AS CLCOMM,
    CAST(dbo.UF_GET_ROUND4VAT((X.CLCOMM + X.CLETC) * dbo.UF_GET_INCVTAXRATE(X.CLINCVTAX)) AS INT) AS CLVT,
    CAST(X.CLETC AS INT) AS CLETC,
    X.PGCOMMTYPE, X.PGINCVTAX AS PGVTTYPE,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
    END AS PGCOMM,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGINCVTAX), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                ROUND((ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag)), 0, Y.CommRoundFlag)
                - (ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)
                   + ROUND(ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)),
                0, Y.VatRoundFlag) AS INT)
    END AS PGVT,
    CAST(X.PGETC AS INT) AS PGETC,
    X.USESTATE,
    0 AS CLTOTAL, 0 AS PGTOTAL, 0 AS POQINCOME, 0 AS CLINTCOMM, 0 AS PGINTEXPCOMM, 0 AS PGINTREALCOMM,
    X.ABROADCHK, X.NonSettleAmt, 0 AS SeperateAmt, X.AllotPeriod,
    X.DiscountAmt, X.DiscountFlag, X.PointAmt, X.MPLTID, X.MobileCo, X.CardAmt, X.CouponAmt, X.MoneyAmt
FROM (
    -- 분기 1: 전체거래건(USESTATE=0)
    SELECT
        @p_batchYmd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID,
        A.PLTID, A.TID, A.CID, A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME,
        A.TXAMT,
        C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
        IIF(C.COMMISSIONTYPE = 0, ((A.TXAMT - ISNULL(A.NonSettleAmt,0)) * (C.COMMISSIONRATE/100.0)), C.COMMISSIONAMT) AS CLCOMM,
        C.ETCAMT AS CLETC,
        B.COMMISSIONTYPE AS PGCOMMTYPE, B.INCVTAX AS PGINCVTAX,
        IIF(B.COMMISSIONTYPE = 0, (A.TXAMT * (B.COMMISSIONRATE/100.0)), B.COMMISSIONAMT) AS PGCOMM,
        B.ETCAMT AS PGETC,
        IIF(B.COMMISSIONTYPE = 0,
            (A.TXAMT * ((B.COMMISSIONRATE/100.0) + (B.COMMISSIONRATE/100.0/10.0))),
            (B.COMMISSIONAMT + (B.COMMISSIONAMT/10.0))) AS PGCOMM4SUM,
        (B.ETCAMT + (B.ETCAMT/10.0)) AS PGETC4SUM,
        0 AS USESTATE,
        A.ABROADCHK, ISNULL(A.NonSettleAmt,0) AS NonSettleAmt, A.AllotPeriod,
        0 AS DiscountAmt, 'N' AS DiscountFlag, A.PointAmt, A.MPLTID,
        IIF(A.PGName = 'impaymobile', A.CardCode, '') AS MobileCo,
        A.CardAmt, A.CouponAmt, A.MoneyAmt
    FROM PaymentDB.dbo.TTxMst A
    JOIN SETTLE_POQ_DB.dbo.TPGSettleRate B ON A.YMD = B.YMD AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
    JOIN SETTLE_POQ_DB.dbo.TClientSettleRate C ON A.YMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
    JOIN SETTLE_POQ_DB.dbo.TPGCMRate D ON A.PGNAME = D.PGNAME AND A.MALLID = D.MALLID
    WHERE A.YMD = @p_batchYmd
      AND D.TAXEXEMPTIONFLAG = 0
      AND A.CLIENTID >= @p_from AND A.CLIENTID < @p_to

    UNION ALL

    -- 분기 2: 부분취소 거래건(USESTATE=2)
    SELECT
        @p_batchYmd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID,
        E.PLTID, E.TID, E.CID, A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME,
        E.TXAMT,
        C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
        IIF(C.COMMISSIONTYPE = 0, (E.TXAMT * (C.COMMISSIONRATE/100.0)), C.COMMISSIONAMT) AS CLCOMM,
        C.ETCAMT AS CLETC,
        B.COMMISSIONTYPE AS PGCOMMTYPE, B.INCVTAX AS PGINCVTAX,
        IIF(B.COMMISSIONTYPE = 0, (E.TXAMT * (B.COMMISSIONRATE/100.0)), B.COMMISSIONAMT) AS PGCOMM,
        B.ETCAMT AS PGETC,
        IIF(B.COMMISSIONTYPE = 0,
            (E.TXAMT * ((B.COMMISSIONRATE/100.0) + (B.COMMISSIONRATE/100.0/10.0))),
            (B.COMMISSIONAMT + (B.COMMISSIONAMT/10.0))) AS PGCOMM4SUM,
        (B.ETCAMT + (B.ETCAMT/10.0)) AS PGETC4SUM,
        2 AS USESTATE,
        A.ABROADCHK, 0 AS NonSettleAmt, A.AllotPeriod,
        IIF(E.DiscountFlag = 'Y', E.DiscountAmt, 0) AS DiscountAmt, E.DiscountFlag, E.PointAmt, A.MPLTID,
        IIF(A.PGName = 'impaymobile', A.CardCode, '') AS MobileCo,
        E.CardAmt, E.CouponAmt, E.MoneyAmt
    FROM PaymentDB.dbo.TTxMst A
    JOIN SETTLE_POQ_DB.dbo.TPGSettleRate B ON A.YMD = B.YMD AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
    JOIN SETTLE_POQ_DB.dbo.TClientSettleRate C ON A.YMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
    JOIN SETTLE_POQ_DB.dbo.TPGCMRate D ON A.PGNAME = D.PGNAME AND A.MALLID = D.MALLID
    JOIN PaymentDB.dbo.TPartialCancelTxMst E ON A.PLTID = E.PLTID
    WHERE E.YMD = @p_batchYmd
      AND D.TAXEXEMPTIONFLAG = 0
      AND E.SettleState = 1
      AND A.CLIENTID >= @p_from AND A.CLIENTID < @p_to

    UNION ALL

    -- 분기 3: 환불건(USESTATE=3)
    SELECT
        @p_batchYmd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID,
        A.PLTID, A.TID, A.CID, A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME,
        E.REFUNDREQAMT AS TXAMT,
        C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
        CASE WHEN F.FeeFlag = 'Y'
             THEN IIF(F.FeeRateType = 3, (E.REFUNDREQAMT * (F.FeeRate/100.0)), F.FeeRate) * (-1)
             ELSE IIF(C.CommissionType = 0, (E.REFUNDREQAMT * (C.CommissionRate/100.0)), C.CommissionAmt)
        END AS CLCOMM,
        CASE WHEN F.FeeFlag = 'Y' THEN 0 ELSE C.EtcAmt END AS CLETC,
        B.COMMISSIONTYPE AS PGCOMMTYPE, B.INCVTAX AS PGINCVTAX,
        0 AS PGCOMM, 0 AS PGETC, 0 AS PGCOMM4SUM, 0 AS PGETC4SUM,
        3 AS USESTATE,
        A.ABROADCHK, 0 AS NonSettleAmt, A.AllotPeriod,
        0 AS DiscountAmt, 'N' AS DiscountFlag, A.PointAmt, A.MPLTID,
        IIF(A.PGName = 'impaymobile', A.CardCode, '') AS MobileCo,
        0 AS CardAmt, 0 AS CouponAmt, 0 AS MoneyAmt
    FROM PaymentDB.dbo.TTxMst A
    JOIN SETTLE_POQ_DB.dbo.TPGSettleRate B ON A.YMD = B.YMD AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
    JOIN SETTLE_POQ_DB.dbo.TClientSettleRate C ON A.YMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
    JOIN SETTLE_POQ_DB.dbo.TPGCMRate D ON A.PGNAME = D.PGNAME AND A.MALLID = D.MALLID
    JOIN PaymentDB.dbo.TRefundMst E ON A.PLTID = E.PLTID
    JOIN PaymentDB.dbo.TRefundClient F ON A.PGNAME = F.PGNAME AND A.MALLID = F.MALLID AND A.CLIENTID = F.CLIENTID
    WHERE E.REQYMD = @p_batchYmd
      AND D.TAXEXEMPTIONFLAG = 0
      AND A.CLIENTID >= @p_from AND A.CLIENTID < @p_to
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y ON X.PGNAME = Y.PGName;

-- SQL_RESTORE_DELETE : 이 단계가 삭제한 것과 동일한 범위(YMD)만 복구 대상으로 삼는다
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_batchYmd;

-- SQL_RESTORE_INSERT : 캡처 시점과 동일한 조립 규칙으로 섀도우 이름을 재구성
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S03';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst SELECT * FROM ' + @v_shadow + N';';
EXEC sp_executesql @v_sql;
```

#### 출력 파라미터 매핑

원본의 `@po_intRetVal`은 본 단계에서 `batch.BatchStepJournal.LegacyReturnCode`로 대체 기록된다 — 성공 시 `0`, 실패 시 `currentStepErrorCode`(`-9`/`-1`/`-2` 중 하나)가 기록된다. `@pi_strYMD`는 오케스트레이터가 전달하는 `batchYmd` 입력값 그대로 매핑되며, 이 단계는 그 외의 입력 파라미터를 추가로 받지 않는다(규칙 5).

#### 섀도우 보존 기간

`batch_shadow.TSettleMst_<RunId>_S03`은 캡처 후 24시간이 지나면 부트스트랩 정리 작업에 의해 자동 삭제된다.