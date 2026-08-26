# 단계 의무의 귀속 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 레거시 출신이 없는 단계에 결정적으로 발급되는 오류코드 규약을 세우고, 여러 단계로 쪼개진 SP의 코드·테이블 의무를 단계에서 문서 단위로 올린다.

**Architecture:** `BatchControlContract`에 예약 대역과 블록 유도 규칙을 상수로 둔다. `PlanStructureEnricher`가 레거시 없는 단계에 블록 시작 코드를 발급한다. `MechanicalValidator`가 (1) 그 단계들이 돌려주는 코드가 블록 안인지 보고 (2) 분할 SP에서만 유래한 코드·테이블은 단계마다 요구하지 않는다. `VerificationPipelineOrchestrator.GenerateBySplitAsync`가 단계 본문을 합쳐 분할 SP의 의무를 문서 단위로 검사한다.

**Tech Stack:** C# .NET 10, xUnit, NSubstitute, Serilog.

**Spec:** `docs/superpowers/specs/2026-08-26-step-obligation-attribution-design.md`

## Global Constraints

- 코드 주석과 커밋 메시지는 한국어로 쓴다. 주석은 *무엇을*이 아니라 *왜*를 적고, 실측 근거가 있으면 함께 적는다.
- 작업은 전용 `git worktree`에서 한다. `main`에 직접 커밋하지 않는다(`AGENTS.md` 범주 8).
- API Key 등 비공개 자격증명을 소스나 `appsettings.json`에 넣지 않는다(`AGENTS.md` 범주 1).
- 각 태스크 종료 시 `dotnet build` 경고 0·오류 0, `dotnet test` 실패 0이어야 한다.
- 코퍼스 테스트가 건너뛰지 않게 워크트리에 코퍼스를 연결한다: `ln -s /Users/payletter/git-root/ReSet/output output`. 건너뜀이 0이어야 코퍼스 단언이 실제로 돈 것이다.
- 예약 대역 값은 정확히 이 규칙이다: 블록 시작 = `-9000 - (N * 10)`, 블록 크기 10. `S01` → `-9010`~`-9019`, `S16` → `-9160`~`-9169`.
- 새 회귀 테스트는 red-green을 확인한다 — 변이를 주입해 그 테스트가 실제로 실패하는 것을 보고 되돌린다. 통과만 하는 테스트는 이 저장소에서 이미 비용을 냈다.

---

## File Structure

**생성**

- `src/ReSet.Core/Services/ControlStepErrorCodes.cs` — 예약 대역 하나만 책임진다. 블록 유도, 대역 소속 판정, 프롬프트 문구. `BatchControlContract`에 넣지 않고 분리하는 이유: 그 파일은 이미 400줄이 넘고 제어 *테이블* 계약을 담당한다. 오류코드 대역은 다른 축이고, 검증기·보강기·프롬프트 세 곳에서 참조된다.
- `tests/ReSet.Core.Tests/ControlStepErrorCodesTests.cs`
- `tests/ReSet.Core.Tests/SplitProcedureObligationTests.cs`

**수정**

- `src/ReSet.Core/Services/PlanStructureEnricher.cs` — `MergeCodes`가 레거시 없는 단계에 발급한다.
- `src/ReSet.Core/Services/MechanicalValidator.cs` — 대역 검사 추가, 분할 SP 코드·테이블 면제, 문서 단위 검사 메서드 추가.
- `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — 귀속 재료 배선, 문서 단위 검사 호출.
- `src/ReSet.Core/Services/AiService.cs` — 규칙 6-1 보강, Critic 기준 한 줄.
- `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`
- `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`
- `tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs`

---

### Task 1: 예약 대역 상수와 블록 유도

**Files:**
- Create: `src/ReSet.Core/Services/ControlStepErrorCodes.cs`
- Test: `tests/ReSet.Core.Tests/ControlStepErrorCodesTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public static class ControlStepErrorCodes`
  - `public const int BlockSize = 10;`
  - `public static int? BlockStart(string? stepCode)` — `S16` → `-9160`, 형태가 아니면 `null`
  - `public static bool IsInBlock(string? stepCode, int code)` — 그 단계의 블록 안인가
  - `public static bool IsReserved(int code)` — 대역(`<= -9000`) 소속인가
  - `public const string PromptClause` — 프롬프트에 실을 문구

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ControlStepErrorCodesTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ControlStepErrorCodesTests
    {
        [Theory]
        [InlineData("S01", -9010)]
        [InlineData("S16", -9160)]
        [InlineData("S40", -9400)]
        [InlineData("s07", -9070)]
        public void BlockStart_ShouldDeriveFromTheStepNumber(string stepCode, int expected)
        {
            // 값을 보면 어느 단계에서 죽었는지 읽혀야 한다. 모델이 B160/B161로
            // 표현하려던 것과 같은 구조이고, 이쪽은 T-SQL INT에 그대로 들어간다.
            Assert.Equal(expected, ControlStepErrorCodes.BlockStart(stepCode));
        }

        [Theory]
        [InlineData("BOOTSTRAP")]
        [InlineData("S")]
        [InlineData("")]
        [InlineData(null)]
        public void BlockStart_WithoutAStepNumber_ShouldReturnNull(string? stepCode)
        {
            // 번호를 못 읽으면 발급하지 않는다. 임의의 값을 지어내면 그것이 곧
            // 이 설계가 없애려는 지어낸 어휘다.
            Assert.Null(ControlStepErrorCodes.BlockStart(stepCode));
        }

        [Fact]
        public void IsInBlock_ShouldAcceptTheWholeBlockAndRejectItsNeighbours()
        {
            Assert.True(ControlStepErrorCodes.IsInBlock("S16", -9160));
            Assert.True(ControlStepErrorCodes.IsInBlock("S16", -9169));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9159));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9170));
            Assert.False(ControlStepErrorCodes.IsInBlock("S16", -9));
        }

        [Fact]
        public void IsReserved_ShouldNotClaimAnyCodeTheLegacyCorpusUses()
        {
            // 코퍼스 전수 실측: 레거시 반환 코드는 -1 ~ -201이다. 대역이 그것을
            // 삼키면 원본 코드가 제어 코드로 오인된다.
            Assert.False(ControlStepErrorCodes.IsReserved(-201));
            Assert.False(ControlStepErrorCodes.IsReserved(-1));
            Assert.True(ControlStepErrorCodes.IsReserved(-9010));
            Assert.True(ControlStepErrorCodes.IsReserved(-9400));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ControlStepErrorCodesTests"`
Expected: FAIL — `ControlStepErrorCodes` 형식을 찾을 수 없다는 컴파일 오류.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/ControlStepErrorCodes.cs`:

```csharp
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 레거시 출신이 없는 단계가 쓰는 오류코드 대역.
    ///
    /// [왜 필요한가]
    /// 규칙 6-1은 "각 DML 앞에서 원본 오류코드로 상태 변수를 갱신하라"고 말하는데,
    /// 원본이 없는 단계에는 지킬 대상이 없다. 규약이 없으니 모델이 자기 체계를
    /// 지어냈다 - 실측(POQSettleBatch1)에서 목차가 B100·B101·B110·B120·B121·
    /// B160·B161을 발급했고 계획서에 54회 등장했다. 등장 검사는 그것들이 본문에
    /// 있는지 확인하고 통과시켰으므로, 검사가 지어낸 어휘를 인증하고 있었다.
    /// 그 어휘가 SQL로 새어 `DECLARE @v_currentStepId INT = B161`이 4회 나왔는데,
    /// B161은 해석되지 않는 식별자라 컴파일되지 않는다.
    ///
    /// [왜 이 모양인가]
    /// 모델의 `B&lt;단계번호&gt;&lt;일련&gt;`은 구조적으로 합리적이었다 - 값만 보고 어느
    /// 단계에서 죽었는지 알 수 있다. 규약도 같은 구조로 주되 T-SQL INT에 들어가는
    /// 값으로 만든다. 코퍼스 전수에서 레거시 반환 코드는 -1 ~ -201이므로 대역을
    /// 그 아래로 충분히 띄운다.
    /// </summary>
    public static class ControlStepErrorCodes
    {
        /// <summary>한 단계에 주어지는 코드 개수. 실측 최대는 단계당 2개였다.</summary>
        public const int BlockSize = 10;

        /// <summary>대역의 시작. 이 값 이하가 예약이다.</summary>
        private const int ReservedCeiling = -9000;

        private static readonly Regex StepNumberRegex =
            new(@"^\s*S(?<n>\d{1,3})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 그 단계의 블록 시작 코드. 단계 코드가 <c>S&lt;숫자&gt;</c>가 아니면 null -
        /// 번호를 못 읽으면 발급하지 않는다.
        /// </summary>
        public static int? BlockStart(string? stepCode)
        {
            if (string.IsNullOrWhiteSpace(stepCode))
            {
                return null;
            }

            var match = StepNumberRegex.Match(stepCode);
            if (!match.Success)
            {
                return null;
            }

            var n = int.Parse(match.Groups["n"].Value);
            return ReservedCeiling - (n * BlockSize);
        }

        /// <summary>그 코드가 이 단계의 블록 안인가.</summary>
        public static bool IsInBlock(string? stepCode, int code)
        {
            var start = BlockStart(stepCode);
            if (start == null)
            {
                return false;
            }

            return code <= start.Value && code > start.Value - BlockSize;
        }

        /// <summary>예약 대역에 속하는 값인가. 레거시 코드와 겹치는지 볼 때 쓴다.</summary>
        public static bool IsReserved(int code) => code <= ReservedCeiling;

        /// <summary>프롬프트에 싣는 문구. 규칙 6-1과 제어 계약 표가 함께 쓴다.</summary>
        public const string PromptClause =
            "[Control Step Error Codes] A step with NO legacy origin has no original error code to preserve, " +
            "so it MUST NOT invent one. Each such step owns a reserved block of 10 negative integers derived " +
            "from its step code: block start = -9000 - (N * 10), where N is the number in `S<N>`. S01 owns " +
            "-9010..-9019, S16 owns -9160..-9169. The block start (-9160 for S16) is that step's GENERAL " +
            "failure code and MUST appear in the section; use block start minus 1, 2, ... only to distinguish " +
            "further failure points within the same step. NEVER write a non-numeric code such as `B161` - " +
            "`DECLARE @v_currentStepId INT = B161` does not compile, because B161 is an unresolved identifier. " +
            "Steps that DO replace a legacy procedure keep that procedure's exact original codes and MUST NOT " +
            "use this reserved band.";
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ControlStepErrorCodesTests"`
Expected: PASS — 13개 통과.

- [ ] **Step 5: red-green을 확인한다**

`ControlStepErrorCodes.cs`에서 `return code <= start.Value && code > start.Value - BlockSize;`를 `return true;`로 바꾸고 위 명령을 다시 돌린다.
Expected: FAIL — `IsInBlock_ShouldAcceptTheWholeBlockAndRejectItsNeighbours`가 실패한다.
확인 후 되돌리고 다시 돌려 PASS를 본다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/ControlStepErrorCodes.cs tests/ReSet.Core.Tests/ControlStepErrorCodesTests.cs
git commit -m "feat: 제어 단계 오류코드 예약 대역을 정의한다"
```

---

### Task 2: 보강기가 제어 단계에 코드를 발급한다

**Files:**
- Modify: `src/ReSet.Core/Services/PlanStructureEnricher.cs` (`MergeCodes`, 171-205행 부근)
- Test: `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`

**Interfaces:**
- Consumes: `ControlStepErrorCodes.BlockStart(string?)`, `ControlStepErrorCodes.IsReserved(int)`
- Produces: 목차 JSON의 `ErrorCodes`가 레거시 없는 단계에서 `["-9160"]` 형태로 채워진다. Task 4·5의 검사가 이 값을 재료로 쓴다.

`MergeCodes`의 현재 앞부분은 이렇다(수정 대상):

```csharp
var declared = ReadStringArray(step, "ErrorCodes");
var procedures = ReadStringArray(step, "LegacyProcedures");

// 레거시 출신이 없으면 보존할 원본 코드가 애초에 없다. 비운 채 둔다.
if (procedures.Count == 0)
{
    return null;
}
```

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`의 클래스 안에 추가한다. 기존 `Structure` 상수의 `S00` 단계가 `LegacyProcedures: []`이므로 그것을 쓴다.

```csharp
[Fact]
public void Enrich_ShouldIssueAReservedCodeToAStepWithNoLegacyOrigin()
{
    // 실측(POQSettleBatch1): 목차가 B100·B101·B110·B120·B121·B160·B161을 스스로
    // 발급했고 계획서에 54회 등장했다. 규약이 없으니 모델이 지어낸 것이다.
    // 발급을 결정적으로 만들면 회차 간 값이 같아 산출물 diff도 안정된다.
    var result = PlanStructureEnricher.Enrich(
        Structure,
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());

    Assert.Contains("\"-9000\"", result.Markdown);
}

[Fact]
public void Enrich_ShouldReplaceAFabricatedCodeRatherThanUnionWithIt()
{
    // 합집합하면 지어낸 어휘가 살아남아 등장 검사가 계속 그것을 인증한다.
    var structure = Structure.Replace("\"ErrorCodes\": [],", "\"ErrorCodes\": [\"B100\"],");

    var result = PlanStructureEnricher.Enrich(
        structure,
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());

    Assert.DoesNotContain("B100", result.Markdown);
}

[Fact]
public void Enrich_ShouldNotIssueAReservedCodeToAStepThatReplacesALegacyProcedure()
{
    // 레거시 출신이 있으면 보존할 원본 코드가 있다. 예약 코드를 섞으면 원본
    // 코드와 제어 코드가 한 목록에 들어가 대조 기준이 흐려진다.
    var result = PlanStructureEnricher.Enrich(
        Structure,
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_Util_PG_Client_CMRate_Ins"] = new[] { "-9" }
        },
        new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());

    Assert.DoesNotContain("\"-9010\"", result.Markdown);
}

[Fact]
public void Enrich_ShouldSkipIssuingWhenALegacyCodeFallsInTheReservedBand()
{
    // 다른 코퍼스가 -9010을 쓰는 SP를 가져오면 조용히 겹치는 것이 최악이다.
    // 겹칠 여지가 보이면 발급을 포기한다 - 덜 하는 쪽이 안전하다.
    var result = PlanStructureEnricher.Enrich(
        Structure,
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_Util_PG_Client_CMRate_Ins"] = new[] { "-9010" }
        },
        new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());

    Assert.DoesNotContain("\"-9000\"", result.Markdown);
}
```

주의: `Structure` 상수의 `S00`은 블록 시작이 `-9000 - 0*10 = -9000`이다. 첫 테스트가 `"-9000"`을 기대하는 이유다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests"`
Expected: FAIL — 새 테스트 3건이 실패한다(`ShouldNotIssueAReservedCodeToAStepThatReplacesALegacyProcedure`는 현재도 통과한다).

- [ ] **Step 3: 최소 구현을 쓴다**

`MergeCodes`의 앞부분을 이렇게 바꾼다:

```csharp
var declared = ReadStringArray(step, "ErrorCodes");
var procedures = ReadStringArray(step, "LegacyProcedures");

// 레거시 출신이 없으면 보존할 원본 코드가 없다. 그렇다고 비워 두면 모델이
// 자기 체계를 지어낸다 - 실측(POQSettleBatch1)에서 목차가 B100·B110·B160
// 같은 코드를 발급했고 계획서에 54회 등장했으며, 그중 하나가
// `DECLARE @v_currentStepId INT = B161`로 새어 컴파일되지 않는 SQL이 됐다.
// 예약 대역에서 결정적으로 발급한다.
if (procedures.Count == 0)
{
    var blockStart = ControlStepErrorCodes.BlockStart(ReadString(step, "Code"));
    if (blockStart == null)
    {
        return null;
    }

    // 레거시 코드가 예약 대역에 걸리면 발급하지 않는다. 조용히 겹치는 것이
    // 지어낸 어휘보다 나쁘다 - 원본 코드가 제어 코드로 오인된다.
    if (AnyLegacyCodeIsReserved(codesByProcedure))
    {
        Log.Warning(
            "레거시 오류코드가 제어 단계 예약 대역과 겹쳐 예약 코드를 발급하지 않습니다 - 단계: {Code}",
            ReadString(step, "Code"));
        return null;
    }

    var issued = blockStart.Value.ToString(CultureInfo.InvariantCulture);

    // 합집합이 아니라 교체다. 모델이 지어낸 코드를 남기면 등장 검사가 계속
    // 그것을 인증한다.
    return declared.Count == 1 && declared[0] == issued
        ? null
        : new[] { issued };
}
```

같은 파일에 헬퍼를 더한다:

```csharp
/// <summary>
/// 명세서에서 뽑은 레거시 코드 중 예약 대역에 걸리는 것이 있는가.
/// 있으면 제어 단계 발급을 포기한다 - 겹친 코드는 어느 쪽 뜻인지 알 수 없다.
/// </summary>
private static bool AnyLegacyCodeIsReserved(
    IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
{
    foreach (var codes in codesByProcedure.Values)
    {
        foreach (var code in codes)
        {
            if (int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && ControlStepErrorCodes.IsReserved(value))
            {
                return true;
            }
        }
    }

    return false;
}
```

`ReadString`이 이 파일에 없으면 `ReadStringArray` 옆에 더한다:

```csharp
private static string ReadString(JsonObject step, string propertyName) =>
    step.TryGetPropertyValue(propertyName, out var node) && node is JsonValue value &&
    value.TryGetValue<string>(out var text)
        ? text
        : string.Empty;
```

파일 상단에 `using System.Globalization;`를 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests"`
Expected: PASS.

- [ ] **Step 5: red-green을 확인한다**

`return new[] { issued };`를 `return null;`로 바꾸고 다시 돌린다.
Expected: FAIL — 발급 테스트 2건이 실패한다. 확인 후 되돌린다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/PlanStructureEnricher.cs tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs
git commit -m "fix: 제어 단계의 오류코드를 예약 대역에서 결정적으로 발급한다"
```

---

### Task 3: 제어 단계가 블록 밖 코드를 돌려주면 잡는다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ValidateBatchStep` 검사 목록, 371행 부근에 호출 추가)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `ControlStepErrorCodes.IsInBlock(string?, int)`, `ControlStepErrorCodes.BlockStart(string?)`
- Produces: `result.Errors`에 `"{Code} 섹션이 예약 블록 밖의 제어 코드 '{값}'을 돌려줍니다..."` 형태의 발화

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`에 추가한다. 기존 테스트의 헬퍼(단계 만들기·검증 호출)를 그대로 쓴다.

```csharp
[Fact]
public void ValidateBatchStep_ShouldRejectANonNumericControlCode()
{
    // 실측(reset-20260824.log, 4회): `DECLARE @v_currentStepId INT = B161`.
    // B161은 해석되지 않는 식별자라 이 SQL은 컴파일되지 않는다. 기존
    // CheckStepIdInitialValue는 DECLARE 정규식이 `-?\d+`만 읽어 이것을 놓친다.
    var step = new BatchStepPlan(
        "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9160" }, false, new string[0]);

    var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = B161;
SELECT 1 FROM dbo.TSettleMst;
SET @po_intRetVal = @v_currentStepId;
```
-9160
";

    var result = Validate(markdown, step);

    Assert.Contains(result.Errors, e => e.Contains("B161"));
}

[Fact]
public void ValidateBatchStep_ShouldRejectAControlCodeOutsideTheStepsBlock()
{
    // 대역만 맞고 블록이 틀리면 반환값으로 단계를 특정할 수 없다.
    var step = new BatchStepPlan(
        "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9160" }, false, new string[0]);

    var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -9010;
SET @po_intRetVal = @v_currentStepId;
```
";

    var result = Validate(markdown, step);

    Assert.Contains(result.Errors, e => e.Contains("-9010"));
}

[Fact]
public void ValidateBatchStep_ShouldAcceptCodesInsideTheStepsBlock()
{
    var step = new BatchStepPlan(
        "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9160" }, false, new string[0]);

    var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -9161;
SET @po_intRetVal = @v_currentStepId;
```
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("-9161"));
}

[Fact]
public void ValidateBatchStep_ShouldNotApplyTheBandRuleToAStepWithALegacyOrigin()
{
    // 레거시 출신이 있는 단계의 -9는 원본 코드다. 대역 검사를 적용하면
    // 정상 단계가 전부 걸린다.
    var step = new BatchStepPlan(
        "S05", "원장 생성", new[] { "dbo.UP_UTIL_SETTLE_INS" },
        new[] { "dbo.TSettleMst" }, new[] { "-9" }, false, new string[0]);

    var markdown = @"### S05 원장 생성

```sql
DECLARE @v_currentStepId INT = 0;
SET @v_currentStepId = -9;
SELECT 1 FROM dbo.TSettleMst;
SET @po_intRetVal = @v_currentStepId;
```
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
}
```

기존 파일에 `Validate` 헬퍼가 없으면 이것을 클래스 상단에 더한다:

```csharp
private static StepValidationResult Validate(string markdown, BatchStepPlan step) =>
    new MechanicalValidator().ValidateBatchStep(
        markdown, step, new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>());
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: FAIL — 앞 두 건이 실패한다(뒤 두 건은 현재도 통과).

- [ ] **Step 3: 최소 구현을 쓴다**

`MechanicalValidator.cs`의 검사 호출 목록(371행 부근, `CheckStepIdInitialValue` 옆)에 한 줄을 더한다:

```csharp
SafeCheck(() => CheckControlStepErrorCodeBand(stepMarkdown, step, result));
```

같은 파일에 검사를 더한다:

```csharp
/// <summary>
/// 레거시 출신이 없는 단계는 자기 예약 블록 안의 코드만 돌려줘야 한다.
///
/// 실측(POQSettleBatch1): 규약이 없던 동안 목차가 B100·B110·B160 같은 코드를
/// 스스로 발급했고, 등장 검사는 그것들이 본문에 있는지 확인하고 통과시켰다 -
/// 검사가 지어낸 어휘를 인증한 것이다. 그중 하나가 SQL로 새어
/// `DECLARE @v_currentStepId INT = B161`이 4회 나왔는데 컴파일되지 않는다.
///
/// 두 가지를 본다: 상태 변수에 대입되는 비수치 토큰(B161)과, 수치지만 이 단계의
/// 블록 밖인 값. 후자를 보는 이유는 반환값만으로 단계를 특정할 수 있어야 하기
/// 때문이다.
/// </summary>
private static void CheckControlStepErrorCodeBand(
    string stepMarkdown, BatchStepPlan step, StepValidationResult result)
{
    // 레거시 출신이 있으면 원본 코드를 쓰는 것이 정상이다.
    if (step.LegacyProcedures.Count > 0) return;
    if (ControlStepErrorCodes.BlockStart(step.Code) == null) return;

    var reported = new HashSet<string>(StringComparer.Ordinal);

    foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
    {
        foreach (Match assignment in ControlCodeAssignmentPattern.Matches(cleaned))
        {
            var raw = assignment.Groups["value"].Value.Trim();
            if (!reported.Add(raw)) continue;

            if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                result.Errors.Add(
                    $"{step.Code} 섹션이 상태 변수에 숫자가 아닌 값 '{raw}'을 대입합니다. " +
                    $"T-SQL에서 해석되지 않는 식별자라 컴파일되지 않습니다 - " +
                    $"이 단계의 예약 블록({ControlStepErrorCodes.BlockStart(step.Code)}부터 10개)을 쓰십시오.");
                continue;
            }

            // 0은 "아직 실패 지점을 지나지 않았다"는 초기값이다. 규칙 6-1이 그렇게 쓴다.
            if (value == 0) continue;

            if (!ControlStepErrorCodes.IsInBlock(step.Code, value))
            {
                result.Errors.Add(
                    $"{step.Code} 섹션이 예약 블록 밖의 제어 코드 '{raw}'을 돌려줍니다. " +
                    $"레거시 출신이 없는 단계는 {ControlStepErrorCodes.BlockStart(step.Code)}부터 " +
                    $"{ControlStepErrorCodes.BlockSize}개의 블록만 씁니다.");
            }
        }
    }
}

// 상태 변수에 값을 대입하는 자리. DECLARE 초기값과 SET 갱신을 함께 본다.
// 값 자리를 `[^\s;,)]+`로 잡는 이유: 숫자만 잡으면 B161 같은 비수치 토큰이
// 매치되지 않아 그대로 통과한다 - 기존 CheckStepIdInitialValue가 놓친 이유다.
private static readonly Regex ControlCodeAssignmentPattern = new(
    @"(?:DECLARE\s+@\w*[Ss]tep\w*\s+INT\s*=|SET\s+@\w*[Ss]tep\w*\s*=)\s*(?<value>[^\s;,)]+)",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

파일 상단 `using`에 `System.Globalization`이 없으면 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: PASS.

- [ ] **Step 5: red-green을 확인한다**

`if (step.LegacyProcedures.Count > 0) return;` 다음 줄에 `return;`을 넣어 검사를 무력화하고 다시 돌린다.
Expected: FAIL — 앞 두 건이 실패한다. 확인 후 되돌린다.

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 제어 단계가 블록 밖 코드를 돌려주면 잡는다"
```

---

### Task 4: 귀속 재료를 검증기에 넘기고 분할 SP를 단계 검사에서 뺀다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ValidateBatchStep` 시그니처 271-279행, 코드·테이블 등장 루프 343-366행 부근)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`GenerateStepSectionWithFloorRetryAsync`의 `ValidateBatchStep` 호출 3238행 부근, `GenerateBySplitAsync` 시그니처)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `SpecReturnCodeExtractor.Extract(IEnumerable<(string FileName, string Content)>)` → `IReadOnlyDictionary<string, IReadOnlyList<string>>`; `SpecTargetTableExtractor.Extract(IEnumerable<SpDefinition>?)` → `IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>`
- Produces: `ValidateBatchStep`에 두 선택 인자가 붙는다 —
  `IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure = null`,
  `IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>? tablesByProcedure = null`.
  둘 다 `allSteps` **뒤**에 붙인다(기존 위치 인자를 밀지 않는다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
[Fact]
public void ValidateBatchStep_ShouldNotDemandACodeThatOnlyASplitProcedureOwes()
{
    // 실측(POQSettleProc4): UP_UTIL_SETTLE_EXCEPTION_PROC이 18개 단계에 나뉘어
    // 있다. 단계마다 그 SP의 코드 전량을 요구하면 18개 단계가 만족 불가능한
    // 요구를 받는다 - 문장 개수 대조가 이미 같은 이유로 면제받는다.
    var s10 = new BatchStepPlan(
        "S10", "예외 정책 1", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
        new[] { "-1", "-2" }, false, new string[0]);
    var s11 = new BatchStepPlan(
        "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
        new[] { "-1", "-2" }, false, new string[0]);

    var markdown = @"### S10 예외 정책 1

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, s10, new[] { "dbo.T1" },
        new Dictionary<string, SpecConditions>(),
        allSteps: new[] { s10, s11 },
        codesByProcedure: new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_X"] = new[] { "-1", "-2" }
        });

    Assert.DoesNotContain(result.Errors, e => e.Contains("'-2'"));
}

[Fact]
public void ValidateBatchStep_ShouldStillDemandACodeANonSplitProcedureOwes()
{
    // 같은 단계가 분할되지 않은 SP도 맡고 있고 그 SP가 그 코드를 가지면
    // 귀속이 확실하므로 계속 요구한다.
    var s10 = new BatchStepPlan(
        "S10", "예외 정책", new[] { "dbo.UP_X", "dbo.UP_Y" }, new[] { "dbo.T1" },
        new[] { "-1", "-2" }, false, new string[0]);
    var s11 = new BatchStepPlan(
        "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
        new[] { "-1" }, false, new string[0]);

    var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, s10, new[] { "dbo.T1" },
        new Dictionary<string, SpecConditions>(),
        allSteps: new[] { s10, s11 },
        codesByProcedure: new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_X"] = new[] { "-1" },
            ["UP_Y"] = new[] { "-2" }
        });

    Assert.Contains(result.Errors, e => e.Contains("'-2'"));
}

[Fact]
public void ValidateBatchStep_WithoutAttributionMaterial_ShouldKeepTheOldBehaviour()
{
    // 재료가 없다는 사실을 결함 없음으로 바꾸지 않는다 - allSteps == null일
    // 때의 하위 호환과 같은 태도다.
    var s10 = new BatchStepPlan(
        "S10", "예외 정책", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
        new[] { "-1", "-2" }, false, new string[0]);
    var s11 = new BatchStepPlan(
        "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
        new[] { "-1", "-2" }, false, new string[0]);

    var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, s10, new[] { "dbo.T1" },
        new Dictionary<string, SpecConditions>(),
        allSteps: new[] { s10, s11 });

    Assert.Contains(result.Errors, e => e.Contains("'-2'"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: FAIL — 첫 테스트가 컴파일되지 않는다(`codesByProcedure` 인자 없음).

- [ ] **Step 3: 최소 구현을 쓴다**

`ValidateBatchStep` 시그니처 끝에 두 인자를 더한다:

```csharp
IReadOnlyList<BatchStepPlan>? allSteps = null,
// [분할 SP 귀속] 코드·테이블이 어느 SP에서 왔는지는 step.ErrorCodes가 평평한
// 목록이라 알 수 없다. 프로시저 단위 재료를 함께 받아야 "분할된 SP에서만
// 유래한 것"을 가려낼 수 있다. 재료가 없으면(null) 종전 동작 그대로다.
IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure = null,
IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>? tablesByProcedure = null)
```

코드 등장 루프를 이렇게 바꾼다:

```csharp
foreach (var errorCode in step.ErrorCodes)
{
    if (string.IsNullOrWhiteSpace(errorCode))
    {
        continue;
    }

    if (IsOwedOnlyBySplitProcedures(errorCode.Trim(), step, allSteps, codesByProcedure))
    {
        continue;
    }

    if (!ContainsToken(stepMarkdown, errorCode.Trim()))
    {
        result.Errors.Add($"{step.Code} 섹션에 원본 오류코드 '{errorCode}'가 등장하지 않습니다.");
    }
}
```

테이블 등장 루프도 같은 모양으로 바꾼다:

```csharp
foreach (var table in step.TargetTables)
{
    var bareName = BareObjectName(table);
    if (bareName.Length == 0)
    {
        continue;
    }

    if (IsTableOwedOnlyBySplitProcedures(bareName, step, allSteps, tablesByProcedure))
    {
        continue;
    }

    if (!ContainsToken(stepMarkdown, bareName))
    {
        result.Errors.Add($"{step.Code} 섹션에 대상 테이블 '{table}'이 등장하지 않습니다.");
    }
}
```

두 헬퍼를 더한다:

```csharp
/// <summary>
/// 이 코드를 빚지는 SP가 전부 분할돼 있는가. 하나라도 분할되지 않은 SP가
/// 그 코드를 가지면 귀속이 확실하므로 이 단계에서 계속 요구한다.
///
/// 재료가 없으면 false - 종전대로 요구한다. 재료 없음을 결함 없음으로
/// 바꾸지 않는다.
/// </summary>
private static bool IsOwedOnlyBySplitProcedures(
    string code,
    BatchStepPlan step,
    IReadOnlyList<BatchStepPlan>? allSteps,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure)
{
    if (allSteps == null || codesByProcedure == null) return false;

    var owners = step.LegacyProcedures
        .Where(p => codesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(p), out var codes) &&
                    codes.Any(c => string.Equals(c.Trim(), code, StringComparison.Ordinal)))
        .ToList();

    if (owners.Count == 0) return false;

    return owners.All(p => IsLegacyProcedureSplitAcrossSteps(p, step.Code, allSteps));
}

/// <summary>
/// 테이블 축의 같은 판정. 오류코드와 달리 테이블은 정적 분석의 쓰기 집합에서
/// 온다(<see cref="SpecTargetTableExtractor"/>).
/// </summary>
private static bool IsTableOwedOnlyBySplitProcedures(
    string bareTable,
    BatchStepPlan step,
    IReadOnlyList<BatchStepPlan>? allSteps,
    IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>? tablesByProcedure)
{
    if (allSteps == null || tablesByProcedure == null) return false;

    var owners = step.LegacyProcedures
        .Where(p => tablesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(p), out var sets) &&
                    sets.WriteTables.Any(t => BareObjectName(t)
                        .Equals(bareTable, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    if (owners.Count == 0) return false;

    return owners.All(p => IsLegacyProcedureSplitAcrossSteps(p, step.Code, allSteps));
}
```

오케스트레이터에서 재료를 넘긴다. `GenerateStepSectionWithFloorRetryAsync`의 시그니처에 두 인자를 더하고(`IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure`, `IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure`), `ValidateBatchStep` 호출에 넘긴다:

```csharp
var stepResult = _validator.ValidateBatchStep(
    content, step, knownTableNames, conditionColumns,
    stepInterfaces: stepInterfaces,
    runRowOwnedTables: runRowOwnedTables,
    statementFactsByProcedure: statementFacts,
    allSteps: steps,
    codesByProcedure: codesByProcedure,
    tablesByProcedure: tablesByProcedure);
```

`GenerateBySplitAsync`도 두 인자를 받아 그대로 내려보낸다. 호출부(1898·2355행 부근) 두 곳은 `RunConsolidatedPipelineAsync`가 이미 가진 `specReturnCodes`·`specTargetTables`를 넘긴다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: PASS.

- [ ] **Step 5: red-green을 확인한다**

`IsOwedOnlyBySplitProcedures`의 `return owners.All(...)`를 `return false;`로 바꾸고 다시 돌린다.
Expected: FAIL — `ShouldNotDemandACodeThatOnlyASplitProcedureOwes`가 실패한다. 확인 후 되돌린다.

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/MechanicalValidator.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 분할된 SP의 코드·테이블을 단계마다 요구하지 않는다"
```

---

### Task 5: 분할 SP의 의무를 문서 단위로 검사한다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (새 public 메서드)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`GenerateBySplitAsync`의 병합 루프 직후, 3156행 부근)
- Test: `tests/ReSet.Core.Tests/SplitProcedureObligationTests.cs`

**Interfaces:**
- Consumes: Task 4가 배선한 `codesByProcedure`·`tablesByProcedure`
- Produces:
  `public IReadOnlyDictionary<string, StepDefect> ValidateSplitProcedureObligations(IReadOnlyDictionary<string, string> sectionsByStepCode, IReadOnlyList<BatchStepPlan> allSteps, IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure, IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>? tablesByProcedure)`
  — 반환은 단계 코드 → `StepDefect(StepDefectKind.QualityFloor, 사유)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SplitProcedureObligationTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// SP 하나가 여러 단계로 쪼개지면 어느 단계가 그 SP의 무엇을 맡는지 알 수 없다.
    /// 단계마다 전량을 요구하면 만족 불가능하고, 아무것도 요구하지 않으면 그 SP의
    /// 코드가 문서 어디에도 없어도 통과한다. 의무를 문서 단위로 올려 둘 다 피한다.
    /// </summary>
    public class SplitProcedureObligationTests
    {
        private static readonly BatchStepPlan S10 = new(
            "S10", "예외 1", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
            new[] { "-1", "-2" }, false, new string[0]);

        private static readonly BatchStepPlan S11 = new(
            "S11", "예외 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
            new[] { "-1", "-2" }, false, new string[0]);

        private static readonly Dictionary<string, IReadOnlyList<string>> Codes = new()
        {
            ["UP_X"] = new[] { "-1", "-2" }
        };

        [Fact]
        public void ShouldPassWhenEveryCodeAppearsInAtLeastOneSharingStep()
        {
            var sections = new Dictionary<string, string>
            {
                ["S10"] = "SET @v = -1;",
                ["S11"] = "SET @v = -2;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, Codes, null);

            Assert.Empty(defects);
        }

        [Fact]
        public void ShouldFlagEverySharingStepWhenACodeAppearsNowhere()
        {
            // 한 단계로 지목할 수 없다 - 어느 단계가 그 코드를 맡았어야 하는지
            // 알 방법이 없기 때문이다. 공유 단계 전부가 재생성 대상이 된다.
            var sections = new Dictionary<string, string>
            {
                ["S10"] = "SET @v = -1;",
                ["S11"] = "SET @v = -1;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, Codes, null);

            Assert.Equal(2, defects.Count);
            Assert.Contains("-2", defects["S10"].Reason);
            Assert.Contains("-2", defects["S11"].Reason);
            Assert.Equal(StepDefectKind.QualityFloor, defects["S10"].Kind);
        }

        [Fact]
        public void ShouldIgnoreAProcedureThatOnlyOneStepOwns()
        {
            // 분할되지 않은 SP는 단계 검사가 그대로 본다. 여기서 또 보면
            // 같은 결함이 두 번 발화된다.
            var solo = new BatchStepPlan(
                "S05", "원장", new[] { "dbo.UP_Y" }, new[] { "dbo.T1" },
                new[] { "-9" }, false, new string[0]);

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                new Dictionary<string, string> { ["S05"] = "본문에 코드가 없다" },
                new[] { solo },
                new Dictionary<string, IReadOnlyList<string>> { ["UP_Y"] = new[] { "-9" } },
                null);

            Assert.Empty(defects);
        }

        [Fact]
        public void WithoutMaterial_ShouldReportNothing()
        {
            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                new Dictionary<string, string> { ["S10"] = "", ["S11"] = "" },
                new[] { S10, S11 }, null, null);

            Assert.Empty(defects);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SplitProcedureObligationTests"`
Expected: FAIL — `ValidateSplitProcedureObligations` 메서드가 없다는 컴파일 오류.

- [ ] **Step 3: 최소 구현을 쓴다**

`MechanicalValidator.cs`에 더한다:

```csharp
/// <summary>
/// 분할된 SP의 코드·테이블이 그 SP를 나눠 맡은 단계들의 본문을 합친 것에
/// 등장하는지 본다.
///
/// [왜 문서 단위인가]
/// 단계마다 SP 전량을 요구하면 만족 불가능하다 - 실측(POQSettleProc4)에서
/// UP_UTIL_SETTLE_EXCEPTION_PROC이 18개 단계에 나뉘어 있다. 그렇다고 면제만
/// 하면 그 SP의 코드가 문서 어디에도 없어도 통과한다. 의무를 단계에서 문서로
/// 올리면 보장을 잃지 않고 불가능한 요구만 없앤다.
///
/// [대가]
/// 결함을 한 단계로 지목하지 못한다. 어느 단계가 그 코드를 맡았어야 하는지
/// 알 방법이 없으므로 공유 단계 전부를 지목한다. 문서 전체 재생성보다는 싸다.
/// </summary>
public IReadOnlyDictionary<string, StepDefect> ValidateSplitProcedureObligations(
    IReadOnlyDictionary<string, string> sectionsByStepCode,
    IReadOnlyList<BatchStepPlan> allSteps,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure,
    IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets>? tablesByProcedure)
{
    var defects = new Dictionary<string, StepDefect>(StringComparer.OrdinalIgnoreCase);
    if (sectionsByStepCode == null || allSteps == null) return defects;

    var procedures = allSteps
        .SelectMany(s => s.LegacyProcedures)
        .Select(BareObjectName)
        .Where(p => p.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var procedure in procedures)
    {
        var sharing = allSteps
            .Where(s => s.LegacyProcedures.Any(p =>
                BareObjectName(p).Equals(procedure, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 한 단계만 맡으면 단계 검사가 그대로 본다. 여기서 또 보면 두 번 발화된다.
        if (sharing.Count < 2) continue;

        var combined = string.Join("\n", sharing
            .Select(s => sectionsByStepCode.TryGetValue(s.Code, out var body) ? body : string.Empty));

        var missing = new List<string>();

        if (codesByProcedure != null &&
            codesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var codes))
        {
            missing.AddRange(codes
                .Select(c => c.Trim())
                .Where(c => c.Length > 0 && !ContainsToken(combined, c)));
        }

        if (tablesByProcedure != null &&
            tablesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var sets))
        {
            missing.AddRange(sets.WriteTables
                .Select(BareObjectName)
                .Where(t => t.Length > 0 && !ContainsToken(combined, t)));
        }

        if (missing.Count == 0) continue;

        var stepList = string.Join(", ", sharing.Select(s => s.Code));
        var reason =
            $"{procedure}를 나눠 맡은 단계({stepList})의 본문을 모두 합쳐도 " +
            $"{string.Join(", ", missing)}가 등장하지 않습니다.";

        foreach (var step in sharing)
        {
            defects[step.Code] = new StepDefect(StepDefectKind.QualityFloor, reason);
        }
    }

    return defects;
}
```

오케스트레이터의 `GenerateBySplitAsync` 병합 루프 직후(3156행 부근, `var ordered = steps` 앞)에 호출을 넣는다:

```csharp
// 분할된 SP의 의무는 단계가 아니라 문서가 진다. 단계 검사에서 뺀 것을
// 여기서 합쳐 본다 - 여기가 sections와 steps를 함께 가진 유일한 지점이다.
foreach (var (code, defect) in _validator.ValidateSplitProcedureObligations(
             sections, steps, codesByProcedure, tablesByProcedure))
{
    floorViolations[code] = defect;
}
```

단일 호출 폴백에는 넣지 않는다. 그 경로에는 `sections`가 없다. 폴백 분기(1943행 부근)에 로그 한 줄을 더한다:

```csharp
Log.Information(
    "단일 호출 경로라 분할 SP 문서 단위 검사를 실행하지 않았습니다 - Job: {JobName}", jobName);
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SplitProcedureObligationTests"`
Expected: PASS.

- [ ] **Step 5: red-green을 확인한다**

`if (sharing.Count < 2) continue;`를 `if (sharing.Count < 99) continue;`로 바꾸고 다시 돌린다.
Expected: FAIL — `ShouldFlagEverySharingStepWhenACodeAppearsNowhere`가 실패한다. 확인 후 되돌린다.

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/MechanicalValidator.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/SplitProcedureObligationTests.cs
git commit -m "feat: 분할 SP의 의무를 문서 단위로 검사한다"
```

---

### Task 6: 프롬프트 계약과 Critic 기준

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` (`ConsolidatedPlanRules`의 규칙 6-1, `ReviewConsolidatedPlanAsync`의 채점 기준 3)
- Test: `tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs`

**Interfaces:**
- Consumes: `ControlStepErrorCodes.PromptClause`
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs`에 추가한다(기존 `CaptureCriticPromptAsync` 헬퍼를 쓴다):

```csharp
[Fact]
public async Task Critic_ShouldCheckTheControlStepErrorCodeBand()
{
    // 생성 규칙이 요구하는데 아무도 채점하지 않으면 어긋나도 통과하고,
    // 자가 수정이 그 축에 영영 닿지 않는다 - 직전 회차에서 실측한 실패 방식이다.
    var prompt = await CaptureCriticPromptAsync();

    Assert.Contains("reserved block", prompt);
    Assert.Contains("does not compile", prompt);
}
```

`tests/ReSet.Core.Tests/FallbackPlanPromptParityTests.cs`에 추가한다(기존 `CaptureAsync` 헬퍼를 쓴다):

```csharp
[Fact]
public async Task Plan_ShouldCarryTheControlStepErrorCodeClause()
{
    var result = await CaptureAsync();

    Assert.Contains("[Control Step Error Codes]", result.SystemPrompt);
    Assert.Contains("-9010..-9019", result.SystemPrompt);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CriticCriteriaCoverageTests|FullyQualifiedName~FallbackPlanPromptParityTests"`
Expected: FAIL — 두 건이 실패한다.

- [ ] **Step 3: 최소 구현을 쓴다**

`ConsolidatedPlanRules`는 `@"..."` 축자 문자열이다. 규칙 4-1이 이미 `BatchObjectSchemaRule` 상수를 문자열 가운데서 이어 붙이는 선례를 갖고 있으므로(`4-1. " + BatchObjectSchemaRule + @"`) 같은 방식으로 규칙 6-1 다음 줄에 6-2를 넣는다.

현재:

```csharp
6-1. [Precise Error Tracking] If the original SP lacked ... NEVER use legacy `GOTO`-based error branching.
7. [Anti-Shortcut for Business Logic] ...
```

바꾼 뒤:

```csharp
6-1. [Precise Error Tracking] If the original SP lacked ... NEVER use legacy `GOTO`-based error branching.
6-2. " + ControlStepErrorCodes.PromptClause + @"
7. [Anti-Shortcut for Business Logic] ...
```

즉 `6-2. ` 뒤에서 축자 문자열을 닫고(`"`), 상수를 잇고, `+ @"`로 다시 연 뒤 줄바꿈하고 `7.`이 이어진다.

Critic 채점 기준 3(`ScoreInterface`)의 마지막 줄 뒤에 한 줄을 더한다:

```
   - Verify that a step with NO legacy origin returns only codes from its own reserved block (block start = -9000 - N*10 for `S<N>`, 10 codes), and never a non-numeric code such as `B161` - `DECLARE @v_currentStepId INT = B161` does not compile because B161 is an unresolved identifier. A step that replaces a legacy procedure must keep that procedure's original codes and must NOT use the reserved band.
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CriticCriteriaCoverageTests|FullyQualifiedName~FallbackPlanPromptParityTests"`
Expected: PASS.

- [ ] **Step 5: red-green을 확인한다**

`ControlStepErrorCodes.PromptClause`를 `""`로 바꾸고 다시 돌린다.
Expected: FAIL — `Plan_ShouldCarryTheControlStepErrorCodeClause`가 실패한다. 확인 후 되돌린다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs tests/ReSet.Core.Tests/FallbackPlanPromptParityTests.cs
git commit -m "feat: 제어 단계 오류코드 규약을 프롬프트와 Critic에 싣는다"
```

---

### Task 7: 회귀 실측과 주석 정리

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (321-330행 주석)
- Modify: `docs/known-defects.md`

**Interfaces:**
- Consumes: Task 1-6 전부
- Produces: 없음

- [ ] **Step 1: 낡은 주석을 고친다**

`MechanicalValidator.cs`의 이 주석은 더 이상 참이 아니다 — 이제 레거시 없는 단계도 예약 코드를 받아 대조된다:

```csharp
// 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다 - 대조 항목 0개가
// 정상이다. 이것을 결함으로 들면 계획이 새로 설계한 정상 단계에 배너가 붙어
// 배너의 변별력이 사라진다.
```

이렇게 바꾼다:

```csharp
// 레거시 출신이 없는 단계는 보존할 원본 코드가 없다. 그래서 이 결함은 레거시
// 출신이 있을 때만 든다. 다만 "대조 항목 0개"로 두지는 않는다 -
// PlanStructureEnricher가 예약 대역에서 코드를 발급하므로, 그 단계도 자기
// 블록 코드로 대조된다(ControlStepErrorCodes 참고).
```

- [ ] **Step 2: 기존 산출물에 대한 회귀를 실측한다**

워크트리에 코퍼스를 연결한 상태에서, `POQSettleBatch1`의 목차를 보강기에 통과시켜 발급 결과를 확인한다. 임시 테스트를 하나 만들어 돌린 뒤 지운다:

```csharp
[Fact]
public void TempProbe()
{
    var structure = File.ReadAllText(
        "/Users/payletter/git-root/ReSet/output/Jobs/POQSettleBatch1/raw/PlanStructure.md");
    var result = PlanStructureEnricher.Enrich(
        structure,
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, SpecTargetTableExtractor.StepTableSets>());
    _out.WriteLine("PROBE B1xx 잔존: " + Regex.Matches(result.Markdown, "B1[0-6][0-9]").Count);
    _out.WriteLine("PROBE 발급된 예약 코드: " +
        string.Join(", ", Regex.Matches(result.Markdown, "-90[0-9]0").Select(m => m.Value).Distinct()));
}
```

Expected: `B1xx 잔존: 0`, 발급된 예약 코드가 S01·S02·S03·S16에 대응하는 네 개.

같은 프로브에서 블록 크기가 충분한지도 본다 — 설계서가 미확정으로 남긴 항목이다.
보강 **전** 목차에서 레거시 없는 단계당 코드 개수를 세고, 최대가 `BlockSize`(10)를
넘지 않는지 확인한다:

```csharp
_out.WriteLine("PROBE 제어 단계당 코드 최대: " +
    Regex.Matches(structure, "\"ErrorCodes\": \\[(?<codes>[^\\]]*)\\]")
        .Select(m => m.Groups["codes"].Value.Split(',').Count(c => c.Trim().Length > 0))
        .DefaultIfEmpty(0).Max());
```

Expected: 2 (실측: S01·S03·S16이 각각 2개). 10을 넘으면 블록 크기를 다시 정해야
하므로 그 사실을 먼저 보고하고 멈춘다.

확인 후 임시 테스트를 지운다.

- [ ] **Step 3: known-defects에 회귀 사실을 기록한다**

`docs/known-defects.md`의 「배치 계획 생성」 절에 더한다:

```markdown
- **제어 단계 코드 교체로 기존 산출물 네 단계가 재생성된다** — `POQSettleBatch1`의
  S01·S02·S03·S16은 목차가 지어낸 `B1xx`를 쓴다. 예약 대역 발급이 그것을 교체하면
  본문에 예약 코드가 없으므로 그 네 단계가 결함으로 판정되어 다시 생성된다.
  오탐이 아니라 지금까지 인증되던 지어낸 어휘가 걸리는 것이다.
  출처: `2026-08-26-step-obligation-attribution-design.md` §7
```

- [ ] **Step 4: 전체 검증**

```bash
dotnet build
dotnet test
```

Expected: 빌드 경고 0·오류 0, 테스트 실패 0·건너뜀 0(코퍼스 연결 시).

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs docs/known-defects.md
git commit -m "docs: 제어 단계 코드 교체의 회귀 범위를 기록한다"
```

---

## 완료 기준

- `ControlStepErrorCodes.BlockStart("S16") == -9160`
- `POQSettleBatch1`의 목차를 보강기에 통과시키면 `B1xx`가 0회 남는다
- 레거시 없는 단계가 블록 밖 코드나 비수치 코드를 돌려주면 `Errors`에 발화가 남는다
- 분할 SP의 코드가 공유 단계 어디에도 없으면 공유 단계 전부가 `StepDefect`를 받는다
- 분할되지 않은 SP는 종전대로 단계마다 요구된다
- 신규 회귀 테스트 전부가 red-green으로 확인된다
- 빌드 경고 0·오류 0, 테스트 실패 0·건너뜀 0
