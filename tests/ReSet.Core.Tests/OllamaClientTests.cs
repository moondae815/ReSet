using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class OllamaClientTests
    {
        // Ollama 는 집계를 usage 객체로 감싸지 않고 응답 루트에 그대로 놓는다.
        [Fact]
        public void ReadUsage_ExtractsRootLevelEvalCounts()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                @"{""message"":{""content"":""ok""},""prompt_eval_count"":8142,""eval_count"":517}");

            var usage = OllamaClient.ReadUsage(doc.RootElement);

            Assert.Equal(8142, usage.Input);
            Assert.Equal(517, usage.Output);
        }

        // Ollama 봉투에는 캐시 항목이 없다. 0 으로 적으면 "재보니 캐시가 0 이었다"는
        // 측정값이 되어, 로컬 모델을 두고 캐시 거동을 논하게 만든다.
        [Fact]
        public void ReadUsage_MarksCacheCountersUnreported()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(@"{""prompt_eval_count"":8142}");

            var usage = OllamaClient.ReadUsage(doc.RootElement);

            Assert.Null(usage.CacheRead);
            Assert.Null(usage.CacheWrite);
        }

        [Fact]
        public void ReadUsage_WithoutCounts_ReportsNothing()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(@"{""message"":{""content"":""ok""}}");

            var usage = OllamaClient.ReadUsage(doc.RootElement);

            Assert.Null(usage.Input);
            Assert.Null(usage.Output);
        }

        [Fact]
        public async Task ChatAsync_WithGemma4ChannelThought_ShouldExtractThinkingAndCleanContent()
        {
            // Arrange
            var responseJson = @"{
                ""message"": {
                    ""role"": ""assistant"",
                    ""content"": ""Before thought\n<|channel>thought\nThis is Gemma 4 thinking\n<channel|>\nAfter thought""
                }
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson); // OpenAiClientTests.cs에 선언된 Handler 재사용
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "http://localhost:11434", "gemma4");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f);

            // Assert
            Assert.Equal("Before thought\n\nAfter thought", result.Content);
            Assert.Equal("This is Gemma 4 thinking", result.ThinkingText);
        }

        [Fact]
        public async Task ChatAsync_WithStandardThinkTag_ShouldExtractThinkingAndCleanContent()
        {
            // Arrange
            var responseJson = @"{
                ""message"": {
                    ""role"": ""assistant"",
                    ""content"": ""<think>Standard think process</think>Actual response content""
                }
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "http://localhost:11434", "deepseek-r1");

            // Act
            var result = await client.ChatAsync("System", "User", 0.7f);

            // Assert
            Assert.Equal("Actual response content", result.Content);
            Assert.Equal("Standard think process", result.ThinkingText);
        }

        [Theory]
        [InlineData("low", 0.1f)]
        [InlineData("medium", 0.4f)]
        [InlineData("high", 0.7f)]
        [InlineData("max", 0.9f)]
        public async Task ChatAsync_ShouldDiversifyTemperatureBasedOnEffort(string effort, float expectedTemp)
        {
            // Arrange
            var responseJson = @"{
                ""message"": {
                    ""role"": ""assistant"",
                    ""content"": ""ollama response""
                }
            }";
            var spyHandler = new OpenAiRequestSpyHandler(responseJson);
            var httpClient = new HttpClient(spyHandler);
            var client = new OllamaClient(httpClient, "http://localhost:11434", "llama3");

            // Act
            var result = await client.ChatAsync("System", "User", 0.5f, effort: effort);

            // Assert
            Assert.NotNull(spyHandler.LastRequestContent);

            using (var doc = System.Text.Json.JsonDocument.Parse(spyHandler.LastRequestContent))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("options", out var optionsProp));
                Assert.True(optionsProp.TryGetProperty("temperature", out var tempProp));
                Assert.Equal(expectedTemp, tempProp.GetSingle());
            }
        }
    }
}
