
-----------------------------------------------------------------
-- ProcedureName   : UP_UTIL_SETTLE_INS
-- Description     : 일별  정산내역 테이블 INSERT(신규 부분취소 포함)
-- Inner SP        : NONE
-- Return Value    : =0->성공, <>0->실패
-- Copyright ⓒ 2001 by PayLetter Inc. All rights reserved.
-- Author          : copygirl@payletter.com, 2002-07-24
-- Modify History  : sk1004@payletter.com, 2003-08-01, 부가세 원단위 절사
--                   sk1004@payletter.com, 2003-12-04, 정산테이블 스키마 변경에 따른 수정,Procedure ReName
--                   tigerfive, 2009-02-18, 비과세 거래건은 정산테이블에 넣지 않음.
--                   chjeon@payletter.com, 2014-10-10, inicard, inibank, inivacct, inihmoney, inigame, iniculture, inibook 부가세 반올림 처리
--                   kks, 2017-04-05, 사용하지 않는 로직 정리(ADSL 등)
--                   moondae, 2019-11-21, 정산 DB 분리
-----------------------------------------------------------------
CREATE                           PROCEDURE [dbo].[UP_UTIL_SETTLE_INS]
@pi_strYMD                      CHAR(8),
@po_intRetVal                   INT OUT
AS
SET NOCOUNT ON

-------------------------------------------------------
--정산 데이터 확인 : 기정산건이 존재할 수 있으므로 예외처리 추가
-------------------------------------------------------      
IF EXISTS(SELECT PLTID
		  FROM   TSettleMst WITH(NOLOCK)
		  WHERE  YMD      = @pi_strYMD
		  AND    OutState IN (1,5)
		  AND    OutYMD   IS NOT NULL ) BEGIN

	SET @po_intRetVal = -9
    RETURN
END

BEGIN TRAN
    -------------------------------------------------------
    --정산 데이터 삭제
    -------------------------------------------------------        
    IF EXISTS(SELECT PLTID FROM TSettleMst WITH(NOLOCK) WHERE YMD = @pi_strYMD ) BEGIN

        DELETE FROM TSettleMst WHERE YMD = @pi_strYMD   
        IF @@ERROR <> 0 BEGIN
            ROLLBACK TRAN
            SET @po_intRetVal = -1
            RETURN
        END
      
    END

    -------------------------------------------------------
    --정산 데이터 등록 : 거래일자가 @pi_strYMD인 모든 승인내역(취소상태도 포함)
    -------------------------------------------------------    
    -- INCVTAX => 부가가치세(0:미포함, 1:포함)
    -- COMMISSIONTYPE => 정산율(0:정율, 1:정액)
    INSERT INTO TSettleMst (YMD, AYMD, CLIENTID, PGNAME, MALLID
                           ,PLTID, TID, CID, PAYERID, PAYERNAME
                           ,SERVICENAME, PRODUCTNAME, TXAMT, CLCOMMTYPE, CLVTTYPE
                           ,CLCOMM, CLVT, CLETC, PGCOMMTYPE, PGVTTYPE
                           ,PGCOMM, PGVT, PGETC, USESTATE, CLTOTAL
                           ,PGTOTAL, POQINCOME, CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM
                           ,ABROADCHK, NonSettleAmt, SeperateAmt, AllotPeriod, DiscountAmt
                           ,DiscountFlag, PointAmt, MPLTID, MobileCo, CardAmt
                           ,CouponAmt, MoneyAmt)

                    SELECT  X.YMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID
                           ,X.PLTID, X.TID, X.CID, X.PAYERID, X.PAYERNAME
                           ,X.SERVICENAME, X.PRODUCTNAME, X.TXAMT, X.CLCOMMTYPE, X.CLINCVTAX
                           ,CAST(X.CLCOMM AS INT) AS CLCOMM
                           ,dbo.UF_GET_ROUND4VAT((X.CLCOMM + X.CLETC) * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax))  AS CLVT                                               
                           ,CAST(X.CLETC  AS INT) AS CLETC
                           ,X.PGCOMMTYPE
                           ,X.PGINCVTAX
                           --[PG원가수수료일치] 
                           ,CASE WHEN Y.CommMethod = 0 THEN                                         --수수료계산방식(0:공급가액, 1:수수료합계)
                                      CAST(ROUND(X.PGCOMM,          0, Y.CommRoundFlag) AS INT)     --0:반올림, 0<>절사
                                 ELSE CAST(ROUND(ROUND(X.PGCOMM4SUM,0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT)
                            END AS PGCOMM

                           ,CASE WHEN Y.CommMethod = 0 THEN
                                      CAST(ROUND((X.PGCOMM + X.PGETC) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                                 ELSE CAST(ROUND( ROUND( (ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag) + ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)), 0, Y.CommRoundFlag) 
                                         -(ROUND( ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)+ROUND( ROUND(X.PGETC4SUM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)), 0, Y.VatRoundFlag) AS INT)
                            END AS PGVT

                           ,CAST(X.PGETC  AS INT) AS PGETC
                           ,X.USESTATE
                           ,0 AS CLTOTAL
                           ,0 AS PGTOTAL
                           ,0 AS POQINCOME
                           ,0 AS CLINTCOMM
                           ,0 AS PGINTEXPCOMM
                           ,0 AS PGINTREALCOMM
                           ,X.ABROADCHK
                           ,X.NonSettleAmt
                           ,0 AS SeperateAmt
                           ,X.AllotPeriod
                           ,X.DiscountAmt
                           ,X.DiscountFlag
                           ,X.PointAmt
                           ,X.MPLTID
                           ,X.MobileCo
                           ,X.CardAmt
                           ,X.CouponAmt
                           ,X.MoneyAmt
                    FROM  (
                            --------------------------------------------------
                            --전체거래건
                            --------------------------------------------------
                            SELECT @pi_strYMD         AS YMD
                                   ,A.YMD             AS AYMD
                                   ,A.CLIENTID
                                   ,A.PGNAME
                                   ,A.MALLID
                                   ,A.PLTID
                                   ,A.TID
                                   ,A.CID
                                   ,A.PAYERID
                                   ,A.PAYERNAME
                                   ,A.SERVICENAME
                                   ,A.PRODUCTNAME
                                   ,A.TXAMT
                                   ,C.COMMISSIONTYPE  AS CLCOMMTYPE
                                   ,C.INCVTAX         AS CLINCVTAX
                                   ,IIF(C.COMMISSIONTYPE=0, ((A.TXAMT-ISNULL(A.NonSettleAmt,0)) * (C.COMMISSIONRATE/100.0)), C.COMMISSIONAMT) AS CLCOMM
                                   ,C.ETCAMT          AS CLETC
                                   ,B.COMMISSIONTYPE  AS PGCOMMTYPE
                                   ,B.INCVTAX         AS PGINCVTAX                                                              
                                   ,IIF(B.COMMISSIONTYPE=0, (A.TXAMT * (B.COMMISSIONRATE/100.0)), B.COMMISSIONAMT) AS PGCOMM        --수수료타입(0:정율)만 계산
                                   ,B.ETCAMT          AS PGETC
                                   --[PG원가수수료일치]                        
                                   ,IIF(B.COMMISSIONTYPE=0, (A.TXAMT * ((B.COMMISSIONRATE/100.0)+(B.COMMISSIONRATE/100.0/10.0)))    --수수료타입(0:정율)만 계산
                                                                      , (B.COMMISSIONAMT+(B.COMMISSIONAMT/10.0)))  AS PGCOMM4SUM    --수수료합계(기본수수료 + 기본수수료*10%)  
                                   ,B.ETCAMT +(B.ETCAMT/10.0) AS PGETC4SUM 
                                   ,0                         AS USESTATE
                                   ,A.ABROADCHK    
                                   ,ISNULL(A.NonSettleAmt,0)  AS NonSettleAmt  
                                   ,A.AllotPeriod
                                   ,0                        AS DiscountAmt
                                   ,'N'                      AS DiscountFlag
                                   ,A.PointAmt
                                   ,A.MPLTID
                                   ,IIF(A.PGName='impaymobile',A.CardCode,'') AS MobileCo
                                   ,A.CardAmt
                                   ,A.CouponAmt
                                   ,A.MoneyAmt
                            FROM    PaymentDB.dbo.TTxMst A WITH(NOLOCK, INDEX=CIDX_TTxMst_YMD)
                                   ,TPGSettleRate        B WITH(NOLOCK)
                                   ,TClientSettleRate    C WITH(NOLOCK)
                                   ,TPGCMRate            D WITH(NOLOCK)
                            WHERE   A.YMD      = B.YMD
                            AND     A.YMD      = C.YMD
                            AND     A.PGNAME   = B.PGNAME
                            AND     A.PGNAME   = C.PGNAME
                            AND     A.PGNAME   = D.PGNAME
                            AND     A.MALLID   = B.MALLID
                            AND     A.MALLID   = C.MALLID
                            AND     A.MALLID   = D.MALLID
                            AND     A.CLIENTID = C.CLIENTID
                            AND     A.YMD      = @pi_strYMD
                            AND     D.TAXEXEMPTIONFLAG = 0       --비과세용 몰아이디 거래건은 정산데이터 미생성(2009-02-18).
                     
                            --------------------------------------------------
                            --부분취소 거래건 : 정상건으로 INSERT
                            --------------------------------------------------
                            UNION ALL

                            SELECT @pi_strYMD           AS YMD
                                  ,A.YMD                AS AYMD
                                  ,A.CLIENTID
                                  ,A.PGNAME
                                  ,A.MALLID
                                  ,E.PLTID
                                  ,E.TID
                                  ,E.CID
                                  ,A.PAYERID
                                  ,A.PAYERNAME
                                  ,A.SERVICENAME
                                  ,A.PRODUCTNAME
                                  ,E.TXAMT
                                  ,C.COMMISSIONTYPE     AS CLCOMMTYPE
                                  ,C.INCVTAX            AS CLINCVTAX
                                  ,IIF(C.COMMISSIONTYPE=0, (E.TXAMT * (C.COMMISSIONRATE/100.0)), C.COMMISSIONAMT) AS CLCOMM     --수수료타입(0:정율,1:정액)만 계산
                                  ,C.ETCAMT             AS CLETC
                                  ,B.COMMISSIONTYPE     AS PGCOMMTYPE
                                  ,B.INCVTAX            AS PGINCVTAX                                
                                  ,IIF(B.COMMISSIONTYPE=0, (E.TXAMT * (B.COMMISSIONRATE/100.0)), B.COMMISSIONAMT) AS PGCOMM     --수수료타입(0:정율,1:정액)만 계산
                                  ,B.ETCAMT             AS PGETC
                                  --[PG원가수수료일치]
                                  ,IIF(B.COMMISSIONTYPE=0, (E.TXAMT * ((B.COMMISSIONRATE/100.0)+(B.COMMISSIONRATE/100.0/10.0)))
                                                         , (B.COMMISSIONAMT+(B.COMMISSIONAMT/10.0))) AS PGCOMM4SUM              --수수료합계(기본수수료 + 기본수수료*10%)  
                                  ,B.ETCAMT +(B.ETCAMT/10.0)  AS PGETC4SUM 
                                  ,2                    AS USESTATE
                                  ,A.ABROADCHK                
                                  ,0                    AS NonSettleAmt 
                                  ,A.AllotPeriod
                                  ,IIF(E.DiscountFlag='Y',E.DiscountAmt,0) AS DiscountAmt
                                  ,E.DiscountFlag                          AS DiscountFlag
                                  ,E.PointAmt
                                  ,A.MPLTID
                                  ,IIF(A.PGName='impaymobile',A.CardCode,'') AS MobileCo
                                  ,E.CardAmt
                                  ,E.CouponAmt
                                  ,E.MoneyAmt
                            FROM   PaymentDB.dbo.TTxMst              A WITH(NOLOCK)
                                  ,TPGSettleRate                     B WITH(NOLOCK)
                                  ,TClientSettleRate                 C WITH(NOLOCK)
                                  ,TPGCMRate                         D WITH(NOLOCK)
                                  ,PaymentDB.dbo.TPartialCancelTxMst E WITH(NOLOCK)
                            WHERE  A.YMD      = B.YMD
                            AND    A.YMD      = C.YMD
                            AND    A.PGNAME   = B.PGNAME
                            AND    A.PGNAME   = C.PGNAME
                            AND    A.PGNAME   = D.PGNAME
                            AND    A.MALLID   = B.MALLID
                            AND    A.MALLID   = C.MALLID
                            AND    A.MALLID   = D.MALLID
                            AND    A.CLIENTID = C.CLIENTID
                            AND    A.PLTID    = E.PLTID
                            AND    E.YMD      = @pi_strYMD
                            AND    D.TAXEXEMPTIONFLAG = 0   --비과세용 몰아이디 거래건은 정산데이터 미생성(2009-02-18).
                            AND    E.SettleState = 1        --정산대상여부(1:정산대상, 2:정산미대상)
                            
                            --------------------------------------------------
                            --환불건
                            --------------------------------------------------
                            UNION ALL

                            SELECT @pi_strYMD                AS YMD
                                  ,A.YMD                     AS AYMD
                                  ,A.CLIENTID
                                  ,A.PGNAME
                                  ,A.MALLID
                                  ,A.PLTID
                                  ,A.TID
                                  ,A.CID
                                  ,A.PAYERID
                                  ,A.PAYERNAME
                                  ,A.SERVICENAME
                                  ,A.PRODUCTNAME
                                  ,E.REFUNDREQAMT            AS TXAMT
                                  ,C.COMMISSIONTYPE          AS CLCOMMTYPE
                                  ,C.INCVTAX                 AS CLINCVTAX
                                  /*
                                  ,CASE WHEN C.COMMISSIONTYPE=0 THEN
                                             E.REFUNDREQAMT * (C.COMMISSIONRATE/100.0)
                                        ELSE IIF(F.FeeFlag='Y' AND F.FeeCharge=1, 0, C.COMMISSIONAMT-E.CLIENTFEEAMT)
                                   END AS CLCOMM
                                  ,IIF(F.FeeFlag='Y' AND F.FeeCharge=1, 0, C.ETCAMT-E.CLIENTFEEAMT)                      AS CLETC   --기타수수료 - 환불수수료
                                  */
                                  ----------------------------------------------------
                                  --FeeFlag    : 수수료 유무(Y:부과,N:미부과)
                                  --FeeRateType: 수수료율 유형(3:직접입력(%),4:직접입력(원))
                                  ,CASE WHEN F.FeeFlag='Y' THEN 
                                             IIF(F.FeeRateType=3,    (E.RefundReqAmt * (F.FeeRate/100.0))       , F.FeeRate) * (-1) --가맹점 환불수수료 부과
                                        ELSE IIF(C.CommissionType=0, (E.RefundReqAmt * (C.CommissionRate/100.0)), C.CommissionAmt)  --FeeRateType=4(정액)일경우, 환불시마다 동일(FeeRate) 수수료발생!!
                                   END  AS CLComm
                                  ,CASE WHEN F.FeeFlag='Y' THEN 0 ELSE C.EtcAmt END AS CLEtc
                                  ----------------------------------------------------

                                  ,B.COMMISSIONTYPE          AS PGCOMMTYPE
                                  ,B.INCVTAX                 AS PGINCVTAX                                
                                  ,0                         AS PGCOMM
                                  ,0                         AS PGETC
                                  --[PG원가수수료일치]
                                  ,0  AS PGCOMM4SUM                         --수수료합계(기본수수료 + 기본수수료*10%)  
                                  ,0  AS PGETC4SUM 
                                  ,3                         AS USESTATE    --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)
                                  ,A.ABROADCHK      
                                  ,0                         AS NonSettleAmt  
                                  ,A.AllotPeriod                      
                                  ,0                        AS DiscountAmt
                                  ,'N'                      AS DiscountFlag   
                                  ,A.PointAmt  
                                  ,A.MPLTID   
                                  ,IIF(A.PGName='impaymobile',A.CardCode,'') AS MobileCo      
                                  ,0                        AS CardAmt
                                  ,0                        AS CouponAmt
                                  ,0                        AS MoneyAmt
                            FROM   PaymentDB.dbo.TTxMst        A WITH(NOLOCK)
                                  ,TPGSettleRate               B WITH(NOLOCK)
                                  ,TClientSettleRate           C WITH(NOLOCK)
                                  ,TPGCMRate                   D WITH(NOLOCK)
                                  ,PaymentDB.dbo.TRefundMst    E WITH(NOLOCK)
                                  ,PaymentDB.dbo.TRefundClient F WITH(NOLOCK)
                            WHERE  A.YMD      = B.YMD
                            AND    A.YMD      = C.YMD
                            AND    A.PGNAME   = B.PGNAME
                            AND    A.PGNAME   = C.PGNAME
                            AND    A.PGNAME   = D.PGNAME
                            AND    A.PGNAME   = F.PGNAME
                            AND    A.MALLID   = B.MALLID
                            AND    A.MALLID   = C.MALLID
                            AND    A.MALLID   = D.MALLID
                            AND    A.MALLID   = F.MALLID
                            AND    A.CLIENTID = C.CLIENTID
                            AND    A.CLIENTID = F.CLIENTID
                            AND    A.PLTID    = E.PLTID
                            AND    E.REQYMD   = @pi_strYMD
                            AND    D.TAXEXEMPTIONFLAG = 0   --비과세용 몰아이디 거래건은 정산데이터 미생성(2009-02-18).
                    ) X 
                    LEFT OUTER JOIN TPGProperty Y WITH(NOLOCK) ON X.PGName = Y.PGName

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END

COMMIT TRAN

RETURN
