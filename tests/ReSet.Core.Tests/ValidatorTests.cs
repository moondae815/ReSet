using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Services;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Plugins;
using ReSet.Validator.Core.Services;
using ReSet.Core.Models;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ValidatorTests
    {
        [Fact]
        public void FileMappingService_ShouldMap_ByFilenameRules()
        {
            // Arrange
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var specDir = Path.Combine(tempBase, "output");
            var codeDir = Path.Combine(tempBase, "src");
            
            Directory.CreateDirectory(specDir);
            Directory.CreateDirectory(codeDir);

            // Spec 파일 생성
            var specPath = Path.Combine(specDir, "Jobs", "Consolidated_Batch_Job", "docs", "BatchMigrationPlan.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Consolidated_Batch_Job Spec");

            // Code 파일 생성
            var codePath = Path.Combine(codeDir, "Consolidated_Batch_Job.cs");
            File.WriteAllText(codePath, "public class Consolidated_Batch_Job {}");

            var config = new ValidatorConfig
            {
                SpecDirectory = specDir,
                SourceCodeDirectory = codeDir
            };

            var service = new FileMappingService();

            try
            {
                // Act
                var mappings = service.ResolveMappings(config);

                // Assert
                Assert.Single(mappings);
                Assert.Equal(specPath, mappings[0].SpecFilePath);
                Assert.Equal(codePath, mappings[0].SourceCodePath);
                Assert.Equal("Consolidated_Batch_Job", mappings[0].MappedName);
            }
            finally
            {
                // Cleanup
                Directory.Delete(tempBase, true);
            }
        }



        [Fact]
        public async Task CsValidatorPlugin_ShouldPass_OnValidSyntax()
        {
            // Arrange
            var plugin = new CsValidatorPlugin();
            var spec = "## 입력 파라미터\n- `@CustomerID` (varchar)";
            var code = @"
using System;
namespace App {
    public class CustOrderHist {
        public void Execute() {
            // Logic here
        }
    }
}";

            // Act
            var result = await plugin.ValidateStaticAsync(spec, code);

            // Assert
            Assert.True(result.Passed);
            Assert.Equal("CustOrderHist", result.ClassOrMethodName);
            Assert.Equal("CustOrderHist", result.ExtractedMetadata["ClassName"]);
        }

        [Fact]
        public async Task CsValidatorPlugin_ShouldFail_OnMismatchedBraces()
        {
            // Arrange
            var plugin = new CsValidatorPlugin();
            var spec = "# Spec";
            var code = "public class BadBraces { // missing close brace";

            // Act
            var result = await plugin.ValidateStaticAsync(spec, code);

            // Assert
            Assert.False(result.Passed);
            Assert.Contains("중괄호 쌍이 일치하지 않습니다", result.ErrorMessage);
        }

        [Fact]
        public async Task CsValidatorPlugin_ShouldFail_OnTransactionEnlistmentViolation()
        {
            // 이 조항을 어기면 검증기의 Rollback 격리가 깨져 정합성 대조 결과가 오염된다.
            // 그래서 AI 판단(L2)에 맡기지 않고 L1에서 기계적으로 막는다.
            // Arrange
            var plugin = new CsValidatorPlugin();
            var spec = "# Spec";
            var code = @"
public class SettleTasklet {
    public void Run(SettleContextDb db) {
        using var tx = db.Database.BeginTransaction();
        db.SaveChanges();
    }
}";

            // Act
            var result = await plugin.ValidateStaticAsync(spec, code);

            // Assert
            Assert.False(result.Passed);
            Assert.Contains("데이터 액세스 경계 위반", result.ErrorMessage);
        }

        [Fact]
        public async Task JavaValidatorPlugin_ShouldFail_OnTransactionEnlistmentViolation()
        {
            // Arrange
            var plugin = new JavaValidatorPlugin();
            var spec = "# Spec";
            var code = @"
package com.example;
public class SettleTasklet {
    public void run() {
        entityManager.getTransaction().begin();
    }
}";

            // Act
            var result = await plugin.ValidateStaticAsync(spec, code);

            // Assert
            Assert.False(result.Passed);
            Assert.Contains("데이터 액세스 경계 위반", result.ErrorMessage);
        }

        [Fact]
        public async Task JavaValidatorPlugin_ShouldPass_OnValidSyntax()
        {
            // Arrange
            var plugin = new JavaValidatorPlugin();
            var spec = "# Spec";
            var code = @"
package com.example;
public class CustOrderHistBatch {
    public void run() {
    }
}";

            // Act
            var result = await plugin.ValidateStaticAsync(spec, code);

            // Assert
            Assert.True(result.Passed);
            Assert.Equal("CustOrderHistBatch", result.ClassOrMethodName);
        }

        [Fact]
        public async Task ValidatorAiService_ShouldParse_ValidJsonResponse()
        {
            // Arrange
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""MATCH"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": """",
  ""Suggestions"": ""Perfect match.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);

            // Act
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            // Assert
            Assert.Equal("MATCH", report.OverallStatus);
            Assert.False(report.HasGaps);
            Assert.Equal("Perfect match.", report.Suggestions);
        }

        [Fact]
        public async Task ValidatorAiService_ShouldSoftFail_OnInvalidResponse()
        {
            // Arrange
            var mockAiClient = Substitute.For<IAiClient>();
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "This is not a JSON response." }));

            var service = new ValidatorAiService(mockAiClient);

            // Act
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            // Assert
            Assert.Equal("MISMATCH", report.OverallStatus);
            Assert.True(report.HasGaps);
            Assert.Contains("AI 응답 파싱 실패", report.Suggestions);
        }

        [Fact]
        public void DataComparisonService_ShouldReportSuccess_WhenDataMatches()
        {
            // Arrange
            var service = new DataComparisonService();
            var legacyJson = @"{
                ""ProcedureName"": ""dbo.TestProc"",
                ""ExecutionResults"": [
                    {
                        ""CaseId"": ""TC001"",
                        ""Status"": ""SUCCESS"",
                        ""ResultSets"": [
                            [
                                { ""Col1"": 1, ""Col2"": ""Val1"" },
                                { ""Col1"": 2, ""Col2"": ""Val2"" }
                            ]
                        ]
                    }
                ]
            }";
            var targetJson = @"{
                ""ProcedureName"": ""dbo.TestProc"",
                ""ExecutionResults"": [
                    {
                        ""CaseId"": ""TC001"",
                        ""Status"": ""SUCCESS"",
                        ""ResultSets"": [
                            [
                                { ""Col1"": 1, ""Col2"": ""Val1"" },
                                { ""Col1"": 2, ""Col2"": ""Val2"" }
                            ]
                        ]
                    }
                ]
            }";

            // Act
            var report = service.CompareOutputs(legacyJson, targetJson);

            // Assert
            Assert.Contains("100% 일치", report);
            Assert.Contains("✅", report);
        }

        [Fact]
        public void DataComparisonService_ShouldReportMismatch_WhenValuesDiffer()
        {
            // Arrange
            var service = new DataComparisonService();
            var legacyJson = @"{
                ""ProcedureName"": ""dbo.TestProc"",
                ""ExecutionResults"": [
                    {
                        ""CaseId"": ""TC001"",
                        ""Status"": ""SUCCESS"",
                        ""ResultSets"": [
                            [
                                { ""Col1"": 1, ""Col2"": ""Val1"" }
                            ]
                        ]
                    }
                ]
            }";
            var targetJson = @"{
                ""ProcedureName"": ""dbo.TestProc"",
                ""ExecutionResults"": [
                    {
                        ""CaseId"": ""TC001"",
                        ""Status"": ""SUCCESS"",
                        ""ResultSets"": [
                            [
                                { ""Col1"": 1, ""Col2"": ""ValChanged"" }
                            ]
                        ]
                    }
                ]
            }";

            // Act
            var report = service.CompareOutputs(legacyJson, targetJson);

            // Assert
            Assert.Contains("불일치 발생", report);
            Assert.Contains("❌", report);
            Assert.Contains("Val1", report);
            Assert.Contains("ValChanged", report);
        }

        [Fact]
        public async Task SpExecutionService_ShouldSoftFail_OnInvalidConnectionString()
        {
            // Arrange
            var service = new SpExecutionService();
            var invalidConn = "Server=invalid_server_name;Database=invalid;User ID=invalid;Password=invalid;TrustServerCertificate=true;Connection Timeout=1";
            var inputsJson = @"{
                ""ProcedureName"": ""dbo.TestProc"",
                ""TestCases"": [
                    {
                        ""CaseId"": ""TC001"",
                        ""Parameters"": {
                            ""Param1"": ""Value1""
                        }
                    }
                ]
            }";

            // Act
            var resultJson = await service.ExecuteStoredProcedureAsync(invalidConn, inputsJson);

            // Assert
            Assert.Contains("FAIL", resultJson);
            Assert.Contains("dbo.TestProc", resultJson);
        }

        [Fact]
        public void GapReport_WithOnlyBoundaryGap_ReportsHasGaps()
        {
            // 경계 위반만 있는 상태를 "차이 없음"으로 보고하면, 이후 HasGaps를 게이트로
            // 쓰는 코드가 생겼을 때 경계 위반만 조용히 누락된다.
            var report = new GapReport
            {
                OverallStatus = "MATCH",
                DataAccessBoundaryGap = "체크포인트 외 경로에서 ORM 사용"
            };

            Assert.True(report.HasGaps);
        }
    }
}
