### S03 | PG/가맹점 정산요율 스냅샷 생성

**레거시 원본**: `dbo.UP_Util_PG_Client_CMRate_Ins`
**파라미터 매핑(rule 5-2)**: `@pi_strYMD CHAR(8) -> p_ymd` (출력 파라미터 `@po_intRetVal INT`는 바인딩 대상이 아니며, 본 스텝 종료 시 `batch.BatchStepJournal.LegacyReturnCode`에 최종 값이 기록된다 — rule 13)
**대상 테이블**: `SETTLE_POQ_DB.dbo.TPGSettleRate`, `SETTLE_POQ_DB.dbo.TClientSettleRate`, `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra`, `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra`, `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo`
**격리 수준**: 본 스텝의 모든 SELECT/DML은 SNAPSHOT 격리 수준 하에서 실행되어야 한다(원본의 `NOLOCK` 힌트는 rule 10에 따라 전부 제거한다).
**청크 여부**: 5개 DELETE→INSERT 쌍은 각각 GROUP BY 없는 단순 재적재이지만, 5개 테이블 모두를 하나의 논리적 스냅샷 단위로 취급해야 하므로(원본이 하나의 트랜잭션으로 묶어 처리) **Single-Transaction 방식**을 사용한다. 청크 키를 인위적으로 만들지 않는다.
**섀도우 사용 여부**: 사용하지 않는다. 5개 DELETE-INSERT 쌍이 전부 하나의 트랜잭션 안에서 실행되므로 실패 시 롤백만으로 원상복구가 완료된다(rule 4).
**사전 가드(rule 5)**: `TSettleMst`에 정산기준일 기준 기지급 내역(`OutState IN (1,5)` AND `OutYMD IS NOT NULL`)이 존재하는지 확인하는 사전 점검은 트랜잭션 시작 여부와 무관하게 매 호출마다 무조건 실행된다.
**오류 코드 중복 보존**: 원본은 사전 점검 실패와 `DELETE 5` 실패에 동일하게 `-9`를 사용한다. 이는 원본의 결함이지만 rule 9에 따라 그대로 보존하며 재매핑하지 않는다.

```pseudocode
// 공통 진입부(문서 상단 공통 SQL 오류 추적 패턴 참조)
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch8", p_ymd: batchYmd })
currentStepErrorCode = NULL
insertStepJournalRunning(runId, "S03")

TRY:
    // 사전 가드는 트랜잭션 시작 전, 매 호출마다 무조건 실행 (rule 5)
    settledExists = queryScalar(SQL_PRECHECK_SETTLED_EXISTS, { p_ymd: batchYmd })
    IF settledExists == 1:
        currentStepErrorCode = -9
        writeStepJournal(runId, "S03", status: "Failed", LegacyReturnCode: currentStepErrorCode)
        stop the pipeline

    beginTransaction()

    currentStepErrorCode = -1
    execute(SQL_DELETE1_TPGSETTLERATE, { p_ymd: batchYmd })
    currentStepErrorCode = -2
    execute(SQL_INSERT1_TPGSETTLERATE, { p_ymd: batchYmd })

    currentStepErrorCode = -3
    execute(SQL_DELETE2_TCLIENTSETTLERATE, { p_ymd: batchYmd })
    currentStepErrorCode = -4
    execute(SQL_INSERT2_TCLIENTSETTLERATE, { p_ymd: batchYmd })

    currentStepErrorCode = -5
    execute(SQL_DELETE3_TPGSETTLERATE4EXTRA, { p_ymd: batchYmd })
    currentStepErrorCode = -6
    execute(SQL_INSERT3_TPGSETTLERATE4EXTRA, { p_ymd: batchYmd })

    currentStepErrorCode = -7
    execute(SQL_DELETE4_TCLIENTSETTLERATE4EXTRA, { p_ymd: batchYmd })
    currentStepErrorCode = -8
    execute(SQL_INSERT4_TCLIENTSETTLERATE4EXTRA, { p_ymd: batchYmd })

    currentStepErrorCode = -9
    execute(SQL_DELETE5_TCLIENTSETTLERATE4MOBILECO, { p_ymd: batchYmd })
    currentStepErrorCode = -10
    execute(SQL_INSERT5_TCLIENTSETTLERATE4MOBILECO, { p_ymd: batchYmd })

    commit()
    writeStepJournal(runId, "S03", status: "Succeeded", LegacyReturnCode: NULL)
CATCH failure:
    // 단일 트랜잭션이므로 롤백만으로 5개 테이블 모두 원상복구된다. 섀도우/보정 DELETE 불필요(rule 4).
    rollbackIfOpen()
    writeStepJournal(runId, "S03", status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_PRECHECK_SETTLED_EXISTS
-- 원본 IF 1: 기지급(선행 정산 완료) 내역 존재 여부 사전 점검. 무조건 실행되는 가드(rule 5).
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
       AND OutState IN (1, 5)
       AND OutYMD IS NOT NULL
) THEN 1 ELSE 0 END;

-- SQL_DELETE1_TPGSETTLERATE
/* DELETE 1: TPGSETTLERATE 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate WHERE YMD = @p_ymd;

-- SQL_INSERT1_TPGSETTLERATE
/* INSERT 1: TPGSETTLERATE 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate
    (YMD, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
     COMMISSIONFOREIGNRATE, COMMISSIONAMT, ETCAMT, COMMISSIONCANCELFLAG,
     COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose, CollectPeriodID, ETCAmtNH)
SELECT @p_ymd, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
       COMMISSIONFOREIGNRATE, COMMISSIONAMT, ETCAMT, COMMISSIONCANCELFLAG,
       COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose, CollectPeriodID, ETCAmtNH
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0;

-- SQL_DELETE2_TCLIENTSETTLERATE
/* DELETE 2: TCLIENTSETTLERATE 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate WHERE YMD = @p_ymd;

-- SQL_INSERT2_TCLIENTSETTLERATE
/* INSERT 2: TCLIENTSETTLERATE 등록 (UNION ALL 활성계약/해지계약, 두 브랜치 컬럼 목록 동일 - rule 7-1) */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate
    (YMD, CLIENTID, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
     COMMISSIONFOREIGNRATE, COMMISSIONAMT, MINCOMMISSIONAMT, ETCAMT, SETTLEPERIOD,
     UNCOLLECTIMPOSE, USESTATE, COMMISSIONCANCELFLAG, COMMISSIONCANCELAMT, SettlePeriodDD,
     SettleCurrency, SettleYMDType, SettleBasicSeq, ModifyType, ModifyCommType,
     ModifyCommRate, ModifyCommAmt, PartnerCommType, PartnerCommRate, PartnerCommAmt,
     PartnerMinCommAmt, PartnerCommCancelFlag, PartnerCommCancelAmt, RefundFlag,
     RefundFeeType, RefundFee, AuthSettleType, MinimumUnitCnt, SeperateFlag,
     SeperateStandard, SeperateType, SeperateRate, SeperateTarget, SettlePeriodFlag,
     TaxFGBill, SettlePeriodID)
SELECT @p_ymd, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION, B.INCVTAX, B.COMMISSIONTYPE,
       B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT, B.MINCOMMISSIONAMT,
       B.ETCAMT, B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE, B.COMMISSIONCANCELFLAG,
       B.COMMISSIONCANCELAMT, B.SettlePeriodDD, A.SettleCurrency, A.SettleYMDType,
       A.SettleBasicSeq, A.ModifyType, A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt,
       B.PartnerCommType, B.PartnerCommRate, B.PartnerCommAmt, B.PartnerMinCommAmt,
       B.PartnerCommCancelFlag, B.PartnerCommCancelAmt, B.RefundFlag, B.RefundFeeType,
       B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt, B.SeperateFlag, B.SeperateStandard,
       B.SeperateType, B.SeperateRate, B.SeperateTarget, B.SettlePeriodFlag, A.TaxFGBill,
       B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID
 WHERE A.USESTATE IN (0, 4, 5, 6)
   AND B.USESTATE IN (0, 4)
UNION ALL
SELECT @p_ymd, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION, B.INCVTAX, B.COMMISSIONTYPE,
       B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT, B.MINCOMMISSIONAMT,
       B.ETCAMT, B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE, B.COMMISSIONCANCELFLAG,
       B.COMMISSIONCANCELAMT, B.SettlePeriodDD, A.SettleCurrency, A.SettleYMDType,
       A.SettleBasicSeq, A.ModifyType, A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt,
       B.PartnerCommType, B.PartnerCommRate, B.PartnerCommAmt, B.PartnerMinCommAmt,
       B.PartnerCommCancelFlag, B.PartnerCommCancelAmt, B.RefundFlag, B.RefundFeeType,
       B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt, B.SeperateFlag, B.SeperateStandard,
       B.SeperateType, B.SeperateRate, B.SeperateTarget, B.SettlePeriodFlag, A.TaxFGBill,
       B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID
 WHERE B.USESTATE = 5
   AND (A.ContractCancelYMD = @p_ymd OR B.ContractCancelYMD = @p_ymd);

-- SQL_DELETE3_TPGSETTLERATE4EXTRA
/* DELETE 3: TPGSettleRate4Extra 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra WHERE YMD = @p_ymd;

-- SQL_INSERT3_TPGSETTLERATE4EXTRA
/* INSERT 3: TPGSettleRate4Extra 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
    (YMD, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3)
SELECT @p_ymd, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard');

-- SQL_DELETE4_TCLIENTSETTLERATE4EXTRA
/* DELETE 4: TClientSettleRate4Extra 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra WHERE YMD = @p_ymd;

-- SQL_INSERT4_TCLIENTSETTLERATE4EXTRA
/* INSERT 4: TClientSettleRate4Extra 등록 (UNION ALL 활성계약/해지계약, 두 브랜치 컬럼 목록 동일 - rule 7-1) */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
    (YMD, ClientID, PGName, MallID, SettlePeriod, CompanySalesType, ExtraSettleFlag,
     incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3,
     PartnerCommRate, PartnerCommRate0, PartnerCommRate1, PartnerCommRate2, PartnerCommRate3,
     ExtraCommFlag, ExtraCommTarget, SettlePeriodID)
SELECT @p_ymd, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod, 0),
       C.CompanySalesType, C.ExtraSettleFlag, B.incVTax, B.CommissionRate,
       D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3,
       ISNULL(B.PartnerCommRate, 0), D.PartnerCommRate0, D.PartnerCommRate1,
       D.PartnerCommRate2, D.PartnerCommRate3, B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID
  JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra D
    ON B.ClientID = D.ClientID AND B.PGName = D.PGName AND B.MallID = D.MallID
 WHERE A.UseState IN (0, 4, 5, 6)
   AND B.UseState IN (0, 4)
UNION ALL
SELECT @p_ymd, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod, 0),
       C.CompanySalesType, C.ExtraSettleFlag, B.incVTax, B.CommissionRate,
       D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3,
       ISNULL(B.PartnerCommRate, 0), D.PartnerCommRate0, D.PartnerCommRate1,
       D.PartnerCommRate2, D.PartnerCommRate3, B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID
  JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra D
    ON B.ClientID = D.ClientID AND B.PGName = D.PGName AND B.MallID = D.MallID
 WHERE B.USESTATE = 5
   AND B.ContractCancelYMD = @p_ymd;

-- SQL_DELETE5_TCLIENTSETTLERATE4MOBILECO
/* DELETE 5: TClientSettleRate4MobileCo 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo WHERE YMD = @p_ymd;

-- SQL_INSERT5_TCLIENTSETTLERATE4MOBILECO
/* INSERT 5: TClientSettleRate4MobileCo 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
    (YMD, ClientID, PGName, MallID, MobileCoCommApply, MobileCo1, MobileCo2, MobileCo3, MobileCo4, MobileCo5, MobileCo6)
SELECT @p_ymd, A.ClientID, A.PGName, A.MallID, A.MobileCoCommApply,
       B.MobileCo1, B.MobileCo2, B.MobileCo3, B.MobileCo4, B.MobileCo5, B.MobileCo6
  FROM SETTLE_POQ_DB.dbo.TClientCMRate A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo B
    ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID;
```