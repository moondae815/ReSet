# 축 B 배치 골격 계약 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 배치 제어 테이블의 정본 계약과 원본 파라미터 사실을 재료로 세워, 프롬프트와 L1이 같은 사실을 소비하게 만든다.

**Architecture:** 재료 둘을 만든다 — `BatchControlContract`(ReSet이 정본을 소유)와 `StepInterfaceFacts`(파서가 이미 확정한 `ProcedureParameters`를 배선). 두 재료를 `AppendSharedStepContext`의 공유 접두사에 표로 렌더하고, 같은 재료로 `MechanicalValidator`가 대조한다. `ConsolidatedPlanRules`에서 거짓을 심는 규칙 셋(규칙 5의 파라미터 발명, Few-Shot의 `THROW`, 넓은 그림자 권장)을 수술한다.

**Tech Stack:** .NET 10 / C#, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3, xUnit

**Spec:** `docs/superpowers/specs/2026-08-18-axis-b-batch-skeleton-design.md`

## Global Constraints

- **지배 계약(설계 §0):** 재료 하나가 사실을 내고 프롬프트와 L1이 **같은 사실**을 소비한다. 규칙만 있고 물리는 기계 검사가 없으면 그 규칙은 없는 것과 같다.
- **캐시 불변성(설계 §4):** `AppendSharedStepContext`가 만드는 부분은 **단계마다 바이트 동일**해야 한다. M2는 전 단계 표를 통째로 싣는다. 단계별로 자기 것만 실으면 입력 토큰이 1배에서 18배가 되고 **산출물은 그대로라 코드만 봐서는 알 수 없다**.
- **정본 어휘(설계 §1.1):** 상태 컬럼은 `<대상>Status`, 성공 종료는 `Succeeded` 하나. `Completed`는 쓰지 않는다. 시각은 `StartedAtUtc`/`CompletedAtUtc`, 실패 사유는 `ErrorMessage`, 작업명은 `JobName`.
- **소프트 스킵:** 재료가 비면 검사를 실행하지 않고 **실행하지 않았다는 사실을 `Log.Information`으로 남긴다**(AGENTS.md 범주 2, `ValidateBatchStep`의 `ErrorCodes` 선례). "대조해서 깨끗함"과 "대조할 것이 없었음"을 결과에서 구별한다.
- **단계 검사 결과는 `StepValidationResult.Errors`(문자열)에 실는다.** `ErrorType`은 `ValidationResult.DetailedErrors` 쪽 어휘다. `Errors`에 실어야 `SuggestedPromptFix`가 재생성 프롬프트로 넘긴다.
- **레드-그린 필수:** 모든 검사·재료 변경은 되돌렸을 때 실제로 실패해야 한다.
- **경고 기준선 9개:** `dotnet build --no-incremental` 결과가 9를 넘으면 안 된다. `Assert.Single(x.Where(...))`를 쓰지 말 것 — `xUnit2031` 경고가 난다. `Assert.Single(x, predicate)` 오버로드를 쓴다.
- **한국어 주석:** 이 저장소의 주석·오류 메시지는 한국어다. **왜 그렇게 했는지**를 적는다.
- **C# 보간 중괄호:** 프롬프트 문자열 안의 `{}`는 `{{}}`로 이스케이프한다(AGENTS.md 범주 7).
- **프롬프트는 영문:** `AiService`의 시스템 프롬프트는 영문 원칙이다(AGENTS.md 범주 4). 한국어는 출력 조건 지시에만 쓴다.

## File Structure

| 파일 | 책임 | 변경 | 태스크 |
|---|---|---|---|
| `src/ReSet.Core/Services/BatchControlContract.cs` | M1 정본 — 테이블 4종의 컬럼·상태 어휘·행 소유권, `RenderDdl`/`RenderPromptTable` | 신규 | 1 |
| `src/ReSet.Core/Services/StepInterfaceFacts.cs` | M2 — 단계별 원본 파라미터 표 조립·렌더 | 신규 | 2 |
| `src/ReSet.Core/Services/AiService.cs` | `AppendSharedStepContext`에 표 둘 배선 | 수정 | 3 |
| `src/ReSet.Core/Services/AiService.cs` | `ConsolidatedPlanRules` 수술(규칙 5·4·11·Few-Shot·2) | 수정 | 4 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 단계 인터페이스 검사 | 수정 | 5 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 제어 어휘·행 출처 검사 | 수정 | 6 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 그림자 계약·반환 경로 검사 | 수정 | 7 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 검증식 카티전 검사 | 수정 | 8 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | M2를 `ValidateBatchStep`에 전달 | 수정 | 9 |
| `src/ReSet.Core/Services/TaskFileComposer.cs` | `AppendBootstrap`에 `RenderDdl()` 주입 | 수정 | 10 |
| `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs` | 실물 코퍼스 골든 케이스 | 신규 | 11 |

**태스크 경계의 근거:** 1·2는 재료(각자 단위 테스트로 닫힌다). 3·4는 프롬프트(3은 배선, 4는 문구 — 리뷰어가 한쪽만 반려할 수 있다). 5~8은 검사를 결함 종류별로 갈랐다 — 각 검사가 독립적으로 red-green을 돌고 잘못 설계된 하나가 나머지를 막지 않는다. 9는 배선, 10은 부트스트랩 DDL, 11은 전체를 실물 코퍼스로 고정한다.

---

### Task 1: `BatchControlContract` — M1 정본

**Files:**
- Create: `src/ReSet.Core/Services/BatchControlContract.cs`
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: 없음(고정 자산)
- Produces:
  - `record ControlColumn(string Name, string SqlType, bool Nullable, IReadOnlyList<string>? AllowedValues)`
  - `enum ControlRowOrigin { FirstStepInserts, EachStepInserts, ProducerInsertsOnly }`
  - `record ControlTable(string Name, IReadOnlyList<ControlColumn> Columns, ControlRowOrigin Origin, string? StatusColumn)`
  - `static class BatchControlContract` — `IReadOnlyList<ControlTable> Tables`, `ControlTable? Find(string name)`, `string RenderDdl()`, `string RenderPromptTable()`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchControlContractTests.cs`:

```csharp
using System;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class BatchControlContractTests
{
    [Fact]
    public void Tables_CoverTheFourControlTables()
    {
        var names = BatchControlContract.Tables.Select(t => t.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "batch.BatchRun",
                "batch.BatchStepJournal",
                "batch.BatchCheckpoint",
                "batch.BatchValidationIssue"
            },
            names);
    }

    // 감사 실측: 같은 저널을 S01은 StepStatus='Succeeded', S02는
    // ExecutionStatus='Completed', S03은 StepStatus='Completed'로 썼다.
    // 성공 종료 어휘가 하나가 아니면 모든 재시작이 차단된다.
    [Fact]
    public void SuccessVocabulary_IsSucceededEverywhere_AndNeverCompleted()
    {
        foreach (var table in BatchControlContract.Tables)
        {
            if (table.StatusColumn == null) continue;

            var status = table.Columns.Single(c => c.Name == table.StatusColumn);
            Assert.NotNull(status.AllowedValues);
            Assert.DoesNotContain("Completed", status.AllowedValues!);
        }

        var journal = BatchControlContract.Find("batch.BatchStepJournal")!;
        var journalStatus = journal.Columns.Single(c => c.Name == journal.StatusColumn);
        Assert.Contains("Succeeded", journalStatus.AllowedValues!);
    }

    [Fact]
    public void StatusColumnName_FollowsTheTargetStatusRule()
    {
        Assert.Equal("RunStatus", BatchControlContract.Find("batch.BatchRun")!.StatusColumn);
        Assert.Equal("StepStatus", BatchControlContract.Find("batch.BatchStepJournal")!.StatusColumn);
        Assert.Equal("CheckpointStatus", BatchControlContract.Find("batch.BatchCheckpoint")!.StatusColumn);
    }

    // 감사 실측 B3: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다.
    // 행 소유권이 계약에 없으면 모든 단계가 UPDATE만 쓴다.
    [Fact]
    public void RowOrigin_IsDeclaredForEveryTable()
    {
        Assert.Equal(ControlRowOrigin.FirstStepInserts, BatchControlContract.Find("batch.BatchRun")!.Origin);
        Assert.Equal(ControlRowOrigin.EachStepInserts, BatchControlContract.Find("batch.BatchStepJournal")!.Origin);
        Assert.Equal(ControlRowOrigin.EachStepInserts, BatchControlContract.Find("batch.BatchCheckpoint")!.Origin);
        Assert.Equal(ControlRowOrigin.ProducerInsertsOnly, BatchControlContract.Find("batch.BatchValidationIssue")!.Origin);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndAcceptsTheBareName()
    {
        Assert.NotNull(BatchControlContract.Find("BATCH.BATCHRUN"));
        Assert.NotNull(BatchControlContract.Find("BatchRun"));
        Assert.Null(BatchControlContract.Find("dbo.TSettleMst"));
    }

    // 부트스트랩 회차 문서가 실을 DDL. 감사 §6-4: 다섯 테이블의 컬럼
    // 정의가 번들 어디에도 없었다.
    [Fact]
    public void RenderDdl_EmitsCreateTableForEveryTable_WithAConstraintOnTheStatusVocabulary()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("CREATE TABLE batch.BatchRun", ddl);
        Assert.Contains("CREATE TABLE batch.BatchStepJournal", ddl);
        Assert.Contains("CREATE TABLE batch.BatchCheckpoint", ddl);
        Assert.Contains("CREATE TABLE batch.BatchValidationIssue", ddl);
        Assert.Contains("CHECK (StepStatus IN (N'Running', N'Succeeded', N'Failed', N'Skipped'))", ddl);
    }

    [Fact]
    public void RenderPromptTable_NamesEveryColumnAndTheRowOrigin()
    {
        var table = BatchControlContract.RenderPromptTable();

        Assert.Contains("StartedAtUtc", table);
        Assert.Contains("ErrorMessage", table);
        Assert.Contains("JobName", table);
        // 행 생성 소유권이 프롬프트에 실려야 B3가 닫힌다.
        Assert.Contains("INSERT", table);
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: 컴파일 실패 — `BatchControlContract`, `ControlRowOrigin` 형식을 찾을 수 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/BatchControlContract.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>제어 테이블 컬럼 하나. AllowedValues가 있으면 그것이 상태 어휘 전부다.</summary>
    public sealed record ControlColumn(
        string Name,
        string SqlType,
        bool Nullable,
        IReadOnlyList<string>? AllowedValues = null);

    /// <summary>
    /// 제어 행을 누가 만드는가.
    ///
    /// 이 축이 계약에 있어야 하는 이유: 실측에서 INSERT INTO batch.BatchRun이
    /// 번들 전체에 0건이었고 S03·S06·S17이 자기 저널·체크포인트 행을 만드는
    /// 지점 없이 UPDATE만 했다. @@ROWCOUNT 검사가 있는 곳은 정상 실행에서도
    /// 상시 실패하고, 없는 곳은 0행 갱신을 오류 없이 지나간다.
    /// </summary>
    public enum ControlRowOrigin
    {
        /// <summary>단계 목록의 첫 단계가 INSERT하며 RunId를 발급한다.</summary>
        FirstStepInserts,

        /// <summary>각 단계가 시작 시 자기 행을 INSERT한 뒤 종료 시 UPDATE한다.</summary>
        EachStepInserts,

        /// <summary>생산 단계가 INSERT만 한다. 전이가 없다.</summary>
        ProducerInsertsOnly
    }

    /// <param name="StatusColumn">상태 어휘를 담은 컬럼. 없으면 null.</param>
    public sealed record ControlTable(
        string Name,
        IReadOnlyList<ControlColumn> Columns,
        ControlRowOrigin Origin,
        string? StatusColumn);

    /// <summary>
    /// 배치 실행 제어 테이블의 정본.
    ///
    /// [왜 ReSet이 정하는가]
    /// 배치 골격에는 레거시 원본이 없다. 원본에서 추출할 수 있는 사실이 아니므로
    /// 누군가는 정해야 하는데, 지금까지 아무도 정하지 않았다. 그 결과 단계 18개가
    /// 각각 독립된 LLM 호출이라 같은 batch.BatchStepJournal에 대해 S01은
    /// StepStatus='Succeeded'를, S02는 ExecutionStatus='Completed'를, S17은
    /// StepState를, integrity-sql.md는 j.Status를 썼다. 어느 쪽으로 DDL을 만들어도
    /// 반대편 단계가 컴파일되지 않는다.
    ///
    /// DataAccessPolicy가 생성 번들의 계약 자산을 단독 소유하는 것과 같은 패턴이다.
    /// 계약 문구를 조립 코드에서 다시 쓰지 마십시오 - 테스트가 닿지 않는 계약이 된다.
    ///
    /// [왜 Completed를 버리는가]
    /// 성공 종료 어휘가 Succeeded와 Completed 둘로 갈리면
    /// CP.CheckpointStatus='Completed' AND SJ.ExecutionStatus&lt;&gt;'Completed' 같은
    /// 대조가 정상 성공한 단계에서 참이 되어 모든 재시작이 차단된다. 규칙을 하나로
    /// 만드는 것이 어느 쪽을 고르는가보다 중요하다.
    ///
    /// [담지 않는 것]
    /// BatchSourceWatermark와 BatchImmutableLedgerBaseline은 어느 원천을 워터마킹하고
    /// 어느 원장을 기준선으로 잡는지에 따라 컬럼이 달라지는 Job 형상 객체다. ReSet이
    /// 정할 수 있는 사실이 아니므로 스키마·명명 규칙만 적용하고 DDL은 계획서에 맡긴다.
    /// </summary>
    public static class BatchControlContract
    {
        private static readonly string[] RunStates = { "Running", "Succeeded", "Failed", "Restarting" };
        private static readonly string[] StepStates = { "Running", "Succeeded", "Failed", "Skipped" };
        private static readonly string[] CheckpointStates = { "Pending", "Succeeded" };

        public static IReadOnlyList<ControlTable> Tables { get; } = new[]
        {
            new ControlTable(
                "batch.BatchRun",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("JobName", "nvarchar(128)", false),
                    new ControlColumn("BatchYmd", "varchar(8)", false),
                    new ControlColumn("RunStatus", "nvarchar(20)", false, RunStates),
                    new ControlColumn("ResumeFromStepCode", "nvarchar(10)", true),
                    new ControlColumn("StartedAtUtc", "datetime2(3)", false),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true),
                    new ControlColumn("ErrorMessage", "nvarchar(max)", true)
                },
                ControlRowOrigin.FirstStepInserts,
                "RunStatus"),

            new ControlTable(
                "batch.BatchStepJournal",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("StepStatus", "nvarchar(20)", false, StepStates),
                    new ControlColumn("LegacyReturnCode", "int", true),
                    new ControlColumn("StartedAtUtc", "datetime2(3)", false),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true),
                    new ControlColumn("ErrorMessage", "nvarchar(max)", true)
                },
                ControlRowOrigin.EachStepInserts,
                "StepStatus"),

            new ControlTable(
                "batch.BatchCheckpoint",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("CheckpointStatus", "nvarchar(20)", false, CheckpointStates),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true)
                },
                ControlRowOrigin.EachStepInserts,
                "CheckpointStatus"),

            new ControlTable(
                "batch.BatchValidationIssue",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("IssueCode", "nvarchar(64)", false),
                    new ControlColumn("Severity", "nvarchar(20)", false,
                        new[] { "Info", "Warning", "Error", "Critical" }),
                    new ControlColumn("ExpectedValue", "nvarchar(200)", true),
                    new ControlColumn("ActualValue", "nvarchar(200)", true),
                    new ControlColumn("DetectedAtUtc", "datetime2(3)", false)
                },
                ControlRowOrigin.ProducerInsertsOnly,
                "Severity")
        };

        /// <summary>
        /// 한정자가 있든 없든, 대소문자가 어떻든 찾는다. 단계 문서는 같은 테이블을
        /// batch.BatchRun으로도 BatchRun으로도 쓴다 - 한쪽만 인식하면 검사가 절반만 돈다.
        /// </summary>
        public static ControlTable? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var bare = BareName(name);
            return Tables.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BareName(t.Name), bare, StringComparison.OrdinalIgnoreCase));
        }

        private static string BareName(string name)
        {
            var idx = name.LastIndexOf('.');
            return idx >= 0 ? name[(idx + 1)..] : name;
        }

        /// <summary>회차 0 부트스트랩 문서가 실을 실제 DDL.</summary>
        public static string RenderDdl()
        {
            var sb = new StringBuilder();

            foreach (var table in Tables)
            {
                sb.AppendLine($"CREATE TABLE {table.Name}");
                sb.AppendLine("(");

                var lines = new List<string>();
                foreach (var col in table.Columns)
                {
                    var nullability = col.Nullable ? "NULL" : "NOT NULL";
                    lines.Add($"    {col.Name} {col.SqlType} {nullability}");
                }

                foreach (var col in table.Columns.Where(c => c.AllowedValues is { Count: > 0 }))
                {
                    var values = string.Join(", ", col.AllowedValues!.Select(v => $"N'{v}'"));
                    lines.Add($"    CONSTRAINT CK_{BareName(table.Name)}_{col.Name} " +
                              $"CHECK ({col.Name} IN ({values}))");
                }

                sb.AppendLine(string.Join(",\n", lines));
                sb.AppendLine(");");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        /// <summary>단계 프롬프트가 실을 계약 표.</summary>
        public static string RenderPromptTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Table | Column | Type | Allowed values | Row origin |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var table in Tables)
            {
                var origin = table.Origin switch
                {
                    ControlRowOrigin.FirstStepInserts =>
                        "The FIRST step in the step list INSERTs this row and issues RunId. Later steps UPDATE it.",
                    ControlRowOrigin.EachStepInserts =>
                        "EACH step INSERTs its own row when it starts, then UPDATEs it when it ends. Never UPDATE a row you did not insert.",
                    _ => "The producing step INSERTs only. There is no state transition."
                };

                foreach (var col in table.Columns)
                {
                    var values = col.AllowedValues is { Count: > 0 }
                        ? string.Join(" / ", col.AllowedValues)
                        : "-";
                    var nullability = col.Nullable ? "" : " NOT NULL";
                    sb.AppendLine(
                        $"| `{table.Name}` | `{col.Name}` | {col.SqlType}{nullability} | {values} | {origin} |");
                }
            }

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: 7 passed

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "feat: 배치 제어 테이블의 정본 계약을 세운다

같은 batch.BatchStepJournal에 대해 S01은 StepStatus='Succeeded'를,
S02는 ExecutionStatus='Completed'를, S17은 StepState를 썼다. 단계 18개가
각각 독립된 LLM 호출이라 정본이 없으면 각자 지어낸다. 성공 종료 어휘를
Succeeded 하나로 정하고 행 생성 소유권을 계약에 담는다."
```

---

### Task 2: `StepInterfaceFacts` — M2 원본 파라미터 표

**Files:**
- Create: `src/ReSet.Core/Services/StepInterfaceFacts.cs`
- Test: `tests/ReSet.Core.Tests/StepInterfaceFactsTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan`(기존 record — `Code`, `Name`, `LegacyProcedures`, `TargetTables`, `ErrorCodes`, `Chunkable`, `SchemaTables`), `SpDefinition.StaticAnalysis`(형식은 `SpStaticAnalysisResult`, **널 아님** — `= new()`로 초기화된다), `SpStaticAnalysisResult.ProcedureParameters`(`List<string>`, 원소는 `"@pi_strYMD varchar(8)"` 형태)
- Produces:
  - `record StepInterface(string StepCode, IReadOnlyList<string> Procedures, IReadOnlyList<string> Parameters)`
  - `static class StepInterfaceFacts`:
    - `IReadOnlyDictionary<string, IReadOnlyList<string>> CollectParameters(IReadOnlyList<SpDefinition>? definitions)`
    - `IReadOnlyList<StepInterface> Build(IReadOnlyList<BatchStepPlan>? steps, IReadOnlyDictionary<string, IReadOnlyList<string>>? parametersByProcedure)`
    - `string RenderPromptTable(IReadOnlyList<StepInterface> interfaces)`
    - `IReadOnlyList<string> ParameterNames(StepInterface iface)`

**왜 조달을 둘로 가르는가:** 오케스트레이터에서 `definitions`가 있는 지점(`:1694`)에는 `steps`가 아직 없고, `steps`가 있는 지점(`GenerateBySplitAsync`)에는 `definitions`가 없다. `knownTableNames`가 이미 `:1694`에서 만들어져 아래로 실려 내려가는 것과 똑같은 형태로 맞춘다 — `CollectParameters`가 `:1694`에서 돌고, `Build`는 `steps`가 있는 곳에서 돈다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepInterfaceFactsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class StepInterfaceFactsTests
{
    private static BatchStepPlan Step(string code, params string[] legacy) => new(
        Code: code,
        Name: $"{code} 단계",
        LegacyProcedures: legacy,
        TargetTables: Array.Empty<string>(),
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    private static SpDefinition Definition(string schema, string name, params string[] parameters)
    {
        var def = new SpDefinition { Schema = schema, Name = name };
        def.StaticAnalysis = new SpStaticAnalysisResult();
        def.StaticAnalysis.ProcedureParameters.AddRange(parameters);
        return def;
    }

    private static IReadOnlyList<StepInterface> BuildFrom(
        IReadOnlyList<BatchStepPlan> steps, params SpDefinition[] defs) =>
        StepInterfaceFacts.Build(steps, StepInterfaceFacts.CollectParameters(defs));

    [Fact]
    public void CollectParameters_KeysByBothTheBareAndTheQualifiedName()
    {
        var map = StepInterfaceFacts.CollectParameters(
            new[] { Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)") });

        Assert.True(map.ContainsKey("UP_UTIL_SETTLE_INS"));
        Assert.True(map.ContainsKey("dbo.UP_UTIL_SETTLE_INS"));
    }

    // 정적 분석이 파라미터를 내지 않았으면 재료가 없는 것이다. 빈 목록을
    // 사실로 내보내면 검사가 그 단계의 모든 파라미터를 결함으로 든다.
    [Fact]
    public void CollectParameters_OmitsAProcedureThatDeclaredNoParameters()
    {
        var map = StepInterfaceFacts.CollectParameters(
            new[] { new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_INS" } });

        Assert.Empty(map);
    }

    [Fact]
    public void Build_MapsEachStepToItsLegacyProcedureParameters()
    {
        var iface = Assert.Single(BuildFrom(
            new[] { Step("S05", "dbo.UP_UTIL_SETTLE_INS") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT")));

        Assert.Equal("S05", iface.StepCode);
        Assert.Equal(new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }, iface.Parameters);
    }

    // 신설 단계는 원본이 없다. 행을 만들면 "파라미터 0개"가 사실처럼 보인다.
    [Fact]
    public void Build_SkipsStepsThatHaveNoLegacyProcedure()
    {
        var built = BuildFrom(
            new[] { Step("S01"), Step("S05", "dbo.UP_UTIL_SETTLE_INS") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)"));

        Assert.Single(built, i => i.StepCode == "S05");
        Assert.DoesNotContain(built, i => i.StepCode == "S01");
    }

    [Fact]
    public void Build_MatchesTheProcedureNameCaseInsensitivelyAndBare()
    {
        Assert.Single(BuildFrom(
            new[] { Step("S05", "UP_util_settle_ins") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)")));
    }

    [Fact]
    public void Build_MergesParametersWhenAStepConsumesTwoProcedures()
    {
        var iface = Assert.Single(BuildFrom(
            new[] { Step("S12", "dbo.UP_Util_Settle_Summary", "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA") },
            Definition("dbo", "UP_Util_Settle_Summary", "@pi_strYMD varchar(8)"),
            Definition("dbo", "UP_UTIL_SETTLE_SUMMARY_EXTRA", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT")));

        // 같은 파라미터는 한 번만. 두 SP가 @pi_strYMD를 공유한다.
        Assert.Equal(new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }, iface.Parameters);
    }

    [Fact]
    public void Build_ReturnsNothingWhenThereIsNoMaterial()
    {
        Assert.Empty(StepInterfaceFacts.Build(new[] { Step("S05", "dbo.X") }, null));
    }

    [Fact]
    public void ParameterNames_StripsTheTypeAndKeepsTheAtSign()
    {
        var iface = new StepInterface("S05", new[] { "dbo.X" },
            new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" });

        Assert.Equal(new[] { "@pi_strYMD", "@po_intRetVal" }, StepInterfaceFacts.ParameterNames(iface));
    }

    [Fact]
    public void RenderPromptTable_ListsEveryStepAndItsParameters()
    {
        var table = StepInterfaceFacts.RenderPromptTable(new[]
        {
            new StepInterface("S05", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" })
        });

        Assert.Contains("S05", table);
        Assert.Contains("@pi_strYMD varchar(8)", table);
        Assert.Contains("@po_intRetVal int OUTPUT", table);
    }

    // 캐시 불변성(설계 §4). 어느 단계를 만들든 같은 표가 실려야 한다.
    [Fact]
    public void RenderPromptTable_IsIndependentOfWhichStepIsBeingGenerated()
    {
        var interfaces = new[]
        {
            new StepInterface("S05", new[] { "dbo.A" }, new[] { "@pi_strYMD varchar(8)" }),
            new StepInterface("S08", new[] { "dbo.B" }, new[] { "@pi_strYMD varchar(8)" })
        };

        Assert.Equal(
            StepInterfaceFacts.RenderPromptTable(interfaces),
            StepInterfaceFacts.RenderPromptTable(interfaces));
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepInterfaceFactsTests"`
Expected: 컴파일 실패 — `StepInterfaceFacts`, `StepInterface` 형식을 찾을 수 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/StepInterfaceFacts.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Parameters">원본 선언 그대로. "@pi_strYMD varchar(8)" 형태다.</param>
    public sealed record StepInterface(
        string StepCode,
        IReadOnlyList<string> Procedures,
        IReadOnlyList<string> Parameters);

    /// <summary>
    /// 단계별 원본 프로시저 인터페이스를 모은다.
    ///
    /// [새 추출기를 만들지 않는 이유]
    /// SqlStaticParser가 ProcedureParameters로 이미 확정하고 있다. 문제는 이 사실이
    /// Job 단계 프롬프트에 실리지 않는다는 것뿐이었다 - AppendSharedStepContext는
    /// jobName·targetLanguage·specs·conventions만 날랐다. 18번의 호출이 원본
    /// 인터페이스에 대한 기계 사실을 하나도 못 받은 채, ConsolidatedPlanRules 규칙 5는
    /// "@pi_bypassPreCheck 파라미터를 제공하라"고 명령했다. 산출물이 원본에 없는 입력을
    /// 지어낸 것이 아니라 프롬프트가 그 이름까지 적어 시켰다.
    ///
    /// [조달을 둘로 가르는 이유]
    /// 오케스트레이터에서 definitions가 있는 지점에는 steps가 아직 없고, steps가 있는
    /// 지점에는 definitions가 없다. CollectParameters가 knownTableNames와 같은 자리에서
    /// 돌아 아래로 실려 내려가고, Build는 steps가 있는 곳에서 돈다.
    ///
    /// [파라미터가 없는 프로시저를 담지 않는 이유]
    /// 정적 분석이 실패했거나 파라미터가 없으면 재료가 없는 것이다. 빈 목록을 사실로
    /// 내보내면 검사가 그 단계의 모든 파라미터를 결함으로 든다. 담지 않으면 소프트 스킵한다.
    /// </summary>
    public static class StepInterfaceFacts
    {
        /// <summary>프로시저 맨이름과 한정명 양쪽으로 찾을 수 있게 담는다.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectParameters(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null) return map;

            foreach (var def in definitions)
            {
                if (def?.Name == null) continue;

                var declared = def.StaticAnalysis.ProcedureParameters;
                if (declared.Count == 0) continue;

                var snapshot = declared.ToList();
                map[def.Name] = snapshot;
                map[$"{def.Schema}.{def.Name}"] = snapshot;
            }

            return map;
        }

        public static IReadOnlyList<StepInterface> Build(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? parametersByProcedure)
        {
            if (steps == null || steps.Count == 0 ||
                parametersByProcedure == null || parametersByProcedure.Count == 0)
            {
                return Array.Empty<StepInterface>();
            }

            var result = new List<StepInterface>();

            foreach (var step in steps)
            {
                var procedures = new List<string>();
                var parameters = new List<string>();

                foreach (var legacy in step.LegacyProcedures ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(legacy)) continue;

                    if (!parametersByProcedure.TryGetValue(legacy, out var declared) &&
                        !parametersByProcedure.TryGetValue(BareName(legacy), out declared))
                    {
                        continue;
                    }

                    procedures.Add(legacy);
                    foreach (var p in declared)
                    {
                        if (!parameters.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            parameters.Add(p);
                        }
                    }
                }

                if (parameters.Count > 0)
                {
                    result.Add(new StepInterface(step.Code, procedures, parameters));
                }
            }

            return result;
        }

        /// <summary>"@pi_strYMD varchar(8)" -&gt; "@pi_strYMD".</summary>
        public static IReadOnlyList<string> ParameterNames(StepInterface iface)
        {
            var names = new List<string>();
            foreach (var declaration in iface.Parameters)
            {
                var trimmed = declaration.Trim();
                var space = trimmed.IndexOf(' ');
                names.Add(space > 0 ? trimmed[..space] : trimmed);
            }
            return names;
        }

        /// <summary>
        /// 단계 프롬프트가 실을 표.
        ///
        /// 어느 단계를 생성하든 전 단계 표를 통째로 싣는다. 단계별로 자기 것만
        /// 실으면 공유 접두사가 매 호출 달라져 프롬프트 캐시가 전부 미스가 되고,
        /// 입력 토큰이 1배에서 18배로 뛴다 - 산출물은 그대로라 코드만 봐서는
        /// 알 수 없는 실패다(architecture.md §4.13).
        /// </summary>
        public static string RenderPromptTable(IReadOnlyList<StepInterface> interfaces)
        {
            if (interfaces == null || interfaces.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("| Step | Legacy procedure | Parameters (this list is exhaustive) |");
            sb.AppendLine("|---|---|---|");

            foreach (var iface in interfaces)
            {
                sb.AppendLine(
                    $"| {iface.StepCode} | {string.Join(", ", iface.Procedures)} | " +
                    $"{string.Join(" · ", iface.Parameters)} |");
            }

            return sb.ToString();
        }

        private static string BareName(string name)
        {
            var idx = name.LastIndexOf('.');
            return idx >= 0 ? name[(idx + 1)..] : name;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepInterfaceFactsTests"`
Expected: 10 passed

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/StepInterfaceFacts.cs tests/ReSet.Core.Tests/StepInterfaceFactsTests.cs
git commit -m "feat: 단계별 원본 파라미터를 재료로 모은다

SqlStaticParser가 ProcedureParameters로 이미 확정하는 사실인데 Job 단계
프롬프트에 실리지 않았다. 18번의 호출이 원본 인터페이스에 대한 기계 사실을
못 받은 채 규칙 5는 @pi_bypassPreCheck를 제공하라고 명령했다.
조달을 둘로 가른 이유는 definitions가 있는 지점에 steps가 없기 때문이다 -
knownTableNames가 실려 내려가는 것과 같은 형태로 맞춘다."
```

---

### Task 3: 두 재료를 공유 접두사에 배선

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` — `AppendSharedStepContext`(`:2797`), `GenerateBatchStepSectionAsync`(`:2726`)
- Modify: `src/ReSet.Core/Services/IAiService.cs`
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: `BatchControlContract.RenderPromptTable()`(Task 1), `StepInterfaceFacts.RenderPromptTable(...)`(Task 2)
- Produces: `GenerateBatchStepSectionAsync(BatchStepPlan step, IReadOnlyList<BatchStepPlan> allSteps, string sharedConventions, List<(string FileName, string Content)> specs, IReadOnlyList<StepInterface> stepInterfaces, string targetLanguage, string jobName, string? effort = null, string? floorFeedback = null, CancellationToken cancellationToken = default)` — `stepInterfaces`가 `specs` 뒤·`targetLanguage` 앞에 들어간다. `IAiService`도 같이 바뀐다.

**테스트 관용:** 이 파일은 `CreateProbe()`가 `MockHttpMessageHandler` + `OpenAiClient`로 `AiService`를 만들고, 프롬프트는 `result.SystemPrompt` / `result.UserPrompt`로 단언한다(`:185-190`, `:181-182`). 그 관용을 그대로 쓴다 — `InternalsVisibleTo`는 이 프로젝트에 없고, 테스트 전용 접근자를 새로 만들지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`의 클래스 안에 더한다.

```csharp
        private static BatchStepPlan ProbeStep(string code) => new(
            Code: code,
            Name: $"{code} 단계",
            LegacyProcedures: Array.Empty<string>(),
            TargetTables: Array.Empty<string>(),
            ErrorCodes: Array.Empty<string>(),
            Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private static readonly List<(string FileName, string Content)> ProbeSpecs =
            new() { ("Spec.md", "# 명세서") };

        // 설계 §4. 어느 단계를 만들든 공유 접두사는 바이트 동일해야 한다.
        // 달라지면 프롬프트 캐시가 전부 미스가 되어 입력 토큰이 18배가 되는데
        // 산출물은 그대로라 코드만 봐서는 알 수 없다.
        [Fact]
        public async Task GenerateBatchStepSection_SharedPrefixIsIdenticalAcrossSteps()
        {
            var steps = new[] { ProbeStep("S05"), ProbeStep("S08") };
            var interfaces = new[]
            {
                new StepInterface("S05", new[] { "dbo.A" }, new[] { "@pi_strYMD varchar(8)" })
            };

            var first = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");
            var second = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[1], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");

            const string marker = "Now write the section";
            Assert.Equal(
                first.UserPrompt.Substring(0, first.UserPrompt.IndexOf(marker, StringComparison.Ordinal)),
                second.UserPrompt.Substring(0, second.UserPrompt.IndexOf(marker, StringComparison.Ordinal)));
        }

        [Fact]
        public async Task GenerateBatchStepSection_CarriesTheControlContractTable()
        {
            var steps = new[] { ProbeStep("S05") };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");

            Assert.Contains("batch.BatchStepJournal", result.UserPrompt);
            Assert.Contains("StepStatus", result.UserPrompt);
            Assert.Contains("Succeeded", result.UserPrompt);
            Assert.Contains("Do NOT invent alternatives", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateBatchStepSection_CarriesTheStepInterfaceTable()
        {
            var steps = new[] { ProbeStep("S05") };
            var interfaces = new[]
            {
                new StepInterface("S05", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                    new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" })
            };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs, interfaces, "C#", "Job");

            Assert.Contains("@po_intRetVal int OUTPUT", result.UserPrompt);
            Assert.Contains("MUST NOT add", result.UserPrompt);
        }

        // 재료가 없으면 표 절 자체를 넣지 않는다. 빈 표를 넣으면 모델이
        // "원본 파라미터가 없다"로 읽는다.
        [Fact]
        public async Task GenerateBatchStepSection_OmitsTheInterfaceSectionWhenThereIsNoMaterial()
        {
            var steps = new[] { ProbeStep("S05") };

            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");

            Assert.DoesNotContain("[Original Procedure Interface]", result.UserPrompt);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~GenerateBatchStepSection"`
Expected: 컴파일 실패 — `GenerateBatchStepSectionAsync`가 `stepInterfaces` 인자를 갖지 않음

- [ ] **Step 3: 최소 구현을 쓴다**

`AppendSharedStepContext`의 시그니처에 `IReadOnlyList<StepInterface> stepInterfaces`를 더하고, 본문 맨 끝(specs 블록 뒤)에 두 절을 붙인다:

```csharp
            builder.AppendLine();
            builder.AppendLine("[Batch Control Table Contract]");
            builder.AppendLine("These four tables are FIXED. Use exactly these column names and status values.");
            builder.AppendLine("Do NOT invent alternatives such as ExecutionStatus, StepState, CompletionStatus,");
            builder.AppendLine("BatchJobName, StartedAt, or DetailMessage. NEVER use the status value 'Completed' -");
            builder.AppendLine("success is 'Succeeded' everywhere. If two steps spell one logical table differently,");
            builder.AppendLine("no single DDL satisfies both and restart is blocked on every run.");
            builder.AppendLine();
            builder.Append(BatchControlContract.RenderPromptTable());

            var interfaceTable = StepInterfaceFacts.RenderPromptTable(stepInterfaces);
            if (interfaceTable.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[Original Procedure Interface]");
                builder.AppendLine("The parameter list below is EXHAUSTIVE for each step. You MUST NOT add an input");
                builder.AppendLine("parameter that is not listed - not for restart, not for skipping, not for");
                builder.AppendLine("bypassing a guard. Steps whose code is absent from this table have no legacy");
                builder.AppendLine("origin, so design their interface from the plan structure instead.");
                builder.AppendLine();
                builder.Append(interfaceTable);
            }
```

`GenerateBatchStepSectionAsync`와 `IAiService`의 시그니처에 같은 인자를 더하고 그대로 넘긴다.

골격 생성 경로의 호출(`AppendSharedStepContext(userPrompt, steps, string.Empty, specs, ...)`)에는 `Array.Empty<StepInterface>()`를 넘긴다 — 골격은 단계 본문을 쓰지 않으므로 재료가 필요 없고, 넣으면 골격 프롬프트만 바뀌어 캐시 접두사가 어긋난다.

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet build 2>&1 | grep -c "warning" && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AiServiceTests_Rich"`
Expected: 경고 9 이하, 테스트 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/IAiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "feat: 제어 계약과 원본 인터페이스 표를 단계 프롬프트에 싣는다

두 표 모두 공유 접두사에 넣는다. 단계별로 자기 것만 실으면 접두사가 매
호출 달라져 캐시가 전부 미스가 되고 입력 토큰이 18배가 된다.
재료가 없으면 표 절 자체를 넣지 않는다 - 빈 표는 모델이 '원본 파라미터가
없다'로 읽는다."
```

---

### Task 4: `ConsolidatedPlanRules` 수술

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:1026-1105` (`ConsolidatedPlanRules`)
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: Task 3의 7인자 `GenerateBatchStepSectionAsync`와 Task 3이 같은 테스트 파일에 만든 헬퍼 `ProbeStep(string)` · `ProbeSpecs` · 기존 `CreateProbe()`
- Produces: 없음(프롬프트 문구만 바뀐다)

**왜 `SystemPrompt`로 단언하는가:** `ConsolidatedPlanRules`는 `private const`지만 `GenerateBatchStepSectionAsync`가 `systemPrompt`에 이어 붙이고 그 값이 `aiResult.SystemPrompt`로 공개된다(`AiService.cs:2748`). 이 프로젝트에는 `InternalsVisibleTo`가 없으므로 접근자를 새로 만들지 말고 이 경로로 읽는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        private static async Task<string> StepSystemPromptAsync()
        {
            var steps = new[] { ProbeStep("S05") };
            var result = await CreateProbe().Service.GenerateBatchStepSectionAsync(
                steps[0], steps, "conventions", ProbeSpecs,
                Array.Empty<StepInterface>(), "C#", "Job");
            return result.SystemPrompt;
        }

        // 규칙 5가 @pi_bypassPreCheck를 발명해 명령했고, S02가 재시작 모드에서
        // 실행 컨텍스트 전체에 그 값을 참으로 고정해 지급 확정 원장의 -9 하드
        // 스톱이 통째로 사라졌다(감사 🔴).
        [Fact]
        public async Task ConsolidatedPlanRules_DoNotInventABypassParameter()
        {
            Assert.DoesNotContain("@pi_bypassPreCheck", await StepSystemPromptAsync());
        }

        [Fact]
        public async Task ConsolidatedPlanRules_MoveRestartSkipOutsideTheStep()
        {
            var rules = await StepSystemPromptAsync();

            Assert.Contains("orchestrator", rules, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MUST NOT add an input parameter", rules);
            Assert.Contains("unconditionally", rules);
        }

        // Few-Shot의 CATCH가 THROW로 끝나 규칙 6-1(상태 변수를 반환하라)과
        // 규칙 13(출력 파라미터를 누락 없이 매핑하라)을 무력화했다. 모델은
        // 산문 규칙보다 코드 예시를 따른다 - 실측 5건이 그렇게 나왔다.
        [Fact]
        public async Task ConsolidatedPlanRules_FewShotCatchReturnsInsteadOfRethrowing()
        {
            var rules = await StepSystemPromptAsync();
            var open = rules.IndexOf("BEGIN CATCH", StringComparison.Ordinal);
            var close = rules.IndexOf("END CATCH", open, StringComparison.Ordinal);
            var catchBlock = rules[open..close];

            Assert.DoesNotContain("THROW;", catchBlock);
            Assert.Contains("RETURN", catchBlock);
        }

        [Fact]
        public async Task ConsolidatedPlanRules_MakeShadowALastResortWithThreeMechanics()
        {
            var rules = await StepSystemPromptAsync();

            Assert.Contains("LAST RESORT", rules);
            Assert.Contains("BEFORE `BEGIN TRAN`", rules);
            Assert.Contains("same range", rules);
            Assert.Contains("sp_executesql", rules);
        }

        [Fact]
        public async Task ConsolidatedPlanRules_ForbidCrossJoinInReconciliationSql()
        {
            Assert.Contains(
                "NEVER compare two aggregates with `CROSS JOIN`", await StepSystemPromptAsync());
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ConsolidatedPlanRules"`
Expected: 5 failed — `@pi_bypassPreCheck`가 아직 있고 `THROW;`가 CATCH 안에 있음

- [ ] **Step 3: 최소 구현을 쓴다**

규칙 5(`:1038`)를 통째로 바꾼다:

```
5. [Idempotency & Restartability] Restart skipping happens OUTSIDE the step. The orchestrator reads `batch.BatchCheckpoint` and simply does not call a step whose checkpoint is already `Succeeded`. Therefore a step MUST NOT add an input parameter for restart, skipping, or bypassing - its interface is exactly the parameter list given in the `[Original Procedure Interface]` table. The original pre-validation guards (for example a `-9` abort when a settled ledger row exists) MUST run unconditionally on every call; NEVER place them inside a conditional a caller can switch off. A step that is called is a step that does its full work, guards included.
```

규칙 4(`:1036`)를 판정 트리로 바꾼다:

```
4. [Transaction Isolation & Shadow Table] NEVER propose `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON` as it is too risky. Use session-level `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`. Shadow tables are a LAST RESORT, not a default: if the step's work fits in a single transaction, use `ROLLBACK TRAN` alone and write NO shadow table and NO compensating DELETE in the CATCH block - the rollback has already restored those rows, so deleting them again in an auto-committed CATCH destroys data that was never lost. Only when the step commits in chunks or rebuilds an aggregate (so a rollback cannot restore it) may you use a shadow, and then all three mechanics are mandatory: (a) create the shadow BEFORE `BEGIN TRAN` - a `SELECT INTO` issued inside the transaction disappears with the rollback and the restore then fails on a missing object; (b) the restore MUST delete exactly the same range the step deleted - NEVER `DELETE FROM Target` without a `WHERE`, which discards rows belonging to other business dates; (c) NEVER reference an outer batch variable inside `EXEC()` - a dynamic batch is a separate scope and fails with an undeclared-variable error; pass values as `sp_executesql` parameters instead.
```

규칙 11(`:1047`)은 규칙 4에 흡수됐으므로 짧게 남긴다:

```
11. [INSERT-only Rollback] For INSERT-only steps, rely on `ROLLBACK TRAN` for single transactions or an explicit `DELETE WHERE [ChunkKey]` compensation for chunked ones. See rule 4 - no shadow table.
```

Few-Shot의 CATCH(`:1089-1095`)를 고친다:

```sql
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    -- Restore only when a shadow was actually captured (rule 4). Delete the SAME range first.
    IF @v_shadowCaptured = 1
    BEGIN
        DELETE FROM dbo.TargetTable WHERE BatchDate = @BatchDate;
        INSERT INTO dbo.TargetTable SELECT * FROM batch_shadow.TargetTable_RunId_S13 WHERE BatchDate = @BatchDate;
    END
    -- Return the tracked code. Do NOT `THROW` here: it unwinds past the caller's
    -- OUTPUT parameter assignment and the original return code is lost (rules 6-1 and 13).
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH
```

규칙 2(`:1028`)의 검증 SQL 항 아래에 한 줄을 더한다:

```
     * NEVER compare two aggregates with `CROSS JOIN`. A cartesian product multiplies each side by the other side's row count, so the comparison fails on correct data and the recorded expected/actual amounts are inflated by that factor. Aggregate each side independently in its own subquery or CTE, then compare the two scalars.
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AiServiceTests_Rich"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "fix: 프롬프트가 심던 거짓 셋을 걷어낸다

규칙 5가 @pi_bypassPreCheck를 발명해 명령했고, Few-Shot의 CATCH가 THROW로
끝나 규칙 6-1·13을 무력화했고, 규칙 4가 그림자를 넓게 권하면서 생성 위치와
복원 범위는 말하지 않았다. 재시작 스킵을 단계 밖으로 옮기고, CATCH를 반환
경로로 바꾸고, 그림자를 마지막 수단으로 좁혀 세 역학을 못박는다."
```

---

### Task 5: 단계 인터페이스 검사 (B1 · B8)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `ValidateBatchStep`(`:198`)에 5번째 인자와 검사 추가
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `StepInterface`, `StepInterfaceFacts.ParameterNames`(Task 2)
- Produces: `ValidateBatchStep(string? stepMarkdown, BatchStepPlan step, IReadOnlyCollection<string> knownTableNames, IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure, IReadOnlyList<StepInterface>? stepInterfaces = null)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    private static IReadOnlyList<StepInterface> Interfaces(string code, params string[] parameters) =>
        new[] { new StepInterface(code, new[] { "dbo.X" }, parameters) };

    [Fact]
    public void ValidateBatchStep_RejectsAParameterThatTheOriginalDoesNotHave()
    {
        var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
            Interfaces("S17", "@pi_strYMD varchar(8)"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
    }

    [Fact]
    public void ValidateBatchStep_AcceptsExactlyTheOriginalParameters()
    {
        var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @po_intRetVal int OUTPUT AS");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
            Interfaces("S17", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT"));

        Assert.DoesNotContain(result.Errors, e => e.Contains("파라미터"));
    }

    // 지역 변수는 파라미터가 아니다. DECLARE된 이름을 결함으로 들면
    // 모든 단계가 상시 실패한다.
    [Fact]
    public void ValidateBatchStep_IgnoresDeclaredLocalVariables()
    {
        var markdown = Section(@"CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8) AS
DECLARE @v_currentStepId INT = 0;
SET @v_currentStepId = -101;");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
            Interfaces("S17", "@pi_strYMD varchar(8)"));

        Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
    }

    // 소프트 스킵: 재료가 없으면 검사하지 않는다. 신설 단계에는 원본이 없다.
    [Fact]
    public void ValidateBatchStep_SkipsTheInterfaceCheckWhenTheStepHasNoOrigin()
    {
        var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_anything bit AS");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
            Interfaces("S99", "@pi_strYMD varchar(8)"));

        Assert.DoesNotContain(result.Errors, e => e.Contains("@pi_anything"));
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 컴파일 실패 — 5인자 오버로드 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`ValidateBatchStep`에 선택 인자를 더하고 `CheckStepInterface`를 호출한다:

```csharp
        /// <summary>
        /// 단계 본문이 선언한 프로시저 파라미터가 원본 인터페이스를 넘지 않는지 본다.
        ///
        /// 이 검사가 필요한 이유: 프롬프트 규칙 5가 @pi_bypassPreCheck를 발명해
        /// 명령했고, S02가 재시작 모드에서 실행 컨텍스트 전체에 그 값을 참으로
        /// 고정해 지급 확정 원장(OutState IN (1,5))의 -9 하드 스톱이 통째로
        /// 사라졌다. 프롬프트를 고쳐도 강제가 없으면 되살아난다.
        ///
        /// DECLARE된 지역 변수는 대상이 아니다. 파라미터 선언 구간
        /// (CREATE PROCEDURE ... AS 사이)에 등장하는 @이름만 본다.
        /// </summary>
        private static void CheckStepInterface(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyList<StepInterface>? stepInterfaces,
            StepValidationResult result)
        {
            var iface = stepInterfaces?.FirstOrDefault(
                i => string.Equals(i.StepCode, step.Code, StringComparison.OrdinalIgnoreCase));

            if (iface == null)
            {
                // 재료가 없다는 사실과 대조해서 깨끗하다는 사실을 로그에서 구별한다.
                Log.Information(
                    "{Code}는 원본 인터페이스 재료가 없어 파라미터 대조 대상이 아닙니다.", step.Code);
                return;
            }

            var allowed = new HashSet<string>(
                StepInterfaceFacts.ParameterNames(iface), StringComparer.OrdinalIgnoreCase);

            foreach (Match declaration in Regex.Matches(
                stepMarkdown,
                @"CREATE\s+PROC(?:EDURE)?\s+[^\s(]+\s*\(?(?<params>[^)]*?)\bAS\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                foreach (Match parameter in Regex.Matches(
                    declaration.Groups["params"].Value, @"@\w+"))
                {
                    if (allowed.Contains(parameter.Value)) continue;

                    result.Errors.Add(
                        $"{step.Code} 섹션이 원본에 없는 입력 파라미터 '{parameter.Value}'를 선언합니다. " +
                        $"이 단계의 인터페이스는 원본 프로시저의 파라미터가 전부입니다 " +
                        $"({string.Join(", ", iface.Parameters)}). 재시작·스킵·검사 우회를 위해 " +
                        "입력을 늘리지 마십시오 - 이미 완료된 단계는 오케스트레이터가 " +
                        "체크포인트를 보고 호출하지 않으며, 업무 보호 검사는 호출될 때마다 " +
                        "무조건 수행되어야 합니다.");
                }
            }
        }
```

호출부에 `CheckStepInterface(stepMarkdown, step, stepInterfaces, result);`를 `CheckForbiddenShortcuts` 옆에 더한다. `MechanicalValidator.cs` 상단에 `using System.Linq;`이 없으면 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "feat: 원본에 없는 입력 파라미터를 L1이 잡는다

프롬프트에서 @pi_bypassPreCheck를 걷어냈지만 강제가 없으면 되살아난다.
파라미터 선언 구간에 등장하는 @이름만 보므로 DECLARE된 지역 변수는
대상이 아니다. 원본이 없는 신설 단계는 소프트 스킵하고 사실을 로그로 남긴다."
```

---

### Task 6: 제어 어휘·행 출처 검사 (B2 · B3)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: `BatchControlContract.Find`, `ControlTable`, `ControlRowOrigin`(Task 1)
- Produces: 없음(private 검사 둘)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    [Fact]
    public void ValidateBatchStep_RejectsAColumnNameOutsideTheControlContract()
    {
        var markdown = Section(
            "UPDATE batch.BatchStepJournal SET ExecutionStatus = N'Completed' WHERE StepCode = N'S17';");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
    }

    [Fact]
    public void ValidateBatchStep_RejectsTheCompletedStatusValue()
    {
        var markdown = Section(
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Completed' WHERE StepCode = N'S17';");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
    }

    [Fact]
    public void ValidateBatchStep_AcceptsTheCanonicalVocabulary()
    {
        var markdown = Section(@"
INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());
UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded', CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId AND StepCode = N'S17';");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
    }

    // B3: UPDATE만 있고 INSERT가 없으면 0행 갱신이다. @@ROWCOUNT 검사가
    // 있는 곳은 정상 실행에서도 상시 실패하고, 없는 곳은 조용히 지나간다.
    [Fact]
    public void ValidateBatchStep_RejectsUpdatingAJournalRowItNeverInserts()
    {
        var markdown = Section(
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S17';");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
    }

    // 읽기만 하는 단계는 대상이 아니다. 다른 단계의 저널을 조회하는 것은 정상이다.
    [Fact]
    public void ValidateBatchStep_DoesNotRequireAnInsertWhenItOnlyReadsTheTable()
    {
        var markdown = Section(
            "SELECT StepStatus FROM batch.BatchStepJournal WHERE RunId = @RunId AND StepCode = N'S16';");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("INSERT"));
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 4 failed(어휘 3 + 행 출처 1), 읽기 전용 케이스는 통과

- [ ] **Step 3: 최소 구현을 쓴다**

```csharp
        /// <summary>
        /// 제어 테이블에 계약 밖의 컬럼명·상태값을 쓰는지 본다.
        ///
        /// 실측: 같은 batch.BatchStepJournal에 S01은 StepStatus='Succeeded',
        /// S02는 ExecutionStatus='Completed', S03은 StepStatus='Completed',
        /// S17은 StepState를 썼다. 어느 쪽으로 DDL을 만들어도 반대편 단계가
        /// 컴파일되지 않는다. 정본이 있으면 단계마다 정본과 대조하는 것으로
        /// 충분하다 - 18개 문서를 한꺼번에 읽는 교차 검사는 필요 없다.
        /// </summary>
        private static void CheckBatchControlVocabulary(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            foreach (var table in BatchControlContract.Tables)
            {
                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                if (!ContainsToken(stepMarkdown, bare)) continue;

                var known = new HashSet<string>(
                    table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

                // 이 테이블을 다루는 구문에서만 컬럼 후보를 본다. 문서 전체를
                // 훑으면 업무 테이블의 컬럼이 후보로 섞인다.
                foreach (Match statement in Regex.Matches(
                    stepMarkdown,
                    $@"(INSERT\s+INTO|UPDATE|FROM|JOIN)\s+(?:\w+\.)?{Regex.Escape(bare)}\b(?<tail>.*?)(?=;|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var tail = statement.Groups["tail"].Value;

                    foreach (Match candidate in Regex.Matches(
                        tail, @"(?<![@\w.])(?<col>[A-Za-z_]\w*)\s*(?==|,|\))"))
                    {
                        var name = candidate.Groups["col"].Value;
                        if (known.Contains(name)) continue;
                        if (!LooksLikeControlColumn(name, known)) continue;

                        result.Errors.Add(
                            $"{step.Code} 섹션이 제어 테이블 `{table.Name}`에 계약 밖의 컬럼 " +
                            $"'{name}'을 씁니다. 이 테이블의 컬럼은 " +
                            $"{string.Join(", ", table.Columns.Select(c => c.Name))}가 전부입니다.");
                    }

                    if (table.StatusColumn == null) continue;
                    var allowed = table.Columns
                        .First(c => c.Name == table.StatusColumn).AllowedValues!;

                    foreach (Match literal in Regex.Matches(tail, @"N?'(?<v>[A-Za-z]\w*)'"))
                    {
                        var value = literal.Groups["v"].Value;
                        if (!IsStatusLikeLiteral(value) || allowed.Contains(value, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        result.Errors.Add(
                            $"{step.Code} 섹션이 `{table.Name}`에 계약 밖의 상태값 '{value}'를 씁니다. " +
                            $"허용 값은 {string.Join(", ", allowed)}입니다 - 성공 종료는 " +
                            "'Succeeded' 하나이며 'Completed'는 쓰지 않습니다. 두 어휘가 섞이면 " +
                            "정상 성공한 단계가 재시작 대조에서 미완료로 판정되어 실행이 상시 차단됩니다.");
                    }
                }
            }
        }

        /// <summary>계약 밖 이름 중 제어 컬럼으로 보이는 것만 든다 — 업무 컬럼 오탐을 막는다.</summary>
        private static bool LooksLikeControlColumn(string name, HashSet<string> known)
        {
            string[] stems = { "Status", "State", "JobName", "StartedAt", "CompletedAt", "Message", "RunId", "StepCode" };
            return stems.Any(stem => name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
                   && !known.Contains(name);
        }

        /// <summary>상태 어휘로 보이는 리터럴만 본다 — 오류 코드·테이블명 리터럴을 거른다.</summary>
        private static bool IsStatusLikeLiteral(string value)
        {
            string[] statusWords =
            {
                "Running", "Succeeded", "Failed", "Skipped", "Restarting", "Pending",
                "Completed", "Validating", "Publishing", "Published", "Retrying", "Unpublished"
            };
            return statusWords.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 자기 소유 제어 행을 만들지 않고 UPDATE만 하는지 본다.
        ///
        /// 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었고 S03·S06·S17이
        /// 자기 저널·체크포인트 행을 만드는 지점 없이 UPDATE만 했다. @@ROWCOUNT
        /// 검사가 있는 S17은 정상 실행에서도 공개가 상시 실패했고, 없는 S06은
        /// 0행 갱신을 오류 없이 지나가 재삽입 방지가 성립하지 않았다.
        /// </summary>
        private static void CheckBatchControlRowOrigin(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            foreach (var table in BatchControlContract.Tables)
            {
                if (table.Origin != ControlRowOrigin.EachStepInserts) continue;

                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                var updates = Regex.IsMatch(
                    stepMarkdown, $@"UPDATE\s+(?:\w+\.)?{Regex.Escape(bare)}\b", RegexOptions.IgnoreCase);
                if (!updates) continue;

                var inserts = Regex.IsMatch(
                    stepMarkdown,
                    $@"(INSERT\s+INTO|MERGE)\s+(?:\w+\.)?{Regex.Escape(bare)}\b",
                    RegexOptions.IgnoreCase);
                if (inserts) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션이 `{table.Name}`을 UPDATE만 하고 자기 행을 만드는 지점이 " +
                    "없습니다. 이 테이블은 각 단계가 시작할 때 자기 행을 INSERT한 뒤 종료할 때 " +
                    "UPDATE하는 계약입니다. 생성 없이 UPDATE만 하면 0행이 갱신되어, @@ROWCOUNT를 " +
                    "검사하는 경로는 정상 실행에서도 상시 실패하고 검사하지 않는 경로는 완료 표시 " +
                    "없이 조용히 지나갑니다.");
            }
        }
```

호출부에 둘 다 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "feat: 제어 테이블의 어휘와 행 출처를 L1이 대조한다

정본이 있으면 단계마다 정본과 대조하는 것으로 충분하다 - 18개 문서를
한꺼번에 읽는 교차 검사는 필요 없다. 계약 밖 이름 중 제어 컬럼으로
보이는 것만 들어 업무 컬럼 오탐을 막는다."
```

---

### Task 7: 그림자 계약·반환 경로 검사 (B6 · B7)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음(private 검사 둘)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    // 감사 🔴(S04): BEGIN TRAN 안에서 만든 SELECT INTO 그림자는 롤백과 함께
    // 사라진다. CATCH의 DELETE는 자동 커밋이라 이미 복원된 행을 다시 지우고
    // 복원 INSERT는 객체 없음 오류로 실패한다.
    [Fact]
    public void ValidateBatchStep_RejectsAShadowCreatedInsideTheTransaction()
    {
        var markdown = Section(@"
BEGIN TRAN;
SELECT * INTO batch_shadow.TClientSettleRate_RunId_S04 FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
DELETE FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BEGIN TRAN") && e.Contains("그림자"));
    }

    [Fact]
    public void ValidateBatchStep_AcceptsAShadowCreatedBeforeTheTransaction()
    {
        var markdown = Section(@"
SELECT * INTO batch_shadow.TClientSettleRate_RunId_S04 FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
BEGIN TRAN;
DELETE FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("그림자"));
    }

    // 감사 🟠(S12): WHERE 없는 전량 삭제 후 전체 스냅샷 재삽입은 당일 외
    // 거래일 행까지 실행 시작 시점으로 되돌린다.
    [Fact]
    public void ValidateBatchStep_RejectsARestoreThatDeletesWithoutARange()
    {
        var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.Contains(result.Errors, e => e.Contains("WHERE"));
    }

    // 감사 🟠(S11): EXEC() 동적 배치는 바깥 배치의 변수를 볼 수 없다.
    [Fact]
    public void ValidateBatchStep_RejectsAnOuterVariableInsideExec()
    {
        var markdown = Section(
            "EXEC(N'INSERT INTO ' + @v_shadowTableName + N' SELECT A.* FROM dbo.T A WHERE A.ProcYMD = @pi_strYMD');");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.Contains(result.Errors, e => e.Contains("sp_executesql"));
    }

    // B7: CATCH가 THROW로 끝나면 호출부의 OUTPUT 대입을 지나쳐 원본 반환 코드가 사라진다.
    [Fact]
    public void ValidateBatchStep_RejectsACatchThatOnlyRethrows()
    {
        var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.Contains(result.Errors, e => e.Contains("THROW"));
    }

    [Fact]
    public void ValidateBatchStep_AcceptsACatchThatSetsTheOutputAndReturns()
    {
        var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

        var result = new MechanicalValidator().ValidateBatchStep(
            markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("THROW"));
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 4 failed(그림자 3 + 반환 1), 정상 케이스 2는 통과

- [ ] **Step 3: 최소 구현을 쓴다**

```csharp
        /// <summary>
        /// 그림자 백업 장치의 세 역학을 본다.
        ///
        /// 감사 실측에서 다섯 단계가 각기 다른 이유로 복구 불능이었다. 규칙 4가
        /// "선행 DELETE 후 복원"만 강제하고 생성 위치·복원 범위·동적 SQL 변수
        /// 스코프는 한 마디도 하지 않았기 때문이다.
        /// </summary>
        private static void CheckShadowBackupContract(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            // (a) 트랜잭션 안에서 만든 그림자는 롤백과 함께 소멸한다.
            var beginTran = stepMarkdown.IndexOf("BEGIN TRAN", StringComparison.OrdinalIgnoreCase);
            if (beginTran >= 0)
            {
                var commit = stepMarkdown.IndexOf("COMMIT TRAN", beginTran, StringComparison.OrdinalIgnoreCase);
                var end = commit > beginTran ? commit : stepMarkdown.Length;
                var inside = stepMarkdown[beginTran..end];

                if (Regex.IsMatch(inside, @"(SELECT\s+.*?\bINTO\s+batch_shadow\.)",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    result.Errors.Add(
                        $"{step.Code} 섹션이 BEGIN TRAN 안에서 그림자 테이블을 만듭니다. " +
                        "SELECT INTO로 만든 테이블은 롤백과 함께 소멸하므로, 실패 시 복원할 " +
                        "대상이 사라진 채 CATCH의 DELETE만 자동 커밋으로 실행되어 롤백이 이미 " +
                        "복원한 행을 다시 지웁니다. 그림자는 BEGIN TRAN 앞에서 만드십시오. " +
                        "단일 트랜잭션으로 끝나는 단계라면 그림자 없이 ROLLBACK TRAN만 쓰십시오.");
                }
            }

            // (b) 복원은 원래 삭제한 범위와 같은 범위를 지워야 한다.
            foreach (Match restore in Regex.Matches(
                stepMarkdown,
                @"DELETE\s+FROM\s+(?<t>[\w.\[\]]+)\s*;(?<tail>.{0,400}?)INSERT\s+INTO\s+\k<t>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                result.Errors.Add(
                    $"{step.Code} 섹션의 복원이 `{restore.Groups["t"].Value}`를 WHERE 없이 " +
                    "전량 삭제한 뒤 재삽입합니다. 복원은 이 단계가 실제로 지운 범위와 같은 " +
                    "범위만 지워야 합니다 - 전량 삭제하면 다른 거래일의 행까지 실행 시작 " +
                    "시점으로 되돌아가, 레거시에 없는 전역 행 집합 변경 경로가 생깁니다.");
            }

            // (c) EXEC() 동적 배치는 바깥 배치의 변수를 볼 수 없다.
            foreach (Match exec in Regex.Matches(
                stepMarkdown, @"EXEC\s*\((?<body>.*?)\)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var body = exec.Groups["body"].Value;
                // 문자열 리터럴 안의 @이름만 본다 - 연결에 쓰인 바깥 변수는 정상이다.
                foreach (Match literal in Regex.Matches(body, @"N?'(?<s>[^']*)'"))
                {
                    if (!Regex.IsMatch(literal.Groups["s"].Value, @"@\w+")) continue;

                    result.Errors.Add(
                        $"{step.Code} 섹션이 EXEC()로 만든 동적 배치 안에서 바깥 배치의 변수를 " +
                        "참조합니다. 동적 배치는 별도 스코프라 그 변수를 볼 수 없어 스칼라 변수 " +
                        "미선언 오류로 실패합니다. sp_executesql의 매개변수로 값을 넘기십시오.");
                    break;
                }
            }
        }

        /// <summary>
        /// CATCH가 반환 경로 없이 THROW로 끝나는지 본다.
        ///
        /// 프롬프트 규칙 6-1은 상태 변수를 CATCH에서 반환하라 하고 규칙 13은 출력
        /// 파라미터를 누락 없이 매핑하라 하는데, Few-Shot 예시의 CATCH가 THROW로
        /// 끝났다. 모델은 산문 규칙보다 코드 예시를 따른다 - 실측 5건이 그렇게 나왔다.
        /// </summary>
        private static void CheckCatchDiscardsReturnCode(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            foreach (Match block in Regex.Matches(
                stepMarkdown, @"BEGIN\s+CATCH(?<body>.*?)END\s+CATCH",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var body = block.Groups["body"].Value;
                if (!Regex.IsMatch(body, @"\bTHROW\b", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(body, @"\bRETURN\b", RegexOptions.IgnoreCase)) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션의 CATCH 블록이 반환 경로 없이 THROW로 끝납니다. " +
                    "THROW는 호출부의 OUTPUT 파라미터 대입을 지나쳐 원본 반환 코드를 " +
                    "잃어버립니다. 추적한 상태 변수를 출력 파라미터에 넣고 RETURN하십시오.");
            }
        }
```

호출부에 둘 다 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorBatchStepTests"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs
git commit -m "feat: 그림자 세 역학과 CATCH 반환 경로를 L1이 잡는다

트랜잭션 안에서 만든 SELECT INTO 그림자는 롤백과 함께 소멸하고, WHERE 없는
복원은 다른 거래일까지 되돌리고, EXEC() 동적 배치는 바깥 변수를 못 본다.
CATCH의 THROW는 호출부의 OUTPUT 대입을 지나쳐 반환 코드를 잃는다."
```

---

### Task 8: 검증식 카티전 검사 (B4 🔴)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `ErrorType`(`:13`), `ValidateConsolidated`(`:144`)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `ErrorType.VerificationCartesianComparison`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 추가한다.

```csharp
    // 감사 🔴(S16): CROSS JOIN 뒤 양변 SUM 비교는 각 변이 상대 건수배가 되어
    // 정상 데이터에서 항상 불일치한다. 그 결과가 S17 공개 상시 차단으로 이어졌다.
    [Fact]
    public void ValidateConsolidated_RejectsACartesianAggregateComparison()
    {
        var markdown = ConsolidatedDocumentWithVerificationSql(@"
SELECT ISNULL(SUM(M.TXAMT),0), ISNULL(SUM(T.TXAMT),0)
FROM dbo.TSettleMst AS M
CROSS JOIN dbo.TSettleByTX AS T
HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);");

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.Contains(result.DetailedErrors,
            e => e.Type == ErrorType.VerificationCartesianComparison);
    }

    [Fact]
    public void ValidateConsolidated_AcceptsIndependentAggregatesComparedAsScalars()
    {
        var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleMst WHERE YMD = @BatchYmd),
     R AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleByTX WHERE YMD = @BatchYmd)
SELECT L.S, R.S FROM L, R WHERE L.S <> R.S;");

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.DoesNotContain(result.DetailedErrors,
            e => e.Type == ErrorType.VerificationCartesianComparison);
    }
```

`ConsolidatedDocumentWithVerificationSql` 헬퍼는 `RequiredConsolidatedHeaders`(`MechanicalValidator.cs:77-83`)의 네 헤더를 그대로 갖춘 최소 문서를 만든다. 그 값은 확인했다 — `통합 배치 아키텍처 개요` · `Mermaid 기반 통합 흐름도` · `단계별 이행 상세 및 의사코드` · `통합 데이터 정합성 검증 SQL 세트`.

```csharp
    private static string ConsolidatedDocumentWithVerificationSql(string sql) => $"""
        ## 통합 배치 아키텍처 개요

        내용.

        ## Mermaid 기반 통합 흐름도

        ```mermaid
        flowchart TD
        A["시작"] --> B["끝"]
        ```

        ## 단계별 이행 상세 및 의사코드

        내용.

        ## 통합 데이터 정합성 검증 SQL 세트

        ```sql
        {sql}
        ```
        """;
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ValidateConsolidated"`
Expected: 컴파일 실패 — `ErrorType.VerificationCartesianComparison` 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`ErrorType`에 `VerificationCartesianComparison`을 `General` 앞에 더한다. `ValidateConsolidated`의 `ValidateMarkdownStructure` 호출 뒤에 검사를 더한다:

```csharp
        /// <summary>
        /// 정합성 검증 SQL이 카티전 곱으로 두 집계를 비교하는지 본다.
        ///
        /// 실측: FROM TSettleMst AS M CROSS JOIN TSettleByTX AS T 뒤
        /// HAVING SUM(M.TXAMT) &lt;&gt; SUM(T.TXAMT)는 좌변이 |T|×SUM_M,
        /// 우변이 |M|×SUM_T가 되어 |M|≠|T|인 정상 데이터에서 항상 불일치한다.
        /// 정상 실행이 매번 데이터 품질 실패로 기록되어 공개가 상시 차단되고,
        /// 증적에는 카티전 배수만큼 부풀려진 틀린 금액이 남는다.
        /// </summary>
        private static void CheckVerificationCartesianComparison(string markdown, ValidationResult result)
        {
            foreach (Match block in Regex.Matches(
                markdown, @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var sql = block.Groups["sql"].Value;
                if (!Regex.IsMatch(sql, @"\bCROSS\s+JOIN\b", RegexOptions.IgnoreCase)) continue;

                // 서로 다른 별칭 둘에 각각 SUM이 걸린 비교만 든다.
                var aliases = Regex.Matches(sql, @"\bSUM\s*\(\s*(?:ISNULL\s*\(\s*)?(?<a>\w+)\.",
                        RegexOptions.IgnoreCase)
                    .Select(m => m.Groups["a"].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (aliases.Count < 2) continue;

                var message =
                    "정합성 검증 SQL이 CROSS JOIN으로 두 집계를 비교합니다. 카티전 곱이라 " +
                    "각 변이 상대 테이블의 건수배가 되어 정상 데이터에서 항상 불일치하고, " +
                    "증적에는 그 배수만큼 부풀려진 금액이 남습니다. 양쪽을 각자의 부질의나 " +
                    "CTE에서 독립적으로 집계한 뒤 두 스칼라를 비교하십시오.";

                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.VerificationCartesianComparison,
                    Message = message,
                    RawContext = sql.Trim()
                });
            }
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorTests"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 카티전 곱으로 두 집계를 비교하는 검증식을 잡는다

CROSS JOIN 뒤 양변 SUM 비교는 각 변이 상대 건수배가 되어 정상 데이터에서
항상 불일치한다. 정상 실행이 매번 데이터 품질 실패로 기록되어 공개가
상시 차단됐다."
```

---

### Task 9: M2를 검증 경로에 배선

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — `:1694`(재료 조달), `:1874`·`:2318`(전달), `:2955`·`:3049`(`GenerateBySplitAsync`), `:3124`·`:3163`·`:3182`(`GenerateStepSectionWithFloorRetryAsync`)
- Test: `tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs`

**Interfaces:**
- Consumes: `StepInterfaceFacts.CollectParameters` / `.Build`(Task 2), 5인자 `ValidateBatchStep`(Task 5), 7인자 `GenerateBatchStepSectionAsync`(Task 3)
- Produces: 없음

**배선 형태:** `definitions`가 있는 지점(`:1694`)에는 `steps`가 아직 없다 — `steps`는 목차 파싱 뒤에 생기고 `GenerateBySplitAsync`(`:2955`)의 인자로 들어온다. 그래서 `knownTableNames`와 **정확히 같은 형태**로 나른다: `:1694`에서 `CollectParameters`로 사전을 만들어 `knownTableNames` 옆에 두고 같은 경로로 실려 보낸 뒤, `steps`가 있는 `GenerateBySplitAsync` 안에서 `Build`를 부른다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

배선 누락은 빌드가 잡지 못한다 — `stepInterfaces`가 선택 인자라 5번째를 빼도 컴파일된다. `KnownTableWiringPolicyScanner`와 같은 취지의 정적 검사를 더한다.

`tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs`에 추가한다. 이 파일의 스캐너가 소스를 어떻게 찾는지 먼저 읽고(`KnownTableWiringPolicyScanner.cs`) 같은 경로 해석 방식을 재사용한다.

```csharp
    // 규칙: _validator.ValidateBatchStep(...) 호출은 5번째 인자(원본 인터페이스)를
    // 받아야 한다. 선택 인자라 빼도 컴파일되므로, 빠뜨린 경로에서만 파라미터
    // 검사가 조용히 꺼진다 - 이 저장소가 카탈로그 인자에서 이미 겪은 실패 모드다.
    [Fact]
    public void Orchestrator_PassesTheStepInterfacesToEveryValidateBatchStepCall()
    {
        var source = File.ReadAllText(Path.Combine(
            KnownTableWiringPolicyScanner.RepositoryRoot(),
            "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        var calls = Regex.Matches(source, @"_validator\.ValidateBatchStep\((?<args>[^;]*?)\)");
        Assert.NotEmpty(calls);

        foreach (Match call in calls)
        {
            var commas = call.Groups["args"].Value.Count(c => c == ',');
            Assert.True(commas >= 4, $"5번째 인자가 없는 호출: {call.Value}");
        }
    }
```

`KnownTableWiringPolicyScanner`에 저장소 루트를 찾는 공개 헬퍼가 없으면, 같은 파일의 기존 테스트가 소스를 읽는 방식을 그대로 복사해 쓴다 — 새 경로 해석 방식을 만들지 않는다.

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~KnownTableWiringPolicyTests"`
Expected: 1 failed — 현재 호출은 인자 4개(쉼표 3개)다

- [ ] **Step 3: 최소 구현을 쓴다**

`:1694`의 `knownTableNames` 바로 아래에 재료를 만든다:

```csharp
            // 원본 인터페이스 재료. knownTableNames와 같은 자리에서 만드는 이유는
            // 둘 다 definitions에서 오고 같은 경로로 단계 검증까지 흘러가기 때문이다.
            // 단계 목록은 여기서 아직 없으므로 프로시저별 사전까지만 만들고,
            // 단계별 조립은 steps를 가진 GenerateBySplitAsync가 한다.
            var parametersByProcedure = StepInterfaceFacts.CollectParameters(definitions);
```

`knownTableNames`가 지나가는 경로를 그대로 따라 `parametersByProcedure`를 더한다 — `:1874`, `:2318`, `GenerateBySplitAsync`(`:2955` 시그니처, `:3049` 호출), `GenerateStepSectionWithFloorRetryAsync`(`:3124` 시그니처, `:3163` 호출).

`GenerateBySplitAsync` 안에서 `conventions`를 만드는 자리(`:3006`) 옆에 조립한다:

```csharp
            // steps가 여기서 처음 확정되므로 단계별 인터페이스도 여기서 만든다.
            // 재시도 루프 밖이다 - 단계마다 뽑아도 결과가 같다.
            var stepInterfaces = StepInterfaceFacts.Build(steps, parametersByProcedure);
```

`GenerateStepSectionWithFloorRetryAsync`에는 `IReadOnlyList<StepInterface> stepInterfaces`로 넘기고, 그 안에서 두 곳에 쓴다:

```csharp
                    var result = await _consolidatorService.GenerateBatchStepSectionAsync(
                        step, steps, conventions, specs, stepInterfaces, targetLanguage, jobName,
                        _consolidatorEffort, floorFeedback, cancellationToken);
```

```csharp
                var stepResult = _validator.ValidateBatchStep(
                    content, step, knownTableNames, conditionColumns, stepInterfaces);
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -c "warning" && dotnet test tests/ReSet.Core.Tests`
Expected: 경고 9 이하, 전체 테스트 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs
git commit -m "feat: 원본 인터페이스 재료를 단계 생성·검증 경로에 배선한다

definitions가 있는 지점에는 steps가 없으므로 knownTableNames와 같은 형태로
프로시저별 사전을 만들어 나르고, steps가 확정되는 곳에서 단계별로 조립한다.
선택 인자라 빼도 컴파일되므로 빠뜨린 경로에서만 검사가 조용히 꺼진다 -
카탈로그 인자에서 이미 겪은 실패 모드라 같은 방식의 정적 검사를 둔다."
```

---

### Task 10: 부트스트랩 회차에 제어 테이블 DDL 주입

**Files:**
- Modify: `src/ReSet.Core/Services/TaskFileComposer.cs` — `AppendBootstrap`(`:174`)
- Test: `tests/ReSet.Core.Tests/TaskFileComposerTests.cs`

**Interfaces:**
- Consumes: `BatchControlContract.RenderDdl()`(Task 1)
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/TaskFileComposerTests.cs`에 추가한다. 기존 파일의 `TaskFileInputs` 조립 헬퍼를 그대로 재사용한다.

```csharp
    // 감사 §6-4: 제어 테이블의 컬럼 정의가 번들 어디에도 없었다. 회차 0 문서는
    // 객체 이름만 나열하고 정의를 단계 문서에 위임했는데, 단계마다 다르게 썼다.
    [Fact]
    public void Bootstrap_CarriesTheControlTableDdl()
    {
        var composed = TaskFileComposer.Compose(BootstrapInputs());

        Assert.Contains("CREATE TABLE batch.BatchStepJournal", composed);
        Assert.Contains("CHECK (StepStatus IN (N'Running', N'Succeeded', N'Failed', N'Skipped'))", composed);
    }

    // 이름 목록은 그대로 있어야 한다. DDL이 있는 것과 이 회차가 만들 객체
    // 목록이 있는 것은 다른 사실이다 - Job이 만드는 그림자·헬퍼는 계약에 없다.
    [Fact]
    public void Bootstrap_StillListsTheCollectedObjectNames()
    {
        var composed = TaskFileComposer.Compose(BootstrapInputs());

        Assert.Contains("batch_shadow.", composed);
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~TaskFileComposerTests"`
Expected: 1 failed — DDL이 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`AppendBootstrap`의 객체 이름 목록 뒤에 붙인다:

```csharp
            // 제어 테이블 넷은 이 도구가 정본을 가지므로 이름만 넘기지 않고 DDL을
            // 그대로 싣는다. 정의를 단계 문서에 위임했더니 단계마다 다른 컬럼명과
            // 상태 어휘를 써서, 어느 쪽으로 만들어도 반대편 단계가 컴파일되지
            // 않는 상태가 됐다(BatchControlContract의 <summary> 참고).
            sb.AppendLine();
            sb.AppendLine("## 실행 제어 테이블 DDL (정본)");
            sb.AppendLine();
            sb.AppendLine("아래 DDL을 그대로 만드십시오. 컬럼명과 상태값을 바꾸지 마십시오 -");
            sb.AppendLine("단계 문서들이 이 이름과 값을 그대로 쓰도록 생성되었습니다.");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.Append(BatchControlContract.RenderDdl());
            sb.AppendLine("```");
```

- [ ] **Step 4: 통과를 확인한다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~TaskFileComposerTests"`
Expected: 전부 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add src/ReSet.Core/Services/TaskFileComposer.cs tests/ReSet.Core.Tests/TaskFileComposerTests.cs
git commit -m "feat: 회차 0 문서가 제어 테이블 DDL을 싣는다

정의를 단계 문서에 위임했더니 단계마다 다른 컬럼명과 상태 어휘를 써서
어느 쪽으로 만들어도 반대편 단계가 컴파일되지 않는 상태가 됐다."
```

---

### Task 11: 실물 코퍼스 골든 케이스

**Files:**
- Create: `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs`

**Interfaces:**
- Consumes: Task 5~8의 검사 전부
- Produces: 없음

- [ ] **Step 1: 실패할 수 있는 테스트를 쓴다**

감사가 실측한 결함 본문을 그대로 코퍼스로 박는다. `AxisAGoldenCaseTests`(206줄)의 구성을 먼저 읽고 같은 관용을 따른다.

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// POQSettleProc16 정합성 감사(2026-08-17)가 실측한 축 B 결함을 코퍼스로 고정한다.
///
/// 단위 테스트가 검사 하나하나의 동작을 보는 것과 달리, 여기서는 실제 산출물에서
/// 오려낸 본문을 넣어 검사가 그 결함을 실제로 잡는지 본다. 검사가 통과하도록
/// 규칙을 느슨하게 만드는 회귀를 막는 것이 목적이다.
/// </summary>
public sealed class AxisBGoldenCaseTests
{
    private static readonly IReadOnlyDictionary<string, SpecConditions> NoConditions =
        new Dictionary<string, SpecConditions>();

    private static readonly string[] Catalog =
    {
        "dbo.TSettleMst", "dbo.TSettleByTX", "dbo.TClientSettleRate4Extra"
    };

    private static BatchStepPlan Step(string code) => new(
        Code: code,
        Name: $"{code} 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: Array.Empty<string>(),
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    private static string Section(string code, string sql) => $"""
        ### {code} 단계

        ```sql
        {sql}
        ```
        """;

    private static StepValidationResult Validate(
        string code, string sql, IReadOnlyList<StepInterface>? interfaces = null) =>
        new MechanicalValidator().ValidateBatchStep(
            Section(code, sql), Step(code), Catalog, NoConditions, interfaces);

    // 감사 S10 🟠 — 보호 검사를 우회 플래그 안에 넣었다.
    [Fact]
    public void S10_ConditionalGuardOnABypassParameter()
    {
        var result = Validate(
            "S10",
            "CREATE PROCEDURE batch.usp_S10 @pi_strYMD varchar(8), @pi_bypassPreCheck bit = 0 AS\n" +
            "IF @pi_bypassPreCheck = 0 AND EXISTS (SELECT 1 FROM dbo.TSettleMst WHERE OutState IN (1,5))\n" +
            "    RETURN -9;",
            new[] { new StepInterface("S10", new[] { "dbo.UP_UTIL_SETTLE_INS_EXTRA" },
                new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }) });

        Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
    }

    // 감사 S03 🟡 — S03만 저널 성공 상태를 'Completed'로 썼다.
    [Fact]
    public void S03_JournalSuccessWrittenAsCompleted()
    {
        var result = Validate(
            "S03",
            "INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)\n" +
            "VALUES (@RunId, N'S03', N'Running', SYSUTCDATETIME());\n" +
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Completed' WHERE StepCode = N'S03';");

        Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
    }

    // 감사 S03 🟠 — 저널 행을 만드는 지점 없이 UPDATE만 한다.
    [Fact]
    public void S03_UpdatesAJournalRowItNeverInserts()
    {
        var result = Validate(
            "S03",
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S03';");

        Assert.Contains(result.Errors, e => e.Contains("INSERT"));
    }

    // 감사 S04 🔴 — 트랜잭션 안에서 만든 그림자가 롤백과 함께 소멸한다.
    [Fact]
    public void S04_ShadowCreatedInsideTheTransaction()
    {
        var result = Validate(
            "S04",
            "BEGIN TRAN;\n" +
            "SELECT * INTO batch_shadow.TClientSettleRate4Extra_RunId_S04\n" +
            "FROM dbo.TClientSettleRate4Extra WHERE YMD = @pi_strYMD;\n" +
            "DELETE FROM dbo.TClientSettleRate4Extra WHERE YMD = @pi_strYMD;\n" +
            "COMMIT TRAN;");

        Assert.Contains(result.Errors, e => e.Contains("BEGIN TRAN"));
    }

    // 감사 S11 🟠 — EXEC() 동적 배치가 바깥 변수를 참조한다.
    [Fact]
    public void S11_OuterVariableInsideExec()
    {
        var result = Validate(
            "S11",
            "EXEC(N'INSERT INTO ' + @v_shadowTableName + " +
            "N' SELECT A.* FROM dbo.TSettleMst A WHERE A.ProcYMD = @pi_strYMD');");

        Assert.Contains(result.Errors, e => e.Contains("sp_executesql"));
    }

    // 감사 B7 — CATCH가 반환 경로 없이 THROW로 끝난다.
    [Fact]
    public void B7_CatchOnlyRethrows()
    {
        var result = Validate(
            "S07",
            "BEGIN CATCH\n    IF @@TRANCOUNT > 0 ROLLBACK TRAN;\n    THROW;\nEND CATCH");

        Assert.Contains(result.Errors, e => e.Contains("THROW"));
    }

    // 감사 S16 🔴 — 카티전 곱으로 두 집계를 비교한다.
    [Fact]
    public void S16_CartesianAggregateComparison()
    {
        var markdown = $"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            내용.

            ## 통합 데이터 정합성 검증 SQL 세트

            ```sql
            SELECT 1
            FROM dbo.TSettleMst AS M
            CROSS JOIN dbo.TSettleByTX AS T
            HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);
            ```
            """;

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.Contains(result.DetailedErrors,
            e => e.Type == ErrorType.VerificationCartesianComparison);
    }
}
```

위 문서의 H2 네 개는 `RequiredConsolidatedHeaders`(`MechanicalValidator.cs:77-83`)의 실제 값과 같다 — 확인했다. 다르게 쓰면 `ValidateConsolidated`가 헤더 누락으로 먼저 실패해 이 테스트가 무엇을 검사하는지 흐려진다.

- [ ] **Step 2: 실행해 전부 통과하는지 본다**

Run: `cd ../ReSet-axis-b && dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AxisBGoldenCaseTests"`
Expected: 7 passed

실패하는 케이스가 있으면 그것은 코퍼스가 아니라 **검사의 결함**이다. Task 5~8로 돌아가 검사를 고친다. 코퍼스를 검사에 맞춰 고치지 않는다 — 실측한 결함을 놓치는 검사를 통과시키는 것이 이 테스트가 막으려는 일이다.

- [ ] **Step 3: 되돌림 검사(레드-그린 확인)**

Task 5~8에서 더한 검사 하나를 임시로 주석 처리하고 대응 골든 케이스가 실제로 실패하는지 확인한 뒤 되돌린다. 여섯 검사 전부에 대해 한 번씩 한다.

- [ ] **Step 4: 전체 테스트와 경고 기준선을 확인한다**

Run: `cd ../ReSet-axis-b && dotnet build --no-incremental 2>&1 | grep -c "warning" && dotnet test`
Expected: 경고 9 이하, 전체 통과

- [ ] **Step 5: 커밋한다**

```bash
cd ../ReSet-axis-b
git add tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs
git commit -m "test: 감사가 실측한 축 B 결함을 코퍼스로 못 박는다

검사 하나하나의 동작이 아니라 실제 산출물에서 오려낸 본문을 넣어, 검사가
그 결함을 실제로 잡는지 본다. 검사가 통과하도록 규칙을 느슨하게 만드는
회귀를 막는 것이 목적이다."
```

---

## 마무리 — 이 계획이 끝나도 남는 것

- **Job 재생성은 하지 않는다.** 축 A가 안정된 뒤 1회만 돌리고, 그 1회에 두 축의 수정이 함께 반영된다. 이 계획은 생성기만 고친다.
- **축 A 브랜치와의 충돌.** 축 A(`feat/set-predicate-material`)도 `AiService.cs`와 `MechanicalValidator.cs`를 고친다. 태스크마다 커밋한 뒤 `git fetch && git rebase main`으로 따라잡는다.
- **재감사로 확인할 것.** 이 계획이 닫으려는 것은 B1 7 · B2 9 · B3 6 · B6 6 · B7 5 · B8 4 · B4 🔴 1 = 38건이다. Job 재생성 후 `reset-consistency-audit` 스킬로 축 B를 다시 재서, 닫힌 것과 남은 것을 센다.
