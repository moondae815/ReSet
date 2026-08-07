using System;
using System.Net.Http;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class AiClientFactoryTests
    {
        [Fact]
        public void CreateClient_WithEmptyProvider_ShouldThrowArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AiClientFactory.CreateClient("", "model", "key", "url"));
            Assert.Throws<ArgumentException>(() => AiClientFactory.CreateClient(null!, "model", "key", "url"));
        }

        [Fact]
        public void CreateClient_WithUnsupportedProvider_ShouldThrowNotSupportedException()
        {
            Assert.Throws<NotSupportedException>(() => AiClientFactory.CreateClient("Unsupported", "model", "key", "url"));
        }

        [Theory]
        [InlineData("openai", typeof(OpenAiClient))]
        [InlineData("OpenAI", typeof(OpenAiClient))]
        [InlineData("ollama", typeof(OllamaClient))]
        [InlineData("claude", typeof(ClaudeClient))]
        [InlineData("anthropic", typeof(ClaudeClient))]
        [InlineData("google", typeof(GoogleClient))]
        [InlineData("z.ai", typeof(ZaiClient))]
        [InlineData("zai", typeof(ZaiClient))]
        public void CreateClient_WithValidProviders_ShouldReturnCorrectClientType(string provider, Type expectedType)
        {
            var client = AiClientFactory.CreateClient(provider, "model", "key", "http://localhost");
            Assert.IsType(expectedType, client);
        }

        [Fact]
        public void CreateClient_WithCustomHttpClient_ShouldUseProvidedClient()
        {
            using var customHttp = new HttpClient();
            var client = AiClientFactory.CreateClient("openai", "model", "key", "http://localhost", customHttp);
            Assert.NotNull(client);
        }

        [Theory]
        [InlineData("claude-cli", typeof(ReSet.Core.Services.Clients.Cli.ClaudeCliClient))]
        [InlineData("Claude-CLI", typeof(ReSet.Core.Services.Clients.Cli.ClaudeCliClient))]
        [InlineData("codex-cli", typeof(ReSet.Core.Services.Clients.Cli.CodexCliClient))]
        [InlineData("agy-cli", typeof(ReSet.Core.Services.Clients.Cli.AntigravityCliClient))]
        public void CreateClient_WithCliProviders_ShouldReturnCorrectClientType(string provider, Type expectedType)
        {
            var client = AiClientFactory.CreateClient(provider, "model", "", "");
            Assert.IsType(expectedType, client);
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void IsCliProvider_WithCliProviders_ReturnsTrue(string provider)
        {
            Assert.True(AiClientFactory.IsCliProvider(provider));
        }

        [Theory]
        [InlineData("claude")]
        [InlineData("openai")]
        [InlineData("ollama")]
        [InlineData("")]
        [InlineData(null)]
        public void IsCliProvider_WithNonCliProviders_ReturnsFalse(string? provider)
        {
            Assert.False(AiClientFactory.IsCliProvider(provider!));
        }

        // CLI provider는 로컬 LLM 분할 파이프라인의 대상이 아니다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void IsLocalProvider_WithCliProviders_ReturnsFalse(string provider)
        {
            Assert.False(AiClientFactory.IsLocalProvider(provider));
        }

        // 타임아웃은 새 매개변수가 아니라 이미 넘어오는 HttpClient에서 읽는다.
        // 설정이 한 곳에서만 관리되고 API 경로와 값이 어긋나지 않는다.
        [Fact]
        public void CreateClient_CliProvider_UsesHttpClientTimeout()
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1234) };
            var client = AiClientFactory.CreateClient("claude-cli", "sonnet", "", "", httpClient);

            var cliClient = Assert.IsType<ReSet.Core.Services.Clients.Cli.ClaudeCliClient>(client);
            Assert.Equal(1234, cliClient.Timeout.TotalSeconds);
        }

        // HttpClient를 주지 않으면 팩토리 기본값 300초를 쓴다.
        [Fact]
        public void CreateClient_CliProvider_WithoutHttpClient_FallsBackToDefaultTimeout()
        {
            var client = AiClientFactory.CreateClient("codex-cli", "gpt-5.6-terra", "", "");

            var cliClient = Assert.IsType<ReSet.Core.Services.Clients.Cli.CodexCliClient>(client);
            Assert.Equal(300, cliClient.Timeout.TotalSeconds);
        }

        [Fact]
        public void CreateClient_CliProvider_WithoutApiKey_DoesNotThrow()
        {
            // CLI provider는 API 키를 갖지 않는다.
            var client = AiClientFactory.CreateClient("claude-cli", "sonnet", "", "");
            Assert.NotNull(client);
        }

        [Fact]
        public void CreateClient_CliProvider_CustomCommandIsAccepted()
        {
            var client = AiClientFactory.CreateClient(
                "claude-cli", "sonnet", "", "", null, null, "/opt/homebrew/bin/claude");

            Assert.IsType<ReSet.Core.Services.Clients.Cli.ClaudeCliClient>(client);
        }

        // 단일 GPU 공유가 실제로 문제인 provider들 — StepConcurrency 경고 대상.
        [Theory]
        [InlineData("ollama")]
        [InlineData("Ollama")]
        [InlineData("local-openai")]
        [InlineData("mlx")]
        public void IsSingleGpuLocalProvider_WithSingleGpuProviders_ReturnsTrue(string provider)
        {
            Assert.True(AiClientFactory.IsSingleGpuLocalProvider(provider));
        }

        // vLLM은 IsLocalProvider에는 걸리지만(청킹 파이프라인 라우팅), 연속 배칭
        // (continuous batching) 덕에 동시 실행이 유리한 백엔드라 "동시성을 낮추라"는
        // 조언의 대상이 아니다 — IsSingleGpuLocalProvider는 vLLM을 제외해야 한다.
        [Theory]
        [InlineData("vllm")]
        [InlineData("claude")]
        [InlineData("openai")]
        [InlineData("")]
        [InlineData(null)]
        public void IsSingleGpuLocalProvider_WithVllmOrNonLocalProviders_ReturnsFalse(string? provider)
        {
            Assert.False(AiClientFactory.IsSingleGpuLocalProvider(provider!));
        }

        [Fact]
        public void IsLocalProvider_WithVllm_StillReturnsTrue()
        {
            // IsLocalProvider는 청킹 파이프라인 라우팅에 쓰이며 vLLM도 그 대상이다.
            // IsSingleGpuLocalProvider 도입이 이 계약을 건드리지 않았음을 고정한다.
            Assert.True(AiClientFactory.IsLocalProvider("vllm"));
        }
    }
}
