### S06 | PLCard 전용 추가정산 원장 반영

#### 개요 및 원본 매핑

- 원본 프로시저: `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD` (`SETTLE_POQ_DB`)
- 인터페이스: `@pi_strYMD CHAR(8)` (INPUT, 입금일) · `@po_intRetVal INT` (OUTPUT, 결과 코드) — 원본 파라미터 목록 그대로이며, 재시작/스킵용 파라미터는 추가하지 않는다(규칙 5).
- 대상 테이블: `SETTLE_POQ_DB.dbo.TSettleMst` (원본 DELETE 1 · INSERT 1 · UPDATE 1 · UPDATE 2 모두 이 한 테이블만을 대상으로 한다).
- 원본 오류 코드: `-9`(사전 정산 데이터 존재 가드), `-1`(DELETE 1 실패), `-2`(UPDATE 2 이후 `@@ERROR` 검사 실패 — INSERT 1·UPDATE 1의 실패도 이 지점에서 관측되므로 같은 코드를 재사용한다). 새 코드를 만들거나 연속 범위로 바꾸지 않는다(규칙 9).
- 사전 가드는 규칙 5에 따라 매 호출마다 조건 없이 실행된다.

#### 청크 전략과 UPDATE 문의 비청크 처리

- **DELETE 1·INSERT 1**은 원본 필터(ProcYMD, YMD, CompanySalesType, TxAmt, ExtraSettleFlag, PG.ExtraType IN (2,3) / A.REQYMD, EDICheckFlag, RecordGB, CompanySalesType, P.ExtraType IN (2,3))에 `PLTID` 범위 조건을 `AND`로 결합해 청크 처리한다. `PLTID`는 `TSettleMst`(POQ 거래번호) 및 원천 `TExtraTxMst`(A.PLTID) 양쪽에 실제로 존재하는 컬럼이므로 규칙 12를 충족한다.
- **UPDATE 1(부호 반전)·UPDATE 2(TOTAL 산출)**에는 명세서 DML 범위 표가 확정한 술어 컬럼(`ProcYMD, YMD, CompanySalesType, USESTATE, TxAmt, ExtraSettleFlag` 및 `ProcYMD, YMD, CompanySalesType, TxAmt, ExtraSettleFlag`) 외에 `PLTID`를 포함한 어떤 컬럼도 추가하지 않는다. 이 두 UPDATE는 특정 청크 범위가 아니라 **그 배치일자 전체의 신규 삽입분**(TxAmt=0, ExtraSettleFlag=1로 식별됨)을 한 번에 갱신하는 원본 의미를 그대로 보존해야 하므로, DELETE·INSERT 청크 루프가 전부 커밋된 뒤 각각 단일 트랜잭션으로 1회씩 실행한다. SET 우변(CLCOMM, CLINTCOMM, CLVT, CLETC, PGCOMM, PGINTEXPCOMM, PGINTREALCOMM, PGVT, PGETC)은 갱신 이전 값을 동시에 참조하므로, 애플리케이션 코드에서 순차 대입으로 옮기지 않고 SQL SET 절 그대로 한 문장에서 평가되게 한다.

#### Shadow 및 트랜잭션/복구 정책

- 이 단계는 DELETE-INSERT를 청크 커밋하므로(규칙 4), 단일 트랜잭션 롤백만으로는 이전 청크의 삭제·삽입을 되돌릴 수 없다. 따라서 **청크 루프 시작 전에** `SETTLE_POQ_DB.dbo.TSettleMst`의 원본 DELETE 1 대상 범위 전체를 섀도우로 캡처한다.
- UPDATE 1·UPDATE 2는 각각 단일 트랜잭션이므로 그 자체 실패는 각 트랜잭션 롤백만으로 복구되지만, DELETE-INSERT 청크가 이미 커밋된 뒤에 UPDATE 1/2가 실패하면 원본 절차의 "전체 트랜잭션 롤백" 의미를 보존하기 위해 캡처해둔 섀도우로 `TSettleMst` 전체를 복원한다.
- 섀도우 테이블은 캡처 후 24시간 뒤 부트스트랩 정리 작업이 자동 삭제한다.

```pseudocode
// S06: PLCard 전용 추가정산 원장 반영
currentStepErrorCode = NULL
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, "S06", status: "Running", legacyReturnCode: NULL)

// (규칙 5) 사전 정산 데이터 존재 가드 - 조건 없이 매 호출 실행
alreadySettled = queryScalar(SQL_PRECHECK_EXISTS, { p_batchYmd: batchYmd })
IF alreadySettled == 1:
    currentStepErrorCode = -9
    writeStepJournal(runId, "S06", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline

// (규칙 4a) 롤백 가능한 트랜잭션을 열기 전, 그 밖에서 섀도우 캡처
execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_batchYmd: batchYmd })
shadowCaptured = true

// DELETE 1 + INSERT 1: PLTID 청크 단위로 커밋
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = -1                     // DELETE 1의 원본 코드
    execute(SQL_DELETE_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    currentStepErrorCode = -2                     // INSERT 1은 UPDATE 2 시점 검사로 귀속되는 원본 코드
    execute(SQL_INSERT_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()

// UPDATE 1 · UPDATE 2: 배치일자 전체를 대상으로 각각 단일 트랜잭션 1회 실행 (청크 키 추가 금지)
beginTransaction()
currentStepErrorCode = -2                          // UPDATE 1도 UPDATE 2 검사 시점에 귀속되는 원본 코드
execute(SQL_UPDATE1_SIGN_FLIP, { p_batchYmd: batchYmd })
commit()

beginTransaction()
currentStepErrorCode = -2                          // UPDATE 2 자신의 원본 코드
execute(SQL_UPDATE2_TOTAL, { p_batchYmd: batchYmd })
commit()

writeStepJournal(runId, "S06", status: "Succeeded", legacyReturnCode: 0)
writeCheckpoint(runId, "S06", status: "Succeeded")
```

```pseudocode
ON FAILURE observed by the application:
    rollbackIfOpen()

    IF shadowCaptured:
        beginTransaction()
        execute(SQL_RESTORE_DELETE, { p_batchYmd: batchYmd })
        execute(SQL_RESTORE_INSERT, { p_runId: runId })
        commit()

    writeStepJournal(runId, "S06", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

#### SQL 문 (문장 앵커 포함)

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_PRECHECK_EXISTS : 원본 IF EXISTS 가드 (라인 19~21). 참이면 -9로 중단.
SELECT CASE WHEN EXISTS (
    SELECT PLTID
      FROM SETTLE_POQ_DB.dbo.TSettleMst A
      INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
        ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
     WHERE A.ProcYMD = @p_batchYmd
       AND A.YMD = @p_batchYmd
       AND A.OutState IN (1, 5)
       AND A.OutYMD IS NOT NULL
       AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
       AND A.ExtraSettleFlag = 1
) THEN 1 ELSE 0 END;

-- SQL_CREATE_AND_CAPTURE_SHADOW : 원본 DELETE 1 대상 범위 전체를 그대로 캡처
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S06';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'
INSERT INTO ' + @v_shadow + N'
SELECT A.* FROM SETTLE_POQ_DB.dbo.TSettleMst A
INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
   ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
WHERE A.ProcYMD = @p_batchYmd AND A.YMD = @p_batchYmd
  AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
  AND A.TxAmt = 0 AND A.ExtraSettleFlag = 1;';
EXEC sp_executesql @v_sql, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;

-- SQL_CHUNK_BOUNDS : 원본 INSERT 1 소스 필터를 그대로 경계 조회에도 적용
SELECT MIN(A.PLTID), MAX(A.PLTID)
  FROM SETTLE_CARD_DB.dbo.TExtraTxMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty P
     ON P.PGNAME = A.PGNAME AND P.ExtraType IN (2, 3)
 WHERE A.REQYMD = @p_batchYmd
   AND A.EDICheckFlag = 'Y'
   AND A.RecordGB <> 'DX'
   AND A.CompanySalesType IN (0, 1, 2, 3);

-- SQL_DELETE_CHUNK : /* DELETE 1: 정산 데이터 삭제 */ 원본 필터 + PLTID 청크 범위
DELETE A
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
     ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
 WHERE A.ProcYMD = @p_batchYmd
   AND A.YMD = @p_batchYmd
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1
   AND A.PLTID >= @p_from AND A.PLTID < @p_to;   -- 청크 범위 조건

-- SQL_INSERT_CHUNK : /* INSERT 1: 정산 데이터 등록(거래일자가 @p_batchYmd인 모든 승인내역) */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
(
    YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
    PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME,
    PGVTTYPE, CLVTTYPE, ABROADCHK,
    CompanySalesType, USESTATE, NonSettleAmt, ExtraTxAmt, TXAMT,
    CLCOMMTYPE, PGCOMMTYPE, CLETC, PGETC, CLTOTAL, PGTOTAL, POQINCOME,
    CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM,
    CLCOMM, CLVT, PGCOMM, PGVT,
    INSTATE, OUTSTATE, INYMD, OUTYMD,
    ProcYMD, ProcState, ExtraSettleFlag
)
SELECT
    X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME,
    X.PGINCVTAX AS PGVTTYPE, X.CLINCVTAX AS CLVTTYPE, X.ABROADCHK,
    X.CompanySalesType, X.USESTATE, X.NonSettleAmt, X.ExtraTxAmt, X.TXAMT,
    0 AS CLCOMMTYPE, 0 AS PGCOMMTYPE, 0 AS CLETC, 0 AS PGETC,
    0 AS CLTOTAL, 0 AS PGTOTAL, 0 AS POQINCOME,
    0 AS CLINTCOMM, 0 AS PGINTEXPCOMM, 0 AS PGINTREALCOMM,
    CAST(ISNULL(X.CLCOMM, 0) AS INT) AS CLCOMM,
    dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM, 0) * dbo.UF_GET_INCVTAXRATE(X.CLINCVTAX)) AS CLVT,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
    END AS PGCOMM,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGINCVTAX), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                  ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                  - ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)
              , 0, Y.VatRoundFlag) AS INT)
    END AS PGVT,
    X.INSTATE,
    IIF(ISNULL(X.CompanySalesType, 4) = 4, 9, X.OUTSTATE) AS OUTSTATE,
    X.INYMD,
    X.OUTYMD,
    X.ProcYMD,
    CASE WHEN (X.CLCOMM IS NULL OR X.PGCOMM IS NULL) THEN 1 ELSE NULL END AS ProcState,
    1 AS ExtraSettleFlag
FROM
(
    SELECT
        A.REQYMD                                           AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD)                    AS CYMD,
        A.AYMD                                              AS AYMD,
        A.CLIENTID                                          AS CLIENTID,
        A.PGNAME                                            AS PGNAME,
        C.MallID                                            AS MALLID,
        A.PLTID                                             AS PLTID,
        A.TID                                               AS TID,
        A.CID                                               AS CID,
        ''                                                  AS PAYERID,
        ''                                                  AS PAYERNAME,
        ''                                                  AS SERVICENAME,
        N'영중소차액정산'                                     AS PRODUCTNAME,
        1                                                   AS PGINCVTAX,
        0                                                   AS CLINCVTAX,
        0                                                   AS ABROADCHK,
        A.CompanySalesType                                  AS CompanySalesType,
        A.USESTATE                                          AS USESTATE,
        0                                                   AS NonSettleAmt,
        0                                                   AS TXAMT,
        A.TxAmt                                             AS ExtraTxAmt,
        SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT(
            A.AYMD, A.PGName, A.ClientID, A.CompanySalesType, B.CardCode, B.CardCPID, A.TxAmt) AS CLComm,
        SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt(
            A.AYMD, B.CardCode, B.CardCPID, A.CompanySalesType, A.TxAmt, B.CheckCardFlag) AS PGComm,
        0                                                   AS INSTATE,
        2                                                   AS OUTSTATE,
        NULL                                                AS INYMD,
        (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.ReqYMD, C.SettlePeriodID)) AS OUTYMD,
        A.REQYMD                                            AS ProcYMD
    FROM SETTLE_CARD_DB.dbo.TExtraTxMst A
    INNER JOIN PLCardDB.dbo.TPLCardTxMst B
       ON B.PLTID = A.PLTID
    INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate C
       ON C.CLIENTID = A.CLIENTID AND C.PGNAME = A.PGNAME
    INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty P
       ON P.PGNAME = A.PGNAME AND P.ExtraType IN (2, 3)
    WHERE A.REQYMD = @p_batchYmd
      AND A.EDICheckFlag = 'Y'
      AND A.RecordGB <> 'DX'
      AND A.CompanySalesType IN (0, 1, 2, 3)
      AND A.PLTID >= @p_from AND A.PLTID < @p_to        -- 청크 범위 조건
) X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y
   ON Y.PGName = X.PGNAME;

-- SQL_UPDATE1_SIGN_FLIP : /* UPDATE 1: 영중소차액정산 수수료 부호 반전 */
-- 원본 술어 컬럼(ProcYMD, YMD, CompanySalesType, USESTATE, TxAmt, ExtraSettleFlag)만 사용, PLTID 등 추가 불가
UPDATE A
   SET CLCOMM        = CLCOMM        * (-1),
       CLINTCOMM     = CLINTCOMM     * (-1),
       CLVT          = CLVT          * (-1),
       CLETC         = CLETC         * (-1),
       PGCOMM        = PGCOMM        * (-1),
       PGINTEXPCOMM  = PGINTEXPCOMM  * (-1),
       PGINTREALCOMM = PGINTREALCOMM * (-1),
       PGVT          = PGVT          * (-1),
       PGETC         = PGETC         * (-1)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
     ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
 WHERE A.ProcYMD = @p_batchYmd
   AND A.YMD = @p_batchYmd
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.USESTATE IN (0)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;

-- SQL_UPDATE2_TOTAL : /* UPDATE 2: TOTAL 수수료 및 POQINCOME 산출 */
-- 원본 술어 컬럼(ProcYMD, YMD, CompanySalesType, TxAmt, ExtraSettleFlag)만 사용, PLTID 등 추가 불가
UPDATE A
   SET CLTOTAL   = (A.CLCOMM + A.CLVT + A.CLETC + A.CLINTCOMM),
       PGTOTAL   = (A.PGCOMM + A.PGVT + A.PGETC +
                    (CASE A.PGINTREALCOMM WHEN 0 THEN A.PGINTEXPCOMM ELSE A.PGINTREALCOMM END)),
       POQINCOME = (A.CLCOMM + A.CLVT + A.CLETC + A.CLINTCOMM)
                 - (A.PGCOMM + A.PGVT + A.PGETC +
                    (CASE A.PGINTREALCOMM WHEN 0 THEN A.PGINTEXPCOMM ELSE A.PGINTREALCOMM END))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
     ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
 WHERE A.ProcYMD = @p_batchYmd
   AND A.YMD = @p_batchYmd
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;

-- SQL_RESTORE_DELETE : 복구 범위는 캡처 때와 동일 (규칙 4b)
DELETE A
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty PG
     ON A.PGName = PG.PGName AND PG.ExtraType IN (2, 3)
 WHERE A.ProcYMD = @p_batchYmd
   AND A.YMD = @p_batchYmd
   AND ISNULL(A.CompanySalesType, 4) IN (0, 1, 2, 3)
   AND A.TxAmt = 0
   AND A.ExtraSettleFlag = 1;

-- SQL_RESTORE_INSERT : 캡처와 동일한 섀도우 이름을 재조립
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TSettleMst_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S06';
DECLARE @v_sql NVARCHAR(MAX) =
    N'INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst SELECT * FROM ' + @v_shadow + N';';
EXEC sp_executesql @v_sql;
```

#### 오류 코드 매핑

| 코드 | 발생 지점 | 배치 처리 |
| :--- | :--- | :--- |
| `-9` | 사전 정산 데이터 존재 가드(IF EXISTS) | 트랜잭션 미개시 상태이므로 롤백 없이 즉시 `Failed` 저널 기록 후 파이프라인 중단 |
| `-1` | DELETE 1(청크별) 실패 | 진행 중인 청크 트랜잭션 롤백 + 섀도우 복원 |
| `-2` | INSERT 1 · UPDATE 1 · UPDATE 2 실패(원본에서 UPDATE 2 이후 `@@ERROR` 검사 지점에 귀속) | 해당 트랜잭션 롤백 + 섀도우 복원 |

`@po_intRetVal` 출력 파라미터는 `batch.BatchStepJournal.LegacyReturnCode`에 위 코드 그대로 매핑되며, 성공 시에는 `0`으로 기록한다(원본은 성공 코드를 명시하지 않으므로 배치 계약의 `Succeeded` 상태와 `0`으로 통일한다).