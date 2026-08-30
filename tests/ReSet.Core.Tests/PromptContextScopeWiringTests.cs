using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Task 10b - PromptContextScope가 실제로 GenerateBatchStepSectionAsync의 입력을
    /// 좁히는지, 그리고 Full 모드가 그 배선의 존재로 인해 단 한 바이트도 달라지지
    /// 않는지를 고정한다.
    /// </summary>
    public class PromptContextScopeWiringTests
    {
        private static (IAiService Service, IAiClient Client) Build(string providerName, string? contextScope = null)
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns(providerName);
            client.ModelName.Returns("test-model");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            return (new AiService(client, 0.2f, contextScope: contextScope), client);
        }

        private static BatchStepPlan Step(string code, params string[] procedures) =>
            new(code, $"{code} 단계",
                LegacyProcedures: procedures,
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static List<(string FileName, string Content)> Specs() => new()
        {
            ("dbo.UP_Util_Settle_Summary", "S11 명세서 본문"),
            ("dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA", "S13 명세서 본문 — 오류 시 4000~4008"),
            ("dbo.UP_Unrelated_Other", "이웃도 아니고 이 단계 소유도 아닌 명세서")
        };

        // 설계 불변식(§14): Full 모드의 접두사는 이 배선의 존재 자체로도 바이트가
        // 달라지면 안 된다. callGraph가 있든 없든 결과가 완전히 같아야 한다 -
        // 그것만이 "Narrow에서만 좁힌다"는 계약을 코드로 고정한다.
        [Fact]
        public async Task FullMode_IgnoresCallGraphAndKeepsEveryByteIdentical()
        {
            var (service, _) = Build("OpenAI");
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_Util_Settle_Summary"] = new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA" }
            };

            var withoutGraph = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test",
                callGraph: null);
            var withGraph = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test",
                callGraph: callGraph);

            Assert.Equal(withoutGraph.UserPrompt, withGraph.UserPrompt);
            Assert.Equal(withoutGraph.SystemPrompt, withGraph.SystemPrompt);
        }

        // Full 모드는 이웃이 아닌 명세서도 전량 싣는다 - 좁히기 자체가 일어나지 않는다.
        [Fact]
        public async Task FullMode_CarriesEverySpecRegardlessOfRelevance()
        {
            var (service, _) = Build("OpenAI");
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test");

            Assert.Contains("이웃도 아니고 이 단계 소유도 아닌 명세서", result.UserPrompt);
        }

        // CLI 제공자는 기본이 Narrow다 - 이 단계 소유도 이웃도 아닌 명세서는 빠져야 한다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public async Task NarrowMode_ExcludesASpecThatIsNeitherOwnedNorAOneHopNeighbor(string cliProvider)
        {
            var (service, _) = Build(cliProvider);
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test",
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Contains("S11 명세서 본문", result.UserPrompt);
            Assert.DoesNotContain("이웃도 아니고 이 단계 소유도 아닌 명세서", result.UserPrompt);
        }

        // 실측 「필수 수정 1·2」의 관계: S13이 S11이 규정한 오류 코드를 지켜야 했다.
        // 1-hop 이웃을 넣지 않으면 이 유형의 결함이 오히려 늘어난다.
        [Fact]
        public async Task NarrowMode_IncludesTheOneHopCallee()
        {
            var (service, _) = Build("claude-cli");
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_Util_Settle_Summary"] = new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA" }
            };

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test",
                callGraph: callGraph);

            Assert.Contains("S13 명세서 본문", result.UserPrompt);
        }

        // Task 9b가 배선한 재시도 피드백은 명세서 좁히기와 무관한 경로(volatileSuffix)로
        // 나간다. Narrow가 명세서를 좁혀도 피드백까지 걷어내면 자가 수정이 끊긴다.
        [Fact]
        public async Task NarrowMode_StillDeliversRetryFeedbackDespiteNarrowingTheSpecs()
        {
            var (service, _) = Build("claude-cli");
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };
            var specsWithFeedback = Specs();
            specsWithFeedback.Add((FeedbackSpec.CriticFileName,
                "[이전 시도에 대한 검토 피드백]: 오류 코드 4000~4008을 보존하십시오."));

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", specsWithFeedback, Array.Empty<StepInterface>(), "C#", "Job_Test",
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Contains("오류 코드 4000~4008을 보존하십시오.", result.UserPrompt);
            // 좁혀진 명세서 표에는 피드백이 프로시저 명세서로 놓이면 안 된다.
            Assert.DoesNotContain("Filename: " + FeedbackSpec.CriticFileName, result.UserPrompt);
        }

        // configured 문자열이 provider 기본을 덮어쓴다 - CLI 제공자에도 Full을 강제할
        // 수 있어야 한다(예: 실측을 다시 확인하려는 A/B 비교).
        [Fact]
        public async Task ConfiguredFull_OverridesTheCliDefaultAndKeepsEverySpec()
        {
            var (service, _) = Build("claude-cli", contextScope: "Full");
            var steps = new[] { Step("S11", "dbo.UP_Util_Settle_Summary") };

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약", Specs(), Array.Empty<StepInterface>(), "C#", "Job_Test");

            Assert.Contains("이웃도 아니고 이 단계 소유도 아닌 명세서", result.UserPrompt);
        }
    }
}
