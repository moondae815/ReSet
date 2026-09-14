### S22 | 통합 실행 저널 최종화 및 통제총계 산출

**개요.** 본 스텝은 레거시 기원이 없는 제어 스텝이다(원본 저장 프로시저 없음). 규칙 6-2에 따라 `S22`의 예약 오류 블록은 `block start = -9000 - (22 * 10) = -9220`이며, 이 스텝이 사용하는 모든 실패 코드는 `-9220..-9229` 범위에서만 선택한다. 대상 테이블은 `batch.BatchRunJournal`과 `batch.BatchControlTotal` 두 가지이다. `batch.BatchRunJournal`은 이 문서 전체에서 계약 테이블 `batch.BatchRun`을 가리키는 표기이며(§S01에서 이 행을 최초 INSERT하고, §S23에서 최종 상태를 확정하는 것과 동일한 매핑), 실제로 전송되는 SQL 문에는 계약이 정의한 물리 테이블명 `batch.BatchRun`만 사용한다 — "BatchRunJournal"이라는 별도의 물리 테이블은 존재하지 않는다.

본 스텝은 S21까지의 모든 업무 스텝이 정상 종료되었는지 무조건 재확인하는 가드(규칙 5 — 호출될 때마다 전체 작업을 수행하며, 가드는 우회할 수 없다)를 수행한 뒤, `SETTLE_POQ_DB.dbo.TSettleMst`·`SETTLE_POQ_DB.dbo.TSettleByTX`·`SETTLE_POQ_DB.dbo.TSettleByOUT`로부터 5개의 통제총계(Control Total)를 산출하여 `batch.BatchControlTotal`에 적재하고, 마지막으로 `batch.BatchRun`(BatchRunJournal)의 재시작 지점(`ResumeFromStepCode`)을 `S23`으로 갱신하여 실행 저널을 다음 단계로 최종화한다. 이 스텝이 산출하는 5개 `ControlName` 리터럴(`N'TSettleMst_TxAmt_Sum'`, `N'TSettleMst_CLTotal_Sum'`, `N'TSettleByTX_TxAmt_Sum'`, `N'TSettleByTX_CLTotal_Sum'`, `N'TSettleByOUT_CLTotal_Sum'`)은 후속 검증 스텝(S23)이 재계산·대사하는 이름과 정확히 동일한 리터럴이어야 하며, 대소문자를 포함해 한 글자도 다르게 적어서는 안 된다 — 이것이 두 스텝 사이의 통제총계 계약이다.

이 스텝의 모든 작업은 단일 트랜잭션으로 커밋되므로(체크 → INSERT 5행 → UPDATE 1행), 규칙 4의 기본값에 따라 섀도우 테이블이나 보정 DELETE는 사용하지 않는다 — 실패 시 그 트랜잭션의 롤백만으로 완전히 원상복구된다. 본 스텝을 포함한 모든 스텝은 SNAPSHOT 격리 수준 하에서 실행되어야 하며, 그 설정 위치나 방식은 이 스텝의 관심사가 아니다.

**인터페이스.** S22는 레거시 기원이 없으므로 원본 파라미터 목록이 존재하지 않는다(규칙 5). 이 스텝이 사용하는 값은 오케스트레이션이 공급하는 `runId`와 `batchYmd`(정산기준일) 뿐이며, 재시작·건너뛰기를 위한 입력 파라미터는 추가하지 않는다.

```pseudocode
// S22: 통합 실행 저널 최종화 및 통제총계 산출
// 레거시 기원 없음 - 예약 오류 블록 -9220..-9229 (rule 6-2) 사용.
// "batch.BatchRunJournal"은 계약 테이블 batch.BatchRun을 가리키는 이 문서의 표기이며,
// SQL 문에는 계약의 물리 테이블명 batch.BatchRun만 사용한다.

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch8", p_ymd: batchYmd })
currentStepErrorCode = NULL
insertStepJournalRunning(runId, "S22")
writeCheckpoint(runId, "S22", status: "Pending")

TRY:
    beginTransaction()

    // (rule 5) 가드는 무조건 실행: S22 자신을 제외한 이 실행의 모든 스텝 저널 행이
    // Succeeded 상태인지 재확인한다. 오케스트레이터가 실패 시 파이프라인을 이미 중단시키므로
    // 정상 경로에서는 항상 0건이어야 하지만, 이 재확인 자체를 건너뛸 수 있는 조건은 없다.
    currentStepErrorCode = -9220
    failedCount = queryScalar(SQL_CHECK_PRIOR_STEPS_SUCCEEDED, { p_runId: runId })
    IF failedCount > 0:
        raise failure   // -9220 으로 저널에 기록됨

    // 통제총계 5종 산출 및 적재 (각 집계는 독립된 서브쿼리에서 계산 후 UNION ALL로 결합 -
    // CROSS JOIN으로 두 집계를 곱하지 않는다)
    currentStepErrorCode = -9221
    execute(SQL_INSERT_CONTROL_TOTALS, { p_runId: runId, p_ymd: batchYmd })

    // 실행 저널(batch.BatchRun) 최종화: 다음 재시작 지점을 S23으로 표시
    currentStepErrorCode = -9222
    execute(SQL_FINALIZE_RUN_JOURNAL, { p_runId: runId })

    commit()

    // 단일 트랜잭션이 방금 커밋되었으므로 마지막으로 지정된 코드(-9222)가 감사 기록으로 남는다.
    writeStepJournal(runId, "S22", status: "Succeeded", LegacyReturnCode: currentStepErrorCode)
    writeCheckpoint(runId, "S22", status: "Succeeded")

CATCH failure:
    rollbackIfOpen()
    // 이 스텝은 단일 트랜잭션으로 완결되므로(rule 4 기본값), 섀도우 캡처도 보정 DELETE도 없다.
    // 롤백이 이미 batch.BatchControlTotal의 부분 삽입과 batch.BatchRun의 갱신을 모두 되돌렸다.
    writeStepJournal(runId, "S22", status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_CHECK_PRIOR_STEPS_SUCCEEDED
-- S22 자신의 Running 행(insertStepJournalRunning 직후 삽입됨)은 StepCode 비교로 제외한다.
SELECT COUNT(*) FROM batch.BatchStepJournal
 WHERE RunId = @p_runId
   AND StepCode <> N'S22'
   AND StepStatus <> N'Succeeded';

-- SQL_INSERT_CONTROL_TOTALS
-- 각 SELECT는 자신의 대상 테이블만 독립적으로 집계한 뒤 UNION ALL로 결합한다.
-- 두 집계를 하나의 스칼라로 비교하려고 CROSS JOIN하지 않는다 - 그 방식은 두 집계를
-- 서로의 행수만큼 곱해 값을 부풀린다.
INSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
SELECT @p_runId, N'S22', N'TSettleMst_TxAmt_Sum',
       CAST(ISNULL(SUM(TXAMT), 0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, N'S22', N'TSettleMst_CLTotal_Sum',
       CAST(ISNULL(SUM(CLTOTAL), 0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, N'S22', N'TSettleByTX_TxAmt_Sum',
       CAST(ISNULL(SUM(TXAMT), 0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, N'S22', N'TSettleByTX_CLTotal_Sum',
       CAST(ISNULL(SUM(CLTOTAL), 0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd
UNION ALL
SELECT @p_runId, N'S22', N'TSettleByOUT_CLTotal_Sum',
       CAST(ISNULL(SUM(CLTOTAL), 0) AS DECIMAL(38,4)), SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleByOUT
 WHERE YMD = @p_ymd;

-- SQL_FINALIZE_RUN_JOURNAL
-- "batch.BatchRunJournal" = 계약 테이블 batch.BatchRun. RunStatus는 아직 확정하지 않으며
-- (그 판정과 발행은 S23/S24의 책임이다), 재시작 재개 지점만 다음 스텝으로 이동시킨다.
UPDATE batch.BatchRun
   SET ResumeFromStepCode = N'S23'
 WHERE RunId = @p_runId
   AND RunStatus = N'Running';
```

**오류 코드 및 재시작.** 이 스텝이 사용하는 유일한 원본 오류 코드는 없으며(레거시 기원 없음), 예약 블록의 일반 실패 코드 `-9220`은 위 가드 실패 지점에 배정되어 있다. `-9221`은 통제총계 적재 실패, `-9222`는 실행 저널 최종화(UPDATE) 실패를 구분한다. 재시작 시 오케스트레이터는 `batch.BatchCheckpoint`에서 `S22`가 `Succeeded`인지 확인해 이미 완료된 경우 이 스텝을 다시 호출하지 않는다(규칙 5). 재호출되는 경우(직전 시도가 `Failed`로 종료된 경우)에도 이 스텝은 파라미터 없이 전체 작업(가드 재확인 → 통제총계 재적재 → 저널 최종화)을 처음부터 다시 수행하며, 단일 트랜잭션이므로 이전 실패 시도가 남긴 부분 데이터는 없다.