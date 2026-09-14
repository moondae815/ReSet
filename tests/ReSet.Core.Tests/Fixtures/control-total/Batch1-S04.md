### S04 | 당일 취소 원장 반영

**레거시 원본**: `dbo.UP_UTIL_SETTLE_CANCEL_INS` (원본 반환 규약: `=0`→성공(암묵적, 호출자 사전 0 초기화 필요), `<>0`→실패, INSERT 실패 시 명시적으로 `-1`이 대입됨. 원본은 `@@ERROR`/`ROLLBACK`/`RETURN`을 절차 본문에 직접 사용하지만, 본 배치 단계에서는 이 판단을 애플리케이션 코드가 담당하며 SQL 문장 자신은 분기하지 않는다.)

**승인된 오류 코드 목록 대조**: 이 단계에 승인된 오류 코드는 `-1`, `-2`이다. 그러나 원본 프로시저 명세(`dbo.UP_UTIL_SETTLE_CANCEL_INS`)를 전수 검토하면 실제로 정의되는 코드는 INSERT 1 문장 실패 시의 `-1` 뿐이며, 성공 시 암묵적 `0`(호출자가 `@po_intRetVal`을 사전에 0으로 초기화해야 신뢰 가능)이 유일한 정상 코드이다. `-2`는 원본 어디에도 정의되지 않은 코드다. 규칙 9에 따라 원본에 없는 코드를 새로 발명하거나 실패 경로에 대입하지 않으므로, 본 단계는 실제 실패 관측에 `-1`만 사용한다. `-2`는 승인 목록에 존재함을 명시하기 위해 이 문단에 기록할 뿐, `currentStepErrorCode`에는 결코 대입되지 않는다.

**대상 테이블**: `SETTLE_POQ_DB.dbo.TSettleMst` (INSERT 전용 — 원본에 UPDATE/DELETE 문은 존재하지 않는다)

**NOLOCK 제거**: 원본 INSERT 1의 원천 조회는 `PaymentDB.dbo.TTxMst`(별칭 A)와 `SETTLE_POQ_DB.dbo.TSettleMst`(별칭 B) 양쪽에 `WITH(NOLOCK)` 힌트를 사용했다. 본 배치는 SNAPSHOT 격리 하에서 실행되므로 두 힌트를 모두 제거한다.

**청크 전략**: 이 단계는 청크 가능(Chunkable: True)하며 순수 INSERT-only 패턴이므로 규칙 11에 따라 섀도우 테이블을 사용하지 않는다. 청크 키는 조인 키이자 `TSettleMst`/`TTxMst` 양쪽에 실제 존재하는 `PLTID`를 사용한다(규칙 12 대상 스키마 컬럼 존재 확인 완료). 원본 업무 필터(`A.YMDCANCEL = @pi_strYMD`, `B.USESTATE = 0`, `ISNULL(B.CompanySalesType,4) NOT IN (0,1,2,3)`, 조인 `A.PLTID = B.PLTID`)는 청크 범위 조건과 `AND`로 결합하여 그대로 보존한다. 각 청크는 규칙 8-1에 따라 독립 트랜잭션으로 커밋된다.

```pseudocode
// (rule 6-1) 단계 진입 시 초기화
currentStepErrorCode = NULL

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, "S04", status: "Running", legacyReturnCode: NULL)

FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    try:
        // (rule 6-1, rule 9) 원본 INSERT 1의 유일한 오류 코드 -1을 문장 직전에 대입
        currentStepErrorCode = -1
        execute(SQL_INSERT_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
        commit()
    catch failure:
        // (rule 8-1) 실패한 청크만 롤백된다. 이전 커밋된 청크는 그대로 남는다.
        rollbackIfOpen()

        // (rule 11) INSERT-only 단계의 보정: 재시작 시 전체 재적재가 안전하도록,
        // 이 단계가 생성하는 출력 범위(원본 INSERT 1의 대입 상수인 YMD/CYMD/USESTATE 조합)를
        // 정확히 그 범위로만 삭제한다. WHERE 없는 전체 삭제는 절대 하지 않는다.
        beginTransaction()
        execute(SQL_COMPENSATE_DELETE, { p_batchYmd: batchYmd })
        commit()

        writeStepJournal(runId, "S04", status: "Failed", legacyReturnCode: currentStepErrorCode)
        stop the pipeline

commit_final:
    writeStepJournal(runId, "S04", status: "Succeeded", legacyReturnCode: 0)
    writeCheckpoint(runId, "S04", status: "Succeeded")
```

```sql
-- SQL_CHUNK_BOUNDS: 원본 업무 필터(YMDCancel 일치)를 경계 조회에도 그대로 포함한다
SELECT MIN(A.PLTID), MAX(A.PLTID)
  FROM PaymentDB.dbo.TTxMst A
 WHERE A.YMDCancel = @p_batchYmd;

-- SQL_INSERT_CHUNK
/* INSERT 1: 취소 정산 레코드 생성 - 정상건(USESTATE=0)을 기반으로 취소 레코드(USESTATE=1) 복제 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst (
    YMD, AYMD, CYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
    PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME, TXAMT,
    CLCOMMTYPE, CLVTTYPE, CLCOMM, CLVT, CLETC,
    PGCOMMTYPE, PGVTTYPE, PGCOMM, PGVT, PGETC,
    CLTOTAL, PGTOTAL, POQINCOME,
    USESTATE, INSTATE, OUTSTATE,
    CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM, ABROADCHK,
    NonSettleAmt, AllotPeriod, DiscountAmt, DiscountFlag, PointAmt,
    MPLTID, MobileCo, CardAmt, CouponAmt, MoneyAmt
)
SELECT
    @p_batchYmd, A.YMD, A.YMDCancel, B.CLIENTID, B.PGNAME, B.MALLID, B.PLTID, B.TID, B.CID,
    B.PAYERID, B.PAYERNAME, B.SERVICENAME, B.PRODUCTNAME, B.TXAMT,
    B.CLCOMMTYPE, B.CLVTTYPE, B.CLCOMM, B.CLVT, B.CLETC,
    B.PGCOMMTYPE, B.PGVTTYPE, B.PGCOMM, B.PGVT, B.PGETC,
    B.CLTOTAL, B.PGTOTAL, B.POQINCOME,
    1, 0, 0,
    ISNULL(B.CLINTCOMM, 0), ISNULL(B.PGINTEXPCOMM, 0), ISNULL(B.PGINTREALCOMM, 0), B.ABROADCHK,
    ISNULL(A.NonSettleAmt, 0), B.AllotPeriod, B.DiscountAmt, B.DiscountFlag, B.PointAmt,
    B.MPLTID, B.MobileCo, B.CardAmt, B.CouponAmt, B.MoneyAmt
  FROM PaymentDB.dbo.TTxMst A
  JOIN SETTLE_POQ_DB.dbo.TSettleMst B ON A.PLTID = B.PLTID
 WHERE A.YMDCancel = @p_batchYmd            -- 원본 업무 필터 보존
   AND B.USESTATE = 0                        -- 원본 업무 필터 보존
   AND ISNULL(B.CompanySalesType, 4) NOT IN (0, 1, 2, 3)  -- 원본 업무 필터 보존
   AND A.PLTID >= @p_from AND A.PLTID < @p_to;            -- 청크 범위 조건

-- SQL_COMPENSATE_DELETE: 이 단계가 원래 생성하는 출력 범위만 정확히 삭제(전체 테이블 삭제 금지)
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_batchYmd
   AND CYMD = @p_batchYmd
   AND USESTATE = 1;

-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';
```

**출력 파라미터 매핑(규칙 13)**: 원본 `@po_intRetVal`(성공 시 암묵적 0 — 호출자 사전 초기화 필요, 실패 시 `-1`)은 본 단계의 `currentStepErrorCode` 및 `batch.BatchStepJournal.LegacyReturnCode`로 대체 매핑된다. 성공 종료 시 저널에는 명시적으로 `0`을 기록하여, 원본의 "호출자가 사전 초기화해야 신뢰 가능"이라는 계약상 모순을 배치 계층에서 해소한다.

**격리 수준**: 본 단계의 모든 트랜잭션은 SNAPSHOT 격리 수준 하에서 실행되어야 한다(설정 위치·방식은 이 문서가 규정하지 않는다).