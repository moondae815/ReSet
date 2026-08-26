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
    /// 재시도 회차의 명세서 목록에는 검토 피드백이 <c>Feedback_Log.txt</c>라는
    /// 가짜 항목으로 끼워져 온다. 프롬프트가 그것을 프로시저 명세서로 세거나
    /// 명세서 자리에 놓으면 안 된다.
    /// </summary>
    public class FeedbackSpecPromptTests
    {
        private static (IAiService Service, IAiClient Client) Build()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            return (new AiService(client, 0.2f), client);
        }

        private static List<(string FileName, string Content)> SpecsWithFeedback() => new()
        {
            ("dbo.UP_A", "명세서 본문 A"),
            ("dbo.UP_B", "명세서 본문 B"),
            (FeedbackSpec.CriticFileName, "[이전 시도에 대한 검토 피드백]: S05가 틀렸습니다.")
        };

        [Fact]
        public async Task Brainstorm_ShouldNotCountTheFeedbackEntryAsAProcedure()
        {
            // 재시도 회차부터 개수가 하나 부풀면 모델이 없는 프로시저를 찾아 단계를
            // 설계한다. 1차 회차와 재시도 회차가 다른 수를 보는 것 자체가 결함이다.
            var (service, _) = Build();

            var result = await service.BrainstormBatchPlanAsync(SpecsWithFeedback(), "C#", "Job_Test");

            Assert.Contains("Total Legacy Stored Procedures to Consolidate: 2 procedures", result.UserPrompt);
            Assert.DoesNotContain("Filename: " + FeedbackSpec.CriticFileName, result.UserPrompt);
        }

        [Fact]
        public async Task Brainstorm_ShouldStillDeliverTheFeedbackUnderItsOwnHeading()
        {
            // 명세서 자리에서 걷어냈다고 피드백이 사라지면 자가 수정이 성립하지 않는다.
            var (service, _) = Build();

            var result = await service.BrainstormBatchPlanAsync(SpecsWithFeedback(), "C#", "Job_Test");

            Assert.Contains(FeedbackSpec.PromptHeader, result.UserPrompt);
            Assert.Contains("S05가 틀렸습니다.", result.UserPrompt);
        }

        [Fact]
        public async Task Brainstorm_WithoutFeedback_ShouldNotEmitTheHeading()
        {
            // 1차 회차 프롬프트에 빈 머리글이 붙으면 재시도 회차와 바이트가 달라져
            // 접두사 캐시가 산다는 전제가 깨진다.
            var (service, _) = Build();
            var specs = new List<(string FileName, string Content)>
            {
                ("dbo.UP_A", "명세서 본문 A"),
                ("dbo.UP_B", "명세서 본문 B")
            };

            var result = await service.BrainstormBatchPlanAsync(specs, "C#", "Job_Test");

            Assert.DoesNotContain(FeedbackSpec.PromptHeader, result.UserPrompt);
            Assert.Contains("Total Legacy Stored Procedures to Consolidate: 2 procedures", result.UserPrompt);
        }

        [Fact]
        public async Task StepSection_ShouldKeepTheFeedbackOutOfTheSharedPrefix()
        {
            // 실제로 피드백이 접두사에 실리는 곳은 골격·단계 섹션이다(리뷰 호출은
            // 오케스트레이터가 원본 specs를 넘기므로 애초에 피드백을 받지 않는다).
            // 단계 섹션 프롬프트는 N개 단계가 공유하는 접두사이고, 피드백이 그 안에
            // 있으면 재시도 회차마다 N개 호출의 접두사가 전부 무효가 된다.
            var (service, client) = Build();
            var step = new BatchStepPlan("S01", "날짜 검증", new[] { "dbo.UP_A" },
                new string[0], new string[0], false, new string[0]);

            await service.GenerateBatchStepSectionAsync(
                step, new[] { step }, "공통 규약", SpecsWithFeedback(),
                new StepInterface[0], "C#", "Job_Test");

            var call = client.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IAiClient.ChatAsync));
            var sharedPrefix = (string)call.GetArguments()[1]!;
            var volatileSuffix = (string?)call.GetArguments()[4];

            Assert.DoesNotContain("S05가 틀렸습니다.", sharedPrefix);
            Assert.Contains("S05가 틀렸습니다.", volatileSuffix);
        }

        [Fact]
        public async Task Review_ShouldKeepTheFeedbackOutOfTheCachePrefix()
        {
            // 리뷰 프롬프트는 명세서를 불변 접두사로 두어 캐시를 노린다(실측 481KB).
            // 회차마다 바뀌는 피드백이 접두사에 섞이면 매 회차 캐시가 통째로 죽는다.
            var (service, client) = Build();

            await service.ReviewConsolidatedPlanAsync(SpecsWithFeedback(), "# 계획서", "Job_Test");

            var stablePrefix = (string)client.ReceivedCalls().Single(call =>
                call.GetMethodInfo().Name == nameof(IAiClient.ChatAsync)).GetArguments()[1]!;

            Assert.DoesNotContain(FeedbackSpec.CriticFileName, stablePrefix);
            Assert.DoesNotContain("S05가 틀렸습니다.", stablePrefix);
            Assert.Contains("dbo.UP_A", stablePrefix);
        }
    }
}
