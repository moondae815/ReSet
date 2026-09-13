### S22: 통합 실행 저널 최종화 및 통제총계 산출

**개요.** 본 스텝은 레거시 원본이 없는 순수 오케스트레이션 스텝이다(`Legacy:` 없음). 규칙 6-2에 따라 이 스텝은 자신만의 예약 오류 코드 블록을 사용한다. 스텝 코드가 `S22`이므로 블록 시작값은 `-9000 - (22 * 10) = -9220`이며, 이 값(`-9220`)이 본 스텝의 **일반 실패 코드**이다. 세부 실패 지점을 구분해야 할 경우에만 `-9221`, `-9222`, … 순으로 블록 시작값에서 더 음수 방향으로 세분화한다(둘 다 `-9220..-9229` 블록 안에 있으므로 규칙 9가 금지하는 "연속 범위 남용"에 해당하지 않는다 — 이 블록 자체가 규칙 6-2가 허용하는 유일한 예외 지점이다).

본 스텝은 두 가지 대상 테이블을 갱신한다.
- **`batch.BatchRunJournal`**: 이 문서의 승인된 스텝 목록이 사용하는 명칭이며, `[Batch Control Table Contract]`가 고정한 물리 테이블 `batch.BatchStepJournal`을 가리킨다. 이 논리 명칭은 처음부터 끝까지 오직 `batch.BatchStepJournal` 하나만을 가리키며, 런 전체의 상태를 담는 별도의 고정 테이블 `batch.BatchRun`(계약표상 `RunId`/`JobName`/`BatchYmd`/`RunStatus` 등을 갖는, S01이 최초 INSERT하고 이후 스텝들이 UPDATE하는 테이블)과는 다른 대상이다. 어떤 후속 스텝도 `BatchRunJournal`이라는 이름으로 신규 물리 테이블을 새로 만들어서는 안 되며, 계약표에 없는 컬럼(예: `FinalStatus`, `PublishedAtUtc`)을 이 이름 아래 정의해서도 안 된다 — 그런 정의는 동일 논리 이름에 대해 서로 다른 DDL을 요구하게 되어 재시작을 매 실행마다 차단한다. 이 스텝을 포함한 모든 스텝은 시작 시 `Running` 행을 자신의 `RunId`+`StepCode`로 `batch.BatchStepJournal`에 INSERT하고 종료 시 그 행을 UPDATE하는 동일한 규약을 따르며, 본 스텝은 추가로 선행 업무 스텝(S01~S21) 전체가 이 저널에서 `Succeeded`로 최종화되어 있는지를 검증하는 역할을 겸한다("통합 실행 저널 최종화").
- **`batch.BatchControlTotal`**: 정산 원장(`SETTLE_POQ_DB.dbo.TSettleMst`)과 거래 요약 테이블(`SETTLE_POQ_DB.dbo.TSettleByTX`)에서 산출한 통제총계를 INSERT 전용으로 적재한다(계약표상 "생성 스텝이 INSERT만 한다 - 상태 전이 없음"). 이 값들은 다음 단계(S23)가 재계산 없이 그대로 대조할 수 있는 기준값이 된다.

본 스텝은 SNAPSHOT 격리 수준 하에서 실행되어야 한다(격리 수준을 어디서/어떻게 지정할지는 이 스텝이 규정하지 않는다). 작업 전체가 배치 스키마 테이블에 대한 단일 트랜잭션으로 완결되고(청크 커밋 없음, `Chunkable: False`), 업무 원장 테이블은 오직 SELECT로만 참조하므로 실패 시 해당 트랜잭션의 롤백만으로 원상복구가 끝난다 — 별도의 섀도우 테이블이나 보정 DELETE는 두지 않는다(규칙 4).

**멱등성.** 이 스텝은 재시작 스킵을 위한 입력 파라미터를 갖지 않는다(규칙 5). 선행 스텝 전수 성공 여부 가드는 호출될 때마다 무조건 실행되며, 이 가드를 우회할 수 있는 조건 분기는 두지 않는다.

```pseudocode
// S22: 통합 실행 저널 최종화 및 통제총계 산출
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch8", p_ymd: batchYmd })

// (rule 6-1) 스텝 로컬 실패지점 변수. NULL로 시작한다 - 어떤 숫자로도 초기화하지 않는다.
currentStepErrorCode = NULL
insertStepJournalRunning(runId, "S22")

TRY:
    beginTransaction()

    // 1) 통합 실행 저널(batch.BatchRunJournal = batch.BatchStepJournal) 최종화 전제조건:
    //    선행 업무 스텝(S01~S21) 전부가 이번 RunId에서 Succeeded로 기록되어 있어야 한다.
    //    이 검사는 스킵 조건 없이 매 호출마다 수행된다(rule 5).
    currentStepErrorCode = -9220
    notSucceededCount = queryScalar(SQL_CHECK_ALL_STEPS_SUCCEEDED, { p_runId: runId })
    IF notSucceededCount > 0:
        RAISE failure   // 가드 실패 - 아래 CATCH로 이동

    // 2) 통제총계 산출: 정산 원장/요약 테이블의 핵심 금액을 batch.BatchControlTotal에 INSERT.
    //    이 값은 S23이 재계산 없이 그대로 비교하는 기준값이 된다.
    currentStepErrorCode = -9221
    execute(SQL_INSERT_CONTROL_TOTALS, { p_runId: runId, p_stepCode: "S22", p_ymd: batchYmd })

    commit()

    writeStepJournal(runId, "S22", status: "Succeeded", LegacyReturnCode: currentStepErrorCode)
CATCH failure:
    // 단일 트랜잭션이므로 롤백이 이미 원상복구를 마쳤다 - 섀도우 복원이나 보정 DELETE는 없다(rule 4).
    rollbackIfOpen()
    writeStepJournal(runId, "S22", status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_CHECK_ALL_STEPS_SUCCEEDED
-- 통합 실행 저널(batch.BatchRunJournal)은 batch.BatchStepJournal과 동일한 물리 테이블을 가리킨다.
-- 이번 RunId에서 선행 업무 스텝 중 Succeeded로 최종화되지 않은 건수를 센다.
SELECT COUNT(*)
  FROM (VALUES
        (N'S01'),(N'S02'),(N'S03'),(N'S04'),(N'S05'),(N'S06'),(N'S07'),
        (N'S08'),(N'S09'),(N'S10'),(N'S11'),(N'S12'),(N'S13'),(N'S14'),
        (N'S15'),(N'S16'),(N'S17'),(N'S18'),(N'S19'),(N'S20'),(N'S21')
       ) AS RequiredSteps(StepCode)
 WHERE NOT EXISTS (
     SELECT 1
       FROM batch.BatchStepJournal J
      WHERE J.RunId = @p_runId
        AND J.StepCode = RequiredSteps.StepCode
        AND J.StepStatus = N'Succeeded'
 );

-- SQL_INSERT_CONTROL_TOTALS
-- 각 SELECT는 독립적으로 자신의 소스를 집계한 뒤 UNION ALL로 결합한다 - 서로 다른 소스를
-- CROSS JOIN으로 곱하지 않는다(정합성 검증 SQL 세트의 원칙을 이 적재 단계에서도 그대로 지킨다).
INSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
SELECT @p_runId, @p_stepCode, N'TSettleMst_TxAmt_Sum',
       CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))),0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, @p_stepCode, N'TSettleMst_CLTotal_Sum',
       CAST(ISNULL(SUM(CAST(CLTOTAL AS DECIMAL(38,4))),0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, @p_stepCode, N'TSettleByTX_TxAmt_Sum',
       CAST(ISNULL(SUM(CAST(TXAMT AS DECIMAL(38,4))),0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd;
```

**오류 추적.** 가드 실패는 블록의 일반 코드 `-9220`으로, 통제총계 INSERT 실패는 세분화 코드 `-9221`로 `batch.BatchStepJournal.LegacyReturnCode`(문서상 표기 `batch.BatchRunJournal`)에 기록된다. `currentStepErrorCode`는 각 문장 직전에 갱신되므로, 실패 시 어느 문장이 원인이었는지 정확히 재구성할 수 있다(규칙 6-1).