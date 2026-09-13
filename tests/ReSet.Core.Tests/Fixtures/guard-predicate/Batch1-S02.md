### S02 | PG/클라이언트 수수료율 스냅샷 적재

#### 개요

레거시 `dbo.UP_Util_PG_Client_CMRate_Ins`를 대체한다. 인터페이스는 원본과 동일하게 `@pi_strYMD CHAR(8)`, `@po_intRetVal INT` 두 개뿐이며, 재시작/스킵을 위한 파라미터는 추가하지 않는다(규칙 5). 원본의 기정산 예외 가드(`TSettleMst`에 `OutState IN (1,5)` 및 `OutYMD IS NOT NULL`인 행 존재 여부)는 매 호출마다 무조건 실행한다. 모든 원본 소스 조회에 붙어 있던 `WITH(NOLOCK)`는 전부 제거하며, 이 단계의 모든 문장은 SNAPSHOT 격리 하에서 실행되어야 한다(규칙 10, 4).

원본은 10개 DML(5개 DELETE-INSERT 쌍)을 하나의 `BEGIN TRAN`으로 묶었지만, 대상 테이블 규모 차이 때문에 쌍마다 재구축 전략을 달리한다.

- **DELETE1/INSERT1** (`TPGSettleRate` ← `TPGCMRate`, 최상위 술어는 `USESTATE`뿐), **DELETE3/INSERT3** (`TPGSettleRate4Extra` ← `TPGCMRate`, 최상위 술어는 `USESTATE, PGName`뿐) — 명세서 DML 범위 표에 CLIENTID류의 순회 가능한 키가 전혀 없고, `USESTATE`(사실상 상수 0)와 `PGName`(4개 리터럴)은 청크 경계로 쓸 범위형 컬럼이 아니다(규칙 12). 존재하지 않는 컬럼을 술어에 추가할 수 없으므로 이 두 쌍은 청크로 나누지 않고 **단일 트랜잭션**으로 처리한다(규칙 6 — 청크 불가 시 가짜 키 금지, 규칙 4 기본 원칙 — 단일 트랜잭션은 섀도우 불필요).
- **DELETE2/INSERT2** (`TClientSettleRate`), **DELETE4/INSERT4** (`TClientSettleRate4Extra`), **DELETE5/INSERT5** (`TClientSettleRate4MobileCo`) — 각 문장의 최상위 술어/조인 키 컬럼 목록에 `CLIENTID`(또는 `ClientID`)가 실제로 포함되어 있으므로 이 컬럼을 청크 키로 사용한다. DELETE는 `YMD` 단일 값 삭제라 청크가 불필요하므로 트랜잭션 밖에서 섀도우를 캡처한 뒤 한 번에 삭제하고, INSERT만 청크 단위로 커밋한다(공유 컨벤션의 Shadow Table 패턴).

5개 쌍은 원본 순서(DELETE1→INSERT1→…→DELETE5→INSERT5)를 그대로 유지한다. 앞 쌍이 커밋된 뒤 뒤 쌍이 실패해도 앞 쌍은 되돌리지 않는다 — 각 쌍은 `YMD`(또는 `ClientID` 범위) 기준 delete-then-insert 구조라 재실행해도 자연히 멱등이므로, 재시작 시 체크포인트가 이 단계를 다시 호출하면 처음부터 다시 수행해도 결과가 동일하다(규칙 5).

#### 단계별 처리 의사코드

```pseudocode
currentStepErrorCode = NULL
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, "S02", status: "Running", legacyReturnCode: NULL)

// 기정산 예외 가드 - 무조건 실행 (규칙 5)
currentStepErrorCode = -9
preSettled = queryScalar(SQL_CHECK_PRESETTLED, { p_batchYmd: batchYmd })
IF preSettled IS NOT NULL:
    writeStepJournal(runId, "S02", status: "Failed", legacyReturnCode: -9)
    stop the pipeline

// --- 쌍1: TPGSettleRate (단일 트랜잭션, 섀도우 없음) ---
beginTransaction()
currentStepErrorCode = -1
execute(SQL_DELETE1, { p_batchYmd: batchYmd })
currentStepErrorCode = -2
execute(SQL_INSERT1, { p_batchYmd: batchYmd })
commit()

// --- 쌍2: TClientSettleRate (섀도우 + 청크 INSERT) ---
execute(SQL_CREATE_AND_CAPTURE_SHADOW_TCSR, { p_runId: runId, p_batchYmd: batchYmd })
beginTransaction()
currentStepErrorCode = -3
execute(SQL_DELETE2_RANGE, { p_batchYmd: batchYmd })
commit()
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS2, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = -4
    execute(SQL_INSERT2_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()

// --- 쌍3: TPGSettleRate4Extra (단일 트랜잭션, 섀도우 없음) ---
beginTransaction()
currentStepErrorCode = -5
execute(SQL_DELETE3, { p_batchYmd: batchYmd })
currentStepErrorCode = -6
execute(SQL_INSERT3, { p_batchYmd: batchYmd })
commit()

// --- 쌍4: TClientSettleRate4Extra (섀도우 + 청크 INSERT) ---
execute(SQL_CREATE_AND_CAPTURE_SHADOW_TCSR4E, { p_runId: runId, p_batchYmd: batchYmd })
beginTransaction()
currentStepErrorCode = -7
execute(SQL_DELETE4_RANGE, { p_batchYmd: batchYmd })
commit()
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS4, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = -8
    execute(SQL_INSERT4_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()

// --- 쌍5: TClientSettleRate4MobileCo (섀도우 + 청크 INSERT) ---
execute(SQL_CREATE_AND_CAPTURE_SHADOW_TCSR4M, { p_runId: runId, p_batchYmd: batchYmd })
beginTransaction()
currentStepErrorCode = -9
execute(SQL_DELETE5_RANGE, { p_batchYmd: batchYmd })
commit()
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS5, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = -10
    execute(SQL_INSERT5_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()

writeStepJournal(runId, "S02", status: "Succeeded", legacyReturnCode: 0)
writeCheckpoint(runId, "S02", status: "Succeeded")
```

#### SQL — 기정산 예외 가드

```sql
-- SQL_CHECK_PRESETTLED (원본 IF 1, 오류코드 -9)
SELECT TOP 1 PLTID
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_batchYmd
   AND OutState IN (1, 5)
   AND OutYMD IS NOT NULL;
```

#### SQL — 쌍1: `SETTLE_POQ_DB.dbo.TPGSettleRate` (단일 트랜잭션)

```sql
-- SQL_DELETE1 (DELETE 1, 오류코드 -1)
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_batchYmd;

-- SQL_INSERT1 (INSERT 1, 오류코드 -2)
/* INSERT1: PG 계약정보 → TPGSettleRate 스냅샷, USESTATE = 0(정상)만 대상 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate
      (YMD, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
       COMMISSIONFOREIGNRATE, COMMISSIONAMT, ETCAMT, COMMISSIONCANCELFLAG,
       COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose, CollectPeriodID, ETCAmtNH)
SELECT @p_batchYmd, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
       COMMISSIONFOREIGNRATE, COMMISSIONAMT, ETCAMT, COMMISSIONCANCELFLAG,
       COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose, CollectPeriodID, ETCAmtNH
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0;
```

#### SQL — 쌍2: `SETTLE_POQ_DB.dbo.TClientSettleRate` (섀도우 + 청크 INSERT, 청크 키 `CLIENTID`)

```sql
-- SQL_CREATE_AND_CAPTURE_SHADOW_TCSR
DECLARE @v_shadow2 NVARCHAR(300) =
    N'batch_shadow.TClientSettleRate_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S02';
DECLARE @v_sql2 NVARCHAR(MAX);
SET @v_sql2 = N'SELECT * INTO ' + @v_shadow2 + N' FROM SETTLE_POQ_DB.dbo.TClientSettleRate WHERE 1 = 0;';
EXEC sp_executesql @v_sql2;
SET @v_sql2 = N'INSERT INTO ' + @v_shadow2 + N' SELECT * FROM SETTLE_POQ_DB.dbo.TClientSettleRate WHERE YMD = @p_batchYmd;';
EXEC sp_executesql @v_sql2, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;

-- SQL_DELETE2_RANGE (DELETE 2, 오류코드 -3) - YMD 단위 1회 삭제, 청크 없음
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_batchYmd;

-- SQL_CHUNK_BOUNDS2 - 원본 두 분기의 술어를 OR로 포함해 경계를 산출
SELECT MIN(A.CLIENTID), MAX(A.CLIENTID)
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID
 WHERE (A.USESTATE IN (0,4,5,6) AND B.USESTATE IN (0,4))
    OR (B.USESTATE = 5 AND (A.ContractCancelYMD = @p_batchYmd OR B.ContractCancelYMD = @p_batchYmd));

-- SQL_INSERT2_CHUNK (INSERT 2, 오류코드 -4)
/* INSERT2: TClientContract A JOIN TClientCMRate B UNION ALL 정상/계약해지 분기, 청크 키 A.CLIENTID */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate
      (YMD, CLIENTID, PGNAME, MALLID, VERSION, INCVTAX, COMMISSIONTYPE, COMMISSIONRATE,
       COMMISSIONFOREIGNRATE, COMMISSIONAMT, MINCOMMISSIONAMT, ETCAMT, SETTLEPERIOD,
       UNCOLLECTIMPOSE, USESTATE, COMMISSIONCANCELFLAG, COMMISSIONCANCELAMT, SettlePeriodDD,
       SettleCurrency, SettleYMDType, SettleBasicSeq, ModifyType, ModifyCommType, ModifyCommRate,
       ModifyCommAmt, PartnerCommType, PartnerCommRate, PartnerCommAmt, PartnerMinCommAmt,
       PartnerCommCancelFlag, PartnerCommCancelAmt, RefundFlag, RefundFeeType, RefundFee,
       AuthSettleType, MinimumUnitCnt, SeperateFlag, SeperateStandard, SeperateType,
       SeperateRate, SeperateTarget, SettlePeriodFlag, TaxFGBill, SettlePeriodID)
SELECT @p_batchYmd, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION, B.INCVTAX, B.COMMISSIONTYPE,
       B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT, B.MINCOMMISSIONAMT, B.ETCAMT,
       B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE, B.COMMISSIONCANCELFLAG, B.COMMISSIONCANCELAMT,
       B.SettlePeriodDD, A.SettleCurrency, A.SettleYMDType, A.SettleBasicSeq, A.ModifyType,
       A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt, B.PartnerCommType, B.PartnerCommRate,
       B.PartnerCommAmt, B.PartnerMinCommAmt, B.PartnerCommCancelFlag, B.PartnerCommCancelAmt,
       B.RefundFlag, B.RefundFeeType, B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt, B.SeperateFlag,
       B.SeperateStandard, B.SeperateType, B.SeperateRate, B.SeperateTarget, B.SettlePeriodFlag,
       A.TaxFGBill, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID
 WHERE A.USESTATE IN (0,4,5,6) AND B.USESTATE IN (0,4)
   AND A.CLIENTID >= @p_from AND A.CLIENTID < @p_to
UNION ALL
SELECT @p_batchYmd, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION, B.INCVTAX, B.COMMISSIONTYPE,
       B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT, B.MINCOMMISSIONAMT, B.ETCAMT,
       B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE, B.COMMISSIONCANCELFLAG, B.COMMISSIONCANCELAMT,
       B.SettlePeriodDD, A.SettleCurrency, A.SettleYMDType, A.SettleBasicSeq, A.ModifyType,
       A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt, B.PartnerCommType, B.PartnerCommRate,
       B.PartnerCommAmt, B.PartnerMinCommAmt, B.PartnerCommCancelFlag, B.PartnerCommCancelAmt,
       B.RefundFlag, B.RefundFeeType, B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt, B.SeperateFlag,
       B.SeperateStandard, B.SeperateType, B.SeperateRate, B.SeperateTarget, B.SettlePeriodFlag,
       A.TaxFGBill, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.CLIENTID = B.CLIENTID
 WHERE B.USESTATE = 5
   AND (A.ContractCancelYMD = @p_batchYmd OR B.ContractCancelYMD = @p_batchYmd)
   AND A.CLIENTID >= @p_from AND A.CLIENTID < @p_to;
```

#### SQL — 쌍3: `SETTLE_POQ_DB.dbo.TPGSettleRate4Extra` (단일 트랜잭션)

```sql
-- SQL_DELETE3 (DELETE 3, 오류코드 -5)
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_batchYmd;

-- SQL_INSERT3 (INSERT 3, 오류코드 -6)
/* INSERT3: PG사 영중소 차액정산, USESTATE = 0 AND PGName IN 4대 PG */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
      (YMD, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3)
SELECT @p_batchYmd, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard');
```

#### SQL — 쌍4: `SETTLE_POQ_DB.dbo.TClientSettleRate4Extra` (섀도우 + 청크 INSERT, 청크 키 `ClientID`)

```sql
-- SQL_CREATE_AND_CAPTURE_SHADOW_TCSR4E
DECLARE @v_shadow4 NVARCHAR(300) =
    N'batch_shadow.TClientSettleRate4Extra_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S02';
DECLARE @v_sql4 NVARCHAR(MAX);
SET @v_sql4 = N'SELECT * INTO ' + @v_shadow4 + N' FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra WHERE 1 = 0;';
EXEC sp_executesql @v_sql4;
SET @v_sql4 = N'INSERT INTO ' + @v_shadow4 + N' SELECT * FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra WHERE YMD = @p_batchYmd;';
EXEC sp_executesql @v_sql4, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;

-- SQL_DELETE4_RANGE (DELETE 4, 오류코드 -7)
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_batchYmd;

-- SQL_CHUNK_BOUNDS4
SELECT MIN(A.ClientID), MAX(A.ClientID)
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra D
          ON B.ClientID = D.ClientID AND B.PGName = D.PGName AND B.MallID = D.MallID
 WHERE (A.UseState IN (0,4,5,6) AND B.UseState IN (0,4))
    OR (B.USESTATE = 5 AND B.ContractCancelYMD = @p_batchYmd);

-- SQL_INSERT4_CHUNK (INSERT 4, 오류코드 -8)
/* INSERT4: TClientContract A, TClientCMRate B, TClient C, TClientCMRate4Extra D UNION ALL 분기, 청크 키 A.ClientID */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
      (YMD, ClientID, PGName, MallID, SettlePeriod, CompanySalesType, ExtraSettleFlag, incVTax,
       CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3, PartnerCommRate,
       PartnerCommRate0, PartnerCommRate1, PartnerCommRate2, PartnerCommRate3,
       ExtraCommFlag, ExtraCommTarget, SettlePeriodID)
SELECT @p_batchYmd, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod,0), C.CompanySalesType,
       C.ExtraSettleFlag, B.incVTax, B.CommissionRate, D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3,
       ISNULL(B.PartnerCommRate,0), D.PartnerCommRate0, D.PartnerCommRate1, D.PartnerCommRate2,
       D.PartnerCommRate3, B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra D
          ON B.ClientID = D.ClientID AND B.PGName = D.PGName AND B.MallID = D.MallID
 WHERE A.UseState IN (0,4,5,6) AND B.UseState IN (0,4)
   AND A.ClientID >= @p_from AND A.ClientID < @p_to
UNION ALL
SELECT @p_batchYmd, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod,0), C.CompanySalesType,
       C.ExtraSettleFlag, B.incVTax, B.CommissionRate, D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3,
       ISNULL(B.PartnerCommRate,0), D.PartnerCommRate0, D.PartnerCommRate1, D.PartnerCommRate2,
       D.PartnerCommRate3, B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
  FROM SETTLE_POQ_DB.dbo.TClientContract A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate B ON A.ClientID = B.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate4Extra D
          ON B.ClientID = D.ClientID AND B.PGName = D.PGName AND B.MallID = D.MallID
 WHERE B.USESTATE = 5
   AND B.ContractCancelYMD = @p_batchYmd
   AND A.ClientID >= @p_from AND A.ClientID < @p_to;
```

#### SQL — 쌍5: `SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo` (섀도우 + 청크 INSERT, 청크 키 `ClientID`)

```sql
-- SQL_CREATE_AND_CAPTURE_SHADOW_TCSR4M
DECLARE @v_shadow5 NVARCHAR(300) =
    N'batch_shadow.TClientSettleRate4MobileCo_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S02';
DECLARE @v_sql5 NVARCHAR(MAX);
SET @v_sql5 = N'SELECT * INTO ' + @v_shadow5 + N' FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo WHERE 1 = 0;';
EXEC sp_executesql @v_sql5;
SET @v_sql5 = N'INSERT INTO ' + @v_shadow5 + N' SELECT * FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo WHERE YMD = @p_batchYmd;';
EXEC sp_executesql @v_sql5, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;

-- SQL_DELETE5_RANGE (DELETE 5, 오류코드 -9)
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_batchYmd;

-- SQL_CHUNK_BOUNDS5
SELECT MIN(A.ClientID), MAX(A.ClientID)
  FROM SETTLE_POQ_DB.dbo.TClientCMRate A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo B
          ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID;

-- SQL_INSERT5_CHUNK (INSERT 5, 오류코드 -10)
/* INSERT5: TClientCMRate A JOIN TClientCMRate4MobileCo B, 청크 키 A.ClientID */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
      (YMD, ClientID, PGName, MallID, MobileCoCommApply, MobileCo1, MobileCo2, MobileCo3, MobileCo4, MobileCo5, MobileCo6)
SELECT @p_batchYmd, A.ClientID, A.PGName, A.MallID, A.MobileCoCommApply,
       B.MobileCo1, B.MobileCo2, B.MobileCo3, B.MobileCo4, B.MobileCo5, B.MobileCo6
  FROM SETTLE_POQ_DB.dbo.TClientCMRate A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo B
          ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
 WHERE A.ClientID >= @p_from AND A.ClientID < @p_to;
```

#### 실패 시 복구 전략

```pseudocode
ON FAILURE observed by the application:
    rollbackIfOpen()   // 현재 열려 있던 청크(또는 단일) 트랜잭션만 롤백됨

    // 쌍1, 쌍3은 단일 트랜잭션이므로 롤백만으로 완전 복원 - 섀도우/보정 없음
    // 쌍2, 쌍4, 쌍5 중 이미 DELETE_RANGE가 커밋된 상태에서 실패했다면 섀도우로 복원
    IF shadow2Captured:
        beginTransaction()
        execute(SQL_RESTORE2_DELETE, { p_batchYmd: batchYmd })
        execute(SQL_RESTORE2_INSERT)   // 동일하게 조립된 @v_shadow2 이름 사용
        commit()
    IF shadow4Captured:
        beginTransaction()
        execute(SQL_RESTORE4_DELETE, { p_batchYmd: batchYmd })
        execute(SQL_RESTORE4_INSERT)
        commit()
    IF shadow5Captured:
        beginTransaction()
        execute(SQL_RESTORE5_DELETE, { p_batchYmd: batchYmd })
        execute(SQL_RESTORE5_INSERT)
        commit()

    writeStepJournal(runId, "S02", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_RESTORE2_DELETE / SQL_RESTORE4_DELETE / SQL_RESTORE5_DELETE (범위 삭제 후 복원 - 동일 YMD만)
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate WHERE YMD = @p_batchYmd;
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra WHERE YMD = @p_batchYmd;
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo WHERE YMD = @p_batchYmd;

-- SQL_RESTORE2_INSERT / SQL_RESTORE4_INSERT / SQL_RESTORE5_INSERT (섀도우 이름 조립 후 복원)
SET @v_sql2 = N'INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate SELECT * FROM ' + @v_shadow2 + N';';
EXEC sp_executesql @v_sql2;
SET @v_sql4 = N'INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4Extra SELECT * FROM ' + @v_shadow4 + N';';
EXEC sp_executesql @v_sql4;
SET @v_sql5 = N'INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo SELECT * FROM ' + @v_shadow5 + N';';
EXEC sp_executesql @v_sql5;
```

섀도우 테이블(`batch_shadow.TClientSettleRate_<runid>_S02`, `batch_shadow.TClientSettleRate4Extra_<runid>_S02`, `batch_shadow.TClientSettleRate4MobileCo_<runid>_S02`)은 캡처 후 24시간이 지나면 부트스트랩 정리 작업에 의해 자동 삭제된다.

#### 오류 코드 매핑

| 문장 | 대상 테이블 | 원본 오류 코드 |
| :--- | :--- | :--- |
| 기정산 예외 가드 | SETTLE_POQ_DB.dbo.TSettleMst(조회) | -9 |
| DELETE 1 | SETTLE_POQ_DB.dbo.TPGSettleRate | -1 |
| INSERT 1 | SETTLE_POQ_DB.dbo.TPGSettleRate | -2 |
| DELETE 2 | SETTLE_POQ_DB.dbo.TClientSettleRate | -3 |
| INSERT 2 | SETTLE_POQ_DB.dbo.TClientSettleRate | -4 |
| DELETE 3 | SETTLE_POQ_DB.dbo.TPGSettleRate4Extra | -5 |
| INSERT 3 | SETTLE_POQ_DB.dbo.TPGSettleRate4Extra | -6 |
| DELETE 4 | SETTLE_POQ_DB.dbo.TClientSettleRate4Extra | -7 |
| INSERT 4 | SETTLE_POQ_DB.dbo.TClientSettleRate4Extra | -8 |
| DELETE 5 | SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo | -9 |
| INSERT 5 | SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo | -10 |
| 전체 성공 | (전체) | 0 |

`@po_intRetVal`은 위 표의 값 그대로 `batch.BatchStepJournal.LegacyReturnCode`에 매핑되며, `-9`는 원본과 동일하게 기정산 가드(진입 직후)와 `DELETE 5` 실패(후반부) 두 지점에서 재사용된다 — 호출 시점과 `StartedAtUtc` 기록으로 두 경로를 구분한다.