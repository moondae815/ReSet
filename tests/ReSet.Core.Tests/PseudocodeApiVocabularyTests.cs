using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [의사코드 API 어휘] 프롬프트가 리포지터리 헬퍼 이름을 규정하지 않아 모델이 단계마다 이름을 새로 지었고,
/// Critic 이 그 산포를 「한 문서 한 표기」 위반으로 감점했다(B18 2 차: 다중 행 조회가 `queryAll`·`queryMany`·`query` 셋).
///
/// 정본 넷은 <b>관측에서</b> 골랐다 — 배송본 <b>14</b> 편 전수에서 `repository.` 접두사가 붙은 호출을 세면
/// `execute` 610(7 편) · `queryScalar` 71(5 편) · `queryRows` 37(4 편) · `queryRow` 33(4 편)이 최다이고,
/// 밀려난 이름은 `queryOne` 9 · `queryAll` 6 · `query` 4 · `queryMany` 3 · `querySingle` 1 · `queryRowOrNone` 1 이다.
/// 접두사를 무관하게 세면 같은 연산을 두 이름 이상으로 쓰는 문서가 <b>6/14</b>(B5·B10·B12·B14·B17·B18)이고,
/// 접두사 자체도 시대별로 갈린다(`repository.execute` 610 대 바닥 `execute` 642 — B1·B4·B5·B7·B8 이 바닥형).
/// 정정: 처음에 「13 편 · 혼용 4」로 적었는데 분모와 집계가 모두 틀렸다(2026-09-17 리뷰).
/// 판독: docs/audit-reports/2026-09-17-의사코드-API-어휘-계약-사전선언.md
/// </summary>
public sealed class PseudocodeApiVocabularyTests
{
    private const string Procedure = "dbo.UP_UTIL_SETTLE_INS";

    private static IReadOnlyList<BatchStepPlan> Steps => new[]
    {
        new BatchStepPlan("S04", "정산 원장 적재", new[] { Procedure }, new[] { "dbo.TSettleMst" }, new[] { "-1" }, false, Array.Empty<string>()),
    };

    private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [ { ""Code"": ""S04"", ""Name"": ""정산 원장 적재"", ""LegacyProcedures"": [""dbo.UP_UTIL_SETTLE_INS""] } ] }
```";

    private static List<(string FileName, string Content)> Specs => new() { (Procedure, "본문") };

    private static IAiService Service()
    {
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-test");
        client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResult { Content = "### S04 정산 원장 적재" });
        return new AiService(client, 0.2f);
    }

    /// <summary>정본 넷과 「이름을 새로 만들지 말라」가 함께 있어야 한다 - 목록만 주면 모델이 그 밖의 이름을 덧붙인다.</summary>
    private static void AssertCarriesTheVocabulary(AiResult result)
    {
        var prompt = result.UserPrompt + result.SystemPrompt;

        Assert.Contains("[Pseudocode Repository API]", prompt);
        Assert.Contains("repository.execute", prompt);
        Assert.Contains("repository.queryRows", prompt);
        Assert.Contains("repository.queryRow", prompt);
        Assert.Contains("repository.queryScalar", prompt);
        // 밀려난 이름을 금지 목록으로 못박는다 - 그것이 실제로 나온 이름들이다.
        Assert.Contains("queryAll", prompt);
        Assert.Contains("queryMany", prompt);
        Assert.Contains("queryOne", prompt);
        Assert.Contains("one row or none", prompt);
        Assert.Contains("never invent a second name for the same operation", prompt);
        // 저널·체크포인트 헬퍼는 이름을 고정하지 않되 문서 안에서 하나여야 한다.
        Assert.Contains("one name per operation for the whole document", prompt);
    }

    /// <summary>재료(단계 인터페이스)가 있는 평소 경로.</summary>
    private static IReadOnlyList<StepInterface> Interfaces => StepInterfaceFacts.Build(
        Steps,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Procedure] = new[] { "@pi_strYMD char(8)" },
        },
        null);

    // 경로마다 잠근다 - 조항이 한 곳이어도 호출이 셋이다.
    [Fact]
    public async Task StepSectionPrompt_CarriesTheVocabulary() =>
        AssertCarriesTheVocabulary(await Service().GenerateBatchStepSectionAsync(
            Steps[0], Steps, "공통 규약", Specs, Interfaces, "C#", "Job_Test"));

    [Fact]
    public async Task SkeletonPrompt_CarriesTheVocabulary() =>
        AssertCarriesTheVocabulary(await Service().GenerateBatchPlanSkeletonAsync(
            Steps, PlanStructure, Specs, "C#", "Job_Test"));

    [Fact]
    public async Task SingleCallFallbackPrompt_CarriesTheVocabulary() =>
        AssertCarriesTheVocabulary(await Service().GenerateConsolidatedBatchPlanAsync(
            PlanStructure, Specs, "C#", "Job_Test"));

    // 재료 유무와 무관한 고정 자산이다 - 제어 계약 표와 같은 계열(단계 인터페이스가 없어도 실린다).
    [Fact]
    public async Task TheVocabularyIsCarriedEvenWithoutStepInterfaces() =>
        AssertCarriesTheVocabulary(await Service().GenerateBatchStepSectionAsync(
            Steps[0], Steps, "공통 규약", Specs, Array.Empty<StepInterface>(), "C#", "Job_Test"));

    // 계획 시도 재사용 키가 올라가야 옛 시도가 새 계약을 우회하지 못한다.
    [Fact]
    public void ThePlanAttemptContractVersion_WasRaisedForThisContract()
    {
        Assert.True(PlanAttemptJournal.ContractVersion >= 3,
            "계약이 바뀌었는데 ContractVersion 이 그대로면 재개가 옛 시도를 그대로 돌려준다");
    }
}
