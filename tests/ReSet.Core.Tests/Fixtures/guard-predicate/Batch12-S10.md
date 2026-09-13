### S10 — 비원천카드 차액정산 삽입

**레거시 원본:** `dbo.UP_UTIL_SETTLE_INS_EXTRA`  
**업무 대상:** `SETTLE_POQ_DB.dbo.TSettleMst`

S09 완료 후 S10을 실행하고, S10 성공 후 S11로 진행한다. S04부터 S08, S10, S11이 동일한 `SETTLE_POQ_DB.dbo.TSettleMst`를 변경하므로 병렬 실행하지 않는다.

#### 인터페이스 및 실행 계약

- `@pi_strYMD CHAR(8) -> p_ymd`
  - 바인딩 계층은 `p_ymd`를 유니코드 가변 문자열이 아니라 정확히 `CHAR(8)`로 전송해야 한다.
- `@po_intRetVal INT OUT -> outputRetVal`
  - 업무 SQL의 입력 바인딩으로 전달하지 않는다.
  - 사전 검증 실패 시 `-9`, 문장 실패 시 해당 원본 오류 코드를 반환한다.
  - 레거시 성공 경로에는 명시적 대입이 없으므로 성공 시 호출자가 제공한 초기값을 그대로 유지한다. 호출자가 `0`으로 초기화했다면 성공 결과도 `0`이다.
- 재시작·우회·사전 검증 생략 파라미터를 추가하지 않는다. 호출된 경우 지급확정 데이터 검증을 항상 수행한다.
- 단계 전체는 **SNAPSHOT 격리**의 단일 업무 트랜잭션으로 실행한다. `PaymentDB.dbo.TExtraSettleIn`과 `SETTLE_POQ_DB` 참조가 같은 일관된 스냅샷을 읽을 수 있어야 한다.
- 청크 실행은 적용하지 않는다. 교차 데이터베이스 원천, 다중 요율 조인, 선삭제 후 삽입 및 연속 갱신을 하나의 독립 청크 키로 분리할 수 없으며 승인된 `Chunkable` 값도 `False`이다.
- 단일 트랜잭션 롤백으로 `SETTLE_POQ_DB.dbo.TSettleMst`가 원상 복구되므로 shadow 테이블과 실패 후 보상 `DELETE`를 사용하지 않는다.
- 모든 원본 `NOLOCK` 힌트는 제거한다. SNAPSHOT 격리 중 `NOLOCK`을 병용하지 않는다.
- 연결의 기본 데이터베이스 문맥은 `SETTLE_POQ_DB`로 고정하여 `dbo.UF_GET_ROUND4VAT`, `dbo.UF_GET_INCVTAXRATE`, `dbo.UIF_SettleYMD`, `dbo.UF_Get_WorkDay2`가 원본과 같은 데이터베이스 객체로 해석되게 한다.

#### 오류 코드 추적

| 실패 지점 | `LegacyReturnCode` / `outputRetVal` |
|---|---:|
| 지급확정 또는 지급완료 데이터 사전 검증 | -9 |
| DELETE 1 | -1 |
| INSERT 1 | 원본 코드 없음, `NULL` |
| UPDATE 1 | -2 |
| UPDATE 2 | -3 |
| UPDATE 3 | 원본 코드 없음, `NULL` |
| UPDATE 4 | -4 |
| UPDATE 5 | -21 |

INSERT 1과 UPDATE 3에는 원본 오류 코드가 없으므로 새 코드를 만들지 않는다. 이 두 문장 직전에는 이전 문장의 코드가 잘못 기록되지 않도록 `currentStepErrorCode`를 `null`로 재설정하고, 정확한 문장 식별자를 `ErrorMessage`에 남긴다.

```csharp
const string stepCode = "S10";

int? currentStepErrorCode = null;
string currentStatement = null;
int? outputRetVal = callerProvidedOutputRetVal;

// 공통 시작 처리에서 S10의 batch.BatchStepJournal 및
// batch.BatchCheckpoint 행을 먼저 생성한다.

var inputBindings = typedBindings(
    p_ymd: bind(batchYmd, databaseType: "CHAR(8)")
);

var workTx = conn.beginTransaction(); // SNAPSHOT 의무

try
{
    currentStatement = "SELECT 1";
    currentStepErrorCode = null;

    var context = repository.queryRow(
        conn,
        workTx,
        SQL_LOAD_CONTEXT,
        inputBindings
    );

    // SQL에서 계산된 지역 변수 결과를 이후 문장에 전달한다.
    // 두 값 모두 원본 지역 변수 타입인 VARCHAR(8)로 바인딩한다.
    var businessBindings = typedBindings(
        p_ymd: bind(batchYmd, databaseType: "CHAR(8)"),
        p_reqYmd: bind(context.ReqYMD, databaseType: "VARCHAR(8)"),
        p_currYmd: bind(context.CurrYMD, databaseType: "VARCHAR(8)")
    );

    // 호출될 때마다 무조건 수행한다. SQL은 행만 반환하고,
    // 중단 여부와 -9 반환은 애플리케이션이 판정한다.
    currentStatement = "IF 1 지급확정 데이터 사전 검증";
    currentStepErrorCode = null;

    var settledRow = repository.queryRowOrNone(
        conn,
        workTx,
        SQL_CHECK_SETTLED_LEDGER,
        businessBindings
    );

    if (settledRow.exists)
    {
        currentStepErrorCode = -9;
        outputRetVal = -9;
        workTx.rollback();

        var failureTx = conn.beginTransaction(); // SNAPSHOT 의무
        repository.execute(conn, failureTx, SQL_JOURNAL_FAILED, {
            p_runId: runId,
            p_stepCode: stepCode,
            p_legacyReturnCode: currentStepErrorCode,
            p_errorMessage: "IF 1 지급확정 또는 지급완료 데이터가 존재함"
        });
        failureTx.commit();

        stopPipeline();
        return;
    }

    currentStatement = "DELETE 1";
    currentStepErrorCode = -1;
    repository.execute(conn, workTx, SQL_DELETE_EXISTING, businessBindings);

    currentStatement = "INSERT 1";
    currentStepErrorCode = null;
    repository.execute(conn, workTx, SQL_INSERT_EXTRA_SETTLEMENT, businessBindings);

    currentStatement = "UPDATE 1";
    currentStepErrorCode = -2;
    repository.execute(conn, workTx, SQL_UPDATE_EXPECTED_OUT_YMD, businessBindings);

    currentStatement = "UPDATE 2";
    currentStepErrorCode = -3;
    repository.execute(conn, workTx, SQL_UPDATE_OVERDUE_OUT_YMD, businessBindings);

    currentStatement = "UPDATE 3";
    currentStepErrorCode = null;
    repository.execute(conn, workTx, SQL_REVERSE_COMMISSION_SIGNS, businessBindings);

    currentStatement = "UPDATE 4";
    currentStepErrorCode = -4;
    repository.execute(conn, workTx, SQL_RECALCULATE_TOTALS, businessBindings);

    currentStatement = "UPDATE 5";
    currentStepErrorCode = -21;
    repository.execute(conn, workTx, SQL_RECALCULATE_INCLUDED_VAT, businessBindings);

    // 성공 시 레거시 출력값은 호출자가 제공한 초기값을 유지한다.
    currentStatement = "S10 성공 저널 갱신";
    currentStepErrorCode = null;
    repository.execute(conn, workTx, SQL_JOURNAL_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode,
        p_legacyReturnCode: outputRetVal
    });

    currentStatement = "S10 체크포인트 갱신";
    currentStepErrorCode = null;
    repository.execute(conn, workTx, SQL_CHECKPOINT_SUCCEEDED, {
        p_runId: runId,
        p_stepCode: stepCode
    });

    workTx.commit();
}
catch (failure)
{
    if (workTx is open)
        workTx.rollback();

    outputRetVal = currentStepErrorCode;

    var failureTx = conn.beginTransaction(); // SNAPSHOT 의무
    repository.execute(conn, failureTx, SQL_JOURNAL_FAILED, {
        p_runId: runId,
        p_stepCode: stepCode,
        p_legacyReturnCode: currentStepErrorCode,
        p_errorMessage: currentStatement + ": " + failure.message
    });
    failureTx.commit();

    stopPipeline();
}
```

#### 애플리케이션 전송 SQL

원본에서 초기값과 함께 선언된 `@v_strReqYMD`, `@v_strCurrYMD`는 다음 SQL 안에 선언 타입과 초기값을 그대로 유지한다. `MIN(ReqYMD)`는 대상 행이 없어도 `NULL`을 대입하는 원본 집계 의미를 보존한다.

```sql
-- SQL_LOAD_CONTEXT
DECLARE @v_strReqYMD VARCHAR(8) = '';
DECLARE @v_strCurrYMD VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112);

/* SELECT 1: 차액정산 요청일 조회 */
SELECT @v_strReqYMD = MIN(ReqYMD)
  FROM PaymentDB.dbo.TExtraSettleIn
 WHERE ResYMD = @p_ymd
   AND ResultCode = '00'
   AND RefundTxType <> 1;

SELECT @v_strReqYMD AS ReqYMD,
       @v_strCurrYMD AS CurrYMD;
```

사전 검증은 원본의 `IF EXISTS`를 애플리케이션 판정용 행 조회로 변환한다. 명세서 DML 범위 표에 별도 문장 행이 없는 `IF 1`이므로 DML 앵커를 추가하지 않는다.

```sql
-- SQL_CHECK_SETTLED_LEDGER
SELECT TOP (1)
       PLTID
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND OutState IN (1, 5)
   AND OutYMD <= @p_currYmd
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND ExtraSettleFlag = 1;
```

```sql
-- SQL_DELETE_EXISTING
/* DELETE 1: 기존 비원천카드 차액정산 데이터 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;
```

명세서의 INSERT 대상 표에 기재된 `X.PRODUCTNAME`은 파생 테이블 원천 표기이며, 유효한 대상 컬럼 식별자는 물리 컬럼 `PRODUCTNAME`이다. 포지셔널 매핑과 값 `'영중소차액정산'`은 그대로 유지한다. 실제 산출에 사용되지 않는 `TClientContract E` 조인도 원본 조인 구조 보존을 위해 제거하지 않는다.

```sql
-- SQL_INSERT_EXTRA_SETTLEMENT
/* INSERT 1: 비원천카드 차액정산 원장 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
(
    YMD,
    CYMD,
    AYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    PLTID,
    TID,
    CID,
    PAYERID,
    PAYERNAME,
    SERVICENAME,
    PRODUCTNAME,
    PGVTTYPE,
    CLVTTYPE,
    ABROADCHK,
    CompanySalesType,
    USESTATE,
    NonSettleAmt,
    ExtraTxAmt,
    TXAMT,
    CLCOMMTYPE,
    PGCOMMTYPE,
    CLETC,
    PGETC,
    CLTOTAL,
    PGTOTAL,
    POQINCOME,
    CLINTCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    CLCOMM,
    CLVT,
    PGCOMM,
    PGVT,
    INSTATE,
    INYMD,
    ProcYMD,
    ProcState,
    ExtraSettleFlag
)
SELECT
    X.YMD,
    X.CYMD,
    X.AYMD,
    X.CLIENTID,
    X.PGNAME,
    X.MALLID,
    X.PLTID,
    X.TID,
    X.CID,
    X.PAYERID,
    X.PAYERNAME,
    X.SERVICENAME,
    X.PRODUCTNAME,
    X.PGINCVTAX,
    X.CLINCVTAX,
    X.ABROADCHK,
    X.CompanySalesType,
    X.USESTATE,
    X.NonSettleAmt,
    X.ExtraTxAmt,
    X.TXAMT,
    0 AS CLCOMMTYPE,
    0 AS PGCOMMTYPE,
    0 AS CLETC,
    0 AS PGETC,
    0 AS CLTOTAL,
    0 AS PGTOTAL,
    0 AS POQINCOME,
    0 AS CLINTCOMM,
    0 AS PGINTEXPCOMM,
    0 AS PGINTREALCOMM,
    CASE
        WHEN X.AYMD < '20190501' THEN 0
        ELSE CAST(ISNULL(X.CLCOMM, 0) AS INT)
    END AS CLCOMM,
    CASE
        WHEN X.AYMD < '20190501' THEN 0
        ELSE dbo.UF_GET_ROUND4VAT
             (
                 ISNULL(X.CLCOMM, 0)
                 * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)
             )
    END AS CLVT,
    CASE
        WHEN Y.CommMethod = 0
            THEN CAST
                 (
                     ROUND(X.PGCOMM, 0, Y.CommRoundFlag)
                     AS INT
                 )
        ELSE CAST
             (
                 ROUND
                 (
                     ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1,
                     0,
                     Y.CommRoundFlag
                 )
                 AS INT
             )
    END AS PGCOMM,
    CASE
        WHEN Y.CommMethod = 0
            THEN CAST
                 (
                     ROUND
                     (
                         X.PGCOMM
                         * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax),
                         0,
                         Y.VatRoundFlag
                     )
                     AS INT
                 )
        ELSE CAST
             (
                 ROUND
                 (
                     ROUND
                     (
                         ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag),
                         0,
                         Y.CommRoundFlag
                     )
                     -
                     ROUND
                     (
                         ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1,
                         0,
                         Y.CommRoundFlag
                     ),
                     0,
                     Y.VatRoundFlag
                 )
                 AS INT
             )
    END AS PGVT,
    X.INSTATE,
    X.INYMD,
    X.ProcYMD,
    CASE
        WHEN X.CLCOMM IS NULL OR X.PGCOMM IS NULL THEN 1
        ELSE NULL
    END AS ProcState,
    1 AS ExtraSettleFlag
FROM
(
    SELECT
        A.ReqYMD AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD) AS CYMD,
        A.OrgYMD AS AYMD,
        A.ResYMD AS ProcYMD,
        A.CLIENTID,
        A.PGNAME,
        A.MID AS MALLID,
        A.PLTID,
        A.TID,
        A.SeqNo AS CID,
        '' AS PAYERID,
        '' AS PAYERNAME,
        '' AS SERVICENAME,
        '영중소차액정산' AS PRODUCTNAME,
        ISNULL(B.incVTax, 0) AS PGINCVTAX,
        ISNULL(C.incVTax, 0) AS CLINCVTAX,
        0 AS ABROADCHK,
        A.CompanySalesType,
        A.USESTATE,
        0 AS NonSettleAmt,
        0 AS TXAMT,
        A.TxAmt AS ExtraTxAmt,
        ISNULL
        (
            CASE
                WHEN ISNULL(A.CompanySalesType, 4) = 0
                    THEN CAST
                         (
                             A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0
                             AS INT
                         )
                WHEN ISNULL(A.CompanySalesType, 4) = 1
                    THEN CAST
                         (
                             A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0
                             AS INT
                         )
                WHEN ISNULL(A.CompanySalesType, 4) = 2
                    THEN CAST
                         (
                             A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0
                             AS INT
                         )
                WHEN ISNULL(A.CompanySalesType, 4) = 3
                    THEN CAST
                         (
                             A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0
                             AS INT
                         )
                ELSE 0
            END,
            0
        ) * IIF(C.ExtraSettleFlag = 'Y', 1, 0) AS CLComm,
        ISNULL
        (
            CASE
                WHEN A.PGName = 'dacomcard'
                  OR A.PGName = 'tosscard'
                    THEN
                        CASE
                            WHEN ISNULL(A.CompanySalesType, 4) = 0
                                THEN
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                        AS INT
                                    )
                                    -
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommRate0 * 1.1) / 100.0)
                                        AS INT
                                    )
                            WHEN ISNULL(A.CompanySalesType, 4) = 1
                                THEN
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                        AS INT
                                    )
                                    -
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommRate1 * 1.1) / 100.0)
                                        AS INT
                                    )
                            WHEN ISNULL(A.CompanySalesType, 4) = 2
                                THEN
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                        AS INT
                                    )
                                    -
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommRate2 * 1.1) / 100.0)
                                        AS INT
                                    )
                            WHEN ISNULL(A.CompanySalesType, 4) = 3
                                THEN
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)
                                        AS INT
                                    )
                                    -
                                    CAST
                                    (
                                        A.TxAmt * ((B.CommRate3 * 1.1) / 100.0)
                                        AS INT
                                    )
                            ELSE 0
                        END
                ELSE
                    CASE
                        WHEN ISNULL(A.CompanySalesType, 4) = 0
                            THEN CAST
                                 (
                                     A.TxAmt * (B.CommissionRate - B.CommRate0) / 100.0
                                     AS INT
                                 )
                        WHEN ISNULL(A.CompanySalesType, 4) = 1
                            THEN CAST
                                 (
                                     A.TxAmt * (B.CommissionRate - B.CommRate1) / 100.0
                                     AS INT
                                 )
                        WHEN ISNULL(A.CompanySalesType, 4) = 2
                            THEN CAST
                                 (
                                     A.TxAmt * (B.CommissionRate - B.CommRate2) / 100.0
                                     AS INT
                                 )
                        WHEN ISNULL(A.CompanySalesType, 4) = 3
                            THEN CAST
                                 (
                                     A.TxAmt * (B.CommissionRate - B.CommRate3) / 100.0
                                     AS INT
                                 )
                        ELSE 0
                    END
            END,
            0
        ) AS PGComm,
        1 AS INSTATE,
        A.ExtraSettleYMD AS INYMD
    FROM PaymentDB.dbo.TExtraSettleIn AS A
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGSettleRate4Extra AS B
      ON B.YMD = A.OrgYMD
     AND B.PGNAME = A.PGNAME
     AND B.MALLID = A.MID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4Extra AS C
      ON C.YMD = A.OrgYMD
     AND C.PGNAME = A.PGNAME
     AND C.MALLID = A.MID
     AND C.CLIENTID = A.CLIENTID
    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TClientContract AS E
      ON E.CLIENTID = A.CLIENTID
    WHERE A.ResYMD = @p_ymd
      AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
      AND A.ResultCode = '00'
      AND A.CompanySalesType IN (0, 1, 2, 3)
      AND A.RefundTxType = 0
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty AS Y
  ON Y.PGName = X.PGNAME;
```

`OutYMD` 산정은 무결과 시 `NULL`을 대입하는 원본 스칼라 하위질의를 유지한다. 이를 `CROSS APPLY`로 변경하지 않는다.

```sql
-- SQL_UPDATE_EXPECTED_OUT_YMD
/* UPDATE 1: 지급상태 및 지급예정일 산정 */
UPDATE A
   SET A.OutState = 2,
       A.OutYMD =
       (
           SELECT OutYMD
             FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID)
       )
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS C
    ON C.PGNAME = A.PGNAME
   AND C.MALLID = A.MALLID
   AND C.CLIENTID = A.CLIENTID
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;
```

```sql
-- SQL_UPDATE_OVERDUE_OUT_YMD
/* UPDATE 2: 당일 이전 지급예정일 보정 */
UPDATE A
   SET A.OutYMD = dbo.UF_Get_WorkDay2(@p_currYmd, 2)
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
 WHERE A.ProcYMD = @p_ymd
   AND A.YMD >= @p_reqYmd
   AND A.PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.OutState = 2
   AND A.OutYMD <= @p_currYmd;
```

UPDATE 3의 아홉 개 우변은 모두 갱신 전 값을 기준으로 동시에 평가되어야 한다. 컬럼별 개별 UPDATE로 분할하지 않는다.

```sql
-- SQL_REVERSE_COMMISSION_SIGNS
/* UPDATE 3: 비원천카드 차액정산 수수료 부호 반전 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLCOMM = CLCOMM * (-1),
       CLINTCOMM = CLINTCOMM * (-1),
       CLVT = CLVT * (-1),
       CLETC = CLETC * (-1),
       PGCOMM = PGCOMM * (-1),
       PGINTEXPCOMM = PGINTEXPCOMM * (-1),
       PGINTREALCOMM = PGINTREALCOMM * (-1),
       PGVT = PGVT * (-1),
       PGETC = PGETC * (-1)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND USESTATE IN (0)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;
```

```sql
-- SQL_RECALCULATE_TOTALS
/* UPDATE 4: 고객사 및 PG 합계와 POQ 수익 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTOTAL =
           CLCOMM + CLVT + CLETC + CLINTCOMM,
       PGTOTAL =
           PGCOMM + PGVT + PGETC
           + CASE PGINTREALCOMM
                 WHEN 0 THEN PGINTEXPCOMM
                 ELSE PGINTREALCOMM
             END,
       POQINCOME =
           CLCOMM + CLVT + CLETC + CLINTCOMM
           -
           (
               PGCOMM + PGVT + PGETC
               + CASE PGINTREALCOMM
                     WHEN 0 THEN PGINTEXPCOMM
                     ELSE PGINTREALCOMM
                 END
           )
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND ExtraSettleFlag = 1;
```

`@v_valIncVat`는 바인딩하지 않고 원본 선언 타입 `DECIMAL(2,1)`과 초기값 `1.1`을 SQL 안에 유지한다. UPDATE 5의 모든 우변도 갱신 전 `CLComm`, `CLEtc`, `CLIntComm`, `PGTotal`을 기준으로 동시에 계산한다.

```sql
-- SQL_RECALCULATE_INCLUDED_VAT
DECLARE @v_valIncVat DECIMAL(2,1) = 1.1;

/* UPDATE 5: 부가세 포함 고객사 수수료 재계산 */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CLTotal =
           CLComm + CLEtc + CLIntComm,
       CLComm =
           CAST(CLComm / @v_valIncVat AS INT)
           + CAST(CLEtc / @v_valIncVat AS INT)
           + CAST(CLIntComm / @v_valIncVat AS INT),
       CLVT =
           CLComm + CLEtc + CLIntComm
           -
           (
               CAST(CLComm / @v_valIncVat AS INT)
               + CAST(CLEtc / @v_valIncVat AS INT)
               + CAST(CLIntComm / @v_valIncVat AS INT)
           ),
       POQIncome =
           CLComm + CLEtc + CLIntComm - PGTotal
 WHERE ProcYMD = @p_ymd
   AND YMD >= @p_reqYmd
   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
   AND ISNULL(CompanySalesType, 4) IN (0, 1, 2, 3)
   AND TxAmt = 0
   AND CLVTType = 1
   AND ExtraSettleFlag = 1;
```