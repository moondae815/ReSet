using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class OpenAiClientTests
    {
        [Fact]
        public async Task ChatAsync_WithGpt5_ShouldUseResponsesApiAndParseOutput()
        {
            // Arrange
            var responseJson = @"{
                ""output"": [
                    {
                        ""type"": ""reasoning"",
                        ""summary"": [
                            { ""type"": ""summary_text"", ""text"": ""Gpt5 reasoning"" }
                        ]
                    },
                    {
                        ""type"": ""message"",
                        ""content"": [
                            { ""type"": ""output_text"", ""text"": ""Gpt5 response"" }
                        ]
                    }
                ]
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-5-model");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f, effort: "high");

            // Assert
            Assert.Equal("Gpt5 response", result.Content);
            Assert.Equal("Gpt5 reasoning", result.ThinkingText);
            Assert.NotNull(spyHandler.LastRequestContent);
            Assert.Contains("/responses", spyHandler.LastRequestUri ?? "");

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("reasoning", out var reasoning));
                Assert.Equal("high", reasoning.GetProperty("effort").GetString());
            }
        }

        [Fact]
        public async Task ChatAsync_WithReasoningModel_ShouldForceTemperature1AndIncludeReasoningEffort()
        {
            // Arrange
            var responseJson = @"{
                ""choices"": [
                    {
                        ""message"": {
                            ""role"": ""assistant"",
                            ""content"": ""o1 response"",
                            ""reasoning_content"": ""o1 reasoning""
                        }
                    }
                ]
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "o1-mini");

            // Act
            var result = await client.ChatAsync("System", "User", 0.5f, effort: "low");

            // Assert
            Assert.Equal("o1 response", result.Content);
            Assert.Equal("o1 reasoning", result.ThinkingText);
            Assert.NotNull(spyHandler.LastRequestContent);
            Assert.Contains("/chat/completions", spyHandler.LastRequestUri ?? "");

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("reasoning_effort", out var effortProp));
                Assert.Equal("low", effortProp.GetString());
                Assert.False(root.TryGetProperty("temperature", out _));
            }
        }

        [Fact]
        public async Task ChatAsync_WithOllamaConfig_ShouldIncludeThinkProperty()
        {
            // Arrange
            var responseJson = @"{
                ""choices"": [
                    {
                        ""message"": {
                            ""role"": ""assistant"",
                            ""content"": ""ollama response""
                        }
                    }
                ]
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "", "http://localhost:11434", "gemma4");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f, effort: "medium");

            // Assert
            Assert.Equal("ollama response", result.Content);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("think", out var thinkProp));
                Assert.Equal("medium", thinkProp.GetString());
            }
        }

        [Theory]
        [InlineData("low", 0.1f)]
        [InlineData("medium", 0.4f)]
        [InlineData("high", 0.7f)]
        [InlineData("max", 0.9f)]
        public async Task ChatAsync_WithOllamaConfig_ShouldDiversifyTemperatureBasedOnEffort(string effort, float expectedTemp)
        {
            // Arrange
            var responseJson = @"{
                ""choices"": [
                    {
                        ""message"": {
                            ""role"": ""assistant"",
                            ""content"": ""ollama response""
                        }
                    }
                ]
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "", "http://localhost:11434", "gemma4");

            // Act
            var result = await client.ChatAsync("System", "User", 0.5f, effort: effort);

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("temperature", out var tempProp));
                Assert.Equal(expectedTemp, tempProp.GetSingle());
            }
        }

        [Fact]
        public async Task ChatAsync_WithErrorResponse_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var responseJson = @"{
                ""error"": {
                    ""message"": ""Quota exceeded""
                }
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-4");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
        }
    }

    public class OpenAiRequestSpyHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        public string? LastRequestContent { get; private set; }
        public string? LastRequestUri { get; private set; }

        public OpenAiRequestSpyHandler(string responseContent)
        {
            _responseContent = responseContent;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
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
