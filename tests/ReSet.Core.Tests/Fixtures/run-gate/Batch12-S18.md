### S18 | 게시 체크포인트 및 잠금 해제

#### 목적 및 실행 계약

S17까지의 체크포인트가 모두 성공했는지 확인한 후 다음 네 대상 테이블을 하나의 원자적 게시 단위로 확정한다.

- `batch.BatchRun`: 실행 상태를 `Succeeded`로 게시
- `batch.BatchStepJournal`: S18 실행 결과를 `Succeeded` 또는 `Failed`로 기록
- `batch.BatchCheckpoint`: S18 체크포인트를 `Succeeded`로 전환
- `batch.BatchRunLock`: 해당 실행이 소유한 잠금을 `Released`로 해제

S18은 레거시 프로시저를 대체하지 않는 제어 단계이므로 원본 입출력 파라미터와 성공 반환코드가 없다. `RunId`는 S02에서 `batch.BatchRun.RunId`의 `IDENTITY`로 발급된 값을 실행 컨텍스트에서 전달받으며 새로 계산하지 않는다. `JobName`은 `POQSettleBatch12`, `StepCode`는 `S18` 상수로 사용한다. 재시작·우회 플래그는 받지 않는다.

모든 트랜잭션은 SNAPSHOT 격리에서 실행해야 한다. 업무 게시가 단일 트랜잭션에 수용되므로 청크, shadow 테이블 및 실패 후 보상 DML을 사용하지 않는다. 모든 조회에서 레거시 `NOLOCK` 힌트를 제거한다.

#### 애플리케이션 제어 흐름

```csharp
const string jobName = "POQSettleBatch12";
const string stepCode = "S18";

int currentStepErrorCode = 0;
string currentStatement = "S18 실행 준비";

conn = connectionFactory.open();

// 공통 시작 패턴으로 S18이 소유하는 저널과 Pending 체크포인트를 생성한다.
startTx = conn.beginTransaction(); // SNAPSHOT 의무
repository.execute(conn, startTx, SQL_JOURNAL_START, {
    p_runId: runId,
    p_stepCode: stepCode
});
startTx.commit();

try
{
    workTx = conn.beginTransaction(); // SNAPSHOT 의무

    // SQL 자체는 분기하지 않는다. 애플리케이션이 조회 결과를 판정한다.
    currentStatement = "SQL_FINALIZATION_GUARD";
    readiness = repository.queryRow(conn, workTx, SQL_FINALIZATION_GUARD, {
        p_runId: runId
    });

    if (readiness.ExpectedCount != 17 || readiness.ReadyCount != 17)
        throw failure("S01부터 S17까지의 체크포인트가 모두 Succeeded가 아님");

    currentStatement = "SQL_PUBLISH_BATCH_RUN";
    currentStepErrorCode = -9181;
    affected = repository.execute(conn, workTx, SQL_PUBLISH_BATCH_RUN, {
        p_runId: runId,
        p_jobName: jobName,
        p_batchYmd: batchYmd
    });
    if (affected != 1)
        throw failure("batch.BatchRun 게시 대상이 정확히 한 건이 아님");

    currentStatement = "SQL_JOURNAL_SUCCEEDED";
    currentStepErrorCode = -9182;
    affected = repository.execute(conn, workTx, SQL_JOURNAL_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode,
        p_legacyReturnCode: null
    });
    if (affected != 1)
        throw failure("S18의 Running 저널을 정확히 한 건 갱신하지 못함");

    currentStatement = "SQL_CHECKPOINT_SUCCEEDED";
    currentStepErrorCode = -9183;
    affected = repository.execute(conn, workTx, SQL_CHECKPOINT_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    });
    if (affected != 1)
        throw failure("S18의 Pending 체크포인트를 정확히 한 건 갱신하지 못함");

    // 잠금 해제는 마지막 DML로 수행한다. 단, 같은 트랜잭션이므로 외부에는
    // BatchRun, 저널, 체크포인트, 잠금 해제가 한 번에 공개된다.
    currentStatement = "SQL_RELEASE_RUN_LOCK";
    currentStepErrorCode = -9184;
    affected = repository.execute(conn, workTx, SQL_RELEASE_RUN_LOCK, {
        p_runId: runId,
        p_jobName: jobName,
        p_batchYmd: batchYmd
    });
    if (affected != 1)
        throw failure("현재 RunId가 소유한 Held 잠금을 정확히 한 건 해제하지 못함");

    workTx.commit();
}
catch (failure)
{
    if (workTx is open)
        workTx.rollback();

    // 특정 게시 DML에 도달하기 전 실패에는 일반 실패 코드 -9180을 사용한다.
    failureCode =
        currentStepErrorCode == 0
            ? -9180
            : currentStepErrorCode;

    failureTx = conn.beginTransaction(); // SNAPSHOT 의무
    repository.execute(conn, failureTx, SQL_JOURNAL_FAILED, {
        p_runId: runId,
        p_stepCode: stepCode,
        p_legacyReturnCode: failureCode,
        p_errorMessage: currentStatement + ": " + failure.message
    });
    failureTx.commit();

    stop pipeline;
}
```

#### 애플리케이션이 전송하는 SQL

```sql
-- SQL_FINALIZATION_GUARD
-- S01~S17 각각에 체크포인트가 정확히 한 건 존재하고 Succeeded인지 검증한다.
WITH ExpectedStep AS
(
    SELECT StepCode
      FROM (VALUES
            (N'S01'), (N'S02'), (N'S03'), (N'S04'), (N'S05'),
            (N'S06'), (N'S07'), (N'S08'), (N'S09'), (N'S10'),
            (N'S11'), (N'S12'), (N'S13'), (N'S14'), (N'S15'),
            (N'S16'), (N'S17')
      ) AS V(StepCode)
),
CheckpointState AS
(
    SELECT StepCode,
           COUNT_BIG(*) AS TotalCount,
           SUM(CASE WHEN CheckpointStatus = N'Succeeded' THEN 1 ELSE 0 END)
               AS SucceededCount
      FROM batch.BatchCheckpoint
     WHERE RunId = @p_runId
       AND StepCode IN
           (N'S01', N'S02', N'S03', N'S04', N'S05', N'S06',
            N'S07', N'S08', N'S09', N'S10', N'S11', N'S12',
            N'S13', N'S14', N'S15', N'S16', N'S17')
     GROUP BY StepCode
)
SELECT COUNT_BIG(*) AS ExpectedCount,
       SUM(CASE
               WHEN C.TotalCount = 1 AND C.SucceededCount = 1 THEN 1
               ELSE 0
           END) AS ReadyCount
  FROM ExpectedStep AS E
  LEFT JOIN CheckpointState AS C
    ON C.StepCode = E.StepCode;

-- SQL_PUBLISH_BATCH_RUN
UPDATE batch.BatchRun
   SET RunStatus = N'Succeeded',
       ResumeFromStepCode = NULL,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND RunStatus IN (N'Running', N'Restarting')
   AND CompletedAtUtc IS NULL;

-- SQL_RELEASE_RUN_LOCK
UPDATE batch.BatchRunLock
   SET LockStatus = N'Released',
       HeartbeatAtUtc = SYSUTCDATETIME(),
       ReleasedAtUtc = SYSUTCDATETIME()
 WHERE JobName = @p_jobName
   AND BatchYmd = @p_batchYmd
   AND OwnerRunId = @p_runId
   AND LockStatus = N'Held';
```

#### 오류 코드와 복구 의미

| 코드 | 실패 지점 |
|---:|---|
| `-9180` | 구체적인 게시 DML에 도달하기 전 발생한 S18 일반 실패 |
| `-9181` | `batch.BatchRun` 성공 게시 실패 |
| `-9182` | `batch.BatchStepJournal` 성공 전환 실패 |
| `-9183` | `batch.BatchCheckpoint` 성공 전환 실패 |
| `-9184` | `batch.BatchRunLock` 잠금 해제 실패 |

업무 트랜잭션 실패 시 네 대상 테이블의 게시 변경은 모두 롤백된다. 따라서 `batch.BatchRunLock`은 `Held` 상태로 유지되고 `batch.BatchRun`은 기존 `Running` 또는 `Restarting` 상태를 유지하며, S18 체크포인트도 `Pending`으로 남는다. 실패 원인이 해소된 뒤 외부 오케스트레이터가 S18을 다시 호출할 수 있으며, 롤백 이후 추가 DELETE나 상태 보상 갱신을 수행해서는 안 된다.