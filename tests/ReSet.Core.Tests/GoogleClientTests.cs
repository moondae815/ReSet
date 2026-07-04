using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class GoogleClientTests
    {
        [Fact]
        public async Task ChatAsync_WithNoEffort_ShouldNotIncludeThinkingConfig()
        {
            // Arrange
            var spyHandler = new RequestSpyHttpMessageHandler("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Response\"}]}}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test_api_key", "https://generativelanguage.googleapis.com", "gemini-1.5-flash");

            // Act
            var result = await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: null);

            // Assert
            Assert.Equal("Response", result.Content);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("generationConfig", out var genConfig));
                Assert.True(genConfig.TryGetProperty("temperature", out var temp));
                Assert.Equal(0.7f, temp.GetSingle());
                Assert.False(genConfig.TryGetProperty("thinkingConfig", out _));
            }
        }

        [Fact]
        public async Task ChatAsync_WithGemini25AndEffort_ShouldIncludeThinkingBudget()
        {
            // Arrange
            var spyHandler = new RequestSpyHttpMessageHandler("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Response\"}]}}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test_api_key", "https://generativelanguage.googleapis.com", "gemini-2.5-flash");

            // Act
            await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: "medium");

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("generationConfig", out var genConfig));
                Assert.False(genConfig.TryGetProperty("temperature", out _));
                Assert.True(genConfig.TryGetProperty("thinkingConfig", out var thinkingConfig));
                Assert.True(thinkingConfig.TryGetProperty("thinkingBudget", out var budget));
                Assert.Equal(4096, budget.GetInt32());
                Assert.True(thinkingConfig.TryGetProperty("includeThoughts", out var incThoughts));
                Assert.True(incThoughts.GetBoolean());
            }
        }

        [Fact]
        public async Task ChatAsync_WithGemini3AndEffort_ShouldIncludeThinkingLevel()
        {
            // Arrange
            var spyHandler = new RequestSpyHttpMessageHandler("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Response\"}]}}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test_api_key", "https://generativelanguage.googleapis.com", "gemini-3.0-flash");

            // Act
            await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: "high");

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("generationConfig", out var genConfig));
                Assert.False(genConfig.TryGetProperty("temperature", out _));
                Assert.True(genConfig.TryGetProperty("thinkingConfig", out var thinkingConfig));
                Assert.True(thinkingConfig.TryGetProperty("thinkingLevel", out var level));
                Assert.Equal("HIGH", level.GetString());
                Assert.True(thinkingConfig.TryGetProperty("includeThoughts", out var incThoughts));
                Assert.True(incThoughts.GetBoolean());
            }
        }

        [Fact]
        public async Task ChatAsync_WithUnsupportedModelAndEffort_ShouldNotIncludeThinkingConfig()
        {
            // Arrange
            var spyHandler = new RequestSpyHttpMessageHandler("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Response\"}]}}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test_api_key", "https://generativelanguage.googleapis.com", "gemini-1.5-flash");

            // Act
            await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: "high");

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("generationConfig", out var genConfig));
                Assert.True(genConfig.TryGetProperty("temperature", out var temp));
                Assert.False(genConfig.TryGetProperty("thinkingConfig", out _));
            }
        }
    }

    public class RequestSpyHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        public string? LastRequestContent { get; private set; }

        public RequestSpyHttpMessageHandler(string responseContent)
        {
            _responseContent = responseContent;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return response;
        }
    }
}
