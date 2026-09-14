### S08 | 예외/프로모션 수수료 보정

`dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`를 대체하는 단계로, 대상 테이블은 `SETTLE_POQ_DB.dbo.TSettleMst` 하나뿐입니다. 원본이 단일 트랜잭션(`BEGIN TRANSACTION` ~ `COMMIT TRANSACTION`)으로 18개의 `UPDATE` 문을 순차 실행하고, 어느 하나라도 실패하면 즉시 롤백 후 반환하는 구조이므로, 이 단계도 청크로 나누지 않고 단일 트랜잭션 안에서 18개 문장을 순서대로 실행합니다. **모든 작업이 하나의 트랜잭션 안에서 끝나므로 섀도우 테이블은 사용하지 않습니다** — 실패 시 트랜잭션 롤백만으로 `SETTLE_POQ_DB.dbo.TSettleMst`는 완전히 복원되며, 그 위에 추가로 보정 DELETE를 수행하면 오히려 손상이 없던 데이터를 파괴하게 됩니다. 이 단계는 SNAPSHOT 격리 수준 하에서 실행되어야 합니다(격리 수준 설정 자체는 이 단계가 지시하지 않으며, 실행 환경이 보장합니다).

원본에서 각 `UPDATE` 직후 `@@ERROR`를 검사해 대입하던 오류 코드 `-101, -102, -1, -2, -3, -4, -5, -10, -11, -19, -20, -201, -21, -27, -28, -29`는 규칙 9에 따라 정확히 그대로 재사용하며, 새 코드로 리매핑하거나 연속 범위로 합치지 않습니다. `@@ERROR` 분기·`GOTO`·`BEGIN TRY/CATCH`는 원본 절차 인용이 아니므로 이 단계의 SQL에는 쓰지 않으며, 실패 관찰과 롤백 결정은 응용 계층이 담당합니다. 원본 `WITH (NOLOCK)` 힌트는 SNAPSHOT 격리와 충돌하므로 전부 제거합니다. 인터페이스는 원본 시그니처인 `@pi_strYMD CHAR(8)`, `@po_intRetVal INT`만 사용하며, 재시작·건너뛰기용 파라미터는 추가하지 않습니다. 원본이 지역 변수로 선언했던 카드 원천PG 목록(`@v_strCardPGNames`)은 응용 계층의 상수로 유지해 매 문장에 파라미터로 전달합니다.

```pseudocode
// 단계 진입 - 상태 변수는 NULL로 시작 (규칙 6-1)
currentStepErrorCode = NULL

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })
writeStepJournal(runId, "S08", status: "Running", legacyReturnCode: NULL)

// 원본 지역 변수 @v_strCardPGNames를 응용 계층 상수로 유지
cardPGNames = "PLCard+SamSungPay+NaverCard+SSGPayCard+KakaoPay+KakaoCard+ApplePay+TossCardAuth"

// 원본과 동일하게 단일 트랜잭션 - 청크 없음, 섀도우 없음
beginTransaction()
try:
    currentStepErrorCode = -101
    execute(SQL_U1_PROMOTION_DISCOUNT, { p_batchYmd: batchYmd, p_cardPGNames: cardPGNames })

    currentStepErrorCode = -102
    execute(SQL_U2_PG_COMM_REAPPLY, { p_batchYmd: batchYmd, p_cardPGNames: cardPGNames })

    currentStepErrorCode = -1
    execute(SQL_U3_KFTC_CLIENT_SECTION_RATE, { p_batchYmd: batchYmd })

    currentStepErrorCode = -1
    execute(SQL_U4_CLIENT_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -2
    execute(SQL_U5_KFTC_PG_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -2
    execute(SQL_U6_INIBANK_PG_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -3
    execute(SQL_U7_CHECKPAY_TOSS_PG_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -4
    execute(SQL_U8_CHECKPAY_TOSS_CLIENT_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -5
    execute(SQL_U9_EASYBANK_CLIENT_MIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -10
    execute(SQL_U10_KAKAOPAY_PGVT, { p_batchYmd: batchYmd })

    currentStepErrorCode = -11
    execute(SQL_U11_INIVACCT_NH_EXCEPTION, { p_batchYmd: batchYmd })

    currentStepErrorCode = -19
    execute(SQL_U12_CARD_PROMOTION_DISCOUNT, { p_batchYmd: batchYmd })

    currentStepErrorCode = -20
    execute(SQL_U13_CARD_ORIGIN_COMM, { p_batchYmd: batchYmd, p_cardPGNames: cardPGNames })

    currentStepErrorCode = -201
    execute(SQL_U14_PLCARD_PARTIAL_CANCEL_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -21
    execute(SQL_U15_MOBILECO_CLIENT_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -27
    execute(SQL_U16_PAYCO_AMOUNT_REALIGN, { p_batchYmd: batchYmd })

    currentStepErrorCode = -28
    execute(SQL_U17_POINTPAY_PAYCO_ORIGIN_COMM, { p_batchYmd: batchYmd })

    currentStepErrorCode = -29
    execute(SQL_U18_OUTSTATE_PROPAGATE, { p_batchYmd: batchYmd })

    commit()
    writeStepJournal(runId, "S08", status: "Succeeded", legacyReturnCode: 0)
    writeCheckpoint(runId, "S08", status: "Succeeded")
catch failure:
    // 단일 트랜잭션이므로 롤백만으로 SETTLE_POQ_DB.dbo.TSettleMst가 완전히 복원된다.
    // 섀도우 캡처/복구를 추가로 수행하지 않는다 (규칙 4).
    rollbackIfOpen()
    writeStepJournal(runId, "S08", status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
/* U1: 프로모션 할인정보 갱신 - 원천PG 외 거래에 프로모션 원가 할인액 반영 (오류 -101) */
UPDATE A
   SET A.DiscountFlag = 'Y',
       A.DiscountAmt  = A.TxAmt - B.OrgDiscountAmt
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN PaymentDB.dbo.TPromotionTxMst B
    ON A.PLTID = B.PLTID
 WHERE A.YMD = @p_batchYmd
   AND A.UseState IN (0, 1)
   AND ISNULL(B.OrgDiscountAmt, 0) > 0
   AND A.PGName NOT IN (SELECT Value FROM STRING_SPLIT(@p_cardPGNames, '+'));

/* U2: 비원천PG 할인 건 PGComm/PGVT 재적용 (오류 -102) */
UPDATE AA
   SET AA.PGComm = BB.PGCOMM,
       AA.PGVT   = BB.PGVT
  FROM SETTLE_POQ_DB.dbo.TSettleMst AA
  JOIN (
        SELECT X.PLTID, X.ID,
               CASE WHEN Y.CommMethod = 0
                    THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
                    ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
               END AS PGCOMM,
               CASE WHEN Y.CommMethod = 0
                    THEN CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                    ELSE CAST(ROUND(
                             ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                           - (ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)
                              + ROUND(ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)),
                           0, Y.VatRoundFlag) AS INT)
               END AS PGVT,
               X.PGName
          FROM (
                SELECT A.PLTID, A.ID, A.PGName, A.PGETC,
                       B.INCVTAX AS PGIncVTax,
                       IIF(B.COMMISSIONTYPE = 0, (A.DiscountAmt * (B.COMMISSIONRATE / 100.0)), B.COMMISSIONAMT) AS PGCOMM,
                       IIF(B.COMMISSIONTYPE = 0,
                           (A.DiscountAmt * ((B.COMMISSIONRATE / 100.0) + (B.COMMISSIONRATE / 100.0 / 10.0))),
                           (B.COMMISSIONAMT + (B.COMMISSIONAMT / 10.0))) AS PGCOMM4SUM,
                       (B.ETCAMT + (B.ETCAMT / 10.0)) AS PGETC4SUM
                  FROM SETTLE_POQ_DB.dbo.TSettleMst A
                  JOIN SETTLE_POQ_DB.dbo.TPGSettleRate B
                    ON A.YMD = B.YMD AND A.PGName = B.PGName AND A.MallID = B.MallID
                 WHERE A.YMD = @p_batchYmd
                   AND A.PGName NOT IN (SELECT Value FROM STRING_SPLIT(@p_cardPGNames, '+'))
                   AND A.DiscountFlag = 'Y'
               ) X
          JOIN SETTLE_POQ_DB.dbo.TPGProperty Y
            ON X.PGName = Y.PGName
       ) BB
    ON AA.PLTID = BB.PLTID AND AA.ID = BB.ID AND AA.PGName = BB.PGName
 WHERE AA.YMD = @p_batchYmd
   AND AA.PGName NOT IN (SELECT Value FROM STRING_SPLIT(@p_cardPGNames, '+'))
   AND AA.DiscountFlag = 'Y';

/* U3: KFTC 고객사 구간요율 CLCOMM/CLVT 재적용 (오류 -1) */
UPDATE A
   SET A.CLCOMM = dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID, A.PGNAME, A.MALLID, (A.TXAMT - ISNULL(A.NonSettleAmt, 0))),
       A.CLVT   = dbo.UF_GET_ROUND4VAT(
                     dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID, A.PGNAME, A.MALLID, (A.TXAMT - ISNULL(A.NonSettleAmt, 0)))
                     * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B
    ON A.YMD = B.YMD AND A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME = 'KFTC'
   AND dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID, A.PGNAME, A.MALLID, (A.TXAMT - ISNULL(A.NonSettleAmt, 0))) <> 0;

/* U4: 고객사 최저수수료 적용 - CLCOMM/CLCOMMTYPE/CLVT 동시(갱신 전 값 기준) 평가 유지 (오류 -1) */
UPDATE A
   SET A.CLCOMMTYPE = CASE WHEN ABS(A.CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT)) THEN 1 ELSE A.CLCOMMTYPE END,
       A.CLCOMM     = CASE WHEN ABS(A.CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT)) THEN CAST(B.MINCOMMISSIONAMT AS INT) ELSE A.CLCOMM END,
       A.CLVT       = CASE WHEN ABS(A.CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT))
                            THEN dbo.UF_GET_ROUND4VAT(B.MINCOMMISSIONAMT * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
                            ELSE A.CLVT END
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B
    ON A.YMD = B.YMD AND A.CLIENTID = B.CLIENTID AND A.PGNAME = B.PGNAME
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('KFTC', 'YELOPAY', 'INIBANK', 'settlevacct', 'inivacct')
   AND B.MINCOMMISSIONAMT <> 0;

/* U5: KFTC PG 최저수수료 구간 산식 재적용 (오류 -2) */
UPDATE A
   SET A.PGCOMMTYPE = 1,
       A.PGVT       = 0,
       A.PGCOMM     = CASE WHEN ABS(A.TXAMT) <= 150 THEN A.TXAMT
                            WHEN ABS(A.TXAMT) > 150 AND ABS(A.TXAMT * (B.COMMISSIONRATE / 100)) <= 150 THEN 150
                            WHEN ABS(A.TXAMT * (B.COMMISSIONRATE / 100)) > 150 AND ABS(A.TXAMT) <= 100000
                                 THEN ROUND(ABS(A.TXAMT) * (B.COMMISSIONRATE / 100), -1, 1)
                            WHEN ABS(A.TXAMT) > 100000 AND ABS(A.TXAMT) <= 1000000 THEN 1500
                            WHEN ABS(A.TXAMT) > 1000000 THEN 3000
                       END
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
    ON A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME = 'KFTC';

/* U6: INIBANK 최소수수료(200/180) 적용 - PGCOMM/PGVT 동시 평가 (오류 -2) */
UPDATE A
   SET A.PGCOMMTYPE = 1,
       A.PGCOMM = CASE WHEN ABS(A.PGCOMM) < IIF(A.MALLID = 'LOLLETTER4', 200, 180)
                        THEN IIF(A.MALLID = 'LOLLETTER4', 200, 180)
                        ELSE ABS(A.PGCOMM) END,
       A.PGVT   = CASE WHEN ABS(A.PGCOMM) < IIF(A.MALLID = 'LOLLETTER4', 200, 180)
                        THEN IIF(A.MALLID = 'LOLLETTER4', 20, 18)
                        ELSE ABS(A.PGVT) END
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME = 'INIBANK';

/* U7: CheckPay Toss TossPoint PG 최저수수료 적용 (오류 -3) */
UPDATE A
   SET A.PGCOMM = CAST(B.CommissionMinAmt AS INT) * (IIF(A.PGCOMM < 0, -1, 1)),
       A.PGVT   = CAST(B.CommissionMinAmt * (0.1) AS INT) * (IIF(A.PGVT < 0, -1, 1))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
    ON A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('CheckPay', 'Toss', 'TossPoint')
   AND ABS(A.PGCOMM) < B.CommissionMinAmt
   AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD));

/* U8: CheckPay Toss TossPoint 고객사 최저수수료 적용 (오류 -4) */
UPDATE A
   SET A.CLCOMM = CAST(B.MinCommissionAmt * (IIF(A.CLCOMM < 0, -1, 1)) AS INT),
       A.CLVT   = dbo.UF_GET_ROUND4VAT(B.MinCommissionAmt * dbo.UF_GET_INCVTAXRATE(A.CLVTType)) * (IIF(A.CLVT < 0, -1, 1))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B
    ON A.ClientID = B.ClientID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('CheckPay', 'Toss', 'TossPoint')
   AND ABS(A.CLCOMM) < B.MinCommissionAmt
   AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD));

/* U9: EasyBank 고객사 최저수수료 적용 (오류 -5) */
UPDATE A
   SET A.CLCOMM = CAST(B.MinCommissionAmt AS INT),
       A.CLVT   = dbo.UF_GET_ROUND4VAT((B.MinCommissionAmt + A.CLETC) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate B
    ON A.ClientID = B.ClientID AND A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('EasyBank')
   AND A.UseState = 0
   AND ABS(A.CLCOMM) < B.MinCommissionAmt;

/* U10: kakaopay KakaoMoney PGVT 재적용 (오류 -10) */
UPDATE A
   SET A.PGVT = CAST(ROUND(A.PGCOMM * (0.1), 0, dbo.UF_GET_PGCommOption(A.PGNAME, 5)) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
    ON A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_batchYmd
   AND A.PGNAME IN ('kakaopay', 'KakaoMoney')
   AND A.TID = A.CID
   AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD));

/* U11: inivacct 농협 가상계좌 기타수수료 예외 적용 (오류 -11) */
UPDATE A
   SET A.PGETC = B.ETCAmtNH,
       A.PGVT  = CAST(ROUND(B.ETCAmtNH * (0.1), 0, dbo.UF_GET_PGCommOption(A.PGNAME, 5)) AS INT)
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
    ON A.PGName = B.PGName AND A.MallID = B.MallID
  JOIN PaymentDB.dbo.TVAccountTxMst C
    ON A.PLTID = C.PLTID
 WHERE C.BankCode IN (11, 12)
   AND A.PGName = 'inivacct'
   AND A.YMD = @p_batchYmd
   AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD));

/* U12: 카드 원천PG 프로모션 할인정보 갱신 (오류 -19) */
UPDATE A
   SET A.DiscountFlag = B.DiscountFlag,
       A.DiscountAmt  = B.DiscountAmt
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN PLCardDB.dbo.TPLCardTxMst B
    ON A.PLTID = B.PLTID
 WHERE A.YMD = @p_batchYmd
   AND A.AYMD >= '20230101'
   AND B.DiscountFlag = 'Y'
   AND A.UseState IN (0, 1)
   AND ISNULL(A.ExtraSettleFlag, 0) = 0;

/* U13: 카드 원천PG 고객사/PG 원가 수수료 반영 - CLCOMM/CLIntComm/CLETC/CLVT/ProcState 동시 평가 (오류 -20) */
UPDATE Y
   SET Y.CLCOMM        = IIF(Y.PGName = 'PLCard', ISNULL(X.CLCOMM, 0), Y.CLCOMM),
       Y.CLIntComm     = IIF(Y.PGName = 'PLCard', ISNULL(X.CLIntComm, 0), Y.CLIntComm),
       Y.CLETC         = IIF(Y.PGName = 'PLCard', ISNULL(X.CLETC, 0), Y.CLETC),
       Y.CLVT          = IIF(Y.PGName = 'PLCard',
                              ISNULL(dbo.UF_GET_ROUND4VAT((X.CLCOMM + X.CLETC + X.CLIntComm) * dbo.UF_GET_INCVTAXRATE(Y.CLVTType)), 0),
                              Y.CLVT),
       Y.PGVTTYPE      = ISNULL(X.PGVTTYPE, 0),
       Y.PGCOMM        = ISNULL(X.PGCOMM, 0),
       Y.PGIntRealComm = ISNULL(X.PGIntComm, 0),
       Y.PGETC         = ISNULL(X.PGETC, 0),
       Y.PGVT          = 0,
       Y.ProcState     = CASE WHEN X.CLCOMM IS NULL OR X.CLIntComm IS NULL OR X.CLETC IS NULL
                                    OR X.PGCOMM IS NULL OR X.PGIntComm IS NULL OR X.PGETC IS NULL
                               THEN IIF(Y.PGName = 'PLCard', 1, Y.ProcState)
                               ELSE Y.ProcState END
  FROM SETTLE_POQ_DB.dbo.TSettleMst Y
  JOIN (
        SELECT A.PLTID, A.ID,
               SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT(B.ClientID, B.CardCode, B.CardCPID,
                    (A.TxAmt - ISNULL(A.NonSettleAmt, 0)), A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod) AS CLCOMM,
               SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST(B.ClientID, B.CardCode, B.CardCPID,
                    (A.TxAmt - ISNULL(A.NonSettleAmt, 0)), A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod) AS CLIntComm,
               CASE WHEN A.AbroadChk = 1 AND A.UseState = 0 THEN ISNULL(D.TransactionFee4Foreign, 0) ELSE 0 END AS CLETC,
               SETTLE_CARD_DB.dbo.UF_GET_COMM4PG(B.CardCode, B.CardCPID,
                    IIF(ISNULL(A.DiscountFlag, 'N') = 'Y', A.DiscountAmt, A.TxAmt), A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod) AS PGCOMM,
               SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST(B.CardCode, B.CardCPID,
                    IIF(ISNULL(A.DiscountFlag, 'N') = 'Y', A.DiscountAmt, A.TxAmt), A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod) AS PGIntComm,
               CASE WHEN C.ETCCommissionType = 0
                    THEN IIF(ISNULL(A.DiscountFlag, 'N') = 'Y', A.DiscountAmt, A.TxAmt) * (C.ETCCommissionRate / 100.0)
                    ELSE C.ETCCommissionAmt END AS PGETC,
               C.IncVTax AS PGVTTYPE
          FROM SETTLE_POQ_DB.dbo.TSettleMst A
          JOIN PLCardDB.dbo.TPLCardTxMst B
            ON A.PLTID = B.PLTID
          JOIN SETTLE_CARD_DB.dbo.TCardContractMgmt C
            ON B.CardCPID = C.CardCPID
          JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt D
            ON B.ClientID = D.ClientID
         WHERE A.YMD = @p_batchYmd
           AND A.PGNAME IN (SELECT Value FROM STRING_SPLIT(@p_cardPGNames, '+'))
           AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))
       ) X
    ON X.PLTID = Y.PLTID AND X.ID = Y.ID
 WHERE Y.YMD = @p_batchYmd
   AND Y.PGNAME IN (SELECT Value FROM STRING_SPLIT(@p_cardPGNames, '+'));

/* U14: PLCard 부분취소 고객사 수수료 재계산 (오류 -201) */
UPDATE A
   SET A.CLCOMM = SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL(
                     B.ClientID, B.CardCode, B.CardCPID, (A.TxAmt - ISNULL(A.NonSettleAmt, 0)),
                     A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod, B.YMD),
       A.CLVT   = dbo.UF_GET_ROUND4VAT(
                     (SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL(
                         B.ClientID, B.CardCode, B.CardCPID, (A.TxAmt - ISNULL(A.NonSettleAmt, 0)),
                         A.AbroadChk, B.CLInterest, B.CheckCardFlag, B.AllotPeriod, B.YMD)
                      + A.CLETC + A.CLIntComm) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN PLCardDB.dbo.TPLCardTxMst B
    ON A.PLTID = B.PLTID
 WHERE A.YMD = @p_batchYmd
   AND A.PGName = 'PLCard'
   AND A.UseState = 2;

/* U15: impaymobile 통신사별 고객사 수수료 재계산 (오류 -21) */
UPDATE A
   SET A.CLComm = dbo.UF_Get_CLComm4MobileCo(A.AYMD, A.ClientID, A.PGName, A.MallID, A.MobileCo, (A.TxAmt - ISNULL(A.NonSettleAmt, 0))),
       A.CLVT   = dbo.UF_Get_Round4Vat(
                     (dbo.UF_Get_CLComm4MobileCo(A.AYMD, A.ClientID, A.PGName, A.MallID, A.MobileCo, (A.TxAmt - ISNULL(A.NonSettleAmt, 0)))
                      + A.CLEtc) * dbo.UF_Get_IncVTaxRate(A.CLVTType))
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo B
    ON A.AYMD = B.YMD AND A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
 WHERE A.YMD = @p_batchYmd
   AND A.PGName = 'impaymobile'
   AND B.MobileCoCommApply = 'Y'
   AND ISNULL(A.MobileCo, '') IN ('1', '2', '3', '4', '5', '6');

/* U16: payco 카드금액 구성요소 재정렬 (오류 -27) */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET CardAmt   = TxAmt,
       CouponAmt = 0,
       MoneyAmt  = 0,
       PointAmt  = 0
 WHERE YMD = @p_batchYmd
   AND PGName = 'payco'
   AND UseState = 1
   AND TxAmt != CardAmt + CouponAmt + MoneyAmt + PointAmt;

/* U17: pointpay payco 원가 수수료(PGComm/PGVT) 재적용 - 두 분기는 UNION ALL로 동일 컬럼 목록을 유지 (규칙 7-1) (오류 -28) */
UPDATE AA
   SET AA.PGComm = BB.PGCOMM,
       AA.PGVT   = BB.PGVT
  FROM SETTLE_POQ_DB.dbo.TSettleMst AA
  JOIN (
        SELECT X.PLTID, X.ID, X.PGName,
               CASE WHEN Y.CommMethod = 0
                    THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
                    ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag) AS INT)
               END AS PGCOMM,
               CASE WHEN Y.CommMethod = 0
                    THEN CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                    ELSE CAST(ROUND(
                             ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                           - (ROUND(ROUND(X.PGCOMM4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)
                              + ROUND(ROUND(X.PGETC4SUM, 0, Y.CommSumRoundFlag) / 1.1, 0, Y.CommRoundFlag)),
                           0, Y.VatRoundFlag) AS INT)
               END AS PGVT
          FROM (
                -- pointpay 분기: PointAmt * CommissionPointRate 기반
                SELECT A.PLTID, A.ID, A.PGName, A.PGETC,
                       B.incVTax AS PGIncVTax,
                       IIF(B.CommissionType = 0, (A.PointAmt * (B.CommissionPointRate / 100.0)), B.CommissionAmt) AS PGCOMM,
                       IIF(B.CommissionType = 0,
                           (A.PointAmt * ((B.CommissionPointRate / 100.0) + (B.CommissionPointRate / 100.0 / 10.0))),
                           (B.CommissionAmt + (B.CommissionAmt / 10.0))) AS PGCOMM4SUM,
                       (B.ETCAmt + (B.ETCAmt / 10.0)) AS PGETC4SUM
                  FROM SETTLE_POQ_DB.dbo.TSettleMst A
                  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
                    ON A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
                 WHERE A.YMD = @p_batchYmd
                   AND A.PGName = 'pointpay'
                UNION ALL
                -- payco 분기: Card/Coupon/Money/Point 각 요율 합산 기반
                SELECT A.PLTID, A.ID, A.PGName, A.PGETC,
                       B.incVTax AS PGIncVTax,
                       IIF(B.CommissionType = 0,
                           (A.CardAmt * (B.CommissionCardRate / 100.0))
                         + (A.CouponAmt * (B.CommissionCouponRate / 100.0))
                         + (A.MoneyAmt * (B.CommissionMoneyRate / 100.0))
                         + (A.PointAmt * (B.CommissionPointRate / 100.0)),
                           B.CommissionAmt) AS PGCOMM,
                       IIF(B.CommissionType = 0,
                           (A.CardAmt * ((B.CommissionCardRate / 100.0) + (B.CommissionCardRate / 100.0 / 10.0)))
                         + (A.CouponAmt * ((B.CommissionCouponRate / 100.0) + (B.CommissionCouponRate / 100.0 / 10.0)))
                         + (A.MoneyAmt * ((B.CommissionMoneyRate / 100.0) + (B.CommissionMoneyRate / 100.0 / 10.0)))
                         + (A.PointAmt * ((B.CommissionPointRate / 100.0) + (B.CommissionPointRate / 100.0 / 10.0))),
                           (B.CommissionAmt + (B.CommissionAmt / 10.0))) AS PGCOMM4SUM,
                       (B.ETCAmt + (B.ETCAmt / 10.0)) AS PGETC4SUM
                  FROM SETTLE_POQ_DB.dbo.TSettleMst A
                  JOIN SETTLE_POQ_DB.dbo.TPGCMRate B
                    ON A.PGNAME = B.PGNAME AND A.MALLID = B.MALLID
                 WHERE A.YMD = @p_batchYmd
                   AND A.PGName = 'payco'
               ) X
          JOIN SETTLE_POQ_DB.dbo.TPGProperty Y
            ON X.PGName = Y.PGName
       ) BB
    ON AA.PLTID = BB.PLTID AND AA.ID = BB.ID AND AA.PGName = BB.PGName
 WHERE AA.YMD = @p_batchYmd
   AND AA.PGName IN ('pointpay', 'payco');

/* U18: 정산완료 연계 취소건 OUTSTATE 전이 - 하위질의는 기준일을 사용하지만 최상위 WHERE는 사용하지 않음 (오류 -29) */
UPDATE SETTLE_POQ_DB.dbo.TSettleMst
   SET OUTSTATE = 9
 WHERE PLTID IN (
        SELECT PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst
         WHERE YMD = @p_batchYmd AND USESTATE = 1 AND OUTSTATE = 9
       )
   AND USESTATE = 0;
```

U4, U6, U8, U9, U13의 여러 컬럼 `SET` 우변은 원본과 동일하게 갱신 전 값을 기준으로 동시에 평가되도록 하나의 `UPDATE` 문 안에 유지했습니다(별도 문장으로 쪼개어 순차 대입하면 결과가 달라집니다). U17은 pointpay·payco 두 원가 산식이 서로 다른 요율 컬럼 조합을 쓰므로 `UNION ALL`로 결합하되, 두 분기 모두 `PLTID, ID, PGName, PGETC, PGIncVTax, PGCOMM, PGCOMM4SUM, PGETC4SUM` 컬럼 목록과 순서를 동일하게 맞췄습니다. U2/U13/U17의 스칼라·파생 테이블 조인은 명세서 원문 그대로 `JOIN`(비-`APPLY`) 형태를 유지했습니다.