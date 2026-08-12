using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// gpt-5.6 이후 모델은 암묵적 cache breakpoint를 **마지막 메시지**에 놓고, 그 지점의
    /// 접두사 전체가 일치해야 캐시가 산다. 12개 단계 요청이 공유하는 243KB 컨텍스트 뒤에
    /// 단계 지시 107자를 이어 붙이면 그 한 줄이 breakpoint 접두사를 매번 깨뜨려,
    /// 실측에서 113,142 토큰 중 2,062(=system 크기)만 히트했다.
    ///
    /// 가변 문구를 별도 메시지로 떼면 공통 메시지 경계가 그대로 남아 캐시가 산다.
    /// </summary>
    public class PromptCacheBreakpointTests
    {
        private const string Gpt5Response = @"{
            ""output"": [
                { ""type"": ""message"", ""content"": [ { ""type"": ""output_text"", ""text"": ""ok"" } ] }
            ]
        }";

        private static (OpenAiClient Client, OpenAiRequestSpyHandler Spy) NewGpt5Client()
        {
            var spy = new OpenAiRequestSpyHandler(Gpt5Response);
            var client = new OpenAiClient(
                new HttpClient(spy), "test_api_key", "https://api.openai.com/v1", "gpt-5.6-terra");
            return (client, spy);
        }

        private static JsonElement InputOf(OpenAiRequestSpyHandler spy) =>
            JsonDocument.Parse(spy.LastRequestContent!).RootElement.GetProperty("input");

        [Fact]
        public async Task Gpt5_KeepsTheVolatileSuffixOutOfTheSharedUserMessage()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync(
                "SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S01 ONLY.");

            var input = InputOf(spy);
            Assert.Equal(3, input.GetArrayLength());
            Assert.Equal("SharedContext", input[1].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("Now write step S01 ONLY.", input[2].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("user", input[2].GetProperty("role").GetString());
        }

        // 캐시가 사는 조건 그 자체: 공통 두 메시지가 요청 간 바이트 단위로 같아야 한다.
        [Fact]
        public async Task Gpt5_TwoStepsShareAByteIdenticalPrefixUpToTheVolatileMessage()
        {
            var (client1, spy1) = NewGpt5Client();
            var (client2, spy2) = NewGpt5Client();

            await client1.ChatAsync("SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S01 ONLY.");
            await client2.ChatAsync("SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S02 ONLY.");

            var a = InputOf(spy1);
            var b = InputOf(spy2);
            Assert.Equal(a[0].GetRawText(), b[0].GetRawText());
            Assert.Equal(a[1].GetRawText(), b[1].GetRawText());
            Assert.NotEqual(a[2].GetRawText(), b[2].GetRawText());
        }

        // 메시지를 나누는 것만으로는 부족했다 — 실측에서 cached_tokens가 2,062에서
        // 2,060으로 그대로였다. 암묵적 breakpoint는 마지막 메시지 하나에만 놓이므로
        // 공통 메시지 경계에는 아무것도 생기지 않는다. 캐시를 살리려면 그 경계를
        // explicit breakpoint로 직접 표시해야 하고, breakpoint는 메시지가 아니라
        // content 블록에 붙으므로 content가 타입 블록 배열이어야 한다.
        [Fact]
        public async Task Gpt5_MarksTheSharedMessageWithAnExplicitCacheBreakpoint()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync(
                "SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S01 ONLY.");

            var shared = InputOf(spy)[1].GetProperty("content")[0];
            Assert.Equal("input_text", shared.GetProperty("type").GetString());
            Assert.Equal("SharedContext", shared.GetProperty("text").GetString());
            Assert.Equal(
                "explicit",
                shared.GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        }

        // breakpoint는 공통 메시지 경계 하나뿐이어야 한다. 가변 메시지에도 붙이면
        // 그 지점의 접두사가 매번 달라 캐시가 살지 않고, 쓰기 비용만 늘어난다.
        [Fact]
        public async Task Gpt5_DoesNotMarkTheVolatileMessageWithABreakpoint()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync(
                "SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S01 ONLY.");

            var input = InputOf(spy);
            Assert.False(input[2].GetProperty("content")[0].TryGetProperty(
                "prompt_cache_breakpoint", out _));
            Assert.False(input[0].GetProperty("content")[0].TryGetProperty(
                "prompt_cache_breakpoint", out _));
        }

        // 블록 배열로 보낼 때는 세 메시지의 표현을 통일한다. 한 요청 안에서 문자열
        // content와 블록 배열을 섞는 형태는 문서가 보증하지 않는다.
        [Fact]
        public async Task Gpt5_WithABreakpoint_SendsEveryMessageAsTypedBlocks()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync(
                "SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "Now write step S01 ONLY.");

            foreach (var message in InputOf(spy).EnumerateArray())
            {
                Assert.Equal("message", message.GetProperty("type").GetString());
                Assert.Equal(JsonValueKind.Array, message.GetProperty("content").ValueKind);
                Assert.Equal("input_text", message.GetProperty("content")[0].GetProperty("type").GetString());
            }
        }

        // 접미사가 없는 호출(브레인스토밍·목차·골격·리뷰)은 형식을 바꾸지 않는다.
        // 얻을 캐시 이득이 없는데 표현만 바꾸면 400의 위험만 떠안는다.
        [Fact]
        public async Task Gpt5_WithoutAVolatileSuffix_KeepsThePlainStringContent()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync("SharedSystem", "SharedContext", 1.0f, effort: "high");

            foreach (var message in InputOf(spy).EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, message.GetProperty("content").ValueKind);
            }
        }

        [Fact]
        public async Task Gpt5_WithoutAVolatileSuffix_StillSendsTwoMessages()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync("SharedSystem", "SharedContext", 1.0f, effort: "high");

            Assert.Equal(2, InputOf(spy).GetArrayLength());
        }

        // 빈 접미사로 메시지를 하나 더 만들면 그 자체가 캐시 접두사를 바꾼다.
        [Fact]
        public async Task Gpt5_WithABlankVolatileSuffix_StillSendsTwoMessages()
        {
            var (client, spy) = NewGpt5Client();

            await client.ChatAsync("SharedSystem", "SharedContext", 1.0f,
                effort: "high", volatileUserSuffix: "   ");

            Assert.Equal(2, InputOf(spy).GetArrayLength());
        }

        // Responses API를 쓰지 않는 경로는 메시지를 나눌 수 없다. 프롬프트가 잘리지 않도록
        // 이어 붙여, 모델이 받는 내용이 분리 이전과 같아야 한다.
        [Fact]
        public async Task ChatCompletions_AppendsTheVolatileSuffixToTheUserMessage()
        {
            var spy = new OpenAiRequestSpyHandler(
                @"{ ""choices"": [ { ""message"": { ""content"": ""ok"" } } ] }");
            var client = new OpenAiClient(
                new HttpClient(spy), "test_api_key", "https://api.openai.com/v1", "gpt-4o");

            await client.ChatAsync("SharedSystem", "SharedContext", 0.7f,
                volatileUserSuffix: "Now write step S01 ONLY.");

            var messages = JsonDocument.Parse(spy.LastRequestContent!)
                .RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal(
                "SharedContext\n\nNow write step S01 ONLY.",
                messages[1].GetProperty("content").GetString());
        }

        private static IReadOnlyList<BatchStepPlan> TwoSteps() => new[]
        {
            new BatchStepPlan("S01", "수수료율 스냅샷",
                new[] { "UP_Util_PG_Client_CMRate_Ins" }, new[] { "dbo.TPGSettleRate" }, new[] { "-1" }, false,
                Array.Empty<string>()),
            new BatchStepPlan("S02", "정산 원장 생성",
                new[] { "UP_UTIL_SETTLE_INS" }, new[] { "dbo.TSettleMst" }, new[] { "-2" }, true,
                Array.Empty<string>())
        };

        private static readonly List<(string FileName, string Content)> Specs = new()
        {
            ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
        };

        private static (IAiService Service, IAiClient Client) NewSpyService()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-5.6-terra");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "### S01 수수료율 스냅샷\n\n```sql\nSELECT 1;\n```" });
            return (new AiService(client, 0.2f), client);
        }

        // 공통 컨텍스트를 담은 메시지에 단계 지시가 섞이면 캐시가 죽는다. 지시는
        // volatileUserSuffix로만 나가야 한다.
        [Fact]
        public async Task StepSection_SendsTheStepInstructionOutsideTheSharedUserMessage()
        {
            var (service, client) = NewSpyService();
            var steps = TwoSteps();

            await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", Specs, "C#", "Test_Job");

            await client.Received(1).ChatAsync(
                Arg.Any<string>(),
                Arg.Is<string>(shared => !shared.Contains("Now write the section for step")),
                Arg.Any<float>(),
                Arg.Any<string>(),
                Arg.Is<string>(volatileSuffix =>
                    volatileSuffix.Contains("Now write the section for step S01")),
                Arg.Any<CancellationToken>());
        }

        // 재시도 피드백도 회차마다 달라지므로 공통 메시지에 남으면 캐시를 깬다.
        [Fact]
        public async Task StepSection_SendsTheFloorFeedbackOutsideTheSharedUserMessage()
        {
            var (service, client) = NewSpyService();
            var steps = TwoSteps();

            await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", Specs, "C#", "Test_Job",
                effort: null, floorFeedback: "코드 블록이 없습니다");

            await client.Received(1).ChatAsync(
                Arg.Any<string>(),
                Arg.Is<string>(shared => !shared.Contains("코드 블록이 없습니다")),
                Arg.Any<float>(),
                Arg.Any<string>(),
                Arg.Is<string>(volatileSuffix => volatileSuffix.Contains("코드 블록이 없습니다")),
                Arg.Any<CancellationToken>());
        }

        // 두 메시지로 나뉘어도 raw/prompt-context.md는 모델이 실제로 받은 것을 서술해야
        // 한다. AGENTS.md가 문서화한 계약이다.
        [Fact]
        public async Task StepSection_RecordsTheMergedPromptSoThePromptContextStaysTruthful()
        {
            var (service, _) = NewSpyService();
            var steps = TwoSteps();

            var result = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", Specs, "C#", "Test_Job",
                effort: null, floorFeedback: "코드 블록이 없습니다");

            Assert.Contains("공통 규약 본문", result.UserPrompt);
            Assert.Contains("Now write the section for step S01", result.UserPrompt);
            Assert.Contains("코드 블록이 없습니다", result.UserPrompt);
        }

        private static (ClaudeClient Client, ClaudeRequestSpyHandler Spy) NewClaudeClient()
        {
            var spy = new ClaudeRequestSpyHandler(
                @"{""content"":[{""type"":""text"",""text"":""ok""}]}");
            var client = new ClaudeClient(
                new HttpClient(spy), "test_api_key", "https://api.anthropic.com", "claude-sonnet-5");
            return (client, spy);
        }

        private static JsonElement UserContentOf(ClaudeRequestSpyHandler spy) =>
            JsonDocument.Parse(spy.LastRequestContent!).RootElement
                .GetProperty("messages")[0].GetProperty("content");

        // 접미사가 없으면 표현을 바꾸지 않는다. 평문 문자열을 블록 배열로 바꾸는 것
        // 자체가 접두사를 바꿔, 접미사 없는 호출들끼리의 캐시를 깨기 때문이다.
        [Fact]
        public async Task Claude_WithoutAVolatileSuffix_KeepsThePlainStringContent()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f);

            Assert.Equal(JsonValueKind.String, UserContentOf(spy).ValueKind);
            Assert.Equal("SharedContext", UserContentOf(spy).GetString());
        }

        // 첫 전송에는 중단점을 찍지 않는다. 캐시 쓰기가 1.25배라, 1회차에 끝나는 잡
        // (실측 5건 중 4건)에서 손해가 확정되기 때문이다.
        [Fact]
        public async Task Claude_OnTheFirstSend_SplitsIntoBlocksWithoutACacheBreakpoint()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            var content = UserContentOf(spy);
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("SharedContext", content[0].GetProperty("text").GetString());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("PlanBody v1", content[1].GetProperty("text").GetString());
            Assert.False(content[0].TryGetProperty("cache_control", out _));
        }

        // 재생성 회차: 같은 접두사를 다시 보내면 공유 블록에 중단점을 찍는다.
        [Fact]
        public async Task Claude_OnTheSecondSend_MarksTheSharedBlockWithCacheControl()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v2");

            var content = UserContentOf(spy);
            Assert.Equal(
                "ephemeral",
                content[0].GetProperty("cache_control").GetProperty("type").GetString());
        }

        // 가변 블록에 찍으면 그 지점의 접두사가 매번 달라 캐시가 살지 않고
        // 쓰기 비용만 늘어난다.
        [Fact]
        public async Task Claude_NeverMarksTheVolatileBlock()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v2");

            Assert.False(UserContentOf(spy)[1].TryGetProperty("cache_control", out _));
        }

        // 시스템 블록의 중단점은 이미 동작 중이고(실측 1,818 히트), user 블록이 달라진
        // 호출에서도 최소한의 폴백 접두사 역할을 한다. 어떤 경우에도 유지한다.
        [Fact]
        public async Task Claude_AlwaysKeepsTheSystemBlockBreakpoint()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            var system = JsonDocument.Parse(spy.LastRequestContent!).RootElement
                .GetProperty("system")[0];
            Assert.Equal(
                "ephemeral",
                system.GetProperty("cache_control").GetProperty("type").GetString());
        }

        // 클라이언트마다 기억이 독립이어야 테스트가 서로를 오염시키지 않고,
        // Actor/Critic처럼 서로 다른 클라이언트가 접두사를 공유하지도 않는다.
        [Fact]
        public async Task Claude_MemoryIsPerClientInstance()
        {
            var (client1, _) = NewClaudeClient();
            var (client2, spy2) = NewClaudeClient();

            await client1.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client2.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            Assert.False(UserContentOf(spy2)[0].TryGetProperty("cache_control", out _));
        }
    }
}
