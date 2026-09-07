using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// API 클라이언트 여섯이 봉투에서 읽은 토큰 집계를 실제로 로그까지 내보내는지 본다.
    ///
    /// 파서 단위 테스트는 "JSON에서 숫자를 뽑았다"까지만 증명한다. 뽑아 놓고 로그로
    /// 내보내지 않으면 관측할 수 없다는 문제는 그대로 남는데, 캐시 미스는 오류를 내지
    /// 않으므로 그 침묵은 영원히 드러나지 않는다. 실제로 OpenRouter 경유 hy4 의 캐시
    /// 히트율을 재려고 생성 기록 API 를 사후에 뒤져야 했던 것이 이 줄이 없어서였다.
    /// 그래서 ChatAsync 를 스텁 HTTP 로 끝까지 돌려 로그 줄을 직접 확인한다.
    /// CLI 쪽의 같은 이유가 <see cref="CliUsageLoggingTests"/>에 있다.
    ///
    /// 전역 Log.Logger를 갈아 끼우므로 반드시 이 컬렉션에 있어야 한다.
    /// </summary>
    [Collection(GlobalSerilogLoggerCollection.Name)]
    public class ApiUsageLoggingTests
    {
        [Fact]
        public async Task Claude_LogsInputOutputAndCacheCounters()
        {
            const string json = @"{""content"":[{""type"":""text"",""text"":""PONG""}],
                ""usage"":{""input_tokens"":357560,""output_tokens"":1204,
                           ""cache_creation_input_tokens"":1818,
                           ""cache_read_input_tokens"":409600}}";

            var client = new ClaudeClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://api.anthropic.com", "claude-sonnet-5");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("Claude", line);
            Assert.Contains("357560", line);
            Assert.Contains("1204", line);
            Assert.Contains("1818", line);
            Assert.Contains("409600", line);
        }

        // OpenRouter 는 hy4 를 부르는 경로다. 이 줄이 곧 캐시 판독의 원자료가 된다.
        [Fact]
        public async Task OpenRouter_LogsCachedTokensFromPromptTokensDetails()
        {
            const string json = @"{""choices"":[{""message"":{""content"":""PONG""}}],
                ""usage"":{""prompt_tokens"":217945,""completion_tokens"":1873,
                           ""prompt_tokens_details"":{""cached_tokens"":204800}}}";

            var client = new OpenRouterClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://openrouter.ai/api/v1", "tencent/hy4-preview");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("OpenRouter", line);
            Assert.Contains("217945", line);
            Assert.Contains("204800", line);
        }

        // 이 규격에는 캐시 쓰기 칸이 없다. 0 이 아니라 "미보고"로 찍혀야, 캐시를 안 쓴
        // 것과 봉투가 말하지 않은 것을 로그만 보고 구별할 수 있다.
        [Fact]
        public async Task OpenRouter_MarksCacheWriteUnreported()
        {
            const string json = @"{""choices"":[{""message"":{""content"":""PONG""}}],
                ""usage"":{""prompt_tokens"":100,""completion_tokens"":10}}";

            var client = new OpenRouterClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://openrouter.ai/api/v1", "tencent/hy4-preview");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            // Serilog는 문자열 스칼라를 따옴표로 감싸 렌더링한다.
            Assert.Contains($"캐시 쓰기: \"{TokenUsage.NotReported}\"", line);
        }

        [Fact]
        public async Task Zai_LogsPromptAndCompletionTokens()
        {
            const string json = @"{""choices"":[{""message"":{""content"":""PONG""}}],
                ""usage"":{""prompt_tokens"":8142,""completion_tokens"":517}}";

            var client = new ZaiClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://api.z.ai/api", "glm-5.3");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("Z.ai", line);
            Assert.Contains("8142", line);
            Assert.Contains("517", line);
        }

        // OpenAI 클라이언트는 경로가 둘이고 봉투 이름이 다르다. gpt-5 계열은 Responses,
        // 그 밖은 채팅 규격으로 간다 - 둘 다 찍혀야 한다.
        [Fact]
        public async Task OpenAi_ResponsesPath_LogsCachedAndReasoningTokens()
        {
            const string json = @"{""output"":[{""type"":""message"",""content"":[
                    {""type"":""output_text"",""text"":""PONG""}]}],
                ""usage"":{""input_tokens"":36000,
                           ""input_tokens_details"":{""cached_tokens"":32768},
                           ""output_tokens"":870,
                           ""output_tokens_details"":{""reasoning_tokens"":612}}}";

            var client = new OpenAiClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://api.openai.com/v1", "gpt-5.6");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f, effort: "medium"));

            Assert.Contains("36000", line);
            Assert.Contains("32768", line);
            Assert.Contains("612", line);
        }

        [Fact]
        public async Task OpenAi_ChatCompletionsPath_LogsPromptTokens()
        {
            const string json = @"{""choices"":[{""message"":{""content"":""PONG""}}],
                ""usage"":{""prompt_tokens"":4250,""completion_tokens"":310,
                           ""prompt_tokens_details"":{""cached_tokens"":4096}}}";

            var client = new OpenAiClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://api.openai.com/v1", "gpt-4o");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("4250", line);
            Assert.Contains("4096", line);
        }

        [Fact]
        public async Task Google_LogsUsageMetadataCounters()
        {
            const string json = @"{""candidates"":[{""content"":{""parts"":[{""text"":""PONG""}]}}],
                ""usageMetadata"":{""promptTokenCount"":423110,""candidatesTokenCount"":1204,
                                   ""cachedContentTokenCount"":409600,""thoughtsTokenCount"":612}}";

            var client = new GoogleClient(
                new HttpClient(new MockHttpMessageHandler(json)), "k", "https://generativelanguage.googleapis.com", "gemini-3-pro");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("Google", line);
            Assert.Contains("423110", line);
            Assert.Contains("409600", line);
            Assert.Contains("612", line);
        }

        [Fact]
        public async Task Ollama_LogsRootLevelEvalCounts()
        {
            const string json = @"{""message"":{""content"":""PONG""},
                ""prompt_eval_count"":8142,""eval_count"":517}";

            var client = new OllamaClient(
                new HttpClient(new MockHttpMessageHandler(json)), "http://127.0.0.1:11434/api/chat", "gemma3");

            var line = await CaptureUsageLineAsync(() => client.ChatAsync("시스템", "사용자", 0.2f));

            Assert.Contains("Ollama", line);
            Assert.Contains("8142", line);
            Assert.Contains("517", line);
            // 캐시 항목이 없는 봉투다. 0 으로 찍히면 로컬 모델을 두고 캐시를 논하게 된다.
            Assert.Contains($"캐시 읽기: \"{TokenUsage.NotReported}\"", line);
        }

        private static async Task<string> CaptureUsageLineAsync(Func<Task> action)
        {
            var messages = await CaptureAsync(action);
            return Assert.Single(messages, m => m.Contains("토큰 사용량"));
        }

        private static async Task<List<string>> CaptureAsync(Func<Task> action)
        {
            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            try
            {
                await action();
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            return sink.Messages;
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }
    }
}
