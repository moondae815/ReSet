# 재료 분모 계기 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 스윕이 명세서 재료마다 「원본 DDL 사실 수」 대 「명세서 행 수」를 세어, 검사가 재료를 잃고 조용히 꺼진 자리를 보고서에 드러낸다.

**Architecture:** 손으로 적는 `SpecMaterials` 카탈로그가 (재료 · 리더 · 읽는 절 · 강제 여부 · DDL 대응물 · 쓰는 검사)의 단일 출처가 되고, 테스트 셋이 그 표가 낡는 것을 막는다. 순수 함수 `SpecMaterialCensus.Count`가 프로시저 단위로 두 수를 세고(같은 SP가 여러 Job에 나오므로 이름으로 접는다), `StepSweepService`가 그 결과를 `SweepIndicators`에 실어 `StepSweepReportWriter`가 인쇄한다. **새 CLI 명령도 새 배선도 만들지 않는다** — `SweepJob`이 `Specs`와 `DdlByProcedure`를 이미 둘 다 들고 있다.

**Tech Stack:** C# / .NET 10 · xUnit · `Microsoft.SqlServer.TransactSql.ScriptDom`(`TSql160Parser`) · Serilog(`Log.Warning`)

**Spec:** `docs/superpowers/specs/2026-08-28-spec-material-census-design.md`

## Global Constraints

- **테스트 게이트: 실패 0 · 건너뜀 0 · 경고 0.** 절대 통과 수는 게이트가 **아니다**(환경 내에서도 최대 5까지 흔들린다).
- **공유 체크아웃이다.** 모든 커밋은 `git commit -- <경로>`. 커밋 직전 반드시 `git diff --cached --name-only`로 남의 변경이 안 섞였는지 본다. 새 파일은 그 경로만 `git add` 한 뒤 같은 형태로 커밋한다.
- **CLI는 `dotnet run --project src/ReSet.Cli -- --sweep` 외에 쓰지 않는다.** 특히 `--sp`(실제 LLM 재생성·비용 발생)와 `--job-name`(`output/Jobs/*/agent/steps/`와 `verification/`을 **통째로 지운다**)을 어떤 이유로도 주지 않는다.
- **`output.bak-2026-08-22`는 테스트 재료다**(`CorpusPaths.PriorEdition`). `output.bak-cache17-20260827`·`output.bak-stage4-control-20260828`도 건드리지 않는다.
- 워크트리에서 작업하면 `output`과 `output.bak-2026-08-22`를 **심링크해야** 코퍼스 테스트가 건너뛰지 않는다(`CorpusSkip.Reason`).
- **코퍼스 수치는 실제 리더로 잰다.** 정규식 근사는 이 저장소에서 세 번 틀렸다(`SELECT INTO`·비-sql 펜스·분모 546 대 326).
- 수치에는 **잰 시각**을 함께 적는다.
- 파싱은 소프트 페일한다(AGENTS.md 범주 2) — 예외를 삼키되 `Log.Warning`을 남기고 빈 결과로 진행한다.

## File Structure

| 파일 | 책임 |
| :--- | :--- |
| `src/ReSet.Core/Services/SpecMaterials.cs` (신규) | 재료 카탈로그의 단일 출처. 데이터만 담고 로직 없음 |
| `src/ReSet.Core/Services/SpecMaterialCensus.cs` (신규) | 순수 계수. 명세서 행 수와 DDL 사실 수를 프로시저 단위로 세고 이름으로 접는다 |
| `src/ReSet.Core/Services/StepSweepModels.cs` (수정) | `SweepIndicators`에 census 결과를 **가산으로만** 더한다 |
| `src/ReSet.Core/Services/StepSweepService.cs` (수정) | census를 호출해 지표에 싣는다 |
| `src/ReSet.Core/Services/StepSweepReportWriter.cs` (수정) | 「재료 분모」 절을 인쇄한다 |
| `tests/ReSet.Core.Tests/SpecMaterialsTests.cs` (신규) | 카탈로그 잠금 셋 |
| `tests/ReSet.Core.Tests/SpecMaterialCensusTests.cs` (신규) | 계수·접기·대조군 |
| `tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs` (수정) | 절 인쇄 |

---

### Task 1: `SpecMaterials` 카탈로그와 리더 다섯의 전수 판정

**Files:**
- Create: `src/ReSet.Core/Services/SpecMaterials.cs`
- Test: `tests/ReSet.Core.Tests/SpecMaterialsTests.cs`

**Interfaces:**
- Produces: `SpecMaterials.All` (`IReadOnlyList<SpecMaterial>`), 레코드 `SpecMaterial(string Name, string ReaderTypeName, IReadOnlyList<string> SectionHeadings, bool Enforced, string? DdlCounterpart, IReadOnlyList<string> ConsumingChecks)`. `DdlCounterpart`가 `null`이면 「잴 수 없음」이다.

**착수 전에 반드시 읽을 것:** 설계서 §3-1. **「미확인」을 추측으로 채우지 마라** — 다섯 리더를 실제로 열어 판정하는 것이 이 태스크의 첫 산출이다.

- [ ] **Step 1: 리더 다섯을 열어 각 재료의 (읽는 절 · 강제 여부 · DDL 대응물)을 판정한다**

다섯 리더는 전부 `public static … Extract(IReadOnlyList<(string FileName, string Content)> specs)` 모양이다:

```
SpecStatementFactsExtractor.cs:142   → SpecStatementFacts (DmlRows · SetTargets · LocalVariables · ErrorCodeToOrdinal)
SpecConditionColumnExtractor.cs:89   → SpecConditions
SpecTargetTableExtractor.cs:30       → StepTableSets
SpecReturnCodeExtractor.cs:32        → IReadOnlyList<string>
SpecRoundingShapeExtractor.cs:56     → IReadOnlyCollection<string>
```

각각에 대해 답을 적는다:

1. **어느 절/표를 읽는가** — 헤딩 리터럴을 코드에서 그대로 옮긴다. 변형이 여럿이면 전부(예: 지역 변수 표는 `SpecStatementFactsExtractor.cs:70-90`이 여섯 변형을 기록해 두었고, 접두사로는 `### 지역 변수`·`### 내부 변수` 둘로 좁혀 쓴다).
2. **강제되는가** — 그 헤딩이 `MachineConfirmedTables.All`에 있는가. `MachineConfirmedTables.cs`를 직접 대조한다.
3. **DDL 대응물이 있는가** — 원본 DDL에서 같은 사실을 뽑는 추출기·방문자가 있는가. 없으면 `null`(=「잴 수 없음」).
4. **이 재료가 0이면 어느 검사가 죽는가** — `MechanicalValidator`에서 그 재료를 쓰는 검사 이름을 전부 찾는다.

착수 시점에 이미 확인된 것(이대로 카탈로그에 넣는다):

| 재료 | 읽는 절 | 강제 | DDL 대응물 |
| :--- | :--- | :--- | :--- |
| `DmlRows` | `### DML 범위 (기계 확정 — 수정 금지)` | 예 | `DmlScopeExtractor` |
| `ErrorCodeToOrdinal` | `### 오류 코드 (기계 확정 — 수정 금지)` | 예 | `DmlScopeExtractor` |
| `LocalVariables` | `### 지역 변수`·`### 내부 변수` (변형 6) | **아니오** | `DeclareVariableElement` 방문 (Task 2가 만든다) |
| `SpecConditions` | 모델 산문의 UDF 절·백틱 컬럼 | **아니오** | 없음으로 보임 — **직접 확인할 것** |

**`Visit`만 grep하지 마라.** `SqlStaticParser.cs:1313`은 `ExplicitVisit(DeclareVariableElement)`이고 `ExpressionTypePathExtractor.cs:149`는 `Visit(DeclareVariableElement)`다. `TSqlFragmentVisitor` 파생이 여럿이므로 **어느 방문자인지 먼저 고정하고 찾는다.**

- [ ] **Step 2: 실패하는 테스트를 쓴다 — 잠금 셋**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecMaterialsTests
    {
        /// <summary>
        /// [왜 이 테스트인가] 새 Spec*Extractor가 조용히 들어오면 카탈로그가
        /// "전수"이기를 그친다. (5-3-7)의 결함이 바로 "어디에도 안 적혀 있어서
        /// 아무도 몰랐다"였다.
        /// </summary>
        [Fact]
        public void EverySpecReader_IsListedInTheCatalog()
        {
            var readers = typeof(SpecMaterials).Assembly.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract && t.IsSealed) // static class
                .Where(t => t.Name.StartsWith("Spec", StringComparison.Ordinal)
                            && t.Name.EndsWith("Extractor", StringComparison.Ordinal))
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);

            var listed = SpecMaterials.All.Select(m => m.ReaderTypeName).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(readers.OrderBy(x => x), listed.Intersect(readers).OrderBy(x => x));
            var missing = readers.Except(listed).ToList();
            Assert.True(missing.Count == 0,
                $"카탈로그에 없는 명세서 리더: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// [왜 이 테스트인가] "강제됨"이 거짓이 되면 다음 사람이 "강제된다니 안심"하고
        /// 지나간다 - 침묵이 관측되지 않는 것과 같은 결과다.
        /// </summary>
        [Fact]
        public void EveryEnforcedMaterial_HasItsHeadingInMachineConfirmedTables()
        {
            var enforcedHeadings = MachineConfirmedTables.All
                .Select(t => t.Heading).ToHashSet(StringComparer.Ordinal);

            foreach (var material in SpecMaterials.All.Where(m => m.Enforced))
            {
                Assert.All(material.SectionHeadings, heading =>
                    Assert.True(enforcedHeadings.Contains(heading),
                        $"{material.Name}은 강제됨으로 표시됐으나 헤딩 `{heading}`이 " +
                        "MachineConfirmedTables.All에 없습니다."));
            }
        }

        /// <summary>
        /// [왜 이 테스트인가] 이 저장소는 이미 한 번 당했다 - 주석이
        /// `CheckAddedPredicates`라는 저장소에 없는 이름을 댔고, 평문이라 컴파일
        /// 경고가 안 나 조용했다(실제는 CheckAnchoredStatementExtras).
        /// </summary>
        [Fact]
        public void EveryNamedCheck_ExistsOnMechanicalValidator()
        {
            var validator = typeof(MechanicalValidator);
            foreach (var name in SpecMaterials.All.SelectMany(m => m.ConsumingChecks).Distinct())
            {
                var method = validator.GetMethod(
                    name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                Assert.True(method != null,
                    $"SpecMaterials가 이름 댄 검사 `{name}`이 MechanicalValidator에 없습니다.");
            }
        }
    }
}
```

- [ ] **Step 3: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~SpecMaterialsTests`
Expected: 컴파일 실패 — `SpecMaterials`가 아직 없다.

- [ ] **Step 4: 카탈로그를 만든다**

```csharp
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <param name="Name">재료 이름. SpecStatementFacts의 속성명 등 코드에 실재하는 이름을 쓴다.</param>
    /// <param name="ReaderTypeName">이 재료를 명세서에서 읽는 Spec*Extractor의 타입 이름.</param>
    /// <param name="SectionHeadings">읽는 절의 헤딩 리터럴. 변형이 여럿이면 전부.</param>
    /// <param name="Enforced">그 헤딩이 MachineConfirmedTables.All에 있는가.</param>
    /// <param name="DdlCounterpart">
    /// 원본 DDL에서 같은 사실을 뽑는 자리. null이면 「잴 수 없음」이다 -
    /// 빈 문자열이나 0으로 두지 마십시오. 빈칸은 0으로 읽히고 0은 정상으로 읽힙니다.
    /// </param>
    /// <param name="ConsumingChecks">이 재료가 0이면 죽는 MechanicalValidator의 검사 이름들.</param>
    public sealed record SpecMaterial(
        string Name,
        string ReaderTypeName,
        IReadOnlyList<string> SectionHeadings,
        bool Enforced,
        string? DdlCounterpart,
        IReadOnlyList<string> ConsumingChecks);

    /// <summary>
    /// 명세서에서 읽는 재료 목록의 단일 출처다.
    ///
    /// [왜 손으로 적는가] MachineConfirmedTables와 같은 이유다 - 리플렉션으로 모으면
    /// 「무엇이 강제되는가」와 「무엇이 이 재료를 쓰는가」라는 판정이 코드에 안 남는다.
    /// 그 판정이 어디에도 없었던 것이 (5-3-7)의 결함 그 자체다.
    ///
    /// [테스트가 이 표를 잠근다] SpecMaterialsTests 참고 - 리더 누락·강제 표시의
    /// 거짓·존재하지 않는 검사 이름 셋을 각각 막는다.
    /// </summary>
    public static class SpecMaterials
    {
        public static readonly IReadOnlyList<SpecMaterial> All = new[]
        {
            new SpecMaterial(
                "DmlRows",
                nameof(SpecStatementFactsExtractor),
                new[] { DmlScopeExtractor.DmlScopeTableHeading },
                Enforced: true,
                DdlCounterpart: nameof(DmlScopeExtractor),
                ConsumingChecks: new[]
                {
                    "CheckAnchoredStatementFacts",
                    "CheckAnchoredStatementExtras",
                    "CheckStatementCountAgainstSpec",
                }),
            new SpecMaterial(
                "LocalVariables",
                nameof(SpecStatementFactsExtractor),
                new[] { "### 지역 변수", "### 내부 변수" },
                Enforced: false,
                DdlCounterpart: nameof(SpecMaterialCensus),
                ConsumingChecks: new[] { "CheckSpecLocalVariablesDeclared" }),
            // Step 1의 판정 결과를 나머지 재료 전부에 대해 여기 채운다.
            // 강제됨이면 SectionHeadings에 MachineConfirmedTables.All의 리터럴을
            // 그대로 쓰고, DDL 대응물이 없으면 DdlCounterpart를 null로 둔다.
        };
    }
}
```

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~SpecMaterialsTests`
Expected: PASS 3개.

세 번째 테스트가 빨간불이면 **검사 이름을 지어낸 것이다** — `MechanicalValidator`를 열어 실제 이름으로 고친다. 검사 이름을 지우지 말고 고쳐라.

- [ ] **Step 6: 전체 테스트 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/SpecMaterials.cs tests/ReSet.Core.Tests/SpecMaterialsTests.cs
git diff --cached --name-only
git commit -m "feat: 명세서 재료 카탈로그를 만들고 테스트로 잠근다

어느 검사가 어느 절에 기대는지가 어디에도 안 적혀 있었다 - (5-3-7) 의 결함이
그것이다. 리더 누락 · 강제 표시의 거짓 · 존재하지 않는 검사 이름 셋을 테스트가 막는다." \
  -- src/ReSet.Core/Services/SpecMaterials.cs tests/ReSet.Core.Tests/SpecMaterialsTests.cs
```

---

### Task 2: `SpecMaterialCensus` — 두 수를 세고 프로시저로 접는다

**Files:**
- Create: `src/ReSet.Core/Services/SpecMaterialCensus.cs`
- Test: `tests/ReSet.Core.Tests/SpecMaterialCensusTests.cs`

**Interfaces:**
- Consumes: `SpecMaterials.All` (Task 1)
- Produces: `SpecMaterialCensus.Count(IReadOnlyList<SweepJob> jobs)` → `IReadOnlyList<SpecMaterialCensusRow>`; 레코드 `SpecMaterialCensusRow(string MaterialName, int? DdlFactCount, int SpecRowCount, IReadOnlyList<string> ObjectsWithLoss)`. `DdlFactCount`가 `null`이면 「잴 수 없음」. Task 3이 이 두 이름을 그대로 쓴다.
- Produces: `SpecMaterialCensus.CountDeclaredVariables(string? ddlText)` → `int` (Task 1의 카탈로그가 `DdlCounterpart`로 이름 댄 자리)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecMaterialCensusTests
    {
        private const string SpecWithVariables = @"# Spec

### 지역 변수 및 시스템 값

| 명칭 | 데이터 타입 | 설명 |
| :--- | :--- | :--- |
| @v_intID | INT | 식별자 |
| @v_intCLTotal | MONEY | 합계 |
";

        private const string SpecWithoutVariables = @"# Spec

### 처리 개요

지역 변수 표가 없는 명세서다.
";

        private const string DdlWithTwoDeclares = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_intID INT;
    DECLARE @v_intCLTotal MONEY;
    SELECT @v_intID = 1;
END";

        private static SweepJob Job(string jobName, string procedure, string spec, string ddl) =>
            new(jobName,
                new List<BatchStepPlan>(),
                new Dictionary<string, string>(),
                new[] { ($"{procedure}.md", spec) },
                new Dictionary<string, string> { [procedure] = ddl },
                new Dictionary<string, string>());

        [Fact]
        public void CountDeclaredVariables_CountsEachDeclareOnce()
        {
            Assert.Equal(2, SpecMaterialCensus.CountDeclaredVariables(DdlWithTwoDeclares));
        }

        [Fact]
        public void CountDeclaredVariables_OnUnparsableDdl_ReturnsZeroInsteadOfThrowing()
        {
            Assert.Equal(0, SpecMaterialCensus.CountDeclaredVariables("this is not sql ((("));
        }

        /// <summary>
        /// DDL 에 둘, 명세서에 0 - 소실이다. 객체 이름이 실려야 한다(개수만으로는 못 되짚는다).
        /// </summary>
        [Fact]
        public void Count_WhenDdlHasFactsButSpecHasNone_ReportsLossWithObjectName()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(0, row.SpecRowCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }

        /// <summary>
        /// [대조군] 명세서가 표를 담으면 소실이 아니다. 이 단언이 없으면 위 테스트가
        /// "언제나 소실이라고 말하는" 계수로도 통과한다.
        /// </summary>
        [Fact]
        public void Count_WhenSpecHasTheTable_ReportsNoLoss()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithVariables, DdlWithTwoDeclares) });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(2, row.SpecRowCount);
            Assert.Empty(row.ObjectsWithLoss);
        }

        /// <summary>
        /// [판 접기] 같은 원본 SP 가 Job 다섯 판에 나와도 한 번만 세어야 한다.
        /// 안 접으면 소실이 5배로 세어져 수가 통째로 왜곡된다 - 태스크 12 의 판 접기와
        /// 같은 함정이다.
        /// </summary>
        [Fact]
        public void Count_FoldsTheSameProcedureAcrossJobs()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
                Job("JobB", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
                Job("JobC", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
            });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }

        /// <summary>
        /// [잴 수 없음] DdlCounterpart 가 null 인 재료는 DdlFactCount 도 null 이어야 한다.
        /// 0 으로 두면 "정상"으로 읽힌다.
        /// </summary>
        [Fact]
        public void Count_ForMaterialWithoutDdlCounterpart_LeavesDdlFactCountNull()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            foreach (var material in SpecMaterials.All.Where(m => m.DdlCounterpart == null))
            {
                Assert.Null(rows.Single(r => r.MaterialName == material.Name).DdlFactCount);
            }
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~SpecMaterialCensusTests`
Expected: 컴파일 실패 — `SpecMaterialCensus`가 아직 없다.

- [ ] **Step 3: 계수를 구현한다**

`CountDeclaredVariables`는 저장소의 추출기 관용구를 그대로 따른다(`AggregateAssignmentExtractor.cs:43-70` 참고 — `TSql160Parser(true)` · 파싱 오류면 빈 결과 · `catch`에서 `Log.Warning` 후 소프트 페일).

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="DdlFactCount">
    /// null 이면 「잴 수 없음」이다 - DDL 대응물이 없는 재료. 0 과 구별해야 한다.
    /// </param>
    /// <param name="ObjectsWithLoss">
    /// DdlFactCount &gt; 0 인데 SpecRowCount == 0 인 객체 이름. 개수가 아니라 이름을
    /// 싣는다 - 이름이 없으면 다음 사람이 되짚을 수 없다.
    /// </param>
    public sealed record SpecMaterialCensusRow(
        string MaterialName,
        int? DdlFactCount,
        int SpecRowCount,
        IReadOnlyList<string> ObjectsWithLoss);

    /// <summary>
    /// 명세서 재료가 원본 DDL 대비 소실됐는지 센다.
    ///
    /// [이 계기가 못 하는 것] 원인을 귀속하지 않는다. DdlFactCount &gt; 0 이고
    /// SpecRowCount == 0 이어도 「모델이 표를 안 썼다」와 「리더가 못 읽는다」가 같은
    /// 수로 보인다. 실물이 있다 - UP_UTIL_SETTLE_SUMMARY_EXTRA 는 지역 변수 표를
    /// 쓰긴 썼는데 전용 헤딩이 없어 리더가 못 읽는다
    /// (SpecStatementFactsExtractor 의 알려진 한계 6번).
    /// </summary>
    public static class SpecMaterialCensus
    {
        public static IReadOnlyList<SpecMaterialCensusRow> Count(IReadOnlyList<SweepJob>? jobs)
        {
            var rows = new List<SpecMaterialCensusRow>();
            if (jobs == null) return rows;

            // [판 접기] 같은 원본 SP 가 최대 다섯 판에 나온다. 프로시저 이름으로 접지
            // 않으면 같은 소실이 다섯 번 세어져 수가 통째로 왜곡된다.
            var specByProcedure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ddlByProcedure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
            {
                foreach (var (fileName, content) in job.Specs)
                {
                    var name = StripExtension(fileName);
                    if (!specByProcedure.ContainsKey(name)) specByProcedure[name] = content;
                }
                foreach (var (procedure, ddl) in job.DdlByProcedure)
                {
                    if (!ddlByProcedure.ContainsKey(procedure)) ddlByProcedure[procedure] = ddl;
                }
            }

            var specs = specByProcedure
                .Select(kv => (FileName: kv.Key, Content: kv.Value))
                .ToList();
            var facts = SpecStatementFactsExtractor.Extract(specs);

            foreach (var material in SpecMaterials.All)
            {
                var specRows = 0;
                int? ddlFacts = material.DdlCounterpart == null ? null : 0;
                var loss = new List<string>();

                foreach (var procedure in specByProcedure.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    var specCount = SpecRowCountFor(material.Name, procedure, facts);
                    specRows += specCount;

                    if (ddlFacts == null) continue;

                    ddlByProcedure.TryGetValue(procedure, out var ddl);
                    var ddlCount = DdlFactCountFor(material.Name, ddl);
                    ddlFacts += ddlCount;

                    if (ddlCount > 0 && specCount == 0) loss.Add(procedure);
                }

                rows.Add(new SpecMaterialCensusRow(material.Name, ddlFacts, specRows, loss));
            }

            return rows;
        }

        /// <summary>
        /// [왜 이 사상이 switch 인가] 재료마다 세는 대상이 다르므로 일반화가 안 된다.
        /// 카탈로그에 재료를 더하면 여기도 함께 더해야 한다 - 안 더하면 그 재료는
        /// 0 을 찍는다. 그 조용함을 SpecMaterialCensusTests 의
        /// Count_ForMaterialWithoutDdlCounterpart_LeavesDdlFactCountNull 가 막지
        /// 못하므로, 새 재료를 더할 때 반드시 대조군 테스트를 함께 더하십시오.
        /// </summary>
        private static int SpecRowCountFor(
            string materialName,
            string procedure,
            IReadOnlyDictionary<string, SpecStatementFacts> facts)
        {
            if (!facts.TryGetValue(procedure, out var f)) return 0;
            return materialName switch
            {
                "LocalVariables" => f.LocalVariables.Count,
                "DmlRows" => f.DmlRows.Count,
                "SetTargets" => f.SetTargets.Count,
                "ErrorCodeToOrdinal" => f.ErrorCodeToOrdinal.Count,
                _ => 0,
            };
        }

        private static int DdlFactCountFor(string materialName, string? ddl) =>
            materialName switch
            {
                "LocalVariables" => CountDeclaredVariables(ddl),
                _ => 0,
            };

        /// <summary>
        /// 원본 DDL 이 DECLARE 한 변수의 수. 커서 선언(DeclareCursorStatement)은 세지
        /// 않는다 - 검사 D 가 보는 것은 값 변수다.
        /// </summary>
        public static int CountDeclaredVariables(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return 0;

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0)) return 0;

                var visitor = new DeclaredVariableVisitor();
                fragment.Accept(visitor);
                return visitor.Names.Count;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[SpecMaterialCensus] DECLARE 수집 실패 - 0으로 진행합니다.");
                return 0;
            }
        }

        private static string StripExtension(string fileName) =>
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

        private sealed class DeclaredVariableVisitor : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            // [Visit 인가 ExplicitVisit 인가] 이 저장소에 둘 다 전례가 있다 -
            // SqlStaticParser.cs:1313 은 ExplicitVisit, ExpressionTypePathExtractor.cs:149
            // 는 Visit 다. 여기서는 하강을 막을 이유가 없으므로 Visit 을 쓴다.
            public override void Visit(DeclareVariableElement node)
            {
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Names.Add(name!);
            }
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~SpecMaterialCensusTests`
Expected: PASS 6개.

- [ ] **Step 5: 전체 테스트 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecMaterialCensus.cs tests/ReSet.Core.Tests/SpecMaterialCensusTests.cs
git diff --cached --name-only
git commit -m "feat: 명세서 재료를 원본 DDL 대비로 세고 프로시저로 접는다

같은 원본 SP 가 최대 다섯 판에 나오므로 이름으로 접는다 - 안 접으면 같은 소실이
다섯 번 세어진다. DDL 대응물이 없는 재료는 0 이 아니라 null(「잴 수 없음」)이다 -
빈칸은 0 으로 읽히고 0 은 정상으로 읽힌다." \
  -- src/ReSet.Core/Services/SpecMaterialCensus.cs tests/ReSet.Core.Tests/SpecMaterialCensusTests.cs
```

---

### Task 3: 스윕이 census를 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepModels.cs` (`SweepIndicators`에 속성 하나 가산)
- Modify: `src/ReSet.Core/Services/StepSweepService.cs` (`Sweep`이 census를 호출)
- Test: `tests/ReSet.Core.Tests/StepSweepServiceTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: `SpecMaterialCensus.Count` · `SpecMaterialCensusRow` (Task 2)
- Produces: `SweepIndicators.MaterialCensus` (`IReadOnlyList<SpecMaterialCensusRow>`, 기본값 빈 목록). Task 4가 이 이름을 그대로 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`StepSweepServiceTests.cs`에 더한다. 기존 픽스처 `OneJobInput()`의 관용구를 따르되, 명세서와 DDL을 실어야 한다.

```csharp
        /// <summary>
        /// [왜 Sweep 이음매인가] 계수가 배선까지 함께 시험된다. 리플렉션으로 내부
        /// 함수를 부르면 "계산은 맞는데 아무도 안 부른다"를 못 잡는다.
        /// </summary>
        [Fact]
        public void Sweep_CarriesTheMaterialCensus()
        {
            var input = InputWithSpecAndDdl(
                procedure: "dbo.P",
                spec: "# Spec\n\n### 처리 개요\n\n표가 없다.\n",
                ddl: "CREATE PROCEDURE dbo.P AS BEGIN DECLARE @v INT; SELECT @v = 1; END");

            var report = StepSweepService.Sweep(input);

            var row = report.Indicators.MaterialCensus
                .Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(1, row.DdlFactCount);
            Assert.Equal(0, row.SpecRowCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }
```

`InputWithSpecAndDdl`은 이 파일에 이미 있는 픽스처 헬퍼 관용구를 따라 새로 만든다 — `SweepJob(jobName, steps, stepMarkdownByCode, specs, ddlByProcedure, dateParameterByProcedure)` 여섯 인자를 그대로 채우고, `steps`와 `stepMarkdownByCode`는 비워도 이 단언에 영향이 없다(census는 단계를 안 본다).

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~StepSweepServiceTests.Sweep_CarriesTheMaterialCensus`
Expected: 컴파일 실패 — `SweepIndicators.MaterialCensus`가 없다.

- [ ] **Step 3: 지표에 가산으로 더한다**

`StepSweepModels.cs`의 `SweepIndicators`에 **위치 인자를 건드리지 말고** `{ get; init; }` 속성으로 더한다(로드맵 4가 침묵 분모를 더한 것과 같은 형태 — `AnchorsResolved` 참고):

```csharp
        /// <summary>
        /// 명세서 재료가 원본 DDL 대비 소실됐는지의 조사 결과.
        ///
        /// [분모가 다르다] 이 목록의 단위는 **프로시저**이고 이 보고서의 다른 수치는
        /// (Job, 단계) 쌍이다. 같은 표에서 나누지 마십시오.
        ///
        /// [이 값이 재는 것] 소실이지 원인이 아니다. SpecMaterialCensus 문서 참고.
        /// </summary>
        public IReadOnlyList<SpecMaterialCensusRow> MaterialCensus { get; init; }
            = new List<SpecMaterialCensusRow>();
```

`StepSweepService.Sweep`에서 지표를 만드는 자리에 한 줄 더한다:

```csharp
                MaterialCensus = SpecMaterialCensus.Count(input.Jobs),
```

- [ ] **Step 4: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~StepSweepServiceTests`
Expected: PASS. **기존 테스트가 하나도 깨지지 않아야 한다** — 깨졌다면 가산이 아니라 기존 동작을 바꾼 것이므로 고쳐서 통과시키지 말고 그대로 보고하라.

- [ ] **Step 5: 전체 테스트 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/StepSweepModels.cs src/ReSet.Core/Services/StepSweepService.cs tests/ReSet.Core.Tests/StepSweepServiceTests.cs
git diff --cached --name-only
git commit -m "feat: 스윕이 재료 분모 조사를 지표에 싣는다

위치 인자를 안 건드리고 init 속성으로만 더한다 - 순수 가산이라 기존 지표가
문자 단위로 불변이어야 한다." \
  -- src/ReSet.Core/Services/StepSweepModels.cs src/ReSet.Core/Services/StepSweepService.cs tests/ReSet.Core.Tests/StepSweepServiceTests.cs
```

---

### Task 4: 보고서가 「재료 분모」 절을 인쇄한다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepReportWriter.cs`
- Test: `tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs`

**Interfaces:**
- Consumes: `SweepIndicators.MaterialCensus` (Task 3)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        /// <summary>
        /// [라벨과 값이 뒤바뀌는 것을 잡는다] 칸마다 다른 값을 넣어야 뒤바뀜이 잡힌다 -
        /// 같은 값을 여러 칸에 넣으면 통과해 버린다.
        /// </summary>
        [Fact]
        public void MaterialCensusSection_PrintsCountsAndLossObjectNames()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(3, 2, 1)
                {
                    MaterialCensus = new[]
                    {
                        new SpecMaterialCensusRow("LocalVariables", 16, 0, new[] { "dbo.A", "dbo.B" }),
                        new SpecMaterialCensusRow("SpecConditions", null, 137, Array.Empty<string>()),
                    },
                },
                new HarnessGaps(
                    new List<string>(), 51, 326, 18,
                    StepInterfacesWereNull: true,
                    RunRowOwnedTablesWereNull: true,
                    KnownTableNamesWereEmpty: true));

            var section = Section(
                StepSweepReportWriter.Render(report, "abc1234", "17", 0), "## 재료 분모");

            Assert.Contains("이 절의 분모는 **프로시저**다", section);
            Assert.Contains("이 수는 소실을 세지 원인을 귀속하지 않는다", section);
            Assert.Contains("| LocalVariables | 16 | 0 | dbo.A · dbo.B |", section);
            // 「잴 수 없음」이 빈칸이나 0 으로 새지 않는다
            Assert.Contains("| SpecConditions | 잴 수 없음 | 137 | — |", section);
            Assert.DoesNotContain("| SpecConditions | 0 |", section);
        }
```

**시그니처를 확인하고 썼다.** 이 클래스의 공개 진입점은 `Write`가 아니라
`StepSweepReportWriter.Render(SweepReport report, string commit, string formatVersion, int)`이고
(`StepSweepReportWriter.cs:25`), 이 테스트 파일에는 절만 잘라 내는 `Section(markdown, heading)`
헬퍼가 이미 있다(`StepSweepReportWriterTests.cs:54` 등). `HarnessGaps`의 인자도 기존
테스트가 쓰는 형태를 그대로 베꼈다 — 지어내지 마라.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~StepSweepReportWriterTests.MaterialCensusSection_PrintsCountsAndLossObjectNames`
Expected: FAIL — `## 재료 분모`가 출력에 없다.

- [ ] **Step 3: 절을 인쇄한다**

기존 「## 침묵 분모」 절(`StepSweepReportWriter.cs:263` 부근)과 같은 관용구로, 그 절 **뒤에** 더한다:

```csharp
            b.AppendLine();
            b.AppendLine("## 재료 분모");
            b.AppendLine();
            b.AppendLine(
                "검사는 재료가 비면 조기 반환한다 - 조용히 꺼지고 발화가 0이 된다. " +
                "이 절은 명세서 재료가 원본 DDL 대비 소실됐는지를 센다. " +
                "**이 절의 분모는 **프로시저**다** - 위 표의 (Job, 단계) 쌍과 다른 단위이므로 " +
                "쌍 수로 나누지 마십시오. **이 수는 소실을 세지 원인을 귀속하지 않는다** - " +
                "「모델이 표를 안 썼다」와 「리더가 못 읽는다」가 같은 수로 보인다.");
            b.AppendLine();
            b.AppendLine("| 재료 | DDL 사실 | 명세서 행 | 소실 객체 |");
            b.AppendLine("| :--- | ---: | ---: | :--- |");
            foreach (var row in indicators.MaterialCensus)
            {
                var ddl = row.DdlFactCount?.ToString() ?? "잴 수 없음";
                var loss = row.ObjectsWithLoss.Count == 0
                    ? "—"
                    : string.Join(" · ", row.ObjectsWithLoss);
                b.AppendLine($"| {row.MaterialName} | {ddl} | {row.SpecRowCount} | {loss} |");
            }
```

- [ ] **Step 4: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter FullyQualifiedName~StepSweepReportWriterTests`
Expected: PASS. 기존 절의 출력이 하나도 안 바뀌어야 한다.

- [ ] **Step 5: 전체 테스트 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/StepSweepReportWriter.cs tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs
git diff --cached --name-only
git commit -m "feat: 보고서가 재료 분모 절을 인쇄한다

절 머리에 분모가 프로시저임을 못 박는다 - 안 적으면 다음 사람이 쌍 수로 나눈다.
「잴 수 없음」을 빈칸이나 0 으로 두지 않는다." \
  -- src/ReSet.Core/Services/StepSweepReportWriter.cs tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs
```

---

### Task 5: 변이로 계기 자체를 검증한다

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-28-material-census-mutations.md`

**Interfaces:**
- Consumes: Task 2~4의 구현 전부

**이 태스크가 존재하는 이유:** 선언만 되고 한 번도 증가하지 않는 계수는 보고서에 영원히 0을 찍으면서 「쟀다」는 인상을 준다. **(5-3-7)이 정확히 그 모양이었으므로 계기 자신이 그 함정에 빠지면 안 된다.**

- [ ] **Step 1: 전용 워크트리를 만든다**

```bash
git worktree add /tmp/reset-census-mutation HEAD
```

**공유 체크아웃에서 제품 코드를 변이시키지 마라** — 다른 세션이 그 변이를 커밋할 수 있다.

- [ ] **Step 2: 변이 셋을 하나씩 넣고 죽는 테스트를 기록한다**

| # | 변이 | 죽어야 하는 테스트 |
| ---: | :--- | :--- |
| 1 | `SpecMaterialCensus.Count`의 판 접기 제거(`ContainsKey` 가드를 지워 매번 덮어쓰게) | `Count_FoldsTheSameProcedureAcrossJobs` |
| 2 | `ddlCount > 0 && specCount == 0` 조건을 `specCount == 0`으로 넓힘 | `Count_WhenSpecHasTheTable_ReportsNoLoss` |
| 3 | `DdlFactCount`의 `null`을 `0`으로 바꿈 | `Count_ForMaterialWithoutDdlCounterpart_LeavesDdlFactCountNull` · 보고서 절 테스트 |

각 변이마다: 변이 넣기 → `dotnet test --filter FullyQualifiedName~SpecMaterialCensus` → **정확히 의도한 테스트만** 죽는지 확인 → `git checkout -- .`로 되돌리기.

- [ ] **Step 3: 안 죽은 변이가 있으면 테스트를 보강한다**

**안 죽은 변이는 테스트의 결함이지 변이의 결함이 아니다.** 그 자리를 잡는 단언을 더하고 다시 돌린다.

- [ ] **Step 4: 보고서를 쓴다**

`docs/audit-reports/sweeps/2026-08-27-silence-denominator-mutations.md`의 양식을 그대로 따른다. 담을 것: 변이 · 기준선 → 변이 후 값 · 판정(죽음/생존) · **안 죽은 변이가 있으면 그 사실과 보강 내용**.

- [ ] **Step 5: 워크트리를 제거하고 제품 코드가 무변경인지 확인한다**

```bash
git worktree remove /tmp/reset-census-mutation
git status --short
```

- [ ] **Step 6: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-28-material-census-mutations.md
git diff --cached --name-only
git commit -m "docs: 재료 분모 계기를 변이로 검증한다

선언만 되고 증가하지 않는 계수는 영원히 0 을 찍으면서 쟀다는 인상을 준다 -
(5-3-7) 이 그 모양이었으므로 계기 자신이 그 함정에 빠지면 안 된다." \
  -- docs/audit-reports/sweeps/2026-08-28-material-census-mutations.md
```

---

### Task 6: 실물 스윕을 뜨고 결과를 기록한다

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-28-step-sweep-material-census.md` (스윕이 생성)
- Modify: `docs/known-defects.md` ((5-3-7)에 실측 결과 문단 추가)
- Modify: `/Users/payletter/.claude/projects/-Users-payletter-git-root-ReSet/memory/axis-b-roadmap.md`

**Interfaces:**
- Consumes: Task 1~5 전부

- [ ] **Step 1: 작업 트리가 깨끗한지 확인한다**

```bash
git status --short
```

**남의 변경이 있으면 그대로 두고 진행하되, 보고서에 그 사실을 적는다.** 스윕 보고서는 실행 시점의 트리 청결도를 기록하지 않으므로 사람이 적어야 한다.

- [ ] **Step 2: 스윕을 돌린다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep
```

**이 인자 말고는 주지 않는다.**

- [ ] **Step 3: 「재료 분모」 절을 읽고 판정한다**

각 재료에 대해 답을 적는다:

- `DDL 사실 > 0 ∧ 명세서 행 = 0` → **소실.** 객체 이름을 확인한다.
- `잴 수 없음` → 그대로 둔다. **0으로 바꿔 적지 마라.**
- 기존 지표(다중 SP · 미포함 코드 · 미지 코드 · 파싱 실패 제외 · 앵커 재사용)가 `2026-08-28-step-sweep-post-cache17.md`와 **문자 단위로 같은지** 대조한다. 다르면 이 회차의 삽입이 순수 가산이 아니라는 뜻이므로 **멈추고 보고한다.**

- [ ] **Step 4: `known-defects.md`의 (5-3-7)에 실측 문단을 더한다**

담을 것:

- 잰 시각과 커밋
- 재료별 (DDL 사실 · 명세서 행 · 소실 객체 **이름**)
- **노출된 재료가 몇이었는가** — (5-3-7)이 물은 「같은 형태를 다른 검사에서 전수로 찾는다」의 답
- **「잰 것」과 「안 잰 것」** — 안 잰 것에 반드시 포함: 원인 귀속(모델이 안 썼나/리더가 못 읽나) · `잴 수 없음`으로 남은 재료
- 이 계기가 **강제를 걸지 않았다**는 것과, 다음 회차의 범위를 이 수치가 정한다는 것

- [ ] **Step 5: 로드맵 메모를 갱신한다**

`memory/axis-b-roadmap.md`의 「⚠⚠ 새 결함 — 검사 D」 절에 **계기 완료**를 적고, `How to apply`를 실측에 맞춰 고친다. 강제(프롬프트·L1 승격)는 **미결로 남기고 그 입력이 이 수치임**을 적는다.

- [ ] **Step 6: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-28-step-sweep-material-census.md
git diff --cached --name-only
git commit -m "docs: 재료 분모를 실물 코퍼스에서 뜨고 노출된 재료를 확정한다

(5-3-7) 이 물은 「같은 형태를 다른 검사에서 전수로 찾는다」의 답이다.
이 회차는 강제를 걸지 않는다 - 다음 회차의 범위를 이 수치가 정한다." \
  -- docs/audit-reports/sweeps/2026-08-28-step-sweep-material-census.md docs/known-defects.md
```

---

## Self-Review

**1. 설계서 각 절의 담당 태스크**

| 설계서 | 태스크 |
| :--- | :--- |
| §0 착수 실측 | 계획서 머리에 그대로 옮김 |
| §1 문제·재발 | 1 (카탈로그 주석) · 5 (변이의 근거) |
| §2 하는 것 1~4 | 1 · 1 · 3+4 · 1 |
| §2 안 하는 것 | Global Constraints · 6 Step 4 |
| §3-1 재료 지도 | 1 |
| §3-2 자리와 단위 | 3 (지표) · 2 (판 접기) |
| §3-3 산출 | 4 |
| §3-4 잠금 테스트 넷 | 1 (셋) · 2·3 (대조군) · 5 (변이) |
| §3-5 원인 귀속 불가 | 2 (클래스 주석) · 4 (절 문구) · 6 Step 4 |
| §4 위험 여섯 | 2 (접기·잴 수 없음) · 5 (배선) · 1 (카탈로그) · 1 Step 1 (미확인) · 2 (소프트 페일) |
| §5 게이트와 규약 | Global Constraints |
| §6 닫지 않는 것 | 6 Step 4~5 |

**빠진 것 없음.**

**2. 플레이스홀더 스캔**

`SpecMaterials.All`의 「Step 1의 판정 결과를 나머지 재료 전부에 대해 여기 채운다」는 **의도된 빈칸이 아니라 Task 1 Step 1이 산출하는 값의 자리**다 — 무엇을 판정해야 하는지(네 물음)와 이미 확정된 네 행을 같은 태스크가 함께 싣는다. 설계서 §3-1이 「미확인을 추측으로 채우지 않는 것이 이 절의 핵심」이라 못 박았으므로 여기서 지어내면 계획이 설계를 어긴다. 그 밖에 「적절히 처리」류 문장 없음.

**3. 이름 일관성**

`SpecMaterial`·`SpecMaterials.All`(1 정의 → 2 소비) · `SpecMaterialCensusRow`·`SpecMaterialCensus.Count`·`CountDeclaredVariables`(2 정의 → 3 소비) · `SweepIndicators.MaterialCensus`(3 정의 → 4 소비) · `DdlFactCount`/`SpecRowCount`/`ObjectsWithLoss`(2 정의 → 4 인쇄) · `DdlCounterpart`(1 정의 → 2 분기). **어긋남 없음.**

**자체 검토에서 고친 것 하나.** Task 4의 테스트 초안이 `StepSweepReportWriter.Write(report, context)`를
불렀는데 **그런 메서드는 없다** — 실물은 `Render(report, commit, formatVersion, int)`이고 테스트에는
`Section(markdown, heading)` 헬퍼가 있다. 시그니처를 실물로 확인해 고쳤다. 계획서가 없는 이름을
부르면 실행자가 그 자리에서 막힌다.

**한 가지 설계서와의 차이를 명시한다.** 설계서 §3-4는 잠금을 「테스트 넷」이라 적었으나, 넷째(계수 배선)는 Task 2의 대조군과 Task 3의 Sweep 이음매 테스트 **둘로 갈라 놓았다** — 순수 계산과 배선은 서로 다른 실패 양식이라 한 테스트로 묶으면 어느 쪽이 깨졌는지 안 보인다.
