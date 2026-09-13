using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [가드 술어 대조] 원본 <c>IF [NOT] EXISTS (SELECT … FROM T WHERE …)</c> 가드를 이행이 옮긴 존재 확인 질의의 WHERE 항이
/// 원본 DDL 과 같은가.
///
/// POQSettleBatch12(2026-09-13) S03 이 <c>UP_Util_PG_Client_CMRate_Ins</c> 20~24 행의 기지급 사전 차단에 원본에 없는
/// <c>PLTID = 1</c> 을 붙여 차단이 꺼진 채 배송됐다. B11 도 같은 가드에 <c>PLTID = 'POQ'</c> 를 붙였다. 검사는 전부 조용했다.
/// 판독: <c>docs/audit-reports/2026-09-13-가드술어-표류-측정.md</c>. 픽스처는 배송본·원본 DDL 을 바이트 그대로 옮긴 것이다.
/// </summary>
public sealed class GuardPredicateTermsTests
{
    private const string Marker = "원본 가드";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "guard-predicate", name));

    private static StepValidationResult Validate(string markdown, string procedure, bool withDdl = true)
    {
        var step = new BatchStepPlan(
            Code: "S03", Name: "가드 단계",
            LegacyProcedures: new[] { "dbo." + procedure },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
            ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());
        var ddl = withDdl
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [procedure] = Fixture(procedure + ".sql") }
            : null;

        return new MechanicalValidator().ValidateBatchStep(
            markdown, step, Array.Empty<string>(), new Dictionary<string, SpecConditions>(), ddlByProcedure: ddl);
    }

    private static IReadOnlyList<string> GuardErrors(StepValidationResult result) =>
        result.Errors.Where(e => e.Contains(Marker)).ToList();

    [Theory]
    [InlineData("Batch11-S05.md", "UP_Util_PG_Client_CMRate_Ins", "PLTID = 'POQ'", null)]
    [InlineData("Batch12-S03.md", "UP_Util_PG_Client_CMRate_Ins", "PLTID = 1", null)]
    [InlineData("Batch12-S10.md", "UP_UTIL_SETTLE_INS_EXTRA", "OutYMD <= @p_currYmd", "OutYMD IS NOT NULL")]
    [InlineData("Batch11-S11.md", "UP_UTIL_SETTLE_INS_EXTRA", "ISNULL(OutYMD, '') <> ''", "OutYMD IS NOT NULL")]
    // claude-cli 판이다. 조건이 여럿 바뀌어 공통 항이 5/7 뿐이라, 처음 쓴 문턱(가드 항 수 − 1)으로는 조용했다.
    [InlineData("Batch10-S08.md", "UP_UTIL_SETTLE_INS_EXTRA", "OutState IN (3, 4)", "OutState IN (1,5) AND OutYMD IS NOT NULL")]
    public void RealGuardTranslationsThatDrift_AreReported(string fixture, string procedure, string added, string? missing)
    {
        var error = Assert.Single(GuardErrors(Validate(Fixture(fixture), procedure)));

        Assert.Contains(added, error);
        if (missing != null) Assert.Contains(missing, error);
    }

    [Theory]
    [InlineData("Batch8-S03.md", "UP_Util_PG_Client_CMRate_Ins")]   // 같은 EXISTS (SELECT 1 …) 관용구로 원본대로
    [InlineData("Batch1-S02.md", "UP_Util_PG_Client_CMRate_Ins")]   // SELECT TOP 1 PLTID 모양
    [InlineData("Batch10-S04.md", "UP_UTIL_SETTLE_INS")]            // SELECT COUNT(1) 모양
    [InlineData("Batch8-S09.md", "UP_UTIL_SETTLE_INS_EXTRA")]       // 조건 7 개 가드를 원본대로
    public void RealGuardTranslationsThatKeepTheOriginalConditions_AreSilent(string fixture, string procedure)
    {
        Assert.Empty(GuardErrors(Validate(Fixture(fixture), procedure)));
    }

    // 양성 대조 짝 ①: 원본대로인 B8/S03 에 B12 의 표류를 그대로 넣으면 발화한다 - 위 침묵이 「이 단계를 못 읽는다」가 아니다.
    [Fact]
    public void KeptGuardWithTheDriftInjected_IsReported()
    {
        var original = Fixture("Batch8-S03.md");
        var mutated = original.Replace(
            "    SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst\n     WHERE YMD = @p_ymd\n",
            "    SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst\n     WHERE PLTID = 1\n       AND YMD = @p_ymd\n");
        Assert.NotEqual(original, mutated);

        Assert.Contains("PLTID = 1", Assert.Single(GuardErrors(Validate(mutated, "UP_Util_PG_Client_CMRate_Ins"))));
    }

    // 양성 대조 짝 ②: B12/S03 에서 지어낸 조건만 빼면 조용하다 - 위 발화가 그 한 항 때문임을 보인다.
    [Fact]
    public void DriftedGuardWithTheInventedTermRemoved_IsSilent()
    {
        var original = Fixture("Batch12-S03.md");
        var mutated = original.Replace(
            "                WHERE PLTID = 1\n                  AND YMD = @p_ymd\n",
            "                WHERE YMD = @p_ymd\n");
        Assert.NotEqual(original, mutated);

        Assert.Empty(GuardErrors(Validate(mutated, "UP_Util_PG_Client_CMRate_Ins")));
    }

    // [가드 중심 짝] 원본대로인 가드 번역 곁에, 가드와 조건 둘을 공유하는 사후 확인 질의가 있어도 조용하다 - 가드마다
    // 공통 항이 가장 많은 질의 하나(진짜 번역)만 짝이 된다. 질의마다 가드를 고르면 이 질의가 가드와 짝지어져 발화한다.
    [Fact]
    public void ASimilarPostCheckBesideAFaithfulGuard_IsNotPairedWithTheGuard()
    {
        var original = Fixture("Batch8-S03.md");
        var mutated = original.Replace(
            "-- SQL_PRECHECK_SETTLED_EXISTS\n",
            "-- SQL_POSTCHECK_REFUNDS\nSELECT COUNT(1) FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd AND OutState IN (1, 5) AND UseState = 3;\n\n-- SQL_PRECHECK_SETTLED_EXISTS\n");
        Assert.NotEqual(original, mutated);

        Assert.Empty(GuardErrors(Validate(mutated, "UP_Util_PG_Client_CMRate_Ins")));
    }

    // [R7] 원본 `ExtraSettleFlag = 1` 을 이행이 매개변수로 바인딩한 것은 표류가 아니다 - 형제 앵커 술어 대조와 같은 규칙.
    [Fact]
    public void OriginalLiteralBoundAsAParameter_IsNotADrift()
    {
        var original = Fixture("Batch8-S09.md");
        var mutated = original.Replace(
            "       AND ExtraSettleFlag = 1\n       AND OutState IN (1,5)\n",
            "       AND ExtraSettleFlag = @p_extraSettleFlag\n       AND OutState IN (1,5)\n");
        Assert.NotEqual(original, mutated);

        Assert.Empty(GuardErrors(Validate(mutated, "UP_UTIL_SETTLE_INS_EXTRA")));
    }

    // 지어낸 항의 컬럼이 원본 가드의 SELECT 목록 컬럼이면 그것이 조건이 아님을 처방에 싣는다(두 판이 그 오독을 했다).
    [Fact]
    public void AddedTermOnTheGuardsSelectListColumn_ExplainsItIsNotACondition()
    {
        Assert.Contains("SELECT 목록", Assert.Single(GuardErrors(Validate(Fixture("Batch12-S03.md"), "UP_Util_PG_Client_CMRate_Ins"))));
        Assert.DoesNotContain("SELECT 목록", Assert.Single(GuardErrors(Validate(Fixture("Batch12-S10.md"), "UP_UTIL_SETTLE_INS_EXTRA"))));
    }

    // 원본 DDL 이 없으면 기준이 없다 - 침묵한다.
    [Fact]
    public void WithoutTheOriginalDdl_IsSilent()
    {
        Assert.Empty(GuardErrors(Validate(Fixture("Batch12-S03.md"), "UP_Util_PG_Client_CMRate_Ins", withDdl: false)));
    }
}
