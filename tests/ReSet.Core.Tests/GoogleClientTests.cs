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
        // Google 은 집계를 usage 가 아니라 usageMetadata 에 담고 이름도 전부 다르다.
        // 캐시 읽기는 cachedContentTokenCount 이고, 추론은 thoughtsTokenCount 다.
        [Fact]
        public void ReadUsage_ExtractsGoogleUsageMetadata()
        {
            using var doc = JsonDocument.Parse(@"{""usageMetadata"":{
                ""promptTokenCount"":423110,
                ""candidatesTokenCount"":1204,
                ""cachedContentTokenCount"":409600,
                ""thoughtsTokenCount"":612}}");

            var usage = GoogleClient.ReadUsage(doc.RootElement);

            Assert.Equal(423110, usage.Input);
            Assert.Equal(1204, usage.Output);
            Assert.Equal(409600, usage.CacheRead);
            Assert.Equal(612, usage.Thinking);
        }

        // usageMetadata 에서 캐시 쓰기에 해당하는 항목은 아직 관측되지 않았다
        // (명시적 캐시는 별개 API인 cachedContents 로 만든다). 이름을 모르는 칸을
        // 추측해 읽지는 않으므로 미보고로 둔다 - 0 은 "재보니 안 썼다"는 다른 말이다.
        // 실물 봉투에서 이름이 확인되면 chat/completions 처럼 읽어 채운다.
        [Fact]
        public void ReadUsage_MarksCacheWriteUnreported()
        {
            using var doc = JsonDocument.Parse(@"{""usageMetadata"":{""promptTokenCount"":10}}");

            var usage = GoogleClient.ReadUsage(doc.RootElement);

            Assert.Null(usage.CacheWrite);
        }

        [Fact]
        public void ReadUsage_WithoutUsageMetadata_ReportsNothing()
        {
            using var doc = JsonDocument.Parse(@"{""candidates"":[]}");

            var usage = GoogleClient.ReadUsage(doc.RootElement);

            Assert.Null(usage.Input);
            Assert.Null(usage.Output);
            Assert.Null(usage.CacheRead);
            Assert.Null(usage.Thinking);
        }

        [Fact]
        public void ReadUsage_WithMalformedMetadata_ReportsNothingWithoutThrowing()
        {
            using var doc = JsonDocument.Parse(@"{""usageMetadata"":{""promptTokenCount"":null}}");

            var usage = GoogleClient.ReadUsage(doc.RootElement);

            Assert.Null(usage.Input);
        }

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

        [Fact]
        public async Task ChatAsync_WithMissingCandidates_ShouldThrowInvalidOperationException()
        {
            var responseJson = @"{ ""some"": ""data"" }";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test", "https://generativelanguage.googleapis.com", "gemini-1.5-flash");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("생성된 후보군", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithPromptFeedbackBlock_ShouldThrowInvalidOperationException()
        {
            var responseJson = @"{ ""promptFeedback"": { ""blockReason"": ""SAFETY"" } }";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test", "https://generativelanguage.googleapis.com", "gemini-1.5-flash");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("안전 필터", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException()
        {
            var responseJson = "Bad request";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson, System.Net.HttpStatusCode.BadRequest);
            var httpClient = new HttpClient(spyHandler);
            var client = new GoogleClient(httpClient, "test", "https://generativelanguage.googleapis.com", "gemini-1.5-flash");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Bad request", ex.Message);
        }
    }

    public class RequestSpyHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly System.Net.HttpStatusCode _statusCode;
        public string? LastRequestContent { get; private set; }

        public RequestSpyHttpMessageHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return response;
        }
    }
}
