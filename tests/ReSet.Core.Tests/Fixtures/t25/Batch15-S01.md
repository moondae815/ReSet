> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S01 (S01의 목차 TargetTables가 비어 있어 대상 테이블 대조를 실행할 수 없습니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S01 — 입력 및 사전조건 검증

**목적**: 실행 식별자가 생성되기 전에 기준일 형식, 데이터베이스 접근성, SNAPSHOT 사용 가능 여부 및 필수 객체 배포 상태를 검증한다. 이 단계는 읽기 전용이며 성공해도 `RunId`를 생성하지 않고 저널·체크포인트를 기록하지 않는다.

#### 인터페이스 및 검증 기준

- 입력: `batchYmd` — 정확히 8자리 ASCII 숫자인 `yyyyMMdd` 문자열
- 고정 작업명: `POQSettleBatch15`
- 재시작·건너뛰기·가드 우회 입력은 두지 않는다.
- 공백 제거, 자동 형식 변환 또는 날짜 보정을 하지 않는다.
- 실제 달력에 존재하지 않는 날짜를 거부한다.
- 미래일 또는 과거일에 대한 별도 업무 제한은 원본에 없으므로 추가하지 않는다.
- 동시 실행 판정과 잠금 획득은 S02가 원자적으로 수행한다. S01에서 선조회하면 검사와 잠금 사이에 경합이 발생하므로 수행하지 않는다.
- 후속 레거시 단계의 기지급 내역 가드는 S01 결과로 대체하거나 생략하지 않는다. 호출된 각 단계가 자신의 원본 가드를 항상 다시 실행한다.

S01은 레거시 기원이 없는 제어 단계다. 실패 지점 변수는 `INT`의 `0`으로 시작하며 실제 검증 직전에 일반 실패 코드 **-9010**을 설정한다. 다른 오류 코드는 발명하지 않는다.

```pseudocode
validateInputAndPrerequisites(batchYmd):
    currentStepErrorCode = 0
    currentStatementId = "S01_NOT_STARTED"

    // 애플리케이션 입력 검증
    currentStepErrorCode = -9010
    currentStatementId = "LOCAL_VALIDATE_BATCH_YMD"

    IF batchYmd IS NULL:
        fail("BatchYmd is required")

    IF length(batchYmd) != 8:
        fail("BatchYmd must contain exactly 8 characters")

    IF batchYmd contains any character outside ASCII 0 through 9:
        fail("BatchYmd must contain ASCII digits only")

    IF parseExactCalendarDate(batchYmd, "yyyyMMdd") fails:
        fail("BatchYmd is not a valid calendar date")

    conn = connectionFactory.open("POQSettleBatch15Preflight")
    tx = null

    TRY:
        // S01의 모든 데이터베이스 조회에도 SNAPSHOT 격리 의무를 적용한다.
        currentStepErrorCode = -9010
        currentStatementId = "REQUIRE_SNAPSHOT_ISOLATION"
        requireSnapshotIsolation(conn)

        tx = conn.beginTransaction()

        currentStepErrorCode = -9010
        currentStatementId = "SQL_DATABASE_PREFLIGHT"
        databaseRows = conn.query(SQL_DATABASE_PREFLIGHT, {}, tx)

        requiredDatabases = {
            "SETTLE_POQ_DB",
            "PaymentDB",
            "PLCardDB",
            "SETTLE_CARD_DB"
        }

        IF databaseRows names are not exactly requiredDatabases:
            fail("A required database is missing or not visible")

        IF any databaseRows.HasAccess != 1:
            fail("The execution principal cannot access a required database")

        IF any databaseRows.StateDescription != "ONLINE":
            fail("A required database is not ONLINE")

        IF any databaseRows.SnapshotStateDescription != "ON":
            fail("SNAPSHOT isolation is not available for every required database")

        currentStepErrorCode = -9010
        currentStatementId = "SQL_REQUIRED_OBJECT_PREFLIGHT"
        missingObjects = conn.query(SQL_REQUIRED_OBJECT_PREFLIGHT, {}, tx)

        IF missingObjects is not empty:
            fail("Required objects are missing or not visible: " + join(missingObjects))

        tx.commit()

        return {
            JobName: "POQSettleBatch15",
            BatchYmd: batchYmd
        }

    ON FAILURE error:
        rollbackIfOpen(tx)

        // 아직 RunId가 없으므로 batch.BatchStepJournal 및
        // batch.BatchCheckpoint에 기록하지 않는다.
        emitLaunchFailure({
            StepCode: "S01",
            LegacyReturnCode: currentStepErrorCode,
            StatementId: currentStatementId,
            ErrorMessage: error.message
        })

        propagate failure
```

#### 애플리케이션이 전송할 사전검증 SQL

데이터베이스 조회는 결과만 반환하며 성공·실패 분기는 애플리케이션이 수행한다. 데이터베이스 설정을 변경하는 문장은 실행하지 않는다.

```sql
-- SQL_DATABASE_PREFLIGHT
SELECT
    D.name AS DatabaseName,
    HAS_DBACCESS(D.name) AS HasAccess,
    D.state_desc AS StateDescription,
    D.snapshot_isolation_state_desc AS SnapshotStateDescription
FROM sys.databases AS D
WHERE D.name IN
(
    N'SETTLE_POQ_DB',
    N'PaymentDB',
    N'PLCardDB',
    N'SETTLE_CARD_DB'
);
```

다음 조회는 마이그레이션 배치가 사용하는 제어 객체, 업무 테이블 및 함수의 배포 여부를 확인한다. 객체명은 SQL에 리터럴로 고정하며 런타임에 업무 객체명을 조립하지 않는다. 교체 대상인 레거시 저장 프로시저 자체는 필수 객체 목록에 포함하지 않는다.

```sql
-- SQL_REQUIRED_OBJECT_PREFLIGHT
WITH RequiredObject AS
(
    SELECT V.ObjectName
    FROM
    (
        VALUES
            (N'batch.BatchRun'),
            (N'batch.BatchRunLock'),
            (N'batch.BatchStepJournal'),
            (N'batch.BatchCheckpoint'),
            (N'batch.BatchControlTotal'),
            (N'batch.BatchValidationIssue'),

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
            (N'SETTLE_POQ_DB.dbo.TPGCollectPeriodMst'),
            (N'SETTLE_POQ_DB.dbo.TCardAllotInterest'),
            (N'SETTLE_POQ_DB.dbo.TStatPGCollect'),
            (N'SETTLE_POQ_DB.dbo.TTArsPGCollect'),
            (N'SETTLE_POQ_DB.dbo.TBArsPGCollect'),
            (N'SETTLE_POQ_DB.dbo.TSettleByTX'),
            (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX'),
            (N'SETTLE_POQ_DB.dbo.TSettleByIN'),
            (N'SETTLE_POQ_DB.dbo.TSettleByOUT'),
            (N'SETTLE_POQ_DB.dbo.TSettleMiss'),

            (N'PaymentDB.dbo.TTxMst'),
            (N'PaymentDB.dbo.TPartialCancelTxMst'),
            (N'PaymentDB.dbo.TRefundMst'),
            (N'PaymentDB.dbo.TRefundClient'),
            (N'PaymentDB.dbo.TPromotionTxMst'),
            (N'PaymentDB.dbo.TVAccountTxMst'),
            (N'PaymentDB.dbo.TCCanceledMst'),
            (N'PaymentDB.dbo.TExtraSettleIn'),

            (N'PLCardDB.dbo.TPLCardTxMst'),

            (N'SETTLE_CARD_DB.dbo.TCardContractMgmt'),
            (N'SETTLE_CARD_DB.dbo.TClientCardContractMgmt'),
            (N'SETTLE_CARD_DB.dbo.TPLCardEDIMst'),
            (N'SETTLE_CARD_DB.dbo.TExtraTxMst'),

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

            (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT'),
            (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST'),
            (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG'),
            (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST'),
            (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL'),
            (N'SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT'),
            (N'SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt')
    ) AS V(ObjectName)
)
SELECT R.ObjectName
FROM RequiredObject AS R
WHERE OBJECT_ID(R.ObjectName) IS NULL
ORDER BY R.ObjectName;
```

#### 트랜잭션·복구 및 결과 처리

- 데이터베이스 사전검증 조회는 하나의 읽기 전용 SNAPSHOT 트랜잭션에서 수행한다.
- `NOLOCK` 또는 `WITH (NOLOCK)` 힌트를 사용하지 않는다.
- S01에는 업무 DML과 대상 테이블이 없으므로 shadow, 보상 DELETE 및 청크 처리를 적용하지 않는다.
- 실패 시 열린 읽기 트랜잭션만 롤백하고 S02를 호출하지 않는다.
- `RunId`가 아직 발급되지 않았으므로 실패를 `batch.BatchStepJournal`이나 `batch.BatchCheckpoint`에 기록하지 않는다.
- 실행 진입점은 실패 코드 **-9010**, 실패한 로컬 검증 또는 SQL 문장 식별자, 원본 데이터베이스 오류를 함께 운영 로그에 전달해야 한다.