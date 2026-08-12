# 목차 대상 테이블 보강 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 목차(`raw/PlanStructure.md`)의 `TargetTables`를 정적 분석의 쓰기 대상으로 채우고, 회차 지시서의 DDL 스코프를 위한 `SchemaTables`를 별도로 두어 검증 재료와 스코프 재료를 가른다.

**Architecture:** `SpecTargetTableExtractor`(신규 순수 함수)가 `SpDefinition` 목록에서 `프로시저 맨이름 → (쓰기, 읽기)` 사전을 만든다. `PlanStructureEnricher`가 그 사전으로 목차 JSON의 `TargetTables`를 교체하고 `SchemaTables`를 채운다. `MechanicalValidator.ValidateBatchStep`은 `TargetTables`로 대조하고, `InstructionBundleWriter.DependenciesForStep`은 `SchemaTables`로 DDL을 좁힌다. 오케스트레이터가 새 선택적 매개변수로 `SpDefinition` 목록을 받고, `Program.cs`의 두 진입 경로가 그것을 넘긴다.

**Tech Stack:** C# / .NET 10, xUnit, `System.Text.Json`(`JsonNode` 재작성 / `JsonDocument` 파싱), Serilog

**설계 문서:** `docs/superpowers/specs/2026-08-12-target-tables-enrichment-design.md`

## Global Constraints

- 빌드는 오류 0건, 경고 **정확히 8건**(기존 `DbMetadataServiceTests`의 CS8600/CS8602)을 유지한다. 새 경고를 만들지 않는다.
- 기준선 테스트 수는 **1,318건**이다. 최종 수가 `1,318 + 신규분`과 어긋나면 의도치 않은 테스트 증감이 있었다는 뜻이다.
- 새 예외 경로를 만들지 않는다. `PlanStructureEnricher`에 넣는 코드는 반드시 `TryRewriteBlock`의 `try` **안**에 있어야 한다.
- 모든 주석과 로그 문구는 한국어로 쓴다(기존 코드 관례).
- 프롬프트 계약을 바꾸지 않는다. `AiService`에 `SchemaTables`를 요구하는 문구를 추가하지 않는다.
- 커밋 메시지는 영어, `feat:`/`fix:`/`test:`/`docs:`/`refactor:` 접두어를 쓰고 본문에 왜를 적는다. 끝에 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`를 붙인다.
- 각 태스크는 `dotnet build`와 `dotnet test`가 통과한 상태로 끝난다.

## File Structure

| 파일 | 책임 | 상태 |
|---|---|---|
| `src/ReSet.Core/Services/SpecTargetTableExtractor.cs` | `SpDefinition` → `프로시저 맨이름 → (쓰기, 읽기)` 사전. 순수 함수 | 신규 |
| `src/ReSet.Core/Services/BatchStepPlan.cs` | `BatchStepPlan`에 `SchemaTables` 추가, 파서가 읽음 | 수정 |
| `src/ReSet.Core/Services/PlanStructureEnricher.cs` | 목차 JSON의 `TargetTables` 교체 + `SchemaTables` 채움 + 버린 선언 보고 | 수정 |
| `src/ReSet.Core/Services/InstructionBundleWriter.cs` | DDL 스코프 원천을 `TargetTables` → `SchemaTables`로 교체 | 수정 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | `definitions` 매개변수, 추출 호출, 보강 결과의 경고 표시 | 수정 |
| `src/ReSet.Cli/Program.cs` | 두 진입 경로에서 `SpDefinition` 목록 전달 | 수정 |
| `tests/ReSet.Core.Tests/SpecTargetTableExtractorTests.cs` | 추출기 단위 테스트 | 신규 |
| `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs` | 보강 규칙·멱등·왕복 | 수정 |
| `tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs` | `SchemaTables` 파싱·하위 호환 | 수정 |
| `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs` | 스코프 좁히기·폴백 | 수정 |
| `tests/ReSet.Core.Tests/Fixtures/S11PlanStructureExcerpt.md` | 실측 S11 회귀 픽스처 | 신규 |

`MechanicalValidator`는 **수정하지 않는다.** `ValidateBatchStep`은 이미 `TargetTables`를 대조하고 있고, 이 작업은 그 필드를 채우기만 한다.

---

### Task 1: `SpecTargetTableExtractor` 신설

**Files:**
- Create: `src/ReSet.Core/Services/SpecTargetTableExtractor.cs`
- Test: `tests/ReSet.Core.Tests/SpecTargetTableExtractorTests.cs`

**Interfaces:**
- Consumes: `ReSet.Core.Models.SpDefinition`(`Name`, `StaticAnalysis`), `SpecReturnCodeExtractor.BareName(string)`
- Produces:
  - `public sealed record StepTableSets(IReadOnlyList<string> WriteTables, IReadOnlyList<string> ReadTables)`
  - `public static IReadOnlyDictionary<string, StepTableSets> Extract(IEnumerable<SpDefinition>? definitions)`
  - `public static string BareTableName(string tableName)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SpecTargetTableExtractorTests.cs`를 만든다.

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecTargetTableExtractorTests
    {
        // 픽스처는 실측 SP(dbo.UP_Util_PG_Client_CMRate_Ins)의 정적 분석을 그대로 옮긴 것이다.
        // 두 제공자 회차가 모두 이 단계의 TargetTables를 빈 배열로 냈고, 정적 분석에는
        // 대상이 다 들어 있었다 - 이 작업이 존재하는 이유다.
        private static SpDefinition RateSnapshotSp() => new()
        {
            Schema = "dbo",
            Name = "UP_Util_PG_Client_CMRate_Ins",
            StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                InsertTables =
                {
                    "SETTLE_POQ_DB.dbo.TPGSettleRate",
                    "SETTLE_POQ_DB.dbo.TClientSettleRate",
                },
                DeleteTables =
                {
                    "SETTLE_POQ_DB.dbo.TPGSettleRate",
                },
                SelectTables =
                {
                    "SETTLE_POQ_DB.dbo.TSettleMst",
                    "SETTLE_POQ_DB.dbo.TClient",
                },
            },
        };

        [Fact]
        public void Extract_ShouldSplitWriteTargetsFromReadSources()
        {
            var result = SpecTargetTableExtractor.Extract(new[] { RateSnapshotSp() });

            var sets = result["up_util_pg_client_cmrate_ins"];
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TPGSettleRate", "SETTLE_POQ_DB.dbo.TClientSettleRate" },
                sets.WriteTables);
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TClient" },
                sets.ReadTables);
        }

        [Fact]
        public void Extract_ShouldUseTheSameKeyRuleAsTheReturnCodeExtractor()
        {
            // 두 추출기가 다른 키 규칙을 쓰면 목차의 LegacyProcedures가 한쪽에만 매칭된다.
            var result = SpecTargetTableExtractor.Extract(new[] { RateSnapshotSp() });

            Assert.True(result.ContainsKey(
                SpecReturnCodeExtractor.BareName("dbo.UP_Util_PG_Client_CMRate_Ins")));
        }

        [Fact]
        public void Extract_ShouldExcludeTempTablesAndTableVariables()
        {
            // 임시 테이블과 테이블 변수는 물리 테이블이 아니라 DDL도 없다. 검증에 걸면
            // 존재하지 않는 요건을 만들고, 그것은 재생성으로 고칠 수 없다.
            var sp = new SpDefinition
            {
                Name = "UP_X",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    InsertTables = { "#TMP", "##Global", "SETTLE_POQ_DB.dbo.TReal" },
                    SelectTables = { "@Buffer", "SETTLE_POQ_DB.dbo.TSource" },
                },
            };

            var sets = SpecTargetTableExtractor.Extract(new[] { sp })["up_x"];

            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TReal" }, sets.WriteTables);
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSource" }, sets.ReadTables);
        }

        [Fact]
        public void Extract_ShouldNotCreateAKeyWhenNothingWasFound()
        {
            // 빈 목록과 "그런 프로시저 없음"이 같아지면 보강기가 둘을 구별할 수 없다.
            var sp = new SpDefinition { Name = "UP_Empty", StaticAnalysis = new SpStaticAnalysisResult() };

            Assert.Empty(SpecTargetTableExtractor.Extract(new[] { sp }));
        }

        [Fact]
        public void Extract_ShouldSurviveANullStaticAnalysis()
        {
            var sp = new SpDefinition { Name = "UP_Null", StaticAnalysis = null! };

            Assert.Empty(SpecTargetTableExtractor.Extract(new[] { sp }));
        }

        [Fact]
        public void Extract_ShouldMergeTwoDefinitionsThatShareABareName()
        {
            var first = new SpDefinition
            {
                Name = "dbo.UP_Dup",
                StaticAnalysis = new SpStaticAnalysisResult { InsertTables = { "DB.dbo.TA" } },
            };
            var second = new SpDefinition
            {
                Name = "other.UP_Dup",
                StaticAnalysis = new SpStaticAnalysisResult { InsertTables = { "DB.dbo.TB" } },
            };

            var sets = SpecTargetTableExtractor.Extract(new[] { first, second })["up_dup"];

            Assert.Equal(new[] { "DB.dbo.TA", "DB.dbo.TB" }, sets.WriteTables);
        }

        [Fact]
        public void BareTableName_ShouldStripQualifiersAndBrackets()
        {
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("SETTLE_POQ_DB.dbo.TSettleMst"));
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("[dbo].[TSettleMst]"));
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("TSettleMst"));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecTargetTableExtractorTests"`
Expected: 컴파일 실패 — `SpecTargetTableExtractor`가 존재하지 않는다.

- [ ] **Step 3: 추출기를 구현한다**

`src/ReSet.Core/Services/SpecTargetTableExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석에서 단계의 대상 테이블과 참조 원본을 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 목차의 TargetTables는 AI가 채우는데, 같은 12개 SP를
    /// 두 제공자로 돌린 실측에서 7개와 17개가 나왔다. 같은 입력에 2.4배가 흔들린다.
    /// 두 회차 모두 S01을 빈 배열로 냈는데, 그 SP의 정적 분석에는 INSERT 대상 5개와
    /// DELETE 대상 5개가 들어 있었다 - 재료는 있고 목차까지 도달하지 않을 뿐이다.
    ///
    /// 오류코드와 달리 명세서 산문에서 뽑지 않는다. 대상 테이블은 파서가 AST에서
    /// 확정한 구조화된 데이터로 이미 존재하므로, 산문을 다시 해석하는 것은 정확도를
    /// 낮추기만 한다.
    /// </summary>
    public static class SpecTargetTableExtractor
    {
        /// <summary>
        /// 한 프로시저의 테이블 집합.
        /// </summary>
        /// <param name="WriteTables">INSERT/UPDATE/DELETE 대상. 하한 검사의 대조 기준이 된다.</param>
        /// <param name="ReadTables">SELECT 원본. 회차 지시서의 DDL 스코프에만 쓰인다.</param>
        public sealed record StepTableSets(
            IReadOnlyList<string> WriteTables,
            IReadOnlyList<string> ReadTables);

        public static IReadOnlyDictionary<string, StepTableSets> Extract(
            IEnumerable<SpDefinition>? definitions)
        {
            var result = new Dictionary<string, StepTableSets>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null)
            {
                return result;
            }

            foreach (var definition in definitions)
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.Name))
                {
                    continue;
                }

                var analysis = definition.StaticAnalysis;
                if (analysis == null)
                {
                    continue;
                }

                var write = new List<string>();
                var writeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAll(analysis.InsertTables, write, writeSeen);
                AddAll(analysis.UpdateTables, write, writeSeen);
                AddAll(analysis.DeleteTables, write, writeSeen);

                var read = new List<string>();
                var readSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAll(analysis.SelectTables, read, readSeen);

                // 둘 다 비면 키를 만들지 않는다. 빈 집합과 "그런 프로시저 없음"이
                // 같아지면 보강기가 "대상이 없는 단계"로 오해해 기존값을 지운다.
                if (write.Count == 0 && read.Count == 0)
                {
                    continue;
                }

                var key = SpecReturnCodeExtractor.BareName(definition.Name);

                // 같은 맨이름이 두 번 들어오면 덮어쓰지 않고 합친다. 덮어쓰면 앞
                // 항목의 대상이 조용히 사라진다.
                if (result.TryGetValue(key, out var existing))
                {
                    var mergedWrite = new List<string>(existing.WriteTables);
                    var mergedWriteSeen = new HashSet<string>(mergedWrite, StringComparer.OrdinalIgnoreCase);
                    AddAll(write, mergedWrite, mergedWriteSeen);

                    var mergedRead = new List<string>(existing.ReadTables);
                    var mergedReadSeen = new HashSet<string>(mergedRead, StringComparer.OrdinalIgnoreCase);
                    AddAll(read, mergedRead, mergedReadSeen);

                    result[key] = new StepTableSets(mergedWrite, mergedRead);
                    continue;
                }

                result[key] = new StepTableSets(write, read);
            }

            return result;
        }

        /// <summary>
        /// 목차의 짧은 표기("TSettleMst")와 정적 분석의 정식 표기
        /// ("SETTLE_POQ_DB.dbo.TSettleMst")를 대조하기 위한 맨 이름.
        ///
        /// 중복 제거에는 쓰지 않는다 - dbo.TPGProperty와 PaymentDB.dbo.TPGProperty는
        /// 맨 이름이 같아도 서로 다른 물리 테이블이다. 이 함수는 "모델이 선언한 이름이
        /// 추출 결과에 있는가"라는 관대한 비교에만 쓴다.
        /// </summary>
        public static string BareTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return string.Empty;
            }

            var text = tableName.Trim();
            var index = text.LastIndexOf('.');
            if (index >= 0)
            {
                text = text[(index + 1)..];
            }

            return text.Trim('[', ']', ' ').ToLowerInvariant();
        }

        private static void AddAll(IEnumerable<string>? source, List<string> target, HashSet<string> seen)
        {
            if (source == null)
            {
                return;
            }

            foreach (var name in source)
            {
                if (!IsPhysicalTable(name))
                {
                    continue;
                }

                var trimmed = name.Trim();
                if (seen.Add(trimmed))
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// 임시 테이블(#, ##)과 테이블 변수(@)를 걸러낸다. 물리 테이블이 아니라 DDL이
        /// 없고, 검증에 걸면 존재하지 않는 요건이 되어 재생성으로 고칠 수 없다.
        /// </summary>
        private static bool IsPhysicalTable(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var first = name.Trim()[0];
            return first != '#' && first != '@';
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecTargetTableExtractorTests"`
Expected: PASS (7건)

- [ ] **Step 5: 전체 스위트와 빌드를 확인한다**

Run: `dotnet build 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: 경고 8개 / 오류 0개, 테스트 1,325건 통과 (1,318 + 7)

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecTargetTableExtractor.cs tests/ReSet.Core.Tests/SpecTargetTableExtractorTests.cs
git commit -m "$(cat <<'EOF'
feat: extract write targets and read sources from the static analysis

The catalog's TargetTables are filled by the model, and two runs of the
same 12 SPs measured 7 and 17 from identical input. Both left S01 empty
while its static analysis held five INSERT targets and five DELETE ones.

Splitting writes from reads here lets the validator compare against what
the step modifies while the bundle writer scopes DDL by what it touches.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `BatchStepPlan.SchemaTables`와 파서 대응

**Files:**
- Modify: `src/ReSet.Core/Services/BatchStepPlan.cs`
- Test: `tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `BatchStepPlan`의 7번째 위치 매개변수 `IReadOnlyList<string> SchemaTables`

> **주의:** `BatchStepPlan`은 위치 레코드다. 생성자 호출부가 모두 바뀐다. `grep -rn "new BatchStepPlan(" src tests`로 전수 확인한 뒤 고친다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs`에 추가한다.

```csharp
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
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchStepPlanParserTests"`
Expected: 컴파일 실패 — `BatchStepPlan`에 `SchemaTables`가 없다.

- [ ] **Step 3: 레코드와 파서를 고친다**

`src/ReSet.Core/Services/BatchStepPlan.cs`의 레코드 선언을 바꾼다.

```csharp
    public sealed record BatchStepPlan(
        string Code,
        string Name,
        IReadOnlyList<string> LegacyProcedures,
        IReadOnlyList<string> TargetTables,
        IReadOnlyList<string> ErrorCodes,
        bool Chunkable,
        IReadOnlyList<string> SchemaTables);
```

레코드 XML 주석의 마지막 문단을 다음으로 교체한다.

```csharp
    /// 세 가지로 쓰인다: 분할 생성의 단위, 하한 검사의 기준(TargetTables/ErrorCodes),
    /// L2가 결함을 지목할 때의 좌표(Code).
    ///
    /// TargetTables와 SchemaTables를 나눠 두는 이유: 앞은 "본문이 이 테이블을
    /// 기술했는가"를 묻는 검증 재료이고, 뒤는 "이 회차 에이전트가 어떤 스키마를
    /// 봐야 하는가"를 정하는 스코프 재료다. 한 필드로 겸하면 읽기 원본을 넣을 때
    /// 검증이 과해지고, 빼면 에이전트가 SELECT를 쓸 스키마를 못 받는다.
    /// SchemaTables는 모델이 내지 않는다 - 도구가 정적 분석에서 채운다.
```

`TryParseBlock`의 `steps.Add(...)`를 바꾼다.

```csharp
                    steps.Add(new BatchStepPlan(
                        code.Trim(),
                        name.Trim(),
                        ReadStringArray(element, "LegacyProcedures"),
                        ReadStringArray(element, "TargetTables"),
                        ReadStringArray(element, "ErrorCodes"),
                        element.TryGetProperty("Chunkable", out var chunkable) &&
                            chunkable.ValueKind == JsonValueKind.True,
                        ReadStringArray(element, "SchemaTables")));
```

- [ ] **Step 4: 나머지 생성자 호출부를 고친다**

Run: `grep -rn "new BatchStepPlan(\|new(code, name" src tests`

각 호출부에 7번째 인자를 더한다. 테스트 픽스처는 `Array.Empty<string>()`을 쓰되, 스코프 동작을 검증하는 곳은 실제 값을 넣는다.

확인된 자리 하나는 `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`의 `Step` 헬퍼다. 이 헬퍼는 위치 인자로 6개를 넘긴다.

```csharp
        private static BatchStepPlan Step(string code, string name) =>
            new(code, name, new[] { "UP_" + code }, new[] { "dbo.T" }, new[] { "-1" }, false, new[] { "dbo.T" });
```

`SchemaTables`에 `TargetTables`와 같은 값을 넣어 기존 테스트의 스코프 동작이 종전과 같게 유지한다 — Task 4가 스코프 원천을 바꾸므로, 이 값이 비면 그 파일의 기존 좁히기 테스트가 전부 전체 폴백으로 떨어진다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test 2>&1 | tail -3`
Expected: 1,327건 통과 (1,325 + 2)

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/BatchStepPlan.cs tests/
git commit -m "$(cat <<'EOF'
feat: give the step plan a schema scope field of its own

TargetTables answers "did the body describe this table", and the bundle
writer was reusing it to answer "which schemas does this stage need".
The two questions want different sets: narrowing DDL to write targets
alone leaves the agent unable to write the SELECT side.

A missing field parses as empty, so catalogs written before this change
keep falling back to the full dependency list exactly as they did.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 보강기가 대상 테이블을 교체하고 스코프를 채운다

**Files:**
- Modify: `src/ReSet.Core/Services/PlanStructureEnricher.cs`
- Test: `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`

**Interfaces:**
- Consumes: `SpecTargetTableExtractor.StepTableSets`, `SpecTargetTableExtractor.BareTableName`, `SpecReturnCodeExtractor.BareName`
- Produces:
  - `public sealed record PlanStructureEnrichment(string Markdown, IReadOnlyList<string> DroppedTableDeclarations)`
  - `public static PlanStructureEnrichment Enrich(string? planStructureMarkdown, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure, IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure)`

> **반환형이 바뀐다.** 기존 호출부 2곳(`VerificationPipelineOrchestrator`)이 Task 5에서 고쳐진다. 이 태스크가 끝난 시점에는 빌드가 깨져 있을 수 있으므로, **Step 4에서 호출부를 `.Markdown`으로 임시 조정해 빌드를 통과시킨다.** 경고 표시 배선은 Task 5가 맡는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`에 추가한다.

```csharp
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
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests"`
Expected: 컴파일 실패 — `Enrich`의 인자가 둘뿐이고 반환형이 `string`이다.

- [ ] **Step 3: 보강기를 고친다**

`PlanStructureEnricher.cs`에 결과 레코드를 더한다.

```csharp
    /// <summary>
    /// 보강 결과. 마크다운과, 검사에서 제외된 목차 선언의 보고를 함께 낸다.
    ///
    /// 버린 선언을 반환값에 싣는 이유: 그것을 계산하는 곳은 여기 하나여야 한다.
    /// 오케스트레이터가 따로 비교하면 두 권위가 생기고, 이 저장소는 그 어긋남을
    /// 이미 여러 번 겪었다.
    /// </summary>
    public sealed record PlanStructureEnrichment(
        string Markdown,
        IReadOnlyList<string> DroppedTableDeclarations);
```

`Enrich`의 시그니처와 본문을 바꾼다.

```csharp
        public static PlanStructureEnrichment Enrich(
            string? planStructureMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure)
        {
            var empty = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return new PlanStructureEnrichment(planStructureMarkdown ?? string.Empty, empty);
            }

            var hasCodes = codesByProcedure != null && codesByProcedure.Count > 0;
            var hasTables = tablesByProcedure != null && tablesByProcedure.Count > 0;
            if (!hasCodes && !hasTables)
            {
                Log.Warning("명세서와 정적 분석에서 추출한 보강 재료가 없어 목차 보강을 건너뜁니다.");
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            var located = BatchStepPlanParser.TryLocateStepsBlock(planStructureMarkdown);
            if (located == null)
            {
                Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            var dropped = new List<string>();
            var rewritten = TryRewriteBlock(
                located.Value.Body,
                codesByProcedure ?? new Dictionary<string, IReadOnlyList<string>>(),
                tablesByProcedure ?? new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(),
                dropped);

            if (rewritten == null)
            {
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            var markdown = planStructureMarkdown[..located.Value.BodyIndex]
                + rewritten
                + planStructureMarkdown[(located.Value.BodyIndex + located.Value.BodyLength)..];

            return new PlanStructureEnrichment(markdown, dropped);
        }
```

`TryRewriteBlock`의 시그니처와 루프 본문을 바꾼다. **`dropped`에 담는 코드는 반드시 이 `try` 안에 있어야 한다.**

```csharp
        private static string? TryRewriteBlock(
            string json,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            List<string> dropped)
        {
            try
            {
                var root = JsonNode.Parse(json);

                if (root is not JsonObject obj ||
                    !obj.TryGetPropertyValue("Steps", out var stepsNode) ||
                    stepsNode is not JsonArray steps)
                {
                    return null;
                }

                var enrichedCodeCount = 0;
                var enrichedTableCount = 0;
                foreach (var stepNode in steps)
                {
                    if (stepNode is not JsonObject step)
                    {
                        continue;
                    }

                    var merged = MergeCodes(step, codesByProcedure);
                    if (merged != null)
                    {
                        step["ErrorCodes"] = new JsonArray(
                            Array.ConvertAll(merged, c => (JsonNode?)JsonValue.Create(c)));
                        enrichedCodeCount++;
                    }

                    if (RewriteTables(step, tablesByProcedure, dropped))
                    {
                        enrichedTableCount++;
                    }
                }

                if (enrichedCodeCount > 0)
                {
                    Log.Information("목차의 오류코드를 명세서에서 보강했습니다 - 단계 수: {Count}개", enrichedCodeCount);
                }

                if (enrichedTableCount > 0)
                {
                    Log.Information("목차의 대상 테이블을 정적 분석에서 보강했습니다 - 단계 수: {Count}개", enrichedTableCount);
                }

                return root.ToJsonString(WriteOptions) + "\n";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "목차 단계 목록 JSON 블록 보강 중 오류가 발생했습니다. 이 블록은 원본을 유지합니다.");
                return null;
            }
        }
```

`RewriteTables`를 새로 더한다.

```csharp
        /// <summary>
        /// 이 단계의 TargetTables를 정적 분석의 쓰기 대상으로 교체하고 SchemaTables를 채운다.
        /// 바뀐 것이 있으면 true.
        ///
        /// 오류코드와 달리 합집합하지 않는 이유: 두 재료의 신뢰도가 대칭이 아니다.
        /// 오류코드는 명세서 산문에서 뽑고 모델도 같은 산문을 보지만, 테이블은 파서가
        /// AST에서 확정하고 모델은 추측한다. 실측에서 한 단계가 선언한 네 테이블 중
        /// 셋이 원본 DDL에 0회 등장했다 - 합집합했다면 그 허위가 검증 요건이 되고,
        /// 재생성이 그것을 고착시켰을 것이다.
        /// </summary>
        private static bool RewriteTables(
            JsonObject step,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            List<string> dropped)
        {
            var procedures = ReadStringArray(step, "LegacyProcedures");
            if (procedures.Count == 0)
            {
                // 레거시 출신이 없는 단계는 계획이 새로 설계한 것이다. 대조할 원본이 없다.
                return false;
            }

            var write = new List<string>();
            var writeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var schema = new List<string>();
            var schemaSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var procedure in procedures)
            {
                if (!tablesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var sets))
                {
                    continue;
                }

                foreach (var table in sets.WriteTables)
                {
                    if (writeSeen.Add(table)) write.Add(table);
                    if (schemaSeen.Add(table)) schema.Add(table);
                }

                foreach (var table in sets.ReadTables)
                {
                    if (schemaSeen.Add(table)) schema.Add(table);
                }
            }

            var changed = false;

            // 쓰기 대상을 하나도 못 뽑았으면 기존 선언을 유지한다. 지우면 멀쩡한
            // 단계가 "검증 불가"로 떨어져 지금보다 나빠진다.
            if (write.Count > 0)
            {
                var declared = ReadStringArray(step, "TargetTables");
                var extractedBareNames = new HashSet<string>(
                    write.ConvertAll(SpecTargetTableExtractor.BareTableName), StringComparer.Ordinal);

                var lost = declared.FindAll(
                    d => !extractedBareNames.Contains(SpecTargetTableExtractor.BareTableName(d)));

                if (lost.Count > 0)
                {
                    var code = ReadScalarString(step, "Code");
                    dropped.Add(
                        $"{code}: 목차가 선언한 대상 테이블 {string.Join(", ", lost)}이(가) " +
                        "정적 분석에 없어 검사에서 제외했습니다. 계획서 본문도 함께 확인하십시오.");
                }

                step["TargetTables"] = new JsonArray(
                    write.ConvertAll(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                changed = true;
            }

            if (schema.Count > 0)
            {
                step["SchemaTables"] = new JsonArray(
                    schema.ConvertAll(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                changed = true;
            }

            return changed;
        }

        private static string ReadScalarString(JsonObject step, string name) =>
            step.TryGetPropertyValue(name, out var node) &&
            node is JsonValue value &&
            value.TryGetValue(out string? text)
                ? text ?? string.Empty
                : string.Empty;
```

- [ ] **Step 4: 기존 호출부를 임시로 조정해 빌드를 통과시킨다**

`VerificationPipelineOrchestrator`의 `Enrich` 호출 2곳에 빈 사전을 넘기고 `.Markdown`을 취한다. Task 5가 제대로 배선한다.

```csharp
// 예: 목차 최초 수립 지점
currentPlanStructure = PlanStructureEnricher.Enrich(
    planResult.Content,
    specReturnCodes,
    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown;
```

- [ ] **Step 5: `ReadStringArray`의 뮤테이션 하중을 세운다**

기존 후속 작업(오류코드 검증 §후속 2)을 여기서 닫는다. 비문자열 항목을 거르는 가드를 지워도 통과하던 테스트를, 걸러지지 않으면 결과가 달라지도록 고친다.

```csharp
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
```

- [ ] **Step 6: 테스트와 빌드를 확인한다**

Run: `dotnet build 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: 경고 8개 / 오류 0개, 1,335건 통과 (1,327 + 8)

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/PlanStructureEnricher.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs
git commit -m "$(cat <<'EOF'
feat: replace the catalog's target tables with what the parser found

Error codes merge because the model and the extractor read the same
prose. Tables do not: the parser settles them from the AST while the
model guesses, and one measured step declared four tables of which three
appear zero times in the source DDL. A union would have promoted those
three into validation requirements, and regeneration would then freeze
them in place.

Extraction replaces instead, and falls back to the declared list when it
finds no write target at all so a parse failure cannot empty a step that
was fine. What gets dropped is reported rather than silently discarded —
those names are in the plan body too.

Also closes the ReadStringArray mutation gap the error-code branch left
behind: the guard now has a numeric key it actually has to filter.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 회차 지시서의 DDL 스코프를 `SchemaTables`로 바꾼다

**Files:**
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs` (`DependenciesForStep`)
- Test: `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan.SchemaTables` (Task 2)
- Produces: 없음 (내부 동작 변경)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`DependenciesForStep`은 `private`이다. 이 파일의 기존 테스트처럼 `WriteAsync`를 돌리고 산출된 `task-NN-*.md`를 읽어 확인한다(`WriteAsync_ShouldScopeStepSchemasToTheStep` 등이 쓰는 방식).

```csharp
        [Fact]
        public async Task WriteAsync_ShouldScopeStepSchemasBySchemaTablesNotTargetTables()
        {
            // 쓰기 대상만으로 좁히면 에이전트가 SELECT를 쓸 원본 스키마를 못 받는다.
            // 실측 S01은 쓰기 5개·읽기 7개였다 - 쓰기만 주면 그 회차는 조회 코드를
            // 쓸 수 없다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 스냅샷 생성\n조각 본문" },
                new[]
                {
                    new BatchStepPlan(
                        "S01", "스냅샷 생성",
                        new[] { "UP_S01" },
                        new[] { "dbo.TWrite" },
                        new[] { "-1" },
                        false,
                        new[] { "dbo.TWrite", "dbo.TRead" }),
                },
                null);

            var inputs = Inputs(layout) with
            {
                SpDefs = new List<SpDefinition>
                {
                    SpDefWithDependency("UP_S01", "TWrite"),
                    SpDefWithDependency("UP_S01_Read", "TRead"),
                    SpDefWithDependency("UP_Other", "TOther"),
                },
            };

            await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

            var s01Task = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-01-S01.md"));

            Assert.Contains("dbo.TWrite", s01Task);
            Assert.Contains("dbo.TRead", s01Task);
            Assert.DoesNotContain("dbo.TOther", s01Task);
        }

        [Fact]
        public async Task WriteAsync_ShouldFallBackToEveryDependency_WhenSchemaTablesAreEmpty()
        {
            // 이 브랜치 이전에 만들어진 목차에는 SchemaTables가 없다. 좁힐 근거가
            // 없으면 전체를 준다 - 종전과 같은 동작이다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 스냅샷 생성\n조각 본문" },
                new[]
                {
                    new BatchStepPlan(
                        "S01", "스냅샷 생성",
                        new[] { "UP_S01" },
                        new[] { "dbo.TWrite" },
                        new[] { "-1" },
                        false,
                        Array.Empty<string>()),
                },
                null);

            var inputs = Inputs(layout) with
            {
                SpDefs = new List<SpDefinition>
                {
                    SpDefWithDependency("UP_S01", "TWrite"),
                    SpDefWithDependency("UP_Other", "TOther"),
                },
            };

            await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

            var s01Task = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-01-S01.md"));

            Assert.Contains("dbo.TWrite", s01Task);
            Assert.Contains("dbo.TOther", s01Task);
        }
```

> `Inputs(...) with { SpDefs = ... }`는 `BundleInputs`가 레코드일 때만 쓸 수 있다. 레코드가 아니면 이 파일의 기존 테스트(`WriteAsync_ShouldWarnWhenStepDeclaresNoTargetTables`)가 `SpDefs`를 바꿀 때 쓰는 방식을 그대로 따른다.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~InstructionBundleWriterTests"`
Expected: 첫 테스트가 FAIL — 아직 `TargetTables`로 좁히므로 `TRead`가 빠진다.

- [ ] **Step 3: `DependenciesForStep`을 고친다**

세 자리를 바꾼다.

```csharp
            if (step.SchemaTables.Count == 0)
            {
                Log.Warning(
                    "단계의 목차 SchemaTables가 비어 있어 의존성 스키마를 좁히지 못하고 전체 목록으로 대체합니다 - " +
                    "Step: {StepCode}, 스키마 수: {Count}개",
                    stepCode, dependencies.Count);
                return dependencies;
            }

            var matched = dependencies
                .Where(dep => step.SchemaTables.Any(target => TableTokensMatch(dep.Label, target)))
                .ToList();

            if (matched.Count == 0)
            {
                Log.Warning(
                    "단계의 SchemaTables와 일치하는 의존성 스키마를 찾지 못해 전체 목록으로 대체합니다 - " +
                    "Step: {StepCode}, SchemaTables: {SchemaTables}",
                    stepCode, string.Join(", ", step.SchemaTables));
                return dependencies;
            }
```

XML 주석의 `TargetTables` 언급을 `SchemaTables`로 바꾸고, 왜 쓰기 대상만으로는 부족한지 한 문장을 더한다.

```csharp
        /// 스코프의 원천이 TargetTables가 아니라 SchemaTables인 이유: 앞은 쓰기 대상만
        /// 담는 검증 재료라, 그것으로 좁히면 에이전트가 SELECT를 쓸 원본 테이블의 컬럼
        /// 정의를 받지 못한다.
```

- [ ] **Step 4: 로그 문구를 단언하는 기존 테스트를 고친다**

`WriteAsync_ShouldWarnWhenStepDeclaresNoTargetTables`가 경고 메시지에 `"TargetTables"`가 들어 있는지 단언한다. 문구를 바꿨으므로 **이 테스트는 깨진다.**

**삭제하지 말고 의도를 유지한 채 고친다.** 그 테스트가 지키는 것은 "두 폴백의 관측성이 같아야 한다"이고, 그 의도는 이 변경 뒤에도 유효하다. 이름과 단언을 새 필드에 맞춘다.

```csharp
        public async Task WriteAsync_ShouldWarnWhenStepDeclaresNoSchemaTables()
        ...
            Assert.Contains(sink.Messages, m => m.Contains("S02") && m.Contains("SchemaTables"));
```

같은 테스트의 픽스처에서 S02의 `SchemaTables`를 비우고 S01은 채워, 한쪽만 폴백에 걸리는 구도를 유지한다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test 2>&1 | tail -3`
Expected: 1,337건 통과 (1,335 + 신규 2건). 이름을 바꾼 경고 테스트는 증감이 아니다 — 총수가 1,338이면 그 테스트를 지우지 않고 새로 하나 더 만든 것이다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/InstructionBundleWriter.cs tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs
git commit -m "$(cat <<'EOF'
fix: scope a stage's DDL by what it touches, not what it writes

Narrowing by TargetTables alone would hand the agent the five tables a
stage inserts into and none of the seven it reads from, which is worse
than the full-list fallback it gets today. SchemaTables carries both.

The two fallbacks stay: an empty field or zero matches still returns
every dependency and says so, because a silently empty schema list is
harder to diagnose than a few extra entries.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 오케스트레이터 배선

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `SpecTargetTableExtractor.Extract`, `PlanStructureEnricher.Enrich`(3인자), `IVerificationUserInteraction.NotifyWarnings(string, List<string>)`
- Produces: `RunConsolidatedPipelineAsync`의 새 선택적 매개변수 `IReadOnlyList<SpDefinition>? definitions = null`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

기존 `Pipeline_ShouldWriteEnrichedErrorCodesToPlanStructureFile`이 그대로 본이 된다. 그 테스트의 fake 배선(`aiService` 6개 스텁 + `SkeletonMarkdownFor` + `FixedErrorCodeSection`)을 복사하고 목차 상수와 단언만 바꾼다.

```csharp
        // 목차는 TargetTables를 비운 채 낸다 - 실측 CLI 회차에서 12단계 중 5개가
        // 이렇게 비어 있었고, 그 5개가 DDL 55개를 통째로 받았다.
        private const string StepsJsonNoTargetTables = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""첫 단계"", ""LegacyProcedures"": [""dbo.UP_X""], ""TargetTables"": [], ""ErrorCodes"": [""-7""] }
  ]
}
```";

        private static SpDefinition DefinitionWithTables() => new()
        {
            Schema = "dbo",
            Name = "UP_X",
            StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                InsertTables = { "SETTLE_POQ_DB.dbo.T1" },
                SelectTables = { "SETTLE_POQ_DB.dbo.TSource" },
            },
        };

        [Fact]
        public async Task Pipeline_ShouldWriteEnrichedTargetTablesToPlanStructureFile()
        {
            var (orchestrator, jobName, outputRoot) = ConsolidatedPipelineFor(StepsJsonNoTargetTables);
            var specs = new List<(string, string)> { ("dbo.UP_X", "`@po_intRetVal = -7`") };

            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", jobName, "OpenAI", outputRoot,
                isBatchMode: true,
                definitions: new[] { DefinitionWithTables() });

            var written = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Jobs", jobName, "raw", "PlanStructure.md"));
            var step = BatchStepPlanParser.TryParse(written)!.Single(s => s.Code == "S01");

            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.T1" }, step.TargetTables);
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.T1", "SETTLE_POQ_DB.dbo.TSource" },
                step.SchemaTables);
        }

        [Fact]
        public async Task Pipeline_ShouldNotEnrichTablesWhenDefinitionsAreOmitted()
        {
            // 기본값 null이 회귀 방어의 본체다. 넘기지 않으면 종전 동작 그대로이고,
            // 이 파일에서 오케스트레이터를 만드는 94곳이 한 줄도 바뀌지 않는다.
            var (orchestrator, jobName, outputRoot) = ConsolidatedPipelineFor(StepsJsonNoTargetTables);
            var specs = new List<(string, string)> { ("dbo.UP_X", "`@po_intRetVal = -7`") };

            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

            var written = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Jobs", jobName, "raw", "PlanStructure.md"));
            var step = BatchStepPlanParser.TryParse(written)!.Single(s => s.Code == "S01");

            Assert.Empty(step.TargetTables);
            Assert.Empty(step.SchemaTables);
        }
```

`ConsolidatedPipelineFor(string catalogMarkdown)`는 `Pipeline_ShouldWriteEnrichedErrorCodesToPlanStructureFile`의 fake 배선을 그대로 옮긴 헬퍼다. 그 테스트가 인라인으로 하는 일을 헬퍼로 빼되, **기존 테스트는 건드리지 않는다** — 두 테스트가 같은 배선을 쓰지만 한쪽을 리팩터링하면 그 테스트가 지키는 것이 흐려진다.

단계 본문 스텁(`FixedErrorCodeSection`)이 `대상은 dbo.T1이고`라는 문장을 담고 있어, 보강된 `SETTLE_POQ_DB.dbo.T1`이 맨이름 토큰 매칭으로 하한 검사를 통과한다. 본문 문구를 바꾸면 이 테스트가 하한 미달로 떨어진다.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: 컴파일 실패 — `definitions` 매개변수가 없다.

- [ ] **Step 3: 시그니처와 추출을 더한다**

```csharp
        public async Task<ConsolidatedPipelineResult> RunConsolidatedPipelineAsync(
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            string provider,
            string outputRoot,
            bool isBatchMode = false,
            IReadOnlyList<SpDefinition>? definitions = null,
            CancellationToken cancellationToken = default)
```

`specReturnCodes` 바로 아래에 추출을 더한다.

```csharp
            // 목차의 TargetTables도 같은 문제를 갖는다 - 같은 12개 SP를 두 제공자로
            // 돌린 실측에서 7개와 17개가 나왔고, 두 회차 모두 같은 단계를 빈 배열로
            // 냈다. 오류코드와 달리 명세서 산문이 아니라 정적 분석에서 뽑는다.
            // definitions가 null이면 빈 사전이라 보강이 일어나지 않는다.
            var specTargetTables = SpecTargetTableExtractor.Extract(definitions);
```

- [ ] **Step 4: 보강 호출 두 곳을 고친다**

목차 최초 수립 지점:

```csharp
                            var planEnrichment = PlanStructureEnricher.Enrich(
                                planResult.Content, specReturnCodes, specTargetTables);
                            currentPlanStructure = planEnrichment.Markdown;
                            NotifyDroppedTableDeclarations(jobName, planEnrichment);
```

`DraftReplacementPlanStructureAsync`는 매개변수를 하나 더 받는다.

```csharp
        private async Task<string?> DraftReplacementPlanStructureAsync(
            string reason,
            IReadOnlyDictionary<string, IReadOnlyList<string>> returnCodes,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> targetTables,
            string currentStructure,
            ...
```

반환 직전을 바꾼다.

```csharp
            var enrichment = PlanStructureEnricher.Enrich(redrafted, returnCodes, targetTables);
            NotifyDroppedTableDeclarations(jobName, enrichment);
            return enrichment.Markdown;
```

호출부 2곳(`:2011`, `:2188` 부근)에 `specTargetTables`를 더한다.

- [ ] **Step 5: 경고 표시 헬퍼를 더한다**

```csharp
        /// <summary>
        /// 목차가 선언했으나 정적 분석에 없는 대상 테이블을 사용자에게 알린다.
        ///
        /// 배너나 StepDefect로 올리지 않는 이유: 이 사실은 목차가 확정된 뒤에
        /// 관측되므로 재생성으로 고칠 수 없다. 고칠 수 없는 것을 재시도 루프에
        /// 넣는 것이 이 저장소가 이미 두 번 물린 실패 모드다. 그렇다고 침묵하지도
        /// 않는다 - 그 이름들은 계획서 본문에도 들어가 있다.
        /// </summary>
        private void NotifyDroppedTableDeclarations(string jobName, PlanStructureEnrichment enrichment)
        {
            if (enrichment.DroppedTableDeclarations.Count == 0)
            {
                return;
            }

            foreach (var message in enrichment.DroppedTableDeclarations)
            {
                Log.Warning("목차 선언과 정적 분석이 어긋납니다 - {Message}", message);
            }

            _userInteraction.NotifyWarnings(jobName, new List<string>(enrichment.DroppedTableDeclarations));
        }
```

- [ ] **Step 6: 테스트와 빌드를 확인한다**

Run: `dotnet build 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: 경고 8개 / 오류 0개, 1,339건 통과 (1,337 + 2)

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "$(cat <<'EOF'
feat: let the consolidated pipeline read the static analysis

The extractor needs SpDefinitions and the orchestrator was only given
spec text. It already knows the type — SpecExpectations.From(spDef) runs
in the same class — so the parameter is new, the dependency is not.

Defaulting it to null is the regression guard: the 94 places that build
this orchestrator in tests keep compiling and keep the old behaviour,
and enrichment only happens where a caller opts in.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `Program.cs`의 두 진입 경로

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` (무인 배치 호출부, 메뉴 3 호출부)

**Interfaces:**
- Consumes: `RunConsolidatedPipelineAsync(..., definitions: ...)` (Task 5), `BatchStepCatalog.LoadDefinitionsAsync`
- Produces: 없음

> `src/ReSet.Cli`에는 테스트 프로젝트가 없다. 이 태스크의 검증은 빌드와 §사람이 직접 확인해야 하는 것이다.

- [ ] **Step 1: 무인 배치 경로를 명명 인자로 바꾸고 정의를 넘긴다**

현재 호출은 `CancellationToken`을 위치 인자로 넘긴다. 새 매개변수를 그 앞에 끼웠으므로 **그대로 두면 조용히 잘못된 자리에 바인딩된다.**

```csharp
                        var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(
                            specsData,
                            targetLanguage,
                            cliArgs.JobName,
                            provider,
                            outputDir,
                            isBatchMode: true,
                            definitions: spDefs,
                            cancellationToken: activeCts.Token);
```

- [ ] **Step 2: 메뉴 3 경로에서 정의 로드를 파이프라인 앞으로 옮긴다**

`LoadDefinitionsAsync` 호출 블록(현재 계획 수립 뒤에 있다)을 `RunConsolidatedPipelineAsync` 호출 **앞**으로 옮기고, `spDefs`를 아래 지시서 생성에서 재사용한다.

```csharp
                        // 정의를 계획 수립 앞에서 읽는다. 목차 보강이 이 정적 분석을
                        // 쓰기 때문이고, 부수 효과로 메타데이터 누락 경고가 수십 분짜리
                        // 계획 수립 전에 뜬다 - 종전에는 계획이 다 끝난 뒤에야 그 SP가
                        // 지시서에서 빠진다는 사실을 알렸다.
                        var loadResult = await BatchStepCatalog.LoadDefinitionsAsync(
                            outputDir, selectedFiles, activeCts.Token);
                        var spDefs = loadResult.Definitions.ToList();

                        foreach (var missing in loadResult.MissingMetadata) { /* 기존 경고 그대로 */ }
                        foreach (var failed in loadResult.FailedToParse) { /* 기존 경고 그대로 */ }

                        var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(
                            specsData,
                            targetLanguage,
                            jobName,
                            provider,
                            outputDir,
                            definitions: spDefs,
                            cancellationToken: activeCts.Token);
```

아래쪽의 중복된 로드 블록과 경고 출력은 제거하고, `ExportConsolidatedMigrationInstructionsAsync`가 위에서 만든 `spDefs`를 그대로 쓰게 한다.

- [ ] **Step 3: 위치 인자 회귀가 없는지 확인한다**

Run: `grep -n "RunConsolidatedPipelineAsync" src/ReSet.Cli/Program.cs`
Expected: 두 호출 모두 `definitions:`와 `cancellationToken:`을 명명 인자로 쓴다.

- [ ] **Step 4: 빌드와 전체 스위트를 확인한다**

Run: `dotnet build 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: 경고 8개 / 오류 0개, 1,339건 통과 (변동 없음 — 이 태스크는 테스트를 더하지 않는다)

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Cli/Program.cs
git commit -m "$(cat <<'EOF'
feat: hand both catalog entry points the definitions they already had

The unattended batch already held spDefs in scope. The menu path loaded
them too, just after the pipeline finished, to write the instructions —
so enrichment moves that load earlier and reuses the result.

The reorder has a second payoff: a missing raw/metadata.json is now
reported before the plan is drafted rather than after, instead of
telling the operator an SP was excluded once the run is already spent.

Both calls switch to named arguments. The new parameter sits before the
cancellation token, and the batch call was passing that positionally.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: S11 회귀 픽스처와 뮤테이션 저항 확인

**Files:**
- Create: `tests/ReSet.Core.Tests/Fixtures/S11PlanStructureExcerpt.md`
- Test: `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`

**Interfaces:**
- Consumes: `RepoPaths.FindRepoRoot()` (기존 관례 — `StepErrorCodeRegressionTests`가 쓰는 방식)
- Produces: 없음

- [ ] **Step 1: 실측 발췌를 픽스처로 체크인한다**

`tests/ReSet.Core.Tests/Fixtures/S11PlanStructureExcerpt.md`. `output/Jobs/POQSettleProc3/raw/PlanStructure.md`의 S11 항목을 **한 글자도 고치지 않고** 옮긴다.

```markdown
```json
{
  "Steps": [
    {
      "Code": "S11",
      "Name": "취소영향 요약 보정",
      "LegacyProcedures": ["UP_UTIL_SETTLE_SUMMARY_ETC"],
      "TargetTables": ["TSettleByTX", "TPartialCancelByTX", "TSettleByIN", "TSettleByOUT"],
      "ErrorCodes": ["-1", "-2", "-3"],
      "Chunkable": false
    }
  ]
}
```
```

- [ ] **Step 2: 회귀 테스트를 쓴다**

```csharp
        [Fact]
        public void Enrich_ShouldDropTheThreeTablesTheSourceDdlNeverMentions()
        {
            // 실측 회귀. output/은 추적되지 않으므로 발췌를 픽스처로 체크인했다.
            // 이 세 이름은 UP_UTIL_SETTLE_SUMMARY_ETC의 DDL 원문에 0회 등장한다
            // (IsParsedSuccessfully = True, 동적 SQL 없음 - 파서가 놓친 것이 아니다).
            var markdown = File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures",
                "S11PlanStructureExcerpt.md"));

            var result = PlanStructureEnricher.Enrich(
                markdown,
                new Dictionary<string, IReadOnlyList<string>>(),
                Tables(("up_util_settle_summary_etc",
                    new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
                    new[] { "SETTLE_POQ_DB.dbo.TSettleMst" })));

            var step = BatchStepPlanParser.TryParse(result.Markdown)![0];
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" }, step.TargetTables);

            var reported = Assert.Single(result.DroppedTableDeclarations);
            Assert.Contains("TSettleByTX", reported);
            Assert.Contains("TPartialCancelByTX", reported);
            Assert.Contains("TSettleByIN", reported);
        }
```

- [ ] **Step 3: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests"`
Expected: PASS

- [ ] **Step 4: 예외 탈출 경로를 함수 이름으로 특정해 확인한다**

설계가 세 함수를 지목했다. 직전 두 브랜치가 연속으로 "예외를 새로 던지는 경로는 없다"고 적고 거짓이었고, 그 교훈이 "확인한 함수의 이름을 함께 적어야 한다"였다. 각 함수에 대해 아래를 **실제로 확인하고 결과를 실행 보고에 적는다.**

| 함수 | 확인할 것 |
|---|---|
| `SpecTargetTableExtractor.Extract` | `SpDefinition`이 null, `StaticAnalysis`가 null, 각 목록이 null, `Name`이 공백 — 어느 것도 던지지 않는가. 호출 지점(`RunConsolidatedPipelineAsync`의 `specTargetTables` 대입)에 봉투가 없다면 추출기가 스스로 방어해야 한다 |
| `PlanStructureEnricher.RewriteTables` | `TryRewriteBlock`의 `try` **안**에서만 호출되는가. `Enrich` 본문에서 `try` 밖으로 새어 나간 코드가 없는가 |
| `BatchStepPlanParser.TryParseBlock` | `SchemaTables`를 읽는 `ReadStringArray` 추가가 기존 `catch (Exception ex) when (ex is not OperationCanceledException)` 안에 있는가 |

확인 방법은 코드 읽기와, 각 null 조합을 넣는 테스트다. 아래를 추가한다.

```csharp
        [Fact]
        public void Extract_ShouldSurviveNullEntriesAndNullLists()
        {
            var definitions = new SpDefinition?[]
            {
                null,
                new SpDefinition { Name = "  ", StaticAnalysis = new SpStaticAnalysisResult() },
                new SpDefinition { Name = "UP_OK", StaticAnalysis = new SpStaticAnalysisResult { InsertTables = null! } },
            };

            var result = SpecTargetTableExtractor.Extract(definitions!);

            Assert.Empty(result);
        }
```

- [ ] **Step 5: 뮤테이션 저항을 확인한다**

가드를 하나씩 지우고 테스트가 **실제로 깨지는지** 확인한 뒤 복원한다. 지웠는데 초록이면 그 테스트는 하중을 지지 않는 것이므로 테스트를 고친다.

| # | 지울 가드 | 깨져야 하는 테스트 |
|---|---|---|
| 1 | `SpecTargetTableExtractor.IsPhysicalTable`의 `#`/`@` 검사 | `Extract_ShouldExcludeTempTablesAndTableVariables` |
| 2 | `RewriteTables`의 `if (write.Count > 0)` 조건 (무조건 교체로 변경) | `Enrich_ShouldKeepTheDeclaredTablesWhenNothingWasExtracted` |
| 3 | `RewriteTables`에서 `schema`에 쓰기 대상을 넣는 두 줄 | `Enrich_ShouldFillSchemaTablesWithWritesAndReads` |
| 4 | `lost.Count > 0` 블록 전체 | `Enrich_ShouldReportDeclarationsTheStaticAnalysisDoesNotHave`, S11 회귀 |

각 뮤테이션에 대해 실행한 명령과 결과를 실행 보고에 남긴다.

- [ ] **Step 6: 커밋**

```bash
git add tests/ReSet.Core.Tests/Fixtures/S11PlanStructureExcerpt.md tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs
git commit -m "$(cat <<'EOF'
test: pin the replacement against the catalog entry that motivated it

The excerpt is the S11 entry exactly as the model wrote it, not a
sentence we composed. What the gate has to handle is the shape that
actually occurred.

Four guards were mutated to confirm the tests carry load; the write-set
fallback and the dropped-declaration report both fail when removed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: 문서 동기화

**Files:**
- Modify: `docs/architecture.md`, `AGENTS.md`

**Interfaces:**
- Consumes: 앞의 모든 태스크
- Produces: 없음

- [ ] **Step 1: `reset-doc-sync` 스킬을 쓴다**

이 저장소에는 세 문서를 소스와 맞추는 전용 스킬이 있다. `Skill` 도구로 `reset-doc-sync`를 호출한다. 아래는 그 스킬이 반영해야 할 지점이다.

- `docs/architecture.md` 2.2 테이블에 `SpecTargetTableExtractor` 행 추가
- `docs/architecture.md` §4 메커니즘의 목차 보강 항목에 대상 테이블 축 추가 — 오류코드는 합집합, 대상 테이블은 교체이며 그 비대칭의 근거가 재료의 신뢰도 차이라는 점
- `AGENTS.md`에 규칙 추가: **목차의 대상 테이블은 정적 분석이 진실의 원천이고 명세서 산문에서 다시 뽑지 않는다.** `TargetTables`(검증)와 `SchemaTables`(스코프)를 한 필드로 합치지 않는다
- `README.md`는 외부 사용자에게 드러나는 변화가 없어 대상 아님

- [ ] **Step 2: 설계 문서의 후속 항목을 닫는다**

`docs/superpowers/specs/2026-08-08-step-error-code-verification-design.md`의 「후속 작업」에서 두 항목을 이 저장소 관례(취소선 + `**해소됨(2026-08-12).**`)로 표시한다.

- `ReadStringArray`의 뮤테이션 저항 항목 → Task 3에서 닫힘
- `TargetTables`도 같은 방식으로 뽑을 수 있다는 항목 → 이 브랜치가 닫음. **다만 「갱신 대상 테이블」 절에서 뽑는다는 서술은 틀렸다는 정정을 함께 남긴다** — 그 문구는 코드에 존재하지 않고, 실제 원천은 정적 분석이다

- [ ] **Step 3: 최종 확인**

Run: `dotnet clean && dotnet build 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: 경고 정확히 8개 / 오류 0개, **1,341건** 통과 (1,318 + 23)

- [ ] **Step 4: 커밋**

```bash
git add docs/ AGENTS.md
git commit -m "$(cat <<'EOF'
docs: sync the reference docs through the target tables enrichment

Records why the two enrichment axes have opposite merge rules, and
corrects the follow-up that said the tables could be pulled from a
"갱신 대상 테이블" section — no such heading exists in the prompts, and
the parser already holds the answer.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## 사람이 직접 확인해야 하는 것

자동 테스트가 덮을 수 없다. `src/ReSet.Cli`에는 테스트 프로젝트가 없다.

1. **실제 Job 1회** — 보강된 `TargetTables`로 하한 검사가 실제로 돌아 통과하는지, 진입점 `MigrationInstructions.md` §0에서 "검증 불가" 목록이 사라지는지
2. **회차 지시서의 DDL** — `task-NN-*.md`에 실리는 스키마가 좁혀졌는지, 그리고 **에이전트가 그것만으로 데이터 액세스 코드를 쓸 수 있는지.** 좁히기가 과하면 이 작업이 결함을 고치면서 새 결함을 만든 것이다
3. **버려진 선언 경고** — 실제 실행에서 뜨는지, 뜬다면 그것이 진짜 허위인지
4. **메뉴 3의 순서 변화** — 메타데이터 누락 경고가 계획 수립 전에 뜨고, 지시서 생성이 종전과 같이 동작하는지
