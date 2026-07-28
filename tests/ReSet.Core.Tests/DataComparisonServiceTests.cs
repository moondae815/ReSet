using System;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class DataComparisonServiceTests
    {
        [Fact]
        public void CompareOutputs_WithValidMatchingJson_ShouldReturn100PercentMatch()
        {
            var legacyJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [ [ { ""Id"": 1, ""Name"": ""Test"" } ] ] } ] }";
            var targetJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [ [ { ""Id"": 1, ""Name"": ""Test"" } ] ] } ] }";
            
            var service = new DataComparisonService();
            var result = service.CompareOutputs(legacyJson, targetJson);
            
            Assert.Contains("100.0%", result);
            Assert.Contains("✅ 데이터 정합성 100% 일치", result);
        }

        [Fact]
        public void CompareOutputs_WithMissingCaseId_ShouldReportMissing()
        {
            var legacyJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [] } ] }";
            var targetJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC02"", ""Status"": ""SUCCESS"", ""ResultSets"": [] } ] }";
            
            var service = new DataComparisonService();
            var result = service.CompareOutputs(legacyJson, targetJson);
            
            Assert.Contains("신규 결과에 테스트 케이스가 누락되었습니다", result);
            Assert.Contains("0.0%", result);
        }

        [Fact]
        public void CompareOutputs_WithStatusMismatch_ShouldFailCase()
        {
            var legacyJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [] } ] }";
            var targetJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""FAIL"", ""ErrorCode"": ""ERR"", ""ResultSets"": [] } ] }";
            
            var service = new DataComparisonService();
            var result = service.CompareOutputs(legacyJson, targetJson);
            
            Assert.Contains("상태 불일치", result);
            Assert.Contains("0.0%", result);
        }

        [Fact]
        public void CompareOutputs_WithDataMismatch_ShouldFailCase()
        {
            var legacyJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [ [ { ""Id"": 1, ""Name"": ""Legacy"" } ] ] } ] }";
            var targetJson = @"{ ""ProcedureName"": ""TestProc"", ""ExecutionResults"": [ { ""CaseId"": ""TC01"", ""Status"": ""SUCCESS"", ""ResultSets"": [ [ { ""Id"": 1, ""Name"": ""Target"" } ] ] } ] }";
            
            var service = new DataComparisonService();
            var result = service.CompareOutputs(legacyJson, targetJson);
            
            Assert.Contains("`Name` 값 불일치", result);
            Assert.Contains("0.0%", result);
        }

        [Fact]
        public void CompareOutputs_WithNullJson_ShouldHandleGracefully()
        {
            var service = new DataComparisonService();
            var result = service.CompareOutputs("invalid", "invalid");
            
            Assert.Contains("데이터 비교 실패", result);
        }
    }
}
