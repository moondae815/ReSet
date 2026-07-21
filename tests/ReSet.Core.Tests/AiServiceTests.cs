using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class AiServiceTests
    {
        [Fact]
        public async Task GenerateSpecificationAsync_WithEmptyApiKeyForOpenAi_ShouldThrowException()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var instructions = "규칙1: 상세하게 쓸 것.";
            
            var client = new OpenAiClient(new HttpClient(), "", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateSpecificationAsync(spDef, instructions));
        }

        [Fact]
        public async Task ReviewSpecificationAsync_WithEmptyApiKeyForOpenAi_ShouldThrowException()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var specMarkdown = "## 개요\n내용";

            var client = new OpenAiClient(new HttpClient(), "", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewSpecificationAsync(spDef, specMarkdown));
        }

        [Fact]
        public async Task GenerateBatchMigrationPlanAsync_WithEmptyApiKeyForOpenAi_ShouldThrowException()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            
            var client = new OpenAiClient(new HttpClient(), "", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateBatchMigrationPlanAsync(spDef, "C#"));
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_WithEmptyApiKeyForOpenAi_ShouldThrowException()
        {
            // Arrange
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1"),
                ("dbo.USP_Test2", "## 개요\n내용2")
            };
            
            var client = new OpenAiClient(new HttpClient(), "", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateConsolidatedBatchPlanAsync(specs, "C#", "Test_Consolidated_Job"));
        }

        [Fact]
        public async Task GenerateSpecificationAsync_Success_ReturnsContent()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_Test", 
                DdlText = "SELECT 1;" 
            };
            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TBL_User",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "Id", DataType = "INT", IsPrimaryKey = true }
                }
            });

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 생성된 명세서\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateSpecificationAsync(spDef, "지침");

            // Assert
            Assert.Equal("## 생성된 명세서", result.Content);
        }

        [Fact]
        public async Task ReviewSpecificationAsync_Success_ReturnsReviewResult()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": true, \\\"FeedbackComment\\\": \\\"결함 발견\\\"}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.ReviewSpecificationAsync(spDef, "## 개요");

            // Assert
            Assert.True(result.HasDefects);
            Assert.Equal("결함 발견", result.FeedbackComment);
        }

        [Fact]
        public async Task ReviewSpecificationAsync_JsonException_ReturnsDefectsTrue()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"Invalid JSON Content\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.ReviewSpecificationAsync(spDef, "## 개요");

            // Assert
            Assert.True(result.HasDefects);
            Assert.Contains("JSON 검토 보고서 파싱 실패", result.FeedbackComment);
        }

        [Fact]
        public async Task ReviewSpecificationAsync_WithMarkdownJsonBlock_ReturnsReviewResult()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\n  \\\"HasDefects\\\": false,\\n  \\\"FeedbackComment\\\": \\\"\\\"\\n}\\n```\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.ReviewSpecificationAsync(spDef, "## 개요");

            // Assert
            Assert.False(result.HasDefects);
            Assert.Equal("", result.FeedbackComment);
        }

        [Fact]
        public async Task ReviewSpecificationAsync_WithSurroundingText_ReturnsReviewResult()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"Here is the JSON report:\\n{\\n  \\\"HasDefects\\\": true,\\n  \\\"FeedbackComment\\\": \\\"마크다운 오류\\\"\\n}\\nHope this helps!\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.ReviewSpecificationAsync(spDef, "## 개요");

            // Assert
            Assert.True(result.HasDefects);
            Assert.Equal("마크다운 오류", result.FeedbackComment);
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_WithOllamaMarkdownWrappedChunk_ShouldUnwrapAndConsolidate()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_Test", 
                DdlText = "UPDATE dbo.TableA SET Col1 = 1;" 
            };

            var mockResponseContent = @"```json
{
  ""Logic"": {
    ""Steps"": [
      { ""StepNumber"": 1, ""StepDescription"": ""Update TableA"" }
    ]
  }
}
```";
            var ollamaJson = $"{{\"message\":{{\"role\":\"assistant\",\"content\":\"{mockResponseContent.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")}\"}}}}";

            var mockHandler = new MockHttpMessageHandler(ollamaJson);
            var httpClient = new HttpClient(mockHandler);

            var client = new ReSet.Core.Services.Clients.OllamaClient(httpClient, "http://localhost:11434", "llama3");
            IAiService service = new AiService(client, 0.2f);

            // Act
            int progressCalledCount = 0;
            var result = await service.DeconstructSpLogicAsync(spDef, "instructions", null, null, default, info => progressCalledCount++);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("Update TableA", result.Content); // 병합된 결과에 내용이 잘 들어갔는지 확인
            Assert.Equal(1, progressCalledCount); // 청크 개수(1개)만큼 콜백 호출 확인
        }
    }

    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly System.Net.HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
