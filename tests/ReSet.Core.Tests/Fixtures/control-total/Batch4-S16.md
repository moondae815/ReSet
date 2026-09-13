### S16 | 제어합계 검증 및 최종 발행/락 해제

본 Step은 레거시 원본이 없는 제어 Step이다. 따라서 원본 오류 코드를 보존하는 대신, 본 문서가 S16에 배정한 예약 코드 블록 `-9160..-9169`를 사용한다. 블록 시작값 `-9160`은 S16의 **GENERAL 실패 코드**이며 반드시 본 절에 등장해야 한다. Step 진입 시 단계 지역 상태 변수 `currentStepErrorCode`는 INT 형으로 선언하고 **반드시 `0`으로 초기화**한다. `0`은 '아직 실패 지점에 도달하지 않음'을 의미하며, `-9160`으로 미리 초기화하지 않는다(Rule 6-2). 각 DML 실행 **직전에** 해당 문장의 실패 지점 코드를 대입하여, 실패 관측 시 `batch.BatchStepJournal.LegacyReturnCode`에 정확한 정수 코드가 남도록 한다. 문자열 코드(`N'B161'` 등)는 절대 사용하지 않는다.

S16은 **SNAPSHOT 격리 수준에서 실행되어야 한다**. `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON`은 제안하지 않으며, 격리 수준 발행 위치/방식은 애플리케이션이 결정한다(Rule 4). 또한 SNAPSHOT 격리 정책에 따라 본 Step의 모든 SQL 문에는 `WITH (NOLOCK)` 또는 `NOLOCK` 힌트를 절대 사용하지 않는다(Rule 10). 새로운 저장 프로시저/함수/트리거는 생성하지 않으며, SQL은 애플리케이션이 전송하는 문장으로만 존재하고 트랜잭션 경계와 예외 처리는 애플리케이션 코드가 소유한다(Rule 3-1).

대상 테이블은 승인 Step 목록에 명시된 `batch.BatchRun`, `batch.BatchRunLock`, `batch.BatchJournal`, `batch.BatchControlTotal`이다. 저널 대상 테이블은 승인 Step 목록에 `batch.BatchJournal`로 명시되어 있으며, 배치 제어 테이블 계약서 상 물리 테이블은 `batch.BatchStepJournal`로 고정되어 있다(스키마는 반드시 `batch`로 통일). Step 코드 `N'S16'`은 `nvarchar(10)` 컬럼에 문자열로 전달하며, 상태 값(`Running`, `Succeeded`, `Failed`, `Held`, `Released`)은 계약서가 정의한 문자열을 그대로 사용한다. 재시작/건너뛰기 입력 파라미터는 추가하지 않으며, `RunId`는 `batch.BatchRun`에서 조회해 컨텍스트로 전달받는다(Rule 5). 제어합계 검증 시 두 집계를 `CROSS JOIN`으로 비교하지 않고, 각 측을 독립 CTE/서브쿼리로 스칼라화한 뒤 비교한다.

```pseudocode
// S16: 제어합계 검증 및 최종 발행/락 해제 (Legacy 없음, 예약 블록 -9160..-9169)
// SNAPSHOT 격리 수준에서 실행되어야 한다. NOLOCK 힌트는 일체 사용하지 않는다.
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: N'POQSettleBatch4', p_ymd: batchYmd })

// (Rule 6-2) 단계 지역 상태 변수: INT, 0 으로 초기화 (예약 블록 시작값 -9160 으로 초기화 금지)
currentStepErrorCode = 0

// 1) Step 시작 저널 등록 (대상: batch.BatchJournal, 계약서 물리 테이블 batch.BatchStepJournal)
//    각 Step은 시작 시 자신의 저널 행을 INSERT 한다 (StepStatus = N'Running').
beginTransaction()
currentStepErrorCode = -9164   // 저널 INSERT 실패 지점 (블록 시작 -9160 에서 4 를 뺀 값)
execute(SQL_INSERT_STEP_JOURNAL_START, { p_runId: runId, p_stepCode: N'S16' })
commit()

// 2) 제어합계 검증: 사전 단계가 적재한 batch.BatchControlTotal 과 원장 재집계를 비교한다.
//    검증은 애플리케이션이 관측하며, SQL 문장 자체는 분기하지 않는다.
verify = queryRow(SQL_VERIFY_CONTROL_TOTALS, { p_runId: runId, p_ymd: batchYmd })
IF verify.IsMatch = 0:
    // 제어합계 불일치는 S16 의 GENERAL 실패 코드 -9160 으로 기록한다.
    currentStepErrorCode = -9160
    writeStepJournal(runId, N'S16', status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline   // 검증 실패 시 최종 발행/락 해제를 진행하지 않는다.

// 3) 제어합계 적재 (대상: batch.BatchControlTotal, producing step INSERT only)
beginTransaction()
currentStepErrorCode = -9162
execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: N'S16', p_ymd: batchYmd })
commit()

// 4) 최종 발행: batch.BatchRun 상태를 Succeeded 로 갱신한다.
beginTransaction()
currentStepErrorCode = -9163
execute(SQL_UPDATE_BATCH_RUN_SUCCESS, { p_runId: runId })
commit()

// 5) 락 해제: batch.BatchRunLock 의 LockStatus 를 N'Released' 로 갱신한다.
beginTransaction()
currentStepErrorCode = -9161
execute(SQL_RELEASE_RUN_LOCK, { p_jobName: N'POQSettleBatch4', p_ymd: batchYmd, p_runId: runId })
commit()

// 6) Step 종료 저널 갱신 (Succeeded)
beginTransaction()
currentStepErrorCode = -9165
execute(SQL_UPDATE_STEP_JOURNAL_SUCCESS, { p_runId: runId, p_stepCode: N'S16' })
commit()

// 실패 관측 경로 (애플리케이션이 처리):
ON FAILURE observed by the application:
    rollbackIfOpen()
    // 제어합계 불일치는 -9160, 그 외 DML 실패는 문장 직전에 대입한
    // -9161 / -9162 / -9163 / -9164 / -9165 가 그대로 남는다.
    writeStepJournal(runId, N'S16', status: "Failed", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';
```

```sql
-- SQL_INSERT_STEP_JOURNAL_START
-- 대상: batch.BatchJournal (배치 제어 계약서 고정 물리 테이블: batch.BatchStepJournal)
INSERT INTO batch.BatchStepJournal
    (RunId, StepCode, StepStatus, LegacyReturnCode, StartedAtUtc)
VALUES
    (@p_runId, @p_stepCode, N'Running', NULL, SYSUTCDATETIME());
```

```sql
-- SQL_VERIFY_CONTROL_TOTALS
-- 제어합계 검증: 두 집계를 독립 CTE 로 스칼라화한 뒤 비교한다. CROSS JOIN 절대 금지.
WITH Expected AS
(
    SELECT ControlName, ControlValue
      FROM batch.BatchControlTotal
     WHERE RunId = @p_runId
       AND StepCode <> N'S16'          -- 사전 단계가 적재한 제어합계
),
Actual AS
(
    SELECT N'TSettleMst_Sum_TXAMT' AS ControlName,
           CAST(SUM(TXAMT) AS DECIMAL(38,4)) AS ControlValue
      FROM SETTLE_POQ_DB.dbo.TSettleMst
     WHERE YMD = @p_ymd
)
SELECT CASE
         WHEN NOT EXISTS (SELECT 1 FROM Expected) THEN 1
         WHEN EXISTS (
                  SELECT 1 FROM Expected e
                   WHERE e.ControlName = N'TSettleMst_Sum_TXAMT'
                     AND e.ControlValue <> (SELECT ControlValue FROM Actual)
              ) THEN 1
         ELSE 0
       END AS IsMatch;
```

```sql
-- SQL_INSERT_CONTROL_TOTAL
-- 대상: batch.BatchControlTotal (producing step INSERT only, 상태 전이 없음)
INSERT INTO batch.BatchControlTotal
    (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
SELECT @p_runId,
       @p_stepCode,
       N'TSettleMst_Sum_TXAMT',
       CAST(SUM(TXAMT) AS DECIMAL(38,4)),
       SYSUTCDATETIME()
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_UPDATE_BATCH_RUN_SUCCESS
-- 대상: batch.BatchRun (최종 발행)
UPDATE batch.BatchRun
   SET RunStatus      = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage   = NULL
 WHERE RunId = @p_runId;
```

```sql
-- SQL_RELEASE_RUN_LOCK
-- 대상: batch.BatchRunLock (락 해제: Held -> Released)
UPDATE batch.BatchRunLock
   SET LockStatus    = N'Released',
       ReleasedAtUtc = SYSUTCDATETIME(),
       HeartbeatAtUtc = SYSUTCDATETIME()
 WHERE JobName  = @p_jobName
   AND BatchYmd = @p_ymd
   AND LockStatus = N'Held';
```

```sql
-- SQL_UPDATE_STEP_JOURNAL_SUCCESS
-- 대상: batch.BatchJournal (계약서 물리 테이블: batch.BatchStepJournal)
UPDATE batch.BatchStepJournal
   SET StepStatus       = N'Succeeded',
       LegacyReturnCode = 0,
       CompletedAtUtc   = SYSUTCDATETIME()
 WHERE RunId    = @p_runId
   AND StepCode = @p_stepCode;
```