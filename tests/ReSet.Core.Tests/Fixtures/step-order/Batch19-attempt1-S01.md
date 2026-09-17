### S01 입력 환경 검증

#### 목적과 실행 순서

`POQSettleBatch19`의 최초 진입 게이트로서 다음 조건을 검증한다.

- 입력 기준일이 정확한 `YYYYMMDD` 형식의 유효한 날짜인지 확인한다.
- `SETTLE_POQ_DB`, `PaymentDB`, `PLCardDB`, `SETTLE_CARD_DB`가 온라인이고 실행 계정으로 접근 가능한지 확인한다.
- 네 데이터베이스 모두 SNAPSHOT 격리를 사용할 수 있는 상태인지 확인한다.
- 후속 단계가 참조하는 업무 테이블, 배치 제어 테이블 및 함수가 존재하고 실행 계정에 메타데이터가 노출되는지 확인한다.
- 모든 검증이 성공한 경우에만 S02를 순차 호출한다. S01과 후속 단계를 병렬 실행하지 않는다.

이 단계는 읽기 전용이며 대상 테이블이 없다. `RunId`가 발급되기 전이므로 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint`를 기록하지 않는다. 입력은 `batchYmd` 하나이며 재시작, 건너뛰기 또는 가드 우회용 입력은 추가하지 않는다. 바인딩 `p_batchYmd`는 `VARCHAR(8)` 의미를 보존해야 한다.

#### 트랜잭션 및 오류 계약

- 모든 데이터베이스 조회는 하나의 읽기 전용 SNAPSHOT 트랜잭션에서 수행되어야 한다.
- 구현은 트랜잭션이 실제 SNAPSHOT 격리로 실행됨을 보장해야 하며, 이를 위해 데이터베이스 설정을 변경해서는 안 된다.
- 제어 단계 로컬 오류 변수는 `INT currentStepErrorCode = 0`으로 시작한다.
- 입력 오류, 데이터베이스 접근 오류, SNAPSHOT 사용 불가, 필수 객체 누락 및 검증 SQL 실행 오류의 일반 실패 코드는 **-9010**이다.
- 실패 시 읽기 전용 트랜잭션을 롤백하고 파이프라인을 중단한다. RunId가 없으므로 실패 저널을 쓰지 않고 오류 코드와 `currentStatementId`를 오케스트레이터의 선행 실행 결과로 전달한다.
- `NOLOCK` 또는 `WITH (NOLOCK)`은 사용하지 않는다. 실패 시 변경된 업무 데이터가 없으므로 Shadow Table이나 보상 DML도 사용하지 않는다.

```csharp
// C# 구현 형태를 나타내는 의사코드이며 데이터 접근 구현 형식은 고정하지 않는다.
int currentStepErrorCode = 0;
string currentStatementId = null;

var conn = connectionFactory.open();

try
{
    currentStepErrorCode = -9010;
    currentStatementId = "BEGIN_SNAPSHOT_TRANSACTION";

    // 구현 메커니즘은 이 트랜잭션이 SNAPSHOT 격리임을 보장해야 한다.
    var tx = conn.beginTransaction();

    try
    {
        currentStepErrorCode = -9010;
        currentStatementId = "SQL_VALIDATE_BATCH_YMD";

        var isValidYmd = repository.queryScalar(
            conn,
            tx,
            SQL_VALIDATE_BATCH_YMD,
            new { p_batchYmd = batchYmd }
        );

        if (isValidYmd != 1)
        {
            fail(currentStepErrorCode, currentStatementId, "정산기준일 형식 또는 달력 날짜가 유효하지 않음");
        }

        currentStepErrorCode = -9010;
        currentStatementId = "SQL_VALIDATE_DATABASES";

        var databaseIssues = repository.queryRows(
            conn,
            tx,
            SQL_VALIDATE_DATABASES,
            new { }
        );

        if (databaseIssues.Count != 0)
        {
            fail(currentStepErrorCode, currentStatementId, "필수 데이터베이스 접근 또는 SNAPSHOT 준비 상태가 유효하지 않음");
        }

        currentStepErrorCode = -9010;
        currentStatementId = "SQL_VALIDATE_REQUIRED_OBJECTS";

        var objectIssues = repository.queryRows(
            conn,
            tx,
            SQL_VALIDATE_REQUIRED_OBJECTS,
            new { }
        );

        if (objectIssues.Count != 0)
        {
            fail(currentStepErrorCode, currentStatementId, "필수 테이블, 배치 객체 또는 함수가 존재하지 않거나 보이지 않음");
        }

        tx.commit();
        // 이 지점에 도달한 경우에만 오케스트레이터가 S02를 호출한다.
    }
    catch
    {
        tx.rollbackIfOpen();
        throw;
    }
}
catch
{
    orchestrator.reportPreRunFailure(
        jobName: "POQSettleBatch19",
        legacyReturnCode: currentStepErrorCode,
        statementId: currentStatementId
    );
    throw;
}
finally
{
    conn.close();
}
```

#### 애플리케이션 전송 SQL

```sql
-- SQL_VALIDATE_BATCH_YMD
SELECT CAST
(
    CASE
        WHEN DATALENGTH(@p_batchYmd) = 8
         AND @p_batchYmd COLLATE Latin1_General_100_BIN2
             NOT LIKE '%[^0-9]%' COLLATE Latin1_General_100_BIN2
         AND TRY_CONVERT(date, @p_batchYmd, 112) IS NOT NULL
         AND CONVERT(char(8), TRY_CONVERT(date, @p_batchYmd, 112), 112)
             = @p_batchYmd
        THEN 1
        ELSE 0
    END
    AS int
) AS IsValidBatchYmd;
```

```sql
-- SQL_VALIDATE_DATABASES
WITH RequiredDatabase(DatabaseName) AS
(
    SELECT V.DatabaseName
      FROM
      (
          VALUES
              (CONVERT(sysname, N'SETTLE_POQ_DB')),
              (CONVERT(sysname, N'PaymentDB')),
              (CONVERT(sysname, N'PLCardDB')),
              (CONVERT(sysname, N'SETTLE_CARD_DB'))
      ) AS V(DatabaseName)
)
SELECT R.DatabaseName,
       D.state_desc AS DatabaseState,
       D.snapshot_isolation_state_desc AS SnapshotIsolationState,
       ISNULL(HAS_DBACCESS(R.DatabaseName), 0) AS HasDatabaseAccess
  FROM RequiredDatabase AS R
  LEFT JOIN sys.databases AS D
    ON D.name = R.DatabaseName
 WHERE D.database_id IS NULL
    OR D.state_desc <> N'ONLINE'
    OR D.snapshot_isolation_state_desc <> N'ON'
    OR ISNULL(HAS_DBACCESS(R.DatabaseName), 0) <> 1
 ORDER BY R.DatabaseName;
```

```sql
-- SQL_VALIDATE_REQUIRED_OBJECTS
-- OBJECT_ID가 NULL이면 실제 누락뿐 아니라 실행 계정의 메타데이터 가시성 부족도
-- 포함하므로 환경 검증 실패로 처리한다.
WITH RequiredObject(FullyQualifiedName) AS
(
    SELECT V.FullyQualifiedName
      FROM
      (
          VALUES
              -- 배치 제어 객체
              (N'SETTLE_POQ_DB.batch.BatchRun'),
              (N'SETTLE_POQ_DB.batch.BatchRunLock'),
              (N'SETTLE_POQ_DB.batch.BatchStepJournal'),
              (N'SETTLE_POQ_DB.batch.BatchCheckpoint'),
              (N'SETTLE_POQ_DB.batch.BatchControlTotal'),
              (N'SETTLE_POQ_DB.batch.BatchValidationIssue'),
              (N'SETTLE_POQ_DB.batch.BatchReconciliation'),

              -- SETTLE_POQ_DB 업무 테이블
              (N'SETTLE_POQ_DB.dbo.TSettleMst'),
              (N'SETTLE_POQ_DB.dbo.TPGCMRate'),
              (N'SETTLE_POQ_DB.dbo.TClientContract'),
              (N'SETTLE_POQ_DB.dbo.TClientCMRate'),
              (N'SETTLE_POQ_DB.dbo.TClient'),
              (N'SETTLE_POQ_DB.dbo.TClientCMRate4Extra'),
              (N'SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo'),
              (N'SETTLE_POQ_DB.dbo.TPGSettleRate'),
              (N'SETTLE_POQ_DB.dbo.TClientSettleRate'),
              (N'SETTLE_POQ_DB.dbo.TPGSettleRate4Extra'),
              (N'SETTLE_POQ_DB.dbo.TClientSettleRate4Extra'),
              (N'SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo'),
              (N'SETTLE_POQ_DB.dbo.TPGProperty'),
              (N'SETTLE_POQ_DB.dbo.TCardAllotInterest'),
              (N'SETTLE_POQ_DB.dbo.TPGCollectPeriodMst'),
              (N'SETTLE_POQ_DB.dbo.TSettleByTX'),
              (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX'),
              (N'SETTLE_POQ_DB.dbo.TSettleByIN'),
              (N'SETTLE_POQ_DB.dbo.TSettleByOUT'),
              (N'SETTLE_POQ_DB.dbo.TStatPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TTArsPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TBArsPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TSettleMiss'),

              -- PaymentDB 업무 테이블
              (N'PaymentDB.dbo.TTxMst'),
              (N'PaymentDB.dbo.TPartialCancelTxMst'),
              (N'PaymentDB.dbo.TRefundMst'),
              (N'PaymentDB.dbo.TRefundClient'),
              (N'PaymentDB.dbo.TPromotionTxMst'),
              (N'PaymentDB.dbo.TVAccountTxMst'),
              (N'PaymentDB.dbo.TCCanceledMst'),
              (N'PaymentDB.dbo.TExtraSettleIn'),

              -- PLCardDB 및 SETTLE_CARD_DB 업무 테이블
              (N'PLCardDB.dbo.TPLCardTxMst'),
              (N'SETTLE_CARD_DB.dbo.TCardContractMgmt'),
              (N'SETTLE_CARD_DB.dbo.TClientCardContractMgmt'),
              (N'SETTLE_CARD_DB.dbo.TPLCardEDIMst'),
              (N'SETTLE_CARD_DB.dbo.TExtraTxMst'),

              -- SETTLE_POQ_DB 함수
              (N'SETTLE_POQ_DB.dbo.UF_GET_ROUND4VAT'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_INCVTAXRATE'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_CLIENTSECTIONRATE'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_PGCommOption'),
              (N'SETTLE_POQ_DB.dbo.UF_Get_CLComm4MobileCo'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_SETTLE_EXCHANGERATE'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD'),
              (N'SETTLE_POQ_DB.dbo.UIF_SettleYMD'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_OUTYMD4REFUND'),
              (N'SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2'),

              -- SETTLE_CARD_DB 함수
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT'),
              (N'SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt')
      ) AS V(FullyQualifiedName)
)
SELECT FullyQualifiedName
  FROM RequiredObject
 WHERE OBJECT_ID(FullyQualifiedName) IS NULL
 ORDER BY FullyQualifiedName;
```

#### 완료 및 재실행 기준

- 세 검증 SQL이 모두 정상 실행되고 각각 유효 결과와 빈 오류 목록을 반환해야 성공이다.
- 성공 결과는 영속화하지 않으며 S02에서 최초 `RunId`를 발급한다.
- S01은 읽기 전용이므로 동일 기준일로 반복 호출해도 데이터 상태가 변하지 않는다.
- 재시작 시에도 S01은 생략 가능한 체크포인트 단계가 아니며, 호출될 때마다 현재 입력과 실행 환경을 다시 검증한다.
