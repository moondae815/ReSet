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
    }
}
