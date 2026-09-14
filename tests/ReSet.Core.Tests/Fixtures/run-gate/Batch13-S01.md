> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S01 (S01의 목차 TargetTables가 비어 있어 대상 테이블 대조를 실행할 수 없습니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S01 — 입력 및 환경 검증

#### 목적과 인터페이스

`POQSettleBatch13` 실행 전에 기준일 형식, 데이터베이스 접근 가능 상태, SNAPSHOT 격리 지원 상태 및 전체 배치 의존 객체의 존재 여부를 검증한다. 이 단계는 업무 또는 제어 테이블을 변경하지 않는 읽기 전용 단계이며, 신규 저장 프로시저·함수·트리거를 생성하지 않는다.

- 입력: `batchYmd: C# string -> p_ymd VARCHAR(8)`
- 작업명은 입력으로 받지 않고 `POQSettleBatch13` 상수를 사용한다.
- 재시작, 건너뛰기 또는 검증 우회 입력은 두지 않는다.
- 레거시 출력 파라미터는 없다. 실패 시 단계 결과의 정수 코드로 `-9010`을 반환한다.
- 모든 데이터베이스 접근은 SNAPSHOT 격리에서 수행해야 하며, `NOLOCK` 힌트는 사용하지 않는다.
- 검증 실패 시 S02를 호출하지 않는다.

#### 검증 절차

```pseudocode
function executeS01(batchYmd):
    currentStepErrorCode = 0
    failedOperationName = null
    conn = null
    tx = null

    TRY:
        // 애플리케이션 입력 검증도 S01 일반 실패 코드로 추적한다.
        failedOperationName = "S01_INPUT_VALIDATION"
        currentStepErrorCode = -9010

        IF batchYmd is null:
            fail("정산기준일이 NULL이다")

        IF length(batchYmd) != 8:
            fail("정산기준일 길이가 8이 아니다")

        IF batchYmd contains a character outside ASCII digits 0 through 9:
            fail("정산기준일에 숫자가 아닌 문자가 포함되어 있다")

        parsedDate = parseExactGregorianDate(batchYmd, "yyyyMMdd")
        IF parsing failed OR format(parsedDate, "yyyyMMdd") != batchYmd:
            fail("정산기준일이 유효한 YYYYMMDD 날짜가 아니다")

        conn = connectionFactory.open()
        ensure all database work in this step runs under SNAPSHOT isolation
        tx = conn.beginTransaction()

        failedOperationName = "SQL_VALIDATE_BATCH_YMD"
        currentStepErrorCode = -9010
        dateResult = conn.queryRow(
            SQL_VALIDATE_BATCH_YMD,
            { p_ymd: batchYmd },
            tx
        )
        IF dateResult.IsValidYmd != 1:
            fail("데이터베이스 날짜 검증에 실패했다")

        failedOperationName = "SQL_VALIDATE_DATABASES"
        currentStepErrorCode = -9010
        databaseIssues = conn.queryRows(SQL_VALIDATE_DATABASES, {}, tx)
        IF databaseIssues is not empty:
            fail("필수 데이터베이스 또는 SNAPSHOT 설정이 준비되지 않았다")

        failedOperationName = "SQL_VALIDATE_REQUIRED_OBJECTS"
        currentStepErrorCode = -9010
        missingObjects = conn.queryRows(SQL_VALIDATE_REQUIRED_OBJECTS, {}, tx)
        IF missingObjects is not empty:
            fail("필수 테이블 또는 함수가 없거나 실행 계정에서 보이지 않는다")

        tx.commit()

        return success(
            StepCode: "S01",
            LegacyReturnCode: null,
            ValidatedBatchYmd: batchYmd
        )

    CATCH error:
        tx.rollbackIfOpen()

        return failure(
            StepCode: "S01",
            LegacyReturnCode: currentStepErrorCode,
            ErrorMessage: failedOperationName + ": " + exact observed error text
        )
```

애플리케이션과 데이터베이스의 날짜 해석 차이를 방지하기 위해 동일 값을 SQL에서도 재검증한다.

```sql
-- SQL_VALIDATE_BATCH_YMD
DECLARE @v_ymd VARCHAR(8) = @p_ymd;

SELECT
    CASE
        WHEN DATALENGTH(@v_ymd) = 8
         AND @v_ymd NOT LIKE '%[^0-9]%' COLLATE Latin1_General_100_BIN2
         AND TRY_CONVERT(DATE, @v_ymd, 112) IS NOT NULL
         AND CONVERT(CHAR(8), TRY_CONVERT(DATE, @v_ymd, 112), 112) = @v_ymd
        THEN 1
        ELSE 0
    END AS IsValidYmd;
```

필수 데이터베이스가 온라인이고 SNAPSHOT 격리를 허용하는지 확인한다. 이 SQL은 데이터베이스 설정을 변경하지 않는다.

```sql
-- SQL_VALIDATE_DATABASES
WITH RequiredDatabase AS
(
    SELECT V.DatabaseName
      FROM
      (
          VALUES
              (N'SETTLE_POQ_DB'),
              (N'PaymentDB'),
              (N'PLCardDB'),
              (N'SETTLE_CARD_DB')
      ) AS V(DatabaseName)
)
SELECT
    R.DatabaseName,
    D.state_desc AS DatabaseState,
    D.snapshot_isolation_state_desc AS SnapshotIsolationState
  FROM RequiredDatabase AS R
  LEFT JOIN sys.databases AS D
    ON D.name = R.DatabaseName
 WHERE D.database_id IS NULL
    OR D.state_desc <> N'ONLINE'
    OR D.snapshot_isolation_state <> 1;
```

의존 객체 검증은 업무 객체와 `batch` 스키마의 배치 제어 객체를 모두 대상으로 한다. `OBJECT_ID`가 `NULL`이면 객체가 없거나 실행 계정에 메타데이터 가시성이 없는 것으로 처리한다.

```sql
-- SQL_VALIDATE_REQUIRED_OBJECTS
WITH RequiredObject AS
(
    SELECT V.ObjectName
      FROM
      (
          VALUES
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
              (N'SETTLE_POQ_DB.dbo.TStatPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TTArsPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TBArsPGCollect'),
              (N'SETTLE_POQ_DB.dbo.TSettleByTX'),
              (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX'),
              (N'SETTLE_POQ_DB.dbo.TSettleByIN'),
              (N'SETTLE_POQ_DB.dbo.TSettleByOUT'),
              (N'SETTLE_POQ_DB.dbo.TSettleMiss'),

              -- PaymentDB 원천 테이블
              (N'PaymentDB.dbo.TTxMst'),
              (N'PaymentDB.dbo.TPartialCancelTxMst'),
              (N'PaymentDB.dbo.TRefundMst'),
              (N'PaymentDB.dbo.TRefundClient'),
              (N'PaymentDB.dbo.TPromotionTxMst'),
              (N'PaymentDB.dbo.TVAccountTxMst'),
              (N'PaymentDB.dbo.TCCanceledMst'),
              (N'PaymentDB.dbo.TExtraSettleIn'),

              -- PLCardDB 및 SETTLE_CARD_DB 원천 테이블
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
              (N'SETTLE_POQ_DB.dbo.UF_Get_WorkDay2'),

              -- SETTLE_CARD_DB 함수
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL'),
              (N'SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT'),
              (N'SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt'),

              -- 배치 제어 객체
              (N'batch.BatchRun'),
              (N'batch.BatchStepJournal'),
              (N'batch.BatchCheckpoint'),
              (N'batch.BatchValidationIssue'),
              (N'batch.BatchControlTotal'),
              (N'batch.BatchRunLock'),
              (N'batch.BatchReconciliation')
      ) AS V(ObjectName)
)
SELECT ObjectName
  FROM RequiredObject
 WHERE OBJECT_ID(ObjectName) IS NULL;
```

#### 오류 및 복구

| 실패 지점 | 반환 코드 | 처리 |
|---|---:|---|
| 입력 형식 또는 실제 날짜 검증 실패 | `-9010` | S02 이후 파이프라인을 시작하지 않는다. |
| SNAPSHOT 트랜잭션 시작 또는 데이터베이스 상태 검증 실패 | `-9010` | 열린 읽기 트랜잭션을 롤백하고 중단한다. |
| 필수 객체 누락 또는 메타데이터 접근 실패 | `-9010` | 누락 객체 목록과 실패한 SQL 이름을 오류 메시지에 포함하고 중단한다. |

S01은 읽기 전용 단일 트랜잭션이므로 Shadow Table, 보상 `DELETE`, 청크 페이징 및 부분 커밋을 사용하지 않는다.