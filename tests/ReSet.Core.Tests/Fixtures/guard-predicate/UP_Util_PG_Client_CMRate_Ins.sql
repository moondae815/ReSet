
/****** 개체: 저장 프로시저 dbo.UP_UTIL_PG_CLIENT_CMRATE_INS    스크립트 날짜: 2008-03-10 오후 2:07:11 ******/  
-----------------------------------------------------------------    
-- ProcedureName   : UP_Util_PG_Client_CMRate_Ins    
-- Description     : PG, 고객사 일별 수수료율 입력    
-- Copyright ⓒ 2019 by PayLetter Inc. All rights reserved.    
-- Author          : kks, 2019-04-30 
-- Modify History  : Just Created.    
-----------------------------------------------------------------    
CREATE                       PROCEDURE [dbo].[UP_Util_PG_Client_CMRate_Ins]    
@pi_strYMD                  CHAR(8),        
@po_intRetVal               INT OUTPUT    
AS    
    
SET NOCOUNT ON    

-------------------------------------------------------
--기정산건이 존재할 수 있으므로 예외처리 추가
-------------------------------------------------------
IF EXISTS(SELECT PLTID
          FROM   TSettleMst WITH(NOLOCK)
          WHERE  YMD       = @pi_strYMD
          AND    OutState IN (1,5)
          AND    OutYMD   IS NOT NULL ) BEGIN 
    SET @po_intRetVal = -9    
    RETURN    
END            
    
-------------------------------------------------------
--일별 수수료율 등록(PG사 & 고객사) 
-------------------------------------------------------  
BEGIN TRAN    
    
    --------------------------------------------------
    --PG사 수수료율
    --------------------------------------------------
    --TPGSETTLERATE 삭제
    DELETE FROM TPGSettleRate    
    WHERE  YMD = @pi_strYMD    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -1    
        RETURN    
    END    
    
    --TPGSETTLERATE 등록
    INSERT INTO TPGSettleRate (YMD, PGNAME, MALLID, VERSION, INCVTAX    
                              ,COMMISSIONTYPE, COMMISSIONRATE, COMMISSIONFOREIGNRATE, COMMISSIONAMT    
                              ,ETCAMT, COMMISSIONCANCELFLAG, COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose
                              ,CollectPeriodID, ETCAmtNH)    
                        SELECT @pi_strYMD, PGNAME, MALLID, VERSION, INCVTAX    
                              ,COMMISSIONTYPE, COMMISSIONRATE, COMMISSIONFOREIGNRATE, COMMISSIONAMT    
                              ,ETCAMT, COMMISSIONCANCELFLAG, COMMISSIONCANCELAMT, COMMISSIONMINAMT, UnCollectImpose 
                              ,CollectPeriodID, ETCAmtNH
                        FROM   TPGCMRate WITH (NOLOCK)    
                        WHERE  USESTATE = 0    --사용상태(0:정상, 1:사용중지)    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -2    
        RETURN    
    END        
    
    --------------------------------------------------
    --고객사 수수료율
    --------------------------------------------------
    --TCLIENTSETTLERATE 삭제
    DELETE FROM TClientSettleRate    
    WHERE  YMD = @pi_strYMD    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -3    
        RETURN    
    END        
    
    --TCLIENTSETTLERATE 등록
    INSERT INTO TClientSettleRate(YMD, CLIENTID, PGNAME, MALLID, VERSION    
                                 ,INCVTAX, COMMISSIONTYPE, COMMISSIONRATE, COMMISSIONFOREIGNRATE, COMMISSIONAMT    
                                 ,MINCOMMISSIONAMT, ETCAMT, SETTLEPERIOD, UNCOLLECTIMPOSE, USESTATE
                                 ,COMMISSIONCANCELFLAG, COMMISSIONCANCELAMT, SettlePeriodDD
                                 ,SettleCurrency, SettleYMDType, SettleBasicSeq, ModifyType, ModifyCommType, ModifyCommRate, ModifyCommAmt                                 
                                 ,PartnerCommType,PartnerCommRate,PartnerCommAmt,PartnerMinCommAmt,PartnerCommCancelFlag,PartnerCommCancelAmt
                                 ,RefundFlag, RefundFeeType, RefundFee, AuthSettleType, MinimumUnitCnt
                                 ,SeperateFlag, SeperateStandard, SeperateType, SeperateRate, SeperateTarget
                                 ,SettlePeriodFlag, TaxFGBill, SettlePeriodID)     
                           SELECT @pi_strYMD, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION    
                                 ,B.INCVTAX, B.COMMISSIONTYPE, B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT    
                                 ,B.MINCOMMISSIONAMT, B.ETCAMT, B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE
                                 ,B.COMMISSIONCANCELFLAG, B.COMMISSIONCANCELAMT, B.SettlePeriodDD
                                 ,A.SettleCurrency, A.SettleYMDType, A.SettleBasicSeq, A.ModifyType, A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt                                 
                                 ,B.PartnerCommType,B.PartnerCommRate,B.PartnerCommAmt,B.PartnerMinCommAmt,B.PartnerCommCancelFlag,B.PartnerCommCancelAmt
                                 ,B.RefundFlag, B.RefundFeeType, B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt
                                 ,B.SeperateFlag, B.SeperateStandard, B.SeperateType, B.SeperateRate, B.SeperateTarget
                                 ,B.SettlePeriodFlag, A.TaxFGBill, B.SettlePeriodID
                           FROM   TClientContract A WITH (NOLOCK)    
                                 ,TClientCMRate   B WITH (NOLOCK)    
                           WHERE  A.CLIENTID = B.CLIENTID    
                           AND    A.USESTATE IN (0,4,5,6)   --상태(0:정상,1:계약신청,2:계약중,3:계약거절,4:정산중지,5:승인중지,6:정산및승인중지,7:계약해지)   
                           AND    B.USESTATE IN (0,4)       --상태(0:정상,1:계약신청,2:계약중,3:계약거절,4:승인중지,5:계약해지) 

                           UNION ALL                              
                           SELECT @pi_strYMD, B.CLIENTID, B.PGNAME, B.MALLID, B.VERSION    
                                 ,B.INCVTAX, B.COMMISSIONTYPE, B.COMMISSIONRATE, B.COMMISSIONFOREIGNRATE, B.COMMISSIONAMT    
                                 ,B.MINCOMMISSIONAMT, B.ETCAMT, B.SETTLEPERIOD, B.UNCOLLECTIMPOSE, B.USESTATE
                                 ,B.COMMISSIONCANCELFLAG, B.COMMISSIONCANCELAMT, B.SettlePeriodDD
                                 ,A.SettleCurrency, A.SettleYMDType, A.SettleBasicSeq, A.ModifyType, A.ModifyCommType, A.ModifyCommRate, A.ModifyCommAmt                                 
                                 ,B.PartnerCommType,B.PartnerCommRate,B.PartnerCommAmt,B.PartnerMinCommAmt,B.PartnerCommCancelFlag,B.PartnerCommCancelAmt
                                 ,B.RefundFlag, B.RefundFeeType, B.RefundFee, B.AuthSettleType, B.MinimumUnitCnt
                                 ,B.SeperateFlag, B.SeperateStandard, B.SeperateType, B.SeperateRate, B.SeperateTarget
                                 ,B.SettlePeriodFlag, A.TaxFGBill, B.SettlePeriodID
                           FROM   TClientContract A WITH (NOLOCK)    
                                 ,TClientCMRate   B WITH (NOLOCK)    
                           WHERE  A.CLIENTID = B.CLIENTID        
                           AND    B.USESTATE = 5
                           AND   (A.ContractCancelYMD = @pi_strYMD  OR B.ContractCancelYMD = @pi_strYMD )
                           --AND    B.ContractCancelYMD = @pi_strYMD 
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -4    
        RETURN    
    END    
    
    --------------------------------------------------
    --차액정산 (PG사 수수료율)
    --------------------------------------------------
    --TPGSettleRate4Extra 삭제
    DELETE FROM TPGSettleRate4Extra    
    WHERE  YMD = @pi_strYMD    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -5    
        RETURN    
    END    
    
    --TPGSettleRate4Extra 등록
    INSERT INTO TPGSettleRate4Extra (YMD, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3)    
                              SELECT @pi_strYMD, PGName, MallID, incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3 
                              FROM   TPGCMRate WITH(NOLOCK)   
                              WHERE  USESTATE = 0    --PG 계약상태(정상)  
                              AND    PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')  
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -6    
        RETURN    
    END     
    
    --------------------------------------------------
    --차액정산 (고객사 수수료율)
    --------------------------------------------------
    --TClientSettleRate4Extra 삭제
    DELETE FROM TClientSettleRate4Extra    
    WHERE  YMD = @pi_strYMD    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -7    
        RETURN    
    END        
    
    --TClientSettleRate4Extra 등록
    INSERT INTO TClientSettleRate4Extra(YMD, ClientID, PGName, MallID, SettlePeriod, CompanySalesType, ExtraSettleFlag
                                       ,incVTax, CommissionRate, CommRate0, CommRate1, CommRate2, CommRate3
                                       ,PartnerCommRate, PartnerCommRate0, PartnerCommRate1, PartnerCommRate2, PartnerCommRate3
                                       ,ExtraCommFlag, ExtraCommTarget, SettlePeriodID)     
                                 SELECT @pi_strYMD, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod,0), C.CompanySalesType, C.ExtraSettleFlag
                                       ,B.incVTax, B.CommissionRate, D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3
                                       ,ISNULL(B.PartnerCommRate,0), D.PartnerCommRate0, D.PartnerCommRate1, D.PartnerCommRate2, D.PartnerCommRate3
                                       ,B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
                                 FROM   TClientContract     A WITH(NOLOCK)    
                                       ,TClientCMRate       B WITH(NOLOCK)     
                                       ,TClient             C WITH(NOLOCK)   
                                       ,TClientCMRate4Extra D WITH(NOLOCK)
                                 WHERE  A.ClientID = B.ClientID 
                                 AND    A.ClientID = C.ClientID 
                                 AND    B.ClientID = D.ClientID 
                                 AND    B.PGName   = D.PGName 
                                 AND    B.MallID   = D.MallID    
                                 AND    A.UseState IN (0,4,5,6)   --상태(0:정상,1:계약신청,2:계약중,3:계약거절,4:정산중지,5:승인중지,6:정산및승인중지,7:계약해지)   
                                 AND    B.UseState IN (0,4)       --상태(0:정상,1:계약신청,2:계약중,3:계약거절,4:승인중지,5:계약해지)
                                  
                                 UNION ALL                                   
                                 SELECT @pi_strYMD, D.ClientID, D.PGName, D.MallID, ISNULL(B.SettlePeriod,0), C.CompanySalesType, C.ExtraSettleFlag
                                       ,B.incVTax, B.CommissionRate, D.CommRate0, D.CommRate1, D.CommRate2, D.CommRate3
                                       ,ISNULL(B.PartnerCommRate,0), D.PartnerCommRate0, D.PartnerCommRate1, D.PartnerCommRate2, D.PartnerCommRate3
                                       ,B.ExtraCommFlag, B.ExtraCommTarget, B.SettlePeriodID
                                 FROM   TClientContract     A WITH(NOLOCK)    
                                       ,TClientCMRate       B WITH(NOLOCK)     
                                       ,TClient             C WITH(NOLOCK)   
                                       ,TClientCMRate4Extra D WITH(NOLOCK)
                                 WHERE  A.ClientID = B.ClientID 
                                 AND    A.ClientID = C.ClientID 
                                 AND    B.ClientID = D.ClientID 
                                 AND    B.PGName   = D.PGName 
                                 AND    B.MallID   = D.MallID           
                                 AND    B.USESTATE = 5
                                 AND    B.ContractCancelYMD = @pi_strYMD 
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -8    
        RETURN    
    END      
    
    --------------------------------------------------
    --통신사별 수수료
    --------------------------------------------------
    --TClientSettleRate4MobileCo 삭제
    DELETE FROM TClientSettleRate4MobileCo    
    WHERE  YMD = @pi_strYMD    
    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -9    
        RETURN    
    END    
    
    --TClientSettleRate4MobileCo 등록
    INSERT INTO TClientSettleRate4MobileCo (YMD, ClientID, PGName, MallID, MobileCoCommApply, MobileCo1, MobileCo2, MobileCo3, MobileCo4, MobileCo5, MobileCo6 )    
                                     SELECT @pi_strYMD, A.ClientID, A.PGName, A.MallID, A.MobileCoCommApply
                                           ,B.MobileCo1, B.MobileCo2, B.MobileCo3, B.MobileCo4, B.MobileCo5, B.MobileCo6
                                     FROM   TClientCMRate          A WITH(NOLOCK)   
                                           ,TClientCMRate4MobileCo B WITH(NOLOCK)   
                                     WHERE  A.ClientID = B.ClientID
                                     AND    A.PGName   = B.PGName
                                     AND    A.MallID   = B.MallID

    IF @@ERROR <> 0 BEGIN    
        ROLLBACK TRAN    
        SET @po_intRetVal = -10    
        RETURN    
    END     
     
    
COMMIT TRAN    
    
RETURN

