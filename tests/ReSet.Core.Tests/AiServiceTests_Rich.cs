using System;
using System.Linq;
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
        /// <summary>
        /// SELECT 대상 테이블이 있어야 155행 지시문이 프롬프트에 실린다.
        /// ProbeSpDef는 UPDATE 매핑만 채우므로 이 픽스처를 따로 둔다.
        /// </summary>
        private static SpDefinition SelectProbeSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_SELECT_PROBE",
                DdlText = "SELECT 1;",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_SELECT_PROBE", CodeObjectType.Procedure)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                SelectTables = new List<string> { "SETTLE_POQ_DB.dbo.TClientSettleRate" }
            };
            return spDef;
        }

        /// <summary>
        /// 2026-08-23 ④ 진단. CRUD 표의 설명 칸이 조인 키를 나열하면서 여러 문장을
        /// 한 주장으로 묶었다(실측: `UPDATE 3 및 UPDATE 4에서 YMD, CLIENTID, PGNAME,
        /// MALLID 조인` - UPDATE 4에는 MALLID 조인이 없다). Critic은 그 줄을 검토하고
        /// "accurately captures the join keys"로 통과시켰다 - 존재 검증과 전칭 검증을
        /// 바꿔치기한 것이다.
        ///
        /// 술어와 조인 키는 `DML 범위`·`집합 술어` 표가 문장별로 확정하므로, 설명 칸이
        /// 그것을 나열할 자리를 없애 틀릴 수 있는 주장의 부류 자체를 제거한다.
        /// 참조 함수 동작 서술 금지와 같은 계열이다.
        /// </summary>
        private static void AssertPredicateProseIsDelegatedToTables(string body)
        {
            // 옛 지시문은 조건 서술을 요구했다 - 살아 있으면 새 규칙과 정면으로 어긋난다.
            Assert.DoesNotContain("조건/참조 컬럼과 함께", body);
            Assert.Contains("조인 키와 WHERE 술어를 나열하지 마십시오", body);
            Assert.Contains(DmlScopeExtractor.DmlScopeTableHeading, body);
            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_CrudDescriptionRule_DelegatesPredicatesToTables()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(SelectProbeSpDef(), "지시");

            AssertPredicateProseIsDelegatedToTables(DecodeMessageContents(handler.LastRequestBody));
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_DelegatesPredicatesToTables()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(SelectProbeSpDef(), "CrudAnalysis", "지시");

            AssertPredicateProseIsDelegatedToTables(DecodeMessageContents(handler.LastRequestBody));
        }

        /// <summary>
        /// Actor와 Critic이 같은 `staticAnalysisText`를 받으므로 규칙이 갈라질 수 없다.
        /// 이 테스트가 그 배선을 고정한다 - 한쪽만 바뀌면 이 세션 초반의 교착이 재현된다.
        /// </summary>
        [Fact]
        public async Task ReviewSpecificationAsync_CrudDescriptionRule_ReachesCriticToo()
        {
            var (service, handler) = CreateProbe();

            await service.ReviewSpecificationAsync(SelectProbeSpDef(), "## 개요");

            AssertPredicateProseIsDelegatedToTables(DecodeMessageContents(handler.LastRequestBody));
        }

        /// <summary>
        /// Critic 기준 1은 술어가 "필터로 서술"됐는지 본다. 산문에서 술어를 빼면 그
        /// 요구가 충족되지 않는 것으로 읽혀 교착이 난다 - 기계 확정 표의 술어 원문이
        /// 곧 필터 서술이라는 것을 기준 안에 못 박는다.
        /// </summary>
        [Fact]
        public async Task ReviewSpecificationAsync_Criterion1_AcceptsTableVerbatimAsFilterDescription()
        {
            var (service, handler) = CreateProbe();

            await service.ReviewSpecificationAsync(SelectProbeSpDef(), "## 개요");

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("a machine-confirmed table carries the predicate verbatim", body);
        }

        // [2026-08-23 ③(b) 최종 리뷰 에스컬레이션 2] 기준 1은 "`조회합니다`로 뭉개면 필터
        // 서술이 아니다 - 보고하라"를 먼저 적고 표 면제를 뒤에 붙였다. ③(b)부터 집합 술어
        // 표에 독립 SELECT(`SELECT n`) 행이 실리는데, 그 문장의 술어는 읽는 행을 가르므로
        // "…인 행만 조회합니다"가 옳은 서술이다 - 순서와 주어 그대로면 리터럴한 Critic이
        // 옳은 문장을 보고할 여지가 글자로 남는다(로그 17개에서 발현 0건, 이론적 경로).
        // 이 테스트는 (a) 독립 SELECT 읽기 필터 면제 문장이 있고 (b) 표 면제가 금지
        // 문장보다 앞에 온다는 두 가지를 프롬프트에서 고정한다.
        [Fact]
        public async Task ReviewSpecificationAsync_Criterion1_TreatsStandaloneSelectReadFilterAsFilterDescription()
        {
            var (service, handler) = CreateProbe();

            await service.ReviewSpecificationAsync(SelectProbeSpDef(), "## 개요");

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("For a standalone SELECT statement", body);
            Assert.Contains("narrows the rows that statement reads", body);
            var exemption = body.IndexOf("a machine-confirmed table carries the predicate verbatim", StringComparison.Ordinal);
            var softening = body.IndexOf("is NOT described as a filter: report it", StringComparison.Ordinal);
            Assert.True(exemption >= 0 && softening >= 0 && exemption < softening,
                $"표 면제({exemption})가 금지 문장({softening})보다 앞에 와야 한다.");
        }

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

        // [2026-08-22 재생성 실측] Critic이 기계 확정 표를 세 번 공격했고 세 번 다
        // 틀렸다 - DB 배치를 "지어낸 것", money -> int를 "절사", 건너뛴 IF가
        // "@@ROWCOUNT를 리셋하지 않는다"고 단정했다. 뒤 둘은 로컬 SQL Server 2022로
        // 실측해 표가 옳음을 확인했다(CAST(CAST(12.5 AS money) AS int) = 13, 앞 SELECT가
        // 2행을 낸 뒤 건너뛴 IF 다음 @@ROWCOUNT = 0).
        //
        // Critic은 DDL 본문만 보므로 실행해야 아는 사실의 근거를 못 찾고 환각으로
        // 판정한다. 그러면 L1(CheckExecutionSemantics)이 원문 복원을 요구해 교착이 되고
        // 재시도가 6/6까지 소진된다. Actor 쪽에만 표 계약이 있고 Critic 쪽이 그것을
        // 물려받지 못한 자리였다 - 정적 리뷰로는 안 보이고 재생성을 돌려야 드러난다.
        [Fact]
        public async Task ReviewSpecificationAsync_ProcedurePrompt_ExemptsMachineConfirmedTablesFromHallucinationJudgment()
        {
            var (service, handler) = CreateProbe();

            await service.ReviewSpecificationAsync(ProbeSpDef(Mapping()), "## 개요");

            var body = DecodeMessageContents(handler.LastRequestBody);
            // 블록의 내용 계약은 MachineConfirmedTablesTests가 본다. 여기서는 그 단일
            // 출처가 이 갈래의 프롬프트에 실렸는지만 확인해, 문구가 두 벌로 갈라질
            // 자리를 없앤다. 앞뒤 빈 줄까지 함께 고정하는 것은, 리터럴을 쪼개 이어
            // 붙이는 조립이라 빈 줄이 조용히 사라져 블록 머리글이 앞 절에 붙어버릴 수
            // 있기 때문이다(실제로 한 번 그렇게 됐다).
            Assert.Contains("\n\n" + MachineConfirmedTables.CriticExemptionBlock + "\n\n", body);
        }

        // 함수 갈래는 별도 시스템 프롬프트를 쓴다. 관측된 Critic 공격 세 건이 전부
        // 함수에서 났으므로(UF_GET_INCVTAXRATE, UF_Get_CLComm4MobileCo,
        // UF_GET_COMM4CLIENT) 이 갈래가 빠지면 수정이 정작 필요한 자리에 닿지 않는다.
        [Fact]
        public async Task ReviewSpecificationAsync_FunctionPrompt_ExemptsMachineConfirmedTablesFromHallucinationJudgment()
        {
            var (service, handler) = CreateProbe();
            var functionDef = ProbeSpDef(Mapping());
            functionDef.ObjectType = CodeObjectType.Function;

            await service.ReviewSpecificationAsync(functionDef, "## 개요");

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("\n\n" + MachineConfirmedTables.CriticExemptionBlock + "\n\n", body);
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

        private static SpDefinition SetPredicateSpDefinition() => new()
        {
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.InState = 1
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.PGName NOT IN ('PLCard','SSGPayCard','KakaoCard')
END"
        };

        [Fact]
        public async Task GenerateSpecificationAsync_WithSetPredicate_ShouldRenderTheTable()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(SetPredicateSpDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, body);
            // Column 칸은 원문 표기(한정자 포함) 그대로다 - 픽스처가 A.PGName NOT IN
            // 이므로 A.PGName이다.
            //
            // [Line이 5에서 8로 바뀐 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
            // 라인 칸이 문장 시작줄이 아니라 그 술어 항 자신의 줄이 됐다
            // (SetPredicateFact.Line 문서 참고). 이 픽스처를 세어 보면 @" 다음 줄바꿈
            // 때문에 1번 줄이 빈 줄이고, 2 CREATE PROCEDURE · 3 AS · 4 BEGIN ·
            // 5 UPDATE · 6 FROM · 7 WHERE · 8 AND A.PGName NOT IN (...) 이다.
            // 그래서 UPDATE 문장의 시작줄은 여전히 5지만 이 항의 줄은 8이다.
            //
            // [열이 여덟이 된 이유 - 2026-08-22 축 A 재감사 ③ Task 7, 설계 §5]
            // 「술어 원문」이 마지막 열로 붙었다. 분해된 항은 기존 여섯 칸을 그대로
            // 채우고 원문 칸이 하나 더 붙는다.
            Assert.Contains(
                "| UPDATE 1 | 8 | A.PGName | NOT IN | 최상위 | 3 | "
                + "'PLCard', 'SSGPayCard', 'KakaoCard' | "
                + "A.PGName NOT IN ('PLCard','SSGPayCard','KakaoCard') |",
                body);
            Assert.Contains("| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |", body);
        }

        /// <summary>
        /// 명세서 골격(RequiredHeadersMarkdown/WrapSpec와 같은 모양)을 만든다.
        /// MechanicalValidatorTests의 동명 헬퍼와 같은 골격이지만 그쪽은 private이라
        /// 여기서 재사용할 수 없다 - 이 클래스 안에서만 쓰는 최소 사본이다.
        /// </summary>
        private static string WrapAsSpecMarkdown(string crudBody) =>
            string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

        /// <summary>
        /// 프롬프트 본문에서 헤딩과 그 뒤 표 행들(`|`로 시작하는 줄)만 잘라낸다.
        /// 왕복 테스트가 손으로 지어낸 표가 아니라 AiService가 실제로 낸 렌더를
        /// 그대로 명세서에 붙여넣도록 하기 위함이다 - 그래야 렌더(EscapeTableCell)와
        /// 파서(ExtractSetPredicateLiteralCell)가 실제로 서로 맞물리는지 검증한다.
        /// </summary>
        private static string ExtractTableSection(string? body, string heading)
        {
            Assert.NotNull(body);
            var lines = body!.Split('\n');
            var startIndex = Array.FindIndex(lines, l => l.Trim() == heading);
            Assert.True(startIndex >= 0, $"heading not found in prompt: {heading}");

            var sectionLines = new List<string> { lines[startIndex].TrimStart() };
            for (var i = startIndex + 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("|", StringComparison.Ordinal)) break;
                sectionLines.Add(trimmed);
            }

            return string.Join("\n", sectionLines);
        }

        [Fact]
        public async Task Validate_SetPredicateWithCommaInsideLiteral_ShouldRoundTripThroughTheRenderedTable()
        {
            // Important 1 재현 - Nm IN ('a,b','c')를 칸 안에서 쉼표로 단순 분할하면
            // 렌더된 칸 "'a,b', 'c'"가 {"'a", "b'", "'c'"} 세 조각으로 쪼개져, 기대
            // 리터럴 {"'a,b'", "'c'"}와 절대 맞지 않는다 - 모델이 표를 한 글자도
            // 안 틀리고 그대로 옮겨도 L1이 "누락/추가"를 보고하는, §0이 막으려는
            // 실패 모양이다. 손으로 지어낸 표가 아니라 AiService가 실제로 렌더한
            // 표(ExtractTableSection)를 그대로 명세서에 붙여 왕복시킨다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 WHERE Nm IN ('a,b','c') END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var promptResult = await service.GenerateSpecificationAsync(spDef, "rules");
            var tableSection = ExtractTableSection(promptResult.SystemPrompt, DmlScopeExtractor.SetPredicateTableHeading);
            var markdown = WrapAsSpecMarkdown(tableSection);

            var expectations = SpecExpectations.From(spDef);
            Assert.NotNull(expectations);

            var result = new MechanicalValidator().Validate(markdown, expectations!);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public async Task Validate_SetPredicateWithPipeInsideLiteral_ShouldRoundTripThroughTheRenderedTable()
        {
            // Important 1의 두 번째 사례 - Nm IN ('a|b','c')는 EscapeTableCell이
            // `|`를 `\|`로 이스케이프해 렌더된 칸이 "'a\|b', 'c'"가 된다. 행을
            // 그냥 `|`로 나누면(이스케이프를 모르는 분할) 이 이스케이프된 파이프
            // 위치에서 행 자체가 잘못 쪼개져 리터럴 칸 마지막 조각이 "b'"만 남는다
            // (리뷰 실측: "누락: 'a\|b' / 추가: b'"). 여기서도 손으로 지어낸 표가
            // 아니라 실제 렌더 결과로 왕복시킨다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 WHERE Nm IN ('a|b','c') END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var promptResult = await service.GenerateSpecificationAsync(spDef, "rules");
            var tableSection = ExtractTableSection(promptResult.SystemPrompt, DmlScopeExtractor.SetPredicateTableHeading);
            var markdown = WrapAsSpecMarkdown(tableSection);

            var expectations = SpecExpectations.From(spDef);
            Assert.NotNull(expectations);

            var result = new MechanicalValidator().Validate(markdown, expectations!);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithSetPredicate_ShouldRenderTheTable()
        {
            // 지역 모델의 최초 생성 경로는 BuildSpecificationPrompts를 아예 호출하지
            // 않는다 - Task 4의 Critical이 정확히 이 비대칭이었다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(
                SetPredicateSpDefinition(), "CrudAnalysis", "rules", null);

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
            Assert.Contains("'SSGPayCard'", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_FunctionWithSetPredicate_ShouldRenderTheTable()
        {
            var functionDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "FN_X",
                ObjectType = CodeObjectType.Function,
                DdlText = @"
CREATE FUNCTION dbo.FN_X()
RETURNS @R TABLE (Id INT)
AS
BEGIN
    INSERT INTO @R (Id) VALUES (1)
    DELETE FROM @R WHERE Id IN (7, 8)
    RETURN
END",
                FunctionReturn = new FunctionReturnInfo
                {
                    IsTableValued = true,
                    Columns = new System.Collections.Generic.List<ColumnInfo>
                    {
                        new ColumnInfo { ColumnName = "Id", DataType = "INT", IsNullable = false }
                    }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 함수 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(functionDef, "rules");

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
            Assert.Contains("| DELETE 1 | 7 | Id | IN | 최상위 | 2 |", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutSetPredicate_ShouldNotRenderTheTable()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                // 2026-08-19: 수집 범위가 리터럴 우변 등호까지 넓어져 `WHERE Id = 1`은
                // 이제 사실을 낸다. 이 테스트의 의도는 "낼 사실이 없으면 표를 만들지
                // 않는다"이므로 술어가 정말 없는 형태(파라미터 비교)로 바꾼다.
                //
                // 2026-08-22(축 A 재감사 ③ Task 6): 행 단위가 최상위 AND 항으로 올라가
                // `WHERE Id = @p`도 원문 전용 행을 낸다 - 분해되지 않는 항이 표에서
                // 사라지지 않는 것이 그 작업의 목적이다(설계 §3 결정 3). 그래서 이제
                // "사실이 정말 없는" 형태는 WHERE 자체가 없는 문장뿐이다. 이 테스트의
                // 의도는 그대로이고, 그 의도를 지금도 만족하는 픽스처로 옮긴다.
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
        }

        /// <summary>
        /// 표 본문에서 헤딩 하나를 찾아 그 구간(다음 헤딩 줄 전까지) 안에서 "라인" 칸이
        /// <paramref name="line"/>인 행의 "문장" 칸을 꺼낸다. MechanicalValidator의
        /// CheckDmlScopeTable/CheckSetPredicates가 표를 대조하는 방식과 같은 모양이다 -
        /// 행 전체가 아니라 칸 하나만 본다.
        /// </summary>
        private static string? FindStatementLabelForLine(string? body, string heading, int line)
        {
            if (body == null) return null;

            var headingIndex = body.IndexOf(heading, StringComparison.Ordinal);
            if (headingIndex < 0) return null;

            var afterHeading = body.Substring(headingIndex + heading.Length);
            foreach (var rawLine in afterHeading.Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal)
                    || trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    break;   // 다음 표/섹션에 들어섰다.
                }
                if (!trimmed.StartsWith("|", StringComparison.Ordinal)) continue;

                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 2 || cells[1] != line.ToString()) continue;

                return cells[0];
            }

            return null;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithSetPredicateGapInDmlOperations_ShouldKeepStatementOrdinalsAligned()
        {
            // 리뷰어 재현(FIX ROUND 1) - 같은 연산(UPDATE) 문장 셋 중 가운데 문장만
            // 집합 술어가 없으면(여기서는 스칼라 비교 Id = 1), 집합 술어 표가 채번을
            // 독자적으로 세는 순간 세 번째 문장부터 두 표의 "UPDATE N"이 서로 다른
            // 문장을 가리킨다. 실제 UP_UTIL_SETTLE_COMM_UPD SP의 3번째 UPDATE(98행,
            // 최상위가 서브쿼리 IN뿐이라 집합 사실을 하나도 못 낸다)부터 실제로
            // 벌어진 결함이다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T1 SET C = 1 WHERE PGName IN ('A','B')
    UPDATE dbo.T2 SET C = 1 WHERE Id = 1
    UPDATE dbo.T3 SET C = 1 WHERE UseState IN (0,1)
END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var body = result.SystemPrompt;

            // 세 번째 UPDATE(dbo.T3, 7번 줄)는 DML 범위 표에서 "UPDATE 3"이어야 한다 -
            // 중간에 집합 술어 없는 UPDATE(dbo.T2)가 끼어도 DML 범위 표의 채번은
            // 모든 UPDATE를 센다.
            var dmlScopeLabel = FindStatementLabelForLine(body, DmlScopeExtractor.DmlScopeTableHeading, 7);
            Assert.Equal("UPDATE 3", dmlScopeLabel);

            // 집합 술어 표의 같은 줄(7번) 행도 같은 "UPDATE 3"을 가리켜야 한다 - 두
            // 표가 채번 규칙을 공유하지 않으면 여기서 "UPDATE 2"가 나온다.
            var setPredicateLabel = FindStatementLabelForLine(body, DmlScopeExtractor.SetPredicateTableHeading, 7);
            Assert.Equal(dmlScopeLabel, setPredicateLabel);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithTwoUpdatesOnSameLine_ShouldAssignDistinctOrdinals()
        {
            // 리뷰어 재현(FIX ROUND 2) - `e14a7a4`가 DML 범위 표와 집합 술어 표의 채번을
            // `Dictionary<(Operation, Line), int>` 하나로 통합했는데, 같은 물리 줄에
            // 같은 연산(UPDATE) 문장이 둘이면 그 키가 충돌한다: 두 문장 모두
            // (UPDATE, 같은 줄) 키를 쓰므로 나중 문장이 쓴 번호가 앞 문장의 번호를
            // 덮어써서 "UPDATE 1"이 사라지고 서로 다른 대상 테이블 둘이 나란히
            // "UPDATE 2"로 찍혔다 - 이미 배포된 기계 확정 표를 조용히 퇴행시키는
            // 결함이다. 문장의 정체성(목록 안 자리)으로 세면 같은 줄이어도 서로
            // 다른 번호를 받는다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T1 SET C = 1 WHERE Id = 1; UPDATE dbo.T2 SET C = 1 WHERE Id = 2
END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var body = result.SystemPrompt;

            // 두 UPDATE 모두 5번 줄(픽스처의 `@"` 다음 줄바꿈으로 1번 줄이 비므로
            // CREATE PROCEDURE가 2번 줄부터 시작 - 위 테스트와 같은 계산)에서
            // 시작하지만, 목록 안 자리는 다르므로 "UPDATE 1"과 "UPDATE 2"로 갈려야
            // 한다.
            Assert.Contains("| UPDATE 1 | 5 | dbo.T1 |", body);
            Assert.Contains("| UPDATE 2 | 5 | dbo.T2 |", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithTwoSetPredicatesOnSameLine_ShouldKeepBothTablesAligned()
        {
            // 재리뷰 재현(FIX ROUND 3) - 위 테스트(둘 다 집합 술어 없음)만으로는
            // 잡히지 않는 결함이다. 같은 줄에 UPDATE 둘(T2·T3)이 있고 <b>둘 다</b>
            // 집합 술어를 가지면, FIX ROUND 2의 `FirstByKey`((연산, 라인) 키의 첫
            // 문장 번호를 집합 술어 표가 "빌려 쓰는" 방식)는 두 술어 행 모두에
            // 같은 번호(그 줄의 첫 문장 번호, 여기서는 T2의 "UPDATE 2")를 붙였다 -
            // T3의 집합 술어 행이 "UPDATE 3"이 아니라 "UPDATE 2"로 찍혀 DML 범위
            // 표의 T2를 가리키는 거짓 귀속이 났다. 이 테스트는 DML 범위 표와 집합
            // 술어 표를 모두 단언해, 같은 대상(T2/T3)의 문장 번호가 두 표에서
            // 일치하는지를 직접 대조한다 - 앞 테스트가 DML 표만 봐서 놓친 지점이다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T1 SET C = 1 WHERE PGName IN ('a','b')
    UPDATE dbo.T2 SET C = 1 WHERE Id IN (1,2); UPDATE dbo.T3 SET C = 1 WHERE Id IN (3,4,5)
END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var body = result.SystemPrompt;

            // DML 범위 표: 목록 안 자리로 센 번호 - T1=1(5번 줄), T2=2·T3=3(둘 다
            // 6번 줄을 공유하지만 자리가 다르다).
            Assert.Contains("| UPDATE 1 | 5 | dbo.T1 |", body);
            Assert.Contains("| UPDATE 2 | 6 | dbo.T2 |", body);
            Assert.Contains("| UPDATE 3 | 6 | dbo.T3 |", body);

            // 집합 술어 표: 같은 대상의 리터럴 행이 DML 범위 표와 같은 문장 번호를
            // 가리켜야 한다. T2의 리터럴(1, 2)이 "UPDATE 2"를, T3의 리터럴(3, 4, 5)이
            // "UPDATE 3"을 가리키지 않으면(예: 둘 다 "UPDATE 2") 표 사이 귀속이
            // 깨진 것이다.
            Assert.Contains("| UPDATE 1 | 5 | PGName | IN | 최상위 | 2 | 'a', 'b' |", body);
            Assert.Contains("| UPDATE 2 | 6 | Id | IN | 최상위 | 2 | 1, 2 |", body);
            Assert.Contains("| UPDATE 3 | 6 | Id | IN | 최상위 | 3 | 3, 4, 5 |", body);
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

        private static async Task<string> StepSystemPromptAsync()
        {
            var steps = new[] { ProbeStep("S05") };
            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");
            // CS8603 경고 회피. SystemPrompt는 널 가능 타입이지만 이 호출 경로는
            // 항상 채운다 - 위 GenerateBatchStepSection_* 테스트가 쓰는 것과 같은 관용이다.
            Assert.NotNull(result.SystemPrompt);
            return result.SystemPrompt!;
        }

        // 규칙 5가 @pi_bypassPreCheck를 발명해 명령했고, S02가 재시작 모드에서
        // 실행 컨텍스트 전체에 그 값을 참으로 고정해 지급 확정 원장의 -9 하드
        // 스톱이 통째로 사라졌다(감사 🔴).
        [Fact]
        public async Task ConsolidatedPlanRules_DoNotInventABypassParameter()
        {
            Assert.DoesNotContain("@pi_bypassPreCheck", await StepSystemPromptAsync());
        }

        [Fact]
        public async Task ConsolidatedPlanRules_MoveRestartSkipOutsideTheStep()
        {
            var rules = await StepSystemPromptAsync();

            Assert.Contains("orchestrator", rules, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MUST NOT add an input parameter", rules);
            Assert.Contains("unconditionally", rules);
        }

        // Few-Shot의 CATCH가 THROW로 끝나 규칙 6-1(상태 변수를 반환하라)과
        // 규칙 13(출력 파라미터를 누락 없이 매핑하라)을 무력화했다. 모델은
        // 산문 규칙보다 코드 예시를 따른다 - 실측 5건이 그렇게 나왔다.
        [Fact]
        public async Task ConsolidatedPlanRules_FewShotCatchReturnsInsteadOfRethrowing()
        {
            var rules = await StepSystemPromptAsync();
            var open = rules.IndexOf("BEGIN CATCH", StringComparison.Ordinal);
            var close = rules.IndexOf("END CATCH", open, StringComparison.Ordinal);
            var catchBlock = rules[open..close];

            Assert.DoesNotContain("THROW;", catchBlock);
            Assert.Contains("RETURN", catchBlock);
        }

        [Fact]
        public async Task ConsolidatedPlanRules_MakeShadowALastResortWithThreeMechanics()
        {
            var rules = await StepSystemPromptAsync();

            Assert.Contains("LAST RESORT", rules);
            Assert.Contains("BEFORE `BEGIN TRAN`", rules);
            Assert.Contains("same range", rules);
            Assert.Contains("sp_executesql", rules);
        }

        [Fact]
        public async Task ConsolidatedPlanRules_ForbidCrossJoinInReconciliationSql()
        {
            Assert.Contains(
                "NEVER compare two aggregates with `CROSS JOIN`", await StepSystemPromptAsync());
        }

        // 규칙 4 수술(2026-08-18)이 옛 (a)(다중 테이블 커버리지)와 (b)(퍼지 정책)를
        // 판정 트리로 바꾸며 대체 없이 지웠다(코드 리뷰 Important). 계획서가 규칙 4를
        // 다시 쓴 의도는 그림자를 마지막 수단으로 좁히는 것이었지 이 둘을 버리는
        // 것이 아니었으므로, 그림자를 쓰기로 한 가지 안에 되살린다. 일부 테이블만
        // 덮으면 복원이 반쪽짜리가 되어 롤백 안 한 것보다 더 나쁜 불일치 상태를
        // 만든다.
        [Fact]
        public async Task ConsolidatedPlanRules_ShadowMustCoverAllModifiedTargetTables()
        {
            Assert.Contains(
                "the shadow strategy MUST cover ALL", await StepSystemPromptAsync());
        }

        // 그림자 테이블은 배치 전용 스키마에 계속 쌓이는 물리 객체다. 퍼지 정책
        // 없이 두면 저장 공간을 영구히 잠식한다(코드 리뷰 Important). 그림자를
        // 쓰기로 한 가지 안에 수명·정리 지시를 되살린다.
        [Fact]
        public async Task ConsolidatedPlanRules_ShadowMustDefineAPurgePolicy()
        {
            Assert.Contains("purge policy", await StepSystemPromptAsync());
        }

        private static SpDefinition ReferencedFunctionSpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM)
                ,A.PGCOMM = SETTLE_CARD_DB.dbo.UF_GET_COMM4PG(A.CPID)
    FROM   dbo.TSettleMst A
END",
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = null, Schema = "dbo", Name = "UF_GET_ROUND4VAT", Type = "SQL_SCALAR_FUNCTION" },
                new() { Database = "SETTLE_CARD_DB", Schema = "dbo", Name = "UF_GET_COMM4PG", Type = "SQL_SCALAR_FUNCTION" },
                new() { Database = null, Schema = "dbo", Name = "TSettleMst", Type = "USER_TABLE" }
            }
        };

        [Fact]
        public async Task GenerateSpecification_ShouldRenderReferencedFunctionTable()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading, body);
            // 로컬 함수와 외부 DB 함수의 링크 경로가 다르다.
            Assert.Contains("../../../Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md", body);
            Assert.Contains("../../../External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md", body);
            // 테이블은 이 표에 실리지 않는다.
            var section = ExtractTableSection(body, DmlScopeExtractor.ReferencedFunctionTableHeading);
            Assert.DoesNotContain("TSettleMst", section);
        }

        [Fact]
        public async Task ReferencedFunctionTable_HeaderAndSeparator_ShouldHaveSameColumnCount()
        {
            // 2026-08-20 축 A 감사에서 헤더와 구분자 열 수가 어긋나 GFM이 표를
            // 통째로 렌더하지 못한 결함이 두 번 나왔다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.ReferencedFunctionTableHeading);

            var rows = section.Split('\n')
                              .Select(l => l.Trim())
                              .Where(l => l.StartsWith("|"))
                              .ToList();

            Assert.True(rows.Count >= 2, "표에 헤더와 구분자 행이 있어야 한다.");
            Assert.Equal(rows[0].Count(c => c == '|'), rows[1].Count(c => c == '|'));
        }

        [Fact]
        public async Task GenerateSpecification_NoFunctionCalls_ShouldOmitTable()
        {
            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                Dependencies = new List<DependencyInfo>()
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain(DmlScopeExtractor.ReferencedFunctionTableHeading, result.SystemPrompt);
        }

        // ---------------------------------------------------------------
        // Task 3(범위 확장): 함수 서술 금지 계약 - 네 자리 모두.
        // 컨트롤러 판정 R3: 배선 경로가 하나가 아니라 셋, 지시 자리가 셋이 아니라
        // 넷이다. 아래 테스트들은 각 자리를 그 자리를 실제로 태우는 공개 API로
        // 개별 검증한다 - 한 경로만 통과하고 나머지는 옛 문구가 남는 재발을 막는다.
        // ---------------------------------------------------------------

        [Fact]
        public async Task GenerateSpecification_ShouldForbidDescribingFunctionBehaviour()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var body = result.SystemPrompt;

            // 옛 지시는 함수 로직을 분석하라고 시켰다.
            Assert.DoesNotContain("analyze its logic", body);
            // 새 계약은 문서 어디에서도 서술을 금지한다 - 이 픽스처는 실제로 함수를
            // 호출하므로(ReferencedFunctionSpDefinition) 참조 함수 표가 렌더되고,
            // 그 표 도입문이 이 핵심 문구를 담고 있다.
            // [I3 - 2026-08-20 최종 전체 브랜치 리뷰] 표 도입문의 주어를 "any
            // function"에서 "표에 실린 함수"로 좁혔다 - 함수 명세서 프롬프트에도
            // 같은 문구가 붙는데, 그 프롬프트의 필수 규칙 2는 함수 자신의 동작
            // 서술을 요구하므로 주어가 무제한이면 두 지시가 정면 충돌한다.
            Assert.Contains("do NOT describe the behaviour of any function listed in this table", body);
        }

        [Fact]
        public async Task GenerateSpecification_RuleA_ShouldForbidDescribingUdfBehaviour_EvenWithoutTheTable()
        {
            // 위 테스트는 참조 함수 표(Task 2가 배선)를 통해 핵심 문구를 확인한다 -
            // 지시 A 문구 자체가 바뀌었는지는 별도로 확인해야 한다. hasUdf는 참이지만
            // (StaticAnalysis.ReferencedFunctions) DDL에는 실제 함수 호출 구문이 없어
            // ExtractFunctionCalls가 0건을 내고 표가 렌더되지 않는 픽스처를 쓴다 -
            // 그러면 남는 것은 지시 A 문구뿐이다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            spDef.StaticAnalysis.ReferencedFunctions.Add("UF_UNUSED_IN_DDL");

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var body = result.SystemPrompt;

            Assert.DoesNotContain(DmlScopeExtractor.ReferencedFunctionTableHeading, body);
            Assert.DoesNotContain("analyze its logic", body);
            Assert.Contains("Do NOT describe what any referenced User Defined Function (UDF) does", body);
            // 수정 라운드 1/5(R3 재판정) - A에도 D와 같은 핵심 리터럴 구절이 있어야 한다.
            // hasUdf(StaticAnalysis)와 표(ExtractFunctionCalls)는 독립적으로 실패할 수
            // 있으므로, 표가 없을 때도 이 리터럴이 남아 있어야 계약이 문서 전체에서
            // 하나도 안 남는 사태를 막는다.
            Assert.Contains("do NOT describe any function's behaviour", body);
            Assert.Contains("belongs only in that function's own Spec.md", body);
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_Monolithic_ShouldForbidDetailingUdfFormulas()
        {
            // 지시 C(:1938, BuildDeconstructionPrompts) - 비-지역 provider는 청크 분할
            // 없이 이 경로 하나로 DeconstructedLogic을 만든다. 그 결과가
            // <deconstructed-logic-source-of-truth>로 명세서 생성 프롬프트에 되돌아오므로
            // 이 자리를 놓치면 함수 공식이 다른 경로로 돌아온다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = dbo.UF_X(C) END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"Logic\\\":{\\\"Steps\\\":[]}}\"}}]}";
            var handler = new MockHttpMessageHandler(mockResponse);
            var client = new OpenAiClient(new HttpClient(handler), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.DeconstructSpLogicAsync(spDef, "지침");

            Assert.DoesNotContain("and detail their formulas, especially for calculations like CLVT and PGVT", handler.LastRequestBody);
            Assert.Contains("Identify all referenced User Defined Functions (UDFs) by name and calling location only.", handler.LastRequestBody);
        }

        [Fact]
        public async Task DeconstructSpLogicAsync_Chunked_ShouldForbidDetailingUdfFormulas()
        {
            // 지시 B(:1762, BuildChunkDeconstructionPrompts) - 지역 provider +
            // AST 분할이 켜지면(기본값) 이 경로로 문장 단위 청크마다 DeconstructedLogic을
            // 만든다. AiResult.SystemPrompt는 청크 파이프라인에서 고정 문자열로
            // 대체되므로("AST Chunking Pipeline Used") 실제로 전송된 HTTP 요청 본문을
            // 봐야 이 자리를 검증할 수 있다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                DdlText = "UPDATE dbo.TableA SET Col1 = dbo.UF_X(Col1);"
            };
            var mockResponseContent = "{\"Logic\":{\"Steps\":[]}}";
            var ollamaJson = $"{{\"message\":{{\"role\":\"assistant\",\"content\":\"{mockResponseContent.Replace("\"", "\\\"")}\"}}}}";
            var handler = new MockHttpMessageHandler(ollamaJson);
            var client = new ReSet.Core.Services.Clients.OllamaClient(new HttpClient(handler), "http://localhost:11434", "llama3");
            IAiService service = new AiService(client, 0.2f);

            await service.DeconstructSpLogicAsync(spDef, "지침");

            Assert.DoesNotContain("and detail their formulas, especially for calculations like CLVT and PGVT", handler.LastRequestBody);
            Assert.Contains("Identify all referenced User Defined Functions (UDFs) by name and calling location only.", handler.LastRequestBody);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldForbidDescribingUdfBehaviour()
        {
            // 지시 D(:2224, BuildSpecSectionPrompts의 "CrudAnalysis" 분기) - 브리프가
            // 몰랐던 자리. 지역 모델의 SP 명세서 최초 생성 경로다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            spDef.StaticAnalysis.ReferencedFunctions.Add("UF_UNUSED_IN_DDL");

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "CrudAnalysis", "rules", null);
            var body = result.SystemPrompt;

            Assert.DoesNotContain("analyze its operation", body);
            Assert.Contains("do NOT describe any function's behaviour", body);
        }

        [Fact]
        public async Task GenerateFunctionSpecification_ShouldRenderReferencedFunctionTable()
        {
            // 배선 경로 1/2: 함수 명세서 경로(BuildFunctionSpecificationPrompts,
            // functionDef 변수를 쓰는 systemPrompt += 관례). 함수가 다른 함수를 부를 때
            // (예: 스칼라 함수가 헬퍼 함수를 호출) 참조 함수 표를 받아야 한다.
            // ExtractFunctionCalls(Task 1의 ReferencedFunctionVisitor)는 UPDATE/
            // DELETE/INSERT 문만 훑는다 - 스칼라 함수의 RETURN 식은 잡지 않는다. 그래서
            // 다중 문장 TVF가 자신의 반환 테이블 변수에 거는 UPDATE로 함수를 호출하는
            // 모양을 쓴다(BuildFunctionSpecificationPrompts 위 주석의 Fix Round 2가
            // 실측한 바로 그 패턴).
            var functionDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_OUTER", CodeObjectType.Function),
                Schema = "dbo",
                Name = "UF_OUTER",
                ObjectType = CodeObjectType.Function,
                DdlText = @"
CREATE FUNCTION dbo.UF_OUTER()
RETURNS @Result TABLE (Val INT)
AS
BEGIN
    UPDATE @Result SET Val = dbo.UF_GET_ROUND4VAT(Val)
    RETURN
END",
                Dependencies = new List<DependencyInfo>
                {
                    new() { Database = null, Schema = "dbo", Name = "UF_GET_ROUND4VAT", Type = "SQL_SCALAR_FUNCTION" }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(functionDef, "rules");

            Assert.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading, result.SystemPrompt);
            Assert.Contains("../../../Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldRenderReferencedFunctionTable()
        {
            // 배선 경로 2/2: 구역 분할 경로(BuildSpecSectionPrompts의 "CrudAnalysis"
            // 분기, sbRules.AddRange 관례). VerificationPipelineOrchestrator의 지역
            // 모델 흐름(IsLocalProvider && ObjectType == Procedure)이 실제로 쓰는
            // 경로라 여기를 놓치면 지역 모델로 만드는 SP 명세서는 보호가 전혀 없다.
            var result = await new AiService(
                new OpenAiClient(new HttpClient(new MockHttpMessageHandler(
                    "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}")), "k", "https://api.openai.com/v1", "gpt-4o"),
                0.2f)
                .GenerateSpecSectionAsync(ReferencedFunctionSpDefinition(), "CrudAnalysis", "rules", null);

            Assert.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading, result.SystemPrompt);
            Assert.Contains("../../../Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md", result.SystemPrompt);
            Assert.Contains("../../../External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md", result.SystemPrompt);
        }

        // ---------------------------------------------------------------
        // 수정 라운드 1/5(R3 재판정) - 컨트롤러가 grep "UDF|User Defined Function"
        // 전수 검색으로 확정한 완결 목록 중 나머지 세 자리(체크리스트 둘, CRUD
        // 규칙 2). Critic 프롬프트(:2556)는 AiServiceTests.cs에서 덮는다.
        // ---------------------------------------------------------------

        [Fact]
        public async Task GenerateSpecificationAsync_Checklist_ShouldAskForCallingLocationNotBusinessRule()
        {
            // 자리 2(:538) - "활용 비즈니스 규칙을 명확히 기재하셨습니까?"는 서술을
            // 요구하는 체크리스트였다. 새 계약은 호출 위치·인자만 묻는다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            spDef.StaticAnalysis.ReferencedFunctions.Add("UF_X");

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain("활용 비즈니스 규칙을 명확히 기재하셨습니까", result.UserPrompt);
            Assert.Contains("호출 위치와 인자를 명확히 기재하셨습니까", result.UserPrompt);
            Assert.Contains("동작·반환값 서술은 금지됩니다", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_Checklist_ShouldAskForCallingLocationNotBusinessRule()
        {
            // 자리 3(:2345) - 같은 체크리스트의 구역 분할 경로 사본.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            spDef.StaticAnalysis.ReferencedFunctions.Add("UF_X");

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "CrudAnalysis", "rules", null);

            Assert.DoesNotContain("활용 비즈니스 규칙을 명확히 기재하셨습니까", result.UserPrompt);
            Assert.Contains("호출 위치와 인자를 명확히 기재하셨습니까", result.UserPrompt);
            Assert.Contains("동작·반환값 서술은 금지됩니다", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_Rule2_ShouldNotDemandComputationDocumentation()
        {
            // 자리 4(:2223) - 내가 처음 범위 밖으로 보고했던 자리. 앞 문장(부재 오기
            // 금지)은 그대로 두고 뒤 문장("Analyze the exact computation ... document
            // it fully")만 계약에 맞게 바꿨는지 확인한다. hasUdf와 무관하게 항상 렌더되는
            // 고정 규칙이므로 StaticAnalysis 없이도 검증할 수 있다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "CrudAnalysis", "rules", null);
            var body = result.SystemPrompt;

            // 부재 오기 금지 문장은 살아 있어야 한다.
            Assert.Contains("You must NEVER skip or declare a referenced UDF as 'not called'", body);
            // 계산식 문서화 요구는 사라져야 한다.
            Assert.DoesNotContain("Analyze the exact computation", body);
            Assert.DoesNotContain("document it fully", body);
            Assert.Contains("do NOT describe any function's behaviour or document its computation", body);
        }

        // ---------------------------------------------------------------
        // 2026-08-20 최종 전체 브랜치 리뷰(dabdd03..01b7afb) - 다섯 자리 수정.
        // ---------------------------------------------------------------

        // I1 - LogicAndVisualization 분기(지역 모델 경로의 「로직 흐름 요약」
        // 생성)에는 hasUdf 분기가 아예 없었다. 그런데 이 분기는
        // <referenced-ddl-source-code>(UDF DDL 전문)를 그대로 받으므로, UDF
        // 소스를 손에 쥔 채 아무 계약도 없이 서술할 수 있었다. 지금까지 이
        // 분기를 직접 태우는 계약 테스트가 없었다.
        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_ShouldForbidDescribingUdfBehaviour()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = dbo.UF_X(C) END",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            spDef.StaticAnalysis.ReferencedFunctions.Add("UF_X");

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 로직 흐름 요약\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "rules", null);
            var body = result.SystemPrompt;

            Assert.Contains("do NOT describe any function's behaviour", body);
            Assert.Contains("belongs only in the function's own Spec.md", body);
        }

        // I1 대조군 - hasUdf가 거짓이면(참조 함수가 전혀 없으면) 이 규칙 자체가
        // 렌더되지 않아야 한다. 무조건 렌더되는 문구라면 조건부 렌더링이 아니라는
        // 뜻이므로 이 테스트가 구별해 준다.
        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_WithoutUdf_ShouldOmitTheRule()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 로직 흐름 요약\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "rules", null);

            Assert.DoesNotContain("do NOT describe any function's behaviour", result.SystemPrompt);
        }

        // ---------------------------------------------------------------
        // 2026-08-21 최종 전체 브랜치 리뷰 - Important 2 (재라운드 ①이 교체).
        // ---------------------------------------------------------------

        // ①(재라운드) - 라운드 1의 "CRUD 분석 절에 이미 실린 표를 근거로 쓰라"는
        // 포인터 지시는 이행 불가였다: 구역 분할 경로는 세 절을 병렬로 생성하므로
        // (VerificationPipelineOrchestrator.cs:1328-1340, :1450-1462) LogicAndVisualization
        // 분기 실행 시점에 CRUD 분석 절은 존재하지 않는다. 지금은 표를 가리키는 대신
        // 잠금 힌트 사실 자체를 인라인 재료로 준다 - 그 재료가 이 분기의 프롬프트
        // 안에 직접 있어야 한다.
        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_ShouldReceiveLockHintFactsInline()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE A SET A.C = 1 FROM dbo.T A WITH (NOLOCK) END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 로직 흐름 요약\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "rules", null);
            var body = result.SystemPrompt;

            // 사실 자체(테이블·힌트)가 프롬프트 문자열에 직접 있어야 한다 - "표를
            // 참고하라"는 포인터만으로는 불충분하다(이행 불가였던 그 실패).
            Assert.Contains("transaction isolation implications", body);
            Assert.Contains("dbo.T", body);
            Assert.Contains("NOLOCK", body);
            // 이 분기가 CRUD 분석 절의 표를 대신 출력하면 안 된다 - 재료임을 명시한다.
            Assert.Contains("Do NOT output", body);
            // 재료 밖(커서 등)의 힌트를 억제하지 말라는 범위 한정이 함께 실려야
            // axis-a.md와 반대 방향을 지시하지 않는다.
            Assert.Contains("do NOT suppress or omit lock-hint statements", body);
            Assert.Contains("cursor declarations", body);
        }

        // ①(재라운드) - 감사 🟡이 실제로 난 절은 `## 개요`다(Spec.md:33,
        // UP_Util_Settle_Summary_AcqManual의 커서 NOLOCK 누락). 그런데
        // OverviewAndParameters 분기는 잠금 힌트를 서술하라는 명시적 규칙이 없어도
        // 개요 산문이 스캔 방식을 요약하며 언급할 수 있다 - 이 분기도 근거 재료를
        // 받아야 한다.
        [Fact]
        public async Task GenerateSpecSectionAsync_OverviewAndParameters_ShouldReceiveLockHintFactsInline()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE A SET A.C = 1 FROM dbo.T A WITH (NOLOCK) END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", "rules", null);
            var body = result.SystemPrompt;

            Assert.Contains("dbo.T", body);
            Assert.Contains("NOLOCK", body);
            Assert.Contains("Do NOT output", body);
        }

        // ①(재라운드) 대조군 - 잠금 힌트가 전혀 없으면 두 분기 모두 이 재료 자체를
        // 렌더하지 않아야 한다(무조건 렌더라면 조건부가 아니라는 뜻).
        [Fact]
        public async Task GenerateSpecSectionAsync_WithoutLockHints_ShouldOmitReferenceMaterialInBothBranches()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 WHERE ID = 1 END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var overview = await service.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", "rules", null);
            var logic = await service.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", "rules", null);

            Assert.DoesNotContain("REFERENCE - lock hint facts", overview.SystemPrompt);
            Assert.DoesNotContain("REFERENCE - lock hint facts", logic.SystemPrompt);
        }

        // I2 - 「참조 함수」 표는 dep.Database가 있어도 이제까지 버려 왔다. 크로스 DB
        // 함수(예: SETTLE_CARD_DB.dbo.UF_GET_COMM4PG)가 로컬 함수와 구분되지 않는
        // "dbo.UF_GET_COMM4PG"로 실려, 규칙 6(3부 식별자 판단은 <sp-source-ddl>만
        // 근거로 하라)이 금지한 형태를 기계 표가 먼저 제공했다. 3부로 렌더되는지
        // 직접 단언한다(기존 테스트들은 링크 경로만 확인했지 표시 문구의 3부
        // 여부는 확인하지 않았다).
        [Fact]
        public async Task GenerateSpecification_ReferencedFunctionTable_ShouldQualifyCrossDatabaseFunctionWithDatabaseName()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.ReferencedFunctionTableHeading);

            // 크로스 DB 함수는 3부(Database.Schema.Name)로 실려야 한다.
            Assert.Contains("| SETTLE_CARD_DB.dbo.UF_GET_COMM4PG |", section);
            // 로컬 함수는 그대로 2부(Schema.Name)여야 한다 - dep.Database가 null.
            Assert.Contains("| dbo.UF_GET_ROUND4VAT |", section);
        }

        // R7 - I2가 dep.Database를 표시에 쓰기 시작하면, 잘못 고른 dep이 DB
        // 한정자까지 잘못 표시하는 피해로 이어진다. call.QualifiedName은 스칼라
        // 함수 호출에서는 한정자를 담지 않는다(ScriptDom의 FunctionCall.FunctionName이
        // 한정자를 안 담기 때문 - DmlScopeExtractorTests.ExtractFunctionCalls_ScalarCall_ShouldReportBareName
        // 참고) - 한정자가 실리는 유일한 경로는 인라인 TVF(FROM 절의
        // SchemaObjectFunctionTableReference)다. 그래서 이 픽스처는 인라인 TVF
        // 호출을 쓴다. 같은 이름의 함수가 로컬과 외부 DB에 둘 다 있고, 호출문은
        // 외부 DB를 명시적으로 3부 한정한다 - 마지막 조각(함수 이름)만으로
        // 대조하던 예전 로직이라면 Dependencies 목록의 첫 항목(로컬)을 잘못
        // 골라 "dbo.UF_DUP"로 표시했을 것이다. 한정자가 있으면 그 한정자로
        // 먼저 대조해야 올바르게 "OTHER_DB.dbo.UF_DUP"를 고른다.
        [Fact]
        public async Task GenerateSpecification_ReferencedFunctionTable_ShouldMatchQualifiedCallOverLastSegmentWhenNamesCollide()
        {
            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.OutVal = (SELECT V FROM OTHER_DB.dbo.UF_DUP(A.C))
    FROM   dbo.TSettleMst A
END",
                Dependencies = new List<DependencyInfo>
                {
                    // 로컬 동명 함수가 목록에서 먼저 온다 - 옛 LastSegment-only 대조라면
                    // 이 항목을 먼저 잡는다.
                    new() { Database = null, Schema = "dbo", Name = "UF_DUP", Type = "SQL_SCALAR_FUNCTION" },
                    new() { Database = "OTHER_DB", Schema = "dbo", Name = "UF_DUP", Type = "SQL_SCALAR_FUNCTION" },
                    new() { Database = null, Schema = "dbo", Name = "TSettleMst", Type = "USER_TABLE" }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.ReferencedFunctionTableHeading);

            Assert.Contains("| OTHER_DB.dbo.UF_DUP |", section);
            Assert.DoesNotContain("| dbo.UF_DUP |", section);
            // 링크도 외부 DB 함수의 폴더로 가야 한다.
            Assert.Contains("../../../External/OTHER_DB/Functions/dbo.UF_DUP/docs/Spec.md", result.SystemPrompt);
        }

        // M2 - 도입문이 표를 놓을 절을 지정하지 않았다. 다른 두 표
        // (DmlScopeTableIntroText, DerivedTableIntroText)는 `## CRUD 분석`을
        // 명시하는데 이 표만 "the document"라고만 했다.
        [Fact]
        public async Task GenerateSpecification_ReferencedFunctionTable_IntroText_ShouldNameTheCrudAnalysisSection()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");

            Assert.Contains("Copy this table verbatim into `## CRUD 분석`", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecification_ErrorCodeChecklist_ShouldUseRealDdlLineNumbers()
        {
            // 2026-08-20 실측(STAT_PGCOLLECT_INS): 체크리스트가 오류값 설정 위치를
            // "Line 20"·"Line 104"로 알렸는데 원본의 실제 줄은 27·116이었다. 그 SP의
            // 빈 줄이 14개이고, 스캔이 빈 줄을 버리고 세면서 번호가 그만큼 밀렸다.
            // LLM은 받은 번호를 충실히 옮겨 명세서 앵커가 원본과 어긋났다.
            //
            // 아래 DDL은 SET 앞에 빈 줄을 두어 그 오프셋을 재현한다. @po_intRetVal
            // 대입은 8행에 있다(1행이 빈 줄).
            var ddl = "\nCREATE PROCEDURE dbo.P\n    @po_intRetVal INT OUTPUT\nAS\nBEGIN\n\n    UPDATE dbo.T SET C = 1\n    SET @po_intRetVal = -1\nEND";

            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = ddl
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            // 리터럴 8로 못 박는다. 픽스처에서 다시 계산해 그 값을 단언에 끼워 넣으면
            // 두 번째 단언이 첫 번째가 방금 고정한 것을 되풀이할 뿐이다.
            Assert.Contains("Line 8: SET @po_intRetVal = -1", result.UserPrompt);
        }

        // ---------------------------------------------------------------
        // Task 4(2026-08-21 machine-facts): 잠금 힌트·ORDER BY·객체 선언 표 배선.
        // 배선 지점은 조정자가 grep으로 전수로 뽑아 셋(잠금 힌트) + 둘(객체 선언)로
        // 확정했다 - 아래 테스트는 그 각 지점을 실제로 태우는 공개 API로 개별
        // 검증한다. 한 경로만 배선하고 나머지를 빠뜨려도 해당 테스트만 실패해야
        // 한다(비대칭 뮤테이션으로 실측할 수 있는 구조).
        // ---------------------------------------------------------------

        private static SpDefinition LockHintSpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE T1
    FROM   dbo.TSettleMst T1

    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.UseState = 0
END"
        };

        [Fact]
        public async Task GenerateSpecification_ShouldRenderTheLockHintTable()
        {
            // 배선 지점 1/3: SP 최초 생성 경로(BuildSpecificationPrompts). 감사가
            // 잡은 결함(INS_EXTRA4PLCARD: 같은 테이블이 별칭마다 NOLOCK 유무가
            // 갈리는데 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갬)은 힌트
            // 있는 행과 없는 행이 한 표에 나란히 서야 드러난다 - 그래서 DELETE의
            // 힌트 없는 FROM 참조와 UPDATE의 NOLOCK 참조를 한 픽스처에 함께 둔다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(LockHintSpDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.LockHintTableHeading, body);
            Assert.Contains("| UPDATE 1 |", body);
            Assert.Contains("NOLOCK", body);
            Assert.Contains("최상위", body);
            // DELETE의 FROM 참조(T1)는 힌트가 없다 - "(없음)"이 렌더되어야 그 자리가
            // 표에서 조용히 빠지지 않고 "힌트 없음"이 확정 사실로 남는다.
            Assert.Contains("| DELETE 1 |", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldRenderTheLockHintTable()
        {
            // 배선 지점 2/3: 지역 모델의 SP 명세서 최초 생성 경로
            // (BuildSpecSectionPrompts의 "CrudAnalysis" 분기). 이 흐름은
            // BuildSpecificationPrompts를 전혀 호출하지 않으므로 별도로 검증해야
            // 한다 - 위 테스트만 통과하고 이 분기가 빠져도 잡아내야 한다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(
                LockHintSpDefinition(), "CrudAnalysis", "rules", null);
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.LockHintTableHeading, body);
            Assert.Contains("| UPDATE 1 |", body);
            Assert.Contains("NOLOCK", body);
        }

        [Fact]
        public async Task GenerateSpecification_NoLockHintScans_ShouldOmitTheTable()
        {
            // FROM도 없고 대상에 힌트도 없는 문장은 스캔할 자리가 없다는 뜻이라 이
            // 표에 빈 행을 만들지 않는다 - DmlScopeExtractor의
            // ExtractLockHints_StatementWithNoScan_ProducesNoRow와 짝이 되는
            // 배선 테스트다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 WHERE X = 1 END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain(DmlScopeExtractor.LockHintTableHeading, result.SystemPrompt);
        }

        private static SpDefinition FunctionWithLockHintDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_MULTI", CodeObjectType.Function),
            Schema = "dbo",
            Name = "UF_MULTI",
            ObjectType = CodeObjectType.Function,
            DdlText = @"
CREATE FUNCTION dbo.UF_MULTI()
RETURNS @Result TABLE (Val INT)
AS
BEGIN
    INSERT INTO @Result (Val)
    SELECT Val FROM dbo.TSourceTable WITH(NOLOCK)
    RETURN
END"
        };

        [Fact]
        public async Task GenerateSpecification_FunctionPath_ShouldRenderTheLockHintTable()
        {
            // 배선 지점 3/3: 함수 명세서 경로(BuildFunctionSpecificationPrompts).
            // 다중 문장 TVF는 자신의 반환 테이블 변수를 채우는 INSERT에서 잠금
            // 힌트를 가질 수 있다(DmlScopeExtractor의 DML 범위 표 Fix Round 2와
            // 같은 근거 - "함수는 DML이 없다"는 잘못된 불변식을 다시 심지 않는다).
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(FunctionWithLockHintDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.LockHintTableHeading, body);
            Assert.Contains("| INSERT 1 |", body);
            Assert.Contains("NOLOCK", body);
        }

        private static SpDefinition OrderBySpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_UTIL_STAT_PGCOLLECT_INS", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "UP_UTIL_STAT_PGCOLLECT_INS",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.UP_UTIL_STAT_PGCOLLECT_INS
    @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.TStatPGCollect (INYMD, CLIENTID, PGNAME, MALLID)
    SELECT INYMD, CLIENTID, PGNAME, MALLID
    FROM   dbo.TSettleMst
    ORDER BY INYMD, CLIENTID, PGNAME, MALLID

    UPDATE dbo.TSettleMst
    SET    ProcYMD = @pi_strYMD
    WHERE  YMD = @pi_strYMD
END"
        };

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_ShouldCarryOrderByColumn()
        {
            // STAT_PGCOLLECT_INS:113 실측 - ORDER BY INYMD, CLIENTID, PGNAME, MALLID가
            // 문서 어디에도 없었다(2026-08-21 축 A 감사). INSERT 행은 목록을,
            // UPDATE(최상위 ORDER BY가 문법상 불가) 행은 "—"를 실어야 한다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(OrderBySpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);

            Assert.Contains("ORDER BY", section);
            Assert.Contains("INYMD, CLIENTID, PGNAME, MALLID", section);
            var updateRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| UPDATE 1 |"));
            Assert.EndsWith("| — |", updateRow.TrimEnd());
        }

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_InsertWithoutOrderBy_ShouldRenderNone()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN INSERT INTO dbo.T (A) SELECT A FROM dbo.S END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);
            var insertRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| INSERT 1 |"));

            Assert.EndsWith("| (없음) |", insertRow.TrimEnd());
        }

        private static SpDefinition GroupBySpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_Util_Settle_Summary", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "UP_Util_Settle_Summary",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary
    @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.TSettleByTX (YMD, CLIENTID, CNT)
    SELECT YMD, CLIENTID, COUNT(*)
    FROM   dbo.TSettleMst
    WHERE  YMD = @pi_strYMD
    GROUP BY YMD, CLIENTID

    UPDATE dbo.TSettleMst
    SET    ProcYMD = @pi_strYMD
    WHERE  YMD = @pi_strYMD
END"
        };

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_ShouldCarryGroupByColumn()
        {
            // UP_Util_Settle_Summary 실측: GROUP BY 첫 키(YMD)가 매핑 표의 설명 칸에서만
            // 언급되다 표에서 통째로 빠졌다(🟡). GROUP BY는 별도 칸으로 확정한다 -
            // UPDATE(최상위 GROUP BY가 문법상 불가) 행은 "—"를 실어야 한다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(GroupBySpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);

            Assert.Contains("GROUP BY", section);
            var insertRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| INSERT 1 |"));
            Assert.Contains("YMD, CLIENTID", insertRow);
            var updateRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| UPDATE 1 |"));
            // GROUP BY 칸은 ORDER BY 앞이라 마지막에서 두 번째 칸이다.
            var updateCells = updateRow.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            Assert.Equal("—", updateCells[updateCells.Count - 2]);
        }

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_InsertWithoutGroupBy_ShouldRenderNone()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN INSERT INTO dbo.T (A) SELECT A FROM dbo.S END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);
            var insertRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| INSERT 1 |"));

            var cells = insertRow.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            // GROUP BY 칸은 ORDER BY 앞이라 마지막에서 두 번째 칸이다.
            Assert.Equal("(없음)", cells[cells.Count - 2]);
        }

        // === 독립 SELECT 행의 렌더 (2026-08-22 축 A 재감사 ③ Task 7) ===========
        //
        // Task 4가 커서 원천 질의를 `SELECT n` 행으로 담게 했는데, 렌더러의 GROUP BY·
        // ORDER BY 칸은 `Operation == "INSERT"`만 보고 나머지를 전부 "—"로 냈다 -
        // 그래서 PROC_ETC:62의 `ORDER BY A.OutYMD, A.ClientID`가 추출은 되는데 표에는
        // 보이지 않았다(추출됐으나 보이지 않는 상태). 대상 칸과 기준일 칸도 같은
        // 문장에서 각각 빈 칸과 "**아니오**"로 나왔는데, 후자는 아무것도 갱신하지 않는
        // 문장에 대한 거짓 단언이다(DmlScopeFact.DateParameterApplied 문서).
        private static SpDefinition CursorSourceSelectSpDefinition()
        {
            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_UTIL_SETTLE_PROC_ETC", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_UTIL_SETTLE_PROC_ETC",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.UP_UTIL_SETTLE_PROC_ETC
    @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE Cur_SettlePost CURSOR FOR
    SELECT A.ClientID, A.YMD, A.OutYMD
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.YMD = @pi_strYMD
    GROUP BY A.ClientID, A.YMD, A.OutYMD
    ORDER BY A.OutYMD, A.ClientID

    UPDATE dbo.TSettleMst
    SET    ProcYMD = @pi_strYMD
    WHERE  OutYMD = '20230101'
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
        public async Task GenerateSpecification_DmlScopeTable_StandaloneSelectRow_ShouldCarryOrderByAndGroupBy()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(CursorSourceSelectSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);
            var selectRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| SELECT 1 |"));
            var cells = selectRow.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            // 칸 순서: 문장 · 라인 · 대상 · 술어 컬럼 · 기준일 · 조인 키 · GROUP BY · ORDER BY
            Assert.Equal("A.OutYMD, A.ClientID", cells[cells.Count - 1]);
            Assert.Equal("ClientID, YMD, OutYMD", cells[cells.Count - 2]);
        }

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_StandaloneSelectRow_ShouldRenderTargetAndDateParameterAsDash()
        {
            // 갱신 대상이 없는 문장에 "**아니오**(최상위 기준 …)"를 적으면, 이 표가
            // 답하는 질문("갱신 대상 범위가 기준일로 좁혀지는가")에 대한 판정이
            // 있었던 것처럼 읽힌다 - 실제로는 판정 자체가 없었다. 대상 칸도 빈 칸이
            // 아니라 "—"여야 "빠뜨렸다"와 "해당 없다"가 갈린다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(CursorSourceSelectSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);
            var selectRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| SELECT 1 |"));
            var cells = selectRow.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            Assert.Equal("—", cells[2]);
            Assert.Equal("—", cells[4]);
        }

        [Fact]
        public async Task GenerateSpecification_DmlScopeTable_DmlRow_ShouldKeepTheDateParameterWordingUnchanged()
        {
            // 위 두 테스트의 짝 - SELECT 행을 가르는 갈래가 DML 행의 문구를 한 글자도
            // 바꾸면 안 된다. 같은 픽스처의 UPDATE는 최상위 WHERE에 기준일이 없으므로
            // 기존 문구가 그대로 나와야 한다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(CursorSourceSelectSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.DmlScopeTableHeading);
            var updateRow = section.Split('\n').Single(l => l.TrimStart().StartsWith("| UPDATE 1 |"));
            var cells = updateRow.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            Assert.Equal("dbo.TSettleMst", cells[2]);
            Assert.Equal("**아니오**(최상위 기준 · 하위 질의는 별도 확인)", cells[4]);
        }

        // === 잠금 힌트 도입문이 범위 칸의 값 셋을 다 설명한다 (Task 7) ===========
        //
        // Task 3 이후 범위 칸에 `하위 질의`가 실리고 문장 칸에 `SELECT n`·`IF n`이
        // 실리는데, 도입문은 `최상위`와 `파생` 둘만 정의하고 있었다. 표를 "그대로
        // 옮기라"고 하면서 산문이 표보다 적은 값을 정의하면 모델이 정의 밖의 행을
        // 오해하거나 지어낸 라벨로 바꿔 적을 수 있다.
        [Fact]
        public async Task GenerateSpecification_LockHintIntro_ShouldDefineSubqueryScopeAndNewStatementKinds()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(CursorSourceSelectSpDefinition(), "rules");
            var body = result.SystemPrompt!;
            var intro = body.Split('\n').Single(l => l.Contains("[CRITICAL LOCK HINT TABLE]"));

            Assert.Contains("`하위 질의`", intro);
            Assert.Contains("`SELECT n`", intro);
            Assert.Contains("`IF n`", intro);
        }

        // === DML 범위 도입문이 SELECT 행과 "—"를 정의한다 (전체 브랜치 리뷰 I1) ===
        //
        // 잠금 힌트 도입문과 같은 결함이 이 표에만 남아 있었다. Task 4가 문장 칸에
        // `SELECT n`을, Task 7이 그 행의 대상·기준일 칸에 `—`를 실었는데 도입문은
        // 한 글자도 바뀌지 않아, 표를 "그대로 옮기라"고 지시하면서 그 표가 쓰는
        // 값 둘을 정의하지 않았다. 실측(PROC_ETC 재생성 표): 8행 중 6행이 `SELECT n`
        // 이고 그 6행의 대상·기준일 칸이 전부 `—`다 - 표 제목은 "DML 범위"인데
        // 행의 다수가 DML이 아니다. 정의되지 않은 값은 모델이 아는 라벨로 바뀐다.
        [Fact]
        public async Task GenerateSpecification_DmlScopeIntro_ShouldDefineStandaloneSelectRowsAndTheDash()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(CursorSourceSelectSpDefinition(), "rules");
            var body = result.SystemPrompt!;
            var intro = body.Split('\n').Single(l => l.Contains("[CRITICAL SCOPE TABLE]"));

            // 사실 1: 문장 칸이 `SELECT n`을 담을 수 있고, 그것은 아무것도 갱신하지
            // 않는 독립 조회다 - DML로 바꿔 서술하면 안 된다.
            Assert.Contains("`SELECT n`", intro);
            // 사실 2: 그 행의 대상·기준일 칸에 실리는 `—`는 "아니오"도 "모름"도 아니라
            // 판정 자체가 없다는 뜻이다.
            Assert.Contains("`—`", intro);
            // 칸 이름은 LockHintIntroText의 방식대로 백틱 없이 적는다("The 문장 column").
            Assert.Contains("대상 and 기준일 파라미터 적용", intro);
        }

        // === 집합 술어 표의 「술어 원문」 열 (Task 7) ===========================
        private static SpDefinition UndecomposedPredicateSpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.InState = 1
    FROM   dbo.TSettleMst A
    WHERE  (A.UseState <> 1 OR A.YMD = A.AYMD)
END"
        };

        [Fact]
        public async Task GenerateSpecification_SetPredicateTable_UndecomposedTerm_ShouldRenderDashesAndPredicateText()
        {
            // 분해되지 않는 항(OR 결합)은 컬럼·연산·원소 수·리터럴이 전부 "—"이고
            // 원문 칸만 찬다 - 그 칸이 이 필터의 유일한 기록처다(설계 §3 결정 3).
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(UndecomposedPredicateSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.SetPredicateTableHeading);
            var row = section.Split('\n').Single(l => l.TrimStart().StartsWith("| UPDATE 1 |"));
            var cells = row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            // 칸 순서: 문장 · 라인 · 컬럼 · 연산 · 범위 · 원소 수 · 리터럴 목록 · 술어 원문
            Assert.Equal(8, cells.Count);
            Assert.Equal("—", cells[2]);
            Assert.Equal("—", cells[3]);
            Assert.Equal("최상위", cells[4]);
            Assert.Equal("—", cells[5]);
            Assert.Equal("—", cells[6]);
            Assert.Equal("(A.UseState <> 1 OR A.YMD = A.AYMD)", cells[7]);
        }

        [Fact]
        public async Task GenerateSpecification_SetPredicateTable_DecomposedTerm_ShouldKeepAllColumnsAndAddPredicateText()
        {
            // 위 사례의 짝 - 분해되는 항은 기존 여섯 칸을 그대로 채우고 원문 칸이
            // 하나 더 붙을 뿐이다. "—"가 분해되는 항까지 삼키면 안 된다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(SetPredicateSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.SetPredicateTableHeading);
            var row = section.Split('\n').Single(l => l.TrimStart().StartsWith("| UPDATE 1 |") && l.Contains("A.PGName"));
            var cells = row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            Assert.Equal(8, cells.Count);
            Assert.Equal("A.PGName", cells[2]);
            Assert.Equal("NOT IN", cells[3]);
            Assert.Equal("3", cells[5]);
            Assert.Equal("'PLCard', 'SSGPayCard', 'KakaoCard'", cells[6]);
            Assert.Equal("A.PGName NOT IN ('PLCard','SSGPayCard','KakaoCard')", cells[7]);
        }

        [Fact]
        public async Task GenerateSpecification_SetPredicateIntro_ShouldExplainTheDashAndThePredicateTextColumn()
        {
            // 표를 "그대로 옮기라"고만 하면 모델은 "—"뿐인 행을 "다른 행과 달라 보이니
            // 빠뜨려도 되는 행"으로 읽을 수 있다. 그 행에서는 원문 칸이 필터의 유일한
            // 기록처라는 것을 도입문이 말해야 한다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(UndecomposedPredicateSpDefinition(), "rules");
            var intro = result.SystemPrompt!.Split('\n')
                .Single(l => l.Contains("[CRITICAL SET PREDICATE TABLE]"));

            Assert.Contains("술어 원문", intro);
            Assert.Contains("`—`", intro);
            Assert.Contains("ONLY record of that filter", intro);
        }

        [Fact]
        public async Task GenerateSpecification_SetPredicateIntro_ShouldTellStandaloneSelectRowsApartFromDmlRows()
        {
            // [2026-08-23 축 A ③(b) Task 6] 이 도입문은 표에 DML 행만 있던 시절에
            // 쓰였다 - "the membership of each set is what determines the target rows"도
            // "narrows the target rows"도 대상 행(쓰는 행)을 말한다. Task 2가 독립
            // SELECT(커서 원천 질의·변수 대입 SELECT·함수 본문 SELECT)의 WHERE를
            // 담게 하면서 표에 `SELECT n` 행이 들어오는데, 그 행의 술어는 무엇을 쓸지가
            // 아니라 무엇을 읽을지를 가른다. 갈라 적지 않으면 모델이 순수 조회 문장의
            // 술어를 "갱신 대상을 좁힌다"로 옮길 길이 열린다 - 이 표는 "수정 금지"라
            // 산문이 바로잡을 수도 없다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(UndecomposedPredicateSpDefinition(), "rules");
            var intro = result.SystemPrompt!.Split('\n')
                .Single(l => l.Contains("[CRITICAL SET PREDICATE TABLE]"));

            Assert.Contains("`SELECT n`", intro);
            // "reads"만 찾으면 같은 줄의 "the 문장 cell reads `SELECT n`"에도 걸려
            // 지시문이 통째로 사라져도 초록이다 - 지시의 본체를 그대로 요구한다.
            Assert.Contains("narrows the rows that statement reads", intro);
            Assert.Contains("INSERT, UPDATE or DELETE writes", intro);
            // 독립 SELECT의 예시는 닫힌 열거가 아니어야 한다. 프로시저가 결과 집합을
            // 그대로 반환하는 `SELECT ... FROM ... WHERE`도 HasFromClause가 참이라
            // `SELECT n` 행을 내는데, 셋으로 닫아 두면 그 문장의 행이 지시 밖으로 읽힌다.
            Assert.Contains("such as", intro);
        }

        [Fact]
        public async Task Validate_UndecomposedPredicate_ShouldRoundTripThroughTheRenderedTable()
        {
            // 렌더러와 L1이 실제로 맞물리는지 왕복으로 확인한다 - 손으로 지어낸 표가
            // 아니라 AiService가 낸 표를 그대로 명세서에 붙인다. 한쪽만 바꾸면
            // "모델이 옳게 옮겨도 L1이 틀렸다고 하는" 실패 모양이 되는데, 그 실패는
            // 이 왕복에서만 드러난다.
            var spDef = UndecomposedPredicateSpDefinition();
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var promptResult = await service.GenerateSpecificationAsync(spDef, "rules");
            var tableSection = ExtractTableSection(promptResult.SystemPrompt, DmlScopeExtractor.SetPredicateTableHeading);
            var markdown = WrapAsSpecMarkdown(tableSection);

            // [왜 이 단언이 먼저 필요한가 - 측정으로 확인함] 원문 열이 없던 시절에도
            // 이 왕복은 통과했다. 분해되지 않은 사실은 리터럴이 비어 있고 렌더된 칸도
            // 비어 있어 "빈 집합끼리" 맞아떨어졌기 때문이다 - 즉 아무것도 묻지 않는
            // 통과였다. 왕복 대상 표가 실제로 원문을 담고 있는지부터 못박는다.
            Assert.Contains("(A.UseState <> 1 OR A.YMD = A.AYMD)", tableSection);

            var expectations = SpecExpectations.From(spDef);
            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.SetPredicates);

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        private static SpDefinition FunctionWithSchemaBindingDefinition() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_GET_OUTYMD4REFUND", CodeObjectType.Function),
            Schema = "dbo",
            Name = "UF_GET_OUTYMD4REFUND",
            ObjectType = CodeObjectType.Function,
            DdlText = "CREATE FUNCTION dbo.UF_GET_OUTYMD4REFUND(@a INT) RETURNS INT WITH SCHEMABINDING AS BEGIN RETURN @a END"
        };

        [Fact]
        public async Task GenerateSpecification_FunctionPath_ShouldRenderTheObjectDeclarationTable()
        {
            // 배선 지점 1/2: 함수 명세서 경로(BuildFunctionSpecificationPrompts).
            // UF_GET_OUTYMD4REFUND 실측(2026-08-21 축 A 감사) - WITH 절이 없다는
            // 것이 DDL에서 확정되는데 명세서가 "확인할 수 없음"으로 적었다. 여기
            // 픽스처는 반대로 SCHEMABINDING이 있는 경우를 확정 재료로 싣는다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(FunctionWithSchemaBindingDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading, body);
            Assert.Contains("dbo.UF_GET_OUTYMD4REFUND", body);
            Assert.Contains("SCHEMABINDING", body);
        }

        [Fact]
        public async Task GenerateSpecification_FunctionPath_NoWithOptions_ShouldRenderNone()
        {
            // "(없음)"이 곧 "스키마 바인딩 아님"이다 - 명세서가 "확인할 수 없음"이라고
            // 쓸 여지를 없애는 것이 이 표의 존재 이유다.
            var functionDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UF_PLAIN",
                ObjectType = CodeObjectType.Function,
                DdlText = "CREATE FUNCTION dbo.UF_PLAIN(@a INT) RETURNS INT AS BEGIN RETURN @a END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(functionDef, "rules");
            var body = result.SystemPrompt;

            Assert.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading, body);
            Assert.Contains("| dbo.UF_PLAIN | (없음) |", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_OverviewAndParameters_ShouldRenderTheObjectDeclarationTable()
        {
            // 배선 지점 2/2: OverviewAndParameters 분기(BuildSpecSectionPrompts).
            // `## 개요` 소속이고 재료가 있을 때만(fact != null) 싣는다 - 프로시저
            // DDL에서는 ObjectDeclarationExtractor.Extract가 항상 null이므로 이
            // 표가 절대 나타나지 않는다(다음 테스트가 그 대칭을 확인한다).
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(
                FunctionWithSchemaBindingDefinition(), "OverviewAndParameters", "rules", null);
            var body = result.SystemPrompt;

            Assert.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading, body);
            Assert.Contains("SCHEMABINDING", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_OverviewAndParameters_ProcedureDdl_ShouldOmitTheObjectDeclarationTable()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END"
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(
                spDef, "OverviewAndParameters", "rules", null);

            Assert.DoesNotContain(ObjectDeclarationExtractor.ObjectDeclarationTableHeading, result.SystemPrompt);
        }

        /// <summary>
        /// 실행 의미 표 픽스처. DDL 조각 자체가 &lt;sp-source-ddl&gt;로도 실리므로,
        /// 표가 실제로 렌더됐는지는 표에서만 나오는 마크업(헤딩 상수)으로 대조해야
        /// 한다 - 원본 DDL에 우연히 있는 단어를 짚으면 거짓양성이 된다.
        /// </summary>
        private static SpDefinition ProbeExecutionSemanticsSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "COMM_UPD",
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "COMM_UPD", CodeObjectType.Procedure)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldPrefillTheExecutionSemanticsTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeExecutionSemanticsSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(ExecutionSemanticsFacts.TableHeading, body);
            Assert.Contains("3부 식별자 참조 0건", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldPrefillTheExecutionSemanticsTable()
        {
            // 지역 모델 경로는 BuildSpecificationPrompts를 전혀 호출하지 않는다.
            // 이 분기에서만 배선이 빠져도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeExecutionSemanticsSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(ExecutionSemanticsFacts.TableHeading, body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutStaticAnalysis_ShouldOmitTheExecutionSemanticsTable()
        {
            var (service, handler) = CreateProbe();
            var spDef = new SpDefinition { Schema = "dbo", Name = "P", DdlText = "SELECT 1;" };

            await service.GenerateSpecificationAsync(spDef, "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(ExecutionSemanticsFacts.TableHeading, body);
        }

        private static SpDefinition ProbeExecutionSemanticsFunctionSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UF_GET_SETTLE",
                ObjectType = CodeObjectType.Function,
                DdlText = "CREATE FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UF_GET_SETTLE", CodeObjectType.Function)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_Function_ShouldPrefillTheExecutionSemanticsTable()
        {
            // 갈래 2(함수 명세서, BuildFunctionSpecificationPrompts)는 `## CRUD 분석`
            // 헤더를 실제로 쓰는 갈래다(필수 H2 목록에 있다) - 이 배선을 직접 단언하는
            // 테스트가 없었다(원장 M2). 이 갈래는 표를 그대로 받아야 한다.
            //
            // [Fix Round 1 - 갈래 2 고유 앵커] "The required H2 headers are exactly"는
            // BuildFunctionSpecificationPrompts에만 있는 문구다(갈래 1은 "The
            // specification H2 headers must strictly use these exact Korean titles"를
            // 쓴다) - 이 앵커가 없으면 BuildSpecificationPrompts의 ObjectType ==
            // CodeObjectType.Function 라우팅 분기가 깨져 이 픽스처가 갈래 1로
            // 떨어져도 표 배선 자체는 그대로라 테스트가 통과해버린다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeExecutionSemanticsFunctionSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("The required H2 headers are exactly", body);
            Assert.Contains(ExecutionSemanticsFacts.TableHeading, body);
            Assert.Contains("3부 식별자 참조 0건", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_ShouldNotEmitExecutionSemanticsTableHeading()
        {
            // Task 14 (Critical) - LogicAndVisualization 분기는 `## 로직 흐름 요약`과
            // `## 비즈니스 흐름 시각화`만 쓴다(`## CRUD 분석`은 쓰지 않는다). 실행 의미
            // 표의 인트로는 "Copy this table verbatim into `## CRUD 분석`"라고 지시하므로,
            // 이 분기에 표 형태로 그대로 실리면 자기모순 지시가 된다 - 모델이 자신의
            // H2 제약을 어기고 `## CRUD 분석` 헤딩을 합성하거나, 표를 통째로 버릴
            // 위험이 있다. 헤딩·표는 없어야 하고, 사실 자체는 참고 재료로는 남아야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeExecutionSemanticsSpDef(), "LogicAndVisualization", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(ExecutionSemanticsFacts.TableHeading, body);
            Assert.Contains("3부 식별자 참조 0건", body);
        }

        // Task 17 - 결함 E(F1 무리)의 실제 앵커는 `## 개요`다(UF_Get_CLComm4MobileCo의
        // Spec.md, "원본 DDL에 3부 식별자가 없으므로 크로스 데이터베이스 참조 여부를
        // 단언할 수 없습니다" - StaticAnalysis가 이미 확정한 값을 되짚었다). 이 결함이
        // 났던 시점에는 `BuildMachineFactBlockLines` 호출부가 네 곳(SP 전체·함수·
        // CrudAnalysis·LogicAndVisualization)뿐이었고 그중 어디에도
        // `OverviewAndParameters`가 없었다 - 이 갈래는 실행 의미 사실을 한 번도 받지
        // 못했다. 지금은 다섯 번째 호출부로 이 갈래도 받는다 - 표가 아니라 참고
        // 재료로(이 갈래는 `## 개요`·`## 파라미터 목록`만 쓰므로 `## CRUD 분석`용 표
        // 지시를 주면 자기모순이 된다) - `BuildLockHintReferenceMaterialLines`가 이미
        // 그 선례다. 이 테스트는 그 배선이 유지되는지를 지킨다.
        [Fact]
        public async Task GenerateSpecSectionAsync_OverviewAndParameters_ShouldReceiveExecutionSemanticsFactsInline()
        {
            // 형제 테스트(LogicAndVisualization_ShouldNotEmitExecutionSemanticsTableHeading
            // 등)와 범위를 맞춘다 - result.SystemPrompt만 보면 사용자 프롬프트로 헤딩이
            // 새는 회귀를 못 잡는다. CreateProbe + DecodeMessageContents(handler.
            // LastRequestBody)로 시스템+사용자 프롬프트 둘 다 본다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeExecutionSemanticsSpDef(), "OverviewAndParameters", "rules", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            // 확정 사실 자체는 실려야 하지만, 이 갈래는 `## CRUD 분석`을 쓰지 않으므로
            // 표·헤딩 형태로 실리면 안 된다 - LogicAndVisualization과 같은 계약이다.
            Assert.Contains("3부 식별자 참조 0건", body);
            Assert.DoesNotContain(ExecutionSemanticsFacts.TableHeading, body);
            Assert.Contains("Do NOT output", body);
        }

        // CASE 분기는 `## 로직 흐름 요약` 소관이고 `## 개요`의 서술 대상이 아니다 -
        // 감사 🟡이 난 자리는 DB 배치(크로스 DB 참조 단언)뿐, CASE 분기 서술이
        // `## 개요`에서 문제 된 적은 없다. 재료를 늘리면 프롬프트가 길어지고 모델이
        // 산만해지므로, 이 갈래에는 CASE 분기 재료를 주지 않는다 - 표로도, 참고
        // 재료로도 싣지 않아야 한다.
        [Fact]
        public async Task GenerateSpecSectionAsync_OverviewAndParameters_ShouldNotReceiveCaseBranchFacts()
        {
            // 형제 테스트와 범위를 맞춘다 - 시스템+사용자 프롬프트 둘 다 본다(위 테스트와
            // 같은 이유).
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeCaseBranchSpDef(), "OverviewAndParameters", "rules", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(CaseBranchExtractor.TableHeading, body);
            Assert.DoesNotContain("REFERENCE - CASE branch facts", body);
        }

        private static SpDefinition ProbeCaseBranchSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "SETTLE_YMD",
                DdlText = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldPrefillTheCaseBranchTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeCaseBranchSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(CaseBranchExtractor.TableHeading, body);
            Assert.Contains(CaseBranchExtractor.ElseConditionText, body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutCase_ShouldOmitTheCaseBranchTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(CaseBranchExtractor.TableHeading, body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldNotEmitCaseBranchTableHeading()
        {
            // Task 14 (Critical) - CrudAnalysis 분기는 `## CRUD 분석` 하나만 쓴다
            // ("only one H2 header"). CASE 분기 표의 인트로는 "Copy this table
            // verbatim into `## 로직 흐름 요약`"라고 지시하므로, 이 분기에 표
            // 형태로 그대로 실리면 자기모순 지시가 된다 - 이 테스트를 고치기 전에
            // 돌리면 헤딩이 실제로 실려 RED가 난다. 헤딩·표는 없어야 하고, 조건/결과
            // 원문 자체는 참고 재료로는 남아야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeCaseBranchSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(CaseBranchExtractor.TableHeading, body);
            Assert.Contains(CaseBranchExtractor.ElseConditionText, body);
        }

        private static SpDefinition ProbeCaseBranchFunctionSpDef()
        {
            var spDef = ProbeCaseBranchSpDef();
            spDef.ObjectType = CodeObjectType.Function;
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_Function_ShouldPrefillTheCaseBranchTable()
        {
            // 갈래 2(함수 명세서, BuildFunctionSpecificationPrompts)는 `## 로직 흐름
            // 요약` 헤더도 실제로 쓰는 갈래다(필수 H2 목록에 있다) - 이 배선을 직접
            // 단언하는 테스트가 없었다(원장 M2). 이 갈래는 표를 그대로 받아야 한다.
            //
            // [Fix Round 1 - 갈래 2 고유 앵커] 위 테스트와 같은 이유로 "The required
            // H2 headers are exactly"(BuildFunctionSpecificationPrompts에만 있는
            // 문구)를 함께 단언한다 - 이게 없으면 BuildSpecificationPrompts의
            // ObjectType == CodeObjectType.Function 라우팅 분기가 깨져 갈래 1로
            // 떨어져도 이 테스트는 그대로 통과한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeCaseBranchFunctionSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("The required H2 headers are exactly", body);
            Assert.Contains(CaseBranchExtractor.TableHeading, body);
            Assert.Contains(CaseBranchExtractor.ElseConditionText, body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_LogicAndVisualization_ShouldPrefillTheCaseBranchTable()
        {
            // 로직 흐름 요약 갈래(갈래 3-3)가 이 표의 실제 소관 절이다 - 여기서
            // 배선이 빠지면 CASE 분기 표가 실제로 실려야 할 절에 나가지 않는다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeCaseBranchSpDef(), "LogicAndVisualization", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(CaseBranchExtractor.TableHeading, body);
        }
    }
}
