### S16 요약 복합 커밋

**목적**: S13 → S14 → S15가 순차 실행한 네 요약 테이블 재구축 결과를 하나의 공유 트랜잭션으로 확정한다. S16은 업무 집계 DML을 추가하지 않으며, `batch.BatchStepJournal`과 `batch.BatchCheckpoint`의 S16 성공 상태를 같은 공유 트랜잭션에 포함한 뒤 최종 커밋한다.

- **대상 테이블**: `batch.BatchStepJournal`, `batch.BatchCheckpoint`
- **레거시 원본 및 인터페이스**: 없음. 레거시 입력·출력 파라미터와 `@po_intRetVal` 매핑도 없다.
- **오류 코드**: 일반 실패 코드 `-9160`
- **실행 순서**: S13, S14, S15 이후에만 실행하며 병렬 실행하지 않는다.
- **격리 수준**: S13에서 시작된 동일한 공유 트랜잭션 전체가 SNAPSHOT 격리 수준이어야 한다.
- **처리 단위**: 청크를 사용하지 않는 단일 복합 트랜잭션이다. Shadow Table과 보상 DELETE를 사용하지 않으며, 실패하면 공유 트랜잭션 전체를 롤백한다.
- **재시작**: 재시작 여부를 입력받지 않는다. 커밋된 S16 체크포인트가 없으면 오케스트레이터가 S13부터 복합 구간을 다시 실행한다.

#### 커밋 전제조건

애플리케이션은 다음 조건을 모두 보장해야 한다.

1. S13에서 시작한 공유 트랜잭션이 아직 활성 상태이며 S14와 S15도 같은 연결 및 트랜잭션으로 실행되었다.
2. 공유 트랜잭션 내부에서 S13, S14, S15의 `batch.BatchStepJournal.StepStatus`가 각각 `Succeeded`이다.
3. 같은 단계들의 `batch.BatchCheckpoint.CheckpointStatus`가 각각 `Succeeded`이다.
4. S16의 성공 저널 및 체크포인트 갱신과 네 요약 테이블 변경이 하나의 커밋으로 함께 확정된다.
5. 커밋 결과가 불명확한 통신 오류가 발생하면 별도 연결에서 S16 체크포인트의 커밋 여부를 먼저 확인한다. 이미 `Succeeded`이면 실패로 재기록하거나 재실행하지 않는다.

#### 애플리케이션 제어 흐름

```pseudocode
// S16은 레거시 단계가 아닌 제어 단계이므로 INT 0으로 시작한다.
currentStepErrorCode = 0
currentStatementId = null
commitAttempted = false

// runId와 sharedTx는 배치 실행 컨텍스트에서 전달된다.
// 재시작·건너뛰기·가드 우회용 입력은 받지 않는다.
REQUIRE sharedTx exists
REQUIRE sharedTx is active
REQUIRE sharedTx belongs to the S13-S16 SNAPSHOT transaction

TRY:
    // 시작 행도 공유 트랜잭션에 넣어 S16 성공 상태와 업무 변경의 원자성을 유지한다.
    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_START_JOURNAL"
    repository.execute(conn, sharedTx, SQL_S16_START_JOURNAL, {
        p_runId: runId
    })

    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_START_CHECKPOINT"
    repository.execute(conn, sharedTx, SQL_S16_START_CHECKPOINT, {
        p_runId: runId
    })

    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_VALIDATE_COMPOSITE_STEPS"
    prerequisiteRows = repository.queryRows(
        conn,
        sharedTx,
        SQL_S16_VALIDATE_COMPOSITE_STEPS,
        { p_runId: runId }
    )

    // 애플리케이션에서 정확히 S13, S14, S15 한 행씩인지 검증한다.
    REQUIRE prerequisiteRows contain exactly one row for each of S13, S14, S15
    REQUIRE every StepStatus equals "Succeeded"
    REQUIRE every CheckpointStatus equals "Succeeded"

    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_MARK_JOURNAL_SUCCEEDED"
    repository.execute(conn, sharedTx, SQL_S16_MARK_JOURNAL_SUCCEEDED, {
        p_runId: runId
    })

    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_MARK_CHECKPOINT_SUCCEEDED"
    repository.execute(conn, sharedTx, SQL_S16_MARK_CHECKPOINT_SUCCEEDED, {
        p_runId: runId
    })

    currentStepErrorCode = -9160
    currentStatementId = "SQL_S16_VERIFY_READY_TO_COMMIT"
    readyRows = repository.queryRows(
        conn,
        sharedTx,
        SQL_S16_VERIFY_READY_TO_COMMIT,
        { p_runId: runId }
    )

    REQUIRE readyRows contain exactly one succeeded journal and checkpoint
            for each of S13, S14, S15, S16

    currentStepErrorCode = -9160
    currentStatementId = "COMMIT_S13_S16_SHARED_TRANSACTION"
    commitAttempted = true
    sharedTx.commit()

CATCH error:
    IF commitAttempted:
        // 커밋 응답 유실과 실제 롤백을 구분한다.
        committedCount = repository.queryScalar(
            controlConn,
            null,
            SQL_S16_COMMIT_PROBE,
            { p_runId: runId }
        )

        IF committedCount == 1:
            // S16 체크포인트가 보이면 공유 커밋은 완료된 것이다.
            return success

    sharedTx.rollbackIfOpen()

    // 공유 트랜잭션의 S16 시작 행도 롤백되었으므로,
    // 별도 SNAPSHOT 제어 트랜잭션에서 S16 시작 행을 다시 만든 후 Failed로 갱신한다.
    failureTx = controlConn.beginTransaction()
    repository.execute(controlConn, failureTx, SQL_S16_START_JOURNAL, {
        p_runId: runId
    })
    repository.execute(controlConn, failureTx, SQL_S16_START_CHECKPOINT, {
        p_runId: runId
    })
    recordStepFailure(
        runId,
        "S16",
        -9160,
        currentStatementId,
        error,
        failureTx
    )
    failureTx.commit()

    stop dependent pipeline
```

#### 애플리케이션이 전송할 SQL

```sql
-- SQL_S16_START_JOURNAL
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
    N'S16',
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);
```

```sql
-- SQL_S16_START_CHECKPOINT
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
    N'S16',
    N'Pending',
    NULL
);
```

```sql
-- SQL_S16_VALIDATE_COMPOSITE_STEPS
SELECT J.StepCode,
       J.StepStatus,
       C.CheckpointStatus
  FROM batch.BatchStepJournal AS J
  INNER JOIN batch.BatchCheckpoint AS C
    ON C.RunId = J.RunId
   AND C.StepCode = J.StepCode
 WHERE J.RunId = @p_runId
   AND J.StepCode IN (N'S13', N'S14', N'S15');
```

```sql
-- SQL_S16_MARK_JOURNAL_SUCCEEDED
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Succeeded',
       LegacyReturnCode = 0,
       CompletedAtUtc = SYSUTCDATETIME(),
       ErrorMessage = NULL
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND StepStatus = N'Running';
```

```sql
-- SQL_S16_MARK_CHECKPOINT_SUCCEEDED
UPDATE batch.BatchCheckpoint
   SET CheckpointStatus = N'Succeeded',
       CompletedAtUtc = SYSUTCDATETIME()
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND CheckpointStatus = N'Pending';
```

```sql
-- SQL_S16_VERIFY_READY_TO_COMMIT
SELECT J.StepCode,
       J.StepStatus,
       C.CheckpointStatus
  FROM batch.BatchStepJournal AS J
  INNER JOIN batch.BatchCheckpoint AS C
    ON C.RunId = J.RunId
   AND C.StepCode = J.StepCode
 WHERE J.RunId = @p_runId
   AND J.StepCode IN (N'S13', N'S14', N'S15', N'S16')
   AND J.StepStatus = N'Succeeded'
   AND C.CheckpointStatus = N'Succeeded';
```

```sql
-- SQL_S16_COMMIT_PROBE
SELECT COUNT_BIG(*)
  FROM batch.BatchCheckpoint
 WHERE RunId = @p_runId
   AND StepCode = N'S16'
   AND CheckpointStatus = N'Succeeded';
```

#### 실패 및 복구 의미

| 실패 지점 | `LegacyReturnCode` | 처리 |
|---|---:|---|
| S13～S15 선행 상태 검증 실패 | -9160 | 공유 트랜잭션 전체 롤백 후 S16 실패 기록 |
| `batch.BatchStepJournal` 시작 또는 성공 갱신 실패 | -9160 | 공유 트랜잭션 전체 롤백 |
| `batch.BatchCheckpoint` 시작 또는 성공 갱신 실패 | -9160 | 공유 트랜잭션 전체 롤백 |
| 공유 트랜잭션 커밋 실패 | -9160 | 커밋 여부 조회 후 미커밋인 경우에만 롤백 및 실패 기록 |
| 공유 트랜잭션 부재 또는 다른 트랜잭션 전달 | -9160 | 구현 계약 위반으로 즉시 중단하고 실패 기록 |

S16 체크포인트의 `Succeeded` 상태는 네 요약 테이블 변경과 동일한 커밋에서만 외부에 보이므로, 체크포인트가 존재한다면 S13～S16 복합 작업 전체가 확정된 것으로 판단할 수 있다.
