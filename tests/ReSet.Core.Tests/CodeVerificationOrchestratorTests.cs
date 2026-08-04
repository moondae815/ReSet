using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;

namespace ReSet.Core.Tests
{
    public class CodeVerificationOrchestratorTests
    {
        [Fact]
        public async Task RunVerificationAsync_WithBatchModeAndMatch_ShouldAutoApproveWithoutUI()
        {
            // Arrange
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var specDir = Path.Combine(tempBase, "output");
            var codeDir = Path.Combine(tempBase, "src");
            var outDir = Path.Combine(tempBase, "reports");

            Directory.CreateDirectory(specDir);
            Directory.CreateDirectory(codeDir);

            var specPath = Path.Combine(specDir, "Jobs", "Consolidated_Batch_Job", "docs", "BatchMigrationPlan.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Spec");
            File.WriteAllText(Path.Combine(codeDir, "Consolidated_Batch_Job.cs"), "public class Consolidated_Batch_Job {}");

            var config = new ValidatorConfig
            {
                SpecDirectory = specDir,
                SourceCodeDirectory = codeDir,
                OutputDirectory = outDir,
                MaxL2Attempts = 1
            };

            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""MATCH"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": """",
  ""Suggestions"": ""Matched""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var mockUi = Substitute.For<IValidationUserInterface>();
            var orchestrator = new CodeVerificationOrchestrator(config, mockAiClient, ui: mockUi);

            try
            {
                // Act
                var results = await orchestrator.RunVerificationAsync(isBatchMode: true, CancellationToken.None);

                // Assert
                Assert.Single(results);
                var result = results[0];
                Assert.True(result.L1Passed);
                Assert.True(result.L2Passed);
                Assert.True(result.IsApproved); // 배치 모드 + L2 Passed -> 자동 승인
                _ = mockUi.DidNotReceive().ConfirmValidationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<GapReport>());
            }
            finally
            {
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }



        [Fact]
        public async Task RunVerificationAsync_WithExportWriteError_ShouldSoftFailWithoutCrash()
        {
            // Arrange
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var specDir = Path.Combine(tempBase, "output");
            var codeDir = Path.Combine(tempBase, "src");

            Directory.CreateDirectory(specDir);
            Directory.CreateDirectory(codeDir);

            var specPath = Path.Combine(specDir, "Procedures", "dbo.TestProc", "docs", "Spec.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Spec");
            File.WriteAllText(Path.Combine(codeDir, "TestProc.cs"), "public class TestProc {}");

            // 잘못된 폴더 경로를 OutputDirectory로 지정하여 쓰기 예외 강제 유도 (리눅스 루트 권한 필요 경로 활용)
            var config = new ValidatorConfig
            {
                SpecDirectory = specDir,
                SourceCodeDirectory = codeDir,
                OutputDirectory = "/invalid_root_dir_no_perm/reports", 
                MaxL2Attempts = 1
            };

            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""MATCH"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": """",
  ""Suggestions"": ""Matched""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var mockUi = Substitute.For<IValidationUserInterface>();
            var orchestrator = new CodeVerificationOrchestrator(config, mockAiClient, ui: mockUi);

            try
            {
                // Act & Assert
                // 예외가 발생하더라도 Soft Fail 정책에 의해 크래시되지 않고 빈 결과나 요약이 반환되는지 확인
                var exception = await Record.ExceptionAsync(() => orchestrator.RunVerificationAsync(isBatchMode: true, CancellationToken.None));
                Assert.Null(exception); // 예외가 던져지지 않아야 함
                mockUi.Received(1).ShowWarning(Arg.Any<string>()); // 경고 로그가 표시되었는지 확인
            }
            finally
            {
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }

        [Fact]
        public async Task RunVerificationAsync_WritesTheBoundaryGapIntoTheReport()
        {
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var specDir = Path.Combine(tempBase, "output");
            var codeDir = Path.Combine(tempBase, "src");
            var outDir = Path.Combine(tempBase, "reports");

            Directory.CreateDirectory(specDir);
            Directory.CreateDirectory(codeDir);

            var specPath = Path.Combine(specDir, "Jobs", "Consolidated_Batch_Job", "docs", "BatchMigrationPlan.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Spec");
            File.WriteAllText(Path.Combine(codeDir, "Consolidated_Batch_Job.cs"), "public class Consolidated_Batch_Job {}");

            var config = new ValidatorConfig
            {
                SpecDirectory = specDir,
                SourceCodeDirectory = codeDir,
                OutputDirectory = outDir,
                MaxL2Attempts = 1
            };

            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""PARTIAL"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": ""청킹 루프 내부 INSERT가 EF Core SaveChanges로 구현됨"",
  ""Suggestions"": ""청킹 INSERT를 파라미터 바인딩 SQL로 되돌리십시오.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var mockUi = Substitute.For<IValidationUserInterface>();
            var orchestrator = new CodeVerificationOrchestrator(config, mockAiClient, ui: mockUi);

            try
            {
                var results = await orchestrator.RunVerificationAsync(isBatchMode: true, CancellationToken.None);

                Assert.Single(results);
                Assert.False(results[0].L2Passed);

                var reportPath = Path.Combine(outDir, "docs", results[0].MappedName, "ValidationReport.md");
                Assert.True(File.Exists(reportPath));

                var report = await File.ReadAllTextAsync(reportPath);
                Assert.Contains("데이터 액세스 경계", report);
                Assert.Contains("EF Core SaveChanges", report);
            }
            finally
            {
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }
    }
}
