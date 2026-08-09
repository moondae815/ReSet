using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;
using System.Collections.Generic;

namespace ReSet.Core.Tests
{
    public class AiServiceTests_Rich
    {
        [Fact]
        public async Task GenerateSpecificationAsync_WithRichStaticAnalysis_CoversPromptBuilding()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_RichMonolithic", 
                DdlText = "SELECT 1;\r\nSET @po_intRetVal = -1;\r\nRETURN -1;" 
            };
            
            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "OtherDb",
                Schema = "dbo",
                Name = "TBL_User",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "Id", DataType = "INT", IsPrimaryKey = true, Description = "User ID", IsDescriptionMissing = true },
                    new ColumnInfo { ColumnName = "Name", DataType = "VARCHAR", Description = "User Name" }
                }
            });
            
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedTables = new List<string> { "dbo.TBL_User" },
                SelectTables = new List<string> { "dbo.TBL_User" },
                InsertTables = new List<string> { "dbo.TBL_Log" },
                AstInsertMappings = new List<AstInsertMapping>
                {
                    new AstInsertMapping { TargetTable = "dbo.TBL_Log", TargetColumns = new List<string> { "LogId" }, SourceQueryBlock = "SELECT 1" },
                    new AstInsertMapping { TargetTable = "dbo.TBL_Log2", TargetColumns = new List<string>(), SourceQueryBlock = "SELECT 2" }
                },
                UpdateTables = new List<string> { "dbo.TBL_User" },
                DeleteTables = new List<string> { "dbo.TBL_User" },
                CreatedTempTables = new List<string> { "#TempLog" },
                LinkedServerReferences = new List<string> { "LINKED_SRV.db.dbo.tbl" },
                ReferencedFunctions = new List<string> { "dbo.UDF_GetDate" },
                ControlFlowSummary = new List<string> { "IF @@ERROR <> 0" },
                ReferencedColumnsPerTable = new Dictionary<string, List<string>>
                {
                    { "dbo.TBL_User", new List<string> { "Id" } }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 생성된 명세서\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);

            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateSpecificationAsync(spDef, "지침", "피드백 수정내용");

            // Assert
            Assert.Equal("## 생성된 명세서", result.Content);
            Assert.Contains("## 생성된 명세서", result.Content);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_WithRichStaticAnalysis_CoversChunkingPromptBuilding()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_RichChunking", 
                DdlText = "EXEC sp_executesql N'SELECT 1';\r\nSET @po_intRetVal = -1;\r\nRETURN -1;" 
            };
            
            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = "TBL_User",
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "Id", DataType = "INT", IsDescriptionMissing = true }
                }
            });
            
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                SelectTables = new List<string> { "dbo.TBL_User" },
                InsertTables = new List<string> { "dbo.TBL_Log" },
                CreatedTempTables = new List<string> { "#TempLog" },
                LinkedServerReferences = new List<string> { "LINKED_SRV.db.dbo.tbl" },
                ReferencedFunctions = new List<string> { "dbo.UDF_GetDate" },
                ParserWarningMessage = "Warning in parser"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\\n테스트\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var client = new OpenAiClient(new HttpClient(mockHandler), "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act - Overview
            var res1 = await service.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", "지침", null, null, CancellationToken.None);
            
            // Act - CrudAnalysis
            var res2 = await service.GenerateSpecSectionAsync(spDef, "CrudAnalysis", "지침", null, null, CancellationToken.None);
            
            // Act - LogicAndVisualization
            var res3 = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "지침", null, null, CancellationToken.None);

            // Assert
            Assert.NotNull(res1);
            Assert.NotNull(res2);
            Assert.NotNull(res3);
        }

        [Fact]
        public async Task GenerateBatchMigrationPlanAsync_WithRichSpDef_CoversBranch()
        {
            // Arrange
            var spDef = new SpDefinition 
            { 
                Schema = "dbo", 
                Name = "USP_BatchTest", 
                DdlText = "SELECT 1;",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 배치 전환 계획\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateBatchMigrationPlanAsync(spDef, "C#");

            // Assert
            Assert.Equal("## 배치 전환 계획", result.Content);
        }

        [Fact]
        public async Task GenerateBatchMigrationPlanAsync_DoesNotAskForOrmPseudocode()
        {
            // 지시서가 ORM을 허용 목록 4가지로 제한하므로, 계획 프롬프트가 ORM 의사코드를
            // 요구하면 두 문서가 서로 다른 기준을 말하게 된다.
            // Arrange
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_Plan",
                DdlText = "SELECT 1;",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 배치 전환 계획\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateBatchMigrationPlanAsync(spDef, "C#");

            // Assert
            Assert.DoesNotContain("ORM pseudocode", result.SystemPrompt);
            Assert.Contains("OOP pseudocode", result.SystemPrompt);
        }

        private static (AiService Service, MockHttpMessageHandler Handler) CreateProbe()
        {
            var handler = new MockHttpMessageHandler(
                "{\"choices\":[{\"message\":{\"content\":\"## 생성된 명세서\"}}]}");
            var client = new OpenAiClient(new HttpClient(handler), "k", "https://api.openai.com/v1", "gpt-4o");
            return (new AiService(client, 0.2f), handler);
        }

        private static SpDefinition ProbeSpDef(params AstUpdateMapping[] mappings)
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "COMM_UPD", DdlText = "SELECT 1;" };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                UpdateTables = new List<string> { "DB.dbo.TCommMst" },
                AstUpdateMappings = new List<AstUpdateMapping>(mappings)
            };
            return spDef;
        }

        private static AstUpdateMapping Mapping(
            string? fromClause = null, params string[] selfReferenced)
        {
            var mapping = new AstUpdateMapping
            {
                TargetTable = "DB.dbo.TCommMst",
                StatementOrdinal = 2,
                FromClauseText = fromClause
            };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "CLVT", SourceExpression = "CLVT * -1" });
            mapping.SelfReferencedColumns.AddRange(selfReferenced);
            return mapping;
        }

        /// <summary>
        /// System.Text.Json의 기본 인코더는 비ASCII 문자를 \uXXXX 형태로 이스케이프해서 직렬화하므로,
        /// 원문 요청 본문 문자열에는 한글 리터럴이 그대로 나타나지 않는다.
        /// 메시지 content 필드들을 실제로 역직렬화해서 이스케이프가 풀린 원문 텍스트로 대조한다.
        /// </summary>
        private static string DecodeMessageContents(string requestBody)
        {
            using var doc = JsonDocument.Parse(requestBody);
            var sb = new StringBuilder();
            foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                sb.AppendLine(message.GetProperty("content").GetString());
            }
            return sb.ToString();
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithUpdateMappings_ShouldPrefillTheTable()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(Mapping()), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("AST UPDATE 타겟-소스 1:1 매핑 추출 데이터", body);
            Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 2)", body);
            Assert.Contains("CLVT * -1", body);
            Assert.Contains("(FILL_DESCRIPTION_HERE)", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutUpdateMappings_ShouldOmitTheBlock()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("AST UPDATE 타겟-소스 1:1 매핑 추출 데이터", body);
            Assert.DoesNotContain("### UPDATE 대상 테이블:", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithFromClause_ShouldAttachNondeterminismWarning()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(
                ProbeSpDef(Mapping(fromClause: "FROM DB.dbo.TCommMst A")), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("비결정적", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutFromClause_ShouldNotAttachNondeterminismWarning()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(Mapping()), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("비결정적", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithSelfReference_ShouldAttachSimultaneousEvaluationRule()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(
                ProbeSpDef(Mapping(fromClause: null, "CLVT")), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("갱신 전 값", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutSelfReference_ShouldNotAttachSimultaneousEvaluationRule()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(Mapping()), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("갱신 전 값", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithPipeInExpression_ShouldEscapeTheTableCell()
        {
            // Arrange
            var (service, handler) = CreateProbe();
            var mapping = new AstUpdateMapping { TargetTable = "DB.dbo.TCommMst", StatementOrdinal = 1 };
            mapping.Assignments.Add(new AstUpdateAssignment
            {
                Column = "FLAGS",
                SourceExpression = "FLAGS | 4"
            });

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(mapping), "지침", null);

            // Assert - 이스케이프하지 않으면 표의 셀 경계가 깨진다.
            // JSON 직렬화가 백슬래시를 한 번 더 이스케이프하므로 본문에는 `\\|`로 나타난다.
            Assert.Contains(@"FLAGS \\| 4", handler.LastRequestBody);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithNewlineInExpression_ShouldEscapeTheTableCell()
        {
            // Arrange - 여러 줄에 걸친 CASE 식처럼 SET 우변에 개행이 들어간 경우를 재현한다.
            var (service, handler) = CreateProbe();
            var mapping = new AstUpdateMapping { TargetTable = "DB.dbo.TCommMst", StatementOrdinal = 1 };
            mapping.Assignments.Add(new AstUpdateAssignment
            {
                Column = "NOTE",
                SourceExpression = "CASE\nWHEN A THEN 1\nEND"
            });

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(mapping), "지침", null);

            // Assert - 개행을 접지 않으면 표 행 하나가 여러 줄로 쪼개져 마크다운 표가 깨진다.
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("CASE WHEN A THEN 1 END", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithUpdateMappings_ShouldPrefillTheTable()
        {
            // Arrange - 지역 모델 경로(GenerateSpecSectionAsync의 "CrudAnalysis" 분기)도
            // BuildSpecificationPrompts(전체 명세서 1회 생성)와 같은 UPDATE fill-in 템플릿을
            // 받아야 한다. L1(VerificationPipelineOrchestrator)이 요구하는 `### UPDATE 대상
            // 테이블:` 접두 H3 헤딩을 지역 모델이 자발적으로 쓰지 않으면 1차 시도가
            // 구조적으로 실패한다.
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecSectionAsync(
                ProbeSpDef(Mapping()), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 2)", body);
            Assert.Contains("CLVT * -1", body);
            Assert.Contains("(FILL_DESCRIPTION_HERE)", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithoutUpdateMappings_ShouldOmitTheBlock()
        {
            // Arrange
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecSectionAsync(
                ProbeSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("### UPDATE 대상 테이블:", body);
        }
    }
}
