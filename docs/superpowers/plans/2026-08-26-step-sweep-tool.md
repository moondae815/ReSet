# 스윕 도구화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 회차마다 새로 짓고 버려지던 코퍼스 스윕 하네스를 저장소 도구로 만들어, 단계 검사 A~E의 발화량과 캐시 17 선결 지표를 한 명령으로 재현 가능하게 측정한다.

**Architecture:** 스윕 로직은 `ReSet.Core`의 `StepSweepService`에 두고 디스크를 모르게 한다. `ReSet.Cli`의 `SweepCommand`가 `output/`을 읽어 메모리 구조로 넘기고 마크다운을 쓴다. 각 `(Job, Step)`마다 `MechanicalValidator.ValidateBatchStep`을 조건 (A)·(B) 두 번 부르고, (B) 사전은 원본 DDL → 표 렌더링 → `SpecStatementFactsExtractor.Extract`(진짜 리더) 왕복으로 만든다.

**Tech Stack:** .NET 10.0 · xUnit · Spectre.Console · `Microsoft.SqlServer.TransactSql.ScriptDom`

**Spec:** `docs/superpowers/specs/2026-08-26-step-sweep-tool-design.md`

## Global Constraints

- **`output/` 쓰기 금지. 읽기만 한다.** 보고서는 `docs/audit-reports/sweeps/`에 쓴다.
- **제품 코드의 동작을 바꾸지 않는다.** 이 계획이 수정하는 기존 파일은 `src/ReSet.Cli/Program.cs`(분기 추가)와 `src/ReSet.Cli/CliArgs.cs`(인자 추가) 둘뿐이다. `MechanicalValidator`·`SpecStatementFactsExtractor`·`DmlScopeExtractor`는 **읽기만** 한다.
- **코퍼스에 의존하는 테스트를 만들지 않는다.** 모든 테스트는 합성 입력으로 돈다. `Skip.If`를 쓰지 않는다.
- **`CurrentCacheFormatVersion`을 올리지 않는다.** 현재 16이며 이 계획의 범위 밖이다.
- 커밋 메시지는 저장소 관례를 따른다 — 한국어 제목 + 근거를 적은 본문 + `Co-Authored-By` / `Claude-Session` 트레일러.
- 각 태스크 끝에서 `dotnet build`(경고 0·오류 0)와 `dotnet test`(실패 0)가 통과해야 한다.

## 파일 구조

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Services/StepSweepModels.cs` (신규) | 입력·출력 레코드와 `SweepCheck`·`SweepCondition` 열거형 | 1 |
| `src/ReSet.Core/Services/StepSweepClassifier.cs` (신규) | 오류 메시지 문자열 → 검사 A~E 귀속, 검사 B·C 좌표 추출 | 1, 4 |
| `src/ReSet.Core/Services/StepSweepService.cs` (신규) | (B) 사전 생성 · 두 조건 실행 · 지표 계산 · 이름 규칙 창구 | 2, 3, 5, 7 |
| `src/ReSet.Core/Services/StepSweepReportWriter.cs` (신규) | `SweepReport` → 마크다운 | 6 |
| `src/ReSet.Cli/SweepCommand.cs` (신규) | `output/` 읽기 · 서비스 호출 · 파일 쓰기 | 7 |
| `src/ReSet.Cli/CliArgs.cs` (수정) | `--sweep` 인자 | 7 |
| `src/ReSet.Cli/Program.cs` (수정) | 배치 가드 앞 분기 | 7 |
| `tests/ReSet.Core.Tests/StepSweepClassifierTests.cs` (신규) | 태스크 1·4 테스트 | 1, 4 |
| `tests/ReSet.Core.Tests/StepSweepServiceTests.cs` (신규) | 태스크 2·3·5 테스트 | 2, 3, 5 |
| `tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs` (신규) | 태스크 6 테스트 | 6 |

## 배경 — 워커가 알아야 할 것

`ValidateBatchStep`은 **타입 있는 오류 목록을 내지 않는다.** `StepValidationResult`에는 `List<string> Errors`와 그 부분집합인 `List<string> PlanDefects`뿐이다(`MechanicalValidator.cs:7327-7346`). 그래서 검사 A~E 귀속은 **메시지 문자열 대조**로 할 수밖에 없다. 이것이 이 도구의 가장 깨지기 쉬운 자리다 — 검사의 문구가 바뀌면 분류가 조용히 무너진다. 태스크 1이 그 문구를 테스트로 못 박고 **미분류 버킷을 만들어 시끄럽게** 보고한다.

다섯 검사가 내는 메시지의 판별 조각(실측):

| 검사 | 메서드 | 판별 조각 |
|---|---|---|
| A | `CheckStatementCountAgainstSpec` | `개만 담고 있습니다. 명세서 DML 범위 표는` |
| B | `CheckAnchoredStatementFacts` | `문장에 명세서가 확정한` |
| C | `CheckAnchoredStatementExtras` | `문장이 명세서에 없는` |
| D | `CheckSpecLocalVariablesDeclared` | `을(를) 선언 없이 씁니다. 명세서 지역 변수 표는` |
| E | `CheckStepIdInitialValue` | `로 초기화하고 CATCH에서 그 값을` |

---

### Task 1: 모델과 오류 메시지 분류기

**Files:**
- Create: `src/ReSet.Core/Services/StepSweepModels.cs`
- Create: `src/ReSet.Core/Services/StepSweepClassifier.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepClassifierTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan`(`src/ReSet.Core/Services/BatchStepPlan.cs:26`) — `(string Code, string Name, IReadOnlyList<string> LegacyProcedures, IReadOnlyList<string> TargetTables, IReadOnlyList<string> ErrorCodes, bool Chunkable, IReadOnlyList<string> SchemaTables)`
- Produces: `SweepCheck`, `SweepCondition`, `SweepFinding`, `SweepJob`, `SweepInput`, `HarnessGaps`, `SweepIndicators`, `SweepReport`, `StepSweepClassifier.Classify(string) → SweepCheck`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepSweepClassifierTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepClassifierTests
    {
        [Theory]
        [InlineData("S01 섹션이 `TSettleMst`에 대한 UPDATE를 8개만 담고 있습니다. 명세서 DML 범위 표는 15개를 확정합니다.", SweepCheck.A)]
        [InlineData("S01 섹션의 UPDATE 13(갱신 13) 문장에 명세서가 확정한 최상위 술어 컬럼 YMD이(가) 없습니다.", SweepCheck.B)]
        [InlineData("S01 섹션의 UPDATE 2(갱신 2) 문장이 명세서에 없는 술어 컬럼 USESTATE을(를) 씁니다.", SweepCheck.C)]
        [InlineData("S01 섹션이 `@v_cnt`을(를) 선언 없이 씁니다. 명세서 지역 변수 표는 이 변수의 타입을 `INT`으로 확정합니다.", SweepCheck.D)]
        [InlineData("S01 섹션이 `@v_err`을(를) `-13`로 초기화하고 CATCH에서 그 값을 `@po_intRetVal`로 돌려줍니다.", SweepCheck.E)]
        public void ClassifiesEachCheckByItsMessage(string message, SweepCheck expected)
        {
            Assert.Equal(expected, StepSweepClassifier.Classify(message));
        }

        // 미분류를 조용히 A로 접으면 검사 문구가 바뀐 날 집계가 틀린 채로 초록이 된다.
        [Fact]
        public void UnknownMessageIsUnclassifiedNotSilentlyBucketed()
        {
            Assert.Equal(
                SweepCheck.Unclassified,
                StepSweepClassifier.Classify("S01 섹션이 '### ' 헤딩으로 시작하지 않습니다."));
        }

        [Fact]
        public void NullOrEmptyMessageIsUnclassified()
        {
            Assert.Equal(SweepCheck.Unclassified, StepSweepClassifier.Classify(null));
            Assert.Equal(SweepCheck.Unclassified, StepSweepClassifier.Classify("   "));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepClassifierTests"
```

Expected: 컴파일 실패 — `SweepCheck`·`StepSweepClassifier`가 없다.

- [ ] **Step 3: 모델을 만든다**

`src/ReSet.Core/Services/StepSweepModels.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>단계 검사 다섯 개. 미분류는 조용히 접지 않고 따로 센다.</summary>
    public enum SweepCheck { A, B, C, D, E, Unclassified }

    /// <summary>
    /// AsIs = 오늘 그대로(캐시 16, 「오류 코드」 표 없음).
    /// SimulatedCache17 = 원본 DDL에서 만든 코드→서수 사전을 주입한 상태.
    /// </summary>
    public enum SweepCondition { AsIs, SimulatedCache17 }

    /// <summary>발화 하나. Kind·Ordinal·Items는 검사 B·C에서만 채워진다.</summary>
    public sealed record SweepFinding(
        string JobName,
        string StepCode,
        SweepCheck Check,
        SweepCondition Condition,
        string Message)
    {
        public string? Kind { get; init; }
        public int? Ordinal { get; init; }
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Job 하나의 측정 재료. 전부 메모리에 올라온 값이다 - 서비스는 파일을 모른다.
    /// </summary>
    /// <param name="Specs">키 규약이 중요하다. FileName은 프로시저 이름
    /// ("dbo.UP_X")이지 파일 경로가 아니다 - SpecStatementFactsExtractor가
    /// MechanicalValidator.BareObjectName(FileName)으로 키를 만들므로
    /// "dbo.UP_X.md"를 넘기면 키가 "md"가 되어 조회가 전부 빗나간다.</param>
    public sealed record SweepJob(
        string JobName,
        IReadOnlyList<BatchStepPlan> Steps,
        IReadOnlyDictionary<string, string> StepMarkdownByCode,
        IReadOnlyList<(string FileName, string Content)> Specs,
        IReadOnlyDictionary<string, string> DdlByProcedure,
        IReadOnlyDictionary<string, string> DateParameterByProcedure);

    /// <param name="PlanParseFailedJobs">PlanStructure.md에서 단계 목록을 못 읽은 Job.</param>
    /// <param name="MissingStepFiles">목차가 선언했으나 agent/steps/에 실물이 없는 단계 수.</param>
    public sealed record SweepInput(
        IReadOnlyList<SweepJob> Jobs,
        IReadOnlyList<string> PlanParseFailedJobs,
        int MissingStepFiles);

    /// <summary>
    /// 대상 범위가 줄어든 것이 개선처럼 보이지 않게 매번 보고서에 싣는 값들.
    /// </summary>
    public sealed record HarnessGaps(
        IReadOnlyList<string> PlanParseFailedJobs,
        int MissingStepFiles,
        int MeasuredPairs,
        int MeasuredJobs,
        bool StepInterfacesWereNull,
        bool RunRowOwnedTablesWereNull,
        bool KnownTableNamesWereEmpty);

    /// <param name="MultiProcedureSteps">참조 원본 SP가 2개 이상인 단계 수.</param>
    /// <param name="StepsMissingSpecCodes">SP 표에는 있는데 단계 SQL에 없는 코드가 있는 단계 수.</param>
    /// <param name="StepsWithUnknownCodes">단계 SQL에는 있는데 SP 표에 없는 코드가 있는 단계 수.</param>
    public sealed record SweepIndicators(
        int MultiProcedureSteps,
        int StepsMissingSpecCodes,
        int StepsWithUnknownCodes);

    public sealed record SweepReport(
        IReadOnlyList<SweepFinding> Findings,
        SweepIndicators Indicators,
        HarnessGaps Gaps);
}
```

- [ ] **Step 4: 분류기를 만든다**

`src/ReSet.Core/Services/StepSweepClassifier.cs`:

```csharp
using System;

namespace ReSet.Core.Services
{
    /// <summary>
    /// ValidateBatchStep의 오류 문자열을 검사 A~E로 귀속시킨다.
    ///
    /// [왜 문자열 대조인가] StepValidationResult에는 타입 있는 오류 목록이 없다
    /// (MechanicalValidator.cs:7327 - List&lt;string&gt; Errors 하나뿐). 검사별 발화량을
    /// 재려면 메시지를 읽는 수밖에 없다.
    ///
    /// [그래서 미분류를 따로 센다] 검사의 문구가 바뀌면 이 대조가 무너지는데,
    /// 모르는 메시지를 아무 칸에나 접어 넣으면 집계가 틀린 채로 그럴듯해진다.
    /// Unclassified로 남겨 보고서에 개수가 찍히게 하고, 0이 아니면 사람이 본다.
    /// </summary>
    public static class StepSweepClassifier
    {
        // 판별 조각은 각 검사의 메시지 조립부에서 그대로 따왔다. 위치는
        // MechanicalValidator.cs의 CheckStatementCountAgainstSpec(:6046) ·
        // CheckAnchoredStatementFacts(:6249) · CheckAnchoredStatementExtras(:6458) ·
        // CheckSpecLocalVariablesDeclared(:6546) · CheckStepIdInitialValue(:5909).
        private const string MarkerA = "개만 담고 있습니다. 명세서 DML 범위 표는";
        private const string MarkerB = "문장에 명세서가 확정한";
        private const string MarkerC = "문장이 명세서에 없는";
        private const string MarkerD = "을(를) 선언 없이 씁니다. 명세서 지역 변수 표는";
        private const string MarkerE = "로 초기화하고 CATCH에서 그 값을";

        public static SweepCheck Classify(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return SweepCheck.Unclassified;

            if (message.Contains(MarkerA, StringComparison.Ordinal)) return SweepCheck.A;
            if (message.Contains(MarkerB, StringComparison.Ordinal)) return SweepCheck.B;
            if (message.Contains(MarkerC, StringComparison.Ordinal)) return SweepCheck.C;
            if (message.Contains(MarkerD, StringComparison.Ordinal)) return SweepCheck.D;
            if (message.Contains(MarkerE, StringComparison.Ordinal)) return SweepCheck.E;

            return SweepCheck.Unclassified;
        }
    }
}
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepClassifierTests"
```

Expected: PASS (7건).

- [ ] **Step 6: 전체 빌드와 테스트**

```bash
dotnet build && dotnet test
```

Expected: 경고 0 · 오류 0 · 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/StepSweepModels.cs src/ReSet.Core/Services/StepSweepClassifier.cs tests/ReSet.Core.Tests/StepSweepClassifierTests.cs
git commit
```

제목: `feat: 스윕 모델과 오류 메시지 분류기를 세운다`
본문에 담을 것: `StepValidationResult`에 타입 있는 오류가 없어 문자열 대조가 불가피하다는 사실, 미분류를 따로 세는 이유.

---

### Task 2: 조건 (B) 사전 — 진짜 계약을 왕복시킨다

**Files:**
- Create: `src/ReSet.Core/Services/StepSweepService.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepServiceTests.cs`

**Interfaces:**
- Consumes: `DmlScopeExtractor.ExtractErrorCodes(string? ddlText, string dateParameterName) → IReadOnlyList<ErrorCodeFact>` (`DmlScopeExtractor.cs:767`) · `ErrorCodeFact(string Operation, int StatementOrdinal, string Code, string Variable)` (:181) · `DmlScopeExtractor.ErrorCodeTableHeading` = `"### 오류 코드 (기계 확정 — 수정 금지)"` (:494) · `SpecStatementFactsExtractor.Extract(IReadOnlyList<(string FileName, string Content)>) → IReadOnlyDictionary<string, SpecStatementFacts>` (:142)
- Produces: `StepSweepService.BuildSimulatedErrorCodeMap(string? ddl, string dateParameterName) → IReadOnlyDictionary<string, (string Kind, int Ordinal)>`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepSweepServiceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepServiceTests
    {
        private const string DdlWithTwoCodes = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT
AS
BEGIN
    DECLARE @v_err INT = 0;

    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;

    UPDATE dbo.TSettleMiss SET UseState = 2 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -14;
END";

        [Fact]
        public void SimulatedMapPairsEachCodeWithItsStatement()
        {
            var map = StepSweepService.BuildSimulatedErrorCodeMap(DdlWithTwoCodes, "@pi_strYMD");

            Assert.Equal(("UPDATE", 1), map["-13"]);
            Assert.Equal(("UPDATE", 2), map["-14"]);
        }

        // 제품 규칙(SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299)과 같아야 한다 -
        // 같은 코드가 두 문장에 붙으면 귀속할 수 없으므로 덮어쓰지 않고 아예 뺀다.
        [Fact]
        public void DuplicateCodeIsDroppedNotOverwritten()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.UP_DUP @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TA SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
    UPDATE dbo.TB SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
END";

            Assert.False(
                StepSweepService.BuildSimulatedErrorCodeMap(ddl, "@pi_strYMD").ContainsKey("-13"));
        }

        [Fact]
        public void EmptyOrUnparsableDdlYieldsEmptyMap()
        {
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap(null, "@pi_strYMD"));
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap("NOT SQL (((", "@pi_strYMD"));
        }

        // 왕복이 진짜 리더를 지나는지 확인한다. 헤딩이 어긋나면 리더가 표를 못 찾아
        // 빈 사전을 돌려준다 - 조용히 틀린 사전을 쓰는 대신 눈에 띄는 0이 된다.
        [Fact]
        public void RenderedTableUsesTheHeadingTheRealReaderLooksFor()
        {
            var rendered = StepSweepService.RenderErrorCodeTable(
                DmlScopeExtractor.ExtractErrorCodes(DdlWithTwoCodes, "@pi_strYMD"));

            Assert.Contains(DmlScopeExtractor.ErrorCodeTableHeading, rendered);

            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", rendered) });
            Assert.Equal(2, facts["UP_TEST"].ErrorCodeToOrdinal.Count);

            var broken = rendered.Replace(DmlScopeExtractor.ErrorCodeTableHeading, "### 오류 코드");
            var brokenFacts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", broken) });
            Assert.Empty(brokenFacts["UP_TEST"].ErrorCodeToOrdinal);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: 컴파일 실패 — `StepSweepService`가 없다.

- [ ] **Step 3: 서비스의 (B) 사전 부분을 만든다**

`src/ReSet.Core/Services/StepSweepService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코퍼스 단계 지시서를 전수로 훑어 단계 검사 A~E의 발화량을 잰다.
    ///
    /// [왜 디스크를 모르는가] 로직이 CLI에 있으면 테스트가 코퍼스 의존 골든이 되고,
    /// 코퍼스가 없을 때 Skip으로 조용히 통과한다(CoverageMapGoldenTests가 그렇다).
    /// 측정을 재현 가능하게 만드는 것이 이 도구의 목적인데 그 도구의 회귀가 초록으로
    /// 숨으면 목적을 스스로 배반한다. 파일 읽기는 SweepCommand에만 있다.
    /// </summary>
    public static class StepSweepService
    {
        /// <summary>
        /// 원본 DDL에서 캐시 17 이후의 코드→서수 사전을 만든다.
        ///
        /// [왜 표로 렌더링해서 리더에 먹이는가] ExtractErrorCodes의 결과를 직접 사전으로
        /// 접으면 중복 코드 처리 규칙이 두 곳에 생긴다. 제품의 규칙
        /// (SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299 - 중복이면 덮어쓰지 않고
        /// 아예 빼고, dropped로 세 번째 등장도 막는다)과 조금만 달라도 실제 파이프라인이
        /// 결코 만들지 않을 사전으로 측정하게 된다. 읽는 쪽을 제품 코드 그대로 쓴다.
        /// </summary>
        public static IReadOnlyDictionary<string, (string Kind, int Ordinal)>
            BuildSimulatedErrorCodeMap(string? ddl, string dateParameterName)
        {
            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, dateParameterName);
            if (facts.Count == 0)
            {
                return new Dictionary<string, (string, int)>(StringComparer.Ordinal);
            }

            var synthesized = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)>
                {
                    ("sweep.synthetic", RenderErrorCodeTable(facts)),
                });

            return synthesized.TryGetValue("synthetic", out var parsed)
                ? parsed.ErrorCodeToOrdinal
                : new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        }

        /// <summary>
        /// ExtractErrorCodes의 결과를 명세서에 실리는 표 모양으로 되돌린다.
        ///
        /// 헤딩과 열 이름은 AiService가 프롬프트에 싣는 것과 같아야 한다 - 어긋나면
        /// ReadErrorCodeToOrdinal이 표를 못 찾아 빈 사전이 나온다. 그 실패는 조용하지
        /// 않다: 조건 (B)의 발화가 통째로 0이 되어 보고서에 드러난다.
        /// </summary>
        public static string RenderErrorCodeTable(IReadOnlyList<ErrorCodeFact> facts)
        {
            var builder = new StringBuilder();
            builder.AppendLine(DmlScopeExtractor.ErrorCodeTableHeading);
            builder.AppendLine();
            builder.AppendLine("| 문장 | 오류 코드 | 설정 대상 |");
            builder.AppendLine("| :--- | :--- | :--- |");

            foreach (var fact in facts)
            {
                builder.AppendLine(
                    $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Code} | {fact.Variable} |");
            }

            return builder.ToString();
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: PASS (4건).

`SimulatedMapPairsEachCodeWithItsStatement`가 실패하면 픽스처 DDL이 `ExtractErrorCodes`가 인식하는 모양이 아닌 것이다. `tests/ReSet.Core.Tests/DmlScopeExtractorErrorCodeTests.cs`의 픽스처를 그대로 참고해 맞춘다 — **테스트를 느슨하게 고치지 말고 픽스처를 고친다.**

- [ ] **Step 5: 전체 빌드와 테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSweepService.cs tests/ReSet.Core.Tests/StepSweepServiceTests.cs
git commit
```

제목: `feat: 조건 (B) 사전을 진짜 계약 왕복으로 만든다`

---

### Task 3: 두 조건으로 스윕을 돌린다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepService.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepServiceTests.cs`

**Interfaces:**
- Consumes: `MechanicalValidator.ValidateBatchStep(string? stepMarkdown, BatchStepPlan step, IReadOnlyCollection<string> knownTableNames, IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure, IReadOnlyList<StepInterface>? stepInterfaces = null, IReadOnlyCollection<string>? runRowOwnedTables = null, IReadOnlyDictionary<string, SpecStatementFacts>? statementFactsByProcedure = null, IReadOnlyList<BatchStepPlan>? allSteps = null) → StepValidationResult` (`MechanicalValidator.cs:271`) · `SpecConditionColumnExtractor.Extract(IEnumerable<(string FileName, string Content)>) → IReadOnlyDictionary<string, SpecConditions>` (:89)
- Produces: `StepSweepService.Sweep(SweepInput) → SweepReport`

**중요:** `MechanicalValidator`는 인스턴스 메서드다. 서비스가 `new MechanicalValidator(...)`를 어떻게 만드는지는 생성자를 직접 읽고 맞춘다(`MechanicalValidator.cs`에서 `public MechanicalValidator`를 찾는다). 생성자가 인자를 요구하면 스윕에 무해한 값(빈 컬렉션·null)을 넘기고, 그 선택을 주석으로 남긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다 — 이것이 이 계획의 핵심 미끼다**

`StepSweepServiceTests.cs`에 추가:

```csharp
        // 명세서: UPDATE 1은 TSettleMst를 YMD·PGNAME으로 필터한다고 확정한다.
        private const string SpecWithOneUpdateRow = @"
### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 | 술어 컬럼 | 조인 키 | GROUP BY | ORDER BY |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 1 | 10 | TSettleMst | YMD, PGNAME | — | — | — |
";

        // 단계 SQL: 코드 라벨(-13)은 있고 U-앵커는 없다. PGNAME 필터가 빠져 있다.
        private const string StepMarkdownMissingPgName = @"### S01. 정산 마스터 갱신

설명 문단이다.

```sql
SET @v_currentStepId = -13;
UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
```
";

        private const string DdlOneUpdateWithCode = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD AND PGNAME = 'X';
    IF @@ERROR <> 0 SET @v_err = -13;
END";

        private static SweepInput OneJobInput() => new(
            new List<SweepJob>
            {
                new(
                    "TestJob",
                    new List<BatchStepPlan>
                    {
                        new("S01", "정산 마스터 갱신",
                            new List<string> { "dbo.UP_TEST" },
                            new List<string> { "TSettleMst" },
                            new List<string> { "-13" },
                            false,
                            new List<string>()),
                    },
                    new Dictionary<string, string> { ["S01"] = StepMarkdownMissingPgName },
                    new List<(string, string)> { ("dbo.UP_TEST", SpecWithOneUpdateRow) },
                    new Dictionary<string, string> { ["dbo.UP_TEST"] = DdlOneUpdateWithCode },
                    new Dictionary<string, string> { ["dbo.UP_TEST"] = "@pi_strYMD" }),
            },
            new List<string>(),
            0);

        // [이 테스트가 이 계획에서 가장 중요하다]
        // 조건 (B) 주입이 통째로 죽어도 "(A)와 (B)가 같다"는 그럴듯한 결과로 통과한다.
        // 코드 앵커만 있고 U-앵커가 없는 단계에서 (A)는 침묵하고 (B)는 발화해야 한다.
        [Fact]
        public void ConditionBFiresWhereConditionAIsSilent()
        {
            var report = StepSweepService.Sweep(OneJobInput());

            var asIs = report.Findings
                .Where(f => f.Condition == SweepCondition.AsIs && f.Check == SweepCheck.B);
            var simulated = report.Findings
                .Where(f => f.Condition == SweepCondition.SimulatedCache17 && f.Check == SweepCheck.B);

            Assert.Empty(asIs);
            Assert.Single(simulated);
            Assert.Equal("TestJob", simulated.Single().JobName);
            Assert.Equal("S01", simulated.Single().StepCode);
        }

        [Fact]
        public void GapsRecordMeasuredPairsAndNullInputs()
        {
            var gaps = StepSweepService.Sweep(OneJobInput()).Gaps;

            Assert.Equal(1, gaps.MeasuredPairs);
            Assert.Equal(1, gaps.MeasuredJobs);
            Assert.True(gaps.StepInterfacesWereNull);
            Assert.True(gaps.RunRowOwnedTablesWereNull);
        }

        [Fact]
        public void ParseFailedJobsAndMissingStepFilesSurviveIntoTheReport()
        {
            var input = new SweepInput(
                new List<SweepJob>(),
                new List<string> { "POQSettleProc4", "POQSettleProc7" },
                51);

            var gaps = StepSweepService.Sweep(input).Gaps;

            Assert.Equal(new[] { "POQSettleProc4", "POQSettleProc7" }, gaps.PlanParseFailedJobs);
            Assert.Equal(51, gaps.MissingStepFiles);
            Assert.Equal(0, gaps.MeasuredPairs);
        }

        // 목차에 있는데 마크다운이 없는 단계는 세지 않는다 - 빈 문자열을 넘기면
        // "섹션 내용이 비어있습니다"가 발화해 결손이 결함으로 둔갑한다.
        [Fact]
        public void StepWithoutMarkdownIsNotMeasured()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>(),
            };
            var input = new SweepInput(new List<SweepJob> { job }, new List<string>(), 1);

            var report = StepSweepService.Sweep(input);

            Assert.Equal(0, report.Gaps.MeasuredPairs);
            Assert.Empty(report.Findings);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: 컴파일 실패 — `Sweep`이 없다.

- [ ] **Step 3: `Sweep`을 구현한다**

`StepSweepService.cs`에 추가:

```csharp
        /// <summary>
        /// 코퍼스 전수를 훑어 검사 A~E의 발화를 조건 (A)·(B) 양쪽으로 모은다.
        ///
        /// [왜 두 조건을 함께 재는가] 고를 수 있으면 잘못 고를 수 있다. 실제로 한 번
        /// 그랬다 - 조건 (B)를 재야 할 자리에서 (A)를 재고 "코퍼스가 변했다"고 보고한
        /// 일이 있었다. 두 조건의 차이 자체가 캐시 17이 켜질 때의 변화량이라 어차피
        /// 둘 다 필요하다.
        /// </summary>
        public static SweepReport Sweep(SweepInput input)
        {
            var validator = CreateValidator();
            var findings = new List<SweepFinding>();
            var measuredPairs = 0;
            var measuredJobs = 0;

            foreach (var job in input.Jobs)
            {
                var conditionColumns = SpecConditionColumnExtractor.Extract(job.Specs);
                var factsAsIs = SpecStatementFactsExtractor.Extract(job.Specs);
                var factsSimulated = InjectSimulatedCodes(factsAsIs, job);

                var measuredInThisJob = false;

                foreach (var step in job.Steps)
                {
                    // 목차가 선언했으나 실물이 없는 단계다. 빈 문자열을 넘기면
                    // "섹션 내용이 비어있습니다"가 발화해 결손이 결함으로 둔갑한다.
                    if (!job.StepMarkdownByCode.TryGetValue(step.Code, out var markdown)
                        || string.IsNullOrWhiteSpace(markdown))
                    {
                        continue;
                    }

                    measuredPairs++;
                    measuredInThisJob = true;

                    Collect(SweepCondition.AsIs, factsAsIs);
                    Collect(SweepCondition.SimulatedCache17, factsSimulated);

                    void Collect(
                        SweepCondition condition,
                        IReadOnlyDictionary<string, SpecStatementFacts> facts)
                    {
                        // 오케스트레이터(VerificationPipelineOrchestrator.cs:3238)의 호출을
                        // 그대로 본뜬다. 갈라지면 파이프라인이 실제로 하지 않는 판정을 재게 된다.
                        // stepInterfaces·runRowOwnedTables는 DB 메타데이터가 필요해 로컬에서
                        // 만들 수 없다. A~E 어느 검사도 그 둘을 읽지 않는다 -
                        // CheckStepInterface(:600)·CheckFirstStepRowCreation(:1518)만 쓴다.
                        var result = validator.ValidateBatchStep(
                            markdown, step,
                            Array.Empty<string>(),
                            conditionColumns,
                            stepInterfaces: null,
                            runRowOwnedTables: null,
                            statementFactsByProcedure: facts,
                            allSteps: job.Steps);

                        foreach (var message in result.Errors)
                        {
                            var check = StepSweepClassifier.Classify(message);
                            findings.Add(
                                StepSweepClassifier.Describe(
                                    job.JobName, step.Code, check, condition, message));
                        }
                    }
                }

                if (measuredInThisJob) measuredJobs++;
            }

            return new SweepReport(
                findings,
                ComputeIndicators(input),
                new HarnessGaps(
                    input.PlanParseFailedJobs,
                    input.MissingStepFiles,
                    measuredPairs,
                    measuredJobs,
                    StepInterfacesWereNull: true,
                    RunRowOwnedTablesWereNull: true,
                    KnownTableNamesWereEmpty: true));
        }

        /// <summary>SP별 재료에 조건 (B)의 코드 사전을 갈아 끼운다. 제품 코드는 안 바뀐다 - init 속성이다.</summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> InjectSimulatedCodes(
            IReadOnlyDictionary<string, SpecStatementFacts> facts, SweepJob job)
        {
            var injected = new Dictionary<string, SpecStatementFacts>(
                facts, StringComparer.OrdinalIgnoreCase);

            foreach (var (procedure, ddl) in job.DdlByProcedure)
            {
                var key = MechanicalValidator.BareObjectName(procedure);
                if (!injected.TryGetValue(key, out var existing)) continue;

                job.DateParameterByProcedure.TryGetValue(procedure, out var dateParameter);
                injected[key] = existing with
                {
                    ErrorCodeToOrdinal = BuildSimulatedErrorCodeMap(ddl, dateParameter ?? string.Empty),
                };
            }

            return injected;
        }
```

`ComputeIndicators`는 태스크 5에서 채운다. 이 태스크에서는 자리만 만든다:

```csharp
        private static SweepIndicators ComputeIndicators(SweepInput input) => new(0, 0, 0);
```

`CreateValidator()`는 `MechanicalValidator`의 생성자를 읽고 맞춘다. 인자가 필요 없으면 `new MechanicalValidator()` 한 줄이다.

`StepSweepClassifier.Describe`는 이 태스크에서는 좌표 없이 만든다 — 태스크 4가 채운다:

```csharp
        public static SweepFinding Describe(
            string jobName, string stepCode, SweepCheck check,
            SweepCondition condition, string message) =>
            new(jobName, stepCode, check, condition, message);
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: PASS (8건).

`ConditionBFiresWhereConditionAIsSilent`가 **양쪽 다 0**이면 (B)가 안 켜진 것이다. 순서대로 확인한다:
1. `BuildSimulatedErrorCodeMap(DdlOneUpdateWithCode, "@pi_strYMD")`가 `-13`을 담는가 (태스크 2 테스트가 이미 이 모양을 검증한다)
2. `MechanicalValidator.BareObjectName("dbo.UP_TEST")`가 `factsAsIs`의 키와 같은가
3. `StepSqlStatementReader.Read(StepMarkdownMissingPgName)`의 문장이 `CodeAnchor == "-13"`을 갖는가

**양쪽 다 1이면** (A)에서도 발화한 것이고, 그러면 픽스처의 단계 마크다운에 U-앵커 모양 주석이 섞였을 가능성이 크다. 주석을 빼고 다시 돌린다.

- [ ] **Step 5: 전체 빌드와 테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSweepService.cs tests/ReSet.Core.Tests/StepSweepServiceTests.cs
git commit
```

제목: `feat: 스윕이 조건 (A)와 (B)를 한 번에 잰다`

---

### Task 4: 검사 B·C 발화의 좌표를 뽑는다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepClassifier.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepClassifierTests.cs`

**Interfaces:**
- Produces: `StepSweepClassifier.Describe(...)`가 검사 B·C에서 `Kind`·`Ordinal`·`Items`를 채운 `SweepFinding`을 낸다.

103건 판정표의 열이 여기서 나온다. 메시지 안에 이미 좌표가 들어 있다 — `"{step.Code} 섹션의 {Kind} {Ordinal}(갱신 {Ordinal}) 문장에 …"`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`StepSweepClassifierTests.cs`에 추가:

```csharp
        [Fact]
        public void DescribeExtractsCoordinatesFromCheckBMessage()
        {
            const string message =
                "S07 섹션의 UPDATE 13(갱신 13) 문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 " +
                "YMD, PGNAME이(가) 없습니다. 명세서 DML 범위 표 UPDATE 13 행의 값은 `YMD, PGNAME`입니다 — ";

            var finding = StepSweepClassifier.Describe(
                "POQSettleBatch1", "S07", SweepCheck.B, SweepCondition.SimulatedCache17, message);

            Assert.Equal("UPDATE", finding.Kind);
            Assert.Equal(13, finding.Ordinal);
            Assert.Equal(new[] { "YMD", "PGNAME" }, finding.Items);
        }

        [Fact]
        public void DescribeExtractsCoordinatesFromCheckCMessage()
        {
            const string message =
                "S09 섹션의 UPDATE 2(갱신 2) 문장이 명세서에 없는 술어 컬럼 USESTATE을(를) 씁니다. " +
                "명세서 DML 범위 표 UPDATE 2 행의 최상위 술어 컬럼은 ";

            var finding = StepSweepClassifier.Describe(
                "POQSettleBatch1", "S09", SweepCheck.C, SweepCondition.SimulatedCache17, message);

            Assert.Equal("UPDATE", finding.Kind);
            Assert.Equal(2, finding.Ordinal);
            Assert.Equal(new[] { "USESTATE" }, finding.Items);
        }

        // 검사 A·D·E의 메시지에는 문장 좌표가 없다. 억지로 뽑아 채우면 없는 좌표가
        // 판정표에 실려 사람이 그 자리를 찾으러 간다.
        [Fact]
        public void DescribeLeavesCoordinatesEmptyForOtherChecks()
        {
            var finding = StepSweepClassifier.Describe(
                "J", "S01", SweepCheck.A, SweepCondition.AsIs,
                "S01 섹션이 `TSettleMst`에 대한 UPDATE를 8개만 담고 있습니다. 명세서 DML 범위 표는 15개를 확정합니다.");

            Assert.Null(finding.Kind);
            Assert.Null(finding.Ordinal);
            Assert.Empty(finding.Items);
        }

        // 문구가 바뀌어 좌표를 못 뽑아도 발화 자체는 세어야 한다 - 집계까지 잃으면
        // 검사가 침묵한 것과 구분되지 않는다.
        [Fact]
        public void DescribeStillCountsWhenCoordinatesCannotBeParsed()
        {
            var finding = StepSweepClassifier.Describe(
                "J", "S01", SweepCheck.B, SweepCondition.SimulatedCache17,
                "문장에 명세서가 확정한 무언가가 없습니다");

            Assert.Equal(SweepCheck.B, finding.Check);
            Assert.Null(finding.Kind);
            Assert.Empty(finding.Items);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepClassifierTests"
```

Expected: FAIL — `Kind`가 `null`이다.

- [ ] **Step 3: 좌표 추출을 구현한다**

`StepSweepClassifier.cs`에 추가:

```csharp
        // "S07 섹션의 UPDATE 13(갱신 13) 문장에" / "... 문장이" 양쪽을 잡는다.
        private static readonly Regex CoordinatePattern = new(
            @"섹션의\s+(?<kind>[A-Z]+)\s+(?<ordinal>\d+)\s*\(",
            RegexOptions.Compiled);

        // 검사 B: "확정한 <라벨> A, B이(가) 없습니다".
        // 라벨을 `.*?`로 넘기면 게으른 수량자가 라벨의 일부를 items에 남긴다
        // ("컬럼 YMD, PGNAME"). 라벨은 두 개뿐이므로(MechanicalValidator.cs:6334·6345)
        // 그대로 못 박는다 - 라벨이 늘면 여기도 늘려야 하고, 그때 태스크 1의
        // 판별 조각 테스트와 함께 갱신한다.
        private static readonly Regex MissingItemsPattern = new(
            @"확정한\s+(?:최상위\s+WHERE\s+술어\s+컬럼|조인\s+키)\s+(?<items>.*?)이\(가\)\s*없습니다",
            RegexOptions.Compiled);

        // 검사 C: "없는 술어 컬럼 A, B을(를) 씁니다"
        private static readonly Regex ExtraItemsPattern = new(
            @"없는\s+술어\s+컬럼\s+(?<items>.*?)을\(를\)\s*씁니다",
            RegexOptions.Compiled);

        /// <summary>
        /// 발화 하나를 판정표의 한 행으로 만든다.
        ///
        /// [왜 좌표를 메시지에서 뽑는가] StepValidationResult가 구조화된 값을 내지 않으므로
        /// 메시지가 유일한 출처다. 뽑히지 않아도 발화는 센다 - 집계까지 잃으면 검사가
        /// 침묵한 것과 구분되지 않는다.
        /// </summary>
        public static SweepFinding Describe(
            string jobName, string stepCode, SweepCheck check,
            SweepCondition condition, string message)
        {
            var finding = new SweepFinding(jobName, stepCode, check, condition, message);
            if (check != SweepCheck.B && check != SweepCheck.C) return finding;

            var coordinate = CoordinatePattern.Match(message);
            if (coordinate.Success)
            {
                finding = finding with
                {
                    Kind = coordinate.Groups["kind"].Value,
                    Ordinal = int.Parse(coordinate.Groups["ordinal"].Value),
                };
            }

            var items = check == SweepCheck.B
                ? MissingItemsPattern.Match(message)
                : ExtraItemsPattern.Match(message);

            if (items.Success)
            {
                finding = finding with { Items = SplitItems(items.Groups["items"].Value) };
            }

            return finding;
        }

        private static IReadOnlyList<string> SplitItems(string raw) =>
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
```

`using System.Collections.Generic;`와 `using System.Text.RegularExpressions;`를 파일 상단에 더한다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepClassifierTests"
```

Expected: PASS (11건). 정규식이 안 맞으면 **테스트의 기대값을 낮추지 말고 정규식을 고친다** — 기대값은 실제 검사가 내는 문장에서 그대로 따온 것이다.

- [ ] **Step 5: 전체 빌드와 테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSweepClassifier.cs tests/ReSet.Core.Tests/StepSweepClassifierTests.cs
git commit
```

제목: `feat: 검사 B·C 발화에서 판정표 좌표를 뽑는다`

---

### Task 5: 선결 지표 둘

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepService.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepServiceTests.cs`

**Interfaces:**
- Consumes: `StepSqlStatementReader.Read(string? stepMarkdown) → IReadOnlyList<StepSqlStatement>` (`StepSqlStatementReader.cs:64`) · `StepSqlStatement(string Kind, string TargetTable, int? Anchor, IReadOnlyList<string> PredicateColumns, IReadOnlyList<string> JoinColumns, bool HasGrouping, bool HasOpaqueJoinSource = false, string? CodeAnchor = null)` (:29)
- Produces: `SweepIndicators`의 세 필드가 실제 값으로 채워진다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`StepSweepServiceTests.cs`에 추가:

```csharp
        [Fact]
        public void MultiProcedureStepsAreCounted()
        {
            var job = OneJobInput().Jobs[0] with
            {
                Steps = new List<BatchStepPlan>
                {
                    new("S01", "둘", new List<string> { "dbo.UP_A", "dbo.UP_B" },
                        new List<string>(), new List<string>(), false, new List<string>()),
                    new("S02", "하나", new List<string> { "dbo.UP_A" },
                        new List<string>(), new List<string>(), false, new List<string>()),
                },
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownMissingPgName,
                    ["S02"] = StepMarkdownMissingPgName,
                },
            };

            var report = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0));

            Assert.Equal(1, report.Indicators.MultiProcedureSteps);
        }

        // UP_UTIL_SETTLE_COMM_UPD의 -9 소실 모양이다. SP 표에는 -13이 있는데
        // 단계 SQL은 -14만 단다 - 라벨이 밀렸다는 신호다.
        [Fact]
        public void CodeSetMismatchIsCountedInBothDirections()
        {
            var job = OneJobInput().Jobs[0] with
            {
                StepMarkdownByCode = new Dictionary<string, string>
                {
                    ["S01"] = StepMarkdownMissingPgName.Replace("-13", "-14"),
                },
            };

            var indicators = StepSweepService.Sweep(
                new SweepInput(new List<SweepJob> { job }, new List<string>(), 0)).Indicators;

            Assert.Equal(1, indicators.StepsMissingSpecCodes);   // 표의 -13이 단계에 없다
            Assert.Equal(1, indicators.StepsWithUnknownCodes);   // 단계의 -14가 표에 없다
        }

        [Fact]
        public void MatchingCodeSetsCountAsNeitherMismatch()
        {
            var indicators = StepSweepService.Sweep(OneJobInput()).Indicators;

            Assert.Equal(0, indicators.StepsMissingSpecCodes);
            Assert.Equal(0, indicators.StepsWithUnknownCodes);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: FAIL — 지표가 전부 0이다(`ComputeIndicators`가 자리만 있다).

- [ ] **Step 3: `ComputeIndicators`를 구현한다**

태스크 3에서 만든 자리표시자를 지우고 `StepSweepService.cs`에 넣는다:

```csharp
        /// <summary>
        /// 캐시 17 인상 전에 세야 할 노출량 둘.
        ///
        /// [다중 레거시 SP 단계] MergeErrorCodeMaps는 코드 문자열만을 키로 삼고 SP로
        /// 스코프하지 않는다. SP A에만 있는 코드가 병합 사전에 남아, 실제로는 SP B에서
        /// 온 문장을 A의 (Kind, Ordinal)로 환산할 수 있다. 하위 가드(후보 1개 판정 +
        /// TargetTable 대조)는 두 SP가 같은 물리 테이블을 갱신하면 통과한다.
        ///
        /// [코드 집합 어긋남] 실측 사례가 있다 - UP_UTIL_SETTLE_COMM_UPD의 원본은
        /// -9/-10/-11을 쓰는데 이행 코드는 같은 세 블록에 -10/-11/-12를 단다. -9가
        /// 소실되고 이후 전체가 1씩 밀렸다. 밀림을 직접 보는 대신 밀림의 원인(라벨
        /// 소실)을 본다 - 집합 단위라 값싸다.
        /// </summary>
        private static SweepIndicators ComputeIndicators(SweepInput input)
        {
            var multiProcedureSteps = 0;
            var missingSpecCodes = 0;
            var unknownCodes = 0;

            foreach (var job in input.Jobs)
            {
                foreach (var step in job.Steps)
                {
                    if (step.LegacyProcedures.Count > 1) multiProcedureSteps++;

                    if (!job.StepMarkdownByCode.TryGetValue(step.Code, out var markdown)
                        || string.IsNullOrWhiteSpace(markdown))
                    {
                        continue;
                    }

                    var stepCodes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var statement in StepSqlStatementReader.Read(markdown))
                    {
                        if (!string.IsNullOrWhiteSpace(statement.CodeAnchor))
                        {
                            stepCodes.Add(statement.CodeAnchor!);
                        }
                    }

                    var specCodes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var procedure in step.LegacyProcedures)
                    {
                        if (!job.DdlByProcedure.TryGetValue(procedure, out var ddl)) continue;
                        job.DateParameterByProcedure.TryGetValue(procedure, out var dateParameter);

                        foreach (var code in BuildSimulatedErrorCodeMap(
                                     ddl, dateParameter ?? string.Empty).Keys)
                        {
                            specCodes.Add(code);
                        }
                    }

                    // 양쪽이 다 비면 대조할 것이 없다 - 어긋남이 아니라 무재료다.
                    if (stepCodes.Count == 0 && specCodes.Count == 0) continue;

                    if (specCodes.Except(stepCodes, StringComparer.Ordinal).Any()) missingSpecCodes++;
                    if (stepCodes.Except(specCodes, StringComparer.Ordinal).Any()) unknownCodes++;
                }
            }

            return new SweepIndicators(multiProcedureSteps, missingSpecCodes, unknownCodes);
        }
```

`using System.Linq;`를 파일 상단에 더한다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepServiceTests"
```

Expected: PASS (11건).

- [ ] **Step 5: 전체 빌드와 테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSweepService.cs tests/ReSet.Core.Tests/StepSweepServiceTests.cs
git commit
```

제목: `feat: 캐시 17 선결 지표 둘을 같은 스윕에서 센다`

---

### Task 6: 보고서 렌더러

**Files:**
- Create: `src/ReSet.Core/Services/StepSweepReportWriter.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs`

**Interfaces:**
- Consumes: `SweepReport`(태스크 1)
- Produces: `StepSweepReportWriter.Render(SweepReport report, string commitHash, string cacheFormatVersions) → string`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepReportWriterTests
    {
        private static SweepReport Report(params SweepFinding[] findings) => new(
            findings,
            new SweepIndicators(3, 2, 1),
            new HarnessGaps(
                new List<string> { "POQSettleProc4" }, 51, 326, 18,
                StepInterfacesWereNull: true,
                RunRowOwnedTablesWereNull: true,
                KnownTableNamesWereEmpty: true));

        // 결손을 안 실으면 줄어든 대상 범위가 개선처럼 보인다.
        [Fact]
        public void HeaderAlwaysCarriesHarnessGaps()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16");

            Assert.Contains("abc1234", markdown);
            Assert.Contains("16", markdown);
            Assert.Contains("POQSettleProc4", markdown);
            Assert.Contains("51", markdown);
            Assert.Contains("326", markdown);
            Assert.Contains("stepInterfaces", markdown);
            Assert.Contains("runRowOwnedTables", markdown);
        }

        // (B)가 상한이라는 사실을 보고서가 스스로 말해야 한다 - 재생성 후 실제
        // 발화량의 예측으로 읽히면 다음 사람이 잘못된 기대를 갖는다.
        [Fact]
        public void ReportStatesThatConditionBIsAnUpperBound()
        {
            Assert.Contains("상한", StepSweepReportWriter.Render(Report(), "abc1234", "16"));
        }

        [Fact]
        public void TalliesSplitByCheckAndCondition()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S02", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S01", SweepCheck.A, SweepCondition.AsIs, "m")),
                "abc1234", "16");

            Assert.Contains("| B | 0 | 2 |", markdown);
            Assert.Contains("| A | 1 | 0 |", markdown);
        }

        [Fact]
        public void AnchoredFindingsBecomeAJudgementTableWithAnEmptyVerdictColumn()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("POQSettleProc13", "S09", SweepCheck.B,
                        SweepCondition.SimulatedCache17, "m")
                    { Kind = "UPDATE", Ordinal = 3, Items = new[] { "PGNAME", "MALLID" } }),
                "abc1234", "16");

            Assert.Contains("POQSettleProc13", markdown);
            Assert.Contains("UPDATE 3", markdown);
            Assert.Contains("PGNAME, MALLID", markdown);
            Assert.Contains("판정", markdown);
        }

        [Fact]
        public void PerJobTableSplitsByJob()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J2", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m")),
                "abc1234", "16");

            Assert.Contains("| J1 | B | 0 | 1 |", markdown);
            Assert.Contains("| J2 | B | 0 | 1 |", markdown);
        }

        // 미분류가 0이 아니면 검사 문구가 바뀐 것이다. 표에 안 실으면 아무도 모른다.
        [Fact]
        public void UnclassifiedCountIsShown()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(new SweepFinding("J", "S01", SweepCheck.Unclassified, SweepCondition.AsIs, "m")),
                "abc1234", "16");

            Assert.Contains("미분류", markdown);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepReportWriterTests"
```

Expected: 컴파일 실패 — `StepSweepReportWriter`가 없다.

- [ ] **Step 3: 렌더러를 구현한다**

`src/ReSet.Core/Services/StepSweepReportWriter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SweepReport를 사람이 읽고 회차 간에 견줄 수 있는 마크다운으로 낸다.
    ///
    /// [왜 결손을 머리말에 강제로 싣는가] 대상 범위가 줄면 발화량도 줄어드는데, 결손을
    /// 안 적으면 그 감소가 개선처럼 읽힌다. 카탈로그가 "총량을 회차 간에 비교하지 마라"고
    /// 경고하는 함정이 정확히 이것이다. 메시지 원문은 표에 싣지 않는다 - 파이프 문자가
    /// 섞이면 표가 깨진다.
    /// </summary>
    public static class StepSweepReportWriter
    {
        // 0이어도 행을 낸다. 빠진 검사와 발화가 0인 검사는 다른 사실이다.
        private static readonly SweepCheck[] Checks =
        {
            SweepCheck.A, SweepCheck.B, SweepCheck.C,
            SweepCheck.D, SweepCheck.E, SweepCheck.Unclassified,
        };

        public static string Render(SweepReport report, string commitHash, string cacheFormatVersions)
        {
            var b = new StringBuilder();

            b.AppendLine("# 단계 검사 스윕");
            b.AppendLine();
            AppendConditions(b, report.Gaps, commitHash, cacheFormatVersions);
            AppendTotals(b, report.Findings);
            AppendPerJob(b, report.Findings);
            AppendUpperBoundNote(b);
            AppendAnchoredFindings(b, report.Findings);
            AppendIndicators(b, report.Indicators);

            return b.ToString();
        }

        private static void AppendConditions(
            StringBuilder b, HarnessGaps gaps, string commitHash, string cacheFormatVersions)
        {
            b.AppendLine("## 실행 조건");
            b.AppendLine();
            b.AppendLine($"- 커밋: `{commitHash}`");
            b.AppendLine($"- 캐시 인덱스 `FormatVersion` 집합: {cacheFormatVersions}");
            b.AppendLine($"- 측정 쌍: {gaps.MeasuredPairs} (Job {gaps.MeasuredJobs}개)");
            b.AppendLine($"- 단계 파일 누락: {gaps.MissingStepFiles}");

            var failed = gaps.PlanParseFailedJobs.Count == 0
                ? "없음"
                : string.Join(", ", gaps.PlanParseFailedJobs);
            b.AppendLine($"- 목차 파싱 실패 Job: {failed}");

            if (gaps.StepInterfacesWereNull)
            {
                b.AppendLine(
                    "- `stepInterfaces`를 `null`로 넘겼다(DB 메타데이터가 필요해 로컬에서 " +
                    "만들 수 없다). 검사 A~E는 이 값을 읽지 않는다.");
            }

            if (gaps.RunRowOwnedTablesWereNull)
            {
                b.AppendLine("- `runRowOwnedTables`를 `null`로 넘겼다(같은 이유). 검사 A~E는 이 값을 읽지 않는다.");
            }

            if (gaps.KnownTableNamesWereEmpty)
            {
                b.AppendLine("- `knownTableNames`가 비어 유령 테이블 검사가 소프트 스킵됐다.");
            }

            b.AppendLine();
        }

        private static void AppendTotals(StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## 검사별 발화량");
            b.AppendLine();
            b.AppendLine("| 검사 | (A) 오늘 | (B) 캐시 17 모사 |");
            b.AppendLine("| :--- | ---: | ---: |");

            foreach (var check in Checks)
            {
                b.AppendLine(
                    $"| {Label(check)} | {Count(findings, check, SweepCondition.AsIs)} " +
                    $"| {Count(findings, check, SweepCondition.SimulatedCache17)} |");
            }

            b.AppendLine();
        }

        private static void AppendPerJob(StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## Job별 발화량");
            b.AppendLine();
            b.AppendLine("| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |");
            b.AppendLine("| :--- | :--- | ---: | ---: |");

            var jobs = findings
                .Select(f => f.JobName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var job in jobs)
            {
                var ofJob = findings.Where(f => f.JobName == job).ToList();
                foreach (var check in Checks)
                {
                    var asIs = Count(ofJob, check, SweepCondition.AsIs);
                    var simulated = Count(ofJob, check, SweepCondition.SimulatedCache17);
                    if (asIs == 0 && simulated == 0) continue;

                    b.AppendLine($"| {job} | {Label(check)} | {asIs} | {simulated} |");
                }
            }

            b.AppendLine();
        }

        private static void AppendUpperBoundNote(StringBuilder b)
        {
            b.AppendLine("## 조건 (B)는 상한이다");
            b.AppendLine();
            b.AppendLine(
                "(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 " +
                "사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 " +
                "`ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 " +
                "축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**");
            b.AppendLine();
        }

        private static void AppendAnchoredFindings(
            StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## 검사 B·C 발화 목록");
            b.AppendLine();
            b.AppendLine("판정 칸은 비어 있다 — 원본 DDL과 이행 SQL을 읽어 사람이 채운다.");
            b.AppendLine();
            b.AppendLine("| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |");
            b.AppendLine("| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            var rows = findings
                .Where(f => f.Check == SweepCheck.B || f.Check == SweepCheck.C)
                .ToList();

            for (var i = 0; i < rows.Count; i++)
            {
                var f = rows[i];
                var statement = f.Kind == null ? "—" : $"{f.Kind} {f.Ordinal}";
                var items = f.Items.Count == 0 ? "—" : string.Join(", ", f.Items);
                var condition = f.Condition == SweepCondition.AsIs ? "A" : "B";

                b.AppendLine(
                    $"| {i + 1} | {Label(f.Check)} | {condition} | {f.JobName} | {f.StepCode} " +
                    $"| {statement} | {items} |  |");
            }

            b.AppendLine();
        }

        private static void AppendIndicators(StringBuilder b, SweepIndicators indicators)
        {
            b.AppendLine("## 캐시 17 선결 지표");
            b.AppendLine();
            b.AppendLine("| 지표 | 값 |");
            b.AppendLine("| :--- | ---: |");
            b.AppendLine($"| 다중 레거시 SP 단계 수 | {indicators.MultiProcedureSteps} |");
            b.AppendLine($"| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | {indicators.StepsMissingSpecCodes} |");
            b.AppendLine($"| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | {indicators.StepsWithUnknownCodes} |");
            b.AppendLine();
        }

        private static int Count(
            IEnumerable<SweepFinding> findings, SweepCheck check, SweepCondition condition) =>
            findings.Count(f => f.Check == check && f.Condition == condition);

        private static string Label(SweepCheck check) =>
            check == SweepCheck.Unclassified ? "미분류" : check.ToString();
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepReportWriterTests"
```

Expected: PASS (6건).

- [ ] **Step 5: 전체 빌드와 테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSweepReportWriter.cs tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs
git commit
```

제목: `feat: 스윕 보고서를 마크다운으로 낸다`

---

### Task 7: CLI 배선

**Files:**
- Create: `src/ReSet.Cli/SweepCommand.cs`
- Modify: `src/ReSet.Core/Services/StepSweepService.cs` (이름 규칙 창구 하나)
- Modify: `src/ReSet.Cli/CliArgs.cs`
- Modify: `src/ReSet.Cli/Program.cs` (커버리지 맵 분기 옆, `Program.cs:145-165` 부근)

**Interfaces:**
- Consumes: `StepSweepService.Sweep(SweepInput) → SweepReport` · `StepSweepReportWriter.Render(SweepReport, string, int) → string` · `BatchStepPlanParser.TryParse(string?) → IReadOnlyList<BatchStepPlan>?`
- Produces: `SweepCommand.Run(string outputDir, string repoRoot) → string?` (쓴 보고서 경로, 아무것도 못 재면 `null`)

**경로 규약(실측):**

| | 경로 |
|---|---|
| Job 목록 | `output/Jobs/*/` |
| 목차 | `output/Jobs/<job>/raw/PlanStructure.md` |
| 단계 지시서 | `output/Jobs/<job>/agent/steps/<step.Code>.md` |
| 명세서 | `output/Procedures/<procedure>/docs/Spec.md` |
| 원본 DDL·파라미터 | `output/Procedures/<procedure>/raw/metadata.json` |

**`Specs`의 `FileName`은 프로시저 디렉터리 이름(`"dbo.UP_UTIL_SETTLE_COMM_UPD"`)을 넘긴다.** 파일 경로나 `"...md"`를 넘기면 `BareObjectName`이 `"md"`를 키로 만들어 모든 조회가 빗나간다.

- [ ] **Step 1: `CliArgs`에 인자를 더한다**

`src/ReSet.Cli/CliArgs.cs`에 `public bool RunSweep { get; set; }`를 더하고, `Program.ParseCommandLineArgs`(`Program.cs:23`)에 `--sweep` 분기를 넣는다. 기존 `--coverage-map` 분기의 모양을 그대로 따른다.

- [ ] **Step 2: Core에 이름 규칙 창구를 연다**

`MechanicalValidator.BareObjectName`은 `internal`이라 CLI(다른 어셈블리)에서 못 부른다. 규칙을 CLI에 다시 구현하면 두 로직이 갈린다 — 그 함수의 문서 주석이 정확히 그 위험을 경고한다. `StepSweepService.cs`에 창구만 연다:

```csharp
        /// <summary>
        /// SweepCommand가 접두사 제거 규칙을 다시 구현하지 않게 하는 창구.
        /// MechanicalValidator.BareObjectName이 internal이라 CLI에서 직접 못 부른다.
        /// 규칙이 두 곳에 생기면 조회가 미묘하게 어긋난다.
        /// </summary>
        public static string BareProcedureName(string qualifiedName) =>
            MechanicalValidator.BareObjectName(qualifiedName);
```

- [ ] **Step 3: `SweepCommand`를 만든다**

`src/ReSet.Cli/SweepCommand.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    /// <summary>
    /// DB·AI 없이 output/ 산출물만으로 단계 검사 A~E를 전수 스윕한다.
    ///
    /// 로직은 전부 StepSweepService에 있다. 여기 남는 것은 파일 읽기와 쓰기뿐이다 -
    /// 그래야 스윕 로직을 코퍼스 없이 테스트할 수 있다(CoverageMapCommand가 반대로
    /// 해서 그 테스트가 코퍼스 없으면 Skip으로 조용히 통과한다).
    /// </summary>
    public static class SweepCommand
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>쓴 보고서 경로. 잰 것이 하나도 없으면 null.</summary>
        public static string? Run(string outputDir, string repoRoot)
        {
            var jobsDir = Path.Combine(outputDir, "Jobs");
            if (!Directory.Exists(jobsDir)) return null;

            // 프로시저 디렉터리 색인. step.LegacyProcedures는 스키마 접두사가 있을 때도
            // 없을 때도 있다(실측 314개 중 134개가 접두사 없음). 맨이름으로 찾는다.
            var procedureDirs = IndexProcedureDirectories(outputDir);

            var jobs = new List<SweepJob>();
            var parseFailed = new List<string>();
            var missingStepFiles = 0;

            foreach (var jobDir in Directory
                         .GetDirectories(jobsDir)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var jobName = Path.GetFileName(jobDir);

                var planPath = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var steps = File.Exists(planPath)
                    ? BatchStepPlanParser.TryParse(File.ReadAllText(planPath))
                    : null;

                if (steps == null || steps.Count == 0)
                {
                    parseFailed.Add(jobName);
                    continue;
                }

                var markdownByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var step in steps)
                {
                    var stepPath = Path.Combine(jobDir, "agent", "steps", $"{step.Code}.md");
                    if (!File.Exists(stepPath))
                    {
                        missingStepFiles++;
                        continue;
                    }

                    markdownByCode[step.Code] = File.ReadAllText(stepPath);
                }

                var specs = new List<(string FileName, string Content)>();
                var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var dateParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var procedures = steps
                    .SelectMany(s => s.LegacyProcedures)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var procedure in procedures)
                {
                    var bare = StepSweepService.BareProcedureName(procedure);
                    if (!procedureDirs.TryGetValue(bare, out var dir)) continue;

                    var specPath = Path.Combine(dir, "docs", "Spec.md");
                    var metaPath = Path.Combine(dir, "raw", "metadata.json");
                    if (!File.Exists(specPath) || !File.Exists(metaPath)) continue;

                    // FileName은 프로시저 이름이지 파일 경로가 아니다.
                    // SpecStatementFactsExtractor가 BareObjectName(FileName)으로 키를 만들어
                    // "...md"를 넘기면 키가 "md"가 되어 모든 조회가 빗나간다.
                    var name = Path.GetFileName(dir);
                    specs.Add((name, File.ReadAllText(specPath)));

                    // metadata.json에는 BOM이 붙어 있다. File.ReadAllText가 자동으로 벗긴다
                    // (CoverageMapCommand.cs:203과 같은 규약).
                    var spDef = JsonSerializer.Deserialize<SpDefinition>(
                        File.ReadAllText(metaPath), JsonOptions);
                    if (spDef == null) continue;

                    ddl[name] = spDef.DdlText ?? string.Empty;
                    dateParameters[name] = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
                }

                jobs.Add(new SweepJob(jobName, steps, markdownByCode, specs, ddl, dateParameters));
            }

            var report = StepSweepService.Sweep(
                new SweepInput(jobs, parseFailed, missingStepFiles));

            // 아무것도 재지 못했는데 0으로 끝나면 파이프라인이 초록으로 통과한다.
            if (report.Gaps.MeasuredPairs == 0) return null;

            var markdown = StepSweepReportWriter.Render(
                report, ShortCommitHash(repoRoot), CacheFormatVersions(outputDir));

            var path = NextAvailablePath(repoRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown);
            return path;
        }

        private static Dictionary<string, string> IndexProcedureDirectories(string outputDir)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(outputDir, "Procedures");
            if (!Directory.Exists(root)) return index;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var bare = StepSweepService.BareProcedureName(Path.GetFileName(dir));
                if (bare.Length > 0) index[bare] = dir;
            }

            return index;
        }

        /// <summary>
        /// 캐시 인덱스에 실제로 실린 FormatVersion 값들. 코드 상수
        /// (CacheManager.CurrentCacheFormatVersion)는 private이라 읽을 수 없고,
        /// 어차피 측정에 영향을 주는 것은 산출물이 어느 버전으로 만들어졌는가다.
        /// </summary>
        private static string CacheFormatVersions(string outputDir)
        {
            var path = Path.Combine(outputDir, ".sp_cache_index.json");
            if (!File.Exists(path)) return "알 수 없음(캐시 인덱스 없음)";

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var versions = new SortedSet<int>();

                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    if (entry.TryGetProperty("FormatVersion", out var v) && v.TryGetInt32(out var n))
                    {
                        versions.Add(n);
                    }
                }

                return versions.Count == 0
                    ? "알 수 없음(항목 없음)"
                    : $"{{{string.Join(", ", versions)}}} — 항목 {doc.RootElement.GetProperty("entries").GetArrayLength()}개";
            }
            catch (Exception ex)
            {
                return $"알 수 없음({ex.GetType().Name})";
            }
        }

        private static string ShortCommitHash(string repoRoot)
        {
            try
            {
                var info = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

                using var process = Process.Start(info);
                if (process == null) return "unknown";

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output.Length == 0 ? "unknown" : output;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// 같은 날 두 번 돌려도 앞 보고서를 덮지 않는다. 이름 고정이 보고서를 잃게 한
        /// 전례가 있다 - ConsistencyReport.md가 그래서 5회차 판을 잃을 뻔했다.
        /// </summary>
        private static string NextAvailablePath(string repoRoot)
        {
            var dir = Path.Combine(repoRoot, "docs", "audit-reports", "sweeps");
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            var path = Path.Combine(dir, $"{today}-step-sweep.md");
            if (!File.Exists(path)) return path;

            for (var suffix = 'b'; suffix <= 'z'; suffix++)
            {
                var candidate = Path.Combine(dir, $"{today}-step-sweep-{suffix}.md");
                if (!File.Exists(candidate)) return candidate;
            }

            throw new InvalidOperationException("같은 날 보고서가 26개를 넘었습니다.");
        }
    }
}
```

**`SpDefinition`의 실제 속성 이름을 `src/ReSet.Core/Models/SpDefinition.cs`에서 확인하고 맞춘다** — `DdlText`·`StaticAnalysis`가 이 이름 그대로인지 본다. `.sp_cache_index.json`의 최상위 키가 `entries`가 아니면 `CacheFormatVersions`를 실물에 맞춘다.

- [ ] **Step 4: `Program.cs`에 분기를 넣는다**

커버리지 맵 분기(`Program.cs:145` 부근) 바로 아래, **배치 가드보다 앞에** 넣는다:

```csharp
            if (cliArgs.RunSweep)
            {
                var written = SweepCommand.Run(outputDir, Directory.GetCurrentDirectory());
                if (written == null)
                {
                    AnsiConsole.MarkupLine("[red]스윕할 대상을 찾지 못했습니다.[/]");
                    // 커버리지 맵 분기와 같은 규약이다 - 종료 코드 0으로 끝나면
                    // 아무것도 만들지 않았는데도 파이프라인이 초록으로 통과한다.
                    Environment.ExitCode = 1;
                    return;
                }

                AnsiConsole.MarkupLine($"[green]스윕 보고서: {Markup.Escape(written)}[/]");
                return;
            }
```

- [ ] **Step 5: 빌드하고 실제로 돌려 본다**

```bash
dotnet build
ln -s /Users/payletter/git-root/ReSet/output output   # 워크트리엔 output/이 없다
dotnet run --project src/ReSet.Cli -- --sweep
```

Expected: `docs/audit-reports/sweeps/2026-08-26-step-sweep.md`가 생기고, 종료 코드 0.

**측정 쌍이 326 근처가 아니면 멈추고 원인을 찾는다.** Task 19의 실측이 326(Job 18개)이었다. 크게 다르면 경로 규약이나 키 규약이 어긋난 것이다 — 보고서 숫자를 그대로 믿지 않는다.

- [ ] **Step 6: 전체 테스트 후 커밋**

```bash
dotnet test
git add src/ReSet.Cli/SweepCommand.cs src/ReSet.Core/Services/StepSweepService.cs src/ReSet.Cli/CliArgs.cs src/ReSet.Cli/Program.cs
git commit
```

제목: `feat: --sweep 서브명령으로 코퍼스 스윕을 돌린다`

**`output` 심링크는 커밋하지 않는다.** `git status`로 확인하고, 추적되고 있으면 `.git/info/exclude`에 넣는다.

---

### Task 8: 첫 측정과 보고서 커밋

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-26-step-sweep.md` (도구가 만든다)
- Modify: `docs/known-defects.md`

- [ ] **Step 1: 스윕을 돌린다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep
```

- [ ] **Step 2: 보고서를 읽고 Task 19 수치와 대조한다**

Task 19의 (A) 조건 실측은 `A=10 · B=0 · C=0 · D=52 · E=59`(측정 쌍 326, Job 18)였다. 조건 (A)의 값이 여기서 크게 벗어나면 **하네스가 파이프라인과 갈라진 것이다.** 벗어난 검사를 지목해 원인을 찾고, 못 찾으면 보고서에 "Task 19와 어긋남" 절을 만들어 수치와 함께 적는다 — **조용히 넘기지 않는다.**

미분류가 0이 아니면 검사 문구가 바뀐 것이다. `StepSweepClassifier`의 판별 조각을 실제 메시지와 다시 맞추고 태스크 1의 테스트를 갱신한다.

- [ ] **Step 3: `known-defects.md`에 이 회차를 기록한다**

「캐시 17 인상 전 선결 조건」 절의 **(5) 인상 전 재측정 항목** 아래에 측정값을 적는다 — 다중 레거시 SP 단계 수, 코드 집합이 어긋나는 SP 수(양방향), 검사 B·C의 (B) 조건 발화량. 보고서 파일 경로를 근거로 링크한다.

- [ ] **Step 4: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-26-step-sweep.md docs/known-defects.md
git commit
```

제목: `docs: 첫 스윕 측정 — 검사 A~E 발화량과 캐시 17 선결 지표`
본문에 담을 것: (A)·(B) 조건별 수치, Task 19와의 대조 결과, 미분류 건수.

---

## 이 계획이 닫지 않는 것

다음 회차로 넘긴다. 이 계획의 산출물(수치)이 그 판단의 근거가 된다.

- **103건 판정 자체.** 도구는 목록과 좌표를 낼 뿐이고, 진짜 결함인지는 원본 DDL과 이행 SQL을 읽어야 갈린다. 보고서의 「판정」 칸이 그 작업의 자리다.
- **코드 집합 대조 방어의 구현.** 태스크 5의 지표가 노출량을 재고, 방어의 모양은 그 수치를 보고 정한다.
- **CTE 가드를 검사 B·C로 확장 · `StepSqlStatementReader`의 INSERT 배선 수정(`:456-465`) · 캐시 16→17 인상 · 전건 재생성.**
