> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S01 (S01의 목차 TargetTables가 비어 있어 대상 테이블 대조를 실행할 수 없습니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S01 | 입력 환경 검증

#### 목적과 인터페이스

통합 배치가 업무 데이터를 변경하기 전에 실행 인자, 데이터베이스 상태, SNAPSHOT 사용 가능 여부 및 후속 단계가 참조할 테이블 스키마를 검증한다. 이 단계는 읽기 전용이며 업무 테이블이나 배치 제어 테이블을 변경하지 않는다.

- 입력: `batchYmd` → SQL 바인딩 `p_ymd` (`CHAR(8)` 형식으로 전달)
- 고정 작업명: `POQSettleBatch11`
- 성공 결과: 다음 단계인 S02 호출 허용
- 실패 결과: 정수 오류 코드 `-9010`과 실패한 검증 또는 SQL 문장명을 반환하고 파이프라인 중단
- 재시작·검증 우회·강제 통과 입력은 두지 않는다.
- 최초 실행의 `RunId`는 S02에서 발급되므로 S01 실패는 실행 등록 전 실패 결과로 호출자에게 반환한다. 환경을 수정한 뒤 새 실행을 S01부터 시작한다.

C# 구현은 다음 책임을 분리한다.

- `InputEnvironmentValidationStep`: 검증 순서, SNAPSHOT 트랜잭션, 오류 코드 추적을 담당한다.
- `BatchYmdValidator`: 8자리 숫자와 실제 달력 날짜 여부를 검증한다.
- `EnvironmentCatalogValidator`: 데이터베이스 상태와 스키마 카탈로그를 승인된 스키마 매니페스트와 비교한다.
- 스키마 매니페스트는 후속 S02~S22 SQL이 참조하는 테이블, 컬럼, SQL 타입, 길이, 정밀도 및 스케일을 포함하는 C# 읽기 전용 배포 산출물로 관리한다. 데이터베이스에 별도 함수나 프로시저를 만들지 않는다.

#### 실행 및 오류 추적

S01은 모든 데이터베이스 조회를 SNAPSHOT 격리 의무를 만족하는 하나의 읽기 전용 트랜잭션에서 수행한다. `NOLOCK` 또는 `WITH (NOLOCK)` 힌트는 사용하지 않는다. 검증용 조회는 레거시 DML 범위에 없는 신규 인프라 문장이므로 문장 앵커를 붙이지 않는다.

제어 단계 상태 변수는 반드시 `INT` 값 `0`으로 초기화한다. S01은 세부 오류 코드를 추가하지 않고 모든 실패에 승인된 일반 코드 `-9010`만 사용하며, `statementName`으로 정확한 실패 검증을 구분한다.

```pseudocode
InputEnvironmentValidationStep.execute(batchYmd):
    currentStepErrorCode = 0
    statementName = NULL

    try:
        statementName = "VALIDATE_BATCH_YMD_INPUT"
        currentStepErrorCode = -9010

        if batchYmd is NULL
           or length(batchYmd) != 8
           or batchYmd contains a non-digit:
            fail("정산기준일은 YYYYMMDD 형식의 8자리 숫자여야 한다")

        conn = connectionFactory.open()
        tx = conn.beginTransaction()
        // 이 트랜잭션의 모든 데이터베이스 접근은 SNAPSHOT 격리 의무를 만족한다.

        statementName = "SQL_VALIDATE_BATCH_YMD"
        currentStepErrorCode = -9010
        dateResult = conn.querySingle(
            SQL_VALIDATE_BATCH_YMD,
            { p_ymd: batchYmd }
        )
        if dateResult.IsValidYmd != 1:
            fail("존재하지 않는 달력 날짜다")

        statementName = "SQL_DATABASE_CAPABILITIES"
        currentStepErrorCode = -9010
        capabilities = conn.query(SQL_DATABASE_CAPABILITIES)

        if capabilities does not contain exactly
           SETTLE_POQ_DB, PaymentDB, PLCardDB, SETTLE_CARD_DB:
            fail("필수 데이터베이스가 없거나 메타데이터를 조회할 수 없다")

        if any database StateDesc != "ONLINE":
            fail("필수 데이터베이스가 ONLINE 상태가 아니다")

        if any database SnapshotIsolationState != 1:
            fail("필수 데이터베이스에서 SNAPSHOT 격리가 허용되지 않는다")

        if SETTLE_POQ_DB.CompatibilityLevel < 130:
            fail("후속 SQL의 STRING_SPLIT 사용에 필요한 호환성 수준을 충족하지 않는다")

        if capabilities.ProductMajorVersion < 13:
            fail("지원하는 SQL Server 주 버전을 충족하지 않는다")

        statementName = "SQL_SCHEMA_CATALOG"
        currentStepErrorCode = -9010
        actualCatalog = conn.query(SQL_SCHEMA_CATALOG)

        differences = EnvironmentCatalogValidator.compare(
            expectedSchemaManifest,
            actualCatalog
        )
        if differences is not empty:
            fail("필수 테이블 또는 컬럼 계약 불일치: " + differences.summary)

        tx.commit()

        return {
            status: "Succeeded",
            errorCode: 0,
            statementName: NULL
        }

    catch error:
        tx.rollbackIfOpen()

        return {
            status: "Failed",
            errorCode: currentStepErrorCode,
            statementName: statementName,
            errorMessage: errorContext.messageWithStatement(statementName)
        }
```

#### 애플리케이션이 전송할 SQL

```sql
-- SQL_VALIDATE_BATCH_YMD
DECLARE @v_ymd CHAR(8) = @p_ymd;

SELECT
    CASE
        WHEN LEN(@v_ymd) = 8
         AND @v_ymd NOT LIKE '%[^0-9]%'
         AND TRY_CONVERT(DATE, @v_ymd, 112) IS NOT NULL
         AND CONVERT(CHAR(8), TRY_CONVERT(DATE, @v_ymd, 112), 112) = @v_ymd
        THEN 1
        ELSE 0
    END AS IsValidYmd;
```

```sql
-- SQL_DATABASE_CAPABILITIES
SELECT
    D.name AS DatabaseName,
    D.state_desc AS StateDesc,
    D.compatibility_level AS CompatibilityLevel,
    D.snapshot_isolation_state AS SnapshotIsolationState,
    D.snapshot_isolation_state_desc AS SnapshotIsolationStateDesc,
    D.collation_name AS CollationName,
    TRY_CONVERT(INT, SERVERPROPERTY('ProductMajorVersion')) AS ProductMajorVersion
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

스키마 카탈로그 조회는 후속 단계가 사용하는 모든 업무 테이블과 고정 배치 제어 테이블을 반환한다. C# 비교기는 객체 누락뿐 아니라 컬럼명, 타입, 길이, 정밀도 및 스케일 차이를 모두 실패로 처리한다. 권한 부족으로 카탈로그 행이 보이지 않는 경우에도 매니페스트 누락으로 판정하여 S02 진입을 차단한다.

```sql
-- SQL_SCHEMA_CATALOG
SELECT
    N'SETTLE_POQ_DB' AS DatabaseName,
    S.name AS SchemaName,
    O.name AS ObjectName,
    C.column_id AS ColumnOrdinal,
    C.name AS ColumnName,
    T.name AS DataTypeName,
    C.max_length AS MaxLength,
    C.precision AS NumericPrecision,
    C.scale AS NumericScale,
    C.is_nullable AS IsNullable
FROM SETTLE_POQ_DB.sys.objects AS O
JOIN SETTLE_POQ_DB.sys.schemas AS S
  ON S.schema_id = O.schema_id
JOIN SETTLE_POQ_DB.sys.columns AS C
  ON C.object_id = O.object_id
JOIN SETTLE_POQ_DB.sys.types AS T
  ON T.user_type_id = C.user_type_id
WHERE O.type = 'U'
  AND
  (
      (
          S.name = N'dbo'
          AND O.name IN
          (
              N'TSettleMst',
              N'TPGSettleRate',
              N'TClientSettleRate',
              N'TPGSettleRate4Extra',
              N'TClientSettleRate4Extra',
              N'TClientSettleRate4MobileCo',
              N'TPGCMRate',
              N'TClientContract',
              N'TClientCMRate',
              N'TClient',
              N'TClientCMRate4Extra',
              N'TClientCMRate4MobileCo',
              N'TPGProperty',
              N'TPGCollectPeriodMst',
              N'TCardAllotInterest',
              N'TSettleByTX',
              N'TPartialCancelByTX',
              N'TSettleByIN',
              N'TSettleByOUT',
              N'TStatPGCollect',
              N'TTArsPGCollect',
              N'TBArsPGCollect',
              N'TSettleMiss'
          )
      )
      OR
      (
          S.name = N'batch'
          AND O.name IN
          (
              N'BatchRun',
              N'BatchRunLock',
              N'BatchStepJournal',
              N'BatchCheckpoint',
              N'BatchValidationIssue',
              N'BatchControlTotal'
          )
      )
  )

UNION ALL

SELECT
    N'PaymentDB',
    S.name,
    O.name,
    C.column_id,
    C.name,
    T.name,
    C.max_length,
    C.precision,
    C.scale,
    C.is_nullable
FROM PaymentDB.sys.objects AS O
JOIN PaymentDB.sys.schemas AS S
  ON S.schema_id = O.schema_id
JOIN PaymentDB.sys.columns AS C
  ON C.object_id = O.object_id
JOIN PaymentDB.sys.types AS T
  ON T.user_type_id = C.user_type_id
WHERE O.type = 'U'
  AND S.name = N'dbo'
  AND O.name IN
  (
      N'TTxMst',
      N'TPartialCancelTxMst',
      N'TRefundMst',
      N'TRefundClient',
      N'TPromotionTxMst',
      N'TVAccountTxMst',
      N'TCCanceledMst',
      N'TExtraSettleIn'
  )

UNION ALL

SELECT
    N'PLCardDB',
    S.name,
    O.name,
    C.column_id,
    C.name,
    T.name,
    C.max_length,
    C.precision,
    C.scale,
    C.is_nullable
FROM PLCardDB.sys.objects AS O
JOIN PLCardDB.sys.schemas AS S
  ON S.schema_id = O.schema_id
JOIN PLCardDB.sys.columns AS C
  ON C.object_id = O.object_id
JOIN PLCardDB.sys.types AS T
  ON T.user_type_id = C.user_type_id
WHERE O.type = 'U'
  AND S.name = N'dbo'
  AND O.name = N'TPLCardTxMst'

UNION ALL

SELECT
    N'SETTLE_CARD_DB',
    S.name,
    O.name,
    C.column_id,
    C.name,
    T.name,
    C.max_length,
    C.precision,
    C.scale,
    C.is_nullable
FROM SETTLE_CARD_DB.sys.objects AS O
JOIN SETTLE_CARD_DB.sys.schemas AS S
  ON S.schema_id = O.schema_id
JOIN SETTLE_CARD_DB.sys.columns AS C
  ON C.object_id = O.object_id
JOIN SETTLE_CARD_DB.sys.types AS T
  ON T.user_type_id = C.user_type_id
WHERE O.type = 'U'
  AND S.name = N'dbo'
  AND O.name IN
  (
      N'TCardContractMgmt',
      N'TClientCardContractMgmt',
      N'TPLCardEDIMst',
      N'TExtraTxMst'
  );
```

#### 실패 및 복구

S01은 읽기 전용 단일 트랜잭션이므로 실패 시 열린 트랜잭션만 롤백한다. 업무 데이터 변경, 섀도 테이블, 보상 DELETE 및 부분 커밋은 없다. 다음 조건 중 하나라도 발생하면 `statementName`과 함께 `-9010`을 반환하고 S02 이후 단계를 호출하지 않는다.

- `batchYmd`가 정확한 `YYYYMMDD` 형식이 아니거나 실제 달력 날짜가 아님
- 필수 데이터베이스가 없거나 `ONLINE`이 아님
- 어느 하나의 필수 데이터베이스라도 SNAPSHOT 격리를 허용하지 않음
- SQL Server 버전 또는 `SETTLE_POQ_DB` 호환성 수준이 후속 SQL 요구사항을 충족하지 않음
- 필수 테이블·컬럼이 없거나 타입 계약이 승인된 매니페스트와 다름
- 실행 계정이 카탈로그를 조회할 수 없거나 데이터베이스 연결에 실패함

환경 또는 권한을 수정한 뒤 동일 업무일자로 S01부터 다시 실행한다. 읽기 전용 단계이므로 재실행에 따른 중복 데이터나 별도 복구 작업은 발생하지 않는다.