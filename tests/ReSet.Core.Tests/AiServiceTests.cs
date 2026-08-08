using System;
using System.Collections.Generic;
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

        // 재수립 모드가 아니면 프롬프트가 지금과 똑같아야 한다.
        // 1회차 목차 설계는 이번 변경의 영향을 받지 않는다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_WithoutPreviousStructure_HasNoRedraftInstruction()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job");

            Assert.DoesNotContain("[Redraft]", result.SystemPrompt);
            // 4개 필수 H2 강제는 두 모드 모두에서 유지된다.
            Assert.Contains("통합 배치 아키텍처 개요", result.SystemPrompt);
        }

        // 재수립 모드에서는 이전 구조를 반복하지 말라는 지시와,
        // 그 판단 근거인 누적 피드백이 프롬프트에 실려야 한다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_WithPreviousStructure_CarriesRedraftInstructionAndFeedback()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job",
                effort: null,
                previousStructure: "## 낡은 목차",
                redraftFeedback: "청킹 불가 스텝이 청킹으로 배치됨");

            Assert.Contains("[Redraft]", result.SystemPrompt);
            Assert.Contains("## 낡은 목차", result.UserPrompt);
            Assert.Contains("청킹 불가 스텝이 청킹으로 배치됨", result.UserPrompt);
            Assert.Contains("통합 배치 아키텍처 개요", result.SystemPrompt);
        }

        // 목차가 단계 목록을 구조화해 내지 않으면 분할 생성이 시작조차 못 한다.
        // 헤딩 파싱은 대안이 아니다 — 실측한 두 목차가 단계를 각각 H3/H4에 뒀고,
        // 한쪽은 `### P20~P23.`으로 4개 단계를 헤딩 하나에 묶었다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_AlwaysRequestsStructuredStepList()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job");

            Assert.Contains("```json", result.SystemPrompt);
            Assert.Contains("\"Steps\"", result.SystemPrompt);
            Assert.Contains("TargetTables", result.SystemPrompt);
            Assert.Contains("ErrorCodes", result.SystemPrompt);

            // 부분 문자열 일치만으로는 이스케이프 오류(예: `""""`가 `""`를 대신하는 경우)를
            // 잡지 못한다. 실제 파서에 태워 유효한 JSON과 올바른 키를 검증한다.
            var steps = BatchStepPlanParser.TryParse(result.SystemPrompt);
            Assert.NotNull(steps);
            Assert.Contains(steps!, s => s.Code == "S01" && !string.IsNullOrWhiteSpace(s.Name));
        }

        // 재수립 모드에서도 유지돼야 한다. 여기서 빠지면 재수립 이후 회차가
        // 조용히 폴백해 분할이 사라진다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_RedraftAlsoRequestsStructuredStepList()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job",
                effort: null,
                previousStructure: "## 낡은 목차",
                redraftFeedback: "스텝 누락");

            Assert.Contains("\"Steps\"", result.SystemPrompt);
            Assert.Contains("[Redraft]", result.SystemPrompt);

            // 재수립 모드도 같은 계약으로 고정한다 — 문자열 존재가 아니라 파서 통과.
            var steps = BatchStepPlanParser.TryParse(result.SystemPrompt);
            Assert.NotNull(steps);
            Assert.Contains(steps!, s => s.Code == "S01" && !string.IsNullOrWhiteSpace(s.Name));
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

            Assert.Contains("NEVER use legacy `GOTO`-based error branching", result.SystemPrompt);
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
        public async Task ReviewConsolidatedPlanAsync_Prompt_ChecksChunkTransactionBoundaryAndForbidsGoto()
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

            Assert.Contains("iteration of a chunking", mockHandler.LastRequestBody);
            Assert.Contains("BEGIN TRAN", mockHandler.LastRequestBody);
            Assert.Contains("COMMIT TRAN", mockHandler.LastRequestBody);
            Assert.Contains("boundary, rather than wrapping the entire loop in a single outer transaction", mockHandler.LastRequestBody);
            Assert.Contains("legacy", mockHandler.LastRequestBody);
            Assert.Contains("GOTO", mockHandler.LastRequestBody);
            Assert.Contains("based error branching is used anywhere in the pseudocode", mockHandler.LastRequestBody);
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

        [Fact]
        public void MermaidAtSignRule_LeadsWithTheQuotingRequirementAndBansParaphrase()
        {
            // 이전 문구는 "@를 쓰지 마라(단, @@ERROR는 예외)"로 금지를 앞세웠다.
            // 모델이 과잉 적용해 @@ERROR를 "at at ERROR"로 풀어 썼고 가독성 점수를 깎았다.
            // 규칙은 허용을 앞세우고, 역설명을 명시적으로 금지해야 한다.
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/AiService.cs");
            var source = System.IO.File.ReadAllText(fullPath);

            // 생성 프롬프트와 Critic 채점 기준 양쪽이 같은 기준을 말해야 한다.
            Assert.Contains("MUST be wrapped in double quotes", source);
            Assert.Contains("never paraphrase or spell out", source);
            Assert.Contains("Flag any paraphrased or spelled-out", source);

            // 과잉 회피를 유발하던 옛 문구가 남아 있으면 안 된다.
            Assert.DoesNotContain("Do not include variables prefixed with '@'", source);
            Assert.DoesNotContain("Avoid variable names with '@'", source);
        }

        private static IReadOnlyList<BatchStepPlan> TwoSteps() => new[]
        {
            new BatchStepPlan("S01", "수수료율 스냅샷",
                new[] { "UP_Util_PG_Client_CMRate_Ins" }, new[] { "dbo.TPGSettleRate" }, new[] { "-1" }, false),
            new BatchStepPlan("S02", "정산 원장 생성",
                new[] { "UP_UTIL_SETTLE_INS" }, new[] { "dbo.TSettleMst" }, new[] { "-2" }, true)
        };

        private static IAiService StepService()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"### S01 수수료율 스냅샷\"}}]}";
            var httpClient = new HttpClient(new MockHttpMessageHandler(mockResponse));
            return new AiService(new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o"), 0.2f);
        }

        [Fact]
        public async Task GenerateBatchStepSectionAsync_CarriesStepContract()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();

            var result = await StepService().GenerateBatchStepSectionAsync(
                steps[1], steps, "공통 규약 본문", specs, "C#", "Test_Job");

            Assert.Contains("S02", result.UserPrompt);
            Assert.Contains("공통 규약 본문", result.UserPrompt);
            Assert.Contains("dbo.TSettleMst", result.UserPrompt);
            // 단계 하나만 쓰라는 계약이 시스템 프롬프트에 있어야 한다.
            Assert.Contains("ONE step section", result.SystemPrompt);
            // 문서 전체 규칙(오류코드 원본 재사용 등)도 함께 실려야 한다.
            Assert.Contains("[Required Content & Rules]", result.SystemPrompt);
        }

        // 접두사가 갈라지면 프롬프트 캐시가 매 단계 미스가 되어 분할 비용이 N배로 뛴다.
        // 이 테스트가 그 회귀를 막는 유일한 장치다.
        [Fact]
        public async Task GenerateBatchStepSectionAsync_KeepsIdenticalPromptPrefixAcrossSteps()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();
            var service = StepService();

            var first = await service.GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", specs, "C#", "Test_Job");
            var second = await service.GenerateBatchStepSectionAsync(
                steps[1], steps, "공통 규약 본문", specs, "C#", "Test_Job");

            const string marker = "Now write the section for step";
            Assert.NotNull(first.UserPrompt);
            Assert.NotNull(second.UserPrompt);
            var firstUserPrompt = first.UserPrompt!;
            var secondUserPrompt = second.UserPrompt!;
            var firstPrefix = firstUserPrompt.Substring(0, firstUserPrompt.IndexOf(marker, StringComparison.Ordinal));
            var secondPrefix = secondUserPrompt.Substring(0, secondUserPrompt.IndexOf(marker, StringComparison.Ordinal));

            Assert.Equal(firstPrefix, secondPrefix);

            // 시스템 프롬프트는 step 인자에서 파생되는 내용이 전혀 없어야 한다.
            // .Replace(...)로 맞춰보는 비교는 구현의 보간 지점을 그대로 베낀 것이라
            // 우연한 부분 문자열 충돌에도 통과할 수 있다 — 완전 동일성이 더 강한 회귀 가드다.
            Assert.Equal(first.SystemPrompt, second.SystemPrompt);
        }

        // 하한 미달 재시도 피드백은 접두사 뒤(말미)에 붙어야 캐시가 유지된다.
        [Fact]
        public async Task GenerateBatchStepSectionAsync_AppendsFloorFeedbackAfterThePrefix()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();

            var result = await StepService().GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", specs, "C#", "Test_Job",
                effort: null, floorFeedback: "코드 블록이 없습니다");

            Assert.NotNull(result.UserPrompt);
            var marker = result.UserPrompt!.IndexOf("Now write the section for step", StringComparison.Ordinal);
            var feedback = result.UserPrompt!.IndexOf("코드 블록이 없습니다", StringComparison.Ordinal);

            Assert.True(feedback > marker, "피드백은 지시문 뒤에 붙어야 한다");
        }

        [Fact]
        public async Task GenerateBatchPlanSkeletonAsync_RequestsPlaceholdersInsteadOfStepBodies()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();

            var result = await StepService().GenerateBatchPlanSkeletonAsync(
                steps, "## 목차 산문", specs, "C#", "Test_Job");

            Assert.NotNull(result.SystemPrompt);
            Assert.NotNull(result.UserPrompt);
            var systemPrompt = result.SystemPrompt!;
            var userPrompt = result.UserPrompt!;

            Assert.Contains("<!-- STEP:S01 -->", systemPrompt);
            Assert.Contains("<!-- STEP:S02 -->", systemPrompt);
            Assert.Contains("단계별 이행 상세 및 의사코드", systemPrompt);
            // 문서 전체 규칙이 함께 실려야 골격의 공통 규약이 그 규칙을 따른다.
            Assert.Contains("[Required Content & Rules]", systemPrompt);
            Assert.Contains("## 목차 산문", userPrompt);
        }

        /// <summary>
        /// 코드 리뷰 지적 사항(Finding 8) 픽스: 골격 호출(GenerateBatchPlanSkeletonAsync)은
        /// 공통 규약을 아직 갖고 있지 않다 — 그 골격 호출 자신이 문서에 그 규약을
        /// 처음 써넣는 쪽이다. 그런데 AppendSharedStepContext는 예전에 sharedConventions가
        /// 빈 문자열이어도 "[Shared Conventions Already Written In The Document]" 헤더를
        /// 무조건 찍었다 — 규약을 써야 할 호출에게 "이미 문서에 있다"는 거짓 전제를
        /// 준 것이다. 헤더는 내용이 있을 때만 나와야 한다.
        /// </summary>
        [Fact]
        public async Task GenerateBatchPlanSkeletonAsync_OmitsSharedConventionsHeaderWhenConventionsAreEmpty()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();

            var result = await StepService().GenerateBatchPlanSkeletonAsync(
                steps, "## 목차 산문", specs, "C#", "Test_Job");

            Assert.NotNull(result.UserPrompt);
            Assert.DoesNotContain("[Shared Conventions Already Written In The Document]", result.UserPrompt!);
        }

        // 단계 섹션 호출(GenerateBatchStepSectionAsync)은 골격이 이미 써 둔 공통 규약을
        // 넘겨받으므로, 그 헤더는 여전히 나와야 한다 — Finding 8 픽스가 헤더를
        // 완전히 없앤 게 아니라 "내용이 있을 때만" 조건부로 만들었는지 확인한다.
        [Fact]
        public async Task GenerateBatchStepSectionAsync_KeepsSharedConventionsHeaderWhenConventionsArePresent()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };
            var steps = TwoSteps();

            var result = await StepService().GenerateBatchStepSectionAsync(
                steps[0], steps, "공통 규약 본문", specs, "C#", "Test_Job");

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("[Shared Conventions Already Written In The Document]", result.UserPrompt!);
            Assert.Contains("공통 규약 본문", result.UserPrompt!);
        }

        // 산문 피드백에서 단계 코드를 키워드 매칭으로 뽑지 않는다.
        // RegenerationScopeSelector의 클래스 주석이 그 방식의 실패를 이미 기록하고 있다 —
        // LLM이 쓴 산문에 키워드를 걸면 프롬프트 문구가 바뀔 때 아무 신호 없이 오작동한다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_ParsesDefectiveStepsFromJson()
        {
            var reviewJson = "{\\\"HasDefects\\\":true,\\\"FeedbackComment\\\":\\\"S08 SQL 누락\\\"," +
                "\\\"DefectiveSteps\\\":[\\\" S08 \\\",\\\"S10\\\"]," +
                "\\\"ScoreAccuracy\\\":7,\\\"ScoreCrud\\\":9,\\\"ScoreInterface\\\":9,\\\"ScoreException\\\":9,\\\"ScoreReadability\\\":9}";
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"" + reviewJson + "\"}}]}";
            var httpClient = new HttpClient(new MockHttpMessageHandler(mockResponse));
            IAiService service = new AiService(
                new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o"), 0.2f);

            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };

            var review = await service.ReviewConsolidatedPlanAsync(specs, "## 계획서", "Test_Job");

            Assert.Equal(new[] { "S08", "S10" }, review.DefectiveSteps);
        }

        [Fact]
        public async Task ReviewConsolidatedPlanAsync_WithoutDefectiveSteps_ReturnsEmptyList()
        {
            var reviewJson = "{\\\"HasDefects\\\":false,\\\"FeedbackComment\\\":\\\"\\\"," +
                "\\\"ScoreAccuracy\\\":9,\\\"ScoreCrud\\\":9,\\\"ScoreInterface\\\":9,\\\"ScoreException\\\":9,\\\"ScoreReadability\\\":9}";
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"" + reviewJson + "\"}}]}";
            var httpClient = new HttpClient(new MockHttpMessageHandler(mockResponse));
            IAiService service = new AiService(
                new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o"), 0.2f);

            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "## 개요\n원장 생성")
            };

            var review = await service.ReviewConsolidatedPlanAsync(specs, "## 계획서", "Test_Job");

            // HasDefects도 함께 확인한다 — DefectiveSteps만 보면 파싱 성공 경로의 빈 배열과
            // catch 폴백의 빈 배열(파싱 자체가 실패했을 때)을 구분할 수 없다.
            // catch 폴백은 HasDefects를 무조건 true로 두므로, false 확인은 성공 경로에서만 통과한다.
            Assert.False(review.HasDefects);
            Assert.Empty(review.DefectiveSteps);
        }

        private static SpDefinition SchemaFilterSpDef(
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> referencedColumns,
            string dependencyName)
        {
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };

            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = dependencyName,
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "CLIENTID", DataType = "varchar(20)" },
                    new ColumnInfo { ColumnName = "CYMD", DataType = "char(8)" },
                    new ColumnInfo { ColumnName = "NonSettleAmt", DataType = "money" }
                }
            });

            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = referencedColumns
            };

            return spDef;
        }

        private static IAiService SpecService()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var httpClient = new HttpClient(new MockHttpMessageHandler(mockResponse));
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            return new AiService(client, 0.2f);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldKeepColumnsFromEveryCanonicalMatch()
        {
            // 정규화가 한정을 못 한 경우(ObjectKey 없음 등) 키가 갈라진 채 남을 수 있다.
            // 첫 매치에서 멈추면 INSERT 전용 컬럼이 스키마 표에서 사라진다.
            var spDef = SchemaFilterSpDef(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } },
                    { "TSettleMst", new System.Collections.Generic.List<string> { "CYMD", "NonSettleAmt" } }
                },
                "TSettleMst");

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.Contains("| CYMD |", result.UserPrompt);
            Assert.Contains("| NonSettleAmt |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldNotMatchATableWhoseNameMerelyContainsTheDependency()
        {
            // dep.Name = "TSettleMst" 가 "TSettleMstBackup" 키에 부분 매칭되던 버그.
            // 백업 테이블의 참조 컬럼이 본 테이블의 필터를 통과시켜선 안 된다.
            var spDef = SchemaFilterSpDef(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TSettleMstBackup", new System.Collections.Generic.List<string> { "CYMD" } },
                    { "SETTLE_POQ_DB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } }
                },
                "TSettleMst");

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.DoesNotContain("| CYMD |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldQualifyDependencyListWithItsDatabase()
        {
            // 의존성 목록이 DB를 안 찍으면 PaymentDB.dbo.TTxMst 와 dbo.TTxMst 가
            // 프롬프트에서 구별되지 않는다. 바로 아래 스키마 블록은 3파트로 찍는다.
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };
            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "PaymentDB",
                Schema = "dbo",
                Name = "TTxMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1
            });

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("PaymentDB.dbo.TTxMst", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldFallBackToBaseNameMatchWhenNoDatabaseContext()
        {
            // ObjectKey가 없으면 CanonicalizeParts가 DB를 못 채워 키가 갈라진 채 남는다.
            // 이 필터는 토큰 절약용 최적화일 뿐 정확성 장치가 아니다 - 과다 포함은
            // 표에 불필요한 행을 몇 개 더할 뿐이지만, 과소 포함은 모델이 "존재하지
            // 않는 컬럼"이라고 잘못 기록한다(14개 명세서를 망가뜨린 바로 그 결함).
            // 그래서 DB 컨텍스트가 없을 때는 베이스 이름 비교로 과다 포함 쪽으로 기운다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };

            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TSettleMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "CLIENTID", DataType = "varchar(20)" },
                    new ColumnInfo { ColumnName = "CYMD", DataType = "char(8)" }
                }
            });

            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } },
                    { "TSettleMst", new System.Collections.Generic.List<string> { "CYMD" } }
                }
            };

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.Contains("| CYMD |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldNotLetBaseNameFallbackMergeDifferentDatabasesWhenContextExists()
        {
            // ObjectKey가 있어 DB 컨텍스트를 확보한 정상 경로에서는 폴백이 적용되면 안 된다.
            // dbo.TPGProperty(현재 DB)와 PaymentDB.dbo.TPGProperty(다른 DB)를 베이스
            // 이름만으로 합치면 서로 다른 물리 테이블의 컬럼이 섞인다.
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };

            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TPGProperty",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "OwnColumn", DataType = "varchar(20)" },
                    new ColumnInfo { ColumnName = "OtherDbColumn", DataType = "varchar(20)" }
                }
            });

            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TPGProperty", new System.Collections.Generic.List<string> { "OwnColumn" } },
                    { "PaymentDB.dbo.TPGProperty", new System.Collections.Generic.List<string> { "OtherDbColumn" } }
                }
            };

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| OwnColumn |", result.UserPrompt);
            Assert.DoesNotContain("| OtherDbColumn |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldFallBackToBaseNameMatchWhenObjectKeyHasNoDatabaseEvenIfDependencyDoes()
        {
            // dep.Database가 있어도 ReferencedColumnsPerTable의 원시 키(예: "TSettleMst")를
            // 한정하는 데는 쓰이지 않는다 - 그 비한정 키의 암묵적 DB는 분석 대상 객체
            // 자신의 DB(spDef.ObjectKey.Database)이지, 지금 비교 중인 의존성의 DB가
            // 아니다. 그래서 키 쪽 한정 가능 여부는 오직 spDef.ObjectKey?.Database에만
            // 달려 있고, dep.Database는 이 판단에 기여하지 않는다. ObjectKey가 없으면
            // dep.Database가 있어도 폴백(베이스 이름 비교)으로 가야 한다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };

            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "PaymentDB",
                Schema = "dbo",
                Name = "TSettleMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "CLIENTID", DataType = "varchar(20)" },
                    new ColumnInfo { ColumnName = "CYMD", DataType = "char(8)" }
                }
            });

            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "PaymentDB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } },
                    { "TSettleMst", new System.Collections.Generic.List<string> { "CYMD" } }
                }
            };

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.Contains("| CYMD |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldUseSameQualifiedNameInDependencyListAndSchemaHeaderWhenDependencyHasDatabase()
        {
            // 설계서 §5: 의존성 목록과 스키마 블록 헤더가 같은 물리 테이블을 다른
            // 표기로 찍으면 모델이 그 둘을 서로 다른 테이블로 읽을 수 있다. dep.Database가
            // 있어 [DB].[Schema].[Name] 대괄호 표기로 갈라지던 경우를 덮는다.
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };
            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "PaymentDB",
                Schema = "dbo",
                Name = "TTxMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "TxId", DataType = "int" }
                }
            });

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("- Name: PaymentDB.dbo.TTxMst, Type:", result.UserPrompt);
            Assert.Contains("### 테이블: PaymentDB.dbo.TTxMst (", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldUseSameQualifiedNameInDependencyListAndSchemaHeaderWhenDependencyHasNoDatabase()
        {
            // dep.Database가 없어 spDef.ObjectKey로 한정되는 경우도 두 블록의 표기가
            // 같아야 한다. 이전에는 스키마 블록 헤더만 DB 없이 "dbo.TSettleMst"로
            // 남아 의존성 목록의 "SETTLE_POQ_DB.dbo.TSettleMst"와 어긋났다.
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };
            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TSettleMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "CLIENTID", DataType = "varchar(20)" }
                }
            });

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("- Name: SETTLE_POQ_DB.dbo.TSettleMst, Type:", result.UserPrompt);
            Assert.Contains("### 테이블: SETTLE_POQ_DB.dbo.TSettleMst (", result.UserPrompt);
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
