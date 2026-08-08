# 단계 오류코드 검증 신뢰성 회복 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 목차의 `ErrorCodes`를 명세서에서 결정론적으로 추출한 값으로 채워 하한 검사가 실제로 대조하게 만들고, 그래도 대조할 수 없는 경우를 "품질 미달"과 구별해 보고한다.

**Architecture:** 순수 함수 두 개(`SpecReturnCodeExtractor`, `PlanStructureEnricher`)를 새로 만들고, 오케스트레이터가 목차 마크다운이 만들어지는 두 지점에서만 보강을 태운다. 그 아래로는 파일 기록·파싱·생성·검사·번들이 전부 보강된 같은 문자열을 본다. 보고 쪽은 위반 사전의 값 타입을 `string`에서 `StepDefect(Kind, Reason)`로 올려 배너를 종류별로 가른다.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, NSubstitute 5.3.0, Serilog 정적 `Log`, `System.Text.Json` / `System.Text.Json.Nodes`

## Global Constraints

- 작업 브랜치는 `fix/step-error-code-verification`, 워크트리는 `.worktrees/step-error-code`. **공유 워킹트리 `/Users/payletter/git-root/ReSet`에서 작업하지 말 것** — 다른 세션이 `fix/static-analysis-identity`에서 병렬로 실행 중이다.
- 다른 세션이 건드리는 파일: `DbMetadataService.cs`, `SqlStaticParser.cs`, `CacheManager.cs`, `AiService.cs`, `StaticAnalysisNormalizer.cs`, `SqlObjectTypeClassifier.cs`, `OfflineDbMetadataService.cs`와 그 테스트. **이 계획은 그 파일들을 하나도 수정하지 않는다.** 수정이 필요해 보이면 멈추고 보고할 것.
- 빌드 경고 기준선은 **8개**다(전부 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602). 늘리지 말 것.
- 전체 테스트는 시작 시점에 **1040개 통과**다. 줄어들면 회귀다.
- 로그 메시지·주석·배너 문구는 한국어로 쓴다. 기존 파일의 어조와 밀도를 따른다.
- 커밋 메시지 본문은 영어, 마지막 줄은 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- **계획서의 코드를 옳다고 전제하지 말 것.** 붙여넣지 말고 실제로 돌려보고, 어긋나면 보고할 것.
- 검증 명령은 워크트리 루트에서:
  - 빌드 `dotnet build ReSet.slnx -v q --nologo`
  - 전체 `dotnet test ReSet.slnx -v q --nologo`
  - 단건 `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~<이름>" -v q --nologo`

---

## 파일 구조

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/SpecReturnCodeExtractor.cs` | **신규.** 명세서 본문 → `프로시저명 → 오류코드 목록`. 순수 함수 |
| `src/ReSet.Core/Services/PlanStructureEnricher.cs` | **신규.** 목차 마크다운 + 코드 사전 → 보강된 목차 마크다운. 순수 함수 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 하한 검사 3분류. `StepValidationResult`에 `NotApplicable` 개념 추가 |
| `src/ReSet.Core/Services/StepDefect.cs` | **신규.** `StepDefect` 레코드와 `StepDefectKind` 열거형 |
| `src/ReSet.Core/Models/PlanLayout.cs` | `FloorViolations`의 값 타입 승격 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 추출·보강 배선, 위반 사전 타입 승격, 회차 결과에 종류 전달 |
| `src/ReSet.Core/Services/VerificationBanner.cs` | 문서 배너를 종류별 두 메서드로 분리 |
| `src/ReSet.Core/Services/InstructionBundleWriter.cs` | `BuildFloorBanner`가 종류에 따라 문구를 가름 |
| `tests/ReSet.Core.Tests/SpecReturnCodeExtractorTests.cs` | **신규** |
| `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs` | **신규.** 왕복 테스트 포함 |
| `tests/ReSet.Core.Tests/Fixtures/` | **신규.** 실측 회차를 축약한 회귀 픽스처 |

---

## Task 1: 명세서 오류코드 추출기

**Files:**
- Create: `src/ReSet.Core/Services/SpecReturnCodeExtractor.cs`
- Test: `tests/ReSet.Core.Tests/SpecReturnCodeExtractorTests.cs`

**Interfaces:**
- Consumes: 없음 (최초 태스크)
- Produces: `public static class SpecReturnCodeExtractor`
  - `public static IReadOnlyDictionary<string, IReadOnlyList<string>> Extract(IEnumerable<(string FileName, string Content)> specs)`
  - 키는 파일명의 마지막 점 뒤를 소문자화한 것. 값은 등장 순서를 유지한 중복 없는 코드 문자열 목록.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SpecReturnCodeExtractorTests.cs` 신규:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecReturnCodeExtractorTests
    {
        // 픽스처는 실측 명세서(output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md)의
        // 「로직 흐름 요약」 형태를 그대로 축약한 것이다. 이 형태에서 뽑히지 않으면
        // 실제 산출물에서도 뽑히지 않는다.
        private const string CommUpdSpec = @"## 로직 흐름 요약

1. `BEGIN TRAN`으로 트랜잭션을 시작합니다.
   - 오류 시 `@po_intRetVal = -1`을 설정하고 롤백합니다.

2. 해외카드 정상거래의 수수료를 계산합니다.
   - 오류 시 `@po_intRetVal = -2`를 설정하고 롤백합니다.

3. 취소거래의 금액 관련 컬럼을 `-1`배 처리합니다.
   - 대상은 `UseState IN (1,2,3)`인 행입니다.
   - 오류 시 `@po_intRetVal = -4`를 설정하고 롤백합니다.

> **문서 작성일시**: 2026-08-05 12:52:30
";

        private static (string, string)[] Specs(params (string, string)[] items) => items;

        [Fact]
        public void Extract_ShouldPullCodesFromReturnVariableAssignments()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_UTIL_SETTLE_COMM_UPD", CommUpdSpec)));

            Assert.Equal(new[] { "-1", "-2", "-4" }, result["up_util_settle_comm_upd"]);
        }

        [Fact]
        public void Extract_ShouldNotMistakeNarrativeNegativesForCodes()
        {
            // "`-1`배 처리합니다"의 -1과 날짜의 -05는 오류코드가 아니다.
            // 일반 음수 패턴으로 훑으면 이 둘을 코드로 오인한다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_UTIL_SETTLE_COMM_UPD", CommUpdSpec)));

            Assert.DoesNotContain("-05", result["up_util_settle_comm_upd"]);
            Assert.DoesNotContain("-08", result["up_util_settle_comm_upd"]);
        }

        [Fact]
        public void Extract_ShouldIgnoreOtherVariables()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "오류 시 `@v_currentStepId = -7`을 설정합니다.")));

            Assert.False(result.ContainsKey("up_x"));
        }

        [Fact]
        public void Extract_ShouldKeepFirstAppearanceOrderAndDedupe()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "`@po_intRetVal = -9` ... `@po_intRetVal = -1` ... `@po_intRetVal = -9`")));

            Assert.Equal(new[] { "-9", "-1" }, result["up_x"]);
        }

        [Fact]
        public void Extract_ShouldNotCreateKeyForSpecWithNoMatch()
        {
            // 빈 목록과 "그런 프로시저 없음"은 다른 사실이다. 빈 목록을 만들면
            // 보강기가 "코드가 없는 프로시저"로 오해한다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("Feedback_Log.txt", "이전 시도에 대한 검토 피드백")));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldKeyByBareNameLowercased()
        {
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("SETTLE_CARD_DB.dbo.UP_Mixed_Case", "`@po_intRetVal = -3`")));

            Assert.True(result.ContainsKey("up_mixed_case"));
        }

        [Fact]
        public void Extract_ShouldMergeDuplicateFileNames()
        {
            // 같은 프로시저가 두 번 실릴 일은 없어야 하지만, 들어와도 마지막 것이
            // 앞의 것을 조용히 덮어쓰면 코드가 사라진다.
            var result = SpecReturnCodeExtractor.Extract(
                Specs(("dbo.UP_X", "`@po_intRetVal = -1`"), ("dbo.UP_X", "`@po_intRetVal = -2`")));

            Assert.Equal(new[] { "-1", "-2" }, result["up_x"]);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SpecReturnCodeExtractorTests" -v q --nologo`

Expected: 컴파일 실패 — `SpecReturnCodeExtractor` 형식이 없음 (CS0103 또는 CS0246)

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/SpecReturnCodeExtractor.cs` 신규:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 명세서 본문에서 원본 프로시저의 반환 오류코드를 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 목차(PlanStructure)의 ErrorCodes는 AI가 채우는데
    /// 실측 두 회차에서 26개 단계 중 25개가 빈 배열이었다. 하한 검사는 그 배열을
    /// foreach로 돌므로 0회 반복하고 통과했다 - 12/12, 13/14 단계의 오류코드 검증이
    /// 무실행인 채 "에러 개수: 0개"로 기록됐다.
    ///
    /// 코드는 문서에서 사라진 적이 없다. 같은 단계 본문이 코드를 산문으로 다 적고
    /// 있었다(S06은 배열이 비었는데 본문에 16개). 모델이 못 쓰는 것이 아니라
    /// 기계 판독 배열만 비운다. 그래서 AI에게 다시 시키는 대신 명세서에서 뽑는다.
    ///
    /// 변수명을 <c>@po_intRetVal</c>로 고정하는 이유는 좁히기 위해서가 아니라
    /// 노이즈를 배제하기 위해서다. 명세서 본문에는 "취소거래의 금액을 -1배
    /// 처리합니다" 같은 서술과 날짜(2026-08-05)의 음수가 흔해, 일반 음수 패턴으로
    /// 훑으면 그 전부를 코드로 오인한다. 실측 명세서 14종에서 반환 변수는 이
    /// 이름 하나뿐이다(247회).
    /// </summary>
    public static class SpecReturnCodeExtractor
    {
        private static readonly Regex ReturnAssignmentRegex = new(
            @"@po_intRetVal\s*=\s*(?<code>-?\d+)",
            RegexOptions.Compiled);

        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Extract(
            IEnumerable<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (specs == null)
            {
                return result;
            }

            foreach (var (fileName, content) in specs)
            {
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var codes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in ReturnAssignmentRegex.Matches(content))
                {
                    var code = match.Groups["code"].Value;
                    if (seen.Add(code))
                    {
                        codes.Add(code);
                    }
                }

                // 매치가 없으면 키를 만들지 않는다. 빈 목록과 "그런 프로시저 없음"이
                // 같아지면, 보강기가 "명세서에 코드가 없는 프로시저"로 오해한다.
                if (codes.Count == 0)
                {
                    continue;
                }

                var key = BareName(fileName);

                // 같은 이름이 두 번 들어오면 덮어쓰지 않고 합친다. 덮어쓰면
                // 앞 항목의 코드가 조용히 사라진다.
                if (result.TryGetValue(key, out var existing))
                {
                    var merged = new List<string>(existing);
                    foreach (var code in codes)
                    {
                        if (!merged.Contains(code, StringComparer.Ordinal))
                        {
                            merged.Add(code);
                        }
                    }

                    result[key] = merged;
                    continue;
                }

                result[key] = codes;
            }

            return result;
        }

        /// <summary>
        /// 명세서 파일명("dbo.UP_X", "SETTLE_CARD_DB.dbo.UP_X")과 목차의
        /// LegacyProcedures("UP_X", "dbo.UP_X")를 같은 키로 만든다. 두 표기의
        /// 조각 수가 다르므로 마지막 점 뒤만 본다 - 기존 SpecPathForStep이
        /// 쓰는 규칙과 같다.
        /// </summary>
        public static string BareName(string procedureOrFileName)
        {
            var index = procedureOrFileName.LastIndexOf('.');
            var bare = index >= 0 ? procedureOrFileName[(index + 1)..] : procedureOrFileName;
            return bare.Trim().ToLowerInvariant();
        }
    }
}
```

`merged.Contains(code, StringComparer.Ordinal)`는 `System.Linq`가 필요하다. `using System.Linq;`를 추가하거나 `seen` 집합을 재활용해도 좋다 — 실제로 빌드해서 확인할 것.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SpecReturnCodeExtractorTests" -v q --nologo`

Expected: PASS 7건

- [ ] **Step 5: 실측 명세서로 확인한다**

워크트리에는 `output/`이 없다(git 추적 대상이 아니다). 공유 트리의 실측 명세서로 추출 수를 대조한다. **읽기만 한다.**

Run:
```bash
cd /Users/payletter/git-root/ReSet && for d in output/Procedures/*/; do n=$(grep -oE '@po_intRetVal\s*=\s*-?[0-9]+' "$d/docs/Spec.md" | grep -oE '\-?[0-9]+$' | sort -u | wc -l | tr -d ' '); echo "$n ${d}"; done
```

Expected: 14개 명세서 전부 1개 이상. `UP_UTIL_SETTLE_COMM_UPD` 16, `UP_UTIL_SETTLE_EXCEPTION_PROC` 16, `UP_Util_PG_Client_CMRate_Ins` 10.

숫자가 다르면 명세서가 그 사이 재생성된 것이다(다른 세션이 캐시를 무효화한다). 그 사실을 보고하되, 각 명세서가 1개 이상만 내면 진행해도 좋다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecReturnCodeExtractor.cs tests/ReSet.Core.Tests/SpecReturnCodeExtractorTests.cs
git commit -m "feat: extract original return codes from the procedure specs

The plan's ErrorCodes array is what the floor check compares against, and
across two measured runs the model filled it once in 26 steps. The codes
were never missing from the documents, only from the array - the same step
bodies spell all of them out in prose.

@po_intRetVal is the sole return variable across all 14 specs, so pinning
the variable name buys precision: a bare negative-number scan would also
catch '-1배 처리' and the -05 of a date.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 2: 목차 보강기

**Files:**
- Create: `src/ReSet.Core/Services/PlanStructureEnricher.cs`
- Test: `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`

**Interfaces:**
- Consumes: `SpecReturnCodeExtractor.Extract`의 반환 타입, `SpecReturnCodeExtractor.BareName`
- Produces: `public static class PlanStructureEnricher`
  - `public static string Enrich(string? planStructureMarkdown, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)`
  - 실패 시 입력을 그대로 반환한다. 예외를 던지지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs` 신규:

```csharp
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
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~PlanStructureEnricherTests" -v q --nologo`

Expected: 컴파일 실패 — `PlanStructureEnricher` 형식이 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/PlanStructureEnricher.cs` 신규:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 목차의 ErrorCodes를 명세서에서 추출한 코드로 채운다.
    ///
    /// 목차 마크다운을 받아 목차 마크다운을 돌려주는 이유: 파이프라인은 목차를
    /// 문자열로 들고 다니고 그 문자열 하나가 파일 기록·파싱·프롬프트의 단일
    /// 출처다. 파싱된 객체만 보강하면 PlanStructure.md에는 빈 배열이 남아,
    /// 나중에 파일을 여는 사람이 무엇을 검사했는지 알 수 없다.
    ///
    /// 실패는 예외가 아니라 원본 반환이다. 보강은 개선이지 필수 단계가 아니다 -
    /// 보강이 실패해도 하한 검사가 "검증 불가"로 그 사실을 기록한다.
    /// </summary>
    public static class PlanStructureEnricher
    {
        // BatchStepPlanParser와 같은 정규식이어야 한다. 두 곳이 다른 블록을
        // 고르면 파일에 기록된 목차와 실제로 쓰이는 목차가 갈라진다.
        private static readonly Regex JsonBlockRegex = new(
            @"```json\s*\r?\n(?<body>.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            // 기본 인코더는 비ASCII를 \uXXXX로 이스케이프한다. 한글 단계명이
            // 그렇게 되면 PlanStructure.md를 사람이 읽을 수 없다.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Enrich(
            string? planStructureMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return planStructureMarkdown ?? string.Empty;
            }

            if (codesByProcedure == null || codesByProcedure.Count == 0)
            {
                return planStructureMarkdown;
            }

            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var body = match.Groups["body"].Value;
                var rewritten = TryRewriteBlock(body, codesByProcedure);
                if (rewritten == null)
                {
                    // 파서와 같은 규칙: 유효하지 않은 블록은 건너뛰고 다음을 본다.
                    continue;
                }

                var bodyGroup = match.Groups["body"];
                return planStructureMarkdown[..bodyGroup.Index]
                    + rewritten
                    + planStructureMarkdown[(bodyGroup.Index + bodyGroup.Length)..];
            }

            Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
            return planStructureMarkdown;
        }

        /// <summary>
        /// 유효한 Steps 블록이면 보강된 JSON 문자열을, 아니면 null을 돌려준다.
        /// </summary>
        private static string? TryRewriteBlock(
            string json, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            if (root is not JsonObject obj ||
                !obj.TryGetPropertyValue("Steps", out var stepsNode) ||
                stepsNode is not JsonArray steps)
            {
                return null;
            }

            var enrichedCount = 0;
            foreach (var stepNode in steps)
            {
                if (stepNode is not JsonObject step)
                {
                    continue;
                }

                var merged = MergeCodes(step, codesByProcedure);
                if (merged == null)
                {
                    continue;
                }

                // ErrorCodes만 교체한다. 객체를 새로 만들면 Chunkable처럼 이미
                // 있는 필드나 나중에 늘어날 필드가 조용히 사라진다.
                step["ErrorCodes"] = new JsonArray(Array.ConvertAll(merged, c => (JsonNode?)JsonValue.Create(c)));
                enrichedCount++;
            }

            if (enrichedCount > 0)
            {
                Log.Information("목차의 오류코드를 명세서에서 보강했습니다 - 단계 수: {Count}개", enrichedCount);
            }

            // 파서가 다시 읽을 수 있는 형태여야 한다. 들여쓰기는 사람이 읽기 위한 것이다.
            return root.ToJsonString(WriteOptions) + "\n";
        }

        /// <summary>
        /// 이 단계의 최종 오류코드 목록. 바뀔 것이 없으면 null.
        ///
        /// 순서는 목차 선언분이 먼저, 그다음 명세서 등장 순서다. 같은 입력에
        /// 같은 출력이고 두 번 태워도 같다(멱등) - 목차는 재수립·구제 채택
        /// 경로에서 여러 번 오간다.
        /// </summary>
        private static string[]? MergeCodes(
            JsonObject step, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            var declared = ReadStringArray(step, "ErrorCodes");
            var procedures = ReadStringArray(step, "LegacyProcedures");

            // 레거시 출신이 없으면 보존할 원본 코드가 애초에 없다. 비운 채 둔다.
            if (procedures.Count == 0)
            {
                return null;
            }

            var merged = new List<string>(declared);
            var seen = new HashSet<string>(declared, StringComparer.Ordinal);
            var changed = false;

            foreach (var procedure in procedures)
            {
                if (!codesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var codes))
                {
                    continue;
                }

                foreach (var code in codes)
                {
                    if (seen.Add(code))
                    {
                        merged.Add(code);
                        changed = true;
                    }
                }
            }

            return changed ? merged.ToArray() : null;
        }

        private static List<string> ReadStringArray(JsonObject step, string name)
        {
            var values = new List<string>();
            if (!step.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
            {
                return values;
            }

            foreach (var item in array)
            {
                var value = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values;
        }
    }
}
```

`item?.GetValue<string>()`는 항목이 문자열이 아니면 `InvalidOperationException`을 던진다. 목차의 배열은 문자열만 담기로 되어 있지만 AI 산출물이므로 실제로 확인하고, 필요하면 `item is JsonValue v && v.TryGetValue<string>(out var s)` 형태로 방어할 것.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~PlanStructureEnricherTests" -v q --nologo`

Expected: PASS 12건

특히 `Enrich_ShouldBeIdempotent`와 `Enrich_RoundTripsThroughTheParser`가 통과해야 한다. 멱등이 깨지면 `ToJsonString`의 출력이 다시 파싱→직렬화 시 달라진다는 뜻이니 그 원인을 찾을 것.

- [ ] **Step 5: 전체 테스트로 회귀를 확인한다**

Run: `dotnet test ReSet.slnx -v q --nologo`

Expected: 실패 0, 통과 1040 + 19 = 1059

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/PlanStructureEnricher.cs tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs
git commit -m "feat: merge extracted return codes into the plan structure

Enriching the parsed objects alone would leave PlanStructure.md holding the
empty arrays, so a later reader could not tell what the gate compared. The
markdown string is the single source the pipeline writes, parses, and
prompts from, so the enrichment has to land there.

Only the ErrorCodes array is replaced; rebuilding the step object would drop
Chunkable and anything added later. The round-trip test pins the contract
that matters - what the file records and what the check uses must not drift.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: 오케스트레이터 배선

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`
  - 최초 목차 수립 직후 (`currentPlanStructure = planResult.Content;` 부근, 약 1782행)
  - `DraftReplacementPlanStructureAsync` 시그니처와 반환 직전 (약 2450–2491행)
  - 그 메서드의 호출부 2곳 (약 1978행, 2154행)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `SpecReturnCodeExtractor.Extract`, `PlanStructureEnricher.Enrich`
- Produces: `DraftReplacementPlanStructureAsync`에 매개변수 추가 —
  `IReadOnlyDictionary<string, IReadOnlyList<string>> returnCodes`가 `string reason` 다음에 온다.

**구현 노트**

목차 마크다운이 **새로 만들어지는 곳은 두 군데뿐**이다. 구제 채택(`AdoptPlanStructureForRescueAsync`)은 이미 보강을 거친 문자열을 되쓰므로 자동으로 덮인다. 이 두 곳 밖에 전파 경로를 만들지 말 것.

추출은 회차마다 하지 말고 재시도 루프에 들어가기 **전에 한 번** 한다(`stepFloorViolations` 선언 부근, 약 1725행). 명세서는 루프 안에서 바뀌지 않는다.

추출 입력은 `specs`다. `specsCopy`가 아니다 — 그쪽에는 `Feedback_Log.txt`가 붙어 있고, 그것은 명세서가 아니다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 기존 테스트 형식을 먼저 읽고 그 헬퍼(가짜 `IAiService`·`IVerificationUserInteraction` 구성)를 재사용한다. 새 테스트 둘을 추가한다:

```csharp
[Fact]
public async Task Pipeline_ShouldWriteEnrichedErrorCodesToPlanStructureFile()
{
    // 목차는 ErrorCodes를 비운 채 내고, 명세서에는 코드가 있다.
    // 파이프라인이 끝난 뒤 PlanStructure.md가 보강된 값을 담아야 한다.
    // (기존 테스트가 쓰는 가짜 AI 서비스 구성 방식을 그대로 따를 것)
    // ...준비: DraftBatchPlanStructureAsync가 ErrorCodes: [] 목차를 내도록,
    //         specs에 "dbo.UP_X" -> "`@po_intRetVal = -7`" 을 넣는다.

    var written = await File.ReadAllTextAsync(
        Path.Combine(outputRoot, "Jobs", jobName, "raw", "PlanStructure.md"));

    Assert.Contains("-7", written);
    var parsed = BatchStepPlanParser.TryParse(written);
    Assert.Equal(new[] { "-7" }, parsed!.Single(s => s.Code == "S01").ErrorCodes);
}

[Fact]
public async Task Pipeline_ShouldEnrichTheRedraftedStructureToo()
{
    // 재수립으로 새 목차가 들어와도 보강을 거쳐야 한다. 최초 수립에만 걸면
    // 재수립 이후 회차의 검사가 다시 무실행이 된다.
    // ...준비: 점수 정체를 만들어 StructureRedraftPolicy가 발동하게 하고,
    //         재수립 응답도 ErrorCodes: [] 목차를 내게 한다.

    Assert.Contains("-7", finalPlanStructureOnDisk);
}
```

기존 테스트 파일에 이 시나리오를 세울 헬퍼가 없으면, 있는 헬퍼를 최소로 확장해 쓴다. 새 테스트 인프라를 짓지 말 것.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~Pipeline_ShouldWriteEnrichedErrorCodes|FullyQualifiedName~Pipeline_ShouldEnrichTheRedrafted" -v q --nologo`

Expected: FAIL — 기록된 목차의 `ErrorCodes`가 비어 있음

- [ ] **Step 3: 배선을 넣는다**

세 곳을 고친다.

(1) 재시도 루프 진입 전, `var stepFloorViolations = new Dictionary<string, string>();` 근처:

```csharp
// 목차가 낼 ErrorCodes는 하한 검사의 유일한 대조 기준인데, 실측 두 회차에서
// 26개 단계 중 25개가 빈 배열이었다. 명세서에서 뽑아 채운다. 명세서는 루프
// 안에서 바뀌지 않으므로 한 번만 뽑는다.
//
// specsCopy가 아니라 specs를 넘긴다 - specsCopy에는 Feedback_Log.txt가
// 붙는데 그것은 명세서가 아니다.
var specReturnCodes = SpecReturnCodeExtractor.Extract(specs);
```

(2) 최초 목차 수립 직후, `currentPlanStructure = planResult.Content;`를 다음으로 바꾼다:

```csharp
currentPlanStructure = PlanStructureEnricher.Enrich(planResult.Content, specReturnCodes);
```

이어지는 `File.WriteAllTextAsync(... "PlanStructure.md", currentPlanStructure)`는 그대로 둔다 — 이미 보강된 문자열을 쓴다.

(3) `DraftReplacementPlanStructureAsync`에 매개변수를 추가하고 반환 직전에 보강한다:

```csharp
private async Task<string?> DraftReplacementPlanStructureAsync(
    string reason,
    IReadOnlyDictionary<string, IReadOnlyList<string>> returnCodes,
    string currentStructure,
    string brainstorming,
    string? redraftFeedback,
    string targetLanguage,
    string jobName,
    CancellationToken cancellationToken)
{
    // ... 본문 그대로 ...

    // 재수립 경로와 L3 사용자 요청 경로가 이 헬퍼를 공유한다. 여기서 한 번
    // 보강하면 두 경로가 함께 덮인다 - 호출부마다 따로 걸면 하나를 빠뜨린다.
    return PlanStructureEnricher.Enrich(redrafted, returnCodes);
}
```

호출부 2곳에 `specReturnCodes`를 두 번째 인자로 넘긴다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests" -v q --nologo`

Expected: 신규 2건 포함 전부 PASS

- [ ] **Step 5: 전체 테스트와 빌드**

Run: `dotnet build ReSet.slnx -v q --nologo && dotnet test ReSet.slnx -v q --nologo`

Expected: 경고 8개 / 오류 0개, 실패 0

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: enrich the plan structure wherever it is drafted

Two sites produce a structure markdown: the first draft and the shared
redraft helper that both the stagnation path and the L3 user path call.
Enriching inside the helper covers both without a third call site to forget.
Rescue adoption re-uses a string that already went through one of them.

Extraction runs once before the retry loop - the specs do not change inside
it - and takes specs rather than specsCopy, which carries an appended
feedback log that is not a spec.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 4: 하한 검사 3분류

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ValidateBatchStep` 약 164–245행, `StepValidationResult` 약 758–800행)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `StepValidationResult`의 기존 `PlanDefects`/`RegenerationCanFix`는 유지. 동작 변경 —
  `ErrorCodes`가 비어도 `LegacyProcedures`가 비어 있으면 결함으로 들지 않는다.

**배경**

현재 코드(커밋 `fcf534f`)는 빈 `ErrorCodes`를 무조건 결함으로 든다. Task 3의 보강이 들어오면 대부분 채워지지만, 레거시 출신이 없는 단계(`S00` 실행 잠금 사전검증, `S08` 수수료 총액 확정)는 보강 후에도 비어 있다. 그것은 결함이 아니라 **해당 없음**이다. 정상 설계에 배너가 붙으면 배너의 변별력이 사라진다.

`TargetTables` 축은 독립이다. `LegacyProcedures`가 비어 오류코드가 "해당 없음"인 단계라도 `TargetTables`가 비면 그 축은 "검증 불가"다 — 레거시 출신이 없다는 것과 쓰는 테이블이 없다는 것은 다른 사실이다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`의 `ValidateBatchStep` 블록 끝에 추가:

```csharp
[Fact]
public void ValidateBatchStep_WithNoLegacyProcedure_TreatsEmptyErrorCodesAsNotApplicable()
{
    // 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다. 실측
    // POQSettleProcDaily6의 S00(실행 잠금 사전검증)과 S08(수수료 총액 확정)이
    // 그런 경우로, 둘 다 계획이 새로 설계한 단계다.
    var plan = S10Plan() with
    {
        LegacyProcedures = Array.Empty<string>(),
        ErrorCodes = Array.Empty<string>(),
    };

    var result = _validator.ValidateBatchStep(S10HealthySection, plan);

    Assert.True(result.IsValid);
    Assert.Empty(result.PlanDefects);
}

[Fact]
public void ValidateBatchStep_WithLegacyProcedureButNoErrorCodes_StillFails()
{
    // 출신이 있는데 코드가 비었다면 보강이 실패한 것이다. 그 사실은 남아야 한다.
    var plan = S10Plan() with { ErrorCodes = Array.Empty<string>() };

    var result = _validator.ValidateBatchStep(S10HealthySection, plan);

    Assert.False(result.IsValid);
    Assert.Contains(result.PlanDefects, d => d.Contains("ErrorCodes"));
}

[Fact]
public void ValidateBatchStep_WithNoLegacyProcedure_StillChecksTargetTables()
{
    // 두 축은 독립이다. 출신이 없다는 것과 쓰는 테이블이 없다는 것은 다른 사실이고,
    // 아무것도 쓰지 않는다는 선언은 그 자체로 확인이 필요하다.
    var plan = S10Plan() with
    {
        LegacyProcedures = Array.Empty<string>(),
        ErrorCodes = Array.Empty<string>(),
        TargetTables = Array.Empty<string>(),
    };

    var result = _validator.ValidateBatchStep(S10HealthySection, plan);

    Assert.False(result.IsValid);
    Assert.Contains(result.PlanDefects, d => d.Contains("TargetTables"));
    Assert.DoesNotContain(result.PlanDefects, d => d.Contains("ErrorCodes"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~ValidateBatchStep" -v q --nologo`

Expected: 신규 3건 중 최소 2건 FAIL — 지금은 `LegacyProcedures`와 무관하게 빈 `ErrorCodes`를 결함으로 든다

- [ ] **Step 3: 구현을 고친다**

`MechanicalValidator.ValidateBatchStep`의 `ErrorCodes` 판정에 조건을 하나 더 건다:

```csharp
// 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다 - 대조 항목 0개가
// 정상이다. 이것을 결함으로 들면 계획이 새로 설계한 정상 단계에 배너가 붙어
// 배너의 변별력이 사라진다.
//
// TargetTables 축은 여기에 딸리지 않는다. 출신이 없다는 것과 쓰는 테이블이
// 없다는 것은 다른 사실이고, 아무것도 쓰지 않는다는 선언은 그 자체로 확인이 필요하다.
if (step.LegacyProcedures.Count > 0 &&
    !step.ErrorCodes.Any(code => !string.IsNullOrWhiteSpace(code)))
{
    result.PlanDefects.Add(
        $"{step.Code}의 목차 ErrorCodes가 비어 있어 원본 오류코드 대조를 실행할 수 없습니다.");
}
```

`TargetTables` 판정은 그대로 둔다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~ValidateBatchStep" -v q --nologo`

Expected: 전부 PASS

- [ ] **Step 5: 전체 테스트**

Run: `dotnet test ReSet.slnx -v q --nologo`

Expected: 실패 0

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "fix: stop flagging steps that have no legacy origin

A step the plan designed from scratch has no original return codes to
preserve, so an empty array is the correct answer rather than a defect.
Two of fourteen steps in the measured run are like that. Banner every one of
them and the banner stops distinguishing anything.

The target-table axis does not follow along: having no legacy origin and
writing no tables are different claims, and a step that declares it writes
nothing still needs checking.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 5: 위반 사전의 값 타입 승격

**Files:**
- Create: `src/ReSet.Core/Services/StepDefect.cs`
- Modify: `src/ReSet.Core/Models/PlanLayout.cs:23`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (약 17곳 — 1702, 1725, 1747, 1954, 2170, 2204, 2245, 2368, 2507, 2533, 2544, 2551, 2572, 2579, 2675, 2720, 2725행 및 `GenerateStepSectionWithFloorRetryAsync` 반환)
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs` (`BuildFloorBanner` 약 293–310행)
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs` (`StepFloorViolations` 시그니처는 이 태스크에서 바꾸지 않는다 — Task 6에서 한다)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`, `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`, `tests/ReSet.Core.Tests/VerificationBannerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  public enum StepDefectKind { QualityFloor, Unverifiable }
  public sealed record StepDefect(StepDefectKind Kind, string Reason);
  ```
  `PlanLayout.FloorViolations`의 타입이 `IReadOnlyDictionary<string, StepDefect>?`가 된다.
  `Reason`은 지금 사전 값이 담고 있던 표시 문자열(`"{Code} (사유)"` 형식)을 그대로 담는다.

**이 태스크의 계약: 출력이 바뀌지 않는다.** 타입만 올리고 배너 문구·구성은 그대로 둔다. 기존 테스트가 전부 통과해야 한다 — 통과하지 않으면 타입 승격 과정에서 동작을 바꾼 것이다.

사전을 둘로 쪼개지 않고 값에 종류를 싣는 이유: 쪼개면 `RestoreAdoptedGenerationState`, `ClearSplitGenerationCacheAfterRedraft`, 분할 생성 캐시가 전부 두 벌을 원자적으로 다뤄야 한다. 그 원자성은 이미 까다롭게 짜여 있어 손대는 비용이 타입 변경보다 크다.

- [ ] **Step 1: 타입을 만든다**

`src/ReSet.Core/Services/StepDefect.cs` 신규:

```csharp
namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 하나에 대해 하한 검사가 낸 판정의 종류.
    ///
    /// 둘을 가르는 이유: 실측에서 14개 단계 중 13개에 "품질 미달" 배너가 붙었는데,
    /// 그 13개는 섹션이 부실한 것이 아니라 대조할 재료가 목차에 없어 검사가 돌지
    /// 못한 것이었다. 두 사실을 같은 배너로 내면 읽는 사람이 어느 쪽인지 알 수
    /// 없고, 배너가 대부분의 단계에 붙어 변별력도 사라진다.
    /// </summary>
    public enum StepDefectKind
    {
        /// <summary>본문이 최소 요건을 못 채웠다. 재생성으로 고칠 수 있다.</summary>
        QualityFloor,

        /// <summary>대조할 재료가 목차에 없어 검사를 실행하지 못했다. 재생성으로 고쳐지지 않는다.</summary>
        Unverifiable,
    }

    /// <param name="Reason">"{Code} (사유)" 형식의 표시 문자열. 배너가 그대로 싣는다.</param>
    public sealed record StepDefect(StepDefectKind Kind, string Reason);
}
```

- [ ] **Step 2: 타입을 전파하고 빌드가 통과할 때까지 고친다**

`Dictionary<string, string>` → `Dictionary<string, StepDefect>`로 바꿔야 하는 곳을 컴파일러가 짚어준다.

Run: `dotnet build ReSet.slnx -v q --nologo`

`GenerateStepSectionWithFloorRetryAsync`의 반환 튜플 `(string Markdown, string? FloorViolation)`은 `(string Markdown, StepDefect? Defect)`가 된다. 세 반환 지점의 값:

```csharp
// 검증 불가 (재생성 건너뜀)
return (content, new StepDefect(StepDefectKind.Unverifiable, $"{step.Code} ({reason})"));

// 생성 실패
return (배너 마크다운, new StepDefect(StepDefectKind.QualityFloor, $"{step.Code} (생성 실패)"));

// 재시도 후에도 미달
return (adopted, new StepDefect(StepDefectKind.QualityFloor, $"{step.Code} (하한 미달)"));
```

`AttachPipelineBanners`는 이 태스크에서 `.Select(kvp => kvp.Value.Reason)`으로만 바꿔 출력을 유지한다. 종류별 분리는 Task 6이다.

`InstructionBundleWriter.BuildFloorBanner`는 `TryGetValue`로 받은 값에서 `.Reason`을 꺼내 쓰도록만 바꾼다. 문구는 그대로.

- [ ] **Step 3: 기존 테스트를 새 타입에 맞춘다**

`VerificationPipelineOrchestratorTests`, `InstructionBundleWriterTests`, `VerificationBannerTests`에서 사전을 만드는 곳을 고친다. **단언은 바꾸지 말 것** — 출력이 같아야 하므로 단언이 그대로 통과해야 정상이다. 단언을 고쳐야 한다면 동작을 바꾼 것이니 멈추고 원인을 찾을 것.

- [ ] **Step 4: 전체 테스트로 무변화를 확인한다**

Run: `dotnet build ReSet.slnx -v q --nologo && dotnet test ReSet.slnx -v q --nologo`

Expected: 경고 8개 / 오류 0개, 실패 0, 통과 수는 Task 4 종료 시점과 동일

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "refactor: carry the defect kind alongside the reason

The violation dictionary held a display string, so a section that fell short
and a section the check could not run against were indistinguishable by the
time they reached a banner. The value now carries which one it is.

The kind rides in the value rather than splitting the dictionary in two:
adopted-state restore, redraft cache clearing, and the split-generation cache
would each have to keep two of them atomically, and that atomicity is already
delicate. No output changes in this commit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 6: 배너 출력 분리

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs` (`StepFloorViolations` 약 108–122행)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`AttachPipelineBanners` 약 2366–2390행)
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs` (`BuildFloorBanner` 약 293–310행)
- Test: `tests/ReSet.Core.Tests/VerificationBannerTests.cs`, `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`

**Interfaces:**
- Consumes: Task 5의 `StepDefect`, `StepDefectKind`
- Produces:
  - `VerificationBanner.StepFloorViolations(IReadOnlyList<string> steps)` — 문구 유지
  - `VerificationBanner.UnverifiableSteps(IReadOnlyList<string> steps)` — 신규

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationBannerTests.cs`에 추가:

```csharp
[Fact]
public void UnverifiableSteps_ShouldNotClaimTheSectionIsSubstandard()
{
    // 이 단계들의 섹션은 멀쩡할 수 있다. 부실하다고 단정하거나 원본 프로시저를
    // 확인하라고 지시하면 과잉이다 - 검사가 못 돌았다는 사실만 전한다.
    var banner = VerificationBanner.UnverifiableSteps(new[] { "S06 (ErrorCodes가 비어 있음)" });

    Assert.Contains("S06", banner);
    Assert.DoesNotContain("하한 미달", banner);
    Assert.DoesNotContain("최소 요건", banner);
    Assert.DoesNotContain("원본 프로시저를 직접 확인", banner);
}

[Fact]
public void StepFloorViolations_ShouldStillClaimTheSectionIsSubstandard()
{
    var banner = VerificationBanner.StepFloorViolations(new[] { "S10 (하한 미달)" });

    Assert.Contains("하한 미달", banner);
    Assert.Contains("최소 요건", banner);
}
```

`tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`에 추가:

```csharp
[Fact]
public async Task WriteAsync_ShouldUseDifferentBannerWordingPerDefectKind()
{
    var layout = new PlanLayout(
        "골격",
        new Dictionary<string, string>
        {
            ["S01"] = "### S01 스냅샷 생성\n조각 본문",
            ["S02"] = "### S02 원장 생성\n조각 본문",
        },
        new[]
        {
            new BatchStepPlan("S01", "스냅샷 생성", new[] { "UP_S01" }, new[] { "dbo.TClient" }, new[] { "-1" }, false),
            new BatchStepPlan("S02", "원장 생성", new[] { "UP_S02" }, new[] { "dbo.TLedger" }, new[] { "-1" }, false),
        },
        new Dictionary<string, StepDefect>
        {
            ["S01"] = new(StepDefectKind.QualityFloor, "S01 (하한 미달)"),
            ["S02"] = new(StepDefectKind.Unverifiable, "S02 (ErrorCodes가 비어 있음)"),
        });

    var inputs = Inputs(layout) with
    {
        SpDefs = new List<SpDefinition>
        {
            SpDefWithDependency("UP_S01", "TClient"),
            SpDefWithDependency("UP_S02", "TLedger"),
        },
    };

    await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

    var s01 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S01.md"));
    var s02 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S02.md"));

    Assert.Contains("품질 미달", s01);
    Assert.DoesNotContain("품질 미달", s02);
    Assert.Contains("검증되지", s02);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~UnverifiableSteps|FullyQualifiedName~ShouldUseDifferentBannerWordingPerDefectKind|FullyQualifiedName~StepFloorViolations_ShouldStill" -v q --nologo`

Expected: 컴파일 실패 — `UnverifiableSteps`가 없음

- [ ] **Step 3: 배너를 가른다**

`VerificationBanner.cs`에 추가:

```csharp
/// <summary>
/// 하한 검사가 대조할 재료를 얻지 못한 단계를 알린다.
///
/// StepFloorViolations와 다른 사실을 나른다 - 저건 "섹션이 부실하다"이고
/// 이건 "섹션은 멀쩡할 수 있는데 검사를 돌리지 못했다"이다. 실측에서 14개
/// 단계 중 13개가 후자였는데 전자의 문구로 나갔고, 그 결과 진입점의
/// "모두 통과"와 배너가 정면으로 모순됐다.
///
/// "원본 프로시저를 직접 확인하십시오" 같은 지시를 붙이지 않는다. 섹션이
/// 부실하다는 근거가 없으므로 과잉이다.
/// </summary>
public static string UnverifiableSteps(IReadOnlyList<string> steps)
{
    var stepLines = RenderBulletList(steps, "(단계명이 기록되지 않았습니다.)");

    return "\n> [!WARNING]\n> **[검증 불가] 아래 단계는 대조할 재료가 목차에 없어 검증되지 못했습니다.**"
        + " 섹션 내용이 부실하다는 뜻은 아닙니다 - 선언된 대상 테이블이나 원본 오류코드가 없어"
        + " 기계 대조를 실행할 수 없었다는 뜻입니다.\n"
        + stepLines
        + "\n\n";
}
```

`AttachPipelineBanners`에서 종류별로 나눠 두 블록을 붙인다. 순서는 심각한 쪽(품질 미달)을 먼저:

```csharp
var byKind = stepFloorViolations
    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
    .ToLookup(kvp => kvp.Value.Kind, kvp => kvp.Value.Reason);

// 붙이는 순서와 읽히는 순서는 반대다 - 앞에 붙일수록 문서 위로 간다.
// 검증 불가를 먼저 붙여 품질 미달이 맨 위에 오게 한다.
var unverifiable = byKind[StepDefectKind.Unverifiable].ToList();
if (unverifiable.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
{
    consolidatedPlan = VerificationBanner.UnverifiableSteps(unverifiable) + consolidatedPlan;
}

var floor = byKind[StepDefectKind.QualityFloor].ToList();
if (floor.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
{
    consolidatedPlan = VerificationBanner.StepFloorViolations(floor) + consolidatedPlan;
}
```

`InstructionBundleWriter.BuildFloorBanner`가 종류에 따라 문구를 가르게 한다:

```csharp
var (headline, tail) = defect.Kind switch
{
    StepDefectKind.Unverifiable => (
        "> ⚠️ **이 단계는 대조할 재료가 없어 검증되지 못했습니다.**",
        "> 섹션 내용이 부실하다는 뜻은 아닙니다. 목차가 대상 테이블이나 원본 오류코드를 선언하지 않아 기계 대조를 실행하지 못했습니다."),
    _ => (
        "> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**",
        "> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오."),
};
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~VerificationBannerTests|FullyQualifiedName~InstructionBundleWriterTests" -v q --nologo`

Expected: 전부 PASS

- [ ] **Step 5: 전체 테스트와 빌드**

Run: `dotnet build ReSet.slnx -v q --nologo && dotnet test ReSet.slnx -v q --nologo`

Expected: 경고 8개 / 오류 0개, 실패 0

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "fix: separate the unverifiable banner from the substandard one

Thirteen of fourteen steps in the measured run were labelled substandard when
their sections were fine and only the check had failed to run. The entry
point then said the plan passed everything two lines above a list of
thirteen failures.

The unverifiable wording states what happened and stops there - no
instruction to go read the original procedure, since nothing suggests the
section is wrong.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 7: 회귀 픽스처와 문서 동기화

**Files:**
- Create: `tests/ReSet.Core.Tests/Fixtures/PlanStructureWithEmptyErrorCodes.md`
- Create: `tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSpecExcerpt.md`
- Create: `tests/ReSet.Core.Tests/StepErrorCodeRegressionTests.cs`
- Modify: `docs/architecture.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 1–6 전부
- Produces: 없음 (종단 태스크)

**픽스처를 체크인하는 이유:** 이 결함을 발견한 실측 산출물(`output/Jobs/POQSettleProcDaily5`, `6`)은 git 추적 대상이 아니라 픽스처로 쓸 수 없다. 게다가 다른 세션이 캐시를 무효화하고 있어 명세서가 재생성될 수 있다. 결함을 재현하는 최소 형태를 저장소 안에 남긴다.

- [ ] **Step 1: 픽스처를 만든다**

`tests/ReSet.Core.Tests/Fixtures/PlanStructureWithEmptyErrorCodes.md` — 실측 `POQSettleProcDaily6`의 목차를 4단계로 축약. `S00`(출신 없음), `S01`(선언 1개), `S06`(선언 0개, 명세서 16개), `S08`(출신 없음)을 담는다. 형식은 Task 2 테스트의 `Structure` 상수와 같되 실제 목차의 산문 구조를 유지한다.

`tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSpecExcerpt.md` — 실측 `dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md`의 「로직 흐름 요약」에서 `@po_intRetVal` 대입이 있는 항목만 발췌. `-1`부터 `-23`까지의 코드를 포함하고, 노이즈(`-1배 처리`, 날짜)도 함께 남긴다 — 노이즈가 없으면 배제 검사가 무의미하다.

**읽는 방식은 기존 관례를 따른다.** `CancellationPolicyTests.cs:131`이 `RepoPaths.FindRepoRoot()`로 저장소 루트를 찾아 소스 트리에서 직접 읽는다(`cancellation-policy-baseline.txt`). 이 저장소에는 출력 디렉터리 복사(`CopyToOutputDirectory`) 설정이 하나도 없으므로 `.csproj`를 건드리지 말고 같은 방식을 쓴다.

- [ ] **Step 2: 회귀 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepErrorCodeRegressionTests.cs` 신규:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실측 회차를 축약한 픽스처로, 결함이 되살아나면 잡는다.
    ///
    /// 실측 산출물(output/Jobs/...)은 git 추적 대상이 아니라 픽스처로 쓸 수 없다.
    /// 결함을 재현하는 최소 형태만 저장소에 남긴다.
    /// </summary>
    public class StepErrorCodeRegressionTests
    {
        // CancellationPolicyTests가 baseline 파일을 읽는 방식과 같다 - 저장소
        // 루트에서 소스 트리를 직접 읽는다. 이 저장소에는 출력 디렉터리 복사
        // 설정이 없으므로 픽스처 하나 때문에 도입하지 않는다.
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        [Fact]
        public void MeasuredPlanStructure_GainsErrorCodesFromTheSpec()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var before = BatchStepPlanParser.TryParse(Fixture("PlanStructureWithEmptyErrorCodes.md"))!;
            var after = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(Fixture("PlanStructureWithEmptyErrorCodes.md"), codes))!;

            Assert.Empty(before.Single(s => s.Code == "S06").ErrorCodes);
            Assert.Equal(16, after.Single(s => s.Code == "S06").ErrorCodes.Count);
        }

        [Fact]
        public void StepsWithoutLegacyOriginStayEmptyAndPassTheFloorCheck()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var steps = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(Fixture("PlanStructureWithEmptyErrorCodes.md"), codes))!;

            var s00 = steps.Single(s => s.Code == "S00");
            Assert.Empty(s00.ErrorCodes);

            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void EnrichedStepPassesTheFloorCheckWhenTheBodyCarriesEveryCode()
        {
            // 실측에서 24개 단계 전부가 이미 코드를 본문에 담고 있었다. 보강이
            // 검사를 진짜로 만들되 재시도 폭주를 부르지는 않는다는 뜻이다.
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var s06 = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(Fixture("PlanStructureWithEmptyErrorCodes.md"), codes))!
                .Single(s => s.Code == "S06");

            var body = $"### {s06.Code} {s06.Name}\n\n```sql\nSELECT 1 FROM {s06.TargetTables[0]};\n```\n\n"
                + string.Join(" ", s06.ErrorCodes.Select(c => $"`{c}`"));

            var result = new MechanicalValidator().ValidateBatchStep(body, s06);

            Assert.True(result.IsValid);
        }
    }
}
```

- [ ] **Step 3: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~StepErrorCodeRegressionTests" -v q --nologo`

Expected: PASS 3건

`RepoPaths`의 네임스페이스는 `CancellationPolicyTests.cs` 상단에서 확인할 것.

- [ ] **Step 4: 문서를 동기화한다**

**다른 세션도 이 두 파일을 고친다.** 병합 충돌을 줄이기 위해 **기존 줄을 고치지 말고 추가만 한다.** 테스트 개수는 마지막에 실제 값으로 맞춘다.

`docs/architecture.md` — §2.2 테이블에 두 행 추가:

| 클래스 | 설명 |
|---|---|
| `SpecReturnCodeExtractor` | 명세서 본문의 `@po_intRetVal` 대입에서 원본 반환 오류코드를 뽑는다 |
| `PlanStructureEnricher` | 목차의 `ErrorCodes`를 추출된 코드로 채워 하한 검사에 대조 기준을 준다 |

§4에 메커니즘 절을 하나 추가한다 — 목차의 검사 재료를 AI에게 다시 요구하지 않고 명세서에서 결정론적으로 채운다는 것, 그리고 "품질 미달"과 "검증 불가"와 "해당 없음"이 서로 다른 사실이라는 것.

`AGENTS.md` — 프로젝트 구조 바로가기에 두 파일을 추가하고, 파이프라인 규칙에 한 줄:

> 하한 검사의 대조 기준(`ErrorCodes`)은 AI가 아니라 도구가 명세서에서 채웁니다. 빈 배열은 통과가 아니라 "검증 불가"입니다. 단, `LegacyProcedures`가 비어 있는 단계는 보존할 원본 코드가 없으므로 정상입니다.

`<!-- synced-through: ... -->` 주석은 **이 태스크에서 갱신하지 않는다.** 이 브랜치는 아직 `main`에 없고, 다른 세션도 같은 주석을 건드린다. 병합 후 `/reset-doc-sync`가 한 번에 맞춘다.

- [ ] **Step 5: 최종 검증**

Run: `dotnet build ReSet.slnx -v q --nologo --no-incremental && dotnet test ReSet.slnx -v q --nologo`

Expected: 경고 8개 / 오류 0개, 실패 0

`AGENTS.md`의 테스트 개수를 이 출력의 실제 값으로 맞춘다.

링크 검증:

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```

Expected: 출력 없음

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "test: pin the measured defect with checked-in fixtures

The artifacts that exposed this - two job outputs under output/ - are not
tracked, and a parallel branch is invalidating the analysis cache that
regenerates the specs, so neither can serve as a fixture. These are the
smallest shapes that still reproduce it.

Docs additions only, no edits to existing lines: another session is editing
the same two files. The synced-through markers are left alone for a single
doc-sync pass after both branches land.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## 자체 점검

**스펙 커버리지**

| 스펙 요구 | 태스크 |
|---|---|
| §1 `SpecReturnCodeExtractor` (패턴, 키, 순서, 빈 키 없음) | Task 1 |
| §2 `PlanStructureEnricher` (같은 정규식, 합집합, 순서, 필드 보존, 실패 시 원본) | Task 2 |
| §3 배선 두 지점, 추출 1회, `specs` 사용 | Task 3 |
| §4 결함 3분류, "해당 없음" | Task 4 |
| §5 `StepDefect` 타입 승격 | Task 5 |
| §6 출력 형태 — 문서 배너 2블록, 단계 파일 문구 분기 | Task 6 |
| §6 진입점 §0 문구 | **Task 6에서 자동 해소.** 진입점의 배너는 계획서 머리말(`slices.Preamble`)을 그대로 실어 나르므로, 문서 배너가 갈리면 진입점도 함께 갈린다. Task 6 Step 5에서 진입점 산출물을 눈으로 확인할 것 |
| 오류 처리 표 4행 | Task 2 (블록 없음/파싱 실패), Task 1 (프로시저 없음/매치 0), Task 2 (왕복) |
| 테스트 계획 전 항목 | Task 1·2·4·6·7 |
| 문서 동기화 | Task 7 |
| 완료 기준 1–4 | Task 3 (1), Task 7 (2), Task 6 (3), Task 7 Step 5 (4) |

**플레이스홀더 점검**

Task 3 Step 1의 테스트가 기존 헬퍼 사용을 전제로 `// ...준비:` 주석을 남긴 유일한 자리다. 그 파일의 픽스처 구성 방식을 모르는 채 코드를 지어내면 컴파일되지 않는 코드를 넘기게 되므로, 의도적으로 "기존 헬퍼를 읽고 따르라"는 지시로 남겼다. 구현자는 그 파일을 먼저 읽어야 한다.

**타입 일관성**

- `SpecReturnCodeExtractor.Extract` 반환 `IReadOnlyDictionary<string, IReadOnlyList<string>>` — Task 2·3에서 같은 타입으로 소비
- `SpecReturnCodeExtractor.BareName` — Task 2가 호출하므로 `public`이어야 한다 (Task 1에서 `public`으로 선언)
- `PlanStructureEnricher.Enrich(string?, IReadOnlyDictionary<...>)` — Task 3의 두 호출부와 일치
- `StepDefect(Kind, Reason)` — Task 5에서 정의, Task 6이 `.Kind`/`.Reason` 사용
- `PlanLayout.FloorViolations` — Task 5에서 `IReadOnlyDictionary<string, StepDefect>?`, Task 6 테스트가 그 타입으로 생성
- `StepValidationResult.PlanDefects` — Task 4가 사용, Task 5에서 `GenerateStepSectionWithFloorRetryAsync`가 `RegenerationCanFix`로 종류를 결정
