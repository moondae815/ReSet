### S04 | 기준 정산 원장 생성

- **원본 프로시저**: `dbo.UP_UTIL_SETTLE_INS` (SETTLE_POQ_DB)
- **파라미터 매핑(rule 5-2)**: `@pi_strYMD CHAR(8) -> p_ymd`. `@po_intRetVal INT`는 OUT 파라미터이므로 바인딩 대상이 아니며(rule 5-2), 원본이 성공 경로에서 값을 대입하지 않는 그대로 이 단계도 성공 시 별도 값을 쓰지 않는다 — `batch.BatchStepJournal.StepStatus = 'Succeeded'`가 성공 판정의 유일한 기준이다(rule 13).
- **대상 테이블**: `SETTLE_POQ_DB.dbo.TSettleMst`
- **원본 오류코드(rule 9, 그대로 유지)**: `-9`(기정산건 존재 가드), `-1`(DELETE 실패), `-2`(INSERT 실패)
- **청크 전략**: 원본 INSERT 1은 3개 UNION ALL 분기(전체거래건/부분취소건/환불건)와 다중 크로스DB JOIN으로 구성된 단일 SELECT이며, 세 분기 모두 `TTxMst.PLTID`(또는 이와 동치조인되는 `TPartialCancelTxMst.PLTID`/`TRefundMst.PLTID`)를 공통 앵커로 갖는다. `TSettleMst`에도 `PLTID` 컬럼이 실재하므로(rule 12) 이 정수 키로 INSERT만 청크한다. **DELETE 1은 명세서 DML 범위 표의 최상위 술어 컬럼이 `YMD` 하나뿐이므로 PLTID를 추가하지 않고 원본과 동일하게 YMD 단일 조건으로 1회 실행**한다.
- **격리수준**: 이 단계의 모든 SQL은 SNAPSHOT 격리수준 하에서 실행되어야 한다(설정 위치는 구현 라운드 결정).
- **Shadow 사용 근거(rule 4)**: DELETE(전량 삭제, 별도 트랜잭션)와 INSERT(다중 청크 트랜잭션)가 서로 다른 트랜잭션 경계에서 커밋되는 재구성이므로, 이후 청크가 실패해도 앞선 삭제·청크는 이미 커밋되어 되돌릴 수 없다. 따라서 `batch_shadow.TSettleMst_<runid>_S04`를 트랜잭션 시작 **전**에 생성·적재한다.

```pseudocode
// (rule 3-1 공용 표기) runId 조회 — 이 이름 있는 SQL 블록은 본 절에서 아래에 정의한다.
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch10", p_ymd: batchYmd })
currentStepErrorCode = NULL
writeStepJournal(runId, "S04", status: "Running", legacyReturnCode: NULL)

// 1) 원본 IF 1 가드 — 재시작 스킵과 무관하게 호출마다 무조건 수행(rule 5)
currentStepErrorCode = -9
alreadySettled = queryScalar(SQL_CHECK_SETTLED_GUARD, { p_ymd: batchYmd })
IF alreadySettled > 0:
    writeStepJournal(runId, "S04", status: "Failed", legacyReturnCode: -9)
    stop the pipeline

// 2) 트랜잭션 밖에서 Shadow 생성 (rule 4a) — 이후 DELETE가 커밋되면 되돌릴 수 없으므로 먼저 백업
repository.execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_ymd: batchYmd })
shadowCaptured = true

// 3) 원본 IF 2 — 당일 기존 정산데이터 존재 여부에 따른 DELETE 1 (자신만의 트랜잭션, rule 8-1)
existingCount = queryScalar(SQL_CHECK_EXISTING_YMD, { p_ymd: batchYmd })
IF existingCount > 0:
    conn.beginTransaction()
    currentStepErrorCode = -1
    repository.execute(SQL_DELETE_RANGE, { p_ymd: batchYmd })
    conn.commit()

// 4) INSERT 1 청크 루프 — PLTID 정수 키 (rule 12), 각 청크 독립 트랜잭션 (rule 8-1)
lo, hi = queryRow(SQL_PLTID_BOUNDS, { p_ymd: batchYmd })   // MIN, MAX — 둘 다 포함
IF lo IS NULL: skip the loop
from = lo
WHILE from <= hi:
    to = from + 5000   // 배타적 상한
    conn.beginTransaction()
    currentStepErrorCode = -2
    repository.execute(SQL_INSERT_CHUNK, { p_ymd: batchYmd, p_from: from, p_to: to })
    conn.commit()
    from = to

writeStepJournal(runId, "S04", status: "Succeeded", legacyReturnCode: currentStepErrorCode)
```

```pseudocode
// 실패 관찰 시 (청크 커밋형이므로 Shadow 복구 수행, rule 4)
ON FAILURE observed by the application:
    conn.rollbackIfOpen()
    IF shadowCaptured:
        conn.beginTransaction()
        repository.execute(SQL_RESTORE_DELETE, { p_ymd: batchYmd })    // (b) 동일 범위(YMD)만 삭제
        repository.execute(SQL_RESTORE_INSERT, { p_runId: runId })
        conn.commit()
    writeStepJournal(runId, "S04", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_CHECK_SETTLED_GUARD - 원본 IF 1 (라인 25~30), 명세서 DML 범위 표에 행이 없는 가드 SELECT라 앵커 없음
SELECT COUNT(1) FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd AND OutState IN (1, 5) AND OutYMD IS NOT NULL;

-- SQL_CREATE_AND_CAPTURE_SHADOW - 테이블명만 조립, 값은 항상 파라미터(rule 4c)
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S04';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N' SELECT * FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;';
EXEC sp_executesql @v_sql, N'@p_ymd CHAR(8)', @p_ymd = @p_ymd;

-- SQL_CHECK_EXISTING_YMD - 원본 IF 2 (라인 39), 명세서 DML 범위 표에 행이 없는 가드 SELECT라 앵커 없음
SELECT COUNT(1) FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;

-- SQL_DELETE_RANGE
/* DELETE 1: 당일분 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;

-- SQL_PLTID_BOUNDS - 이 계획이 새로 들이는 청크 경계 조회(인프라), 명세서 DML 범위 표에 없으므로 앵커 없음
SELECT MIN(A.PLTID), MAX(A.PLTID)
  FROM PaymentDB.dbo.TTxMst A
 WHERE A.YMD = @p_ymd;

-- SQL_INSERT_CHUNK
/* INSERT 1: 정산 원장 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
    (YMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID, PAYERID, PAYERNAME,
     SERVICENAME, PRODUCTNAME, TXAMT, CLCOMMTYPE, CLVTTYPE, CLCOMM, CLVT, CLETC,
     PGCOMMTYPE, PGVTTYPE, PGCOMM, PGVT, PGETC, USESTATE, CLTOTAL, PGTOTAL,
     POQINCOME, CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM, ABROADCHK, NonSettleAmt,
     SeperateAmt, AllotPeriod, DiscountAmt, DiscountFlag, PointAmt, MPLTID,
     MobileCo, CardAmt, CouponAmt, MoneyAmt)
SELECT
    X.YMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME, X.TXAMT,
    X.CLCOMMTYPE, X.CLINCVTAX,
    CAST(X.CLCOMM AS INT),
    dbo.UF_GET_ROUND4VAT((X.CLCOMM + X.CLETC) * dbo.UF_GET_INCVTAXRATE(X.CLINCVTAX)),
    CAST(X.CLETC AS INT),
    X.PGCOMMTYPE, X.PGINCVTAX,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT) END,
    CASE WHEN Y.CommMethod = 0 THEN CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGINCVTAX), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                 ROUND( (ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag)), 0, Y.CommRoundFlag)
                 - (ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)
                    + ROUND(ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)),
                 0, Y.VatRoundFlag) AS INT) END,
    CAST(X.PGETC AS INT),
    X.USESTATE, 0, 0, 0, 0, 0, 0,
    X.ABROADCHK, X.NonSettleAmt, 0, X.AllotPeriod, X.DiscountAmt, X.DiscountFlag,
    X.PointAmt, X.MPLTID, X.MobileCo, X.CardAmt, X.CouponAmt, X.MoneyAmt
FROM (
    -- 분기1: 전체거래건 (USESTATE=0)
    SELECT @p_ymd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID, A.PLTID, A.TID, A.CID,
           A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME, A.TXAMT,
           C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
           IIF(C.COMMISSIONTYPE = 0, ((A.TXAMT - ISNULL(A.NonSettleAmt, 0)) * (C.COMMISSIONRATE / 100.0)), C.COMMISSIONAMT) AS CLCOMM,
           C.ETCAMT AS CLETC,
           B.COMMISSIONTYPE AS PGCOMMTYPE, B.INCVTAX AS PGINCVTAX,
           IIF(B.COMMISSIONTYPE = 0, (A.TXAMT * (B.COMMISSIONRATE / 100.0)), B.COMMISSIONAMT) AS PGCOMM,
           B.ETCAMT AS PGETC,
           IIF(B.COMMISSIONTYPE = 0, (A.TXAMT * ((B.COMMISSIONRATE / 100.0) + (B.COMMISSIONRATE / 100.0 / 10.0))), (B.COMMISSIONAMT + (B.COMMISSIONAMT / 10.0))) AS PGCOMM4SUM,
           B.ETCAMT + (B.ETCAMT / 10.0) AS PGETC4SUM,
           0 AS USESTATE,
           A.ABROADCHK, ISNULL(A.NonSettleAmt, 0) AS NonSettleAmt, A.AllotPeriod,
           0 AS DiscountAmt, 'N' AS DiscountFlag, A.PointAmt, A.MPLTID,
           IIF(A.PGName = 'impaymobile', A.CardCode, '') AS MobileCo,
           A.CardAmt, A.CouponAmt, A.MoneyAmt
      FROM PaymentDB.dbo.TTxMst A
      JOIN SETTLE_POQ_DB.dbo.TPGSettleRate B ON A.YMD = B.YMD AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
      JOIN SETTLE_POQ_DB.dbo.TClientSettleRate C ON A.YMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
      JOIN SETTLE_POQ_DB.dbo.TPGCMRate D ON A.PGNAME = D.PGNAME AND A.MALLID = D.MALLID
     WHERE A.YMD = @p_ymd
       AND D.TAXEXEMPTIONFLAG = 0
       AND A.PLTID >= @p_from AND A.PLTID < @p_to    -- 청크 조건(원본 필터에 AND로 결합, rule 8)

    UNION ALL

    -- 분기2: 부분취소거래건 (USESTATE=2)
    SELECT @p_ymd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID, E.PLTID, E.TID, E.CID,
           A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME, E.TXAMT,
           C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
           IIF(C.COMMISSIONTYPE = 0, (E.TXAMT * (C.COMMISSIONRATE / 100.0)), C.COMMISSIONAMT) AS CLCOMM,
           C.ETCAMT AS CLETC,
           B.COMMISSIONTYPE AS PGCOMMTYPE, B.INCVTAX AS PGINCVTAX,
           IIF(B.COMMISSIONTYPE = 0, (E.TXAMT * (B.COMMISSIONRATE / 100.0)), B.COMMISSIONAMT) AS PGCOMM,
           B.ETCAMT AS PGETC,
           IIF(B.COMMISSIONTYPE = 0, (E.TXAMT * ((B.COMMISSIONRATE / 100.0) + (B.COMMISSIONRATE / 100.0 / 10.0))), (B.COMMISSIONAMT + (B.COMMISSIONAMT / 10.0))) AS PGCOMM4SUM,
           B.ETCAMT + (B.ETCAMT / 10.0) AS PGETC4SUM,
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
     WHERE E.YMD = @p_ymd
       AND D.TAXEXEMPTIONFLAG = 0
       AND E.SettleState = 1
       AND E.PLTID >= @p_from AND E.PLTID < @p_to    -- 청크 조건(원본 필터에 AND로 결합, rule 8)

    UNION ALL

    -- 분기3: 환불건 (USESTATE=3)
    SELECT @p_ymd AS YMD, A.YMD AS AYMD, A.CLIENTID, A.PGNAME, A.MALLID, A.PLTID, A.TID, A.CID,
           A.PAYERID, A.PAYERNAME, A.SERVICENAME, A.PRODUCTNAME, E.REFUNDREQAMT AS TXAMT,
           C.COMMISSIONTYPE AS CLCOMMTYPE, C.INCVTAX AS CLINCVTAX,
           CASE WHEN F.FeeFlag = 'Y' THEN IIF(F.FeeRateType = 3, (E.RefundReqAmt * (F.FeeRate / 100.0)), F.FeeRate) * (-1)
                ELSE IIF(C.CommissionType = 0, (E.RefundReqAmt * (C.CommissionRate / 100.0)), C.CommissionAmt) END AS CLCOMM,
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
     WHERE E.REQYMD = @p_ymd
       AND D.TAXEXEMPTIONFLAG = 0
       AND A.PLTID >= @p_from AND A.PLTID < @p_to    -- 청크 조건(원본 필터에 AND로 결합, rule 8)
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y ON X.PGNAME = Y.PGName;

-- SQL_RESTORE_DELETE - 이 단계가 삭제한 것과 정확히 같은 범위(YMD)만 지운다(rule 4b)
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;

-- SQL_RESTORE_INSERT - 위에서 캡처한 동일 Shadow 전체를 그대로 복원
DECLARE @v_shadow2 NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S04';
DECLARE @v_sql2 NVARCHAR(MAX);
SET @v_sql2 = N'INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst SELECT * FROM ' + @v_shadow2 + N';';
EXEC sp_executesql @v_sql2;
```

- **Shadow 보관 정책(rule 4e)**: `batch_shadow.TSettleMst_<runid>_S04`는 부트스트랩 정리 잡이 생성 후 24시간 경과 시 자동 DROP한다.
- **재시작성**: 이 단계는 원본 파라미터(`@pi_strYMD`) 외에 별도의 재시작용 입력을 추가하지 않는다(rule 5). 오케스트레이터가 `batch.BatchCheckpoint`에서 `S04`가 이미 `Succeeded`이면 재호출을 건너뛴다.