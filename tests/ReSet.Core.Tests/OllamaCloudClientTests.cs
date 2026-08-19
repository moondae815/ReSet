using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Ollama Cloud(https://ollama.com)는 로컬 Ollama와 같은 네이티브 /api/chat 프로토콜을
    /// 쓰지만, Bearer 토큰 인증이 붙고 GPU를 우리가 쥐고 있지 않다. 그 두 가지 차이만
    /// 고정한다 — 나머지 페이로드 계약은 OllamaClientTests가 이미 지킨다.
    /// </summary>
    public class OllamaCloudClientTests
    {
        private const string OkResponse = @"{
            ""message"": { ""role"": ""assistant"", ""content"": ""cloud response"" }
        }";

        [Fact]
        public async Task ChatAsync_Cloud_SendsBearerAuthorizationHeader()
        {
            var spyHandler = new OpenAiRequestSpyHandler(OkResponse);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "https://ollama.com", "gpt-oss:120b", null, "sk-ollama-test", isCloud: true);

            var result = await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("cloud response", result.Content);
            Assert.NotNull(spyHandler.LastRequestHeaders);
            Assert.NotNull(spyHandler.LastRequestHeaders!.Authorization);
            Assert.Equal("Bearer", spyHandler.LastRequestHeaders.Authorization!.Scheme);
            Assert.Equal("sk-ollama-test", spyHandler.LastRequestHeaders.Authorization.Parameter);
        }

        // API 키는 요청마다 붙어야 한다. HttpClient는 provider들이 공유하므로
        // DefaultRequestHeaders에 심으면 Claude/OpenAI 요청에까지 Ollama 키가 샌다.
        [Fact]
        public async Task ChatAsync_Local_DoesNotSendAuthorizationHeader()
        {
            var spyHandler = new OpenAiRequestSpyHandler(OkResponse);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "http://localhost:11434", "llama3");

            await client.ChatAsync("System", "User", 0.2f);

            Assert.NotNull(spyHandler.LastRequestHeaders);
            Assert.Null(spyHandler.LastRequestHeaders!.Authorization);
        }

        [Fact]
        public async Task ChatAsync_Cloud_WithBlankEndpoint_FallsBackToOllamaComApiChat()
        {
            var spyHandler = new OpenAiRequestSpyHandler(OkResponse);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "", "gpt-oss:120b", null, "sk-ollama-test", isCloud: true);

            await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("https://ollama.com/api/chat", spyHandler.LastRequestUri);
        }

        // 로컬의 기본 엔드포인트는 그대로여야 한다 — 클라우드 도입이 기존 동작을
        // 바꾸지 않았음을 고정한다.
        [Fact]
        public async Task ChatAsync_Local_WithBlankEndpoint_StillFallsBackToLocalhost()
        {
            var spyHandler = new OpenAiRequestSpyHandler(OkResponse);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "", "llama3");

            await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("http://localhost:11434/api/chat", spyHandler.LastRequestUri);
        }

        [Fact]
        public async Task ChatAsync_Cloud_WithHostOnlyEndpoint_AppendsApiChatPath()
        {
            var spyHandler = new OpenAiRequestSpyHandler(OkResponse);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "https://ollama.com/", "deepseek-v3.1:671b", null, "sk-ollama-test", isCloud: true);

            await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("https://ollama.com/api/chat", spyHandler.LastRequestUri);
        }

        // ProviderName은 로그 표기일 뿐 아니라 IsLocalProvider의 입력이기도 하다.
        // 클라우드가 "Ollama"로 보이면 AST 분할 파이프라인과 온도 0.05 고정,
        // <think> 유도 프롬프트가 원격 모델에 잘못 걸린다.
        [Fact]
        public void ProviderName_DistinguishesCloudFromLocal()
        {
            using var httpClient = new HttpClient();
            var local = new OllamaClient(httpClient, "http://localhost:11434", "llama3");
            var cloud = new OllamaClient(httpClient, "https://ollama.com", "gpt-oss:120b", null, "sk-ollama-test", isCloud: true);

            Assert.Equal("Ollama", local.ProviderName);
            Assert.Equal("Ollama Cloud", cloud.ProviderName);
            Assert.True(AiClientFactory.IsLocalProvider(local.ProviderName));
            Assert.False(AiClientFactory.IsLocalProvider(cloud.ProviderName));
            Assert.False(AiClientFactory.IsSingleGpuLocalProvider(cloud.ProviderName));
        }

        // 클라우드는 키가 구조적으로 필수다. 키 없이 만들면 401 응답을 볼 때까지
        // 원인을 알 수 없으므로 생성 시점에 발급 위치를 알려주며 끊는다.
        [Fact]
        public void Constructor_Cloud_WithoutApiKey_Throws()
        {
            using var httpClient = new HttpClient();

            var ex = Assert.Throws<ArgumentException>(
                () => new OllamaClient(httpClient, "https://ollama.com", "gpt-oss:120b", null, "  ", isCloud: true));

            Assert.Contains("ollama.com/settings/keys", ex.Message);
        }
    }
}
