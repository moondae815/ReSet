using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    [Collection(GlobalSerilogLoggerCollection.Name)]
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

        // 오류코드만 검사하는 기존 테스트들은 대상 테이블 재료가 없다. 빈 사전을
        // 넘기면 RewriteTables가 아무 것도 못 찾아 TargetTables를 그대로 둔다 -
        // 종전 동작과 같다.
        private static readonly IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> EmptyTables =
            new Dictionary<string, SpecTargetTableExtractor.StepTableSets>();

        private static BatchStepPlan Step(string markdown, string code) =>
            BatchStepPlanParser.TryParse(markdown)!.Single(s => s.Code == code);

        [Fact]
        public void Enrich_ShouldFillAnEmptyErrorCodeArray()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.Equal(new[] { "-1", "-2" }, Step(enriched, "S02").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldUnionWithWhatThePlanAlreadyDeclared()
        {
            // 목차 선언이 먼저, 그다음 명세서 등장 순서. 결정론을 위해 순서를 고정한다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.Equal(new[] { "-9", "-1", "-10" }, Step(enriched, "S01").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldLeaveStepsWithNoLegacyProcedureEmpty()
        {
            // 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.Empty(Step(enriched, "S00").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldMatchProcedureNamesIgnoringSchemaPrefixAndCase()
        {
            // 목차는 "dbo.UP_UTIL_SETTLE_INS", 명세서 키는 "up_util_settle_ins"다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.NotEmpty(Step(enriched, "S02").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldPreserveOtherFieldsWhenMergingErrorCodes()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;
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
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.Contains("수수료율 스냅샷", enriched);
            Assert.DoesNotContain("\\u", enriched);
        }

        [Fact]
        public void Enrich_ShouldPreserveProseOutsideTheJsonBlock()
        {
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;

            Assert.Contains("산문은 그대로 보존되어야 한다.", enriched);
            Assert.Contains("꼬리 산문도 보존되어야 한다.", enriched);
            Assert.Contains("### 기계 판독 실행 단계 목록", enriched);
        }

        [Fact]
        public void Enrich_ShouldBeIdempotent()
        {
            // 목차는 재수립·구제 채택 경로에서 여러 번 오간다. 두 번 태워도 같아야 한다.
            var once = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;
            var twice = PlanStructureEnricher.Enrich(once, Codes(), EmptyTables).Markdown;

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Enrich_ShouldReturnInputUnchangedWhenThereIsNoJsonBlock()
        {
            const string noBlock = "# 목차\n\nJSON 블록이 없다.";

            Assert.Equal(noBlock, PlanStructureEnricher.Enrich(noBlock, Codes(), EmptyTables).Markdown);
        }

        [Fact]
        public void Enrich_ShouldReturnInputUnchangedWhenTheJsonIsBroken()
        {
            var broken = "# 목차\n\n```json\n{ \"Steps\": [ {{{ ]\n```\n";

            Assert.Equal(broken, PlanStructureEnricher.Enrich(broken, Codes(), EmptyTables).Markdown);
        }

        /// <summary>
        /// Steps 배열 원소가 객체가 아니면 선택기가 부르는 TryParseBlock 안에서
        /// InvalidOperationException이 난다. 클래스 docstring의 계약("실패는 예외가
        /// 아니라 원본 반환이다")대로 Enrich는 이 입력에서도 원본을 그대로 돌려줘야
        /// 한다 - 예외가 호출부(재수립 헬퍼 포함)까지 뚫고 나가면 안 된다.
        /// </summary>
        [Theory]
        [InlineData("\"S01\"")]
        [InlineData("null")]
        public void Enrich_ShouldReturnInputUnchangedWhenAStepElementIsNotAnObject(string element)
        {
            var markdown = "# 목차\n\n```json\n{ \"Steps\": [ " + element + " ] }\n```\n";

            var enriched = PlanStructureEnricher.Enrich(markdown, Codes(), EmptyTables).Markdown;

            Assert.Equal(markdown, enriched);
        }

        [Fact]
        public void Enrich_ShouldEnrichTheSameBlockTheParserReads()
        {
            // 파서는 첫 번째 '유효한' 블록을 고른다. 보강기가 다른 블록을 고르면
            // 파일에 기록된 목차와 실제로 쓰이는 목차가 갈라진다.
            var withDecoy = "```json\n{ \"NotSteps\": 1 }\n```\n\n" + Structure;

            var enriched = PlanStructureEnricher.Enrich(withDecoy, Codes(), EmptyTables).Markdown;

            Assert.Equal(new[] { "-1", "-2" }, Step(enriched, "S02").ErrorCodes);
            Assert.Contains("NotSteps", enriched);
        }

        [Fact]
        public void Enrich_ShouldReturnInputUnchangedWhenAStepObjectHasADuplicateKey()
        {
            // JsonNode.Parse는 중복 프로퍼티 이름 자체는 통과시키지만, 나중에
            // TryGetPropertyValue를 부르는 순간 ArgumentException을 던진다
            // (JsonException이 아니다). 이 경로를 못 잡으면 예외가 Enrich 밖으로
            // 새 나가 파이프라인을 죽인다.
            const string duplicateKey = @"# 목차

```json
{
  ""Steps"": [
    {
      ""Code"": ""S01"",
      ""Name"": ""중복 키 단계"",
      ""LegacyProcedures"": [""UP_Util_PG_Client_CMRate_Ins""],
      ""TargetTables"": [""dbo.TPGSettleRate""],
      ""ErrorCodes"": [],
      ""ErrorCodes"": [],
      ""Chunkable"": true
    }
  ]
}
```
";

            var enriched = PlanStructureEnricher.Enrich(duplicateKey, Codes(), EmptyTables).Markdown;

            Assert.Equal(duplicateKey, enriched);
        }

        [Fact]
        public void Enrich_ShouldIgnoreNonStringItemsInLegacyProcedures()
        {
            // ReadStringArray의 `item is not JsonValue value || !value.TryGetValue(out
            // string? text)` 방어가 실제로 도는지 확인한다. 숫자 항목은 프로시저명이
            // 될 수 없으므로 무시되고, 나머지 문자열 항목만으로 보강이 진행되어야 한다.
            const string mixed = @"# 목차

```json
{
  ""Steps"": [
    {
      ""Code"": ""S01"",
      ""Name"": ""혼합 배열 단계"",
      ""LegacyProcedures"": [""UP_Util_PG_Client_CMRate_Ins"", 123],
      ""TargetTables"": [""dbo.TPGSettleRate""],
      ""ErrorCodes"": [],
      ""Chunkable"": true
    }
  ]
}
```
";

            var enriched = PlanStructureEnricher.Enrich(mixed, Codes(), EmptyTables).Markdown;

            Assert.Equal(new[] { "-1", "-9", "-10" }, Step(enriched, "S01").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldIgnoreNullItemsInErrorCodes()
        {
            // JSON null은 JsonValue가 아니므로 같은 방어가 걸러야 한다. 나머지 선언값과
            // 명세서 추출분은 정상적으로 합쳐져야 한다.
            const string mixed = @"# 목차

```json
{
  ""Steps"": [
    {
      ""Code"": ""S01"",
      ""Name"": ""혼합 배열 단계"",
      ""LegacyProcedures"": [""UP_Util_PG_Client_CMRate_Ins""],
      ""TargetTables"": [""dbo.TPGSettleRate""],
      ""ErrorCodes"": [""-9"", null],
      ""Chunkable"": true
    }
  ]
}
```
";

            var enriched = PlanStructureEnricher.Enrich(mixed, Codes(), EmptyTables).Markdown;

            Assert.Equal(new[] { "-9", "-1", "-10" }, Step(enriched, "S01").ErrorCodes);
        }

        [Fact]
        public void Enrich_RoundTripsThroughTheParser()
        {
            // 이 계약이 깨지면 파일에 기록된 값과 검사에 쓰인 값이 갈라진다 -
            // 지금 고치려는 결함과 정확히 같은 종류다.
            var enriched = PlanStructureEnricher.Enrich(Structure, Codes(), EmptyTables).Markdown;
            var parsed = BatchStepPlanParser.TryParse(enriched);

            Assert.NotNull(parsed);
            Assert.Equal(3, parsed!.Count);
            Assert.Equal(new[] { "-1", "-2" }, parsed.Single(s => s.Code == "S02").ErrorCodes);
        }

        [Fact]
        public void Enrich_ShouldLogWhenExtractionYieldedNoCodesAtAll()
        {
            // codesByProcedure와 tablesByProcedure가 통째로 비면 원본을 그대로
            // 돌려주는데, 흔적이 없으면 "보강이 돌았는데 못 채운 것"과 "추출이 0건이라
            // 시작조차 안 된 것"을 운영자가 로그만 보고 구별할 수 없다.
            var empty = new Dictionary<string, IReadOnlyList<string>>();

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
            try
            {
                var enriched = PlanStructureEnricher.Enrich(Structure, empty, EmptyTables).Markdown;
                Assert.Equal(Structure, enriched);
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            Assert.Contains(sink.Messages, m => m.Contains("보강 재료가 없어") && m.Contains("보강을 건너뜁니다"));
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }

        /// <summary>
        /// 파서가 버리는 블록이 앞에 있으면 보강도 그 블록을 건너뛰고 파서와 같은 블록을
        /// 골라야 한다. 종전에는 보강기가 자기 기준으로 첫 블록을 받아들여, 파일에 기록된
        /// 목차와 파이프라인이 쓰는 목차가 갈렸다.
        /// </summary>
        [Fact]
        public void Enrich_FirstBlockRejectedByParser_ShouldEnrichTheBlockTheParserReads()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Name": "Code가 없어 파서가 버리는 항목", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```

```json
{ "Steps": [ { "Code": "S01", "Name": "성한 항목", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```
""";

            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_a"] = new[] { "-101", "-102" }
            };

            var enriched = PlanStructureEnricher.Enrich(markdown, codes, EmptyTables).Markdown;

            // 파서가 읽는 블록(둘째)에만 코드가 들어가야 한다.
            var located = BatchStepPlanParser.TryLocateStepsBlock(enriched);
            Assert.NotNull(located);
            Assert.Equal(new[] { "-101", "-102" }, located!.Value.Steps[0].ErrorCodes);

            // 첫 블록은 손대지 않는다.
            Assert.Contains("Code가 없어 파서가 버리는 항목", enriched);
            var firstBlockEnd = enriched.IndexOf("```", enriched.IndexOf("Code가 없어", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("-101", enriched[..firstBlockEnd]);
        }

        /// <summary>
        /// 중복 프로퍼티 이름이 있는 블록은 JsonNode가 던져 보강할 수 없다. 그때 뒤 블록으로
        /// 넘어가면 파서가 읽는 블록(앞의 것)과 갈린다. 보강을 포기하는 편이 옳다 -
        /// 보강되지 않은 단계는 하한 검사가 "검증 불가"로 보고한다.
        /// </summary>
        [Fact]
        public void Enrich_DuplicateKeysInTheParsedBlock_ShouldNotFallThroughToAnotherBlock()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Code": "S01", "Name": "중복 키", "LegacyProcedures": ["UP_A"], "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```

```json
{ "Steps": [ { "Code": "S99", "Name": "뒤 블록", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```
""";

            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_a"] = new[] { "-101" }
            };

            var enriched = PlanStructureEnricher.Enrich(markdown, codes, EmptyTables).Markdown;

            // 아무것도 보강되지 않는다. 특히 뒤 블록이 조용히 보강되면 안 된다.
            Assert.Equal(markdown, enriched);
        }

        /// <summary>
        /// 블록이 하나뿐인 정상 목차에서는 종전과 같은 결과여야 한다.
        ///
        /// "재포맷만으로도 통과한다"는 함정을 피하려면 seed한 코드가 실제로 산출물에
        /// 도착했는지까지 확인해야 한다 - Structure의 S01은 LegacyProcedures로
        /// "UP_Util_PG_Client_CMRate_Ins"를 선언하므로 그 bare name과 일치하는
        /// 코드를 seed한다.
        /// </summary>
        [Fact]
        public void Enrich_SingleValidBlock_ShouldStillEnrichInPlace()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_util_pg_client_cmrate_ins"] = new[] { "-201" }
            };

            var enriched = PlanStructureEnricher.Enrich(Structure, codes, EmptyTables).Markdown;

            Assert.Contains("산문은 그대로 보존되어야 한다.", enriched);
            Assert.NotEqual(Structure, enriched);
            Assert.Contains("-201", Step(enriched, "S01").ErrorCodes);
        }

        /// <summary>
        /// 블록 추출 정규식은 소스 트리에 정확히 한 번만 존재해야 한다. 두 벌이 되는 순간
        /// 한쪽만 고쳐지고, 그 갈림은 어디에도 드러나지 않는다.
        ///
        /// 찾는 것은 정규식 패턴 문자열이지 ```json 이라는 낱말이 아니다 - AiService는
        /// 프롬프트 본문에서 그 낱말을 여러 번 쓰고 그것들은 이 검사의 대상이 아니다.
        /// </summary>
        [Fact]
        public void JsonBlockRegexLiteral_ShouldExistExactlyOnceInSourceTree()
        {
            const string literal = @"```json\s*\r?\n(?<body>.*?)```";
            var srcRoot = Path.Combine(RepoPaths.FindRepoRoot(), "src");

            var separator = Path.DirectorySeparatorChar;
            var hits = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                    !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
                .Where(path => File.ReadAllText(path).Contains(literal, StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(srcRoot, path).Replace(separator, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "ReSet.Core/Services/BatchStepPlan.cs" }, hits);
        }

        private static IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> Tables(
            params (string Procedure, string[] Write, string[] Read)[] items)
        {
            var map = new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var (procedure, write, read) in items)
            {
                map[procedure] = new SpecTargetTableExtractor.StepTableSets(write, read);
            }

            return map;
        }

        private const string OneStep = @"```json
{
  ""Steps"": [
    {
      ""Code"": ""S11"",
      ""Name"": ""취소영향 요약 보정"",
      ""LegacyProcedures"": [""UP_UTIL_SETTLE_SUMMARY_ETC""],
      ""TargetTables"": [""TSettleByTX"", ""TPartialCancelByTX"", ""TSettleByIN"", ""TSettleByOUT""],
      ""ErrorCodes"": [""-1""],
      ""Chunkable"": true
    }
  ]
}
```";

        [Fact]
        public void Enrich_ShouldReplaceTargetTablesWithTheExtractedWriteSet()
        {
            var result = PlanStructureEnricher.Enrich(
                OneStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                    new[] { "SETTLE_POQ_DB.dbo.TSettleMst" })));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" }, step.TargetTables);
        }

        [Fact]
        public void Enrich_ShouldReportDeclarationsTheStaticAnalysisDoesNotHave()
        {
            // 실측: API 회차의 S11이 네 개를 선언했고 셋은 원본 DDL에 0회 등장한다.
            // 합집합했다면 그 허위가 검증 요건으로 승격돼 재생성이 고착시켰을 것이다.
            var result = PlanStructureEnricher.Enrich(
                OneStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                    System.Array.Empty<string>())));

            var reported = Assert.Single(result.DroppedTableDeclarations);
            Assert.Contains("S11", reported);
            Assert.Contains("TSettleByTX", reported);
            Assert.Contains("TPartialCancelByTX", reported);
            Assert.Contains("TSettleByIN", reported);
            Assert.DoesNotContain("TSettleByOUT", reported);
        }

        [Fact]
        public void Enrich_ShouldKeepTheDeclaredTablesWhenNothingWasExtracted()
        {
            // 파싱 실패나 대상 0개인 프로시저에서 기존값을 지우면 멀쩡한 단계가
            // "검증 불가"로 떨어진다. 재료를 0으로 만들지 않는다.
            var result = PlanStructureEnricher.Enrich(
                OneStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    System.Array.Empty<string>(),
                    new[] { "SETTLE_POQ_DB.dbo.TSettleMst" })));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Equal(4, step.TargetTables.Count);
            Assert.Empty(result.DroppedTableDeclarations);
        }

        [Fact]
        public void Enrich_ShouldFillSchemaTablesWithWritesAndReads()
        {
            var result = PlanStructureEnricher.Enrich(
                OneStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                    new[] { "SETTLE_POQ_DB.dbo.TSettleMst" })));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT", "SETTLE_POQ_DB.dbo.TSettleMst" },
                step.SchemaTables);
        }

        [Fact]
        public void Enrich_ShouldPreserveOtherFields()
        {
            var result = PlanStructureEnricher.Enrich(
                OneStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                    System.Array.Empty<string>())));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.True(step.Chunkable);
            Assert.Equal(new[] { "-1" }, step.ErrorCodes);
            Assert.Equal("취소영향 요약 보정", step.Name);
        }

        [Fact]
        public void Enrich_ShouldBeIdempotentForTables()
        {
            var tables = Tables(("up_util_settle_summary_etc",
                new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" }));
            var codes = new Dictionary<string, IReadOnlyList<string>>();

            var once = PlanStructureEnricher.Enrich(OneStep, codes, tables);
            var twice = PlanStructureEnricher.Enrich(once.Markdown, codes, tables);

            Assert.Equal(once.Markdown, twice.Markdown);
            Assert.Empty(twice.DroppedTableDeclarations);
        }

        [Fact]
        public void Enrich_ShouldLeaveStepsWithoutLegacyProceduresAlone()
        {
            const string designedStep = @"```json
{
  ""Steps"": [
    { ""Code"": ""S00"", ""Name"": ""실행 잠금 사전검증"", ""LegacyProcedures"": [], ""TargetTables"": [] }
  ]
}
```";

            var result = PlanStructureEnricher.Enrich(
                designedStep,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_x", new[] { "DB.dbo.T" }, System.Array.Empty<string>())));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Empty(step.TargetTables);
            Assert.Empty(step.SchemaTables);
        }

        [Fact]
        public void Enrich_ShouldIgnoreNonStringEntriesInLegacyProcedures()
        {
            // 가드가 없으면 숫자 123이 문자열로 읽혀 codesByProcedure의 "123" 키에
            // 매칭된다. 그 키를 실제로 채워 두어야 가드 제거가 테스트를 깬다.
            const string numericProcedure = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""숫자 항목"", ""LegacyProcedures"": [123], ""ErrorCodes"": [] }
  ]
}
```";

            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["123"] = new[] { "-99" },
            };

            var result = PlanStructureEnricher.Enrich(
                numericProcedure,
                codes,
                new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Empty(step.ErrorCodes);
        }
    }
}
