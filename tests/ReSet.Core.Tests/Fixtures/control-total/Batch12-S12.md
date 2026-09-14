> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**
> 
> S12 (S12이(가) 대체하는 원본 UP_Util_Settle_Summary의 트랜잭션이 단계로 갈렸습니다(원본 라인 23~239). 그 안에서 실행되던 `UP_Util_Settle_Summary_AcqManual`가 별도 단계 S13로 분리됐는데, 그 피호출자는 자기 트랜잭션이 없어 언제나 이 트랜잭션 안에서 원자적으로 실행됐습니다. 두 단계가 각자 커밋하면 뒤 단계의 실패가 앞 단계를 되돌리지 못합니다. / S12이(가) 대체하는 원본 UP_Util_Settle_Summary의 트랜잭션이 단계로 갈렸습니다(원본 라인 23~239). 그 안에서 실행되던 `UP_UTIL_SETTLE_SUMMARY_EXTRA`가 별도 단계 S14로 분리됐는데, 그 피호출자는 자기 트랜잭션이 없어 언제나 이 트랜잭션 안에서 원자적으로 실행됐습니다. 두 단계가 각자 커밋하면 뒤 단계의 실패가 앞 단계를 되돌리지 못합니다.)
> 
> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다.

### S12 기본 요약 재구축

#### 역할과 실행 경계

`dbo.UP_Util_Settle_Summary`의 기본 DELETE 4건과 INSERT 4건을 C# 배치 애플리케이션이 순서대로 실행한다. 원본의 하위 프로시저 호출은 각각 S13과 S14로 분리하되, 실행 순서는 반드시 **S12 → S13 → S14**로 유지한다.

- 대상 테이블:
  - `SETTLE_POQ_DB.dbo.TSettleByTX`
  - `SETTLE_POQ_DB.dbo.TPartialCancelByTX`
  - `SETTLE_POQ_DB.dbo.TSettleByIN`
  - `SETTLE_POQ_DB.dbo.TSettleByOUT`
- S12가 시작한 S12~S14 복합 업무 트랜잭션은 **SNAPSHOT 격리**로 실행해야 한다.
- 네 대상 테이블을 순차 재구축하므로 병렬 실행하지 않는다.
- 전역 집계와 다중 대상 테이블 치환 때문에 청크 키를 도입하지 않는다. 실행 모드는 논리적 `Single-Transaction Shadow Swap`, 즉 단일 복합 트랜잭션 안의 DELETE/INSERT 원자 치환이며 물리 shadow 테이블은 생성하지 않는다.
- S12 완료 시에는 커밋하지 않는다. S13과 S14까지 성공한 후 S12·S13·S14의 성공 저널과 체크포인트를 같은 트랜잭션에서 확정하고 한 번만 커밋한다.
- S12 또는 후속 S13·S14 실패 시 복합 트랜잭션 전체를 롤백한다. shadow나 롤백 후 보상 DELETE는 사용하지 않는다.
- 원본의 모든 `NOLOCK` 힌트는 제거한다. 원천 `SETTLE_POQ_DB.dbo.TSettleMst`도 SNAPSHOT 일관성으로 읽는다.

#### 인터페이스와 반환 코드

- `@pi_strYMD CHAR(8) -> p_ymd`
  - 바인딩 메타데이터를 정확히 `CHAR(8)`로 지정한다.
- `@po_intRetVal INT OUTPUT -> stepResult.LegacyReturnCode`
  - 출력 파라미터는 SQL 입력으로 바인딩하지 않는다.
  - 성공 시 애플리케이션이 `0`을 반환한다.
  - 실패 시 현재 DML 직전에 기록한 원본 오류 코드를 반환하고 `batch.BatchStepJournal.LegacyReturnCode`에도 동일하게 기록한다.

| 원본 결과 또는 문장 | 반환 코드 |
|---|---:|
| 성공 | 0 |
| `DELETE 1` — `TSettleByTX` 삭제 실패 | -1 |
| `DELETE 2` — `TPartialCancelByTX` 삭제 실패 | -2 |
| `DELETE 3` — `TSettleByIN` 삭제 실패 | -3 |
| `DELETE 4` — `TSettleByOUT` 삭제 실패 | -4 |
| `INSERT 1` — `TSettleByTX` 적재 실패 | -5 |
| `INSERT 2` — `TPartialCancelByTX` 적재 실패 | -6 |
| `INSERT 3` — `TSettleByIN` 적재 실패 | -7 |
| `INSERT 4` — `TSettleByOUT` 적재 실패 | -8 |

S13과 S14의 반환 코드는 해당 단계가 소유하며 S12 코드로 변환하지 않는다.

#### 애플리케이션 실행 의사코드

```csharp
// 공개 업무 인터페이스는 원본과 동일하다.
// @pi_strYMD CHAR(8) -> p_ymd
// @po_intRetVal INT OUTPUT -> stepResult.LegacyReturnCode
//
// runId, stepCode 및 공유 트랜잭션은 배치 실행 컨텍스트에서 얻으며
// 재시작·건너뛰기·우회 입력 파라미터를 추가하지 않는다.

int? currentStepErrorCode = null;
string currentStatement = null;
int outputLegacyReturnCode;

conn = batchContext.connection;
runId = batchContext.runId;
stepCode = "S12";

// 공통 시작 저널 처리 후 S12가 S12~S14 공유 업무 트랜잭션을 연다.
// 이 트랜잭션은 SNAPSHOT 격리 의무를 만족해야 한다.
workTx = conn.beginTransaction();
batchContext.sharedWorkTx = workTx;

try
{
    currentStatement = "DELETE 1: TSettleByTX 당일 거래 요약 삭제";
    currentStepErrorCode = -1;
    repository.execute(conn, workTx, SQL_DELETE_SETTLE_BY_TX, {
        p_ymd: batchYmd
    });

    currentStatement = "DELETE 2: TPartialCancelByTX 당일 부분취소 요약 삭제";
    currentStepErrorCode = -2;
    repository.execute(conn, workTx, SQL_DELETE_PARTIAL_CANCEL_BY_TX, {
        p_ymd: batchYmd
    });

    currentStatement = "DELETE 3: TSettleByIN 당일 회수 요약 삭제";
    currentStepErrorCode = -3;
    repository.execute(conn, workTx, SQL_DELETE_SETTLE_BY_IN, {
        p_ymd: batchYmd
    });

    currentStatement = "DELETE 4: TSettleByOUT 당일 지급 요약 삭제";
    currentStepErrorCode = -4;
    repository.execute(conn, workTx, SQL_DELETE_SETTLE_BY_OUT, {
        p_ymd: batchYmd
    });

    currentStatement = "INSERT 1: TSettleByTX 기본 거래 요약 적재";
    currentStepErrorCode = -5;
    repository.execute(conn, workTx, SQL_INSERT_SETTLE_BY_TX, {
        p_ymd: batchYmd
    });

    currentStatement = "INSERT 2: TPartialCancelByTX 부분취소 요약 적재";
    currentStepErrorCode = -6;
    repository.execute(conn, workTx, SQL_INSERT_PARTIAL_CANCEL_BY_TX, {
        p_ymd: batchYmd
    });

    currentStatement = "INSERT 3: TSettleByIN 회수 요약 적재";
    currentStepErrorCode = -7;
    repository.execute(conn, workTx, SQL_INSERT_SETTLE_BY_IN, {
        p_ymd: batchYmd
    });

    currentStatement = "INSERT 4: TSettleByOUT 지급 요약 적재";
    currentStepErrorCode = -8;
    repository.execute(conn, workTx, SQL_INSERT_SETTLE_BY_OUT, {
        p_ymd: batchYmd
    });

    outputLegacyReturnCode = 0;

    // 여기서 커밋하거나 S12 체크포인트를 Succeeded로 바꾸지 않는다.
    // 동일 workTx를 사용하여 S13, S14를 순차 호출한다.
    // S14 검증 완료 직전에 S12 소유 성공 처리로 저널의 반환 코드를 0으로
    // 기록하고 체크포인트를 Succeeded로 갱신한 뒤 복합 트랜잭션을 커밋한다.
}
catch (failure)
{
    if (workTx is open)
        workTx.rollback();

    outputLegacyReturnCode = currentStepErrorCode;

    // 별도 SNAPSHOT 실패 기록 트랜잭션에서 S12가 생성한 저널만 갱신한다.
    // LegacyReturnCode에는 currentStepErrorCode를 기록하고,
    // ErrorMessage에는 currentStatement와 failure.message를 기록한다.
    markS12Failed(
        runId,
        currentStepErrorCode,
        currentStatement + failure.message
    );

    stop pipeline;
}
```

S12 DML은 다음 순서 그대로 실행한다.

```sql
-- SQL_DELETE_SETTLE_BY_TX
/* DELETE 1: 당일 거래 요약 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_DELETE_PARTIAL_CANCEL_BY_TX
/* DELETE 2: 당일 부분취소 요약 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_DELETE_SETTLE_BY_IN
/* DELETE 3: 당일 회수 요약 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleByIN
 WHERE YMD = @p_ymd;
```

```sql
-- SQL_DELETE_SETTLE_BY_OUT
/* DELETE 4: 당일 지급 요약 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleByOUT
 WHERE YMD = @p_ymd
   AND OUTSTATE IN (2, 9);
```

```sql
-- SQL_INSERT_SETTLE_BY_TX
/* INSERT 1: 기본 거래 요약 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX
(
    YMD,
    AYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    TXCNT,
    TXAMT,
    CLCOMM,
    CLINTCOMM,
    CLETC,
    CLVT,
    CLTOTAL,
    PGCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    PGETC,
    PGVT,
    PGTOTAL,
    POQINCOME,
    USESTATE,
    CompanySalesType,
    ExtraTxAmt,
    ProcYMD,
    SeperateAmt,
    ExtraSettleFlag
)
SELECT
    YMD,
    AYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    ISNULL(COUNT(*), 0),
    SUM(ISNULL(TXAMT, 0)),
    SUM(ISNULL(CLCOMM, 0)),
    SUM(ISNULL(CLINTCOMM, 0)),
    SUM(ISNULL(CLETC, 0)),
    SUM(ISNULL(CLVT, 0)),
    SUM(ISNULL(CLTOTAL, 0)),
    SUM(ISNULL(PGCOMM, 0)),
    SUM(ISNULL(PGINTEXPCOMM, 0)),
    SUM(ISNULL(PGINTREALCOMM, 0)),
    SUM(ISNULL(PGETC, 0)),
    SUM(ISNULL(PGVT, 0)),
    SUM(ISNULL(PGTOTAL, 0)),
    SUM(ISNULL(POQINCOME, 0)),
    USESTATE,
    CompanySalesType,
    SUM(ISNULL(ExtraTxAmt, 0)),
    ProcYMD,
    SUM(ISNULL(SeperateAmt, 0)),
    ExtraSettleFlag
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_ymd
GROUP BY
    YMD,
    AYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    USESTATE,
    CompanySalesType,
    ProcYMD,
    ExtraSettleFlag;
```

```sql
-- SQL_INSERT_PARTIAL_CANCEL_BY_TX
/* INSERT 2: 부분취소 거래 요약 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TPartialCancelByTX
(
    YMD,
    AYMD,
    PLTID,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    TXCNT,
    TXAMT,
    CLCOMM,
    CLINTCOMM,
    CLETC,
    CLVT,
    CLTOTAL,
    PGCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    PGETC,
    PGVT,
    PGTOTAL,
    POQINCOME,
    USESTATE,
    CompanySalesType,
    ExtraTxAmt,
    ProcYMD,
    SeperateAmt,
    ExtraSettleFlag
)
SELECT
    YMD,
    AYMD,
    PLTID,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    ISNULL(COUNT(*), 0),
    SUM(ISNULL(TXAMT, 0)),
    SUM(ISNULL(CLCOMM, 0)),
    SUM(ISNULL(CLINTCOMM, 0)),
    SUM(ISNULL(CLETC, 0)),
    SUM(ISNULL(CLVT, 0)),
    SUM(ISNULL(CLTOTAL, 0)),
    SUM(ISNULL(PGCOMM, 0)),
    SUM(ISNULL(PGINTEXPCOMM, 0)),
    SUM(ISNULL(PGINTREALCOMM, 0)),
    SUM(ISNULL(PGETC, 0)),
    SUM(ISNULL(PGVT, 0)),
    SUM(ISNULL(PGTOTAL, 0)),
    SUM(ISNULL(POQINCOME, 0)),
    USESTATE,
    CompanySalesType,
    SUM(ISNULL(ExtraTxAmt, 0)),
    ProcYMD,
    SUM(ISNULL(SeperateAmt, 0)),
    ExtraSettleFlag
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_ymd
  AND USESTATE = 2
GROUP BY
    YMD,
    AYMD,
    PLTID,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    USESTATE,
    CompanySalesType,
    ProcYMD,
    ExtraSettleFlag;
```

```sql
-- SQL_INSERT_SETTLE_BY_IN
/* INSERT 3: 자동회수 요약 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleByIN
(
    YMD,
    AYMD,
    INYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    INCNT,
    TXAMT,
    CLCOMM,
    CLINTCOMM,
    CLETC,
    CLVT,
    CLTOTAL,
    PGCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    PGETC,
    PGVT,
    PGTOTAL,
    POQINCOME,
    USESTATE,
    CompanySalesType,
    ExtraTxAmt,
    ProcYMD,
    SeperateAmt,
    ExtraSettleFlag
)
SELECT
    YMD,
    AYMD,
    INYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    COUNT(*),
    SUM(ISNULL(TXAMT, 0)),
    SUM(ISNULL(CLCOMM, 0)),
    SUM(ISNULL(CLINTCOMM, 0)),
    SUM(ISNULL(CLETC, 0)),
    SUM(ISNULL(CLVT, 0)),
    SUM(ISNULL(CLTOTAL, 0)),
    SUM(ISNULL(PGCOMM, 0)),
    SUM(ISNULL(PGINTEXPCOMM, 0)),
    SUM(ISNULL(PGINTREALCOMM, 0)),
    SUM(ISNULL(PGETC, 0)),
    SUM(ISNULL(PGVT, 0)),
    SUM(ISNULL(PGTOTAL, 0)),
    SUM(ISNULL(POQINCOME, 0)),
    USESTATE,
    CompanySalesType,
    SUM(ISNULL(ExtraTxAmt, 0)),
    ProcYMD,
    SUM(ISNULL(SeperateAmt, 0)),
    ExtraSettleFlag
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_ymd
  AND INSTATE = 1
GROUP BY
    YMD,
    AYMD,
    INYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    USESTATE,
    CompanySalesType,
    ProcYMD,
    ExtraSettleFlag;
```

```sql
-- SQL_INSERT_SETTLE_BY_OUT
/* INSERT 4: 자동회수 지급 요약 적재 */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleByOUT
(
    YMD,
    AYMD,
    INYMD,
    OUTYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    OUTCNT,
    TXAMT,
    CLCOMM,
    CLINTCOMM,
    CLETC,
    CLVT,
    CLTOTAL,
    PGCOMM,
    PGINTEXPCOMM,
    PGINTREALCOMM,
    PGETC,
    PGVT,
    PGTOTAL,
    POQINCOME,
    USESTATE,
    OUTSTATE,
    SettleCurrency,
    ForeignSettleAmt,
    CompanySalesType,
    ExtraTxAmt,
    ProcYMD,
    SeperateAmt,
    ExtraSettleFlag
)
SELECT
    YMD,
    AYMD,
    INYMD,
    OUTYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    ISNULL(COUNT(*), 0),
    SUM(ISNULL(TXAMT, 0)),
    SUM(ISNULL(CLCOMM, 0)),
    SUM(ISNULL(CLINTCOMM, 0)),
    SUM(ISNULL(CLETC, 0)),
    SUM(ISNULL(CLVT, 0)),
    SUM(ISNULL(CLTOTAL, 0)),
    SUM(ISNULL(PGCOMM, 0)),
    SUM(ISNULL(PGINTEXPCOMM, 0)),
    SUM(ISNULL(PGINTREALCOMM, 0)),
    SUM(ISNULL(PGETC, 0)),
    SUM(ISNULL(PGVT, 0)),
    SUM(ISNULL(PGTOTAL, 0)),
    SUM(ISNULL(POQINCOME, 0)),
    USESTATE,
    OUTSTATE,
    SettleCurrency,
    SUM(ISNULL(ForeignSettleAmt, 0)),
    CompanySalesType,
    SUM(ISNULL(ExtraTxAmt, 0)),
    ProcYMD,
    SUM(ISNULL(SeperateAmt, 0)),
    ExtraSettleFlag
FROM SETTLE_POQ_DB.dbo.TSettleMst
WHERE YMD = @p_ymd
  AND OUTSTATE IN (2, 9)
GROUP BY
    YMD,
    AYMD,
    INYMD,
    OUTYMD,
    CLIENTID,
    PGNAME,
    MALLID,
    SERVICENAME,
    PRODUCTNAME,
    USESTATE,
    OUTSTATE,
    SettleCurrency,
    CompanySalesType,
    ProcYMD,
    ExtraSettleFlag;
```

#### 재시작 및 실패 처리

- S12 체크포인트가 이미 `Succeeded`이면 공통 오케스트레이터가 S12를 호출하지 않는다.
- S12가 호출된 경우 네 DELETE와 네 INSERT를 항상 전부 수행한다.
- S12 자체 DML 실패 시 `-1`, `-2`, `-3`, `-4`, `-5`, `-6`, `-7`, `-8` 중 정확한 실패 문장 코드를 기록한다.
- 모든 S12 DML이 성공하면 출력 결과는 `0`이지만, S14 완료 전까지 S12 저널은 최종 성공 처리하지 않고 체크포인트도 `Pending`으로 유지한다.
- S13 또는 S14 실패로 복합 트랜잭션이 롤백된 경우 S12 업무 변경도 모두 원상복구된다. 이때 실제 하위 실패 코드는 실패한 단계의 저널에 기록하며, S12에는 하위 단계 코드를 재매핑하지 않는다.
- 복합 트랜잭션 롤백 후 `SETTLE_POQ_DB.dbo.TSettleByTX`, `SETTLE_POQ_DB.dbo.TPartialCancelByTX`, `SETTLE_POQ_DB.dbo.TSettleByIN`, `SETTLE_POQ_DB.dbo.TSettleByOUT`에 추가 DELETE나 복구 INSERT를 실행하지 않는다.