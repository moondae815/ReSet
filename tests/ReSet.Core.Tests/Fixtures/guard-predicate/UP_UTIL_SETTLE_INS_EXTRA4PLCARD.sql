
-----------------------------------------------------------------
-- ProcedureName   : UP_Util_Settle_Ins_Extra4PLCard
-- Description     : 원천카드 영중소 차액정산 데이터 등록
-- Copyright ⓒ 2001 by PayLetter Inc. All rights reserved.
-- Author          : sailorjk, 2021-02-04
-----------------------------------------------------------------
CREATE                           PROCEDURE [dbo].[UP_UTIL_SETTLE_INS_EXTRA4PLCARD]
@pi_strYMD                      CHAR(8),    --입금일
@po_intRetVal                   INT OUT
AS
SET NOCOUNT ON

DECLARE @v_strReqYMD            VARCHAR(8) = ''

-------------------------------------------------------
--정산 데이터 확인
-------------------------------------------------------      
IF EXISTS(SELECT PLTID
		  FROM   TSettleMst A WITH (NOLOCK)
          INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName AND PG.ExtraType IN (2,3)
		  WHERE  ProcYMD   = @pi_strYMD
          AND    YMD       = @pi_strYMD
		  AND    OutState IN (1,5)
		  AND    OutYMD   IS NOT NULL
          AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)       --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
          AND    ExtraSettleFlag = 1 ) BEGIN
	SET @po_intRetVal = -9
    RETURN
END

BEGIN TRAN
    -------------------------------------------------------
    --정산 데이터 삭제
    ------------------------------------------------------- 
    DELETE A FROM TSettleMst A
    INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName AND PG.ExtraType IN (2,3)
    WHERE  ProcYMD   = @pi_strYMD 
    AND    YMD       = @pi_strYMD
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)   
    AND    TxAmt     = 0  
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
                           ,OUTSTATE
                           ,INYMD
                           ,OUTYMD
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
                           ,X.USESTATE  
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
                           ,CAST(ISNULL(X.CLCOMM,0) AS INT) AS CLCOMM
                           ,dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM,0) * dbo.UF_GET_INCVTAXRATE(X.CLIncVTax)) AS CLVT 

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
                           ,IIF(ISNULL(X.CompanySalesType,4)=4,9,X.OUTSTATE) AS OUTSTATE
                           ,X.INYMD
                           ,X.OUTYMD   
                           ,X.ProcYMD 
                           ,CASE WHEN (X.CLCOMM IS NULL  OR  X.PGCOMM IS NULL) 
                                 THEN 1
                                 ELSE NULL 
                            END AS ProcState  --NULL이 아니면 확인 필요  
                           ,1   AS ExtraSettleFlag      --차액정산구분(1:차액정산, 2:환급정산)                                     
                    FROM  ( 
                            SELECT A.REQYMD             AS YMD 
                                  ,IIF(A.UseState=0, NULL, A.YMD)  AS CYMD
                                  ,A.AYMD               AS AYMD
                                  ,A.REQYMD             AS ProcYMD
                                  ,A.CLIENTID
                                  ,A.PGNAME
                                  ,C.MallID             AS MALLID
                                  ,A.PLTID
                                  ,A.TID                AS TID 
                                  ,A.CID                AS CID 
                                  ,''                   AS PAYERID
                                  ,''                   AS PAYERNAME
                                  ,''                   AS SERVICENAME
                                  ,'영중소차액정산'       AS PRODUCTNAME
                                  ,1                    AS PGINCVTAX                 --PGVT 없음
                                  ,0                    AS CLINCVTAX                 
                                  ,0                    AS ABROADCHK                 --해외카드구분(1:해외카드 0:그외카드) : 영중소 우대수수료는 국내카드만 대상  
                                  ,A.CompanySalesType     
                                  ,A.USESTATE                      
                                  ,0                    AS NonSettleAmt   
                                  ,0                    AS TXAMT                           
                                  ,A.TxAmt              AS ExtraTxAmt  
                                  ,SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT(A.AYMD, A.PGName, A.ClientID, A.CompanySalesType, B.CardCode, B.CardCPID, A.TxAmt) AS CLComm     
                                  ,SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt(A.AYMD, B.CardCode, B.CardCPID, A.CompanySalesType, A.TxAmt, B.CheckCardFlag) AS PGComm 
                                   
                                  --[회수 및 정산일]  
                                  ,0                    AS INSTATE
                                  ,2                    AS OUTSTATE
                                  ,NULL                 AS INYMD
                                  ,(SELECT OutYMD FROM dbo.UIF_SettleYMD(A.ReqYMD, C.SettlePeriodID)) AS OUTYMD
                                  
                            FROM  SETTLE_CARD_DB.dbo.TExtraTxMst   A WITH(NOLOCK)
                            JOIN        PLCardDB.dbo.TPLCardTxMst  B WITH(NOLOCK) ON A.PLTID  = B.PLTID
                            JOIN   SETTLE_POQ_DB.dbo.TClientCMRate C WITH(NOLOCK) ON A.PGNAME = C.PGNAME AND A.MALLID = C.MALLID AND A.CLIENTID = C.CLIENTID
                            JOIN   SETTLE_POQ_DB.dbo.TPGProperty   P WITH(NOLOCK) ON A.PGName = P.PGName AND P.ExtraType IN (2,3)
                            WHERE A.REQYMD       = @pi_strYMD
                            AND   A.EDICheckFlag = 'Y'
                            AND   A.RecordGB    <> 'DX'                          --KSNet에서 환급매입요청-응답 후 (반기)응답
                            AND   A.CompanySalesType IN (0,1,2,3)                --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) => PG사 결과 데이터로 진행(PG사:POQ 불일치 가능성)
                    ) X 
                    LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y WITH(NOLOCK) ON X.PGName = Y.PGName
                    
                    
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
    FROM   TSettleMst A WITH (NOLOCK)
    INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName AND PG.ExtraType IN (2,3)
    WHERE  ProcYMD           = @pi_strYMD
    AND    YMD               = @pi_strYMD
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)              --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반)   
    AND    A.USESTATE        IN (0)                              --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)    
    AND    TxAmt             = 0  
    AND    A.ExtraSettleFlag = 1
        
    --------------------------------------------------
    --TOTAL 수수료 적용
    -------------------------------------------------- 
    UPDATE TSettleMst          
    SET    CLTOTAL   = (CLCOMM + CLVT + CLETC + CLINTCOMM)          
          ,PGTOTAL   = (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END))          
          ,POQINCOME = (CLCOMM + CLVT + CLETC + CLINTCOMM) - (PGCOMM + PGVT + PGETC + (CASE PGINTREALCOMM WHEN 0 THEN PGINTEXPCOMM ELSE PGINTREALCOMM END)) 
    FROM   TSettleMst A WITH (NOLOCK)
    INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName AND PG.ExtraType IN (2,3)
    WHERE  ProcYMD           = @pi_strYMD
    AND    YMD               = @pi_strYMD       
    AND    ISNULL(CompanySalesType,4) IN (0,1,2,3)               --사업자 매출구분(0:영세, 1:중소1, 2:중소2, 3:중소3, 4:일반) 
    AND    TxAmt             = 0  
    AND    A.ExtraSettleFlag = 1

    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END

COMMIT TRAN

RETURN
