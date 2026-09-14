### S21 | 최종 결과 게시

#### 책임과 인터페이스

S01~S20의 성공 상태를 확인한 뒤 다음 세 대상 테이블에 최종 실행 결과를 원자적으로 게시한다.

- `batch.BatchRun`: 실행 상태를 `Succeeded`로 확정한다.
- `batch.BatchStepJournal`: S21이 시작할 때 자기 행을 `Running`으로 **INSERT**하고, 게시 성공 후 같은 행을 `Succeeded`로 갱신한다.
- `batch.BatchCheckpoint`: S21이 시작할 때 자기 행을 `Pending`으로 **INSERT**하고, 게시 성공 후 같은 행을 `Succeeded`로 갱신한다.

레거시 프로시저가 없는 제어 단계이므로 원본 입출력 파라미터는 없다. 실행 컨텍스트에서 `runId BIGINT`, `batchYmd VARCHAR(8)`를 받고 작업명은 `POQSettleBatch11`로 고정한다. 재시작·건너뛰기·우회 입력은 추가하지 않는다.

모든 데이터베이스 문장은 **SNAPSHOT 격리** 의무를 만족해야 한다. S21은 비청크 단계이며 최종 게시 DML을 하나의 트랜잭션으로 처리한다. 실패하면 해당 트랜잭션을 롤백하므로 섀도 테이블이나 보상 DELETE를 사용하지 않는다.

#### 오류 코드

| 코드 | 실패 지점 |
|---:|---|
| `-9210` | 선행 단계 검증 실패 또는 DML 이전의 일반 실패 |
| `-9211` | `batch.BatchStepJournal` 시작 행 INSERT 실패 |
| `-9212` | `batch.BatchCheckpoint` 시작 행 INSERT 실패 |
| `-9213` | `batch.BatchRun` 최종 성공 상태 게시 실패 |
| `-9214` | `batch.BatchStepJournal` 성공 상태 갱신 실패 |
| `-9215` | `batch.BatchCheckpoint` 성공 상태 갱신 실패 |
| `-9216` | 실패 시 `batch.BatchRun` 실패 상태 게시 실패 |
| `-9217` | 실패 시 `batch.BatchStepJournal` 실패 상태 기록 실패 |

`currentStepErrorCode`는 `INT 0`으로 초기화하고 각 DML 직전에 해당 코드를 대입한다. 실제 실패 문장명은 별도로 보존하여 오류 메시지에 포함한다.

#### C# 배치 실행 의사코드

```pseudocode
const stepCode = "S21"
const jobName = "POQSettleBatch11"

currentStepErrorCode = 0
statementName = NULL
startRowsCommitted = false
publicationCommitted = false

try
{
    // S21 자신의 저널·체크포인트 행을 먼저 생성한다.
    startConn = connectionFactory.open()
    startTx = startConn.beginTransaction()   // SNAPSHOT 격리 의무

    statementName = "SQL_INSERT_S21_JOURNAL"
    currentStepErrorCode = -9211
    affected = startConn.execute(SQL_INSERT_S21_JOURNAL, {
        p_runId: runId,
        p_stepCode: stepCode
    })
    if affected != 1:
        failStep("S21 저널 시작 행이 정확히 1건 생성되지 않음")

    statementName = "SQL_INSERT_S21_CHECKPOINT"
    currentStepErrorCode = -9212
    affected = startConn.execute(SQL_INSERT_S21_CHECKPOINT, {
        p_runId: runId,
        p_stepCode: stepCode
    })
    if affected != 1:
        failStep("S21 체크포인트 시작 행이 정확히 1건 생성되지 않음")

    startTx.commit()
    startRowsCommitted = true

    // S01~S20이 모두 Succeeded인지 검증한다.
    // 결과 불일치는 DML 이전의 일반 제어 실패이므로 -9210을 사용한다.
    statementName = "SQL_VERIFY_PRIOR_CHECKPOINTS"
    currentStepErrorCode = -9210
    succeededStepCount = queryScalar(SQL_VERIFY_PRIOR_CHECKPOINTS, {
        p_runId: runId
    })
    if succeededStepCount != 20:
        failStep("S01~S20 체크포인트가 모두 Succeeded가 아님")

    publishConn = connectionFactory.open()
    publishTx = publishConn.beginTransaction()   // SNAPSHOT 격리 의무

    statementName = "SQL_PUBLISH_BATCH_RUN_SUCCESS"
    currentStepErrorCode = -9213
    affected = publishConn.execute(SQL_PUBLISH_BATCH_RUN_SUCCESS, {
        p_runId: runId,
        p_jobName: jobName,
        p_batchYmd: batchYmd
    })
    if affected != 1:
        failStep("batch.BatchRun 성공 게시 대상이 정확히 1건이 아님")

    statementName = "SQL_PUBLISH_S21_JOURNAL_SUCCESS"
    currentStepErrorCode = -9214
    affected = publishConn.execute(SQL_PUBLISH_S21_JOURNAL_SUCCESS, {
        p_runId: runId,
        p_stepCode: stepCode
    })
    if affected != 1:
        failStep("S21 Running 저널 행이 정확히 1건이 아님")

    statementName = "SQL_PUBLISH_S21_CHECKPOINT_SUCCESS"
    currentStepErrorCode = -9215
    affected = publishConn.execute(SQL_PUBLISH_S21_CHECKPOINT_SUCCESS, {
        p_runId: runId,
        p_stepCode: stepCode
    })
    if affected != 1:
        failStep("S21 Pending 체크포인트 행이 정확히 1건이 아님")

    statementName = "SQL_VERIFY_FINAL_PUBLICATION"
    currentStepErrorCode = -9210
    finalState = publishConn.queryRow(SQL_VERIFY_FINAL_PUBLICATION, {
        p_runId: runId,
        p_stepCode: stepCode
    })
    if finalState.RunSucceededCount != 1
       or finalState.JournalSucceededCount != 1
       or finalState.CheckpointSucceededCount != 1:
        failStep("최종 게시 상태 검증 실패")

    publishTx.commit()
    publicationCommitted = true
}
catch
{
    startTx.rollbackIfOpen()
    publishTx.rollbackIfOpen()

    rootFailureCode =
        currentStepErrorCode == 0 ? -9210 : currentStepErrorCode
    rootStatementName = statementName

    // 시작 행이 커밋된 경우에만 S21 자신의 행을 Failed로 전환한다.
    // 체크포인트는 허용 상태가 Pending/Succeeded뿐이므로 Pending으로 유지한다.
    if startRowsCommitted and not publicationCommitted:
    {
        failureConn = connectionFactory.open()
        failureTx = failureConn.beginTransaction()   // SNAPSHOT 격리 의무

        statementName = "SQL_PUBLISH_BATCH_RUN_FAILURE"
        currentStepErrorCode = -9216
        affected = failureConn.execute(SQL_PUBLISH_BATCH_RUN_FAILURE, {
            p_runId: runId,
            p_jobName: jobName,
            p_batchYmd: batchYmd,
            p_errorMessage: errorContext.messageWithStatement(rootStatementName)
        })
        if affected != 1:
            failControlReporting("batch.BatchRun 실패 게시 실패", currentStepErrorCode)

        statementName = "SQL_PUBLISH_S21_JOURNAL_FAILURE"
        currentStepErrorCode = -9217
        affected = failureConn.execute(SQL_PUBLISH_S21_JOURNAL_FAILURE, {
            p_runId: runId,
            p_stepCode: stepCode,
            p_legacyReturnCode: rootFailureCode,
            p_errorMessage: errorContext.messageWithStatement(rootStatementName)
        })
        if affected != 1:
            failControlReporting("S21 실패 저널 갱신 실패", currentStepErrorCode)

        failureTx.commit()
    }

    stopPipeline()
}
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_INSERT_S21_JOURNAL
INSERT INTO batch.BatchStepJournal
(
    RunId,
    StepCode,
    StepStatus,
    LegacyReturnCode,
    StartedAtUtc,
    CompletedAtUtc,
    ErrorMessage
)
VALUES
(
    @p_runId,
    @p_stepCode,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

-- SQL_INSERT_S21_CHECKPOINT
INSERT INTO batch.BatchCheckpoint
(
    RunId,
    StepCode,
    CheckpointStatus,
    CompletedAtUtc
)
VALUES
(
    @p_runId,
    @p_stepCode,
    N'Pending',
    NULL
);

-- SQL_VERIFY_PRIOR_CHECKPOINTS
SELECT COUNT(DISTINCT StepCode)
  FROM batch.BatchCheckpoint
 WHERE RunId = @p_runId
   AND StepCode IN
   (
       N'S01', N'S02', N'S03', N'S04', N'S05',
       N'S06', N'S07', N'S08', N'S09', N'S10',
       N'S11', N'S12', N'S13', N'S14', N'S15',
       N'S16', N'S17', N'S18', N'S19', N'S20'
   )
   AND CheckpointStatus = N'Succeeded';

-- SQL_PUBLISH_BATCH_RUN_SUCCESS
UPDATE batch.BatchRun
   SET RunStatus = N'Succeeded',
       ResumeFromStepCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND RunStatus IN (N'Running', N'Restarting');

-- SQL_PUBLISH_S21_JOURNAL_SUCCESS
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';

-- SQL_PUBLISH_S21_CHECKPOINT_SUCCESS
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND CheckpointStatus = N'Pending';

-- SQL_VERIFY_FINAL_PUBLICATION
SELECT
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchRun
         WHERE RunId = @p_runId
           AND RunStatus = N'Succeeded'
           AND CompletedAtUtc IS NOT NULL
    ) AS RunSucceededCount,
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchStepJournal
         WHERE RunId = @p_runId
           AND StepCode = @p_stepCode
           AND StepStatus = N'Succeeded'
           AND CompletedAtUtc IS NOT NULL
    ) AS JournalSucceededCount,
    (
        SELECT COUNT_BIG(*)
          FROM batch.BatchCheckpoint
         WHERE RunId = @p_runId
           AND StepCode = @p_stepCode
           AND CheckpointStatus = N'Succeeded'
           AND CompletedAtUtc IS NOT NULL
    ) AS CheckpointSucceededCount;

-- SQL_PUBLISH_BATCH_RUN_FAILURE
UPDATE batch.BatchRun
   SET RunStatus = N'Failed',
       ResumeFromStepCode = N'S21',
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND RunStatus IN (N'Running', N'Restarting');

-- SQL_PUBLISH_S21_JOURNAL_FAILURE
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed',
       LegacyReturnCode = @p_legacyReturnCode,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = @p_errorMessage
 WHERE RunId = @p_runId
   AND StepCode = @p_stepCode
   AND StepStatus = N'Running';
```

모든 영향 행 수 판정은 C# 애플리케이션이 실행 결과를 관찰하여 수행한다. SQL에는 `@@ROWCOUNT`, `@@ERROR`, 자체 분기 또는 예외 처리 래퍼를 넣지 않으며, 모든 조회에서 `NOLOCK` 힌트를 제거한다. 최종 성공 트랜잭션이 커밋되기 전까지 `batch.BatchRun`, S21의 `batch.BatchStepJournal`, S21의 `batch.BatchCheckpoint` 성공 상태 중 일부만 영구 반영되어서는 안 된다.