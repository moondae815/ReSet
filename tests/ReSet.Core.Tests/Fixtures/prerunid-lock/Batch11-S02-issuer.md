### S02 | 배치 실행 등록

#### 목적 및 인터페이스

`POQSettleBatch11`의 신규 실행을 `batch.BatchRun`에 등록하고, 데이터베이스 `IDENTITY`가 발급한 `RunId`를 이후 S03~S22의 실행 컨텍스트로 반환한다.

- 입력: `batchYmd` — SQL 바인딩 `@p_batchYmd VARCHAR(8) -> p_batchYmd`
- 고정값: `POQSettleBatch11` — SQL 바인딩 `@p_jobName NVARCHAR(128) -> p_jobName`
- 반환: `runId` — `SCOPE_IDENTITY()`를 `BIGINT`로 변환한 값
- 레거시 원본이 없는 제어 단계이므로 성공 시 `LegacyReturnCode`는 `NULL`이며, 실패 일반 코드는 **-9020**이다.
- 재시작 시 기존 실행의 `RunId`와 체크포인트를 사용하는 오케스트레이터가 S02를 호출하지 않는다. 실제로 호출된 S02는 우회 조건 없이 항상 새로운 실행 행을 등록한다.

#### C# 애플리케이션 처리

`S02BatchRunRegistrationStep`은 등록과 발급된 식별자 검증을 하나의 트랜잭션으로 처리한다. 해당 트랜잭션은 반드시 SNAPSHOT 격리 의무를 충족해야 하며, 프레임워크별 설정 방식은 구현 라운드에서 결정한다.

```pseudocode
function ExecuteS02(batchYmd) returns long
{
    currentStepErrorCode = 0
    statementName = NULL
    runId = NULL

    conn = connectionFactory.open()
    tx = conn.beginTransaction()  // SNAPSHOT 격리 의무

    try
    {
        statementName = "SQL_REGISTER_BATCH_RUN"
        currentStepErrorCode = -9020
        runId = conn.queryScalar(
            SQL_REGISTER_BATCH_RUN,
            {
                p_jobName: "POQSettleBatch11",
                p_batchYmd: batchYmd
            })

        statementName = "SQL_VERIFY_BATCH_RUN"
        registered = conn.querySingle(
            SQL_VERIFY_BATCH_RUN,
            {
                p_runId: runId,
                p_jobName: "POQSettleBatch11",
                p_batchYmd: batchYmd
            })

        if runId is NULL or runId <= 0 or registered is NULL
            throw stepFailure(statementName, -9020)

        tx.commit()

        // 이후 단계는 이 값을 새로 계산하지 않고 동일한 실행 컨텍스트로 전달받는다.
        return runId
    }
    catch
    {
        tx.rollbackIfOpen()

        failureCode =
            currentStepErrorCode == 0
                ? -9020
                : currentStepErrorCode

        recordControlFailureWhenRunContextExists(
            stepCode: "S02",
            legacyReturnCode: failureCode,
            statementName: statementName)

        stopPipeline()
    }
}
```

등록과 `RunId` 취득은 반드시 같은 연결·같은 SQL 배치에서 수행하여 다른 세션의 식별자가 섞이지 않도록 한다.

```sql
-- SQL_REGISTER_BATCH_RUN
INSERT INTO batch.BatchRun
(
    JobName,
    BatchYmd,
    RunStatus,
    ResumeFromStepCode,
    StartedAtUtc,
    CompletedAtUtc,
    ErrorMessage
)
VALUES
(
    @p_jobName,
    @p_batchYmd,
    N'Running',
    NULL,
    SYSUTCDATETIME(),
    NULL,
    NULL
);

SELECT CONVERT(BIGINT, SCOPE_IDENTITY()) AS RunId;
```

커밋 전에 발급된 행의 필수 속성을 검증한다. 검증 실패도 동일 트랜잭션을 롤백하므로 `batch.BatchRun`에는 불완전한 실행 행이 남지 않는다.

```sql
-- SQL_VERIFY_BATCH_RUN
SELECT
    RunId,
    JobName,
    BatchYmd,
    RunStatus,
    ResumeFromStepCode,
    StartedAtUtc
FROM batch.BatchRun
WHERE RunId = @p_runId
  AND JobName = @p_jobName
  AND BatchYmd = @p_batchYmd
  AND RunStatus = N'Running'
  AND ResumeFromStepCode IS NULL;
```

#### 트랜잭션·동시성·복구 기준

- `batch.BatchRun` INSERT, `SCOPE_IDENTITY()` 취득 및 등록 행 검증을 하나의 SNAPSHOT 트랜잭션으로 묶는다.
- 등록 실패나 검증 실패 시 열린 트랜잭션만 롤백한다. 단일 트랜잭션으로 원상 복구되므로 섀도 테이블이나 보상 DELETE를 사용하지 않는다.
- 동일 업무일자의 동시 실행이 각각 별도의 `RunId`를 발급받는 것은 허용한다. 실제 단일 실행 소유권 충돌은 다음 S03의 `batch.BatchRunLock`에서 판정한다.
- `RunId`는 `IDENTITY` 결과만 사용하며 애플리케이션에서 계산하거나 `MAX(RunId) + 1` 방식으로 생성하지 않는다.
- 이 단계는 비청크 작업이며 `batch.BatchRun`에 존재하지 않는 가상 청크 키를 도입하지 않는다.
