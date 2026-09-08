using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「이전 면제 - 커서 그룹」. 원본이 <b>커서로 집계 그룹을 순회</b>하던 자리를
    /// 이행이 집합 연산으로 치환하면 커서 변수였던 술어는 <b>옮겨간 것이 아니라 개념이
    /// 사라진다</b> - 검사 B 의 기존 <c>relocated</c> 로는 원리적으로 안 걸린다.
    ///
    /// 사전 선언: docs/audit-reports/2026-09-08-이전면제-사전선언.md
    /// 축·정밀도 실측: docs/audit-reports/2026-09-08-이전면제-실측.md
    ///
    /// [★ 이 시험들이 왜 유일한 탐지기인가]
    /// 면제 대상인 실물 두 발화는 <b>오늘 코퍼스에 존재하지 않는다</b> - 그 문장
    /// (<c>Batch1/S12/INSERT 1</c>)이 앵커를 못 받아 검사 B·C 가 아예 안 돌기 때문이다
    /// (그 앵커를 살리는 U-앵커 한 줄이 이 면제의 <b>다음</b> 걸음이다). 그래서
    /// <c>--sweep</c> 도 코퍼스도 이 면제의 생사를 못 본다. <b>면제가 죽으면 여기서만
    /// 빨개진다.</b>
    /// </summary>
    public class CursorGroupExemptionTests
    {
        private const string CursorDdl = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary_AcqManual @p CHAR(8)
AS
BEGIN
    DECLARE cur CURSOR FOR
        SELECT OutYMD, ClientID, PGName FROM dbo.TSettleMst
         WHERE EDIReqYmd = @p GROUP BY OutYMD, ClientID, PGName;
    DELETE FROM dbo.TSettleByOUT WHERE OutYMD = @o AND ClientID = @c AND PGName = @g;
END";

        private const string NoCursorDdl = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary_AcqManual @p CHAR(8)
AS
BEGIN
    DELETE FROM dbo.TSettleByOUT WHERE OutYMD = @o;
END";

        private static BatchStepPlan Step() => new(
            Code: "S12", Name: "S12 단계",
            LegacyProcedures: new[] { "dbo.UP_Util_Settle_Summary_AcqManual" },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
            ErrorCodes: new[] { "-1" }, Chunkable: false, SchemaTables: Array.Empty<string>());

        /// 실물 모양이다 - 원본 SELECT 1 이 그룹을 내고 DELETE 1 이 그 그룹 키로 필터한다.
        private static IReadOnlyDictionary<string, SpecStatementFacts> Facts(
            string[] selectGroupBy, string[] selectPredicates, string[]? secondSelectGroupBy = null)
        {
            var rows = new List<SpecDmlRow>
            {
                new("SELECT", 1, 29, "—", selectPredicates, new[] { "ClientID" },
                    selectGroupBy, Array.Empty<string>()),
                new("DELETE", 1, 46, "TSettleByOUT",
                    new[] { "OutYMD", "ClientID", "PGName" },
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
            };
            if (secondSelectGroupBy != null)
            {
                rows.Insert(1, new SpecDmlRow("SELECT", 2, 31, "—", Array.Empty<string>(),
                    Array.Empty<string>(), secondSelectGroupBy, Array.Empty<string>()));
            }

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_Util_Settle_Summary_AcqManual"] = new SpecStatementFacts(
                    rows, Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };
        }

        /// 커서를 집합으로 치환한 이행 - 그룹 키가 WHERE 에서 사라지고 원천 SELECT 의
        /// 필터가 올라왔다. 앵커(`DELETE 1`)가 있어야 검사 B·C 가 돈다.
        private const string RewrittenStep =
            "### S12 단계\n\n```sql\n" +
            "-- SQL_DELETE_GROUP (DELETE 1, 원본 라인 46)\n" +
            "DELETE FROM SETTLE_POQ_DB.dbo.TSettleByOUT\n" +
            " WHERE EDIReqYmd = @p_batchYmd AND AcqType = 1 AND OutState IN (2,9);\n" +
            "```\n";

        private static StepValidationResult Validate(
            IReadOnlyDictionary<string, SpecStatementFacts> facts, string ddl) =>
            new MechanicalValidator().ValidateBatchStep(
                RewrittenStep, Step(), Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(), null, null, facts, null, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_Util_Settle_Summary_AcqManual"] = ddl
                });

        [Fact]
        public void ExemptsTheCursorGroupKeysFromTheMissingPredicateCheck()
        {
            var result = Validate(
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" }),
                CursorDdl);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ExemptsTheSourceSelectPredicatesFromTheExtraConditionCheck()
        {
            var result = Validate(
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" }),
                CursorDdl);

            Assert.DoesNotContain(result.Errors,
                e => e.Contains("EDIReqYmd") || e.Contains("AcqType") || e.Contains("OutState"));
        }

        [Fact]
        public void StaysStrictWhenTheSourceSelectHasNoGroupBy()
        {
            // SUMMARY_ETC 모양이다 - 커서가 **행**을 순회한다. 술어가 사라지면 진짜 결함이다.
            var result = Validate(
                Facts(Array.Empty<string>(), new[] { "YMD", "OutState" }),
                CursorDdl);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void StaysStrictWithoutACursorInTheOriginal()
        {
            // 커서가 없으면 「술어 == 어떤 SELECT 의 GROUP BY」는 관용구가 아니라 우연이다.
            var result = Validate(
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" }),
                NoCursorDdl);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void StaysStrictWhenTwoSelectsShareTheSameGroupBy()
        {
            // 귀속이 모호하면 면제하지 않는다(작성 계약 7).
            var result = Validate(
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" },
                      secondSelectGroupBy: new[] { "OutYMD", "ClientID", "PGName" }),
                CursorDdl);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void StaysStrictWithoutTheOriginalDdl()
        {
            var result = new MechanicalValidator().ValidateBatchStep(
                RewrittenStep, Step(), Array.Empty<string>(),
                new Dictionary<string, SpecConditions>(), null, null,
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" }),
                null, null, null, null);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void DoesNotExemptExtrasTheSourceSelectNeverFiltered()
        {
            // 면제는 원천 SELECT 의 술어 컬럼까지다. 그 밖의 새 조건은 여전히 발화한다.
            var step = "### S12 단계\n\n```sql\n" +
                "-- SQL_DELETE_GROUP (DELETE 1, 원본 라인 46)\n" +
                "DELETE FROM SETTLE_POQ_DB.dbo.TSettleByOUT\n" +
                " WHERE EDIReqYmd = @p_batchYmd AND SomeInventedFlag = 1;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                step, Step(), Array.Empty<string>(), new Dictionary<string, SpecConditions>(),
                null, null,
                Facts(new[] { "OutYMD", "ClientID", "PGName" },
                      new[] { "EDIReqYmd", "AcqType", "OutState" }),
                null, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_Util_Settle_Summary_AcqManual"] = CursorDdl
                });

            Assert.Contains(result.Errors, e => e.Contains("SomeInventedFlag"));
        }
    }
}
