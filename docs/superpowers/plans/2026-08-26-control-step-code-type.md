# 제어 단계 코드의 타입 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 레거시 출신이 없는 제어 단계가 문자열 오류 코드를 쓰면 기계로 잡는다. 단, 자기 단계 식별자(`N'S01'`)는 정당하므로 통과시킨다.

**Architecture:** `MechanicalValidator.CheckControlStepErrorCodeBand`의 현재 게이트는 "같은 펜스에서 `INT`로 선언된 변수"만 본다. 문자열로 선언된 변수는 그 집합에 못 들어와 대입이 통째로 건너뛰어진다. 문자열 리터럴 경로를 따로 열되, 값이 그 단계 자신의 코드이면 침묵한다. 프롬프트(`ControlStepErrorCodes.PromptClause`)와 Critic 기준에 같은 규약을 싣고, 미룬 Minor 둘(M2·M4)을 함께 닫는다.

**Tech Stack:** C# .NET 10, xUnit, NSubstitute, Serilog.

**Spec:** `docs/superpowers/specs/2026-08-26-control-step-code-type-design.md`

## Global Constraints

- 코드 주석·문서·커밋 메시지는 **한국어**. 주석은 *무엇을*이 아니라 *왜*를 적고, 실측 근거가 있으면 수치와 함께 적는다.
- 작업은 전용 `git worktree`에서 한다. `main`에 직접 커밋하지 않는다(`AGENTS.md` 범주 8).
- API Key 등 자격증명을 소스나 `appsettings.json`에 넣지 않는다(`AGENTS.md` 범주 1).
- 각 태스크 종료 시 `dotnet build` 경고 0·오류 0, `dotnet test` 실패 0.
- 워커 워크트리에는 `output/` 코퍼스가 없어 코퍼스 테스트 약 15건이 **건너뜀**으로 표시된다. 환경 조건이지 결함이 아니다. **워커의 합격선은 실패 0**이고, 건너뜀 0은 코디네이터가 통합 시점에 확인한다. `ln`은 허용 명령이 아니므로 심링크를 만들려 하지 말고, 코퍼스가 필요하면 절대경로 `/Users/payletter/git-root/ReSet/output`을 직접 읽는다.
- **기존 침묵을 되돌리지 않는다.** `NULL`·변수 참조(`@LegacyCode`)·`CASE` 식·함수 호출(`ERROR_NUMBER()`)·정수 `0`·레거시 출신이 있는 단계는 계속 침묵해야 한다. 이것들은 오탐 두 라운드를 들여 걷어낸 것이다.
- **`"컴파일되지 않습니다"`라는 문구를 문자열 코드에 쓰지 않는다.** `N'B120'`은 컴파일된다. 거짓 진술이다.
- 신규 회귀 테스트는 red-green을 확인한다 — 변이를 주입해 실제로 실패하는 것을 보고 되돌린다.
- 커밋 메시지 끝에 다음 두 줄:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Lj91m24NBsVPZUH4BxNtxv
```

---

## File Structure

**수정**

- `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckControlStepErrorCodeBand`에 문자열 리터럴 경로 추가. `ControlCodeAssignmentPattern`은 **바꾸지 않는다**(이름 패턴을 넓히지 않는다는 것이 설계의 결정이다).
- `src/ReSet.Core/Services/ControlStepErrorCodes.cs` — `PromptClause`에 타입 규약·식별자 예외를 더하고 M2·M4를 닫는다.
- `src/ReSet.Core/Services/AiService.cs` — Critic 채점 기준 한 줄.
- `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`
- `tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs`, `tests/ReSet.Core.Tests/FallbackPlanPromptParityTests.cs`
- `docs/known-defects.md`

**생성하지 않는다.** 새 클래스가 필요 없다.

---

### Task 1: 상태 변수가 둘 이상인 단계가 있는지 먼저 센다

**Files:**
- 임시 프로브(만들었다가 **반드시 삭제**)

**Interfaces:**
- Consumes: 없음
- Produces: 측정 결과(보고서에만). 코드 산출물 없음.

설계서 「미확정 사항」이 이것을 구현 첫 단계에서 확인하라고 적었다. 한 단계가 상태 변수를 둘 이상 두면 "어느 것이 코드인가"를 가려야 하고, 그것은 설계가 §4에서 피하기로 한 이름 추정과 같은 문제가 된다.

- [ ] **Step 1: 프로브를 쓴다**

`tests/ReSet.Core.Tests/TempStateVarProbe.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    public class TempStateVarProbe
    {
        private readonly ITestOutputHelper _out;
        public TempStateVarProbe(ITestOutputHelper o) => _out = o;

        [Fact]
        public void Probe()
        {
            var root = "/Users/payletter/git-root/ReSet/output/Jobs";
            var decl = new Regex(@"DECLARE\s+(?<var>@\w*[Ss]tep\w*)\s+(?<type>\w+)", RegexOptions.IgnoreCase);
            var multi = new List<string>();
            var scanned = 0;

            foreach (var jobDir in Directory.EnumerateDirectories(root).OrderBy(d => d))
            {
                var structure = Path.Combine(jobDir, "raw", "PlanStructure.md");
                if (!File.Exists(structure)) continue;
                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(structure));
                if (steps == null) continue;

                foreach (var step in steps.Where(s => s.LegacyProcedures.Count == 0))
                {
                    var file = Path.Combine(jobDir, "agent", "steps", $"{step.Code}.md");
                    if (!File.Exists(file)) continue;
                    scanned++;

                    var names = decl.Matches(File.ReadAllText(file))
                        .Select(m => m.Groups["var"].Value.ToLowerInvariant())
                        .Distinct()
                        .ToList();

                    if (names.Count > 1)
                    {
                        multi.Add($"{Path.GetFileName(jobDir)}/{step.Code}: {string.Join(", ", names)}");
                    }
                }
            }

            _out.WriteLine($"VARPROBE 훑은 제어 단계: {scanned}개");
            _out.WriteLine($"VARPROBE 상태 변수 이름이 둘 이상인 단계: {multi.Count}개");
            foreach (var m in multi.Take(15)) _out.WriteLine("VARPROBE   " + m);
        }
    }
}
```

- [ ] **Step 2: 돌린다**

Run: `dotnet test --filter "FullyQualifiedName~TempStateVarProbe" --logger "console;verbosity=detailed" 2>&1 | grep VARPROBE`

- [ ] **Step 3: 판정한다**

`상태 변수 이름이 둘 이상인 단계`가 **0개이면** 설계 그대로 진행한다.

**0개가 아니면 멈추고 보고한다.** 목록과 함께 어떤 이름들이 함께 나오는지 적는다. 그 경우 "어느 변수가 코드인가"를 가려야 하는데 설계가 그 판단을 하지 않았으므로, 코디네이터가 설계를 보완해야 한다. 임의로 규칙을 만들지 마라.

- [ ] **Step 4: 프로브를 지운다**

```bash
rm tests/ReSet.Core.Tests/TempStateVarProbe.cs
git status --short
```

프로브는 커밋하지 않는다. 이 태스크는 커밋 산출물이 없다 — 측정과 판정이 산출물이다.

---

### Task 2: 문자열 코드를 잡되 자기 식별자는 통과시킨다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckControlStepErrorCodeBand`, 6152행 부근)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `ControlStepErrorCodes.BlockStart(string?)`, `ControlStepErrorCodes.BlockSize`
- Produces: `result.Errors`에 `"{Code} 섹션이 상태 변수에 문자열 코드 '{값}'을 대입합니다..."` 형태의 발화

**현재 코드의 두 자리**를 알아 두어야 한다.

첫째, 게이트(6176행 부근):

```csharp
else if (!intDeclaredVars.Contains(name))
{
    // 이 펜스에서 INT로 선언된 적이 없는 변수다 - 문자열 상태
    // 변수(N'B120' 등)일 수 있으므로 대상이 아니다.
    continue;
}
```

둘째, 문자열 리터럴 침묵(6190행 부근):

```csharp
// 문자열 리터럴(옵션 N 접두사)도 이 검사의 대상이 아니다 - 문자열
// 코드로 응답하는 제어 단계를 이 검사가 판정하지 않는다.
if (raw.Length > 0 &&
    (raw[0] == '\'' || (raw.Length > 1 && (raw[0] == 'N' || raw[0] == 'n') && raw[1] == '\'')))
{
    continue;
}
```

`ControlCodeAssignmentPattern`의 `DECLARE` 갈래가 `INT`만 매치하므로, `DECLARE @v_currentStepCode NVARCHAR(10) = N'B120'`은 **아예 매치되지 않는다.** 그래서 정규식도 한 번 손대야 한다 — 다만 **이름 패턴은 그대로 두고 타입 자리만 넓힌다.**

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`에 추가한다.

```csharp
[Fact]
public void ValidateBatchStep_ShouldRejectAStringErrorCodeInAControlStep()
{
    // 실측(POQSettleBatch1/S03, POQSettleProc13 등 17단계): 레거시 출신이 없는
    // 단계가 N'B120'·N'BATCH-LOCK-001' 같은 문자열 코드를 쓴다. B1xx를 INT 축에서
    // 몰아낸 뒤에도 문자열 자리에는 지어낸 어휘가 그대로 남아 있었다.
    var step = new BatchStepPlan(
        "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9030" }, false, new string[0]);

    var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

    var result = Validate(markdown, step);

    Assert.Contains(result.Errors, e => e.Contains("B120"));
    // 이 값은 컴파일된다. 거짓 진술을 하면 안 된다.
    Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일되지 않습니다"));
}

[Fact]
public void ValidateBatchStep_ShouldAcceptAStepIdentifierStringInAControlStep()
{
    // 실측 12단계: `DECLARE @v_stepCode nvarchar(10) = N'S01'`은 정당하다.
    // BatchControlContract가 batch.BatchStepJournal.StepCode를 nvarchar(10)으로
    // 규정하므로, 자기 신원을 저널에 쓰려면 문자열이어야 한다. 이것을 위반으로
    // 잡으면 제어 계약을 어기라고 요구하는 셈이다.
    var step = new BatchStepPlan(
        "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9010" }, false, new string[0]);

    var markdown = @"### S01 실행 등록

```sql
DECLARE @v_stepCode nvarchar(10) = N'S01';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("S01'") || e.Contains("문자열 코드"));
}

[Fact]
public void ValidateBatchStep_ShouldRejectAnotherStepsIdentifier()
{
    // 예외는 자기 코드에만 걸린다. 남의 신원을 자기 상태 변수에 담는 것은
    // 정당한 용법이 아니고, 예외를 "단계 코드 형태이면"으로 넓히면 N'S99' 같은
    // 없는 단계까지 통과한다.
    var step = new BatchStepPlan(
        "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9010" }, false, new string[0]);

    var markdown = @"### S01 실행 등록

```sql
DECLARE @v_stepCode nvarchar(10) = N'S02';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

    var result = Validate(markdown, step);

    Assert.Contains(result.Errors, e => e.Contains("S02"));
}

[Fact]
public void ValidateBatchStep_ShouldNotFlagATimestampOrFlagStateVariable()
{
    // 실측 2건: `@v_stepStartedAtUtc DATETIME2(3) = SYSUTCDATETIME()`와
    // `@v_isStepCompleted BIT = 0`. 이름은 상태 변수 패턴에 걸리지만 코드가 아니다.
    // 규칙이 "타입이 비INT면 위반"이었다면 이 둘이 걸렸을 것이다 - 값으로 가르는
    // 이유가 이것이다.
    var step = new BatchStepPlan(
        "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9030" }, false, new string[0]);

    var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_stepStartedAtUtc DATETIME2(3) = SYSUTCDATETIME();
DECLARE @v_isStepCompleted BIT = 0;
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
}

[Fact]
public void ValidateBatchStep_ShouldStillBeSilentOnNonLiteralValues()
{
    // 오탐 두 라운드를 들여 걷어낸 침묵이다. 이번 변경이 되살리면 회귀다.
    var step = new BatchStepPlan(
        "S22", "정리", new string[0], new[] { "dbo.TSettleMst" },
        new[] { "-9220" }, false, new string[0]);

    var markdown = @"### S22 정리

```sql
DECLARE @v_currentStepId INT = NULL;
SET @v_currentStepId = @LegacyCode;
SET @v_currentStepId = CASE WHEN 1 = 1 THEN -9221 ELSE -9222 END;
SET @v_currentStepId = ERROR_NUMBER();
SELECT 1 FROM dbo.TSettleMst;
```
-9220
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일되지 않습니다"));
    Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
}

[Fact]
public void ValidateBatchStep_ShouldNotApplyTheStringRuleToAStepWithALegacyOrigin()
{
    // 레거시 출신이 있으면 원본 규약을 따른다. 이 검사의 관할이 아니다.
    var step = new BatchStepPlan(
        "S05", "원장 생성", new[] { "dbo.UP_UTIL_SETTLE_INS" },
        new[] { "dbo.TSettleMst" }, new[] { "-9" }, false, new string[0]);

    var markdown = @"### S05 원장 생성

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9
";

    var result = Validate(markdown, step);

    Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
}
```

`Validate` 헬퍼가 이미 이 파일에 있다. 없으면 다음을 클래스 상단에 더한다:

```csharp
private static StepValidationResult Validate(string markdown, BatchStepPlan step) =>
    new MechanicalValidator().ValidateBatchStep(
        markdown, step, new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>());
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: FAIL — `ShouldRejectAStringErrorCodeInAControlStep`과 `ShouldRejectAnotherStepsIdentifier`가 실패한다(나머지 넷은 현재도 통과한다).

- [ ] **Step 3: 최소 구현을 쓴다**

먼저 정규식의 **타입 자리만** 넓힌다. 이름 패턴 `@\w*[Ss]tep\w*`는 그대로다.

```csharp
// 상태 변수에 값을 대입하는 자리. DECLARE 초기값과 SET 갱신을 함께 본다.
// 값 자리를 `[^\s;,)]+`로 잡는 이유: 숫자만 잡으면 B161 같은 비수치 토큰이
// 매치되지 않아 그대로 통과한다 - 기존 CheckStepIdInitialValue가 놓친 이유다.
// `declare` 그룹은 이 대입이 DECLARE에서 왔는지, `type` 그룹은 무슨 타입으로
// 선언됐는지 표시한다.
//
// 타입 자리를 INT에서 `\w+`로 넓힌 이유: `DECLARE @v_currentStepCode
// NVARCHAR(10) = N'B120'`이 INT만 볼 때는 아예 매치되지 않아, 문자열 코드가
// 검사에 도달조차 못 했다(실측 17단계). 이름 패턴은 넓히지 않는다 - 넓히면
// 메시지 변수 88건·ERROR_NUMBER() 계열 42건이 딸려 온다.
private static readonly Regex ControlCodeAssignmentPattern = new(
    @"(?:(?<declare>DECLARE)\s+@(?<name>\w*[Ss]tep\w*)\s+(?<type>\w+)\s*(?:\([^)]*\))?\s*=|SET\s+@(?<name>\w*[Ss]tep\w*)\s*=)\s*(?<value>[^\s;,)]+)",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

다음으로 게이트를 고친다. `intDeclaredVars`는 그대로 유지하되, **문자열로 선언된 변수도 추적**한다:

```csharp
// 펜스 단위로 새로 센다 - 다른 펜스의 DECLARE가 이 펜스의 SET을
// INT로 인증하면 안 된다.
var intDeclaredVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var trackedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (Match assignment in ControlCodeAssignmentPattern.Matches(cleaned))
{
    var name = assignment.Groups["name"].Value;
    var isDeclare = assignment.Groups["declare"].Success;

    if (isDeclare)
    {
        trackedVars.Add(name);
        if (IsIntegerType(assignment.Groups["type"].Value))
        {
            intDeclaredVars.Add(name);
        }
    }
    else if (!trackedVars.Contains(name))
    {
        // 이 펜스에서 선언된 적이 없는 변수다. 어떤 타입인지 알 수 없으므로
        // 판정하지 않는다 - 귀속할 수 없으면 침묵한다.
        continue;
    }
```

`IsIntegerType`을 같은 클래스에 더한다:

```csharp
/// <summary>
/// 정수 타입인가. 상태 코드로 쓸 수 있는 타입을 가린다.
///
/// 비정수라고 곧바로 위반은 아니다 - 실측에서 `@v_stepStartedAtUtc DATETIME2`와
/// `@v_isStepCompleted BIT`가 상태 변수 이름 패턴에 걸렸지만 코드가 아니었다.
/// 위반 여부는 타입이 아니라 대입되는 값이 정한다.
/// </summary>
private static bool IsIntegerType(string? type) =>
    type != null &&
    (type.Equals("int", StringComparison.OrdinalIgnoreCase) ||
     type.Equals("bigint", StringComparison.OrdinalIgnoreCase) ||
     type.Equals("smallint", StringComparison.OrdinalIgnoreCase) ||
     type.Equals("tinyint", StringComparison.OrdinalIgnoreCase));
```

마지막으로 문자열 리터럴 침묵을 조건부로 바꾼다. 기존 블록을 다음으로 교체한다.

**위치가 중요하다** — 이 블록은 `NULL` 검사 **뒤**, `@`/`CASE`/`(` 검사 **앞**에 와야 한다. 문자열 리터럴 안에 괄호가 들어갈 수 있어서(`N'B(1)'`) 뒤에 두면 함수 호출로 오인돼 조용히 넘어간다. 기존 블록이 이미 그 자리에 있으므로 자리를 옮기지 말고 내용만 바꾼다.

```csharp
// 문자열 리터럴(옵션 N 접두사)이면 값을 꺼내 판정한다.
//
// 자기 단계 코드는 정당하다 - BatchControlContract가
// batch.BatchStepJournal.StepCode를 nvarchar(10)으로 규정하므로, 단계가 자기
// 신원을 저널에 쓰려면 문자열이어야 한다(실측 12단계). 그 밖의 문자열은
// 지어낸 오류 어휘다(실측 17단계: N'B120'·N'BATCH-LOCK-001' 등).
//
// "컴파일되지 않습니다"라고 쓰지 않는다 - N'B120'은 컴파일된다. 거짓 진술은
// 이 저장소가 두 라운드를 들여 걷어낸 것이다.
var literal = TryReadStringLiteral(raw);
if (literal != null)
{
    if (string.Equals(literal, step.Code, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    result.Errors.Add(
        $"{step.Code} 섹션이 상태 변수에 문자열 코드 '{literal}'을 대입합니다. " +
        $"레거시 출신이 없는 단계는 예약 블록({blockStart}부터 " +
        $"{ControlStepErrorCodes.BlockSize}개)의 음수 정수를 씁니다 - " +
        $"자기 단계 코드('{step.Code}')를 저널에 쓰는 것만 문자열로 둡니다.");
    continue;
}
```

`TryReadStringLiteral`을 같은 클래스에 더한다:

```csharp
/// <summary>
/// `'...'` 또는 `N'...'` 형태이면 따옴표 안 내용을, 아니면 null.
/// 값은 원문에서 읽었으므로 리터럴 내용이 살아 있다.
/// </summary>
private static string? TryReadStringLiteral(string raw)
{
    if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
    {
        return raw[1..^1];
    }

    if (raw.Length >= 3 && (raw[0] == 'N' || raw[0] == 'n') &&
        raw[1] == '\'' && raw[^1] == '\'')
    {
        return raw[2..^1];
    }

    return null;
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: PASS.

전체도 돌린다: `dotnet test` → 실패 0.

- [ ] **Step 5: 변이 세 곳을 확인한다**

계획서가 변이 지점을 하나만 주면 다른 자리가 무방비로 남는다는 것이 직전 회차에서 네 번 드러났다. 세 곳을 확인한다.

1. **문자열 경로 무력화** — `if (literal != null)` 블록의 `result.Errors.Add(...)`를 지우고 `continue;`만 남긴다.
   Expected: `ShouldRejectAStringErrorCodeInAControlStep`·`ShouldRejectAnotherStepsIdentifier`가 실패한다.
2. **식별자 예외 제거** — `if (string.Equals(literal, step.Code, ...)) continue;`를 지운다.
   Expected: `ShouldAcceptAStepIdentifierStringInAControlStep`이 실패한다. **이 변이가 죽지 않으면 예외가 실제로 일하는지 검증되지 않은 것이다.**
3. **정규식 타입 자리 되돌리기** — `(?<type>\w+)\s*(?:\([^)]*\))?`를 `INT`로 되돌린다.
   Expected: 문자열 테스트들이 실패한다(매치 자체가 안 되므로).

각각 실측하고 되돌린 뒤 다시 통과를 확인한다. **살아남는 변이가 있으면 보고하고 테스트를 보강한 뒤 다시 확인한다.**

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "fix: 제어 단계의 문자열 오류 코드를 잡되 자기 식별자는 통과시킨다"
```

---

### Task 3: 프롬프트에 타입 규약을 싣고 미룬 Minor 둘을 닫는다

**Files:**
- Modify: `src/ReSet.Core/Services/ControlStepErrorCodes.cs` (`PromptClause`)
- Modify: `src/ReSet.Core/Services/AiService.cs` (Critic 채점 기준 3, `ScoreInterface` 절)
- Test: `tests/ReSet.Core.Tests/FallbackPlanPromptParityTests.cs`, `tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`FallbackPlanPromptParityTests`에 추가한다(기존 `CaptureAsync` 헬퍼를 쓴다):

```csharp
[Fact]
public async Task Plan_ShouldRequireAnIntegerStatusCodeForOriginlessSteps()
{
    var result = await CaptureAsync();

    Assert.Contains("integer status code", result.SystemPrompt);
}

[Fact]
public async Task Plan_ShouldKeepTheStepIdentifierAsAString()
{
    // 이 구분이 없으면 모델이 저널의 StepCode까지 숫자로 바꾸려 들 수 있고,
    // 그것은 BatchControlContract(StepCode nvarchar(10)) 위반이 된다.
    var result = await CaptureAsync();

    Assert.Contains("step identifier written to `batch.BatchStepJournal.StepCode` stays a string", result.SystemPrompt);
}

[Fact]
public async Task Plan_ShouldTellTheModelToInitializeTheStateVariableToZero()
{
    // M4: 블록 시작을 "일반 실패 코드"라고만 하면 모델이 그것을 초기값으로 삼고,
    // CheckStepIdInitialValue("어느 코드와도 겹치지 않는 값으로 초기화하라")와
    // 부딪힌다.
    var result = await CaptureAsync();

    Assert.Contains("initialize the state variable to `0`, not to the block start", result.SystemPrompt);
}
```

`CriticCriteriaCoverageTests`에 추가한다(기존 `CaptureCriticPromptAsync` 헬퍼를 쓴다):

```csharp
[Fact]
public async Task Critic_ShouldCheckTheStringCodeAxis()
{
    // 생성 규칙이 요구하는데 아무도 채점하지 않으면 어긋나도 통과하고,
    // 자가 수정이 그 축에 영영 닿지 않는다.
    var prompt = await CaptureCriticPromptAsync();

    Assert.Contains("string status code", prompt);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~FallbackPlanPromptParityTests|FullyQualifiedName~CriticCriteriaCoverageTests"`
Expected: FAIL — 새 테스트 4건이 실패한다.

- [ ] **Step 3: `PromptClause`를 다시 쓴다**

현재 문안을 다음으로 교체한다. **기존 요구를 하나도 약화시키지 않는다** — 예약 대역, 블록 유도, `B161` 반례, 레거시 단계 제외가 모두 유지돼야 한다.

```csharp
/// <summary>프롬프트에 싣는 문구. 규칙 6-1과 제어 계약 표가 함께 쓴다.</summary>
public const string PromptClause =
    "[Control Step Error Codes] A step with NO legacy origin has no original error code to preserve. " +
    "It MUST NOT invent one - instead it uses the reserved block this document assigns to it. " +
    "Each such step owns a block of 10 negative integers derived from its step code: " +
    "block start = -9000 - (N * 10), where N is the number in `S<N>`. S01 owns -9010..-9019, " +
    "S16 owns -9160..-9169. The block start (-9160 for S16) is that step's GENERAL failure code and " +
    "MUST appear in the section; use block start minus 1, 2, ... only to distinguish further failure " +
    "points within the same step. Initialize the state variable to `0`, not to the block start - " +
    "`0` means 'no failure point reached yet', and initializing to a real code makes the step report " +
    "a failure it never had. " +
    "The status code is an integer status code: declare the state variable as INT and assign only " +
    "integers from the block. NEVER assign a string code such as `N'B161'` or `N'BATCH-LOCK-001'` - " +
    "an invented string vocabulary is exactly what this rule exists to prevent, and a non-numeric " +
    "bare token such as `B161` does not even compile (`DECLARE @v INT = B161` has no such identifier). " +
    "One string stays a string: the step identifier written to `batch.BatchStepJournal.StepCode` stays a string " +
    "(`N'S01'`), because the control contract declares that column `nvarchar(10)`. That is identity, not a code. " +
    "Steps that DO replace a legacy procedure keep that procedure's exact original codes and MUST NOT " +
    "use this reserved band.";
```

M2(급하게 읽히는 문면)는 첫 두 문장을 갈라 닫는다 — "has no original error code to preserve." 다음에 "It MUST NOT invent one - instead it uses the reserved block…"이 오면서 금지와 대체가 한 호흡에 이어진다. M4는 "Initialize the state variable to `0`…" 문장이 닫는다.

- [ ] **Step 4: Critic 기준을 더한다**

`AiService.cs`의 채점 기준 3(`ScoreInterface`) 마지막 줄 뒤에 한 줄을 더한다:

```
   - Verify that a step with no legacy origin assigns only integers from its reserved block to its state variable, and never a string status code (`N'B120'`, `N'BATCH-LOCK-001'`). The one exception is the step identifier written to `batch.BatchStepJournal.StepCode`, which stays a string because the control contract declares that column `nvarchar(10)`.
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~FallbackPlanPromptParityTests|FullyQualifiedName~CriticCriteriaCoverageTests"`
Expected: PASS.

전체도 돌린다: `dotnet test` → 실패 0. **기존 프롬프트 테스트가 깨지면 문안을 바꾸다 기존 요구를 지운 것이다** — 지우지 말고 되살린다.

- [ ] **Step 6: 변이와 단언 토큰 고유성**

1. `PromptClause`에서 `"integer status code"`를 지운다 → `Plan_ShouldRequireAnIntegerStatusCodeForOriginlessSteps`가 실패해야 한다.
2. Critic 새 줄을 지운다 → `Critic_ShouldCheckTheStringCodeAxis`가 실패해야 한다.

각 단언 토큰이 프롬프트 안에서 고유한지 `grep -c`로 확인하고 결과를 보고한다. 겹치면 다른 테스트가 이 테스트를 가릴 수 있다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/ControlStepErrorCodes.cs src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/FallbackPlanPromptParityTests.cs tests/ReSet.Core.Tests/CriticCriteriaCoverageTests.cs
git commit -m "feat: 제어 단계 코드의 타입 규약을 프롬프트와 Critic에 싣는다"
```

---

### Task 4: 미결 셋을 결정됨으로 바꾸고 근거를 남긴다

**Files:**
- Modify: `docs/known-defects.md`

**Interfaces:**
- Consumes: Task 1~3
- Produces: 없음

- [ ] **Step 1: 문자열 코드 항목을 결정됨으로 바꾼다**

현재 "**문자열 코드로 응답하는 제어 단계의 규약이 아직 없다(침묵)**" 항목을 찾아, 다음을 반영해 다시 쓴다. **기존 서술을 지우지 말고 결정과 실측을 덧붙인다** — 무엇이 언제 왜 정해졌는지가 남아야 한다.

- 결정: **INT로 통일**한다. 상태 코드 값은 예약 블록의 음수 정수여야 하고, 자기 단계 식별자(`N'S01'`)만 문자열로 남는다.
- 실측(측정 조건 포함): 레거시 없는 제어 단계 131개 중 상태 변수를 비INT로 선언한 단계가 27개인데, 그 안에 문자열 **오류 코드** 17개 · **단계 식별자** 12개 · 타임스탬프·플래그 2개가 섞여 있었다. 앞서 기록된 "26개"는 이 갈래를 세지 않은 수였다.
- 17단계 목록: `POQSettleProc13`(S01,S02,S03,S16,S17,S18) · `POQSettleProc19`(S02,S03,S04,S17) · `POQSettleProc14`(S03,S16,S17) · `POQSettleProc18`(S01,S18) · `POQSettleBatch1`(S03) · `POQSettlePrco20`(S16)
- 왜 타입이 아니라 값으로 가르는가: 정당한 `@v_stepCode nvarchar(10) = N'S01'`과 위반인 `@v_currentStepCode NVARCHAR(10) = N'B120'`은 타입이 같다.

- [ ] **Step 2: 소급 범위 항목을 결정됨으로 바꾼다**

"**44개 단계 재생성 범위를 기존 산출물에 소급할지 미정**" 항목에 다음을 반영한다.

- 결정: **강제하지 않고 수렴**시킨다. 검사는 켜 둔 채로 두고, 해당 Job을 다시 돌릴 때 자연히 걸려 고쳐지게 한다. 유예 장치는 넣지 않는다 — 새 코드가 필요하고 "유예를 언제 끝낼 것인가"라는 새 미결을 만든다.
- 총계 갱신: 숫자 축 44 + 문자열 축 17 − 겹침 1(`POQSettlePrco20/S16`) = **60단계**(10개 Job).

- [ ] **Step 3: T4·T5 항목을 결정됨으로 바꾼다**

- 결정: **기록만 유지**한다. `MaxSteps` 인상과 `POQSettleProc5` 재생성은 별건으로 남긴다.

- [ ] **Step 4: 출력 파라미터 축을 열지 않은 근거를 남긴다**

새 항목으로 더한다.

- 제어 단계 131개 중 `CREATE PROCEDURE`가 있는 것은 **36개(27%)**뿐이다 — 대상 Job의 target language가 C#이라 대부분이 T-SQL 프로시저가 아니다.
- 그 36개 안에서도 어느 파라미터가 반환 코드인지를 이름으로 가려야 한다: `@po_intRetVal int`(24) · `@po_strErrMsg varchar`(21, 메시지) · `@po_isValid bit`(2, 플래그) · `@po_runId bigint`(1, 식별자) · `@po_intRetVal nvarchar`(1, 이름과 타입이 어긋남).
- 이름 추정은 이 저장소에서 두 번 오탐을 냈다. 값 축은 131개를 균일하게 덮는다.
- 남는 여지: `@po_intRetVal`인데 `nvarchar`인 1건은 이름 추정이 필요 없다. 1건이라 지금 검사를 만들 값어치가 없다고 판단했다 — 이 축을 열려는 다음 사람은 이 수치부터 다시 재라.

- [ ] **Step 5: 검증과 커밋**

```bash
dotnet build
dotnet test
git add docs/known-defects.md
git commit -m "docs: 제어 단계 코드 축의 미결 셋을 결정으로 바꾼다"
```

문서만 바꾸므로 테스트 수치가 달라지면 안 된다 — 달라지면 무언가 잘못 건드린 것이다.

---

## 회귀 실측 — 코디네이터가 통합 시점에 잰다

워커 워크트리에는 `output/`이 없어 손으로 만든 픽스처만 돈다. 직전 회차에 그 공백에서 Critical이 하나 나왔다. 통합 뒤 코퍼스를 연결하고 다음을 확인한다.

| 항목 | 기대 |
|---|---|
| 문자열 축 새 발화 | **17단계** (6개 Job) |
| 식별자·타임스탬프·플래그 | **0건** — 하나라도 걸리면 규칙이 틀렸다 |
| 숫자 축 "예약 블록 밖" | **161건 / 44단계 불변** |
| "컴파일되지 않습니다" 거짓 발화 | **0건 유지** |

수치가 다르면 **맞추려고 조건을 바꾸지 말고 보고한다.**

## 완료 기준

- `@v_currentStepCode NVARCHAR(10) = N'B120'`이 걸린다
- `@v_stepCode nvarchar(10) = N'S01'`(S01 단계)은 걸리지 않는다
- `S01` 단계의 `N'S02'`는 걸린다
- `DATETIME2`·`BIT` 상태 변수는 걸리지 않는다
- `NULL`·`@변수`·`CASE`·함수 호출의 기존 침묵이 유지된다
- 변이 세 곳(Task 2)과 두 곳(Task 3)이 전부 red로 확인된다
- 빌드 경고 0·오류 0, 테스트 실패 0·건너뜀 0(코퍼스 연결 시)
