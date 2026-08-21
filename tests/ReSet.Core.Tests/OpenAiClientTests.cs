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
        public async Task ChatAsync_WithGpt5MixedReasoningSummaries_ShouldPreserveNonEmptyReasoningText()
        {
            // Arrange: 실제 Responses API는 summary가 비어 있는 reasoning 항목을 함께 반환할 수 있다.
            var responseJson = @"{
                ""output"": [
                    {
                        ""type"": ""reasoning"",
                        ""summary"": [
                            { ""type"": ""summary_text"", ""text"": ""첫 번째 추론"" }
                        ]
                    },
                    {
                        ""type"": ""reasoning"",
                        ""summary"": []
                    },
                    {
                        ""type"": ""reasoning"",
                        ""summary"": [
                            { ""type"": ""summary_text"", ""text"": ""두 번째 추론"" }
                        ]
                    },
                    {
                        ""type"": ""reasoning"",
                        ""summary"": []
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
            Assert.Equal("첫 번째 추론두 번째 추론", result.ThinkingText);
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

        [Fact]
        public async Task ChatAsync_WithGpt5ErrorResponse_ShouldThrowException()
        {
            var responseJson = @"{ ""error"": { ""message"": ""Gpt5 error"" } }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-5");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Gpt5 error", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithRegularModelErrorResponse_ShouldThrowException()
        {
            var responseJson = @"{ ""error"": { ""message"": ""Regular error"" } }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-4o");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Regular error", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException()
        {
            var responseJson = "Bad request";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson, System.Net.HttpStatusCode.BadRequest);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-4o");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Bad request", ex.Message);
        }

        /// <summary>
        /// 상태 코드가 메시지 문자열 안에만 있으면 재시도 판정이 산문 매칭이 된다.
        /// AiRetryPolicy가 429와 401을 가르는 근거가 이 속성이다.
        /// </summary>
        [Theory]
        [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
        [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
        [InlineData(System.Net.HttpStatusCode.Unauthorized)]
        public async Task ChatAsync_ErrorResponse_PreservesStatusCodeOnException(
            System.Net.HttpStatusCode statusCode)
        {
            var spyHandler = new OpenAiRequestSpyHandler("error body", statusCode);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-4o");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));

            Assert.Equal(statusCode, ex.StatusCode);
        }
    }

    public class OpenAiRequestSpyHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly System.Net.HttpStatusCode _statusCode;
        public string? LastRequestContent { get; private set; }
        public string? LastRequestUri { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }

        public OpenAiRequestSpyHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestHeaders = request.Headers;
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
