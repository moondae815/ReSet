using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// [RunId 수명 계약] 제어 계약 표가 「각 단계가 자기 저널·체크포인트 행을 쓴다」고만 말하면, 첫 단계를 읽기 전용 사전 검증으로 두고 그다음 단계가
    /// <c>batch.BatchRun</c> 을 INSERT 하는 목차(계약이 허용한 설계)에서 사전 검증 단계에 쓸 RunId 가 없다. GPT 판 4 개 중 3 개가 그 목차로
    /// 게이트가 S01 을 요구하는 게시 불가 계획서를 배송했다. 판독: docs/audit-reports/2026-09-14-RunId수명-계약-사전선언.md
    ///
    /// 표 하나(<see cref="BatchControlContract.RenderPromptTable"/>)가 세 생성 경로에 실리므로 잠금은 **경로마다** 건다 -
    /// 조항이 한 곳에 있어도 호출이 셋이면, 잠금은 도는 쪽을 봐야 한다.
    /// </summary>
    public class RunIdLifetimeContractTests
    {
        private const string LifetimeMarker = "Run id lifetime";
        private const string GateSentence = "MUST NOT list such a step";

        private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [
  { ""Code"": ""S01"", ""Name"": ""사전 검증"", ""LegacyProcedures"": [] },
  { ""Code"": ""S02"", ""Name"": ""실행 등록"", ""LegacyProcedures"": [] }
] }
```";

        private static IReadOnlyList<BatchStepPlan> Steps => new[]
        {
            new BatchStepPlan("S01", "사전 검증", Array.Empty<string>(), Array.Empty<string>(), new[] { "-9010" }, false, Array.Empty<string>()),
            new BatchStepPlan("S02", "실행 등록", Array.Empty<string>(), new[] { "batch.BatchRun" }, new[] { "-9020" }, false, Array.Empty<string>()),
        };

        private static IAiService Service()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "### S01 사전 검증" });
            return new AiService(client, 0.2f);
        }

        private static List<(string FileName, string Content)> Specs => new() { ("dbo.UP_A", "본문") };

        [Fact]
        public void ContractTable_StatesTheRunIdLifetime()
        {
            var table = BatchControlContract.RenderPromptTable();

            Assert.Contains(LifetimeMarker, table);
            Assert.Contains(GateSentence, table);
            // 「각 단계」를 RunId 가 생긴 뒤로 좁혔다 - 무조건 문구가 남으면 모순이 그대로다.
            Assert.DoesNotContain("| EACH step INSERTs its own row when it starts", table);
            Assert.Contains("EACH step that runs after the run row exists INSERTs its own row", table);
        }

        [Fact]
        public async Task SkeletonPrompt_CarriesTheRunIdLifetime()
        {
            var result = await Service().GenerateBatchPlanSkeletonAsync(Steps, PlanStructure, Specs, "C#", "Job_Test");
            Assert.Contains(GateSentence, result.UserPrompt + result.SystemPrompt);
        }

        [Fact]
        public async Task StepSectionPrompt_CarriesTheRunIdLifetime()
        {
            var result = await Service().GenerateBatchStepSectionAsync(
                Steps[0], Steps, "공통 규약", Specs, Array.Empty<StepInterface>(), "C#", "Job_Test");
            Assert.Contains(GateSentence, result.UserPrompt + result.SystemPrompt);
        }

        [Fact]
        public async Task SingleCallFallbackPrompt_CarriesTheRunIdLifetime()
        {
            var result = await Service().GenerateConsolidatedBatchPlanAsync(PlanStructure, Specs, "C#", "Job_Test");
            Assert.Contains(GateSentence, result.UserPrompt + result.SystemPrompt);
        }
    }
}
