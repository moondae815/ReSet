
-----------------------------------------------------------------
-- ProcedureName   : UP_Util_Settle_Ins_Extra
-- Description     : 영중소 차액정산 데이터 등록
-- Copyright ⓒ 2001 by PayLetter Inc. All rights reserved.
-- Author          : kks, 2019-04-30
-- Modify History  : moondae, 2019-11-21, 정산 DB 분리
--                 : dhhwnag@payletter.com, 2021-05-13, 토스카드 추가
-----------------------------------------------------------------
CREATE                           PROCEDURE [dbo].[UP_UTIL_SETTLE_INS_EXTRA]
@pi_strYMD                      CHAR(8),    --입금일
@po_intRetVal                   INT OUT
AS
SET NOCOUNT ON

DECLARE @v_strReqYMD            VARCHAR(8) = ''
DECLARE @v_strCurrYMD           VARCHAR(8) = CONVERT(VARCHAR(8), GETDATE(), 112)
-------------------------------------------------------
--차액정산 요청일 조회(성능상의 이슈로 거래일(요청일) 조회 후 검색
-------------------------------------------------------  
SELECT @v_strReqYMD = MIN(ReqYMD)
FROM   PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)
WHERE  ResYMD       = @pi_strYMD
AND    ResultCode   = '00'
AND    RefundTxType <> 1            --RefundTxType: 환급거래구분(0:비대상,1:대상)

-------------------------------------------------------
--정산 데이터 확인
-------------------------------------------------------      
IF EXISTS(SELECT PLTID
          FROM   TSettleMst WITH(NOLOCK)
          WHERE  ProcYMD   = @pi_strYMD
          AND    YMD      >= @v_strReqYMD
          AND    OutState IN (1,5)
          AND    OutYMD   IS NOT NULL
          AND    PGName   IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')    --재판매 신용카드
          AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)                --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
          AND    ExtraSettleFlag = 1 ) BEGIN                            --차액정산구분(1:차액정산, 2:환급정산) :2022.09.20추가
    SET @po_intRetVal = -9
    RETURN
END

BEGIN TRAN
    -------------------------------------------------------
    --정산 데이터 삭제
    ------------------------------------------------------- 
    DELETE FROM TSettleMst
    WHERE  ProcYMD   = @pi_strYMD 
    AND    YMD      >= @v_strReqYMD
    AND    PGName IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)   
    AND    TxAmt   = 0  
    AND    ExtraSettleFlag = 1
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END

    -------------------------------------------------------
    --정산 데이터 등록 : 거래일자가 @pi_strYMD인 모든 승인내역(취소상태도 포함)
    -------------------------------------------------------    
    INSERT INTO TSettleMst (YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID
                           ,PLTID, TID, CID, PAYERID, PAYERNAME
                           ,SERVICENAME, X.PRODUCTNAME
                           ,PGVTTYPE      
                           ,CLVTTYPE
                           ,ABROADCHK
                           ,CompanySalesType   
                           ,USESTATE   
                           ,NonSettleAmt                                            
                           ,ExtraTxAmt    
                           ,TXAMT
                           ,CLCOMMTYPE 
                           ,PGCOMMTYPE                                     
                           ,CLETC
                           ,PGETC
                           ,CLTOTAL
                           ,PGTOTAL
                           ,POQINCOME
                           ,CLINTCOMM
                           ,PGINTEXPCOMM
                           ,PGINTREALCOMM
                           ,CLCOMM
                           ,CLVT
                           ,PGCOMM
                           ,PGVT                          
                           ,INSTATE
                           ,INYMD
                           ,ProcYMD
                           ,ProcState
                           ,ExtraSettleFlag )
                           
                    SELECT  X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID
                           ,X.PLTID, X.TID, X.CID, X.PAYERID, X.PAYERNAME
                           ,X.SERVICENAME, X.PRODUCTNAME
                           ,X.PGINCVTAX      
                           ,X.CLINCVTAX 
                           ,X.ABROADCHK                 --해외카드구분(1:해외카드 0:그외카드) : 영중소 우대수수료는 국내카드만 대상  
                           ,X.CompanySalesType   
                           ,X.USESTATE                  --거래상태(0:정상,1:취소, 2:부분취소)
                           ,X.NonSettleAmt                                             
                           ,X.ExtraTxAmt    
                           ,X.TXAMT
                           ,0 AS CLCOMMTYPE 
                           ,0 AS PGCOMMTYPE                                     
                           ,0 AS CLETC
                           ,0 AS PGETC
                           ,0 AS CLTOTAL
                           ,0 AS PGTOTAL
                           ,0 AS POQINCOME
                           ,0 AS CLINTCOMM
                           ,0 AS PGINTEXPCOMM
                           ,0 AS PGINTREALCOMM
                           
                           --[판가수수료] 
                           ,CASE WHEN X.AYMD < '20190501' THEN 0 ELSE CAST(ISNULL(X.CLCOMM,0) AS INT) END AS CLCOMM
                           ,CASE WHEN X.AYMD < '20190501' THEN 0 ELSE dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM,0) * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)) END AS CLVT 

                           --[원가수수료] 
                           ,CASE WHEN Y.CommMethod = 0 THEN                                  --수수료계산방식(0:공급가액, 1:수수료합계)
                                      CAST(ROUND(X.PGCOMM,       0, Y.CommRoundFlag) AS INT) --0:반올림, 0<>절사
                                 ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT)
                            END AS PGCOMM

                           ,CASE WHEN Y.CommMethod = 0 THEN
                                      CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGIncVTax), 0, Y.VatRoundFlag) AS INT)
                                 ELSE CAST(ROUND( ROUND( ROUND(X.PGCOMM,0,Y.CommSumRoundFlag), 0, Y.CommRoundFlag) 
                                                -(ROUND( ROUND(X.PGCOMM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)), 0, Y.VatRoundFlag) AS INT)
                            END AS PGVT
                           ,X.INSTATE
                           ,X.INYMD
                           ,X.ProcYMD 
                           ,CASE WHEN (X.CLCOMM IS NULL  OR  X.PGCOMM IS NULL) 
                                 THEN 1
                                 ELSE NULL 
                            END AS ProcState            --NULL이 아니면 확인 필요  
                           ,1   AS ExtraSettleFlag      --차액정산구분(1:차액정산, 2:환급정산)                        
                    FROM  (
                            SELECT A.ReqYMD             AS YMD 
                                  ,IIF(A.UseState=0, NULL, A.YMD)  AS CYMD
                                  ,A.OrgYMD             AS AYMD
                                  ,A.ResYMD             AS ProcYMD
                                  ,A.CLIENTID
                                  ,A.PGNAME
                                  ,A.MID                AS MALLID
                                  ,A.PLTID
                                  ,A.TID
                                  ,A.SeqNo              AS CID
                                  ,''                   AS PAYERID
                                  ,''                   AS PAYERNAME
                                  ,''                   AS SERVICENAME
                                  ,'영중소차액정산'        AS PRODUCTNAME
                                  ,ISNULL(B.incVTax,0)  AS PGINCVTAX      
                                  ,ISNULL(C.incVTax,0)  AS CLINCVTAX
                                  ,0                    AS ABROADCHK                 --해외카드구분(1:해외카드 0:그외카드) : 영중소 우대수수료는 국내카드만 대상  
                                  ,A.CompanySalesType     
                                  ,A.USESTATE                                       --거래상태(0:정상,1:취소, 2:부분취소)                     
                                  ,0                    AS NonSettleAmt   
                                  ,0                    AS TXAMT                           
                                  ,A.TxAmt              AS ExtraTxAmt  
                                  ,ISNULL(CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate0) / 100.0 ) AS INT)
                                               WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate1) / 100.0 ) AS INT)
                                               WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate2) / 100.0 ) AS INT)
                                               WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (C.CommissionRate - C.CommRate3) / 100.0 ) AS INT)
                                               ELSE 0  
                                          END,0) * IIF(C.ExtraSettleFlag='Y',1,0) AS CLComm     --영중소우대수수료 지급여부(Y:지급,N:미지급): 미지급 0원처리                                          
                                          --END,0) * IIF(C.ExtraCommFlag='Y' AND C.ExtraCommTarget IN (1,3),1,0) AS CLComm    --영중소우대수수료 지급여부(Y:지급,N:미지급): 미지급 0원처리
              
                                  ,ISNULL(CASE WHEN A.PGName='dacomcard' OR A.PGName='tosscard' THEN
                                                    CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT)
                                                                                                - CAST((A.TxAmt * ((B.CommRate0      * 1.1) / 100.0) ) AS INT)
                                                         WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT)
                                                                                                - CAST((A.TxAmt * ((B.CommRate1      * 1.1) / 100.0) ) AS INT)
                                                         WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT)
                                                                                                - CAST((A.TxAmt * ((B.CommRate2      * 1.1) / 100.0) ) AS INT) 
                                                         WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0) ) AS INT)
                                                                                                - CAST((A.TxAmt * ((B.CommRate3      * 1.1) / 100.0) ) AS INT) 
                                                         ELSE 0  
                                                    END 
                                               ELSE
                                                    CASE WHEN ISNULL(A.CompanySalesType,4)=0 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate0) / 100.0 ) AS INT)
                                                         WHEN ISNULL(A.CompanySalesType,4)=1 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate1) / 100.0 ) AS INT)
                                                         WHEN ISNULL(A.CompanySalesType,4)=2 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate2) / 100.0 ) AS INT)
                                                         WHEN ISNULL(A.CompanySalesType,4)=3 THEN CAST((A.TxAmt * (B.CommissionRate - B.CommRate3) / 100.0 ) AS INT)
                                                         ELSE 0  
                                                    END
                                          END,0)  AS PGComm --dacomcard의 경우 부가세 포함된 수수료로 계산 후 처리
                                   
                                  --[회수일]  
                                  ,1                AS INSTATE
                                  ,A.ExtraSettleYMD AS INYMD
                            FROM PaymentDB.dbo.TExtraSettleIn       A WITH(NOLOCK)  --2021-07-21, 수수료정책변경(요청일->원거래일)
                            LEFT OUTER JOIN TPGSettleRate4Extra     B WITH(NOLOCK) ON A.OrgYMD = B.YMD AND A.PGNAME = B.PGNAME AND A.MID = B.MALLID
                            LEFT OUTER JOIN TClientSettleRate4Extra C WITH(NOLOCK) ON A.OrgYMD = C.YMD AND A.PGNAME = C.PGNAME AND A.MID = C.MALLID AND A.CLIENTID = C.CLIENTID
                            LEFT OUTER JOIN TClientContract         E WITH(NOLOCK) ON A.CLIENTID = E.CLIENTID
                            WHERE  A.ResYMD            = @pi_strYMD
                            AND    A.PGNAME           IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')    --재판매 신용카드
                            AND    A.ResultCode        = '00'                                       --차액정산결과(00:성공) => TExtraSettleIn테이블에 영중소가 아닌 데이터도 존해하나, 영중소가 아니면 ResultCode <> 00
                            AND    A.CompanySalesType IN (0,1,2,3)                                  --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) => PG사 결과 데이터로 진행(PG사:POQ 불일치 가능성),20220708
                            AND    A.RefundTxType      = 0                                          --환급거래구분 (0:비대상,1:대상)
                            --AND    C.ExtraSettleFlag  = 'Y'                                       --영중소 우대수수료 지급여부(Y:지급, N:미지급): 지급여부와 상관없이 진행, 정산을 0원처리
                     
                    ) X 
                    LEFT OUTER JOIN TPGProperty Y WITH(NOLOCK) ON X.PGName = Y.PGName
                    
       
    --------------------------------------------------
    --정산일 적용
    -------------------------------------------------- 
    UPDATE TSettleMst          
    SET    OutState    = 2
          ,OutYMD      = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))
    FROM   TSettleMst    A WITH(NOLOCK)
    JOIN   TClientCMRate C WITH(NOLOCK) 
      ON   A.PGNAME    = C.PGNAME 
     AND   A.MALLID    = C.MALLID 
     AND   A.CLIENTID  = C.CLIENTID
    WHERE  A.ProcYMD   = @pi_strYMD
    AND    A.YMD      >= @v_strReqYMD     
    AND    A.PGNAME   IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')    --재판매 신용카드
    AND    ISNULL(A.CompanySalesType,4) IN (0,1,2,3)                            --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
    AND    A.TxAmt     = 0  
    AND    A.ExtraSettleFlag = 1
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END
    
    --정산일이 당일 이전인 경우 (재판매_차액정산의 경우 YMD 기준 몇일(2~5일 정도) 후 데이터 응답 및 정산 데이터 생성됨.)
    UPDATE TSettleMst          
    SET    OutYMD      = dbo.UF_Get_WorkDay2(@v_strCurrYMD,2)
    FROM   TSettleMst    A WITH(NOLOCK)
    WHERE  A.ProcYMD   = @pi_strYMD
    AND    A.YMD      >= @v_strReqYMD     
    AND    A.PGNAME   IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')    --재판매 신용카드
    AND    ISNULL(A.CompanySalesType,4) IN (0,1,2,3)                            --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
    AND    A.TxAmt     = 0  
    AND    A.ExtraSettleFlag = 1
    AND    A.OutState  = 2
    AND    A.OutYMD   <= @v_strCurrYMD
    
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -3
        RETURN
    END
                     
    --------------------------------------------------
    --영중소차액정산 수수료적용: 일반 거래건과 반대개념으로 적용(거래금액/회수금액/정산금액은 유지)
    --------------------------------------------------    
    UPDATE TSettleMst  
    SET    CLCOMM           = CLCOMM        * (-1)
          ,CLINTCOMM        = CLINTCOMM     * (-1)
          ,CLVT             = CLVT          * (-1)
          ,CLETC            = CLETC         * (-1)
          ,PGCOMM           = PGCOMM        * (-1)
          ,PGINTEXPCOMM     = PGINTEXPCOMM  * (-1)
          ,PGINTREALCOMM    = PGINTREALCOMM * (-1)
          ,PGVT             = PGVT          * (-1)
          ,PGETC            = PGETC         * (-1)
    FROM   TSettleMst with(nolock)
    WHERE  ProcYMD           = @pi_strYMD
    AND    YMD              >= @v_strReqYMD
    AND    PGNAME           IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')  --재판매 신용카드
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)                      --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반)   
    AND    USESTATE         IN (0)                                      --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)    
    AND    TxAmt             = 0  
    AND    ExtraSettleFlag = 1
        
    --------------------------------------------------
    --TOTAL 수수료 적용
    -------------------------------------------------- 
    UPDATE TSettleMst          
    SET    CLTOTAL   = (CLCOMM + CLVT + CLETC + CLINTCOMM)          
          ,PGTOTAL   = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))          
          ,POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM) - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)) 
    WHERE  ProcYMD   = @pi_strYMD
    AND    YMD      >= @v_strReqYMD     
    AND    PGNAME   IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard') --재판매 신용카드
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)             --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
    AND    TxAmt     = 0  
    AND    ExtraSettleFlag = 1
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -4
        RETURN
    END
    
    -----------------------------------------------
    --부가세포함 계산방식 추가 : 2021.02.19부터 적용
    -----------------------------------------------    
    --수수료합계 : 거래금액 * 수수료율(원단위 이하 절사)    
    --공급가액  : 수수료합계 / 1.1 (원단위 이하 절사)
    --부가세   : 수수료합계 - 공급가액    
    DECLARE @v_valIncVat   DECIMAL(2,1) = 1.1

    UPDATE TSettleMst
    SET    CLTotal   = (CLComm+CLEtc+CLIntComm)
          ,CLComm    =                            (CAST(CLComm/@v_valIncVat AS INT)+CAST(CLEtc/@v_valIncVat AS INT)+CAST(CLIntComm/@v_valIncVat AS INT))
          ,CLVT      = (CLComm+CLEtc+CLIntComm) - (CAST(CLComm/@v_valIncVat AS INT)+CAST(CLEtc/@v_valIncVat AS INT)+CAST(CLIntComm/@v_valIncVat AS INT))
          ,POQIncome = (CLComm+CLEtc+CLIntComm) - PGTotal        
    WHERE  ProcYMD   = @pi_strYMD
    AND    YMD      >= @v_strReqYMD     
    AND    PGNAME   IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')                      --재판매 신용카드
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)                                  --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
    AND    TxAmt     = 0  
    AND    CLVTType  = 1                                                            --부가세 포함여부(0:별도, 1:포함, 2:없음)
    AND    ExtraSettleFlag = 1
    --AND    ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET())    --예외처리 제거(2021.11.29)
    IF @@ERROR <> 0 BEGIN          
        ROLLBACK TRAN          
        SET @po_intRetVal = -21         
        RETURN          
    END  


COMMIT TRAN

RETURN

