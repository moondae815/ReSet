### S01 | 입력 및 실행환경 검증

#### 목적과 인터페이스

레거시 프로시저가 없는 읽기 전용 제어 단계다. C# 입력은 `batchYmd` 문자열 하나이며 작업명은 코드 상수 `POQSettleBatch18`로 고정한다. 재시작, 건너뛰기 또는 가드 우회 입력은 두지 않는다.

다음 조건을 모두 만족해야 S02를 순차 호출한다.

- `batchYmd`가 정확히 8자리 ASCII 숫자이고 유효한 `yyyyMMdd` 날짜다.
- `SETTLE_POQ_DB`, `PaymentDB`, `PLCardDB`, `SETTLE_CARD_DB`가 온라인 상태이며 SNAPSHOT 격리를 사용할 수 있다.
- `SETTLE_POQ_DB`가 쓰기 가능한 상태다.
- 통합 배치가 참조하는 테이블, 함수, 필수 컬럼과 `batch` 스키마 제어 테이블이 배포 명세와 일치한다.
- 실행 계정에 원천 조회 권한과 향후 대상 테이블별 `INSERT`, `UPDATE`, `DELETE` 권한이 있다.

이 단계 자체는 업무 테이블 또는 제어 테이블을 변경하지 않는다. 모든 조회는 SNAPSHOT 격리의 단일 읽기 트랜잭션에서 수행하며, 레거시의 `NOLOCK` 계열 힌트는 사용하지 않는다.

#### 입력값 검증 SQL

C#에서도 동일 규칙으로 선검증하되, 데이터베이스 경계에서 아래 검증을 다시 수행한다. 조회 결과가 `0`이면 애플리케이션이 실패로 판정하며 SQL 자체에는 분기를 넣지 않는다.

```sql
-- SQL_VALIDATE_BATCH_YMD
DECLARE @v_batchYmd VARCHAR(32) = CONVERT(VARCHAR(32), @p_batchYmd);

SELECT
    CASE
        WHEN LEN(@v_batchYmd) = 8
         AND DATALENGTH(@v_batchYmd) = 8
         AND @v_batchYmd NOT LIKE '%[^0-9]%' COLLATE Latin1_General_100_BIN2
         AND TRY_CONVERT(DATE, @v_batchYmd, 112) IS NOT NULL
         AND CONVERT(CHAR(8), TRY_CONVERT(DATE, @v_batchYmd, 112), 112) = @v_batchYmd
        THEN 1
        ELSE 0
    END AS IsValidBatchYmd;
```

#### 데이터베이스 및 SNAPSHOT 준비 상태 검증

아래 결과는 정확히 네 행이어야 한다. 애플리케이션은 모든 행의 `state_desc`가 `ONLINE`이고 `snapshot_isolation_state_desc`가 `ON`인지 확인하며, `SETTLE_POQ_DB.is_read_only`가 `0`인지 추가 확인한다.

```sql
-- SQL_VALIDATE_DATABASE_ENVIRONMENT
SELECT
    D.name AS DatabaseName,
    D.state_desc AS DatabaseState,
    D.snapshot_isolation_state_desc AS SnapshotIsolationState,
    D.is_read_only AS IsReadOnly
FROM sys.databases AS D
WHERE D.name IN
(
    N'SETTLE_POQ_DB',
    N'PaymentDB',
    N'PLCardDB',
    N'SETTLE_CARD_DB'
)
ORDER BY D.name;
```

#### 객체·컬럼 배포 상태 검증

애플리케이션은 빌드 시 확정한 필수 객체·컬럼 매니페스트와 아래 카탈로그 결과를 비교한다. 매니페스트에는 S04∼S18의 모든 직접 참조 테이블과 함수, 그리고 `batch.BatchRun`, `batch.BatchRunLock`, `batch.BatchStepJournal`, `batch.BatchCheckpoint`, `batch.BatchValidationIssue`, `batch.BatchControlTotal`을 포함한다. 객체 또는 필수 컬럼이 하나라도 없거나 예상 객체 유형과 다르면 실행을 중단한다.

```sql
-- SQL_READ_DEPENDENCY_CATALOG
SELECT
    N'SETTLE_POQ_DB' AS DatabaseName,
    S.name AS SchemaName,
    O.name AS ObjectName,
    O.type AS ObjectType,
    C.name AS ColumnName
FROM SETTLE_POQ_DB.sys.objects AS O
JOIN SETTLE_POQ_DB.sys.schemas AS S
  ON S.schema_id = O.schema_id
LEFT JOIN SETTLE_POQ_DB.sys.columns AS C
  ON C.object_id = O.object_id
WHERE S.name IN (N'dbo', N'batch')

UNION ALL

SELECT
    N'PaymentDB',
    S.name,
    O.name,
    O.type,
    C.name
FROM PaymentDB.sys.objects AS O
JOIN PaymentDB.sys.schemas AS S
  ON S.schema_id = O.schema_id
LEFT JOIN PaymentDB.sys.columns AS C
  ON C.object_id = O.object_id
WHERE S.name = N'dbo'

UNION ALL

SELECT
    N'PLCardDB',
    S.name,
    O.name,
    O.type,
    C.name
FROM PLCardDB.sys.objects AS O
JOIN PLCardDB.sys.schemas AS S
  ON S.schema_id = O.schema_id
LEFT JOIN PLCardDB.sys.columns AS C
  ON C.object_id = O.object_id
WHERE S.name = N'dbo'

UNION ALL

SELECT
    N'SETTLE_CARD_DB',
    S.name,
    O.name,
    O.type,
    C.name
FROM SETTLE_CARD_DB.sys.objects AS O
JOIN SETTLE_CARD_DB.sys.schemas AS S
  ON S.schema_id = O.schema_id
LEFT JOIN SETTLE_CARD_DB.sys.columns AS C
  ON C.object_id = O.object_id
WHERE S.name = N'dbo';
```

실행 계정의 크로스 데이터베이스 조회 가능 여부는 정적 0행 조회로 확인한다. 이 SQL은 데이터를 반환하거나 변경하지 않지만 각 객체의 존재 여부와 `SELECT` 접근 가능 여부를 컴파일 단계에서 검증한다.

```sql
-- SQL_PROBE_SOURCE_READ_ACCESS
SELECT TOP (0) 1 AS Probe FROM SETTLE_POQ_DB.dbo.TSettleMst
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPGCMRate
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientContract
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientCMRate
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClient
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientCMRate4Extra
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientCMRate4MobileCo
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPGSettleRate
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientSettleRate
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPGProperty
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TCardAllotInterest
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPGCollectPeriodMst
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TTArsPGCollect
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TBArsPGCollect
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TStatPGCollect
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TSettleMiss
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TSettleByTX
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TSettleByIN
UNION ALL SELECT TOP (0) 1 FROM SETTLE_POQ_DB.dbo.TSettleByOUT
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TTxMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TPartialCancelTxMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TRefundMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TRefundClient
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TPromotionTxMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TVAccountTxMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TCCanceledMst
UNION ALL SELECT TOP (0) 1 FROM PaymentDB.dbo.TExtraSettleIn
UNION ALL SELECT TOP (0) 1 FROM PLCardDB.dbo.TPLCardTxMst
UNION ALL SELECT TOP (0) 1 FROM SETTLE_CARD_DB.dbo.TCardContractMgmt
UNION ALL SELECT TOP (0) 1 FROM SETTLE_CARD_DB.dbo.TClientCardContractMgmt
UNION ALL SELECT TOP (0) 1 FROM SETTLE_CARD_DB.dbo.TPLCardEDIMst
UNION ALL SELECT TOP (0) 1 FROM SETTLE_CARD_DB.dbo.TExtraTxMst;
```

#### C# 실행 흐름과 오류 처리

제어 단계 상태 변수는 `0`으로 시작한다. 검증 불합격 또는 기술 오류가 확인된 시점에 일반 실패 코드 `-9010`을 설정한다. `currentStatementId`에는 실패한 조회의 식별자를 남겨 입력 오류, 데이터베이스 상태 오류, 객체 누락 또는 권한 오류를 구분한다.

```csharp
var currentStatementId = "";
int currentStepErrorCode = 0;
var conn = connectionFactory.open();
var tx = conn.beginTransaction();

// 이 연결의 S01 조회 전체가 SNAPSHOT 격리에서 실행됨을 보장한다.
try
{
    currentStatementId = "SQL_VALIDATE_BATCH_YMD";
    var inputCheck = repository.queryOne(
        conn,
        tx,
        SQL_VALIDATE_BATCH_YMD,
        new { p_batchYmd = batchYmd });

    if (inputCheck.IsValidBatchYmd != 1)
    {
        currentStepErrorCode = -9010;
        tx.rollbackIfOpen();
        reportPreRunFailure(
            "S01",
            currentStatementId,
            currentStepErrorCode,
            "정산기준일은 유효한 8자리 yyyyMMdd 값이어야 한다.");
        stopPipeline();
    }

    currentStatementId = "SQL_VALIDATE_DATABASE_ENVIRONMENT";
    var databases = repository.queryAll(
        conn,
        tx,
        SQL_VALIDATE_DATABASE_ENVIRONMENT,
        new { });

    if (!allRequiredDatabasesAreOnlineAndSnapshotReady(databases)
        || !settleDatabaseIsWritable(databases))
    {
        currentStepErrorCode = -9010;
        tx.rollbackIfOpen();
        reportPreRunFailure(
            "S01",
            currentStatementId,
            currentStepErrorCode,
            describeDatabaseValidationFailure(databases));
        stopPipeline();
    }

    currentStatementId = "SQL_READ_DEPENDENCY_CATALOG";
    var catalog = repository.queryAll(
        conn,
        tx,
        SQL_READ_DEPENDENCY_CATALOG,
        new { });

    var dependencyDiff = compareWithRequiredObjectColumnManifest(catalog);
    if (dependencyDiff.hasMismatch)
    {
        currentStepErrorCode = -9010;
        tx.rollbackIfOpen();
        reportPreRunFailure(
            "S01",
            currentStatementId,
            currentStepErrorCode,
            dependencyDiff.description);
        stopPipeline();
    }

    currentStatementId = "SQL_PROBE_SOURCE_READ_ACCESS";
    repository.queryAll(
        conn,
        tx,
        SQL_PROBE_SOURCE_READ_ACCESS,
        new { });

    currentStatementId = "WRITE_PERMISSION_MANIFEST_CHECK";
    var permissionDiff = validateRequiredWritePermissions(conn, tx);
    if (permissionDiff.hasMismatch)
    {
        currentStepErrorCode = -9010;
        tx.rollbackIfOpen();
        reportPreRunFailure(
            "S01",
            currentStatementId,
            currentStepErrorCode,
            permissionDiff.description);
        stopPipeline();
    }

    tx.commit();
}
catch
{
    tx.rollbackIfOpen();

    if (currentStepErrorCode == 0)
    {
        currentStepErrorCode = -9010;
    }

    reportPreRunFailure(
        "S01",
        currentStatementId,
        currentStepErrorCode,
        formatError(
            "S01",
            currentStatementId,
            currentSqlErrorNumber(),
            currentSqlErrorMessage()));

    throw;
}
finally
{
    conn.close();
}
```

S01은 `batch.BatchRun` 행이 생성되기 전 단계이므로 RunId가 없으며 `batch.BatchStepJournal`과 `batch.BatchCheckpoint`에 기록하지 않는다. 실패 진단은 실행 전 운영 로그로 반환하고 파이프라인을 중단한다. 성공한 경우에만 S02의 실행 등록 및 잠금 획득을 호출한다.
