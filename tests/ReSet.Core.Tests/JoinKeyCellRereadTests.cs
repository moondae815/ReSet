using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// 조인 키 칸의 **되읽기**. <c>b739de01</c>(2026-09-08)이 칸 뒤에 <c>· 외부 조인 …</c> 꼬리를
/// 붙였고 렌더(AiService)와 L1 전사 대조는 <see cref="DmlScopeFact.JoinKeysCell"/>
/// 한 자리로 맞췄는데, 그 칸을 다시 컬럼 목록으로 읽는 셋째 소비자
/// (<see cref="SpecStatementFactsExtractor"/>)가 안 따라왔다. 마지막 키가 꼬리와 붙어
/// <c>PGName · 외부 조인 Y(LEFT OUTER)</c> 라는 컬럼 하나가 되고, 검사 B 가 그런 컬럼을 매 시도
/// 요구해 재시도 예산을 소진했다(코퍼스 14 좌표 중 12 가 이 오탐 —
/// <c>docs/audit-reports/2026-09-13-축B-재시도불수렴-판독.md</c>).
///
/// 픽스처는 전부 실물에서 오렸다: 명세서 행은 09-10 세대 <c>Spec.md</c> 의 해당 줄, 원본 SQL 은
/// <c>INS_EXTRA4PLCARD</c> DDL 52~173 행, 이행 SQL 은 <c>POQSettleBatch8/S10.md</c> 80~165 행.
/// </summary>
public sealed class JoinKeyCellRereadTests
{
    private const string Header = """
| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
""";

    private static SpecDmlRow ReadRow(string procedure, string row)
    {
        var spec = "### DML 범위 (기계 확정 — 수정 금지)\n\n" + Header + "\n" + row + "\n";
        return Assert.Single(SpecStatementFactsExtractor.Extract(new[] { (procedure, spec) })
            .Values.Single().DmlRows);
    }

    // ── 1. 실물 칸 셋 ─────────────────────────────────────────────────────

    [Fact]
    public void Reread_SettleInsCell_DropsTheOuterJoinTail()
    {
        var row = ReadRow("dbo.UP_UTIL_SETTLE_INS", """
| INSERT 1 | 55 | TSettleMst | (없음) | **아니오**(최상위 기준 · 하위 질의는 별도 확인) | PGName · 외부 조인 Y(LEFT OUTER) | (없음) | (없음) |
""");

        Assert.Equal(new[] { "PGName" }, row.JoinKeys);
    }

    // 꼬리 안에도 쉼표가 있다(`Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), …`). 쉼표로 먼저
    // 쪼개면 꼬리 조각이 컬럼 이름으로 남는다 - 꼬리를 먼저 떼야 하는 이유다.
    [Fact]
    public void Reread_InsExtraCell_DropsATailThatItselfContainsCommas()
    {
        var row = ReadRow("dbo.UP_UTIL_SETTLE_INS_EXTRA", """
| INSERT 1 | 63 | TSettleMst | (없음) | **아니오**(최상위 기준 · 하위 질의는 별도 확인) | PGName, CLIENTID, OrgYMD, YMD, MID, MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) | (없음) | (없음) |
""");

        Assert.Equal(new[] { "PGName", "CLIENTID", "OrgYMD", "YMD", "MID", "MALLID" }, row.JoinKeys);
    }

    [Fact]
    public void Reread_InsExtra4PlCardCell_DropsTheOuterJoinTail()
    {
        var row = ReadRow("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD", """
| INSERT 1 | 52 | TSettleMst | (없음) | **아니오**(최상위 기준 · 하위 질의는 별도 확인) | PGName, ExtraType, MALLID, CLIENTID, PLTID · 외부 조인 Y(LEFT OUTER) | (없음) | (없음) |
""");

        Assert.Equal(new[] { "PGName", "ExtraType", "MALLID", "CLIENTID", "PLTID" }, row.JoinKeys);
    }

    // ── 2. 왕복 — 렌더와 되읽기가 같은 형식을 쓴다 ───────────────────────────

    private const string InsExtra4PlCardInsert = """
CREATE PROCEDURE dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD @pi_strYMD VARCHAR(8) AS
BEGIN
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
END
""";

    private static string RenderRow(DmlScopeFact fact) =>
        $"| INSERT 1 | {fact.Line} | TSettleMst | (없음) | 예 | {fact.JoinKeysCell} | (없음) | (없음) |";

    // 원본 DDL → 추출기 → 칸 렌더 → 되읽기. 기대값이 모델 산출물이 아니라 추출기가 원본에서
    // 뽑은 JoinKeys 라 순환이 아니다. 전건(외부 조인이 실제로 잡혔다)을 먼저 단언한다 -
    // 꼬리가 없는 사실로 왕복하면 이 시험은 아무것도 안 잰다.
    [Fact]
    public void RoundTrip_RealDdlWithTopLevelOuterJoin_RereadsExactlyTheExtractedKeys()
    {
        var fact = Assert.Single(
            DmlScopeExtractor.Extract(InsExtra4PlCardInsert, "@pi_strYMD"), f => f.Operation == "INSERT");
        Assert.NotEmpty(fact.OuterJoins);
        Assert.NotEmpty(fact.JoinKeys);

        var row = ReadRow("dbo.P", RenderRow(fact));

        Assert.Equal(fact.JoinKeys, row.JoinKeys);
    }

    // 키 없이 외부 조인만 있으면 칸은 `(없음) · 외부 조인 …` 이다(JoinKeysCell 의 한 갈래,
    // L1 전사 대조 MechanicalValidator 가 이 경우를 따로 다룬다). 꼬리를 떼면 `(없음)` 이 남아
    // 빈 목록이 되어야 한다.
    [Fact]
    public void RoundTrip_OuterJoinWithoutKeys_RereadsAsEmpty()
    {
        var fact = new DmlScopeFact(
            "INSERT", 10, "TSettleMst", Array.Empty<string>(), false,
            Array.Empty<string>(), Array.Empty<string>())
        { OuterJoins = new[] { "Y(LEFT OUTER)" } };
        Assert.StartsWith("(없음)", fact.JoinKeysCell);

        var row = ReadRow("dbo.P", RenderRow(fact));

        Assert.Empty(row.JoinKeys);
    }

    // ── 3. 검사 B — 실물 이행 SQL ──────────────────────────────────────────

    private const string Batch8S10Insert1 = """
-- SQL_INSERT_1
DECLARE @v_strReqYMD VARCHAR(8) = ''; -- (rule 5-1) 원본 미사용 지역 변수, 재대입/참조 없음

/* INSERT 1: 신규 영중소 차등정산 데이터 등록 - 조인 키 PGName, ExtraType, MALLID, CLIENTID, PLTID · 외부 조인 Y(LEFT OUTER) */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst
    (YMD, CYMD, AYMD, CLIENTID, PGNAME, MALLID, PLTID, TID, CID,
     PAYERID, PAYERNAME, SERVICENAME, PRODUCTNAME, PGVTTYPE, CLVTTYPE, ABROADCHK,
     CompanySalesType, USESTATE, NonSettleAmt, ExtraTxAmt, TXAMT,
     CLCOMMTYPE, PGCOMMTYPE, CLETC, PGETC, CLTOTAL, PGTOTAL, POQINCOME,
     CLINTCOMM, PGINTEXPCOMM, PGINTREALCOMM, CLCOMM, CLVT, PGCOMM, PGVT,
     INSTATE, OUTSTATE, INYMD, OUTYMD, ProcYMD, ProcState, ExtraSettleFlag)
SELECT
    X.YMD, X.CYMD, X.AYMD, X.CLIENTID, X.PGNAME, X.MALLID, X.PLTID, X.TID, X.CID,
    X.PAYERID, X.PAYERNAME, X.SERVICENAME, X.PRODUCTNAME,
    X.PGINCVTAX AS PGVTTYPE, X.CLINCVTAX AS CLVTTYPE, X.ABROADCHK,
    X.CompanySalesType, X.USESTATE, X.NonSettleAmt, X.ExtraTxAmt, X.TXAMT,
    0 AS CLCOMMTYPE, 0 AS PGCOMMTYPE, 0 AS CLETC, 0 AS PGETC,
    0 AS CLTOTAL, 0 AS PGTOTAL, 0 AS POQINCOME,
    0 AS CLINTCOMM, 0 AS PGINTEXPCOMM, 0 AS PGINTREALCOMM,
    CAST(ISNULL(X.CLCOMM,0) AS INT) AS CLCOMM,
    dbo.UF_GET_ROUND4VAT(ISNULL(X.CLCOMM,0) * dbo.UF_GET_INCVTAXRATE(X.CLINCVTAX)) AS CLVT,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
         ELSE CAST(ROUND(ROUND(X.PGCOMM, 0, Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag) AS INT)
    END AS PGCOMM,
    CASE WHEN Y.CommMethod = 0
         THEN CAST(ROUND((X.PGCOMM) * dbo.UF_GET_INCVTAXRATE(X.PGINCVTAX), 0, Y.VatRoundFlag) AS INT)
         ELSE CAST(ROUND(
                ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag), 0, Y.CommRoundFlag)
                - (ROUND(ROUND(X.PGCOMM,0,Y.CommSumRoundFlag)/1.1, 0, Y.CommRoundFlag)),
                0, Y.VatRoundFlag) AS INT)
    END AS PGVT,
    X.INSTATE,
    IIF(ISNULL(X.CompanySalesType,4) = 4, 9, X.OUTSTATE) AS OUTSTATE,
    X.INYMD,
    X.OUTYMD,
    X.ProcYMD,
    CASE WHEN (X.CLCOMM IS NULL OR X.PGCOMM IS NULL) THEN 1 ELSE NULL END AS ProcState,
    1 AS ExtraSettleFlag
FROM (
    SELECT
        A.REQYMD AS YMD,
        IIF(A.UseState = 0, NULL, A.YMD) AS CYMD,
        A.AYMD AS AYMD,
        A.REQYMD AS ProcYMD,
        A.CLIENTID AS CLIENTID,
        A.PGNAME AS PGNAME,
        C.MallID AS MALLID,
        A.PLTID AS PLTID,
        A.TID AS TID,
        A.CID AS CID,
        '' AS PAYERID,
        '' AS PAYERNAME,
        '' AS SERVICENAME,
        N'영중소차액정산' AS PRODUCTNAME,
        1 AS PGINCVTAX,
        0 AS CLINCVTAX,
        0 AS ABROADCHK,
        A.CompanySalesType AS CompanySalesType,
        A.USESTATE AS USESTATE,
        0 AS NonSettleAmt,
        0 AS TXAMT,
        A.TxAmt AS ExtraTxAmt,
        SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT(
            A.AYMD, A.PGName, A.ClientID, A.CompanySalesType, B.CardCode, B.CardCPID, A.TxAmt) AS CLComm,
        SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt(
            A.AYMD, B.CardCode, B.CardCPID, A.CompanySalesType, A.TxAmt, B.CheckCardFlag) AS PGComm,
        0 AS INSTATE,
        2 AS OUTSTATE,
        NULL AS INYMD,
        (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.ReqYMD, C.SettlePeriodID)) AS OUTYMD
    FROM SETTLE_CARD_DB.dbo.TExtraTxMst A
    INNER JOIN PLCardDB.dbo.TPLCardTxMst B
            ON B.PLTID = A.PLTID                                            -- 조인 키: PLTID
    INNER JOIN SETTLE_POQ_DB.dbo.TClientCMRate C
            ON C.PGNAME = A.PGNAME AND C.CLIENTID = A.CLIENTID AND C.MallID = A.MALLID
                                                                              -- 조인 키: PGName, CLIENTID, MALLID
    INNER JOIN SETTLE_POQ_DB.dbo.TPGProperty P
            ON P.PGName = A.PGName AND P.ExtraType IN (2,3)                 -- 조인 키: PGName, ExtraType(파생 X 내부 필터)
    WHERE A.REQYMD = @p_ymd
      AND A.EDICheckFlag = 'Y'
      AND A.RecordGB <> 'DX'
      AND A.CompanySalesType IN (0,1,2,3)
) AS X
LEFT OUTER JOIN SETTLE_POQ_DB.dbo.TPGProperty Y
        ON Y.PGName = X.PGNAME AND Y.ExtraType IN (2,3);                    -- 외부 조인 Y(LEFT OUTER), 조인 키: PGName, ExtraType
""";

    private static StepValidationResult ValidateInsert1(string sql)
    {
        var facts = SpecStatementFactsExtractor.Extract(new[]
        {
            ("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD",
             "### DML 범위 (기계 확정 — 수정 금지)\n\n" + Header + "\n" + """
| INSERT 1 | 52 | TSettleMst | (없음) | **아니오**(최상위 기준 · 하위 질의는 별도 확인) | PGName, ExtraType, MALLID, CLIENTID, PLTID · 외부 조인 Y(LEFT OUTER) | (없음) | (없음) |
""" + "\n")
        });
        var step = new BatchStepPlan(
            Code: "S10", Name: "S10 단계",
            LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD" },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
            ErrorCodes: new[] { "-1", "-2" }, Chunkable: false, SchemaTables: Array.Empty<string>());
        var markdown = "### S10 단계\n\n```sql\n" + sql + "\n```\n";

        return new MechanicalValidator().ValidateBatchStep(
            markdown, step, new[] { "dbo.TSettleMst" },
            new Dictionary<string, SpecConditions>(), null, null, facts);
    }

    // 이행은 원본의 조인 키를 전부 가진다(파생 X 안 `B.PLTID = A.PLTID` · `C.MallID = A.MALLID` ·
    // 최상위 `Y.PGName = X.PGNAME`). 이 SQL 에 검사 B 가 조인 키를 요구하면 오탐이다.
    [Fact]
    public void CheckB_RealStepThatKeepsEveryJoinKey_IsSilent()
    {
        var result = ValidateInsert1(Batch8S10Insert1);

        Assert.DoesNotContain(result.Errors, e => e.Contains("조인 키"));
    }

    // 양성 대조군. 위 시험이 조용해진 것이 「검사가 이 문장을 못 읽는다」가 아니라 「키가 다
    // 있다」 때문임을 보인다 - 같은 본문에서 칸의 **마지막** 키(꼬리가 붙어 있던 자리)만
    // 지우면 그 컬럼 이름 그대로 발화해야 한다.
    [Fact]
    public void CheckB_SameStepWithTheTailPositionKeyRemoved_ReportsThatBareColumn()
    {
        var mutated = Batch8S10Insert1.Replace("ON B.PLTID = A.PLTID", "ON B.TID = A.TID");
        Assert.NotEqual(Batch8S10Insert1, mutated);

        var result = ValidateInsert1(mutated);

        Assert.Contains(result.Errors, e => e.Contains("조인 키 PLTID이(가) 없습니다"));
    }
}
