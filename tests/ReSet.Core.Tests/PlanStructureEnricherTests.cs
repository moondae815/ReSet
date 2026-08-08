using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PlanStructureEnricherTests
    {
        private const string Structure = @"# 목차

산문은 그대로 보존되어야 한다.

### 기계 판독 실행 단계 목록

```json
{
  ""Steps"": [
    {
      ""Code"": ""S00"",
      ""Name"": ""실행 잠금 사전검증"",
      ""LegacyProcedures"": [],
      ""TargetTables"": [""dbo.BatchExecution""],
      ""ErrorCodes"": [],
      ""Chunkable"": false
    },
    {
      ""Code"": ""S01"",
      ""Name"": ""수수료율 스냅샷"",
      ""LegacyProcedures"": [""UP_Util_PG_Client_CMRate_Ins""],
      ""TargetTables"": [""dbo.TPGSettleRate""],
      ""ErrorCodes"": [""-9""],
      ""Chunkable"": true
    },
    {
      ""Code"": ""S02"",
      ""Name"": ""기본 정산원장 생성"",
      ""LegacyProcedures"": [""dbo.UP_UTIL_SETTLE_INS""],
      ""TargetTables"": [""dbo.TSettleMst""],
      ""ErrorCodes"": [],
      ""Chunkable"": false
    }
  ]
}
```

꼬리 산문도 보존되어야 한다.
";

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> Codes() =>
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_util_pg_client_cmrate_ins"] = new[] { "-1", "-9", "-10" },
                ["up_util_settle_ins"] = new[] { "-1", "-2" },
            };

        private static BatchStepPlan Step(string markdown, string code) =>
            BatchStepPlanParser.TryParse(markdown)!.Single(s => s.Code == code);

        [Fact]
        public void Enrich_ShouldFillAnEmptyErrorCodeArray()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.Equal(new[] { "-1", "-2" }, Step(enriched, "S02").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldUnionWithWhatThePlanAlreadyDeclared()
        {
            // 목차 선언이 먼저, 그다음 명세서 등장 순서. 결정론을 위해 순서를 고정한다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.Equal(new[] { "-9", "-1", "-10" }, Step(enriched, "S01").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldLeaveStepsWithNoLegacyProcedureEmpty()
        {
            // 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.Empty(Step(enriched, "S00").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldMatchProcedureNamesIgnoringSchemaPrefixAndCase()
        {
            // 목차는 "dbo.UP_UTIL_SETTLE_INS", 명세서 키는 "up_util_settle_ins"다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.NotEmpty(Step(enriched, "S02").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldPreserveOtherFields()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());
            var s01 = Step(enriched, "S01");

            Assert.True(s01.Chunkable);
            Assert.Equal("수수료율 스냅샷", s01.Name);
            Assert.Equal(new[] { "dbo.TPGSettleRate" }, s01.TargetTables);
            Assert.Equal(new[] { "UP_Util_PG_Client_CMRate_Ins" }, s01.LegacyProcedures);
        }

        [Fact]
        public void Enrich_ShouldKeepKoreanTextUnescaped()
        {
            // JsonSerializer의 기본 인코더는 비ASCII를 \uXXXX로 이스케이프한다.
            // 그대로 두면 PlanStructure.md의 한글 단계명이 사람이 못 읽는 문자열이 된다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.Contains("수수료율 스냅샷", enriched);
            Assert.DoesNotContain("\\u", enriched);
        }

        [Fact]
        public void Enrich_ShouldPreserveProseOutsideTheJsonBlock()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());

            Assert.Contains("산문은 그대로 보존되어야 한다.", enriched);
            Assert.Contains("꼬리 산문도 보존되어야 한다.", enriched);
            Assert.Contains("### 기계 판독 실행 단계 목록", enriched);
        }

        [Fact]
        public void Enrich_ShouldBeIdempotent()
        {
            // 목차는 재수립·구제 채택 경로에서 여러 번 오간다. 두 번 태워도 같아야 한다.
            var once = PlanStructureEnricher.Enrich(Structure, Codes());
            var twice = PlanStructureEnricher.Enrich(once, Codes());

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Enrich_ShouldReturnInputUnchangedWhenThereIsNoJsonBlock()
        {
            const string noBlock = "# 목차\n\nJSON 블록이 없다.";

            Assert.Equal(noBlock, PlanStructureEnricher.Enrich(noBlock, Codes()));
        }

        [Fact]
        public void Enrich_ShouldReturnInputUnchangedWhenTheJsonIsBroken()
        {
            var broken = "# 목차\n\n```json\n{ \"Steps\": [ {{{ ]\n```\n";

            Assert.Equal(broken, PlanStructureEnricher.Enrich(broken, Codes()));
        }

        [Fact]
        public void Enrich_ShouldEnrichTheSameBlockTheParserReads()
        {
            // 파서는 첫 번째 '유효한' 블록을 고른다. 보강기가 다른 블록을 고르면
            // 파일에 기록된 목차와 실제로 쓰이는 목차가 갈라진다.
            var withDecoy = "```json\n{ \"NotSteps\": 1 }\n```\n\n" + Structure;

            var enriched = PlanStructureEnricher.Enrich(withDecoy, Codes());

            Assert.Equal(new[] { "-1", "-2" }, Step(enriched, "S02").ErrorCodes);
            Assert.Contains("NotSteps", enriched);
        }

        [Fact]
        public void Enrich_RoundTripsThroughTheParser()
        {
            // 이 계약이 깨지면 파일에 기록된 값과 검사에 쓰인 값이 갈라진다 -
            // 지금 고치려는 결함과 정확히 같은 종류다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes());
            var parsed = BatchStepPlanParser.TryParse(enriched);

            Assert.NotNull(parsed);
            Assert.Equal(3, parsed!.Count);
            Assert.Equal(new[] { "-1", "-2" }, parsed.Single(s => s.Code == "S02").ErrorCodes);
        }
    }
}
