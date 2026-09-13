> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S13 (S13 본문이 `BatchControlTotal`에 쓰는데 그 표가 목차에 없습니다 - 목차가 선언한 것은 ControlTotal뿐입니다. 목차가 권한과 DDL 을 끌고 가므로, 승인된 표만 보고 권한을 잡으면 이 표에 권한이 없어 실행이 실패합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S13 | 원장 동결 통제 합계

#### 목적 및 실행 순서

S12 완료 직후, S14 요약 처리를 시작하기 전에 `SETTLE_POQ_DB.dbo.TSettleMst`의 기준일 원장을 읽어 통제 합계를 고정한다. 이후 S14~S19는 이 기준 원장을 변경하지 않아야 하며, S20은 본 단계의 합계와 요약·통계 결과를 비교한다.

승인 단계 목록의 `batch.ControlTotal`은 논리 대상 명칭이며, 고정 배치 객체 계약에 따른 실제 물리 대상은 `batch.BatchControlTotal`이다. 별도의 `batch.ControlTotal` 테이블을 생성하거나 참조하지 않는다.

- S12 → **S13** → S14 순서를 직렬로 유지하며 병렬 실행하지 않는다.
- 입력 인터페이스는 `long runId -> @p_runId BIGINT`, `string batchYmd -> @p_ymd VARCHAR(8)`이다.
- 단계 코드는 오케스트레이션 상수 `N'S13'`을 사용한다.
- 재시작·건너뛰기 입력은 추가하지 않는다.
- 레거시 원본이 없는 제어 단계이므로 출력 파라미터는 없다.
- 일반 실패 코드는 **`-9130`**이며 단계 상태 변수는 `INT 0`으로 초기화한다.
- 모든 데이터베이스 문장은 SNAPSHOT 격리 의무를 만족해야 한다.
- `NOLOCK` 및 `WITH (NOLOCK)` 힌트는 사용하지 않는다.

#### 통제 합계 정의

기준일 `YMD = @p_ymd`에 대해 다음 18개 값을 `batch.BatchControlTotal`에 저장한다.

| ControlName | 산식 |
|---|---|
| `LedgerRowCount` | `COUNT_BIG(*)` |
| `TxAmt` | `SUM(TXAMT)` |
| `CLComm` | `SUM(CLCOMM)` |
| `CLIntComm` | `SUM(CLINTCOMM)` |
| `CLEtc` | `SUM(CLETC)` |
| `CLVT` | `SUM(CLVT)` |
| `CLTotal` | `SUM(CLTOTAL)` |
| `PGComm` | `SUM(PGCOMM)` |
| `PGIntExpComm` | `SUM(PGINTEXPCOMM)` |
| `PGIntRealComm` | `SUM(PGINTREALCOMM)` |
| `PGEtc` | `SUM(PGETC)` |
| `PGVT` | `SUM(PGVT)` |
| `PGTotal` | `SUM(PGTOTAL)` |
| `POQIncome` | `SUM(POQINCOME)` |
| `NonSettleAmt` | `SUM(NonSettleAmt)` |
| `ExtraTxAmt` | `SUM(ExtraTxAmt)` |
| `SeperateAmt` | `SUM(SeperateAmt)` |
| `ForeignSettleAmt` | `SUM(ForeignSettleAmt)` |

원장 행이 없더라도 `LedgerRowCount=0`과 나머지 금액 합계 `0`을 모두 기록한다.

#### C# 배치 제어 의사코드

```pseudocode
ExecuteS13(runId, batchYmd)
{
    currentStepErrorCode = 0
    statementName = null

    // 공통 규약에 따라 S13 저널과 체크포인트 시작 행을 먼저 등록한다.

    try
    {
        conn = connectionFactory.open()
        tx = conn.beginTransaction()

        // 이 트랜잭션의 모든 문장은 SNAPSHOT 격리 의무를 충족해야 한다.
        // S12가 성공했고 S14가 아직 시작되지 않았음을 오케스트레이터가 보장한다.

        statementName = "SQL_CAPTURE_LEDGER_TOTALS"
        currentStepErrorCode = -9130
        conn.execute(SQL_CAPTURE_LEDGER_TOTALS, {
            p_runId: runId,
            p_ymd: batchYmd
        })

        validation = conn.queryRow(SQL_VALIDATE_CAPTURE, {
            p_runId: runId,
            p_ymd: batchYmd
        })

        if (validation.ActualControlCount != validation.ExpectedControlCount
            || validation.DistinctControlCount != validation.ExpectedControlCount
            || validation.MismatchCount != 0)
        {
            throw errorContext.validationFailure(
                "SQL_VALIDATE_CAPTURE",
                validation)
        }

        tx.commit()

        // 성공 LegacyReturnCode는 레거시 출력이 없으므로 NULL로 기록한다.
        markStepSucceeded(runId, "S13", legacyReturnCode: null)
    }
    catch
    {
        // 단일 트랜잭션이므로 실패한 INSERT와 검증 전 변경을 모두 원복한다.
        tx.rollbackIfOpen()

        writeStepJournal(
            runId,
            "S13",
            status: "Failed",
            LegacyReturnCode: currentStepErrorCode,
            ErrorMessage: errorContext.messageWithStatement(statementName))

        stopPipeline()
    }
}
```

#### 애플리케이션이 전송하는 SQL

```sql
-- SQL_CAPTURE_LEDGER_TOTALS
DECLARE @v_capturedAtUtc DATETIME2(3) = SYSUTCDATETIME();

;WITH Ledger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        COALESCE(SUM(CAST(ISNULL(TXAMT,            0) AS DECIMAL(38,4))), 0) AS TxAmt,
        COALESCE(SUM(CAST(ISNULL(CLCOMM,           0) AS DECIMAL(38,4))), 0) AS CLComm,
        COALESCE(SUM(CAST(ISNULL(CLINTCOMM,        0) AS DECIMAL(38,4))), 0) AS CLIntComm,
        COALESCE(SUM(CAST(ISNULL(CLETC,            0) AS DECIMAL(38,4))), 0) AS CLEtc,
        COALESCE(SUM(CAST(ISNULL(CLVT,             0) AS DECIMAL(38,4))), 0) AS CLVT,
        COALESCE(SUM(CAST(ISNULL(CLTOTAL,          0) AS DECIMAL(38,4))), 0) AS CLTotal,
        COALESCE(SUM(CAST(ISNULL(PGCOMM,           0) AS DECIMAL(38,4))), 0) AS PGComm,
        COALESCE(SUM(CAST(ISNULL(PGINTEXPCOMM,    0) AS DECIMAL(38,4))), 0) AS PGIntExpComm,
        COALESCE(SUM(CAST(ISNULL(PGINTREALCOMM,   0) AS DECIMAL(38,4))), 0) AS PGIntRealComm,
        COALESCE(SUM(CAST(ISNULL(PGETC,            0) AS DECIMAL(38,4))), 0) AS PGEtc,
        COALESCE(SUM(CAST(ISNULL(PGVT,             0) AS DECIMAL(38,4))), 0) AS PGVT,
        COALESCE(SUM(CAST(ISNULL(PGTOTAL,          0) AS DECIMAL(38,4))), 0) AS PGTotal,
        COALESCE(SUM(CAST(ISNULL(POQINCOME,        0) AS DECIMAL(38,4))), 0) AS POQIncome,
        COALESCE(SUM(CAST(ISNULL(NonSettleAmt,     0) AS DECIMAL(38,4))), 0) AS NonSettleAmt,
        COALESCE(SUM(CAST(ISNULL(ExtraTxAmt,       0) AS DECIMAL(38,4))), 0) AS ExtraTxAmt,
        COALESCE(SUM(CAST(ISNULL(SeperateAmt,      0) AS DECIMAL(38,4))), 0) AS SeperateAmt,
        COALESCE(SUM(CAST(ISNULL(ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
),
ControlValues AS
(
    SELECT V.ControlName, V.ControlValue
    FROM Ledger AS L
    CROSS APPLY
    (
        VALUES
            (N'LedgerRowCount',    L.LedgerRowCount),
            (N'TxAmt',             L.TxAmt),
            (N'CLComm',            L.CLComm),
            (N'CLIntComm',         L.CLIntComm),
            (N'CLEtc',             L.CLEtc),
            (N'CLVT',              L.CLVT),
            (N'CLTotal',           L.CLTotal),
            (N'PGComm',            L.PGComm),
            (N'PGIntExpComm',      L.PGIntExpComm),
            (N'PGIntRealComm',     L.PGIntRealComm),
            (N'PGEtc',             L.PGEtc),
            (N'PGVT',              L.PGVT),
            (N'PGTotal',           L.PGTotal),
            (N'POQIncome',         L.POQIncome),
            (N'NonSettleAmt',      L.NonSettleAmt),
            (N'ExtraTxAmt',        L.ExtraTxAmt),
            (N'SeperateAmt',       L.SeperateAmt),
            (N'ForeignSettleAmt',  L.ForeignSettleAmt)
    ) AS V(ControlName, ControlValue)
)
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT
    @p_runId,
    N'S13',
    C.ControlName,
    C.ControlValue,
    @v_capturedAtUtc
FROM ControlValues AS C
WHERE NOT EXISTS
(
    SELECT 1
    FROM batch.BatchControlTotal AS E
    WHERE E.RunId = @p_runId
      AND E.StepCode = N'S13'
      AND E.ControlName = C.ControlName
);
```

재호출 시 이미 커밋된 동일 `(RunId, StepCode, ControlName)` 행을 중복 생성하지 않는다. 애플리케이션은 영향 행 수로 분기하지 않고 아래 검증 결과로 기존 값과 현재 원장 합계가 동일한지 확인한다.

```sql
-- SQL_VALIDATE_CAPTURE
;WITH Ledger AS
(
    SELECT
        CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
        COALESCE(SUM(CAST(ISNULL(TXAMT,            0) AS DECIMAL(38,4))), 0) AS TxAmt,
        COALESCE(SUM(CAST(ISNULL(CLCOMM,           0) AS DECIMAL(38,4))), 0) AS CLComm,
        COALESCE(SUM(CAST(ISNULL(CLINTCOMM,        0) AS DECIMAL(38,4))), 0) AS CLIntComm,
        COALESCE(SUM(CAST(ISNULL(CLETC,            0) AS DECIMAL(38,4))), 0) AS CLEtc,
        COALESCE(SUM(CAST(ISNULL(CLVT,             0) AS DECIMAL(38,4))), 0) AS CLVT,
        COALESCE(SUM(CAST(ISNULL(CLTOTAL,          0) AS DECIMAL(38,4))), 0) AS CLTotal,
        COALESCE(SUM(CAST(ISNULL(PGCOMM,           0) AS DECIMAL(38,4))), 0) AS PGComm,
        COALESCE(SUM(CAST(ISNULL(PGINTEXPCOMM,    0) AS DECIMAL(38,4))), 0) AS PGIntExpComm,
        COALESCE(SUM(CAST(ISNULL(PGINTREALCOMM,   0) AS DECIMAL(38,4))), 0) AS PGIntRealComm,
        COALESCE(SUM(CAST(ISNULL(PGETC,            0) AS DECIMAL(38,4))), 0) AS PGEtc,
        COALESCE(SUM(CAST(ISNULL(PGVT,             0) AS DECIMAL(38,4))), 0) AS PGVT,
        COALESCE(SUM(CAST(ISNULL(PGTOTAL,          0) AS DECIMAL(38,4))), 0) AS PGTotal,
        COALESCE(SUM(CAST(ISNULL(POQINCOME,        0) AS DECIMAL(38,4))), 0) AS POQIncome,
        COALESCE(SUM(CAST(ISNULL(NonSettleAmt,     0) AS DECIMAL(38,4))), 0) AS NonSettleAmt,
        COALESCE(SUM(CAST(ISNULL(ExtraTxAmt,       0) AS DECIMAL(38,4))), 0) AS ExtraTxAmt,
        COALESCE(SUM(CAST(ISNULL(SeperateAmt,      0) AS DECIMAL(38,4))), 0) AS SeperateAmt,
        COALESCE(SUM(CAST(ISNULL(ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0) AS ForeignSettleAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_ymd
),
CurrentValues AS
(
    SELECT V.ControlName, V.ControlValue
    FROM Ledger AS L
    CROSS APPLY
    (
        VALUES
            (N'LedgerRowCount',    L.LedgerRowCount),
            (N'TxAmt',             L.TxAmt),
            (N'CLComm',            L.CLComm),
            (N'CLIntComm',         L.CLIntComm),
            (N'CLEtc',             L.CLEtc),
            (N'CLVT',              L.CLVT),
            (N'CLTotal',           L.CLTotal),
            (N'PGComm',            L.PGComm),
            (N'PGIntExpComm',      L.PGIntExpComm),
            (N'PGIntRealComm',     L.PGIntRealComm),
            (N'PGEtc',             L.PGEtc),
            (N'PGVT',              L.PGVT),
            (N'PGTotal',           L.PGTotal),
            (N'POQIncome',         L.POQIncome),
            (N'NonSettleAmt',      L.NonSettleAmt),
            (N'ExtraTxAmt',        L.ExtraTxAmt),
            (N'SeperateAmt',       L.SeperateAmt),
            (N'ForeignSettleAmt',  L.ForeignSettleAmt)
    ) AS V(ControlName, ControlValue)
),
Captured AS
(
    SELECT
        ControlName,
        MAX(ControlValue) AS ControlValue,
        COUNT(*) AS ValueCount
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId
      AND StepCode = N'S13'
    GROUP BY ControlName
)
SELECT
    18 AS ExpectedControlCount,
    (
        SELECT COUNT(*)
        FROM batch.BatchControlTotal
        WHERE RunId = @p_runId
          AND StepCode = N'S13'
    ) AS ActualControlCount,
    (
        SELECT COUNT(DISTINCT ControlName)
        FROM batch.BatchControlTotal
        WHERE RunId = @p_runId
          AND StepCode = N'S13'
    ) AS DistinctControlCount,
    (
        SELECT COUNT(*)
        FROM CurrentValues AS V
        LEFT JOIN Captured AS C
          ON C.ControlName = V.ControlName
        WHERE C.ControlName IS NULL
           OR C.ValueCount <> 1
           OR C.ControlValue <> V.ControlValue
    ) AS MismatchCount;
```

#### 트랜잭션 및 복구

- 집계 조회, `batch.BatchControlTotal` 적재, 즉시 검증을 하나의 SNAPSHOT 트랜잭션에서 수행한다.
- 비청크 단계이므로 가짜 청크 키를 추가하지 않는다.
- 실패 시 열린 트랜잭션만 롤백한다. 롤백이 대상 변경을 복원하므로 섀도 테이블과 보상 `DELETE`를 사용하지 않는다.
- 커밋 전 검증 실패도 `-9130`과 `SQL_VALIDATE_CAPTURE` 문장 식별자로 기록하고 파이프라인을 중단한다.
- 커밋 이후에는 S20 검증이 완료될 때까지 동일 업무일자의 `SETTLE_POQ_DB.dbo.TSettleMst`를 변경하는 후속 단계를 실행하지 않는다.