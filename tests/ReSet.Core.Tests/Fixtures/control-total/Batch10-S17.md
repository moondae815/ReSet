> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S17 (S17 본문이 `BatchValidationIssue`에 쓰는데 그 표가 목차에 없습니다 - 목차가 선언한 것은 BatchControlTotal뿐입니다. 목차가 권한과 DDL 을 끌고 가므로, 승인된 표만 보고 권한을 잡으면 이 표에 권한이 없어 실행이 실패합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S17. 통합 정합성 검증 및 컨트롤 합계 기록

**단계 개요**

본 단계는 레거시 원본이 없는 순수 제어 단계로, S03~S16까지 적재·재계산된 정산 원장(`SETTLE_POQ_DB.dbo.TSettleMst`)과 4종 요약 테이블(`TSettleByTX`, `TPartialCancelByTX`, `TSettleByIN`, `TSettleByOUT`) 및 PG 수납 통계(`TStatPGCollect`) 간의 금액·건수 정합성을 검증하고, 그 결과를 배치 컨트롤 테이블 `batch.BatchControlTotal`에 기록한다. 불일치가 발견되면 `batch.BatchValidationIssue`에 상세 사유를 남기고 배치 파이프라인을 중단시킨다.

레거시 계승 원본이 없으므로 rule 6-2에 따라 예약 오류 블록을 사용한다. 단계 코드 `S17`의 N=17이므로 블록 시작값은 `-9000 - (17*10) = -9170`이며, 이는 본 단계의 **일반 실패 코드**로 사용한다. 세부 검증 항목별 실패 지점을 구분할 필요가 있는 경우 `-9171`, `-9172`, ... 순으로 블록 내에서만 세분화하며, 그 이상의 코드는 이 문서 다른 단계의 블록을 침범하지 않는다(S17 블록은 `-9170`~`-9179`).

모든 SQL 문은 SNAPSHOT 격리수준 하에서 실행되어야 하는 의무를 가지며(설정 위치는 구현 라운드가 결정), 본 단계는 순수 INSERT/조회로만 구성되어 단일 트랜잭션 안에서 완결되므로 Shadow 테이블이나 보정 DELETE를 사용하지 않는다(rule 4). 검증 대상 원장·요약 테이블에는 어떠한 DML도 가하지 않고 읽기만 하므로, 실패 시 단순 롤백만으로 `batch.BatchControlTotal`/`batch.BatchValidationIssue`에 대한 부분 기록이 복구된다.

**의사코드 (애플리케이션 계층)**

```pseudocode
// (공통 진입부) 이 배치가 아직 시도하지 않은 신규 인프라 조회이므로
// 명세서 DML 범위 표에 대응 행이 없다 - 앵커를 달지 않는다 (S17은 레거시 원본이 없음).
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch10", p_ymd: batchYmd })

// (rule 6-2) 상태 변수는 정수(INT)로 선언하고 0으로 초기화한다. 0은 '아직 실패 지점 없음'을 뜻하며,
// 예약 블록(-9170..-9179)의 값만 대입한다. 문자열 코드는 절대 대입하지 않는다.
currentStepErrorCode = 0
writeStepJournal(runId, "S17", status: "Running", legacyReturnCode: NULL)

hasCriticalIssue = false

beginTransaction()

// ---- 검증 1: 거래 원장 총액 vs 거래집계(TSettleByTX) 총액 ----
currentStepErrorCode = -9170
ledgerTxTotal   = queryScalar(SQL_LEDGER_TX_TOTAL,   { p_ymd: batchYmd })
summaryTxTotal  = queryScalar(SQL_SUMMARY_TX_TOTAL,  { p_ymd: batchYmd })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleMst_CLTotal_TX",  p_controlValue: ledgerTxTotal })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleByTX_CLTotal",    p_controlValue: summaryTxTotal })
IF ledgerTxTotal <> summaryTxTotal:
    repository.execute(SQL_INSERT_VALIDATION_ISSUE, {
        p_runId: runId, p_stepCode: "S17", p_issueCode: "TX_SUMMARY_MISMATCH",
        p_severity: "Critical", p_expected: ledgerTxTotal, p_actual: summaryTxTotal })
    hasCriticalIssue = true

// ---- 검증 2: 지급대상(OutState IN 2,9) 원장 총액 vs 정산집계(TSettleByOUT) 총액 ----
currentStepErrorCode = -9171
ledgerOutTotal  = queryScalar(SQL_LEDGER_OUT_TOTAL,  { p_ymd: batchYmd })
summaryOutTotal = queryScalar(SQL_SUMMARY_OUT_TOTAL, { p_ymd: batchYmd })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleMst_CLTotal_OUT",  p_controlValue: ledgerOutTotal })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleByOUT_CLTotal",    p_controlValue: summaryOutTotal })
IF ledgerOutTotal <> summaryOutTotal:
    repository.execute(SQL_INSERT_VALIDATION_ISSUE, {
        p_runId: runId, p_stepCode: "S17", p_issueCode: "OUT_SUMMARY_MISMATCH",
        p_severity: "Critical", p_expected: ledgerOutTotal, p_actual: summaryOutTotal })
    hasCriticalIssue = true

// ---- 검증 3: 회수대상(INSTATE=1) 원장 거래금액 vs 회수집계(TSettleByIN) 거래금액 ----
currentStepErrorCode = -9172
ledgerInTotal   = queryScalar(SQL_LEDGER_IN_TOTAL,   { p_ymd: batchYmd })
summaryInTotal  = queryScalar(SQL_SUMMARY_IN_TOTAL,  { p_ymd: batchYmd })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleMst_TxAmt_IN",  p_controlValue: ledgerInTotal })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleByIN_TxAmt",     p_controlValue: summaryInTotal })
IF ledgerInTotal <> summaryInTotal:
    repository.execute(SQL_INSERT_VALIDATION_ISSUE, {
        p_runId: runId, p_stepCode: "S17", p_issueCode: "IN_SUMMARY_MISMATCH",
        p_severity: "Critical", p_expected: ledgerInTotal, p_actual: summaryInTotal })
    hasCriticalIssue = true

// ---- 검증 4: 원장 회수예정액 vs PG 수납 통계(TStatPGCollect) 회수금액 ----
currentStepErrorCode = -9173
ledgerCollectTotal  = queryScalar(SQL_LEDGER_COLLECT_TOTAL,  { p_ymd: batchYmd })
statCollectTotal    = queryScalar(SQL_STAT_COLLECT_TOTAL,    { p_ymd: batchYmd })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TSettleMst_CollectAmt",     p_controlValue: ledgerCollectTotal })
repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: "S17", p_controlName: "TStatPGCollect_CollectAmt", p_controlValue: statCollectTotal })
IF ledgerCollectTotal <> statCollectTotal:
    repository.execute(SQL_INSERT_VALIDATION_ISSUE, {
        p_runId: runId, p_stepCode: "S17", p_issueCode: "PGCOLLECT_STAT_MISMATCH",
        p_severity: "Warning", p_expected: ledgerCollectTotal, p_actual: statCollectTotal })
    // Warning은 파이프라인을 중단시키지 않는다 - Critical만 hasCriticalIssue를 세운다.

IF hasCriticalIssue:
    // 이 단계는 INSERT-only이므로 트랜잭션 롤백만으로 batch.BatchControlTotal /
    // batch.BatchValidationIssue에 대한 부분 기록이 전부 원복된다 (rule 11, rule 4 기본 정책).
    conn.rollbackIfOpen()
    writeStepJournal(runId, "S17", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
ELSE:
    conn.commit()
    writeStepJournal(runId, "S17", status: "Succeeded", legacyReturnCode: currentStepErrorCode)
```

**SQL 문 (애플리케이션이 전송하는 구문)**

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_LEDGER_TX_TOTAL
-- 정산 원장(TSettleMst)의 당일 거래분(취소 포함) 고객사총액을 독립적으로 집계한다.
-- CROSS JOIN 없이 자체 집계 스칼라를 산출한다.
SELECT ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;

-- SQL_SUMMARY_TX_TOTAL
-- 거래집계(TSettleByTX)의 동일 기준일 고객사총액을 독립적으로 집계한다.
SELECT ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd;

-- SQL_LEDGER_OUT_TOTAL
-- 지급대상(OutState IN 2,9) 원장 고객사총액.
SELECT ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND OutState IN (2, 9);

-- SQL_SUMMARY_OUT_TOTAL
-- 정산집계(TSettleByOUT)의 동일 조건 고객사총액.
SELECT ISNULL(SUM(CAST(CLTotal AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByOUT
 WHERE YMD = @p_ymd
   AND OutState IN (2, 9);

-- SQL_LEDGER_IN_TOTAL
-- 회수완료(InState=1) 원장 거래금액.
SELECT ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND InState = 1;

-- SQL_SUMMARY_IN_TOTAL
-- 회수집계(TSettleByIN)의 동일 조건 거래금액.
SELECT ISNULL(SUM(CAST(TxAmt AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByIN
 WHERE YMD = @p_ymd;

-- SQL_LEDGER_COLLECT_TOTAL
-- 원장 기준 회수예정액(거래금액-PG사총액), TStatPGCollect의 COLLECTAMT 산식(TXAMT-PGTOTAL)과 동일 정의를 사용.
SELECT ISNULL(SUM(CAST(TxAmt - PGTotal AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE InYMD = @p_ymd
   AND InState = 1;

-- SQL_STAT_COLLECT_TOTAL
-- PG 수납 통계(TStatPGCollect)의 동일 기준일 회수금액 합계.
SELECT ISNULL(SUM(CAST(COLLECTAMT AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TStatPGCollect
 WHERE INYMD = @p_ymd;

-- SQL_INSERT_CONTROL_TOTAL
INSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
VALUES (@p_runId, @p_stepCode, @p_controlName, @p_controlValue, SYSUTCDATETIME());

-- SQL_INSERT_VALIDATION_ISSUE
INSERT INTO batch.BatchValidationIssue
       (RunId, StepCode, IssueCode, Severity, ExpectedValue, ActualValue, DetectedAtUtc)
VALUES (@p_runId, @p_stepCode, @p_issueCode, @p_severity,
        CAST(@p_expected AS NVARCHAR(200)), CAST(@p_actual AS NVARCHAR(200)), SYSUTCDATETIME());
```

**정합성 검증 설계 근거**

- 4건의 검증 모두 좌변·우변을 **각자 독립된 스칼라 집계**(`SQL_LEDGER_*`, `SQL_SUMMARY_*`/`SQL_STAT_*`)로 산출한 뒤 애플리케이션 계층에서 비교한다. 두 집계를 하나의 `CROSS JOIN` 질의로 합쳐 비교하지 않는 이유는, 카티션 곱이 각 변의 건수만큼 금액을 배수로 부풀려 정상 데이터에서도 검증이 실패하고 `batch.BatchControlTotal`에 잘못 부풀려진 값이 기록되기 때문이다.
- 검증 1~3(`TX_SUMMARY_MISMATCH`, `OUT_SUMMARY_MISMATCH`, `IN_SUMMARY_MISMATCH`)은 S13(정산 요약 재구성)이 만든 3개 요약 테이블이 S04~S12의 원장 최종 상태와 정확히 일치해야 한다는 불변식을 검증하므로 `Severity = 'Critical'`로 기록하고, 하나라도 불일치하면 `hasCriticalIssue`가 세워져 파이프라인을 중단시킨다.
- 검증 4(`PGCOLLECT_STAT_MISMATCH`)는 S16(PG 수납 통계 재구성)의 산출물을 원장과 대조하는 것으로, 통계성 리포트 성격이 강해 `Severity = 'Warning'`으로 기록하고 파이프라인을 중단시키지 않는다.
- 모든 컨트롤 값은 `batch.BatchControlTotal`에 항상 기록되며(불일치 여부와 무관), 이는 사후 감사 시 각 실행 회차의 스냅샷 비교를 가능하게 한다.
- 본 단계는 대상 원장·요약 테이블에 어떠한 UPDATE/DELETE도 수행하지 않는 순수 INSERT-only 검증 단계이므로 rule 4의 Shadow 대상이 아니며, 실패 시 단일 트랜잭션 롤백만으로 `batch.BatchControlTotal`과 `batch.BatchValidationIssue`에 대한 부분 기록이 완전히 원복된다.