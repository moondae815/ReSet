using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchStepPlanParserTests
    {
        private const string ValidBlock = @"## 목차

본문 산문이 앞에 온다.

```json
{
  ""Steps"": [
    {
      ""Code"": ""S01"",
      ""Name"": ""일별 계약 수수료율 스냅샷"",
      ""LegacyProcedures"": [""UP_Util_PG_Client_CMRate_Ins""],
      ""TargetTables"": [""dbo.TPGSettleRate""],
      ""ErrorCodes"": [""-1"", ""-2""],
      ""Chunkable"": false
    },
    {
      ""Code"": ""S02"",
      ""Name"": ""기본 정산 원장 생성"",
      ""LegacyProcedures"": [""UP_UTIL_SETTLE_INS""],
      ""TargetTables"": [""dbo.TSettleMst""],
      ""ErrorCodes"": [""-1""],
      ""Chunkable"": true
    }
  ]
}
```

뒤에도 산문이 있다.";

        [Fact]
        public void TryParse_WithValidStepsBlock_ReturnsStepsInOrder()
        {
            var steps = BatchStepPlanParser.TryParse(ValidBlock);

            Assert.NotNull(steps);
            Assert.Equal(2, steps!.Count);
            Assert.Equal("S01", steps[0].Code);
            Assert.Equal("기본 정산 원장 생성", steps[1].Name);
            Assert.Equal(new[] { "dbo.TSettleMst" }, steps[1].TargetTables);
            Assert.Equal(new[] { "-1", "-2" }, steps[0].ErrorCodes);
            Assert.False(steps[0].Chunkable);
            Assert.True(steps[1].Chunkable);
        }

        [Fact]
        public void TryParse_WithNoJsonBlock_ReturnsNull()
        {
            Assert.Null(BatchStepPlanParser.TryParse("## 목차\n산문만 있다."));
        }

        [Fact]
        public void TryParse_WithMalformedJson_ReturnsNull()
        {
            Assert.Null(BatchStepPlanParser.TryParse("```json\n{ \"Steps\": [ }\n```"));
        }

        [Fact]
        public void TryParse_WithEmptyStepsArray_ReturnsNull()
        {
            Assert.Null(BatchStepPlanParser.TryParse("```json\n{ \"Steps\": [] }\n```"));
        }

        [Fact]
        public void TryParse_WithMoreThanMaxSteps_ReturnsNull()
        {
            var items = new List<string>();
            for (int i = 0; i <= BatchStepPlanParser.MaxSteps; i++)
            {
                items.Add($"{{ \"Code\": \"S{i:D2}\", \"Name\": \"n{i}\" }}");
            }
            var markdown = "```json\n{ \"Steps\": [" + string.Join(",", items) + "] }\n```";

            Assert.Null(BatchStepPlanParser.TryParse(markdown));
        }

        [Fact]
        public void TryParse_WithStepMissingCode_ReturnsNull()
        {
            Assert.Null(BatchStepPlanParser.TryParse(
                "```json\n{ \"Steps\": [ { \"Name\": \"이름만 있다\" } ] }\n```"));
        }

        [Fact]
        public void TryParse_SkipsUnrelatedJsonBlockAndFindsStepsBlock()
        {
            var markdown = "```json\n{ \"Unrelated\": 1 }\n```\n\n" +
                "```json\n{ \"Steps\": [ { \"Code\": \"S01\", \"Name\": \"첫 단계\" } ] }\n```";

            var steps = BatchStepPlanParser.TryParse(markdown);

            Assert.NotNull(steps);
            Assert.Single(steps!);
            Assert.Equal("S01", steps![0].Code);
        }

        [Fact]
        public void TryParse_WithMissingOptionalArrays_ReturnsEmptyCollections()
        {
            var steps = BatchStepPlanParser.TryParse(
                "```json\n{ \"Steps\": [ { \"Code\": \"S01\", \"Name\": \"첫 단계\" } ] }\n```");

            Assert.NotNull(steps);
            Assert.Empty(steps![0].TargetTables);
            Assert.Empty(steps[0].ErrorCodes);
            Assert.Empty(steps[0].LegacyProcedures);
        }
    }
}
