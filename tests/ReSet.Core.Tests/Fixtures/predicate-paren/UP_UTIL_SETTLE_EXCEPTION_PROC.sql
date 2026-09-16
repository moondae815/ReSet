
-----------------------------------------------------------------
--ProcedureName   : UP_UTIL_SETTLE_EXCEPTION_PROC
--Description     : 일별  정산내역 테이블 update - 예외로직 처리
--Inner SP        : NONE
--Return Value    : =0->성공, <>0->실패
--Copyright ⓒ 2001 by PayLetter Inc. All rights reserved.
--Author          : Sangsoo Lee, 3702, dialup, 2002-05-13
--Modify History  :
--sk1004@payletter.com, 2003-08-01, 부가세 원단위 절사
--sk1004@payletter.com, 2003-12-04, 정산테이블 스키마 변경에 따른 수정,Procedure ReName
--                  yjlee, 2019-12-18, PaymentDB 제거, PLCardDB -> SETTLE_CARD_DB
-----------------------------------------------------------------
CREATE                       PROCEDURE [dbo].[UP_UTIL_SETTLE_EXCEPTION_PROC]
@pi_strYMD                  CHAR(8),
@po_intRetVal               INT OUTPUT
AS

SET NOCOUNT ON

-------------------------------------------------------
--변수 및 조건문 정의
-------------------------------------------------------      
DECLARE @v_strClientID VARCHAR(20)
DECLARE @v_intMinCommissionAmt INT
DECLARE @v_strCardPGNames VARCHAR(100) = CONCAT('PLCard','+SamSungPay','+NaverCard','+SSGPayCard','+KakaoPay','+KakaoCard','+ApplePay','+TossCardAuth')

-------------------------------------------------------
--정산 예외 수수료 적용 
-------------------------------------------------------   
--로직 정리 및 제거(kks, 2017-04-05) : VIRTUALBANK(20140904), BANKTOWNBANK(20080227) 이후 거래건 없어 관련 로직 삭제
BEGIN TRAN

    --------------------------------------------------
    --[원천PG 외] 프로모션 정보 업데이트
    --------------------------------------------------
    --PG수수료 정율일 경우 추가 처리 필요
    UPDATE SETTLE_POQ_DB.dbo.TSettleMst
    SET    DiscountFlag = 'Y'
          ,DiscountAmt  = A.TxAmt - B.OrgDiscountAmt
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)
          ,PaymentDB.dbo.TPromotionTxMst B WITH(NOLOCK)
    WHERE  A.PLTID    = B.PLTID
    AND    A.YMD      = @pi_strYMD
    AND    A.UseState IN (0, 1)
    AND    ISNULL(B.OrgDiscountAmt, 0) > 0
    AND    A.PGName   NOT IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -101
        RETURN
    END

    --PG수수료, PG부가세 재적용
    UPDATE SETTLE_POQ_DB.dbo.TSettleMst
    SET    PGComm = BB.PGCOMM
          ,PGVT   = BB.PGVT
    FROM   SETTLE_POQ_DB.dbo.TSettleMst AA WITH(NOLOCK)
    JOIN (
        SELECT PLTID
              ,ID
              ,CASE WHEN Y.CommMethod = 0 THEN                                         --수수료계산방식(0:공급가액, 1:수수료합계)
                         CAST(ROUND(X.PGCOMM,          0, Y.CommRoundFlag) AS INT)     --0:반올림, 0<>절사
                    ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM,0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT)
               END AS PGCOMM

              ,CASE WHEN Y.CommMethod = 0 THEN
                         CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                    ELSE CAST(ROUND( ROUND( (ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)), 0, Y.CommRoundFlag) 
                            -(ROUND( ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)+ROUND( ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)), 0, Y.VatRoundFlag) AS INT)
                    END AS PGVT
        FROM (
            SELECT A.PLTID
                  ,A.ID
                  ,A.PGName
                  ,A.PGETC
                  ,B.INCVTAX AS PGINCVTAX
                  ,IIF(B.COMMISSIONTYPE=0, (A.DiscountAmt * (B.COMMISSIONRATE/100.0)), B.COMMISSIONAMT) AS PGCOMM    --수수료타입(0:정율,1:정액)만 계산
                  ,IIF(B.COMMISSIONTYPE=0, (A.DiscountAmt * ((B.COMMISSIONRATE/100.0)+(B.COMMISSIONRATE/100.0/10.0)))
                                         , (B.COMMISSIONAMT+(B.COMMISSIONAMT/10.0))) AS PGCOMM4SUM   --수수료합계(기본수수료 + 기본수수료*10%)
                  ,B.ETCAMT +(B.ETCAMT/10.0) AS PGETC4SUM
            FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)
                  ,SETTLE_POQ_DB.dbo.TPGSettleRate B WITH(NOLOCK)
            WHERE  A.YMD      = B.YMD
            AND    A.PGName   = B.PGName
            AND    A.MallID   = B.MallID
            AND    A.YMD      = @pi_strYMD
            AND    A.PGName   NOT IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))
            AND    A.DiscountFlag = 'Y'
        ) X    
        LEFT OUTER JOIN TPGProperty Y WITH(NOLOCK) ON X.PGName = Y.PGName
    ) BB
    ON     AA.PLTID    = BB.PLTID
    AND    AA.ID       = BB.ID
    WHERE  AA.YMD      = @pi_strYMD
    AND    AA.PGName   NOT IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))
    AND    AA.DiscountFlag = 'Y'
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -102
        RETURN
    END

    --------------------------------------------------
    --인터넷뱅킹(KFTC) 고객사 수수료 처리
    --------------------------------------------------
    --구간별 고정 수수료가 적용되어있는 경우 수수료 재적용, mjbyon, 20130828
    UPDATE TSettleMst
    SET    CLCOMM = dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID,A.PGNAME,A.MALLID, (A.TXAMT-ISNULL(A.NonSettleAmt,0)))    
          ,CLVT   = dbo.UF_GET_ROUND4VAT(dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID,A.PGNAME,A.MALLID,(A.TXAMT-ISNULL(A.NonSettleAmt,0))) * dbo.UF_GET_INCVTAXRATE(CLVTType))
    FROM   TSettleMst        A WITH(NOLOCK)
          ,TClientSettleRate B WITH(NOLOCK)
    WHERE  A.YMD      = B.YMD
    AND    A.CLIENTID = B.CLIENTID
    AND    A.PGNAME   = B.PGNAME
    AND    A.MALLID   = B.MALLID
    AND    A.YMD      = @pi_strYMD
    AND    A.PGNAME   = 'KFTC'
    AND    dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID,A.PGNAME,A.MALLID,(A.TXAMT-ISNULL(A.NonSettleAmt,0))) <> 0
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END
    
    --------------------------------------------------
    --고객사 최저수수료 적용
    --------------------------------------------------
    --최저수수료가 0 인 고객사는 최저수수료 적용 대상 아님
    UPDATE TSettleMst
    SET    CLCOMMTYPE = CASE WHEN ABS(CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT)) THEN
                                  1
                             ELSE CLCOMMTYPE
                        END
          ,CLCOMM     = CASE WHEN ABS(CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT)) THEN
                                  CAST(B.MINCOMMISSIONAMT AS INT)
                             ELSE CLCOMM
                        END
          ,CLVT       = CASE WHEN ABS(CLCOMM) < ABS(CAST(B.MINCOMMISSIONAMT AS INT)) THEN
                                  dbo.UF_GET_ROUND4VAT(B.MINCOMMISSIONAMT * dbo.UF_GET_INCVTAXRATE(CLVTType))
                             ELSE CLVT
                        END
    FROM   TSettleMst        A WITH(NOLOCK)
          ,TClientSettleRate B WITH(NOLOCK)
    WHERE  A.YMD      = B.YMD
    AND    A.CLIENTID = B.CLIENTID
    AND    A.PGNAME   = B.PGNAME
    AND    A.YMD      = @pi_strYMD
    AND    A.PGNAME  IN ('KFTC','YELOPAY','INIBANK','settlevacct','inivacct')
    AND    B.MINCOMMISSIONAMT <> 0
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END
        
    --------------------------------------------------
    --인터넷뱅킹(KFTC) PG 최저수수료 적용
    --------------------------------------------------
    --fic3333, 2009.06.09, 수수료율 DB에서 데이터 참조하여 수수료 계산.
    --kks, 2024-03-29, 500만원초과 구간 삭제.
    UPDATE TSettleMst
    SET    PGCOMMTYPE = 1
          ,PGVT       = 0
          ,PGCOMM     =  CASE WHEN ABS(A.TXAMT) <= 150    THEN  A.TXAMT
					          WHEN ABS(A.TXAMT) > 150 AND ABS(A.TXAMT * (B.COMMISSIONRATE/100)) <= 150    THEN 150
					          WHEN ABS(A.TXAMT * (B.COMMISSIONRATE/100)) > 150 AND ABS(A.TXAMT) <= 100000 THEN ROUND(ABS(A.TXAMT) * (B.COMMISSIONRATE/100),-1,1) --원단위 절삭 FROM YAPER 2005-04-06
					          WHEN ABS(A.TXAMT) > 100000  AND ABS(A.TXAMT) <= 1000000 THEN 1500
					          WHEN ABS(A.TXAMT) > 1000000 THEN 3000
				         END
    FROM   TSettleMst A WITH (NOLOCK)
          ,TPGCMRate  B WITH (NOLOCK)
    WHERE  A.PGNAME = B.PGNAME
    AND    A.MALLID = B.MALLID
    AND    A.YMD    = @pi_strYMD
    AND    A.PGNAME = 'KFTC'
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END       
        
    --------------------------------------------------
    --인터넷뱅킹(INIBANK) PG 수수료 처리
    --------------------------------------------------
    --inibank 최저수수료 200원
    UPDATE TSettleMst
    SET    PGCOMMTYPE = 1
          ,PGCOMM = CASE WHEN ABS(A.PGCOMM) < IIF(MALLID = 'LOLLETTER4', 200, 180) THEN 
                              IIF(MALLID = 'LOLLETTER4', 200, 180)
			             ELSE ABS(A.PGCOMM)
			        END
          ,PGVT   = CASE WHEN ABS(A.PGCOMM) < IIF(MALLID = 'LOLLETTER4', 200, 180) THEN 
                              IIF(MALLID = 'LOLLETTER4', 20, 18)
			             ELSE ABS(A.PGVT)
			        END
    FROM   TSettleMst A WITH (NOLOCK)
    WHERE  A.YMD    = @pi_strYMD
    AND    A.PGNAME = 'INIBANK'
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END
        
    --------------------------------------------------
    --최저수수료 적용 : CheckPay, Toss, TossPoint
    --------------------------------------------------
    --PG 최저수수료
    UPDATE TSettleMst
    SET    PGCOMM   = CAST(B.CommissionMinAmt         AS INT) * (IIF(PGCOMM < 0, -1, 1))
          ,PGVT     = CAST(B.CommissionMinAmt * (0.1) AS INT) * (IIF(PGVT   < 0, -1, 1))
    FROM   TSettleMst A WITH (NOLOCK)
          ,TPGCMRate  B WITH (NOLOCK)
    WHERE  A.PGNAME = B.PGNAME
    AND    A.MALLID = B.MALLID
    AND    A.YMD    = @pi_strYMD
    AND    A.PGNAME IN ('CheckPay','Toss','TossPoint') 
    AND    ABS(A.PGCOMM) < B.CommissionMinAmt
    AND   (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))      --당일 이전 취소건 제외
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -3
        RETURN
    END      
    
    --Client 최저수수료
    UPDATE TSettleMst
    SET    CLCOMM     = CAST(B.MinCommissionAmt * (IIF(CLCOMM < 0, -1, 1)) AS INT)
          ,CLVT       = dbo.UF_GET_ROUND4VAT(B.MinCommissionAmt*dbo.UF_GET_INCVTAXRATE(A.CLVTType)) * (IIF(CLVT < 0, -1, 1))
    FROM   TSettleMst    A WITH (NOLOCK)
          ,TClientCMRate B WITH (NOLOCK)
    WHERE  A.ClientID = B.ClientID
    AND    A.PGNAME   = B.PGNAME
    AND    A.MALLID   = B.MALLID
    AND    A.YMD      = @pi_strYMD
    AND    A.PGNAME  IN ('CheckPay','Toss','TossPoint') 
    AND    ABS(A.CLCOMM) < B.MinCommissionAmt
    AND   (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))      --당일 이전 취소건 제외
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -4
        RETURN
    END      
        
    --------------------------------------------------
    --최저수수료 적용 : EasyBank
    --------------------------------------------------
    --Client 최저수수료(승인건+기본수수료에만 적용)
    UPDATE TSettleMst
    SET    CLCOMM      = CAST(B.MinCommissionAmt AS INT)
          ,CLVT        = dbo.UF_GET_ROUND4VAT((B.MinCommissionAmt+A.CLETC) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
    FROM   TSettleMst    A WITH (NOLOCK)
          ,TClientCMRate B WITH (NOLOCK)
    WHERE  A.ClientID  = B.ClientID
    AND    A.PGNAME    = B.PGNAME
    AND    A.MALLID    = B.MALLID
    AND    A.YMD       = @pi_strYMD
    AND    A.PGNAME   IN ('EasyBank') 
    AND    A.UseState  = 0                   --승인건만
    AND    ABS(A.CLCOMM) < B.MinCommissionAmt
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -5
        RETURN
    END      
    
    --------------------------------------------------
    --카카오페이(카카오머니) PGVT 계산
    -------------------------------------------------- 
    UPDATE TSettleMst
    SET    PGVT     = CAST(ROUND(A.PGCOMM * (0.1),0,dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT)  --[PG원가수수료일치]
    FROM   TSettleMst A WITH (NOLOCK)
          ,TPGCMRate  B WITH (NOLOCK)
    WHERE  A.PGNAME = B.PGNAME
    AND    A.MALLID = B.MALLID
    AND    A.YMD    = @pi_strYMD
    AND    A.PGNAME IN ('kakaopay','KakaoMoney')
    AND    A.TID    = A.CID                                             --카카오페이(카카오머니,카카오카드 중 카카오머니만 강제회수)
    AND   (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))      --당일 이전 취소건 제외
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -10
        RETURN
    END     
    
    --------------------------------------------------
    --INIVAcct 가상계좌 농협 예외처리(2022.02.14)
    -------------------------------------------------- 
    UPDATE TSettleMst
    SET    PGETC    = B.ETCAmtNH
          ,PGVT     = CAST(ROUND(B.ETCAmtNH * (0.1),0,dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT)  --[PG원가수수료일치]
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)
          ,SETTLE_POQ_DB.dbo.TPGCMRate  B WITH(NOLOCK)
          ,PaymentDB.dbo.TVAccountTxMst C WITH(NOLOCK)
    WHERE  A.PGName   = B.PGName
    AND    A.MallID   = B.MallID
    AND    A.PLTID    = C.PLTID
    AND    C.BankCode IN (11,12)                                    --농협(11),단위농협(12)
    AND    A.PGName   = 'inivacct'                                  --INIVAcct 가상계좌만
    AND    A.YMD      = @pi_strYMD
    AND   (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))  --당일 이전 취소건 제외
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -11
        RETURN
    END     

    --------------------------------------------------
    --[원천PG] 프로모션 정보 업데이트(2023.01.01 거래건부터) 
    --------------------------------------------------   
    --부분취소건: UP_UTIL_SETTLE_INS 에서 처리, 환불건: 프로모션 없음.
    UPDATE SETTLE_POQ_DB.dbo.TSettleMst
    SET    DiscountFlag   = B.DiscountFlag
          ,DiscountAmt    = B.DiscountAmt
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)
          ,PLCardDB.dbo.TPLCardTxMst    B WITH(NOLOCK)
    WHERE  A.PLTID        = B.PLTID
    AND    A.YMD          = @pi_strYMD
    AND    A.AYMD        >= '20230101'
    AND    B.DiscountFlag = 'Y'
    AND    A.UseState    IN (0,1)               --상태(0:전체건,1:취소건,2:부분취소건,3:환불건)
    AND    ISNULL(A.ExtraSettleFlag,0) = 0      --차액정산구분(1:차액정산,2:환급정산) 

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -19
        RETURN
    END

    --------------------------------------------------
    --[원천PG] 수수료 적용
    -------------------------------------------------- 
    --판가정책 변경(원천->재판용): SamSungPay(2021.01.01)
    --원가정책 변경(재판용->원가): SSGPayCard / KakaoPay(2021.07.21)
    UPDATE dbo.TSettleMst
    SET    CLCOMM        = IIF(Y.PGName = 'PLCard', ISNULL(X.CLCOMM,0),     Y.CLCOMM)   --원천카드를 제외한 카드의 판가는 재판용 수수료 적용
          ,CLIntComm     = IIF(Y.PGName = 'PLCard', ISNULL(X.CLIntComm,0),  Y.CLIntComm)
          ,CLETC         = IIF(Y.PGName = 'PLCard', ISNULL(X.CLETC,0),      Y.CLETC)
          ,CLVT          = IIF(Y.PGName = 'PLCard', ISNULL(dbo.UF_GET_ROUND4VAT((X.CLCOMM+X.CLETC+X.CLIntComm) * dbo.UF_GET_INCVTAXRATE(Y.CLVTType)),0), Y.CLVT)
          ,PGVTTYPE      = ISNULL(X.PGVTTYPE,0)
          ,PGCOMM        = ISNULL(X.PGCOMM,0)
          ,PGIntRealComm = ISNULL(X.PGIntComm,0) 
          ,PGETC         = ISNULL(X.PGETC,0)        --기타 수수료 타입 (0:정액 1: 정율) - 할부이자 부담율 등   
          ,PGVT          = 0                        --카드사 원가에는 VAT 없음.
          ,ProcState     = CASE WHEN X.CLCOMM    IS NULL 
                                  OR X.CLIntComm IS NULL 
                                  OR X.CLETC     IS NULL 
                                  OR X.PGCOMM    IS NULL 
                                  OR X.PGIntComm IS NULL 
                                  OR X.PGETC     IS NULL 
                                THEN IIF(Y.PGName = 'PLCard', 1, ProcState)
                                ELSE ProcState END  --NULL이 아니면 확인 필요
    FROM (
            SELECT A.PLTID, A.ID
                  ,SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT         (B.ClientID,B.CardCode,B.CardCPID,(A.TxAmt-ISNULL(A.NonSettleAmt,0)),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod) AS CLCOMM
                  ,SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST(B.ClientID,B.CardCode,B.CardCPID,(A.TxAmt-ISNULL(A.NonSettleAmt,0)),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod) AS CLIntComm
                  ,CASE WHEN A.AbroadChk = 1 AND A.UseState = 0 THEN        --해외카드(P021)의 경우 승인 시 트랜잭션Fee 적용
                             ISNULL(D.TransactionFee4Foreign,0)
                        ELSE 0
                   END AS CLETC 
                  ,SETTLE_CARD_DB.dbo.UF_GET_COMM4PG         (B.CardCode,B.CardCPID,IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod) AS PGCOMM
                  ,SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST(B.CardCode,B.CardCPID,IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod) AS PGIntComm
                  ,CASE WHEN C.ETCCommissionType = 0 THEN
                             IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt) * (C.ETCCommissionRate/100.0)    --프로모션건 원가계산방식 변경(2023.01.01) 
                        ELSE C.ETCCommissionAmt
                   END AS PGETC   
                  ,C.IncVTax  AS PGVTTYPE 
            FROM   dbo.TSettleMst                                      A WITH(NOLOCK)
                 INNER JOIN PLCardDB.dbo.TPLCardTxMst                  B WITH(NOLOCK) ON A.PLTID     = B.PLTID
                 INNER JOIN SETTLE_CARD_DB.dbo.TCardContractMgmt       C WITH(NOLOCK) ON B.CardCPID  = C.CardCPID
            LEFT OUTER JOIN SETTLE_CARD_DB.dbo.TClientCardContractMgmt D WITH(NOLOCK) ON B.ClientID  = D.ClientID   --PLCard를 사용하지 않는 가맹점은 가맹점정보를 등록하지 않음.(KakaoPay/SSGPayCard만 사용하는 가맹점)
            WHERE  A.YMD       = @pi_strYMD 
            AND    A.PGNAME    IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))   --원천PG명 
            AND   (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))                      --당일 이전 취소건 제외
        ) X
        ,dbo.TSettleMst Y WITH(NOLOCK)
    WHERE X.PLTID   = Y.PLTID
    AND   X.ID      = Y.ID 
    AND   Y.YMD     = @pi_strYMD  
    AND   Y.PGNAME  IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))              --원천PG명 

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -20
        RETURN
    END

    --------------------------------------------------
    --[원천PG] 수수료 적용 - 부분취소
    --------------------------------------------------
    UPDATE TSettleMst
    SET    CLCOMM =                       SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL(B.ClientID,B.CardCode,B.CardCPID,(A.TxAmt-ISNULL(A.NonSettleAmt,0)),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod,B.YMD)
          ,CLVT   = dbo.UF_GET_ROUND4VAT((SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL(B.ClientID,B.CardCode,B.CardCPID,(A.TxAmt-ISNULL(A.NonSettleAmt,0)),A.AbroadChk,B.CLInterest,B.CheckCardFlag,B.AllotPeriod,B.YMD)+A.CLETC+A.CLIntComm) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)
    JOIN   PLCardDB.dbo.TPLCardTxMst    B WITH(NOLOCK)
    ON     A.PLTID    = B.PLTID
    WHERE  A.YMD      = @pi_strYMD
    AND    A.PGName   = 'PLCard'
    AND    A.UseState = 2

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -201
        RETURN
    END
        
    --------------------------------------------------
    --IMPay 통신사별 수수료 적용
    --------------------------------------------------
    UPDATE TSettleMst
    SET    CLComm =                        dbo.UF_Get_CLComm4MobileCo(A.AYMD,A.ClientID,A.PGName,A.MallID,A.MobileCo,(A.TxAmt-ISNULL(A.NonSettleAmt,0))) 
          ,CLVT   = dbo.UF_Get_Round4Vat( (dbo.UF_Get_CLComm4MobileCo(A.AYMD,A.ClientID,A.PGName,A.MallID,A.MobileCo,(A.TxAmt-ISNULL(A.NonSettleAmt,0)))+A.CLEtc) * dbo.UF_Get_IncVTaxRate(A.CLVTType) )
    FROM   TSettleMst                 A WITH(NOLOCK)
          ,TClientSettleRate4MobileCo B WITH(NOLOCK)
    WHERE  A.AYMD     = B.YMD
    AND    A.ClientID = B.ClientID
    AND    A.PGName   = B.PGName
    AND    A.MallID   = B.MallID
    AND    A.YMD      = @pi_strYMD
    AND    A.PGName   = 'impaymobile'
    AND    B.MobileCoCommApply     = 'Y'                        --통신사별 수수료 적용여부(Y:적용, N:미적용)
    AND    ISNULL(A.MobileCo,'') IN ('1','2','3','4','5','6')   --없으면, 이전 계산된 통합수수료 적용

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -21
        RETURN
    END
        
    --------------------------------------------------
    --Payco 카드금액 적용, 20251111 컬럼을 추가하여 Payco 취소기한인 180일 이후(20260510)에는 아래 로직은 제거해도 됨.
    --------------------------------------------------
    UPDATE SETTLE_POQ_DB.dbo.TSettleMst
    SET    CardAmt   = TxAmt
          ,CouponAmt = 0
          ,MoneyAmt  = 0
          ,PointAmt  = 0
    WHERE  YMD      = @pi_strYMD
    AND    PGName   = 'payco'
    AND    UseState = 1
    AND    TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -27
        RETURN
    END
        
    --------------------------------------------------
    --PointPay/Payco 원가 수수료 적용
    --------------------------------------------------
    UPDATE SETTLE_POQ_DB.dbo.TSettleMst
    SET    PGComm = BB.PGCOMM
          ,PGVT   = BB.PGVT
    FROM   SETTLE_POQ_DB.dbo.TSettleMst AA WITH(NOLOCK)
    JOIN (
        SELECT PLTID
              ,ID
              ,CASE WHEN Y.CommMethod = 0 THEN                                         --수수료계산방식(0:공급가액, 1:수수료합계)
                         CAST(ROUND(X.PGCOMM,          0, Y.CommRoundFlag) AS INT)     --0:반올림, 0<>절사
                    ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM,0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT)
               END AS PGCOMM

              ,CASE WHEN Y.CommMethod = 0 THEN
                         CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                    ELSE CAST(ROUND( ROUND( (ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)), 0, Y.CommRoundFlag) 
                            -(ROUND( ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)+ROUND( ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)), 0, Y.VatRoundFlag) AS INT)
                    END AS PGVT
        FROM (
            SELECT A.PLTID
                  ,A.ID
                  ,A.PGName
                  ,A.PGETC
                  ,PGINCVTAX    = B.incVTax
                  ,PGCOMM       = IIF(B.CommissionType=0, (A.PointAmt*(B.CommissionPointRate/100.0)), B.CommissionAmt)      --수수료타입(0:정율,1:정액)만 계산
                  ,PGCOMM4SUM   = IIF(B.CommissionType=0, (A.PointAmt*((B.CommissionPointRate/100.0)+(B.CommissionPointRate/100.0/10.0)))
                                                        , (B.CommissionAmt+(B.CommissionAmt/10.0)))                         --수수료합계(기본수수료 + 기본수수료*10%)
                  ,PGETC4SUM    = B.ETCAmt+(B.ETCAmt/10.0)
            FROM   TSettleMst A WITH(NOLOCK)
            JOIN   TPGCMRate  B WITH(NOLOCK)
            ON     A.PGName   = B.PGName
            AND    A.MallID   = B.MallID
            WHERE  A.YMD      = @pi_strYMD
            AND    A.PGName   = 'pointpay'
            UNION ALL
            SELECT A.PLTID
                  ,A.ID
                  ,A.PGName
                  ,A.PGETC
                  ,PGINCVTAX    = B.incVTax
                  ,PGCOMM       = IIF(B.CommissionType=0, (A.CardAmt  *(B.CommissionCardRate/100.0))
                                                         +(A.CouponAmt*(B.CommissionCouponRate/100.0))
                                                         +(A.MoneyAmt *(B.CommissionMoneyRate/100.0))
                                                         +(A.PointAmt *(B.CommissionPointRate/100.0))
                                                         , B.CommissionAmt)                                                 --수수료타입(0:정율,1:정액)만 계산
                  ,PGCOMM4SUM   = IIF(B.CommissionType=0, (A.CardAmt  *((B.CommissionCardRate/100.0)  +(B.CommissionCardRate/100.0/10.0)))
                                                         +(A.CouponAmt*((B.CommissionCouponRate/100.0)+(B.CommissionCouponRate/100.0/10.0)))
                                                         +(A.MoneyAmt *((B.CommissionMoneyRate/100.0) +(B.CommissionMoneyRate/100.0/10.0)))
                                                         +(A.PointAmt *((B.CommissionPointRate/100.0) +(B.CommissionPointRate/100.0/10.0)))
                                                        , (B.CommissionAmt+(B.CommissionAmt/10.0)))                         --수수료합계(기본수수료 + 기본수수료*10%)
                  ,PGETC4SUM    = B.ETCAmt+(B.ETCAmt/10.0)
            FROM   TSettleMst A WITH(NOLOCK)
            JOIN   TPGCMRate  B WITH(NOLOCK)
            ON     A.PGName   = B.PGName
            AND    A.MallID   = B.MallID
            WHERE  A.YMD      = @pi_strYMD
            AND    A.PGName   = 'payco'
        ) X    
        LEFT OUTER JOIN TPGProperty Y WITH(NOLOCK) ON X.PGName = Y.PGName
    ) BB
    ON     AA.PLTID    = BB.PLTID
    AND    AA.ID       = BB.ID
    WHERE  AA.YMD      = @pi_strYMD
    AND    AA.PGName   IN ('pointpay', 'payco')
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -28
        RETURN
    END

    
    --------------------------------------------------
    --취소건중 정산되지 않은건은 정산불가 처리(정상,취소건 모두)
    --------------------------------------------------
    UPDATE TSettleMst
    SET    OUTSTATE = 9
    WHERE  PLTID IN
          (SELECT PLTID
           FROM   TSettleMst WITH (NOLOCK)
           WHERE  YMD      = @pi_strYMD
           AND    USESTATE = 1
           AND    OUTSTATE = 9)
    AND    USESTATE = 0
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -29
        RETURN
    END

COMMIT TRAN

RETURN
