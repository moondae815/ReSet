using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class ZaiClientTests
    {
        [Fact]
        public async Task ChatAsync_WithNoEffort_ShouldIncludeTemperatureAndNotThinking()
        {
            // Arrange
            var responseJson = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Test Response\"},\"finish_reason\":\"stop\"}]}";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new ZaiClient(httpClient, "test_api_key", "https://api.z.ai/api", "glm-4");

            // Act
            var result = await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: null);

            // Assert
            Assert.Equal("Test Response", result.Content);
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("temperature", out var temp));
                Assert.Equal(0.7f, temp.GetSingle());
                Assert.False(root.TryGetProperty("reasoning_effort", out _));
                Assert.False(root.TryGetProperty("thinking", out _));
            }
        }

        [Theory]
        [InlineData("low", "minimal")]
        [InlineData("medium", "high")]
        [InlineData("high", "max")]
        public async Task ChatAsync_WithEffort_ShouldMapCorrectlyAndNotIncludeTemperature(string inputEffort, string expectedApiEffort)
        {
            // Arrange
            var responseJson = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Test Response\"},\"finish_reason\":\"stop\"}]}";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new ZaiClient(httpClient, "test_api_key", "https://api.z.ai/api", "glm-5.2");

            // Act
            await client.ChatAsync("System prompt", "User prompt", 0.7f, effort: inputEffort);

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.False(root.TryGetProperty("temperature", out _));
                
                Assert.True(root.TryGetProperty("reasoning_effort", out var effortProp));
                Assert.Equal(expectedApiEffort, effortProp.GetString());

                Assert.True(root.TryGetProperty("thinking", out var thinkingProp));
                Assert.True(thinkingProp.TryGetProperty("type", out var typeProp));
                Assert.Equal("enabled", typeProp.GetString());
            }
        }

        [Fact]
        public async Task ChatAsync_WithErrorResponse_ShouldThrowInvalidOperationException()
        {
            var responseJson = @"{ ""error"": { ""message"": ""Zai error"" } }";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson, System.Net.HttpStatusCode.OK);
            var httpClient = new HttpClient(spyHandler);
            var client = new ZaiClient(httpClient, "test", "https://api.z.ai/api", "glm-4");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Zai error", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException()
        {
            var responseJson = "Bad request";
            var spyHandler = new RequestSpyHttpMessageHandler(responseJson, System.Net.HttpStatusCode.BadRequest);
            var httpClient = new HttpClient(spyHandler);
            var client = new ZaiClient(httpClient, "test", "https://api.z.ai/api", "glm-4");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));
            Assert.Contains("Bad request", ex.Message);
        }
    }
}
