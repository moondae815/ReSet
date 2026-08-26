using System.Collections.Generic;
using System.Linq;
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
        public void TryParse_WithDuplicateStepCodes_ReturnsNull()
        {
            // Code가 겹치면 분할 생성이 `sections[step.Code] = markdown`으로 모으는
            // 과정에서 나중 것이 앞의 것을 덮어쓴다. 호출은 두 번 나가고 문서에는
            // 하나만 남는데 사라진 쪽은 아무 데도 보고되지 않는다.
            Assert.Null(BatchStepPlanParser.TryParse(
                "```json\n{ \"Steps\": [ " +
                "{ \"Code\": \"S01\", \"Name\": \"날짜 검증\" }, " +
                "{ \"Code\": \"S01\", \"Name\": \"영업일 잠금\" } ] }\n```"));
        }

        [Fact]
        public void TryParse_WithStepCodesDifferingOnlyByCase_ReturnsNull()
        {
            // 섹션 사전과 헤딩 대조가 대소문자를 가리지 않으므로 s01과 S01도 겹친다.
            Assert.Null(BatchStepPlanParser.TryParse(
                "```json\n{ \"Steps\": [ " +
                "{ \"Code\": \"S01\", \"Name\": \"날짜 검증\" }, " +
                "{ \"Code\": \"s01\", \"Name\": \"영업일 잠금\" } ] }\n```"));
        }

        [Fact]
        public void TryParse_WithStepMissingCode_ReturnsNull()
        {
            Assert.Null(BatchStepPlanParser.TryParse(
                "```json\n{ \"Steps\": [ { \"Name\": \"이름만 있다\" } ] }\n```"));
        }

        /// <summary>
        /// Steps 배열 원소가 객체가 아니면 ReadString의 TryGetProperty가
        /// InvalidOperationException을 던진다. JsonException만 잡던 TryParseBlock은
        /// 이 예외를 놓친다 - null을 돌려주는 대신 예외가 호출부까지 뚫고 나간다.
        /// </summary>
        [Theory]
        [InlineData("\"S01\"")]
        [InlineData("null")]
        public void TryParse_WithNonObjectStepElement_ReturnsNull(string element)
        {
            var markdown = "```json\n{ \"Steps\": [ " + element + " ] }\n```";

            Assert.Null(BatchStepPlanParser.TryParse(markdown));
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

        /// <summary>
        /// 선택기가 돌려주는 범위는 원본 마크다운에서 그대로 잘라낼 수 있어야 한다.
        /// 보강기가 이 범위만 갈아 끼우므로, 어긋나면 펜스가 깨지거나 산문이 잘린다.
        /// </summary>
        [Fact]
        public void TryLocateStepsBlock_ShouldPointAtTheExactBodySpan()
        {
            var markdown = """
# 목차

산문

```json
{ "Steps": [ { "Code": "S01", "Name": "첫 단계" } ] }
```

뒤 산문
""";

            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(located);
            Assert.Equal(
                located!.Value.Body,
                markdown.Substring(located.Value.BodyIndex, located.Value.BodyLength));
            Assert.Single(located.Value.Steps);
            Assert.Equal("S01", located.Value.Steps[0].Code);
        }

        /// <summary>
        /// 파서가 버리는 블록은 선택기도 버린다. 이 성질이 보강기와의 일치를 만든다 -
        /// 두 곳이 각자 판정하면 첫 블록에서 갈린다.
        /// </summary>
        [Fact]
        public void TryLocateStepsBlock_ShouldSkipBlocksTheParserRejects()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Name": "Code가 없는 항목" } ] }
```

```json
{ "Steps": [ { "Code": "S02", "Name": "성한 항목" } ] }
```
""";

            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(located);
            Assert.Contains("S02", located!.Value.Body);
            Assert.DoesNotContain("Code가 없는 항목", located.Value.Body);
        }

        [Fact]
        public void TryLocateStepsBlock_NoValidBlock_ShouldReturnNull()
        {
            var markdown = """
# 목차

```json
{ "NotSteps": [] }
```
""";

            Assert.Null(BatchStepPlanParser.TryLocateStepsBlock(markdown));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryLocateStepsBlock_BlankInput_ShouldReturnNull(string? markdown)
        {
            Assert.Null(BatchStepPlanParser.TryLocateStepsBlock(markdown));
        }

        /// <summary>
        /// TryParse는 선택기 위의 얇은 껍데기다. 두 결과가 갈리면 파서 안에서 이미
        /// 목차가 둘로 나뉜 것이다.
        /// </summary>
        [Fact]
        public void TryParse_ShouldReturnTheLocatedBlocksSteps()
        {
            var markdown = """
```json
{ "Steps": [ { "Code": "S01", "Name": "첫 단계" }, { "Code": "S02", "Name": "둘째 단계" } ] }
```
""";

            var parsed = BatchStepPlanParser.TryParse(markdown);
            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(parsed);
            Assert.NotNull(located);
            Assert.Equal(parsed!.Select(s => s.Code), located!.Value.Steps.Select(s => s.Code));
        }

        [Fact]
        public void TryParse_ShouldReadSchemaTables()
        {
            var markdown = @"```json
{
  ""Steps"": [
    {
      ""Code"": ""S01"",
      ""Name"": ""요율 스냅샷 생성"",
      ""LegacyProcedures"": [""UP_X""],
      ""TargetTables"": [""DB.dbo.TWrite""],
      ""SchemaTables"": [""DB.dbo.TWrite"", ""DB.dbo.TRead""],
      ""ErrorCodes"": [""-1""]
    }
  ]
}
```";

            var steps = BatchStepPlanParser.TryParse(markdown);

            Assert.NotNull(steps);
            Assert.Equal(new[] { "DB.dbo.TWrite", "DB.dbo.TRead" }, steps![0].SchemaTables);
        }

        [Fact]
        public void TryParse_ShouldTreatAMissingSchemaTablesAsEmpty()
        {
            // 이 브랜치 이전에 만들어진 목차에는 이 필드가 없다. 없으면 빈 목록이고,
            // DependenciesForStep이 종전처럼 전체 목록으로 폴백한다.
            var markdown = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""옛 목차"", ""TargetTables"": [""DB.dbo.TWrite""] }
  ]
}
```";

            var steps = BatchStepPlanParser.TryParse(markdown);

            Assert.NotNull(steps);
            Assert.Empty(steps![0].SchemaTables);
        }
    }
}
