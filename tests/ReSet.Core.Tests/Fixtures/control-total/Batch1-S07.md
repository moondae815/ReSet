### S07 | 기본 수수료 산출 및 부호 반전

`dbo.UP_UTIL_SETTLE_COMM_UPD`를 대체하는 단계로, 원본과 동일하게 `SETTLE_POQ_DB.dbo.TSettleMst` 테이블에 대해 15개의 UPDATE 문(U1~U15)을 **하나의 트랜잭션** 안에서 순차 실행한다. U7은 부분취소 합이 승인금액과 일치하는(`HAVING SUM(TxAmt)=0`) PLTID를 찾는 파생 테이블 K와, Toss/TossPoint 부분취소·취소수수료부과 대상 PLTID를 찾는 파생 테이블 D를 포함하는 다중 조인·집계 문장이라 단일 PK 기준 청킹이 수학적으로 불가능하다. 따라서 본 단계는 `Chunkable: False`이며, 원본 프로시저와 마찬가지로 15개 UPDATE 전체가 **하나의 트랜잭션**으로 완결되므로(공유 컨벤션에 따라 섀도우는 S02~S06, S11~S15에서만 사용) 별도의 섀도우 테이블 없이 트랜잭션 롤백만으로 복구한다(규칙 4 기본 원칙). 모든 원본 `WITH(NOLOCK)` 힌트는 SNAPSHOT 격리 정책과 충돌하므로 전부 제거했다(규칙 10). 이 단계가 호출하는 커넥션/세션은 SNAPSHOT 격리 수준에서 실행되어야 한다.

인터페이스는 원본 파라미터 목록을 그대로 따른다: `@pi_strYMD`는 `batchYmd`로, `@po_intRetVal`은 아래 오류 코드 표에 따라 산출되는 `currentStepErrorCode`(실패 시) 또는 `0`(성공 시)으로 매핑되어 `batch.BatchStepJournal.LegacyReturnCode`에 기록된다.

```pseudocode
// (rule 6-1) 단계 진입 시 NULL로 초기화 - 아직 실패 지점 없음
currentStepErrorCode = NULL

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })

writeStepJournal(runId, "S07", status: "Running", legacyReturnCode: NULL)

beginTransaction()
try:
    currentStepErrorCode = -1
    execute(SQL_U1, { p_batchYmd: batchYmd })

    currentStepErrorCode = -2
    execute(SQL_U2, { p_batchYmd: batchYmd })

    // 원본 라인 87~114 티모넷 정산 예외처리 블록은 주석 처리되어 비활성 상태이므로 이관하지 않는다.
    currentStepErrorCode = -4
    execute(SQL_U3, { p_batchYmd: batchYmd })

    currentStepErrorCode = -5
    execute(SQL_U4, { p_batchYmd: batchYmd })

    currentStepErrorCode = -6
    execute(SQL_U5, { p_batchYmd: batchYmd })

    currentStepErrorCode = -7
    execute(SQL_U6, { p_batchYmd: batchYmd })

    currentStepErrorCode = -8
    execute(SQL_U7, { p_batchYmd: batchYmd })

    currentStepErrorCode = -9
    execute(SQL_U8, { p_batchYmd: batchYmd })

    currentStepErrorCode = -10
    execute(SQL_U9, { p_batchYmd: batchYmd })

    currentStepErrorCode = -11
    execute(SQL_U10, { p_batchYmd: batchYmd })

    currentStepErrorCode = -12
    execute(SQL_U11, { p_batchYmd: batchYmd })

    // 원본 라인 376~384 테스트건(페이레터 관련) 수수료 0원 처리 블록은 주석 처리되어 비활성 상태이므로 이관하지 않는다.
    currentStepErrorCode = -20
    execute(SQL_U12, { p_batchYmd: batchYmd })

    currentStepErrorCode = -21
    execute(SQL_U13, { p_batchYmd: batchYmd })

    currentStepErrorCode = -22
    execute(SQL_U14, { p_batchYmd: batchYmd })

    currentStepErrorCode = -23
    execute(SQL_U15, { p_batchYmd: batchYmd })

    commit()
    writeStepJournal(runId, "S07", status: "Succeeded", legacyReturnCode: 0)
    writeCheckpoint(runId, "S07", status: "Succeeded")
catch failure:
    // 단일 트랜잭션이므로 롤백만으로 TSettleMst는 완전히 복원된다 - 섀도우/보정 DELETE 없음
    rollbackIfOpen()
    writeStepJournal(runId, "S07", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_U1 (원본 오류 코드 -1)
/* U1: 할부이자 적용(ALLTHEGATE, NICECARD) - 할부이자수수료/부가세, PG원가수수료일치 */
UPDATE B
   SET CLINTCOMM = CAST((B.TXAMT-ISNULL(B.NonSettleAmt,0))*(CAST(A.ALLOTPERIOD AS INT)*0.01) AS INT),
       CLVT = dbo.UF_GET_ROUND4VAT((B.CLCOMM + B.CLETC + CAST((B.TXAMT-ISNULL(B.NonSettleAmt,0))*(CAST(A.ALLOTPERIOD AS INT)*0.01) AS INT)) * dbo.UF_GET_INCVTAXRATE(B.CLVTType)),
       PGINTEXPCOMM = CAST(ROUND(IIF(ISNULL(B.DiscountFlag,'N')='Y',B.DiscountAmt,B.TxAmt)*(C.COMMISSIONRATE/100.0),0,dbo.UF_GET_PGCommOption(A.PGNAME,3)) AS INT),
       PGVT = CAST(ROUND( (B.PGCOMM + B.PGETC + CAST(ROUND(IIF(ISNULL(B.DiscountFlag,'N')='Y',B.DiscountAmt,B.TxAmt)*(C.COMMISSIONRATE/100.0),0,dbo.UF_GET_PGCommOption(A.PGNAME,3)) AS INT)) * (10.0/100.0),0,dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst B
  JOIN PaymentDB.dbo.TTxMst A ON A.PLTID = B.PLTID
  JOIN SETTLE_POQ_DB.dbo.TCardAllotInterest C ON A.CARDMEMBERNO = C.CARDMEMBERNO AND A.ALLOTPERIOD = C.ALLOTPERIOD
 WHERE B.YMD = @p_batchYmd
   AND A.CLIENTINTEREST = 1
   AND A.USESTATE = 0
   AND A.PGNAME IN ('ALLTHEGATE','NICECARD');

-- SQL_U2 (원본 오류 코드 -2)
/* U2: 해외카드 수수료 적용(정상건) - 인증료 포함 취소 부가세 보정, unionpay 보정 */
UPDATE A
   SET CLCOMM = CAST( IIF(B.COMMISSIONTYPE=0, ((A.TXAMT-ISNULL(A.NonSettleAmt,0)) * (B.COMMISSIONFOREIGNRATE/100.0)), B.COMMISSIONAMT) AS INT ),
       CLVT = dbo.UF_GET_ROUND4VAT((IIF(B.COMMISSIONTYPE=0, ((A.TXAMT-ISNULL(A.NonSettleAmt,0)) * (B.COMMISSIONFOREIGNRATE/100.0)), B.COMMISSIONAMT) + B.ETCAMT) * dbo.UF_GET_INCVTAXRATE(B.INCVTAX)),
       PGCOMM = CAST( ROUND( IIF(C.COMMISSIONTYPE=0, (IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt) * (C.COMMISSIONFOREIGNRATE/100.0)), C.COMMISSIONAMT), 0, dbo.UF_GET_PGCommOption(A.PGNAME,3)) AS INT ),
       PGVT = CAST( ROUND( ROUND( IIF(C.COMMISSIONTYPE=0, (IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt) * (C.COMMISSIONFOREIGNRATE/100.0)), C.COMMISSIONAMT), 0, dbo.UF_GET_PGCommOption(A.PGNAME,3)) * dbo.UF_GET_INCVTAXRATE(C.INCVTAX), 0, dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT) + CAST( ROUND(C.ETCAMT * dbo.UF_GET_INCVTAXRATE(C.INCVTAX), 0, dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B ON A.YMD = B.YMD AND A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
  JOIN SETTLE_POQ_DB.dbo.TPGSettleRate C ON A.YMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.ABROADCHK = 1
   AND A.PGNAME IN ('ALLTHEGATE','DACOMCARD','UNIONPAY','INICARD','TOSSCARD','NICECARD')
   AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD));

-- SQL_U3 (원본 오류 코드 -4) - 원본 라인 87~114 예외블록(비활성, 활성화 시 -4 반환)과 코드 충돌 없음
/* U3: 취소거래건(USESTATE 1,2,3) 금액/수수료 부호 반전 - 강제취소건 제외 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET TXAMT = TXAMT * (-1),
       CLCOMM = CLCOMM * (-1),
       CLINTCOMM = CLINTCOMM * (-1),
       CLVT = CLVT * (-1),
       CLETC = CLETC * (-1),
       PGCOMM = PGCOMM * (-1),
       PGINTEXPCOMM = PGINTEXPCOMM * (-1),
       PGINTREALCOMM = PGINTREALCOMM * (-1),
       PGVT = PGVT * (-1),
       PGETC = PGETC * (-1),
       NonSettleAmt = NonSettleAmt * (-1),
       DiscountAmt = DiscountAmt * (-1),
       PointAmt = PointAmt * (-1),
       CardAmt = CardAmt * (-1),
       CouponAmt = CouponAmt * (-1),
       MoneyAmt = MoneyAmt * (-1)
 WHERE YMD = @p_batchYmd
   AND USESTATE IN (1,2,3)
   AND PLTID NOT IN (
        SELECT ' ' UNION ALL
        SELECT PLTID FROM PaymentDB.dbo.TCCanceledMst WHERE CYMD = @p_batchYmd
       );

-- SQL_U4 (원본 오류 코드 -5)
/* U4: CheckPay/Toss/TossPoint 전체취소 PG 취소수수료 추가 부과 */
UPDATE A
   SET PGCOMM = A.PGCOMM + C.CommissionCancelAmt,
       PGVT = A.PGVT + CAST(C.CommissionCancelAmt*(0.1) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TPGSettleRate C ON A.AYMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('CheckPay','Toss','TossPoint')
   AND A.USESTATE IN (1)
   AND C.CommissionCancelFlag = 1;

-- SQL_U5 (원본 오류 코드 -6)
/* U5: CheckPay/Toss/TossPoint 전체취소 고객사 취소수수료 부과 */
UPDATE A
   SET CLCOMM = CASE WHEN B.CommissionCancelFlag = 1 THEN A.CLCOMM + B.CommissionCancelAmt ELSE IIF(A.PGNAME IN ('Toss','TossPoint'), 0, A.CLCOMM) END,
       CLVT = CASE WHEN B.CommissionCancelFlag = 1 THEN A.CLVT + dbo.UF_GET_ROUND4VAT(B.CommissionCancelAmt * dbo.UF_GET_INCVTAXRATE(A.CLVTType)) ELSE IIF(A.PGNAME IN ('Toss','TossPoint'), 0, A.CLVT) END
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B ON A.AYMD = B.YMD AND A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('CheckPay','Toss','TossPoint')
   AND A.USESTATE IN (1);

-- SQL_U6 (원본 오류 코드 -7)
/* U6: Toss/TossPoint 부분취소 건 수수료 0 처리 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLComm = 0,
       CLVT = 0,
       PGComm = 0,
       PGVT = 0
 WHERE YMD = @p_batchYmd
   AND PGNAME IN ('Toss','TossPoint')
   AND UseState = 2;

-- SQL_U7 (원본 오류 코드 -8) - 파생 테이블 K(부분취소합=0인 PLTID 집계), D(Toss/TossPoint 부분취소·취소수수료부과 대상 PLTID) 보존
/* U7: 부분취소 합계가 승인금액과 같아지는 건(완전취소) 최종 수수료 재계산 - Toss/TossPoint */
UPDATE X
   SET CLCOMM = (K.CLCOMM*(-1)) + Y.CommissionCancelAmt,
       CLVT = (K.CLVT *(-1)) + dbo.UF_GET_ROUND4VAT(Y.CommissionCancelAmt * dbo.UF_GET_INCVTAXRATE(X.CLVTType)),
       PGCOMM = (K.PGCOMM*(-1)) + Z.CommissionCancelAmt,
       PGVT = (K.PGVT *(-1)) + CAST(Z.CommissionCancelAmt*(0.1) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst X
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate Y ON X.AYMD = Y.YMD AND X.CLIENTID = Y.CLIENTID AND X.PGNAME = Y.PGNAME AND X.MALLID = Y.MALLID
  JOIN SETTLE_POQ_DB.dbo.TPGSettleRate Z ON X.AYMD = Z.YMD AND X.PGNAME = Z.PGNAME AND X.MALLID = Z.MALLID
  JOIN (
        SELECT C.PLTID,
               MAX(C.ID)     AS ID,
               MAX(C.CLComm) AS CLComm,
               MAX(C.CLVT)   AS CLVT,
               MAX(C.PGComm) AS PGComm,
               MAX(C.PGVT)   AS PGVT
          FROM SETTLE_POQ_DB.dbo.TSettleMst C
          JOIN (
                SELECT A.PLTID
                  FROM SETTLE_POQ_DB.dbo.TSettleMst A
                  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B
                    ON A.AYMD = B.YMD AND A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
                 WHERE A.YMD = @p_batchYmd
                   AND A.PGNAME IN ('Toss','TossPoint')
                   AND A.USESTATE IN (2)
                   AND B.CommissionCancelFlag = 1
               ) D ON C.PLTID = D.PLTID
         GROUP BY C.PLTID
        HAVING SUM(C.TxAmt) = 0
       ) K ON X.PLTID = K.PLTID AND X.ID = K.ID;

-- SQL_U8 (원본 오류 코드 -9)
/* U8: 가상계좌(inivacct) 전체취소 취소수수료 추가 부과 - 미부과시 승인수수료 환급으로 변경 */
UPDATE A
   SET CLComm = 0 + IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLComm, 0),
       CLETC = 0 + IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLETC , B.CommissionCancelAmt),
       CLVT = IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLVT , dbo.UF_GET_ROUND4VAT((A.CLComm + B.CommissionCancelAmt)*dbo.UF_GET_INCVTAXRATE(A.CLVTType))),
       PGETC = 0 + IIF(ISNULL(C.CommissionCancelFlag,0)=0, 0, C.CommissionCancelAmt),
       PGVT = 0 + IIF(ISNULL(C.CommissionCancelFlag,0)=0, 0, CAST(C.CommissionCancelAmt*dbo.UF_GET_INCVTAXRATE(C.IncVTax) AS INT))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate C ON A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.USESTATE = 1
   AND A.PGNAME IN ('inivacct');

-- SQL_U9 (원본 오류 코드 -10)
/* U9: 간편계좌이체(easybank) 전체/부분취소 취소수수료 추가 부과 - 미부과시 승인수수료 환급 */
UPDATE A
   SET CLComm = 0 + IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLComm, 0),
       CLETC = 0 + IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLETC , B.CommissionCancelAmt),
       CLVT = 0 + IIF(ISNULL(B.CommissionCancelFlag,0)=0, A.CLVT , dbo.UF_GET_ROUND4VAT(B.CommissionCancelAmt*dbo.UF_GET_INCVTAXRATE(A.CLVTType))),
       PGETC = 0 + IIF(ISNULL(C.CommissionCancelFlag,0)=0, 0, C.CommissionCancelAmt),
       PGVT = 0
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate C ON A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.USESTATE IN (1,2)
   AND A.PGNAME IN ('easybank');

-- SQL_U10 (원본 오류 코드 -11)
/* U10: KFTC/INIBANK 인터넷뱅킹 부분취소 또는 익일이후 전체취소 - 환불수수료 부담 반영 */
UPDATE A
   SET PGCOMM = 0,
       PGVT = 0,
       CLCOMM = IIF(ISNULL(B.RefundFeeType,0)=0, 0, A.CLCOMM),
       CLVT = IIF(ISNULL(B.RefundFeeType,0)=0, 0, A.CLVT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('KFTC','INIBANK')
   AND ((A.USESTATE = 2) OR (A.CYMD > A.AYMD AND A.USESTATE = 1))
   AND B.RefundFlag = 'Y';

-- SQL_U11 (원본 오류 코드 -12)
/* U11: 가상계좌(hectofirm) 취소건 원가-취소수수료 미환급(2023.12.13~) */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET PGComm = 0,
       PGVT = 0
 WHERE YMD = @p_batchYmd
   AND PGNAME IN ('hectofirm')
   AND UseState IN (1);

-- SQL_U12 (원본 오류 코드 -20)
/* U12: TOTAL 수수료 집계 - CLTOTAL, PGTOTAL, POQINCOME */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL = (CLCOMM + CLVT + CLETC + CLINTCOMM),
       PGTOTAL = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)),
       POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM) - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))
 WHERE YMD = @p_batchYmd;

-- SQL_U13 (원본 오류 코드 -21) - @v_valIncVat는 DECIMAL(2,1)=1.1 로컬 변수, CLComm/CLEtc 자기참조 항은 갱신 전 값으로 동시 평가됨
/* U13: 부가세포함(CLVTType=1) 계산방식 - 공급가액/부가세 분리 */
DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;

UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal = (CLComm+CLEtc+CLIntComm),
       CLEtc = 0,
       CLComm = (CAST(CLComm/@v_valIncVat AS INT)+CAST(CLEtc/@v_valIncVat AS INT)+CAST(CLIntComm/@v_valIncVat AS INT)),
       CLVT = (CLComm+CLEtc+CLIntComm) - (CAST(CLComm/@v_valIncVat AS INT)+CAST(CLEtc/@v_valIncVat AS INT)+CAST(CLIntComm/@v_valIncVat AS INT)),
       POQIncome = (CLComm+CLEtc+CLIntComm) - PGTotal
 WHERE YMD = @p_batchYmd
   AND CLVTType = 1
   AND (UseState <> 1 OR (UseState=1 AND YMD=AYMD));

-- SQL_U14 (원본 오류 코드 -22)
/* U14: 분할정산 적용 - 분할정산기준/대상건 */
UPDATE A
   SET SeperateAmt = ROUND(IIF(B.SeperateStandard=1, A.TxAmt, (A.TxAmt-A.CLTotal)) * (B.SeperateRate/100.0),0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
 WHERE B.SeperateFlag = 'Y'
   AND A.YMD = @p_batchYmd
   AND A.UseState = CASE WHEN B.SeperateTarget=2 THEN 0 ELSE A.UseState END
   AND B.USESTATE IN (0,4);

-- SQL_U15 (원본 오류 코드 -23)
/* U15: 외화정산(승인일 기준 환율 적용) - SettleCurrency, ForeignSettleAmt */
UPDATE B
   SET SettleCurrency = A.SettleCurrency,
       ForeignSettleAmt = ROUND((B.TxAmt - B.CLTotal - ISNULL(B.SeperateAmt,0)) / dbo.UF_GET_SETTLE_EXCHANGERATE(B.YMD, B.ClientID), 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst B
  JOIN SETTLE_POQ_DB.dbo.TClientContract A ON A.ClientID = B.ClientID
 WHERE A.SettleYMDType = 2
   AND B.YMD = @p_batchYmd;
```

**재시작/복구 전략**: 본 단계는 오케스트레이터가 `batch.BatchCheckpoint`에서 `S07` 상태가 `Succeeded`가 아닐 때만 호출한다(규칙 5). 모든 UPDATE는 `YMD = @p_batchYmd`(또는 조인 경유 `AYMD/YMD`) 기준으로 값을 재계산하는 멱등 연산이므로, 실패 후 재실행 시 트랜잭션 롤백으로 완전히 복원된 `SETTLE_POQ_DB.dbo.TSettleMst`에 대해 U1~U15가 처음부터 다시 계산되어도 결과가 달라지지 않는다. 실패 시점의 `currentStepErrorCode`(-1, -2, -4, -5, -6, -7, -8, -9, -10, -11, -12, -20, -21, -22, -23 중 하나)는 `batch.BatchStepJournal.LegacyReturnCode`에 기록되어 어느 UPDATE에서 실패했는지 정확히 추적 가능하며, 성공 시에는 `0`이 기록되어 원본 `@po_intRetVal` 성공 규약(호출자 사전 0 초기화)을 그대로 재현한다.