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
                GlobalStatementOrdinal = 2,
                SourceLine = 77,
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
            Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (갱신 2 · 원본 DDL 라인 77)", body);
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
            Assert.Contains("non-deterministic", body);
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
            Assert.DoesNotContain("non-deterministic", body);
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
            Assert.Contains("pre-update values", body);
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
            Assert.DoesNotContain("pre-update values", body);
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
            Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (갱신 2 · 원본 DDL 라인 77)", body);
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

        private static SpDefinition ProbeSpDefWithSchema()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "COMM_UPD", DdlText = "SELECT 1;" };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };

            var dep = new DependencyInfo
            {
                Name = "TCommMst", Schema = "dbo", Database = "DB", Type = "USER_TABLE"
            };
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLVT", DataType = "int" });
            spDef.Dependencies.Add(dep);

            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithSchemaTable_ShouldDeclareItComplete()
        {
            // Arrange - A 검사가 "참조 컬럼은 빠짐없이 실린다"를 보장하므로, 모델에게 줄
            // 올바른 지시는 부재 주장을 적을 빈칸을 여는 것이 아니라 그 반대다.
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDefWithSchema(), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다", body);
            Assert.Contains("스키마에 없다고 기술하지 마십시오", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutSchemaTable_ShouldOmitTheDeclaration()
        {
            // Arrange - 스키마 표가 없으면 "이 표는 완전합니다"는 가리킬 대상이 없는
            // 거짓 문장이 된다. ProbeSpDef는 의존성이 없어 표가 렌더링되지 않는다.
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다", body);
        }

        /// <summary>
        /// UP_UTIL_STAT_PGCOLLECT_INS 실측 형태 - INSERT 전용, UPDATE 매핑이 전혀 없다.
        /// ThreePartObjectReferences 근거 문구는 UPDATE 헤딩 병기가 구조적으로 닿지
        /// 못하는 이 형태에 실제로 미치는지를 증명하기 위한 것이라 이 헬퍼가 필요하다.
        /// </summary>
        private static SpDefinition ProbeInsertOnlySpDef(params string[] threePartObjectReferences)
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "UP_UTIL_STAT_PGCOLLECT_INS", DdlText = "SELECT 1;" };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                InsertTables = new List<string> { "dbo.TStatPGCollect" },
                AstInsertMappings = new List<AstInsertMapping>
                {
                    new AstInsertMapping
                    {
                        TargetTable = "dbo.TStatPGCollect",
                        TargetColumns = new List<string> { "C" },
                        SourceQueryBlock = "SELECT C FROM TSettleMst"
                    }
                },
                ThreePartObjectReferences = new List<string>(threePartObjectReferences)
            };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_SystemPrompt_ShouldContainTheIdentifierNotationRule()
        {
            // 라운드 1 Critical의 짝 - 메인 생성 경로. 규칙이 빠지면 이 어서션이 실패해야
            // 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("PARSER-NORMALIZED", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_SystemPrompt_ShouldContainTheIdentifierNotationRule()
        {
            // 라운드 1 Critical: 지역 모델 CrudAnalysis 경로가 원문 표기(a)는 병기하면서
            // 규칙(b)은 빠뜨려, 동기 부여 사례가 재생성돼도 같은 결함을 반복할 수 있었다.
            // 이 테스트가 그 분기 자체를 대상으로 삼는다 - 규칙이 이 분기에서만 삭제돼도
            // 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("PARSER-NORMALIZED", body);
        }

        /// <summary>
        /// COMM_UPD 실측 형태 - hasComments를 대체한 SourceCommentExtractor의 체크리스트
        /// 항목이 실제 프롬프트에 닿는지를 증명하기 위한 것이라 이 헬퍼가 필요하다.
        /// UPDATE 절 바로 아래 주석 처리된 조건(NonExecutable, 앵커 있음)을 둔다.
        /// </summary>
        private static SpDefinition ProbeSpDefWithSourceComment()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "COMM_UPD",
                DdlText = @"
CREATE PROCEDURE dbo.COMM_UPD AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  ID > 0
    --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29)
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldContainTheSourceCommentChecklistItem()
        {
            // Task 5의 짝 - 메인 생성 경로(BuildSpecificationPrompts). hasComments는 계산만
            // 되고 어디에서도 쓰이지 않는 죽은 변수였다 - 이 체크리스트 항목이 그 자리를
            // 대신한다. 항목이 빠지면 이 어서션이 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDefWithSourceComment(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("원본 DDL의 주석", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldContainTheSourceCommentChecklistItem()
        {
            // 위 테스트의 짝. 지역 모델 CrudAnalysis 경로(BuildSpecSectionPrompts의
            // "CrudAnalysis" 분기)는 지역 모델의 최초 생성 경로이자 L3 재생성 경로다 -
            // BuildSpecificationPrompts에만 항목을 두면 이 경로가 규칙을 받지 못한 채로
            // 남는다(Task 4의 Critical과 같은 모양의 결함). 이 테스트는 그 분기 자체를
            // 대상으로 삼는다 - 항목이 이 분기에서만 삭제돼도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeSpDefWithSourceComment(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("원본 DDL의 주석", body);
        }

        /// <summary>
        /// COMM_UPD 실측 형태(3인자 ROUND, UDF가 세 번째 인자) - 체크리스트 항목이
        /// 실제 프롬프트에 닿는지 증명하기 위한 헬퍼다.
        /// </summary>
        private static SpDefinition ProbeSpDefWithRoundingCall()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "COMM_UPD",
                DdlText = @"
CREATE PROCEDURE dbo.COMM_UPD AS
BEGIN
    UPDATE dbo.T
    SET    PGComm = ROUND(A.TxAmt * B.Rate / 100, 0, dbo.UF_GET_PGCommOption(A.PGName))
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldContainTheRoundingSemanticsChecklistItem()
        {
            // 메인 생성 경로(BuildSpecificationPrompts). 항목이 빠지면 이 어서션이
            // 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDefWithRoundingCall(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(RoundingSemanticsExtractor.SemanticsSentence, body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldContainTheRoundingSemanticsChecklistItem()
        {
            // 위 테스트의 짝. 지역 모델 CrudAnalysis 경로(BuildSpecSectionPrompts의
            // "CrudAnalysis" 분기)는 지역 모델의 최초 생성 경로이자 L3 재생성 경로다 -
            // BuildSpecificationPrompts에만 항목을 두면 이 경로가 규칙을 받지 못한 채로
            // 남는다(SourceComment 체크리스트와 같은 모양의 결함이 재발할 수 있다).
            // 이 테스트는 그 분기 자체를 대상으로 삼는다 - 항목이 이 분기에서만
            // 삭제돼도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeSpDefWithRoundingCall(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(RoundingSemanticsExtractor.SemanticsSentence, body);
        }

        /// <summary>
        /// Util_Settle_Summary 실측 형태 - SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에
        /// 있는데 명세서 전체에 언급이 없었다. 체크리스트 항목이 실제 프롬프트에
        /// 닿는지 증명하기 위한 헬퍼다.
        /// </summary>
        private static SpDefinition ProbeSpDefWithSessionOption()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_Util_Settle_Summary",
                DdlText = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary AS
SET NOCOUNT ON
BEGIN TRAN
    SELECT 1
COMMIT TRAN"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldContainTheSessionOptionsChecklistItem()
        {
            // 메인 생성 경로(BuildSpecificationPrompts). 항목이 빠지면 이 어서션이
            // 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDefWithSessionOption(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("호출 계층에 미치는 영향", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldContainTheSessionOptionsChecklistItem()
        {
            // 위 테스트의 짝. 지역 모델 CrudAnalysis 경로(BuildSpecSectionPrompts의
            // "CrudAnalysis" 분기)는 지역 모델의 최초 생성 경로이자 L3 재생성 경로다 -
            // BuildSpecificationPrompts에만 항목을 두면 이 경로가 규칙을 받지 못한 채로
            // 남는다(SourceComment·Rounding 체크리스트와 같은 모양의 결함이 재발할 수
            // 있다). 이 테스트는 그 분기 자체를 대상으로 삼는다 - 항목이 이 분기에서만
            // 삭제돼도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeSpDefWithSessionOption(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("호출 계층에 미치는 영향", body);
        }

        /// <summary>
        /// Util_Settle_Summary 실측 형태 - CREATE 이전의 헤더 주석(Header 종류)이
        /// "Inner SP : NONE"을 선언한다. 체크리스트 항목이 실제 프롬프트에 닿는지
        /// 증명하기 위한 헬퍼다. 원본 DDL이 이미 &lt;sp-source-ddl&gt;로 프롬프트에
        /// 통째로 들어가므로, 어서션은 그 DDL 원문에는 없는 체크리스트 고유 문구를
        /// 써야 한다 - 그렇지 않으면 체크리스트 항목이 없어도 원문 병기만으로
        /// 통과해 버려 아무것도 증명하지 못한다.
        /// </summary>
        private static SpDefinition ProbeSpDefWithHeaderComment()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_Util_Settle_Summary",
                DdlText = @"
-- Inner SP        : NONE
CREATE PROCEDURE dbo.UP_Util_Settle_Summary AS
BEGIN
    EXEC dbo.OtherProc
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldContainTheHeaderContractContradictionChecklistItem()
        {
            // 메인 생성 경로(BuildSpecificationPrompts). 항목이 빠지면 이 어서션이
            // 실패해야 한다. "헤더 주석이 선언한 계약"은 체크리스트 문구에만 있고
            // 원본 DDL(&lt;sp-source-ddl&gt;)에는 없다 - DDL 원문 병기만으로는
            // 통과할 수 없다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDefWithHeaderComment(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("헤더 주석이 선언한 계약", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldContainTheHeaderContractContradictionChecklistItem()
        {
            // 위 테스트의 짝. 지역 모델 CrudAnalysis 경로(BuildSpecSectionPrompts의
            // "CrudAnalysis" 분기)는 지역 모델의 최초 생성 경로이자 L3 재생성 경로다 -
            // BuildSpecificationPrompts에만 항목을 두면 이 경로가 규칙을 받지 못한 채로
            // 남는다(SourceComment·Rounding·SessionOption 체크리스트와 같은 모양의
            // 결함이 재발할 수 있다). 이 테스트는 그 분기 자체를 대상으로 삼는다 -
            // 항목이 이 분기에서만 삭제돼도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeSpDefWithHeaderComment(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("헤더 주석이 선언한 계약", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_InsertOnlyWithNoThreePartReferences_ShouldGroundTheEmptyCase()
        {
            // UPDATE 헤딩 원문 병기는 UPDATE 문이 없는 SP에 구조적으로 닿지 않는다.
            // 정적 분석 섹션에 실리는 이 목록만이 UP_UTIL_STAT_PGCOLLECT_INS 같은
            // INSERT 전용 SP에도 근거를 제공한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeInsertOnlySpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("3부 식별자 기반 크로스 데이터베이스 참조라고 단언하지 마십시오", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithThreePartObjectReferences_ShouldRenderTheActualList()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(
                ProbeInsertOnlySpDef("SETTLE_POQ_DB.dbo.TSettleMst"), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", body);
        }

        /// <summary>
        /// EXCEPTION_PROC 실행순서 18의 실측 형태 - YMD 파라미터가 EXISTS 서브쿼리
        /// 안에만 있고 바깥 UPDATE 대상에는 걸리지 않는다. 이 DDL 조각과 파라미터
        /// 문자열 자체가 <sp-source-ddl>에도 그대로 실리므로, 표가 실제로 렌더됐는지는
        /// 표에서만 나오는 마크업(헤딩·볼드 "아니오")으로 대조해야 한다 - 원본 DDL
        /// 텍스트에 우연히 있는 단어를 짚으면 표가 없어도 통과하는 거짓양성 테스트가 된다.
        /// </summary>
        private static SpDefinition ProbeDmlScopeSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "EXCEPTION_PROC",
                DdlText = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.UseState = 0
    AND    EXISTS (SELECT 1 FROM dbo.TSettleMst B WHERE B.YMD = @pi_strYMD)
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ProcedureParameters = new List<string> { "@pi_strYMD" }
            };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithDmlScopeFacts_ShouldPrefillTheScopeTable()
        {
            // 부재를 서술했는지는 자연어 판정이라 앵커가 없다. 표를 미리 채워 주고
            // L1이 행의 보존만 보는 것이 이 설계의 핵심이다(설계 3.1).
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeDmlScopeSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(DmlScopeExtractor.DmlScopeTableHeading, body);
            Assert.Contains("**아니오**", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithDmlScopeFacts_ShouldPrefillTheScopeTable()
        {
            // 지역 모델 경로(GenerateSpecSectionAsync의 "CrudAnalysis" 분기)도 같은 표를
            // 받아야 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
            // BuildSpecificationPrompts를 전혀 호출하지 않는다. 이 테스트는 그 분기
            // 자체를 대상으로 삼는다 - 배선이 이 분기에서만 빠져도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeDmlScopeSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(DmlScopeExtractor.DmlScopeTableHeading, body);
            Assert.Contains("**아니오**", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutDmlStatements_ShouldOmitTheScopeTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(DmlScopeExtractor.DmlScopeTableHeading, body);
        }

        /// <summary>
        /// EXCEPTION_PROC 실행순서 13 실측 형태 - 축 A 🔴. UPDATE의 SET 우변이
        /// ISNULL(X.PGCOMM, 0)에서 멈추고, X 안의 IIF(ISNULL(A.DiscountFlag,'N')='Y', ...)가
        /// 프로모션 건의 원가 기준금액이다. 아래 두 테스트는 DiscountFlag 등 표현식
        /// 안의 식별자로 대조한다 - 원본 DDL에도 우연히 있는 단어가 아니라 파생
        /// 테이블 정의 표에서만 나오는 값이어야 표가 없어도 통과하는 거짓양성을
        /// 피한다.
        /// </summary>
        private static SpDefinition ProbeDerivedTableSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "EXCEPTION_PROC",
                DdlText = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.PGComm = ISNULL(X.PGCOMM, 0)
    FROM   dbo.TSettleMst A
    JOIN   (SELECT PLTID,
                   IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGCOMM
            FROM   dbo.TSettleMst A) X ON X.PLTID = A.PLTID
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithDerivedColumns_ShouldPrefillTheDerivedTableTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeDerivedTableSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(DerivedTableColumnExtractor.DerivedTableHeading, body);
            Assert.Contains("DiscountFlag", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithDerivedColumns_ShouldPrefillTheDerivedTableTable()
        {
            // 지역 모델 경로(GenerateSpecSectionAsync의 "CrudAnalysis" 분기)도 같은 표를
            // 받아야 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
            // BuildSpecificationPrompts를 전혀 호출하지 않는다. 이 테스트는 그 분기
            // 자체를 대상으로 삼는다 - 배선이 이 분기에서만 빠져도 실패해야 한다
            // (비대칭 뮤테이션으로 실측: 이 분기만 끄면 이 테스트만 실패하고 위
            // GenerateSpecificationAsync 테스트는 그대로 통과한다).
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeDerivedTableSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(DerivedTableColumnExtractor.DerivedTableHeading, body);
            Assert.Contains("DiscountFlag", body);
        }

        private static BatchStepPlan ProbeStep(string code) => new(
            Code: code,
            Name: $"{code} 단계",
            LegacyProcedures: Array.Empty<string>(),
            TargetTables: Array.Empty<string>(),
            ErrorCodes: Array.Empty<string>(),
            Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private static readonly List<(string FileName, string Content)> ProbeSpecs =
            new() { ("Spec.md", "# 명세서") };

        // 설계 §4. 어느 단계를 만들든 공유 접두사는 바이트 동일해야 한다.
        // 달라지면 프롬프트 캐시가 전부 미스가 되어 입력 토큰이 18배가 되는데
        // 산출물은 그대로라 코드만 봐서는 알 수 없다.
        [Fact]
        public async Task GenerateBatchStepSection_SharedPrefixIsIdenticalAcrossSteps()
        {
            var steps = new[] { ProbeStep("S05"), ProbeStep("S08") };
            var interfaces = new[]
            {
                new StepInterface("S05", new[] { "dbo.A" }, new[] { "@pi_strYMD varchar(8)" })
            };

            var first = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");
            var second = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[1], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");

            const string marker = "Now write the section";
            // CS8602 경고 회피. UserPrompt는 널 가능 타입이지만 이 호출 경로는
            // 항상 채운다 - 다른 테스트(AiServiceTests.cs)가 쓰는 것과 같은 관용이다.
            Assert.NotNull(first.UserPrompt);
            Assert.NotNull(second.UserPrompt);
            var firstUserPrompt = first.UserPrompt!;
            var secondUserPrompt = second.UserPrompt!;
            Assert.Equal(
                firstUserPrompt.Substring(0, firstUserPrompt.IndexOf(marker, StringComparison.Ordinal)),
                secondUserPrompt.Substring(0, secondUserPrompt.IndexOf(marker, StringComparison.Ordinal)));
        }

        [Fact]
        public async Task GenerateBatchStepSection_CarriesTheControlContractTable()
        {
            var steps = new[] { ProbeStep("S05") };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");

            Assert.Contains("batch.BatchStepJournal", result.UserPrompt);
            Assert.Contains("StepStatus", result.UserPrompt);
            Assert.Contains("Succeeded", result.UserPrompt);
            Assert.Contains("Do NOT invent alternatives", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateBatchStepSection_CarriesTheStepInterfaceTable()
        {
            var steps = new[] { ProbeStep("S05") };
            var interfaces = new[]
            {
                new StepInterface("S05", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                    new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" })
            };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");

            Assert.Contains("@po_intRetVal int OUTPUT", result.UserPrompt);
            Assert.Contains("MUST NOT add", result.UserPrompt);
        }

        // 재료가 없으면 표 절 자체를 넣지 않는다. 빈 표를 넣으면 모델이
        // "원본 파라미터가 없다"로 읽는다.
        [Fact]
        public async Task GenerateBatchStepSection_OmitsTheInterfaceSectionWhenThereIsNoMaterial()
        {
            var steps = new[] { ProbeStep("S05") };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");

            Assert.DoesNotContain("[Original Procedure Interface]", result.UserPrompt);
        }
    }
}
