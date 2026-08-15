using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;
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

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

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
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                effort: null,
                previousStructure: "## 낡은 목차",
                redraftFeedback: "청킹 불가 스텝이 청킹으로 배치됨");

            Assert.Contains("[Redraft]", result.SystemPrompt);
            Assert.Contains("## 낡은 목차", result.UserPrompt);
            Assert.Contains("청킹 불가 스텝이 청킹으로 배치됨", result.UserPrompt);
            Assert.Contains("통합 배치 아키텍처 개요", result.SystemPrompt);
        }

        // 상한은 코드에만 있고 프롬프트에는 없었다. 그 사이 프롬프트는 "NEVER collapse
        // several steps"로 잘게 쪼개라고만 밀었고, 실측에서 claude-opus-5가 73단계를 냈다가
        // BatchStepPlanParser가 목차 전체를 버렸다. 모델은 자기가 들어본 적 없는 규칙에
        // 걸린 것이다 — 상수를 프롬프트에 실어야 지킬 기회가 생긴다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_TellsTheModelTheStepCountCap()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            // 숫자를 손으로 적으면 상수와 갈라진다. 보간을 강제해, 상한을 바꾼 사람이
            // 프롬프트를 고치지 않으면 이 테스트가 실패하게 만든다.
            Assert.Contains(
                $"AT MOST {BatchStepPlanParser.MaxSteps} entries",
                result.SystemPrompt);
        }

        // 숫자만 주면 모델이 권고로 읽는다. 넘겼을 때 목차가 통째로 버려지고 단일 호출로
        // 폴백한다는 대가를 알려줘야 입도를 조절할 근거가 생긴다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_TellsTheModelWhatExceedingTheCapCosts()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.Contains("discards a longer list", result.SystemPrompt);
            Assert.Contains("one step per internal branch", result.SystemPrompt);
        }

        // 2026-08-13에 넣은 규칙이 회귀를 만들었다. "명세서가 부르는 그대로 쓰라"고
        // 요구했는데 목차 단계는 명세서를 받지 않는다. codex-cli는 추정이 규칙 위반이라
        // 판단해 단계 목록을 통째로 비웠고(POQSettleProc7), 단계별 섹션 33개와 원본
        // 오류코드 20개가 사라진 문서가 92점으로 통과했다. 명단을 주면 같은 요구가
        // 암기가 아니라 선택이 되어 비로소 지킬 수 있는 규칙이 된다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_PutsTheProcedureRosterInTheUserPrompt()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job",
                new[] { "dbo.UP_UTIL_SETTLE_INS", "dbo.UP_Util_Settle_Summary" });

            Assert.Contains("[Source Procedures", result.UserPrompt);
            Assert.Contains("- dbo.UP_UTIL_SETTLE_INS", result.UserPrompt);
            Assert.Contains("- dbo.UP_Util_Settle_Summary", result.UserPrompt);
        }

        // 명단은 잡마다 달라지므로 시스템 프롬프트에 실으면 캐시 접두사가 매번 깨진다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_KeepsTheRosterOutOfTheSystemPrompt()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.DoesNotContain("dbo.UP_UTIL_SETTLE_INS", result.SystemPrompt);
        }

        [Fact]
        public async Task DraftBatchPlanStructureAsync_TellsTheModelToSelectFromTheRoster()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.Contains("copied verbatim from the supplied Source", result.SystemPrompt);
            // 회귀를 만든 옛 문구는 남아 있으면 안 된다.
            Assert.DoesNotContain("exactly as the source specifications name it", result.SystemPrompt);
        }

        // 거부가 더 비싸다는 사실을 알려주지 않으면 모델은 거부를 택한다. 실측에서
        // 빈 Steps 목록 하나가 단계별 섹션 전부와 단계별 검사 전부를 없앴다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_ForbidsAnEmptyStepList()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.Contains("Never emit an empty `Steps` list", result.SystemPrompt);
            Assert.Contains("discards every per-step section", result.SystemPrompt);
        }

        // 2026-08-13 회귀는 이 관용이 `LegacyProcedures` 한 필드에만 적용되고
        // `TargetTables`·`ErrorCodes`는 여전히 명세서 대조 수준의 정확성을
        // 요구한다고 읽혀 만들어졌다 - 모델이 이 스테이지에서 명세서를 받지
        // 못한다는 사실을 알리지 않은 채였다. 세 필드 모두 하류에서 교정된다는
        // 사실과 함께 관용을 넓혀야 같은 회귀가 재발하지 않는다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_BroadensTheEscapeHatchToAllThreeRosterFields()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            // 관용이 세 필드 모두를 이름으로 명시해야 한다 - 하나만 남으면
            // 모델은 나머지 둘에 대해 다시 거부를 택한다.
            Assert.Contains("imperfect `LegacyProcedures`, `TargetTables`, or `ErrorCodes` is recoverable", result.SystemPrompt);

            // TargetTables·ErrorCodes가 이 단계에서 받지 못하는 명세서를 근거로
            // 하류에서 다시 계산된다는 사실이 규칙 본문에 적혀 있어야 한다.
            Assert.Contains("This stage receives no source specifications", result.SystemPrompt);
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

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

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
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" },
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
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_PinsTheBatchObjectSchemaNames()
        {
            // 실측(POQSettleProc10): 프롬프트가 배치 전용 객체를 어느 스키마에 두라고
            // 말한 적이 없어, 계획서가 batch·poqbatch·poqsettlebatch 세 이름으로
            // 갈라졌다. 회차 0의 수집기는 batch 계열만 보므로 나머지 238건이 참조하는
            // 객체는 아무도 만들지 않았다.
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

            Assert.Contains("[Batch Object Schema]", result.SystemPrompt);
            // 스키마 이름의 소유자는 수집기다. 프롬프트가 자기 목록을 따로 들면
            // 두 곳이 갈라지므로, 그 배열에서 온 이름이 실제로 실렸는지 본다.
            foreach (var schema in BatchInfraObjectCollector.Schemas)
            {
                Assert.Contains($"`{schema}`", result.SystemPrompt);
            }
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ShadowExampleUsesTheBatchShadowSchema()
        {
            // 규칙만 넣고 예시를 두면 프롬프트가 스스로 모순된다 - 옛 예시
            // TargetTable_Shadow_YYYYMMDD는 스키마가 없어 새 규칙과 어긋나고,
            // 모델은 규칙보다 눈앞의 예시를 따라간다.
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

            Assert.DoesNotContain("TargetTable_Shadow_YYYYMMDD", result.SystemPrompt);
            Assert.Contains("batch_shadow.", result.SystemPrompt);
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

        /// <summary>
        /// 노드 라벨 예시가 이중 따옴표였다. verbatim이 아닌 일반 문자열의
        /// \"\"는 따옴표 두 개로 렌더되므로 모델에게 id1[""Text""]가 모범
        /// 예시로 전달됐고, 그대로 따라 쓴 결과를 L2가 "치명적 Mermaid 문법
        /// 오류"로 반려했다 - 이 저장소의 배치 실행에서 반복 관측된 패턴이다.
        /// Critic 기준은 따옴표 1쌍을 요구하므로 예시가 기준과 어긋나 있었다.
        /// </summary>
        [Fact]
        public void MermaidNodeLabelExample_ShowsOneQuotePair_NotDoubled()
        {
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/AiService.cs");
            var source = System.IO.File.ReadAllText(fullPath);

            Assert.DoesNotContain("id1[\\\"\\\"Text", source);
            Assert.DoesNotContain("id2[\\\"\\\"Return Result\\\"\\\"]", source);
            Assert.Contains("id1[\\\"Text (Extra)\\\"]", source);
            Assert.Contains("id2[\\\"Return Result\\\"]", source);
        }

        /// <summary>
        /// 체크리스트도 같은 결함을 갖고 있었다. 큰따옴표를 쓰라는 항목이
        /// 정작 그 예시로 ("")를 보여 주면 이중 따옴표를 지시하는 것으로 읽힌다.
        /// </summary>
        [Fact]
        public void MermaidChecklist_DoesNotDemonstrateDoubledQuotes()
        {
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/AiService.cs");
            var source = System.IO.File.ReadAllText(fullPath);

            Assert.DoesNotContain("큰따옴표(\\\"\\\")", source);
        }

        /// <summary>
        /// Critic은 ScoreInterface에서 결과셋(Rowset) 반환 여부 명시를 채점하는데,
        /// 그 요구가 분할 경로(로컬 모델용) 규칙에만 있고 통짜 경로의 Actor
        /// 규칙에는 없었다. 실측 배치에서 "결과셋 반환 여부가 명시되어 있지
        /// 않습니다"라는 인터페이스 감점이 반복됐다.
        ///
        /// 소스 전체를 훑으면 Critic 기준 때문에 언제나 통과하므로,
        /// Actor 규칙을 쌓는 rules.Add 줄만 골라서 확인한다.
        /// </summary>
        [Fact]
        public void ResultSetRule_IsInGenerationRules_NotOnlyInCriticCriteria()
        {
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/AiService.cs");
            var lines = System.IO.File.ReadAllLines(fullPath);

            var found = false;
            foreach (var line in lines)
            {
                if (line.Contains("rules.Add(") && line.Contains("Rowset"))
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found, "Actor 생성 규칙(rules.Add)에 결과셋(Rowset) 명시 요구가 없습니다.");
        }

        /// <summary>
        /// 「참조 컬럼」 칸은 그 별칭이 실제로 읽는 컬럼만 담아야 한다.
        /// CANCEL_INS 실측에서 별칭 B의 참조 컬럼 목록에 삽입 대상 컬럼
        /// (YMD·AYMD·CYMD)과 상수로 채워지는 컬럼(INSTATE·OUTSTATE), 그리고
        /// 다른 별칭에서 오는 NonSettleAmt가 섞였다. 같은 문서의 INSERT 매핑
        /// 표는 정확했으므로 문서 내부에서 서로 어긋난 상태였다.
        ///
        /// 기계 게이트를 만들지 않고 생성 규칙으로 막는다 - 별칭 스코프 판정은
        /// 오탐 위험이 크고, 이 표를 만드는 문서가 26개 중 2개뿐이라 비용이
        /// 맞지 않는다.
        /// </summary>
        [Fact]
        public void ReferencedColumnRule_LimitsTheListToWhatTheAliasActuallyReads()
        {
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/AiService.cs");
            var lines = System.IO.File.ReadAllLines(fullPath);

            var found = false;
            foreach (var line in lines)
            {
                if (line.Contains("rules.Add(")
                    && line.Contains("only the columns that the query actually reads"))
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found, "Actor 생성 규칙에 참조 컬럼 목록의 범위 제한이 없습니다.");
        }

        private static IReadOnlyList<BatchStepPlan> TwoSteps() => new[]
        {
            new BatchStepPlan("S01", "수수료율 스냅샷",
                new[] { "UP_Util_PG_Client_CMRate_Ins" }, new[] { "dbo.TPGSettleRate" }, new[] { "-1" }, false,
                Array.Empty<string>()),
            new BatchStepPlan("S02", "정산 원장 생성",
                new[] { "UP_UTIL_SETTLE_INS" }, new[] { "dbo.TSettleMst" }, new[] { "-2" }, true,
                Array.Empty<string>())
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

        // 캐시는 접두사 일치다. 잡 이름이 명세서보다 앞에 있으면 잡이 바뀔 때마다
        // 뒤따르는 명세서 전량(실측 481KB)이 무효가 된다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_PutsTheJobNameAfterTheSpecifications()
        {
            // 이 테스트는 실제 OpenAiClient를 거쳐 나가는 원문 wire JSON을 검사한다.
            // System.Text.Json의 기본 인코더는 비 ASCII 문자를 \uXXXX로 이스케이프하므로,
            // 원문 문자열 그대로 남는 ASCII 고유 마커를 쓴다 — 한글 마커는 이스케이프되어
            // IndexOf가 항상 -1을 반환해 이 assert의 의도(순서 검증)를 검증할 수 없다.
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "SpecUniqueMarker")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## Plan", "Job_UniqueMarker");

            var body = mockHandler.LastRequestBody;
            Assert.True(
                body.IndexOf("SpecUniqueMarker") < body.IndexOf("Job_UniqueMarker"),
                "명세서가 잡 이름보다 앞에 와야 캐시 접두사가 잡 간에 공유된다.");
        }

        // 계획서 본문은 회차마다 재생성되므로 가변 조각에 있어야 한다. 고정 조각에
        // 들어가면 접두사가 매 회차 달라져 캐시가 살지 않는다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_SendsThePlanBodyAsTheVolatileSuffix()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "명세서 내용")
            };
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-4o");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "{\"HasDefects\": false}" });
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "계획서고유표시", "Test_Job");

            await client.Received(1).ChatAsync(
                Arg.Any<string>(),
                Arg.Is<string>(stable => stable.Contains("명세서 내용")
                                         && !stable.Contains("계획서고유표시")),
                Arg.Any<float>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(suffix => suffix != null && suffix.Contains("계획서고유표시")),
                Arg.Any<CancellationToken>());
        }

        // 제공자 간 동일성: 메시지를 나눌 수 없는 경로는 PromptComposition이 이어 붙인
        // 한 덩어리를 받고, Claude는 같은 두 조각을 블록으로 받는다. 두 조각을 합친
        // 결과가 세 부분을 원래 순서대로 담고 있어야 내용이 같다고 말할 수 있다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_MergedPromptKeepsEveryPartInOrder()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "명세서고유표시")
            };
            string? stable = null;
            string? suffix = null;
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-4o");
            client.ChatAsync(
                    Arg.Any<string>(),
                    Arg.Do<string>(s => stable = s),
                    Arg.Any<float>(),
                    Arg.Any<string?>(),
                    Arg.Do<string?>(v => suffix = v),
                    Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "{\"HasDefects\": false}" });
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "계획서고유표시", "Job_고유표시");

            var merged = PromptComposition.MergeVolatileSuffix(stable!, suffix!);
            Assert.True(
                merged.IndexOf("명세서고유표시") < merged.IndexOf("Job_고유표시"),
                "합친 결과에서도 명세서가 잡 이름보다 앞이어야 한다.");
            Assert.True(
                merged.IndexOf("Job_고유표시") < merged.IndexOf("계획서고유표시"),
                "합친 결과에서도 잡 이름이 계획서 본문보다 앞이어야 한다.");
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
