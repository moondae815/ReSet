using System;
using System.Net.Http;
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
    }
}
