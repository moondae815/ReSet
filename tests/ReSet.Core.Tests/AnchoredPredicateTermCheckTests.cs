using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 앵커 DML 의 「원본에 없는 최상위 술어」 (설계:
    /// docs/superpowers/specs/2026-09-11-앵커-DML-최상위-술어-대조-design.md).
    ///
    /// [픽스처] 원본은 output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql:193-226,
    /// 이행은 output/Jobs/POQSettleBatch6/agent/steps/S12.md 의 INSERT 4. 서수를 1 로 줄였고 SELECT·INSERT
    /// 목록을 줄였다 - WHERE 는 축자다. 진짜 증거는 코퍼스 스윕(Task 7)이지 이 픽스처가 아니다.
    /// </summary>
    public class AnchoredPredicateTermCheckTests
    {
        private const string Marker = "원본에 없는 최상위 술어";
        private const string Procedure = "UP_UTIL_SETTLE_SUMMARY_EXTRA";

        private const string OriginalDdl = @"
CREATE PROCEDURE dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA
    @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_strReqYMD CHAR(8)

    DELETE TSettleByOUT
    WHERE  ProcYMD            = @pi_strYMD
    AND    YMD               >= @v_strReqYMD
    AND    OUTYMD            >= @v_strReqYMD
    AND    ISNULL(OUTYMD,'') <> ''
    AND    PGNAME            IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard') --재판매 신용카드
    AND    CompanySalesType  IN (0,1,2,3)
    AND    ExtraSettleFlag   = 1

    INSERT INTO TSettleByOUT (YMD, OUTYMD)
                       SELECT YMD, OUTYMD
                       FROM   TSettleMst WITH(NOLOCK)
                       WHERE  ProcYMD            = @pi_strYMD
                       AND    YMD               >= @v_strReqYMD
                       AND    ISNULL(OUTYMD,'') <> ''
                       AND    PGNAME            IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard') --재판매 신용카드
                       AND    CompanySalesType  IN (0,1,2,3)
                       AND    ExtraSettleFlag   = 1
                       GROUP BY YMD, OUTYMD
END";

        /// <summary>Batch6/S12 INSERT 4 의 WHERE 그대로 - DELETE 4 의 OUTYMD 항이 붙어 있다.</summary>
        private const string CopiedWhere =
            " WHERE ProcYMD = @p_ymd\n" +
            "   AND YMD >= @v_strReqYMD\n" +
            "   AND OUTYMD >= @v_strReqYMD\n" +
            "   AND ISNULL(OUTYMD,'') <> ''\n" +
            "   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')\n" +
            "   AND CompanySalesType IN (0,1,2,3)\n" +
            "   AND ExtraSettleFlag = 1\n";

        private const string FaithfulWhere =
            " WHERE ProcYMD = @p_ymd\n" +
            "   AND YMD >= @v_strReqYMD\n" +
            "   AND ISNULL(OUTYMD,'') <> ''\n" +
            "   AND PGNAME IN ('allthegate', 'dacomcard', 'tosscard', 'nicecard')\n" +
            "   AND CompanySalesType IN (0,1,2,3)\n" +
            "   AND ExtraSettleFlag = 1\n";

        private static string StepWithInsert(string where, string extraLines = "") =>
            "### S12 단계\n\n```sql\n" +
            "/* INSERT 1: 차액정산 정산집계 데이터 등록 (TSettleByOUT) */\n" +
            "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByOUT (YMD, OUTYMD)\n" +
            "SELECT YMD, OUTYMD\n" +
            "  FROM SETTLE_POQ_DB.dbo.TSettleMst\n" +
            where + extraLines +
            " GROUP BY YMD, OUTYMD;\n" +
            "```\n";

        private static BatchStepPlan Step(params string[] procedures) => new(
            Code: "S12", Name: "S12 단계",
            LegacyProcedures: procedures.Length == 0 ? new[] { $"dbo.{Procedure}" } : procedures,
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
            ErrorCodes: new[] { "4008" }, Chunkable: false, SchemaTables: Array.Empty<string>());

        private static SpecStatementFacts Facts(bool bannered = false) =>
            new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("DELETE", 1, 8, "TSettleByOUT",
                        new[] { "ProcYMD", "YMD", "OUTYMD", "PGNAME", "CompanySalesType", "ExtraSettleFlag" },
                        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    new SpecDmlRow("INSERT", 1, 17, "TSettleByOUT",
                        new[] { "ProcYMD", "YMD", "OUTYMD", "PGNAME", "CompanySalesType", "ExtraSettleFlag" },
                        Array.Empty<string>(), new[] { "YMD", "OUTYMD" }, Array.Empty<string>()),
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            { IsL1Exhausted = bannered };

        private static StepValidationResult Validate(
            string markdown,
            IReadOnlyDictionary<string, SpecStatementFacts>? facts = null,
            IReadOnlyDictionary<string, string>? ddl = null,
            BatchStepPlan? step = null,
            bool withDdl = true) =>
            new MechanicalValidator().ValidateBatchStep(
                markdown, step ?? Step(), Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(), null, null,
                facts ?? new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase) { [Procedure] = Facts() },
                null, null, null,
                withDdl
                    ? ddl ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Procedure] = OriginalDdl }
                    : null);

        [Fact]
        public void Fires_WhenTheInsertCarriesTheDeletesExtraTerm()
        {
            var error = Assert.Single(Validate(StepWithInsert(CopiedWhere)).Errors, e => e.Contains(Marker));

            Assert.Contains("S12 섹션의 INSERT 1(TSettleByOUT) 문장이", error);
            Assert.Contains("`OUTYMD >= @v_strReqYMD`", error);
            // 원본 쪽은 DDL 원문을 싣는다(공백은 한 칸으로 접힌다) - 사람이 읽는 것은 원문이다.
            Assert.Contains("`ProcYMD = @pi_strYMD AND YMD >= @v_strReqYMD AND ISNULL(OUTYMD,'') <> ''", error);
        }

        [Fact]
        public void StaysSilent_WhenOnlyVariableNamesAndQualifiersDiffer()
        {
            Assert.DoesNotContain(Validate(StepWithInsert(FaithfulWhere)).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_OnOrchestrationTerms()
        {
            // E1 - 청크 범위와 실행 식별자는 원본이 가질 수 없는 항이다.
            var extra = "   AND PGNAME >= @p_from AND PGNAME <= @p_toExclusive\n" +
                        "   AND RunId = @p_runId AND StepCode = @p_stepCode\n";

            Assert.DoesNotContain(Validate(StepWithInsert(FaithfulWhere, extra)).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void Fires_WhenAnOrGroupMixesOrchestrationAndBusinessVariables()
        {
            // E1 은 OR 묶음 안의 변수가 **모두** 오케스트레이션일 때만 면제한다.
            var extra = "   AND (PGNAME >= @p_from OR USESTATE = @p_state)\n";

            Assert.Contains(Validate(StepWithInsert(FaithfulWhere, extra)).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void Fires_WhenAVariableIsOnlyPrefixedLikeAChunkBound()
        {
            // `@p_fromage` 는 청크 범위가 아니다 - 뒤에 대문자가 올 때만 면제한다.
            var extra = "   AND PGNAME >= @p_fromage\n";

            Assert.Contains(Validate(StepWithInsert(FaithfulWhere, extra)).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WithoutTheOriginalDdl()
        {
            // S1 - 재료가 없으면 대조가 성립하지 않는다.
            Assert.DoesNotContain(Validate(StepWithInsert(CopiedWhere), withDdl: false).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTwoLegacyProceduresClaimTheSameKey()
        {
            // S2 - 같은 (INSERT, 1, TSettleByOUT) 를 두 SP 가 내면 귀속할 수 없다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                [Procedure] = Facts(),
                ["UP_OTHER"] = Facts(),
            };
            var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Procedure] = OriginalDdl,
                ["UP_OTHER"] = OriginalDdl.Replace("UP_UTIL_SETTLE_SUMMARY_EXTRA", "UP_OTHER"),
            };

            var result = Validate(StepWithInsert(CopiedWhere), facts, ddl, Step($"dbo.{Procedure}", "dbo.UP_OTHER"));

            Assert.DoesNotContain(result.Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTheAnchoredTargetIsNotTheOriginalsTarget()
        {
            // S2 - Batch6 U-앵커 불일치의 모양. 앵커가 가리키는 서수의 원본 대상이 다르면 키가 없다.
            var markdown = StepWithInsert(CopiedWhere).Replace("dbo.TSettleByOUT (", "dbo.TSettleByOUT2 (");

            Assert.DoesNotContain(Validate(markdown).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTheOriginalStatementHasNoTopLevelTerm()
        {
            // S3 - 원본이 WHERE 없이 통째로 옮기면 「더했다」의 기준이 없다.
            var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Procedure] = "CREATE PROCEDURE dbo.P AS BEGIN INSERT INTO TSettleByOUT (YMD) SELECT YMD FROM TSettleMst END",
            };

            Assert.DoesNotContain(Validate(StepWithInsert(CopiedWhere), ddl: ddl).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTheSpecCarriesTheL1ExhaustedBanner()
        {
            // S5 - L1 을 통과하지 못한 명세서의 표(커서 면제·스테이징 대상)는 기준값이 아니다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                [Procedure] = Facts(bannered: true),
            };

            Assert.DoesNotContain(Validate(StepWithInsert(CopiedWhere), facts).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTheOnlyAddedTermIsAJoinEquality()
        {
            // 조인 등식은 N5 의 몫이다 - 둘이 같은 자리에서 두 번 발화하지 않는다.
            var markdown = "### S12 단계\n\n```sql\n" +
                "/* INSERT 1: 등록 */\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByOUT (YMD, OUTYMD)\n" +
                "SELECT A.YMD, A.OUTYMD\n" +
                "  FROM SETTLE_POQ_DB.dbo.TSettleMst A, SETTLE_POQ_DB.dbo.TSettleMst B\n" +
                FaithfulWhere.Replace("ProcYMD", "A.ProcYMD") +
                "   AND A.PLTID = B.PLTID\n" +
                " GROUP BY A.YMD, A.OUTYMD;\n" +
                "```\n";

            Assert.DoesNotContain(Validate(markdown).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void StaysSilent_WhenTheStatementReadsOnlyStaging()
        {
            // E3 - 게시문이 자기 실행이 적재한 스테이징만 되읽으려고 거는 술어는 원본 원천의 술어가 아니다.
            var markdown = "### S12 단계\n\n```sql\n" +
                "INSERT INTO batch.S12Stage (YMD, OUTYMD) SELECT YMD, OUTYMD FROM SETTLE_POQ_DB.dbo.TSettleMst\n" +
                FaithfulWhere + ";\n" +
                "/* INSERT 1: 게시 */\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByOUT (YMD, OUTYMD)\n" +
                "SELECT YMD, OUTYMD FROM batch.S12Stage WHERE ExecutionId = @p_exec;\n" +
                "```\n";

            Assert.DoesNotContain(Validate(markdown).Errors, e => e.Contains(Marker));
        }

        // ── R7 (설계 §8-1) ──────────────────────────────────────────
        // 원본: output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql:89-99 (UPDATE 1)
        // 이행: output/Jobs/POQSettleBatch4/agent/steps/S11.md 의 SQL_UPDATE_MISS — 코퍼스 오탐 실물(설계 §7-3 #5).
        // 실행 자리(S11.md:71-76)는 하나이고 p_intOutState: 2 를 바인딩한다.
        private const string ProcEtcProcedure = "UP_UTIL_SETTLE_PROC_ETC";

        private const string ProcEtcDdl = @"
CREATE PROCEDURE dbo.UP_UTIL_SETTLE_PROC_ETC
    @pi_strYMD CHAR(8)
AS
BEGIN
        IF @v_intID > 0 BEGIN
            --TSettleMiss 수수료 업데이트
            UPDATE TSettleMiss
            SET    YMD          = @v_strYMD
                  ,CLSettleAmt += @v_intCLTotal
                  ,CLComm      += @v_intCLComm
                  ,CLVT        += @v_intCLVT
            WHERE  ID           = @v_intID
            AND    ClientID     = @v_strClientID
            AND    OutYMD       = @v_strOutYMD
            AND    OutState     = 2
            AND    IssueType    = @v_intIssueType
        END
END";

        private static string ProcEtcStep(string outStateTerm, string issueTypeTerm = "IssueType = @p_intIssueType") =>
            "### S11 단계\n\n```sql\n" +
            "-- SQL_UPDATE_MISS (갱신 1 · 라인 89 원문)\n" +
            "UPDATE SETTLE_POQ_DB.dbo.TSettleMiss\n" +
            "   SET YMD         = @p_strYMD,\n" +
            "       CLSettleAmt = CLSettleAmt + @p_intCLTotal,\n" +
            "       CLComm      = CLComm      + @p_intCLComm,\n" +
            "       CLVT        = CLVT        + @p_intCLVT\n" +
            " WHERE ID        = @p_intID\n" +
            "   AND ClientID  = @p_strClientID\n" +
            "   AND OutYMD    = @p_strOutYMD\n" +
            $"   AND {outStateTerm}\n" +
            $"   AND {issueTypeTerm};\n" +
            "```\n";

        private static StepValidationResult ValidateProcEtc(string markdown) =>
            new MechanicalValidator().ValidateBatchStep(
                markdown,
                new BatchStepPlan(
                    Code: "S11", Name: "S11 단계",
                    LegacyProcedures: new[] { $"dbo.{ProcEtcProcedure}" },
                    TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMiss" },
                    ErrorCodes: new[] { "4000" }, Chunkable: false, SchemaTables: Array.Empty<string>()),
                Array.Empty<string>(), new Dictionary<string, SpecConditions>(), null, null,
                new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
                {
                    [ProcEtcProcedure] = new SpecStatementFacts(
                        new[]
                        {
                            new SpecDmlRow("UPDATE", 1, 89, "TSettleMiss",
                                new[] { "ID", "ClientID", "OutYMD", "OutState", "IssueType" },
                                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        },
                        Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
                },
                null, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ProcEtcProcedure] = ProcEtcDdl });

        [Fact]
        public void R7_StaysSilent_WhenTheStepBindsAParameterWhereTheOriginalHasALiteral()
        {
            Assert.DoesNotContain(
                ValidateProcEtc(ProcEtcStep("OutState  = @p_intOutState")).Errors, e => e.Contains(Marker));
        }

        [Fact]
        public void R7_IsOneWay_FiresWhenTheStepHardcodesALiteralWhereTheOriginalHasAVariable()
        {
            var error = Assert.Single(
                ValidateProcEtc(ProcEtcStep("OutState  = 2", "IssueType = 15")).Errors, e => e.Contains(Marker));
            Assert.Contains("IssueType = 15", error);
        }

        [Fact]
        public void R7_StillFires_WhenTheLiteralItselfChanges()
        {
            Assert.Contains(ValidateProcEtc(ProcEtcStep("OutState  = 3")).Errors, e => e.Contains(Marker));
        }
    }
}
