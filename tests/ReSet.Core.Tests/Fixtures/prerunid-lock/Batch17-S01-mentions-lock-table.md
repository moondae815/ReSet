### S01 입력 및 실행 전제 검증

**목적 및 인터페이스**

- 호출 인터페이스: `validateS01(string batchYmd)`
- `batchYmd`는 SQL 바인딩 `@p_batchYmd VARCHAR(8)`에 대응한다.
- 작업명은 입력으로 받지 않고 문서 상수 `POQSettleBatch17`을 사용한다.
- 입력값은 정확히 8자리 ASCII 숫자이며 실제 달력 날짜여야 한다. 미래일 제한 등 원본에 없는 업무 규칙은 추가하지 않는다.
- 이 단계에는 출력 파라미터가 없고, 실패 코드는 예약된 일반 실패 코드 **-9010**이다.
- 아직 `batch.BatchRun` 행과 `RunId`가 없으므로 `batch.BatchStepJournal` 및 `batch.BatchCheckpoint`를 기록하지 않는다. 실패 상세는 실행기 로그에 `S01`, 검사명, **-9010**, 원본 예외를 함께 남긴다.

**트랜잭션 및 실행 조건**

- 모든 데이터베이스 조회는 SNAPSHOT 격리 의무를 충족하는 하나의 읽기 전용 트랜잭션에서 실행한다.
- 레거시의 `NOLOCK` 힌트는 사용하지 않는다.
- `SETTLE_POQ_DB`, `PaymentDB`, `PLCardDB`, `SETTLE_CARD_DB`가 온라인이고 접근 가능하며 SNAPSHOT 실행을 허용해야 한다.
- 승인된 대상·원천 테이블과 직접 호출 함수가 모두 존재해야 한다.
- 읽기 전용 단일 트랜잭션이므로 청크, shadow 테이블, 보상 DML은 사용하지 않는다.

```csharp
void validateS01(string batchYmd)
{
    const string jobName = "POQSettleBatch17";
    const string stepCode = "S01";

    int currentStepErrorCode = 0;
    string currentCheckName = "NotStarted";

    try
    {
        currentStepErrorCode = -9010;

        currentCheckName = "JobName";
        if (jobName != "POQSettleBatch17")
            raiseFailure(stepCode, currentStepErrorCode, currentCheckName);

        currentCheckName = "BatchYmdShape";
        if (batchYmd == null ||
            batchYmd.Length != 8 ||
            batchYmd.Any(ch => ch < '0' || ch > '9'))
        {
            raiseFailure(stepCode, currentStepErrorCode, currentCheckName);
        }

        var conn = connectionFactory.open();
        var tx = conn.beginTransaction(); // SNAPSHOT 격리 의무를 만족해야 한다.

        try
        {
            currentCheckName = "BatchYmdCalendar";
            var inputResult = repository.queryRow(
                conn,
                SQL_VALIDATE_BATCH_YMD,
                { p_batchYmd: batchYmd }); // VARCHAR(8)로 바인딩

            if (inputResult.IsValid != 1)
                raiseFailure(stepCode, currentStepErrorCode, currentCheckName);

            currentCheckName = "DatabaseAvailability";
            var databaseResults = repository.queryRows(
                conn,
                SQL_VALIDATE_DATABASES,
                {});

            if (!containsExactlyRequiredDatabases(databaseResults) ||
                databaseResults.Any(row =>
                    row.IsOnline != 1 ||
                    row.IsAccessible != 1 ||
                    row.IsSnapshotEnabled != 1))
            {
                raiseFailure(stepCode, currentStepErrorCode, currentCheckName);
            }

            currentCheckName = "RequiredObjects";
            var missingObjects = repository.queryRows(
                conn,
                SQL_FIND_MISSING_OBJECTS,
                {});

            if (missingObjects.Any())
                raiseFailure(
                    stepCode,
                    currentStepErrorCode,
                    currentCheckName,
                    joinObjectNames(missingObjects));

            tx.commit();
        }
        catch
        {
            tx.rollback();
            throw;
        }
    }
    catch (Exception error)
    {
        // RunId 생성 전 단계이므로 제어 테이블을 갱신하지 않는다.
        writePreRunFailureLog(
            jobName: jobName,
            stepCode: stepCode,
            legacyReturnCode: currentStepErrorCode == 0 ? -9010 : currentStepErrorCode,
            checkName: currentCheckName,
            errorMessage: preserveSqlFailureDetail(error));

        throw;
    }
}
```

```sql
-- SQL_VALIDATE_BATCH_YMD
SELECT CASE
           WHEN DATALENGTH(@p_batchYmd) = 8
            AND @p_batchYmd COLLATE Latin1_General_100_BIN2
                NOT LIKE '%[^0-9]%'
            AND TRY_CONVERT(date, @p_batchYmd, 112) IS NOT NULL
           THEN 1
           ELSE 0
       END AS IsValid;
```

```sql
-- SQL_VALIDATE_DATABASES
SELECT D.name AS DatabaseName,
       CASE WHEN D.state_desc = N'ONLINE' THEN 1 ELSE 0 END AS IsOnline,
       CASE WHEN HAS_DBACCESS(D.name) = 1 THEN 1 ELSE 0 END AS IsAccessible,
       CASE WHEN D.snapshot_isolation_state = 1 THEN 1 ELSE 0 END
           AS IsSnapshotEnabled
  FROM sys.databases AS D
 WHERE D.name IN
       (
           N'SETTLE_POQ_DB',
           N'PaymentDB',
           N'PLCardDB',
           N'SETTLE_CARD_DB'
       );
```

필수 객체 검사는 이름을 런타임에 조합하지 않고 승인된 물리 이름을 리터럴로 고정한다. 결과 행이 한 건이라도 있으면 해당 객체가 누락된 것이다.

```sql
-- SQL_FIND_MISSING_OBJECTS
WITH RequiredObject AS
(
    SELECT V.ObjectName,
           V.ObjectId
      FROM
      (
          VALUES
          (N'batch.BatchRun',
              OBJECT_ID(N'batch.BatchRun')),
          (N'batch.BatchRunLock',
              OBJECT_ID(N'batch.BatchRunLock')),
          (N'batch.BatchStepJournal',
              OBJECT_ID(N'batch.BatchStepJournal')),
          (N'batch.BatchCheckpoint',
              OBJECT_ID(N'batch.BatchCheckpoint')),
          (N'batch.BatchControlTotal',
              OBJECT_ID(N'batch.BatchControlTotal')),
          (N'batch.BatchValidationIssue',
              OBJECT_ID(N'batch.BatchValidationIssue')),
          (N'batch.BatchReconciliationIssue',
              OBJECT_ID(N'batch.BatchReconciliationIssue')),

          (N'SETTLE_POQ_DB.dbo.TSettleMst',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TSettleMst')),
          (N'SETTLE_POQ_DB.dbo.TPGSettleRate',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TPGSettleRate')),
          (N'SETTLE_POQ_DB.dbo.TClientSettleRate',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TClientSettleRate')),
          (N'SETTLE_POQ_DB.dbo.TPGSettleRate4Extra',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TPGSettleRate4Extra')),
          (N'SETTLE_POQ_DB.dbo.TClientSettleRate4Extra',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TClientSettleRate4Extra')),
          (N'SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo')),
          (N'SETTLE_POQ_DB.dbo.TStatPGCollect',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TStatPGCollect')),
          (N'SETTLE_POQ_DB.dbo.TSettleMiss',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TSettleMiss')),
          (N'SETTLE_POQ_DB.dbo.TSettleByTX',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TSettleByTX')),
          (N'SETTLE_POQ_DB.dbo.TPartialCancelByTX',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TPartialCancelByTX')),
          (N'SETTLE_POQ_DB.dbo.TSettleByIN',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TSettleByIN')),
          (N'SETTLE_POQ_DB.dbo.TSettleByOUT',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.TSettleByOUT')),

          (N'PaymentDB.dbo.TTxMst',
              OBJECT_ID(N'PaymentDB.dbo.TTxMst')),
          (N'PaymentDB.dbo.TPartialCancelTxMst',
              OBJECT_ID(N'PaymentDB.dbo.TPartialCancelTxMst')),
          (N'PaymentDB.dbo.TRefundMst',
              OBJECT_ID(N'PaymentDB.dbo.TRefundMst')),
          (N'PaymentDB.dbo.TRefundClient',
              OBJECT_ID(N'PaymentDB.dbo.TRefundClient')),
          (N'PaymentDB.dbo.TPromotionTxMst',
              OBJECT_ID(N'PaymentDB.dbo.TPromotionTxMst')),
          (N'PaymentDB.dbo.TVAccountTxMst',
              OBJECT_ID(N'PaymentDB.dbo.TVAccountTxMst')),
          (N'PaymentDB.dbo.TExtraSettleIn',
              OBJECT_ID(N'PaymentDB.dbo.TExtraSettleIn')),
          (N'PaymentDB.dbo.TCCanceledMst',
              OBJECT_ID(N'PaymentDB.dbo.TCCanceledMst')),
          (N'PLCardDB.dbo.TPLCardTxMst',
              OBJECT_ID(N'PLCardDB.dbo.TPLCardTxMst')),
          (N'SETTLE_CARD_DB.dbo.TCardContractMgmt',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.TCardContractMgmt')),
          (N'SETTLE_CARD_DB.dbo.TClientCardContractMgmt',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.TClientCardContractMgmt')),
          (N'SETTLE_CARD_DB.dbo.TPLCardEDIMst',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.TPLCardEDIMst')),
          (N'SETTLE_CARD_DB.dbo.TExtraTxMst',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.TExtraTxMst')),

          (N'SETTLE_POQ_DB.dbo.UF_GET_ROUND4VAT',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_ROUND4VAT')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_INCVTAXRATE',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_INCVTAXRATE')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_CLIENTSECTIONRATE',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_CLIENTSECTIONRATE')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_PGCommOption',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_PGCommOption')),
          (N'SETTLE_POQ_DB.dbo.UF_Get_CLComm4MobileCo',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_Get_CLComm4MobileCo')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_SETTLE_EXCHANGERATE',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_SETTLE_EXCHANGERATE')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD')),
          (N'SETTLE_POQ_DB.dbo.UIF_SettleYMD',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UIF_SettleYMD')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_OUTYMD4REFUND',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_OUTYMD4REFUND')),
          (N'SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2',
              OBJECT_ID(N'SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST')),
          (N'SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT')),
          (N'SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt',
              OBJECT_ID(N'SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt'))
      ) AS V(ObjectName, ObjectId)
)
SELECT ObjectName
  FROM RequiredObject
 WHERE ObjectId IS NULL
 ORDER BY ObjectName;
```

성공 시 오케스트레이터는 S02로 진행한다. 실패 시에는 비즈니스 테이블이나 배치 제어 테이블을 변경하지 않은 상태로 파이프라인을 중단하고 **-9010**을 반환한다.
