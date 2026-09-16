

----------------------------------------------------------------    
-- ProcedureName   : UP_Util_Settle_Expect_Proc
-- Description     : 회수일 및 정산일 처리(수납건 제외)
-- Copyright ⓒ 2025 by PayLetter Inc. All rights reserved.   
-- Author          : kks, 2025-07-30 
-----------------------------------------------------------------    
CREATE                           PROCEDURE [dbo].[UP_UTIL_SETTLE_EXPECT_PROC]
@pi_strYMD                      CHAR(8),   
@po_intRetVal                   INT OUTPUT    
AS    
    
SET NOCOUNT ON    
    
DECLARE @v_PLCardSettlePeriodPG VARCHAR(200) = 'PLCard,SamSungPay,NaverCard,ApplePay,TossCardAuth'  --원천(PLCard) 사용PG

BEGIN TRAN  

---------------------------------------------------
--1.자동회수처리
---------------------------------------------------
    ---------------------------------
    --1-1.자동회수 처리
    ---------------------------------
    --원천 사용PG 제외 + IMpay(선정산도 수납회수처리: 회수율관리)
    UPDATE TSettleMst     
    SET    InState = 1 
          ,INYMD   = dbo.UF_GET_COLLECTYMD(@pi_strYMD,B.CollectPeriodID)  
    FROM   TSettleMst          A WITH(NOLOCK)       
          ,TPGCMRate           B WITH(NOLOCK)  
          ,TPGCollectPeriodMst C WITH(NOLOCK)  
    WHERE  A.PGNAME          = B.PGNAME     
    AND    A.MALLID          = B.MALLID  
    AND    A.YMD             = @pi_strYMD  
    AND    B.CollectPeriodID = C.CollectPeriodID  
    AND    A.INSTATE         = 0    
    AND    C.CollectFlag     = 1
    AND    A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth')
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END

    --원천 사용PG 일부 예외(KakaoPay_Money)
    UPDATE TSettleMst     
    SET    InState = 1 
          ,INYMD   = dbo.UF_GET_COLLECTYMD(@pi_strYMD,B.CollectPeriodID)  
    FROM   TSettleMst          A WITH(NOLOCK)       
          ,TPGCMRate           B WITH(NOLOCK)  
          ,TPGCollectPeriodMst C WITH(NOLOCK)  
    WHERE  A.PGNAME          = B.PGNAME     
    AND    A.MALLID          = B.MALLID  
    AND    A.YMD             = @pi_strYMD  
    AND    B.CollectPeriodID = C.CollectPeriodID  
    AND    A.INSTATE         = 0    
    AND    C.CollectFlag     = 1
    AND   (A.PGName IN ('KakaoPay','KakaoMoney') AND A.TID = A.CID)
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END
    
    --회수주기 오설정으로 상태는 회수, 회수일이 공백일 경우 상태 원복
    UPDATE TSettleMst     
    SET    InState = 0
    FROM   TSettleMst          A WITH(NOLOCK)       
          ,TPGCMRate           B WITH(NOLOCK)  
          ,TPGCollectPeriodMst C WITH(NOLOCK)  
    WHERE  A.PGNAME          = B.PGNAME     
    AND    A.MALLID          = B.MALLID  
    AND    A.YMD             = @pi_strYMD  
    AND    B.CollectPeriodID = C.CollectPeriodID  
    AND    A.INSTATE         = 1    
    AND    C.CollectFlag     = 1
    AND    ISNULL(A.InYMD,'')= ''
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -3
        RETURN
    END
    
    ---------------------------------
    --1-2.인터넷뱅킹(KFTC) 환불건 미회수 처리: 페이레터 계좌에서 환불처리
    ---------------------------------
    --당일+전체취소 : 회수처리
    --당일이후 +부분취소, 익일이후+전체취소/부분취소 : 미회수처리
    UPDATE TSettleMst
    SET    INSTATE  = 0    
          ,INYMD    = NULL   
    FROM   TSettleMst     A WITH(NOLOCK)          
          ,TClientCMRate  B WITH(NOLOCK)          
    WHERE  A.CLIENTID   = B.CLIENTID          
    AND    A.PGNAME     = B.PGNAME          
    AND    A.MALLID     = B.MALLID          
    AND    A.YMD        = @pi_strYMD  
    AND    A.PGNAME    IN ('KFTC')
    AND  ((A.USESTATE   = 2) OR (A.CYMD > A.AYMD AND A.USESTATE = 1))   --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건) 
    AND    B.RefundFlag = 'Y'                                           --(계좌이체) 환불서비스 사용여부
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -4
        RETURN
    END  
    
    ---------------------------------
    --1-3.환불건 미회수 처리: 페이레터 계좌에서 환불처리 (2020.12.01 회수건부터)
    ---------------------------------
    UPDATE TSettleMst
    SET    INSTATE  = 0    
          ,INYMD    = NULL        
    WHERE  YMD      = @pi_strYMD  
    AND    USESTATE = 3           --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)
    AND    ISNULL(INYMD,'') <> ''
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -5
        RETURN
    END   
  
---------------------------------------------------
--2.자동정산처리
---------------------------------------------------
    --TClientContract B: 고객사정산주기(0:1일,1:1주,2:2주,3:1개월,4:1개월말일,5:1개월초)    
    --TClientCMRate   C: 통신군정산주기(1:1개월,2:2개월,3:3개월,4:1주,5:1주초), 상품권정산주기(1:1개월,2:2개월), 일정산주기(3:D+3일, 4:D+4일...) 

    ---------------------------------
    --2-1.선정산 처리(통신군) : 수납정산은 수납시 처리
    ---------------------------------     
    UPDATE TSettleMst     
    SET    OutState      = 2
          ,OutYMD        = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))
    FROM   TSettleMst      A WITH(NOLOCK)
    JOIN   TClientContract B WITH(NOLOCK) ON A.ClientID = B.ClientID 
    JOIN   TClientCMRate   C WITH(NOLOCK) ON A.ClientID = C.ClientID AND A.PGName = C.PGName AND A.MallID = C.MallID     
    JOIN   TPGCMRate       D WITH(NOLOCK) ON A.PGName   = D.PGName   AND A.MallID = D.MallID  
    WHERE  A.YMD         = @pi_strYMD    
    AND    A.OutState    = 0
    AND    LEFT(D.PayToolType,1) IN ('C')           --C:통신군
    AND    ISNULL(C.UnCollectImpose,1) = 0          --선정산 여부(0:선정산, 0<>수납정산)
    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -10    
        RETURN    
    END 

    ---------------------------------
    --2-2.정산 처리(금융군+상품권군)
    ---------------------------------
    UPDATE TSettleMst     
    SET    OutState          = 2     
          ,OutYMD            = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))  
    FROM   TSettleMst          A WITH(NOLOCK)  
    JOIN   TClientContract     B WITH(NOLOCK) ON A.ClientID = B.ClientID  
    JOIN   TClientCMRate       C WITH(NOLOCK) ON A.ClientID = C.ClientID  AND A.PGName = C.PGName AND A.MallID = C.MallID 
    JOIN   TPGCMRate           D WITH(NOLOCK) ON A.PGName   = D.PGName    AND A.MallID = D.MallID 
    JOIN   TPGCollectPeriodMst E WITH(NOLOCK) ON D.CollectPeriodID = E.CollectPeriodID
    WHERE  A.YMD             = @pi_strYMD    
    AND    A.OutState        = 0    
    AND    LEFT(D.PayToolType,1) IN ('A','B')                                               --A:금융군, B:상품권군
    AND    E.CollectFlag         IN (1,9)                                                   --회수구분(1:자동회수,7:수납회수,8:미회수,9:미지정)
    AND    A.PGName NOT IN (SELECT VALUE FROM STRING_SPLIT(@v_PLCardSettlePeriodPG,','))    --원천PG 사용(매입(수동))
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -11
        RETURN
    END

    
    --------------------------------------------------
    --2-3.[원천PG] 정산일 설정 (자동매입)(회수와 상관없이 정산일 설정)
    --------------------------------------------------
    UPDATE dbo.TSettleMst
    SET    OutState    = IIF(OutState=0, 2, OutState)                                        --지급상태(0:미지급,1:지급완료,2:지급예정,5:지급확정,9:지급불가)
          ,OutYMD      = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, B.SettlePeriodID))
    FROM    SETTLE_POQ_DB.dbo.TSettleMst              A WITH(NOLOCK) 
    JOIN    SETTLE_POQ_DB.dbo.TClientCMRate           B WITH(NOLOCK) ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID 
    JOIN   SETTLE_CARD_DB.dbo.TClientCardContractMgmt C WITH(NOLOCK) ON A.ClientID = C.ClientID
    WHERE  A.YMD       = @pi_strYMD
    AND    A.PGName   IN (SELECT VALUE FROM STRING_SPLIT(@v_PLCardSettlePeriodPG,','))
    AND    A.OutState IN (0,9)
    AND    C.AcqType   = 0                                                                   --수동매입건 제외(매입일 기준으로 정산일 설정), AcqType매입방식(0:자동,1:수동)

    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -12   
        RETURN    
    END 
    
    --------------------------------------------------
    --2-4.[원천PG] 정산일 설정 (수동매입: 매입요청일 기준)
    --------------------------------------------------  
    UPDATE dbo.TSettleMst
    SET    OutState   = IIF(OutState=0, 2, OutState)
          ,OutYMD     = (SELECT OutYMD FROM dbo.UIF_SettleYMD(E.ReqYMD, B.SettlePeriodID))
          ,EDIReqYmd  = E.ReqYMD   
    FROM    SETTLE_POQ_DB.dbo.TSettleMst              A WITH(NOLOCK) 
    JOIN    SETTLE_POQ_DB.dbo.TClientCMRate           B WITH(NOLOCK) ON A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
    JOIN   SETTLE_CARD_DB.dbo.TClientCardContractMgmt C WITH(NOLOCK) ON A.ClientID = C.ClientID
    JOIN   SETTLE_CARD_DB.dbo.TPLCardEDIMst           E WITH(NOLOCK) ON A.PLTID    = E.PLTID  AND A.YMD = E.YMD AND A.UseState = E.UseState 
                                                                    AND ABS(IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt)) = ABS(E.Amt)
    WHERE  A.PGName   IN (SELECT VALUE FROM STRING_SPLIT(@v_PLCardSettlePeriodPG,','))
    AND    A.OutState IN (0,9)
    AND    E.ReqYMD    = @pi_strYMD     --매입요청일(D)+1 : 집계 고려
    AND    E.AcqType   = 1              --AcqType 매입방식(0:자동,1:수동)

    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -13   
        RETURN    
    END 
     
    ---------------------------------
    --2-5.환불건 정산처리
    ---------------------------------
    UPDATE TSettleMst     
    SET    OUTSTATE = 2
          ,OUTYMD   = CASE WHEN ISNULL(dbo.UF_GET_OUTYMD4REFUND(ClientID,PGName,MallID,YMD), '') = '' 
                           THEN OutYMD
                           ELSE dbo.UF_GET_OUTYMD4REFUND(ClientID,PGName,MallID,YMD) END
    FROM   TSettleMst WITH(NOLOCK)   
    WHERE  YMD      = @pi_strYMD   
    AND    USESTATE = 3         --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)
    AND    OUTSTATE IN (0,2)
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -15
        RETURN
    END

    
    ---------------------------------
    --2-6.복합결제 (포인트결제 정산주기: 주결제수단(카드)의 정산주기로 재설정)
    ---------------------------------
    UPDATE A
    SET    OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK)    --복합결제중 포인트결제
    JOIN   SETTLE_POQ_DB.dbo.TSettleMst B WITH(NOLOCK)    --복합결제중 주결제수단(신용카드)
      ON   A.MPLTID   = B.PLTID
    JOIN   SETTLE_POQ_DB.dbo.TClientCMRate C WITH(NOLOCK)
      ON   B.ClientID = C.ClientID
     AND   B.PGName   = C.PGName
     AND   B.MallID   = C.MallID
    WHERE  A.YMD      = @pi_strYMD
    AND    A.PGName   = 'pointpay'
    AND    B.PGName  IN ('plcard','impaymobile')       --복합결제 가능 결제수단
    AND    A.OutState = 2
        
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -17
        RETURN
    END
         
COMMIT TRAN    
    
RETURN 
