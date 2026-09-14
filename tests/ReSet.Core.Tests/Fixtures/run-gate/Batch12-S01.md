> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S01 (S01의 목차 TargetTables가 비어 있어 대상 테이블 대조를 실행할 수 없습니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S01 — 입력 및 실행 컨텍스트 확정

**역할과 인터페이스**

- 입력은 `batchYmd` 한 개이며 재시작·건너뛰기·우회 플래그를 받지 않는다.
- `batchYmd`는 8자리 ASCII 숫자이고 `yyyyMMdd` 형식의 실제 달력 날짜여야 한다. 공백 제거, 구분자 삽입, 자동 보정은 허용하지 않는다.
- `JobName`은 `POQSettleBatch12`, 현재 단계 코드는 `S01`, 기본 업무 데이터베이스는 `SETTLE_POQ_DB`로 고정한다.
- 레거시 프로시저가 없는 제어 단계이므로 원본 입력·출력 파라미터는 없다.
- 성공 결과는 후속 단계가 사용할 불변 실행 컨텍스트의 기초 정보인 `JobName`, `BatchYmd`, `TargetDatabase`이다. `RunId`는 이 단계에서 생성하거나 계산하지 않는다.

**구성요소 책임**

- `InputContextValidator`: C# 계층에서 문자열 길이, ASCII 숫자 여부, 실제 달력 날짜 여부를 검증한다.
- `DatabasePreflightRepository`: 현재 연결 데이터베이스, 실제 트랜잭션 격리 수준, 관련 데이터베이스의 접근 가능성 및 SNAPSHOT 허용 상태를 조회한다.
- `ExecutionContextBuilder`: 검증된 값만 사용하여 후속 단계에 전달할 불변 컨텍스트를 만든다.

S01의 모든 데이터베이스 조회는 **SNAPSHOT 격리 트랜잭션**에서 실행되어야 한다. 데이터베이스 설정을 변경하지 않으며 SQL에는 `NOLOCK` 계열 힌트를 포함하지 않는다.

```sql
-- SQL_VALIDATE_BATCH_YMD
-- 애플리케이션 검증을 우회한 잘못된 날짜가 DB 경계로 진입하지 못하도록 재확인한다.
DECLARE @v_parsed DATE = TRY_CONVERT(DATE, @p_ymd, 112);

SELECT
    @v_parsed AS ParsedDate,
    CONVERT(CHAR(8), @v_parsed, 112) AS CanonicalYmd;
```

`p_ymd`는 SQL Server 바인딩 타입 `VARCHAR(8)`로 전달한다. 애플리케이션은 `ParsedDate IS NOT NULL`이고 `CanonicalYmd`가 원래 `batchYmd`와 정확히 같은 경우에만 계속 진행한다.

```sql
-- SQL_VERIFY_CURRENT_DATABASE
SELECT
    DB_NAME() AS CurrentDatabase,
    S.transaction_isolation_level AS TransactionIsolationLevel
FROM sys.dm_exec_sessions AS S
WHERE S.session_id = @@SPID;

-- SQL_VERIFY_DATABASE_READINESS
SELECT
    D.name AS DatabaseName,
    D.state_desc AS DatabaseState,
    D.snapshot_isolation_state_desc AS SnapshotIsolationState,
    HAS_DBACCESS(D.name) AS HasDatabaseAccess
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

애플리케이션은 다음 조건을 모두 확인한다.

1. `CurrentDatabase = N'SETTLE_POQ_DB'`
2. `TransactionIsolationLevel = 5`, 즉 현재 업무 트랜잭션이 SNAPSHOT
3. 네 데이터베이스가 모두 한 행씩 반환됨
4. 각 데이터베이스의 `DatabaseState = N'ONLINE'`
5. 각 데이터베이스의 `SnapshotIsolationState = N'ON'`
6. 각 데이터베이스의 `HasDatabaseAccess = 1`

```csharp
const string jobName = "POQSettleBatch12";
const string stepCode = "S01";
const string targetDatabase = "SETTLE_POQ_DB";

int currentStepErrorCode = 0;
string currentStatement = "입력 검증";
transaction workTx = null;

try
{
    if (batchYmd is null ||
        batchYmd.Length != 8 ||
        batchYmd.Any(ch => ch < '0' || ch > '9') ||
        !tryParseExactCalendarDate(batchYmd, "yyyyMMdd"))
    {
        throw validationFailure("BatchYmd는 유효한 yyyyMMdd 형식이어야 한다.");
    }

    conn = connectionFactory.open();
    workTx = conn.beginTransaction(); // SNAPSHOT 의무

    currentStatement = "SQL_VALIDATE_BATCH_YMD";
    dateCheck = repository.queryRow(
        conn,
        workTx,
        SQL_VALIDATE_BATCH_YMD,
        { p_ymd: typedVarchar8(batchYmd) });

    if (dateCheck.ParsedDate is null ||
        dateCheck.CanonicalYmd != batchYmd)
    {
        throw validationFailure("DB 날짜 검증 결과가 입력값과 일치하지 않는다.");
    }

    currentStatement = "SQL_VERIFY_CURRENT_DATABASE";
    databaseContext = repository.queryRow(
        conn,
        workTx,
        SQL_VERIFY_CURRENT_DATABASE,
        { });

    if (databaseContext.CurrentDatabase != targetDatabase ||
        databaseContext.TransactionIsolationLevel != 5)
    {
        throw preflightFailure("기본 데이터베이스 또는 SNAPSHOT 격리 조건이 충족되지 않았다.");
    }

    currentStatement = "SQL_VERIFY_DATABASE_READINESS";
    databaseStates = repository.queryRows(
        conn,
        workTx,
        SQL_VERIFY_DATABASE_READINESS,
        { });

    requiredDatabases =
    {
        "SETTLE_POQ_DB",
        "PaymentDB",
        "PLCardDB",
        "SETTLE_CARD_DB"
    };

    if (!databaseStates.containsExactly(requiredDatabases) ||
        databaseStates.Any(row =>
            row.DatabaseState != "ONLINE" ||
            row.SnapshotIsolationState != "ON" ||
            row.HasDatabaseAccess != 1))
    {
        throw preflightFailure("관련 데이터베이스의 접근성 또는 SNAPSHOT 준비 상태가 불충분하다.");
    }

    workTx.commit();

    return immutableContext(
        JobName: jobName,
        BatchYmd: batchYmd,
        TargetDatabase: targetDatabase);
}
catch (failure)
{
    if (workTx is open)
        workTx.rollback();

    int failureCode =
        currentStepErrorCode == 0
            ? -9010
            : currentStepErrorCode;

    emitStartupFailure(
        StepCode: stepCode,
        LegacyReturnCode: failureCode,
        ErrorMessage: currentStatement + ": " + failure.message);

    stopPipeline();
}
```

**실패 및 재시작 정책**

- S01은 업무 DML을 수행하지 않으므로 부분 변경이나 복구 대상이 없다.
- 비레거시 일반 실패 코드는 **-9010**이며 상태 변수는 반드시 `0`으로 시작한다.
- 입력 검증 실패, 연결 실패, SNAPSHOT 트랜잭션 개시 실패, SQL 조회 실패, 데이터베이스 준비 상태 불충족은 모두 `currentStatement`와 함께 `-9010`으로 보고한다.
- 이 시점에는 실행 등록 전이므로 실패 시 후속 단계를 호출하지 않는다. 재시도는 S01 전체 검증부터 다시 수행하며, 읽기 전용 단계이므로 반복 실행 결과가 업무 데이터를 변경하지 않는다.