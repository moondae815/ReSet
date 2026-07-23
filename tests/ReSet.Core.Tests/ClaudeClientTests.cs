using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class ClaudeClientTests
    {
        [Fact]
        public async Task ChatAsync_WithClaude35_ShouldIncludeMaxTokens8192AndTemperature()
        {
            // Arrange
            var spyHandler = new ClaudeRequestSpyHandler("{\"content\":[{\"type\":\"text\",\"text\":\"Claude response\"}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test_api_key", "https://api.anthropic.com", "claude-3-5-sonnet");

            // Act
            var result = await client.ChatAsync("System prompt", "User prompt", 0.7f);

            // Assert
            Assert.Equal("Claude response", result.Content);
            Assert.Null(result.ThinkingText);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("max_tokens", out var maxTokens));
                Assert.Equal(8192, maxTokens.GetInt32());
                Assert.True(root.TryGetProperty("temperature", out var temp));
                Assert.Equal(0.7f, temp.GetSingle());
            }
        }

        [Fact]
        public async Task ChatAsync_WithClaude4AndThinking_ShouldIncludeAdaptiveThinkingAndOutputConfig()
        {
            // Arrange
            var spyHandler = new ClaudeRequestSpyHandler("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Some thoughts\"},{\"type\":\"text\",\"text\":\"Claude response\"}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test_api_key", "https://api.anthropic.com", "claude-4-opus-4-8");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f, effort: "high");

            // Assert
            Assert.Equal("Claude response", result.Content);
            Assert.Equal("Some thoughts", result.ThinkingText);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("thinking", out var thinking));
                Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
                Assert.True(root.TryGetProperty("output_config", out var outConfig));
                Assert.Equal("high", outConfig.GetProperty("effort").GetString());
            }
        }

        [Fact]
        public async Task ChatAsync_WithClaude37AndThinking_ShouldIncludeBudgetTokens()
        {
            // Arrange
            var spyHandler = new ClaudeRequestSpyHandler("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Claude 3.7 thoughts\"},{\"type\":\"text\",\"text\":\"Claude 3.7 response\"}]}");
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test_api_key", "https://api.anthropic.com", "claude-3-7-sonnet");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f, effort: "medium");

            // Assert
            Assert.Equal("Claude 3.7 response", result.Content);
            Assert.Equal("Claude 3.7 thoughts", result.ThinkingText);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("thinking", out var thinking));
                Assert.Equal("enabled", thinking.GetProperty("type").GetString());
                Assert.Equal(4000, thinking.GetProperty("budget_tokens").GetInt32());
            }
        }

        [Fact]
        public async Task ChatAsync_WithErrorResponse_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var spyHandler = new ClaudeRequestSpyHandler("{\"error\":{\"message\":\"Invalid API Key\"}}");
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test_api_key", "https://api.anthropic.com", "claude-3-5-sonnet");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
        }

        [Fact]
        public async Task ChatAsync_WithClaudeErrorResponse_ShouldThrowException()
        {
            var responseJson = @"{ ""type"": ""error"", ""error"": { ""message"": ""Claude error"" } }";
            var spyHandler = new ClaudeRequestSpyHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test", "https://api.anthropic.com", "claude-3-5");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Claude error", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException()
        {
            var responseJson = "Bad request";
            var spyHandler = new ClaudeRequestSpyHandler(responseJson, System.Net.HttpStatusCode.BadRequest);
            var httpClient = new HttpClient(spyHandler);
            var client = new ClaudeClient(httpClient, "test", "https://api.anthropic.com", "claude-3-5");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Bad request", ex.Message);
        }
    }

    public class ClaudeRequestSpyHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly System.Net.HttpStatusCode _statusCode;
        public string? LastRequestContent { get; private set; }

        public ClaudeRequestSpyHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
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
