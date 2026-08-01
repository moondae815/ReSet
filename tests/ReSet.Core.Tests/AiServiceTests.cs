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
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Consolidated_Job"));
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
        public async Task GenerateSpecificationAsync_FunctionPrompt_DoesNotRequireTransaction()
        {
            // This fails if the stored-procedure prompt is reused for a UDF.
            var functionDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "FN_Calc",
                ObjectType = CodeObjectType.Function,
                DdlText = "CREATE FUNCTION dbo.FN_Calc() RETURNS TABLE AS RETURN SELECT CAST(1 AS decimal(18,2)) AS Amount",
                FunctionReturn = new FunctionReturnInfo
                {
                    IsTableValued = true,
                    Columns = new System.Collections.Generic.List<ColumnInfo>
                    {
                        new ColumnInfo { ColumnName = "Amount", DataType = "decimal(18,2)", IsNullable = true }
                    }
                }
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 함수 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(functionDef, "rules");

            Assert.DoesNotContain("BEGIN TRAN", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ROLLBACK", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("error return code", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("return contract", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("determinism", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TVF result schema", result.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("- Amount: decimal(18,2) (nullable)", result.UserPrompt, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ReviewSpecificationAsync_FunctionPrompt_UsesTvfNullabilityWithoutProcedureInstructions()
        {
            var functionDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "FN_Calc",
                ObjectType = CodeObjectType.Function,
                DdlText = "CREATE FUNCTION dbo.FN_Calc() RETURNS TABLE AS RETURN SELECT CAST(1 AS decimal(18,2)) AS Amount",
                FunctionReturn = new FunctionReturnInfo
                {
                    IsTableValued = true,
                    Columns = new System.Collections.Generic.List<ColumnInfo>
                    {
                        new ColumnInfo { ColumnName = "Amount", DataType = "decimal(18,2)", IsNullable = true }
                    }
                }
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\":false,\\\"FeedbackComment\\\":\\\"\\\",\\\"ScoreAccuracy\\\":10,\\\"ScoreCrud\\\":10,\\\"ScoreInterface\\\":10,\\\"ScoreException\\\":10,\\\"ScoreReadability\\\":10}\"}}]}";
            var handler = new MockHttpMessageHandler(mockResponse);
            IAiService service = new AiService(new OpenAiClient(new HttpClient(handler), "test_key", "https://api.openai.com/v1", "gpt-4o"), 0.2f);

            var result = await service.ReviewSpecificationAsync(functionDef, "## 개요");

            Assert.False(result.HasDefects);
            Assert.Contains("Amount decimal(18,2) (nullable)", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN TRAN", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("return codes", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Success_ReturnsContent()
        {
            // Arrange
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1"),
                ("dbo.USP_Test2", "## 개요\n내용2")
            };
            
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            // Assert
            Assert.Equal("## 통합 배치 명세", result.Content);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints()
        {
            // Arrange
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            // Assert
            Assert.Contains("[NOLOCK Prohibition]", result.SystemPrompt);
            Assert.Contains("[INSERT-only Rollback]", result.SystemPrompt);
            Assert.Contains("[Chunk Key Validation]", result.SystemPrompt);
            Assert.Contains("[Output Parameters Interface]", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ShadowRestoreDeletesBeforeInsert()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            // 선행 DELETE 없는 옛 복원 예시가 되살아나면 실패해야 한다.
            Assert.DoesNotContain("(e.g., `INSERT INTO Target SELECT * FROM Shadow`)", result.SystemPrompt);
            Assert.Contains("DELETEs the affected range FIRST", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ForbidsGotoErrorBranching()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            Assert.Contains("GOTO", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ContainsChunkTransactionBoundaryRule()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            Assert.Contains("[Chunk Transaction Boundary]", result.SystemPrompt);
        }

        [Fact]
        public async Task ReviewConsolidatedPlanAsync_Prompt_ChecksNolockAndInsertOnlyRollback()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job");

            Assert.Contains("NOLOCK", mockHandler.LastRequestBody);
            Assert.Contains("INSERT-only", mockHandler.LastRequestBody);
        }

        [Fact]
        public async Task ReviewConsolidatedPlanAsync_Prompt_ChecksUnionAndJoinPreservation()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job");

            Assert.Contains("UNION ALL", mockHandler.LastRequestBody);
            Assert.Contains("multi-table JOINs", mockHandler.LastRequestBody);
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
            Assert.Equal(2, progressCalledCount); // 청크 개수(1개) + 초기 상태(1개)만큼 콜백 호출 확인
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_WithStaticAnalysis_ShouldMapGlobalParams()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_TestParams", 
                DdlText = "CREATE PROCEDURE dbo.USP_TestParams AS BEGIN SELECT 1; END;",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            // SqlStaticParser에 의해 추출되었다고 가정
            spDef.StaticAnalysis.ProcedureParameters.Add("@Param1 INT");
            spDef.StaticAnalysis.ProcedureParameters.Add("@Param2 VARCHAR(50) OUTPUT");

            var mockResponseContent = @"```json
{
  ""Logic"": { ""Steps"": [] }
}
```";
            var mockJson = $"{{\"message\":{{\"role\":\"assistant\",\"content\":\"{mockResponseContent.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")}\"}}}}";

            var mockHandler = new MockHttpMessageHandler(mockJson);
            var httpClient = new HttpClient(mockHandler);

            var client = new ReSet.Core.Services.Clients.OllamaClient(httpClient, "http://localhost", "model");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.DeconstructSpLogicAsync(spDef, "instructions");

            // Assert
            Assert.NotNull(result);
            Console.WriteLine("DEBUG_CONTENT: " + result.Content);
            Assert.Contains("@Param1", result.Content);
            Assert.Contains("INT", result.Content);
            Assert.Contains("@Param2", result.Content);
            Assert.Contains("VARCHAR(50)", result.Content);
            Assert.Contains("OUTPUT", result.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("USP_TestParams", result.Content);
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_WithOllama_CallsChunking_ReturnsContent()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Large", DdlText = "SELECT 1;\nSELECT 2;\nSELECT 3;" };
            
            var mockResponse = "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"Logic\\\":{}}\"}}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new ReSet.Core.Services.Clients.OllamaClient(httpClient, "http://localhost", "llama3");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.DeconstructSpLogicAsync(spDef, "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("\"Overview\":", result.Content);
        }
        [Fact]
        public async Task DeconstructSpLogicAsync_WithOllama_FeedbackLog_UsesRegenerationAndCache()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_LargeFeedback", DdlText = "SELECT 1;\nUPDATE dbo.TableA SET Col1 = 1;\nSELECT 3;" };
            
            var mockResponse = "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"Logic\\\":{}}\"}}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new ReSet.Core.Services.Clients.OllamaClient(httpClient, "http://localhost", "llama3");
            IAiService service = new AiService(client, 0.2f);

            var chunkCacheDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "output", "Procedures", "dbo.USP_LargeFeedback", "raw", "chunks");
            if (!System.IO.Directory.Exists(chunkCacheDir))
                System.IO.Directory.CreateDirectory(chunkCacheDir);
            
            // Create a fake cache for chunk 0 and 2
            System.IO.File.WriteAllText(System.IO.Path.Combine(chunkCacheDir, "chunk_0.json"), "{\"Logic\":{\"Steps\":[]}}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(chunkCacheDir, "chunk_2.json"), "{\"Logic\":{\"Steps\":[]}}");

            // Act
            // Feedback contains UPDATE keyword, which should match chunk 1 (UPDATE dbo.TableA). 
            // So chunk 0 and 2 should be loaded from cache.
            var result = await service.DeconstructSpLogicAsync(spDef, "지침", "Fix the UPDATE statement", null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("\"Overview\":", result.Content);
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_Monolithic_Success_ReturnsDeconstructedLogic()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"Overview\\\": {}}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.DeconstructSpLogicAsync(spDef, "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("\"Overview\":", result.Content);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_Success_ReturnsSectionMarkdown()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\\n테스트\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("## 개요", result.Content);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_Success()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var client = new OpenAiClient(new HttpClient(mockHandler), "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);
            

            // Act
            var result = await service.GenerateSpecSectionAsync(spDef, "CrudAnalysis", "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_Success()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 로직 흐름 요약\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var client = new OpenAiClient(new HttpClient(mockHandler), "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);
            

            // Act
            var result = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithFeedback_IncludesFeedbackInPrompt()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "SELECT 1;" };
            
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 생성된 명세서\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateSpecificationAsync(spDef, "지침", "피드백 수정내용");

            // Assert
            Assert.Equal("## 생성된 명세서", result.Content);
            Assert.Contains("피드백 수정내용", result.SystemPrompt + result.UserPrompt);
        }

        [Fact]
        public async Task ReviewConsolidatedPlanAsync_Success_ReturnsReviewResult()
        {
            // Arrange
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false, \\\"ScoreAccuracy\\\": 10}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.ReviewConsolidatedPlanAsync(specs, "## 통합 계획서", "Test_Job");

            // Assert
            Assert.False(result.HasDefects);
            Assert.Equal(10, result.ScoreAccuracy);
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_Success_ReturnsContent()
        {
            // Arrange
            var spDefs = new System.Collections.Generic.List<SpDefinition>
            {
                new SpDefinition 
                { 
                    Schema = "dbo", 
                    Name = "USP_Test", 
                    DdlText = "SELECT 1;",
                    Dependencies = new System.Collections.Generic.List<DependencyInfo>
                    {
                        new DependencyInfo 
                        { 
                            Schema = "dbo", Name = "TestTable", Type = "TABLE",
                            Columns = new System.Collections.Generic.List<ColumnInfo>
                            {
                                new ColumnInfo { ColumnName = "Id", DataType = "int", Description = "Primary Key" },
                                new ColumnInfo { ColumnName = "Status", DataType = "varchar" }
                            }
                        }
                    }
                }
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 1. 개요 및 목적\\n테스트 정책서\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateSettlementPolicyRulebookAsync(spDefs, "{\"profiling\": {}}");

            // Assert
            Assert.Equal("## 1. 개요 및 목적\n테스트 정책서", result.Content);
            Assert.Contains("TestTable", result.UserPrompt);
            Assert.Contains("Primary Key", result.UserPrompt);
            Assert.Contains("No description", result.UserPrompt);
        }
        [Fact]
        public async Task DeconstructSpLogicAsync_WithRichSpDef_CoversFormattingMethods()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_RichTest", DdlText = "SELECT 1;" };
            
            // Add rich dependencies to trigger FormatTableSchemaToMarkdown and BuildSpMetadataTexts
            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "OtherDb",
                Schema = "dbo",
                Name = "TBL_User",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "Id", DataType = "INT", IsPrimaryKey = true, Description = "User ID" },
                    new ColumnInfo { ColumnName = "Name", DataType = "VARCHAR", Description = "User Name" }
                },
                Indexes = new System.Collections.Generic.List<TableIndexInfo>
                {
                    new TableIndexInfo { IndexName = "PK_User", IsPrimaryKey = true, IsUnique = true, Columns = new System.Collections.Generic.List<string> { "Id" } }
                }
            });
            
            // Add Static Analysis results
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedTables = new System.Collections.Generic.List<string> { "dbo.TBL_User" },
                SelectTables = new System.Collections.Generic.List<string> { "dbo.TBL_User" },
                InsertTables = new System.Collections.Generic.List<string> { "dbo.TBL_Log" },
                AstInsertMappings = new System.Collections.Generic.List<AstInsertMapping>
                {
                    new AstInsertMapping { TargetTable = "dbo.TBL_Log", TargetColumns = new System.Collections.Generic.List<string> { "LogId" }, SourceQueryBlock = "SELECT 1" }
                },
                UpdateTables = new System.Collections.Generic.List<string> { "dbo.TBL_User" },
                DeleteTables = new System.Collections.Generic.List<string> { "dbo.TBL_User" },
                CreatedTempTables = new System.Collections.Generic.List<string> { "#TempLog" },
                LinkedServerReferences = new System.Collections.Generic.List<string> { "LINKED_SRV.db.dbo.tbl" },
                ReferencedFunctions = new System.Collections.Generic.List<string> { "dbo.UDF_GetDate" },
                ControlFlowSummary = new System.Collections.Generic.List<string> { "IF @@ERROR <> 0" },
                ReferencedColumnsPerTable = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "dbo.TBL_User", new System.Collections.Generic.List<string> { "Id" } }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"Overview\\\": {}}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.DeconstructSpLogicAsync(spDef, "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("\"Overview\":", result.Content);
        }
    }

    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly System.Net.HttpStatusCode _statusCode;
        public string LastRequestBody { get; private set; } = string.Empty;

        public MockHttpMessageHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return response;
        }
    }
}
