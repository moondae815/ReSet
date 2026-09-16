-- SQL_UPDATE_03
/* UPDATE 3: KFTC 고객사 구간수수료 반영 */
UPDATE A
   SET CLCOMM =
           dbo.UF_GET_CLIENTSECTIONRATE(
               A.CLIENTID,
               A.PGNAME,
               A.MALLID,
               A.TXAMT - ISNULL(A.NonSettleAmt, 0)
           ),
       CLVT =
           dbo.UF_GET_ROUND4VAT(
               dbo.UF_GET_CLIENTSECTIONRATE(
                   A.CLIENTID,
                   A.PGNAME,
                   A.MALLID,
                   A.TXAMT - ISNULL(A.NonSettleAmt, 0)
               )
               * dbo.UF_GET_INCVTAXRATE(A.CLVTType)
           )
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
  INNER JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B
    ON A.YMD = B.YMD
   AND A.CLIENTID = B.CLIENTID
   AND A.PGNAME = B.PGNAME
   AND A.MALLID = B.MALLID
 WHERE A.YMD = @p_ymd
   AND A.PGNAME = 'KFTC'
   AND dbo.UF_GET_CLIENTSECTIONRATE(
           A.CLIENTID,
           A.PGNAME,
           A.MALLID,
           A.TXAMT - ISNULL(A.NonSettleAmt, 0)
       ) <> 0;
