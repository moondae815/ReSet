using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Validator.Core.Services;

namespace ReSet.Core.Tests
{
    public class ValidatorAiServiceTests
    {
        [Fact]
        public async Task VerifyCodeAsync_WithValidMatchJson_ShouldParseSuccessfully()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""MATCH"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""Suggestions"": ""Perfect match.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            Assert.Equal("MATCH", report.OverallStatus);
            Assert.Equal("Perfect match.", report.Suggestions);
            Assert.NotNull(report.SystemPrompt);
        }

        [Fact]
        public async Task VerifyCodeAsync_WithMalformedJson_ShouldReturnMismatch()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"I am an AI. I cannot output JSON properly.";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            Assert.Equal("MISMATCH", report.OverallStatus);
            Assert.Contains("AI 응답 파싱 실패", report.Suggestions);
        }

        [Fact]
        public async Task GenerateTestParametersAsync_ShouldReturnCleanJson()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""ProcedureName"": ""Test"",
  ""TestCases"": []
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var result = await service.GenerateTestParametersAsync("spec", "proc");

            Assert.StartsWith("{", result);
            Assert.Contains("Test", result);
        }

        [Fact]
        public async Task GenerateMockTableDataAsync_ShouldReturnCleanJson()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""Tables"": []
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var result = await service.GenerateMockTableDataAsync("spec", "ddl", "deps");

            Assert.StartsWith("{", result);
            Assert.Contains("Tables", result);
        }

        [Fact]
        public async Task VerifyCodeAsync_SendsTheDataAccessBoundaryCriteria()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"{
  ""OverallStatus"": ""MATCH"",
  ""Suggestions"": ""ok""
}";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            // 규칙 문구는 DataAccessPolicy가 단독 소유한다. 프롬프트는 그것을 그대로 싣는다.
            Assert.Contains(DataAccessPolicy.VerificationCriteria, report.SystemPrompt);
            Assert.Contains("DataAccessBoundaryGap", report.SystemPrompt);
        }

        [Fact]
        public async Task VerifyCodeAsync_ParsesTheBoundaryGapField()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""PARTIAL"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": ""정산 집계 UPDATE가 EF Core ExecuteUpdate로 구현됨"",
  ""Suggestions"": ""집계 UPDATE를 파라미터 바인딩 SQL로 되돌리십시오.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            Assert.Equal("PARTIAL", report.OverallStatus);
            Assert.Contains("EF Core ExecuteUpdate", report.DataAccessBoundaryGap);
            Assert.True(report.HasGaps);
        }

        [Fact]
        public async Task GenerateUnitTestCodeAsync_ShouldReturnCleanCode()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var codeResponse = @"```csharp
public class Test {}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = codeResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var result = await service.GenerateUnitTestCodeAsync("spec", "proc", "C#");

            Assert.Equal("public class Test {}", result);
        }
    }
}
