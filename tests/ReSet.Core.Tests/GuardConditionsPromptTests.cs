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
/// [가드 조건 블록] 단계 프롬프트에 원본 가드의 WHERE 가 없어, GPT 가 명세서 CRUD 참조 컬럼 칸의 SELECT 목록 <c>PLTID</c> 를 조건으로 옮겼다
/// (GPT 판 다섯 연속). 같은 요청 재생에서 원본 DDL 가드 조건 블록을 붙이면 0/3 이었다(그대로 2/3).
/// 판독: docs/audit-reports/2026-09-14-가드PLTID-날조-원인-측정.md §1·§3.
/// </summary>
public sealed class GuardConditionsPromptTests
{
    private const string Procedure = "dbo.UP_Util_PG_Client_CMRate_Ins";
    private const string ExactWhere = "YMD = @pi_strYMD AND OutState IN (1,5) AND OutYMD IS NOT NULL";

    private static string CmRateDdl() => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "guard-predicate", "UP_Util_PG_Client_CMRate_Ins.sql"));

    private static IReadOnlyList<BatchStepPlan> Steps => new[]
    {
        new BatchStepPlan("S03", "요율 스냅샷", new[] { Procedure }, new[] { "dbo.TPGSettleRate" }, new[] { "-9" }, false, Array.Empty<string>()),
    };

    private static IReadOnlyList<StepInterface> Interfaces(bool withDdl = true) =>
        StepInterfaceFacts.Build(
            Steps,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [Procedure] = new[] { "@pi_strYMD char(8)", "@po_intRetVal int" } },
            withDdl ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UP_Util_PG_Client_CMRate_Ins"] = CmRateDdl() } : null);

    // H1: 실물 DDL 에서 가드 한 줄 - WHERE 는 원문 항을 AND 로 이은 것(공백 접힘), SELECT 목록은 따로.
    [Fact]
    public void Build_CarriesTheGuardsExactWhereAndItsSelectListSeparately()
    {
        var guard = Assert.Single(Assert.Single(Interfaces()).Guards!);

        Assert.Equal(ExactWhere, guard.Where);
        Assert.Equal(new[] { "PLTID" }, guard.SelectColumns);
        Assert.Equal("TSettleMst", guard.Table);
        Assert.Equal(20, guard.Line);
    }

    private static IAiService Service()
    {
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-test");
        client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResult { Content = "### S03 요율 스냅샷" });
        return new AiService(client, 0.2f);
    }

    private static List<(string FileName, string Content)> Specs => new() { (Procedure, "본문") };

    private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [ { ""Code"": ""S03"", ""Name"": ""요율 스냅샷"", ""LegacyProcedures"": [""dbo.UP_Util_PG_Client_CMRate_Ins""] } ] }
```";

    private static void AssertCarriesGuards(AiResult result)
    {
        var prompt = result.UserPrompt + result.SystemPrompt;
        Assert.Contains("[Original Guard Conditions]", prompt);
        Assert.Contains(ExactWhere, prompt);
        Assert.Contains("not a condition", prompt);
    }

    // H2: 경로마다.
    [Fact]
    public async Task StepSectionPrompt_CarriesTheGuardConditions() =>
        AssertCarriesGuards(await Service().GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs, Interfaces(), "C#", "Job_Test"));

    [Fact]
    public async Task SkeletonPrompt_CarriesTheGuardConditions() =>
        AssertCarriesGuards(await Service().GenerateBatchPlanSkeletonAsync(Steps, PlanStructure, Specs, "C#", "Job_Test", stepInterfaces: Interfaces()));

    [Fact]
    public async Task SingleCallFallbackPrompt_CarriesTheGuardConditions() =>
        AssertCarriesGuards(await Service().GenerateConsolidatedBatchPlanAsync(PlanStructure, Specs, "C#", "Job_Test", stepInterfaces: Interfaces()));

    // H3: 가드 재료가 없으면 절이 없다 - 가드 없는 Job 프롬프트는 전과 같다.
    [Fact]
    public async Task WithoutGuards_TheSectionIsAbsent()
    {
        var result = await Service().GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs, Interfaces(withDdl: false), "C#", "Job_Test");
        Assert.DoesNotContain("[Original Guard Conditions]", result.UserPrompt + result.SystemPrompt);
    }

    // H4: 오케스트레이터의 Build 호출 둘이 DDL 을 넘긴다 - 발화 시험은 배선이 빠져도 초록이다.
    [Fact]
    public void OrchestratorPassesTheDdlToBothInterfaceBuilds()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));
        Assert.Equal(2, Regex.Matches(source, @"StepInterfaceFacts\.Build\(\s*\w+\s*,\s*parametersByProcedure\s*,\s*ddlByProcedure\s*\)").Count);
    }
}
