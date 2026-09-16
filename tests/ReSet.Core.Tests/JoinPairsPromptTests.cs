using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [조인 짝 표] 명세서 「DML 범위」 표의 「조인 키」 칸은 <b>컬럼 이름만</b> 담아 어느 테이블끼리의 짝인지를 말하지 않는다.
/// 그래서 GPT 판 다섯이 같은 자리에서 짝을 지어냈다(`UP_UTIL_SETTLE_EXPECT_PROC` UPDATE 11 에 `A.ClientID = B.ClientID`).
/// 재료는 검사 N5 가 쓰는 `MechanicalValidator.BuildOriginalJoinPairs` 를 그대로 읽는다(오라클 = 원본 DDL).
/// 판독: docs/audit-reports/2026-09-16-조인짝-프롬프트-사전선언.md
/// </summary>
public sealed class JoinPairsPromptTests
{
    private const string Procedure = "dbo.UP_UTIL_SETTLE_EXPECT_PROC";
    private const string Update11Pairs =
        "TClientCMRate.ClientID=TSettleMst.ClientID AND TClientCMRate.PGName=TSettleMst.PGName AND TClientCMRate.MallID=TSettleMst.MallID AND TSettleMst.MPLTID=TSettleMst.PLTID";

    private static string Ddl() => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "join-pairs", "UP_UTIL_SETTLE_EXPECT_PROC.sql"));

    private static IReadOnlyList<BatchStepPlan> Steps => new[]
    {
        new BatchStepPlan("S09", "지급예정", new[] { Procedure }, new[] { "dbo.TSettleMst" }, new[] { "-17" }, false, Array.Empty<string>()),
    };

    private static IReadOnlyList<StepInterface> Interfaces(bool withDdl = true) =>
        StepInterfaceFacts.Build(
            Steps,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [Procedure] = new[] { "@pi_strYMD char(8)", "@po_intRetVal int" } },
            withDdl ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UP_UTIL_SETTLE_EXPECT_PROC"] = Ddl() } : null);

    // J1: 실물 DDL 에서 UPDATE 11 의 짝 넷이 그대로 나온다 - 자기 조인(MPLTID=PLTID)을 포함한다.
    [Fact]
    public void Build_CarriesTheExactJoinPairsOfEachStatement()
    {
        var joins = Assert.Single(Interfaces()).JoinPairs!;
        var update11 = Assert.Single(joins, j => j.Kind == "UPDATE" && j.Ordinal == 11);

        Assert.Equal("TSettleMst", update11.Target);
        Assert.Equal(Update11Pairs, string.Join(" AND ", update11.Pairs));
        Assert.Contains("TSettleMst.MPLTID=TSettleMst.PLTID", update11.Pairs);
        Assert.DoesNotContain("TSettleMst.ClientID=TSettleMst.ClientID", update11.Pairs);
    }

    // 렌더는 문장을 (종류 서수 · 대상)으로 가리키고 짝을 AND 로 잇는다.
    [Fact]
    public void RenderJoinPairTable_NamesTheStatementAndItsPairs()
    {
        var table = StepInterfaceFacts.RenderJoinPairTable(Interfaces());

        Assert.Contains("| S09 | UPDATE 11 | TSettleMst | `" + Update11Pairs + "` |", table);
    }

    private static IAiService Service()
    {
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-test");
        client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResult { Content = "### S09 지급예정" });
        return new AiService(client, 0.2f);
    }

    private static List<(string FileName, string Content)> Specs => new() { (Procedure, "본문") };

    private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [ { ""Code"": ""S09"", ""Name"": ""지급예정"", ""LegacyProcedures"": [""dbo.UP_UTIL_SETTLE_EXPECT_PROC""] } ] }
```";

    private static void AssertCarriesJoinPairs(AiResult result)
    {
        var prompt = result.UserPrompt + result.SystemPrompt;
        Assert.Contains("[Original Join Pairs]", prompt);
        Assert.Contains(Update11Pairs, prompt);
        Assert.Contains("do not add a pair the original did not have", prompt);
    }

    // J2: 경로마다 잠근다 - 조항이 한 곳이어도 호출이 셋이다.
    [Fact]
    public async Task StepSectionPrompt_CarriesTheJoinPairs() =>
        AssertCarriesJoinPairs(await Service().GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs, Interfaces(), "C#", "Job_Test"));

    [Fact]
    public async Task SkeletonPrompt_CarriesTheJoinPairs() =>
        AssertCarriesJoinPairs(await Service().GenerateBatchPlanSkeletonAsync(Steps, PlanStructure, Specs, "C#", "Job_Test", stepInterfaces: Interfaces()));

    [Fact]
    public async Task SingleCallFallbackPrompt_CarriesTheJoinPairs() =>
        AssertCarriesJoinPairs(await Service().GenerateConsolidatedBatchPlanAsync(PlanStructure, Specs, "C#", "Job_Test", stepInterfaces: Interfaces()));

    // J3: 재료가 없으면 절이 없다 - 짝 없는 Job 의 프롬프트는 바이트 그대로다.
    [Fact]
    public async Task WithoutTheOriginalDdl_TheSectionIsAbsent()
    {
        var result = await Service().GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs, Interfaces(withDdl: false), "C#", "Job_Test");

        Assert.DoesNotContain("[Original Join Pairs]", result.UserPrompt + result.SystemPrompt);
    }

    // J4: 재료는 검사와 같은 출처다 - 렌더가 자기 추출기를 새로 만들면 두 채번이 조용히 갈린다.
    [Fact]
    public void TheMaterialComesFromTheCheckSOwnBuilder()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "StepInterfaceFacts.cs"));
        var code = Regex.Replace(source, @"//[^\n]*", string.Empty);

        Assert.Matches(new Regex(@"MechanicalValidator\.BuildOriginalJoinPairs\(\s*step\s*,\s*ddlByProcedure\s*\)"), code);
    }
}
