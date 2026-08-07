# 마이그레이션 지시서 분할 및 Step 단위 코드 생성 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 외부 코딩 에이전트에 넘기는 7,816줄 단일 지시서를 Step 단위 번들로 분할하고, 코드 생성을 회차 단위로 오케스트레이션한다.

**Architecture:** 계획서는 이미 `SplitGeneration.Skeleton`/`Sections`로 조각 생성된 뒤 합쳐진다. 그 조각을 `PlanLayout`으로 호출부까지 나르고, **조각은 경계 앵커로만** 쓰며 본문은 정제가 끝난 최종 문서에서 잘라낸다. 잘라낸 조각으로 `agent/` 번들을 구성하고, `CodegenWorkflowOrchestrator`가 Bootstrap → Step 1..N → Assembly 회차를 순차로 돌린다.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, NSubstitute 5.3.0, Serilog

## Global Constraints

- 타깃 프레임워크는 `net10.0`. 테스트 프로젝트는 `tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` 하나뿐이며 새 테스트 프로젝트를 만들지 않는다.
- 주석은 한국어로 쓰고 **"무엇"이 아니라 "왜"**를 적는다. 기존 파일들의 주석 밀도와 어조를 따른다.
- 로깅은 Serilog 정적 `Log`를 쓴다. 메시지 템플릿은 한국어, 파라미터는 `{PascalCase}`.
- `appsettings.json`의 `CodegenSettings` 스키마, `ArgumentTemplateResolver`, `ICodingEngine`, `ExternalCliCodingEngine`, `DataAccessPolicy.InstructionRules`의 **문구**는 변경하지 않는다.
- `AppendFeedbackToInstructionsAsync`의 시그니처는 변경하지 않는다. 넘기는 경로만 달라진다.
- `task-*.md`는 반드시 `agent/` **직하**에 둔다. 하위 디렉터리에 두면 `ArgumentTemplateResolver.ResolveJobDirectory`(두 단계 위 = Job 루트)가 `{jobDir}`을 `agent/`로 해석해 `--add-dir`이 `raw/ddl/`과 `Procedures/*/docs/Spec.md`를 덮지 못한다.
- 분할이 실패해도 **지침 순서 교정은 항상 적용한다.** 이것이 이 작업에서 가장 값싸고 효과가 큰 변경이다.
- 부분 분할을 하지 않는다. 일부 Step의 경계를 못 찾으면 전체를 단일 파일 폴백으로 떨어뜨린다. 빈 `steps/*.md`가 조용히 생기는 것이 최악이다.
- 빌드 명령: `dotnet build ReSet.slnx -v q --nologo`. 현재 경고 8개 / 오류 0개가 기준선이며, 오류 0개를 유지한다.
- 테스트 명령: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~<TestClass>"`

## 참조

- 설계: `docs/superpowers/specs/2026-08-07-migration-instructions-split-design.md`
- 조각 생성 쪽: `docs/superpowers/specs/2026-08-06-batch-plan-step-split-design.md`

## File Structure

**신규 (ReSet.Core)**

| 파일 | 책임 |
|---|---|
| `Models/PlanLayout.cs` | 조각 전달 계약 (record) |
| `Services/MarkdownSectionLocator.cs` | 펜스 인식 헤딩 탐색. `BatchPlanAssembler`의 private 헬퍼를 승격 |
| `Services/PlanBoundaryResolver.cs` | 최종 문서를 `PlanSlices`로 자르는 3단 폴백 |
| `Services/InstructionEntryPointComposer.cs` | 진입점 마크다운 조립 (순수 함수) |
| `Services/TaskFileComposer.cs` | `task-*.md` 조립 (순수 함수) |
| `Services/InstructionBundleWriter.cs` | 디스크 쓰기와 파일 배치 |
| `Services/AgentProgressStore.cs` | `progress.json` 입출력 + `todo.md` 렌더링 |

**신규 (ReSet.Validator.Core)**

| 파일 | 책임 |
|---|---|
| `Models/CodegenStage.cs` | 회차 하나와 회차 목록 (record) |

**수정**

| 파일 | 변경 |
|---|---|
| `Models/ConsolidatedPipelineResult.cs` | `Layout` 추가 (기본값 `null`) |
| `Services/BatchPlanAssembler.cs` | 헬퍼를 `MarkdownSectionLocator`에 위임 |
| `Services/VerificationPipelineOrchestrator.cs` | 성공 반환 2곳에서 `PlanLayout` 구성 |
| `Services/MetadataExporter.cs` | `ExportConsolidatedMigrationInstructionsAsync`를 `InstructionBundleWriter` 호출로 축소 |
| `Services/DataAccessPolicy.cs` | 계약 스텁에 Repository·Step 등록 규약 추가 |
| `Validator.Core/Services/FileMappingService.cs` | 명시적 쌍 주입 오버로드 |
| `Validator.Core/Services/CodeVerificationOrchestrator.cs` | 검증 스코프 오버로드 |
| `Validator.Core/Services/CodegenWorkflowOrchestrator.cs` | `RunStagedWorkflowAsync` 추가 |
| `Cli/Program.cs` (`:895`, `:1414`) | 배선 |

## 구현 단계

- **Phase A (Task 1~5)** — 계획 조각을 산출물까지 나른다
- **Phase B (Task 6~10)** — 번들을 쓴다
- **Phase C (Task 11~13)** — 회차를 돌린다
- **Phase D (Task 14~15)** — 계약과 배선

---

## Phase A — 계획 조각을 산출물까지 나른다

### Task 1: `PlanLayout` 모델과 파이프라인 결과 확장

**Files:**
- Create: `src/ReSet.Core/Models/PlanLayout.cs`
- Modify: `src/ReSet.Core/Models/ConsolidatedPipelineResult.cs`
- Test: `tests/ReSet.Core.Tests/PlanLayoutTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan` (기존, `ReSet.Core.Services`)
- Produces: `PlanLayout(string? Skeleton, IReadOnlyDictionary<string,string>? Sections, IReadOnlyList<BatchStepPlan>? Steps, IReadOnlyDictionary<string,string>? FloorViolations)`, `PlanLayout.IsSplitAvailable`, `ConsolidatedPipelineResult`의 5번째 위치 파라미터 `PlanLayout? Layout = null`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/PlanLayoutTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PlanLayoutTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계", new[] { "UP_X" }, new[] { "dbo.T" }, new[] { "-1" }, false);

        [Fact]
        public void IsSplitAvailable_ShouldBeFalse_WhenSectionsMissing()
        {
            // 조각이 없으면 앵커를 만들 수 없다. Skeleton만으로는 Step 경계를 모른다.
            var layout = new PlanLayout("골격", null, new[] { Step("S01") }, null);

            Assert.False(layout.IsSplitAvailable);
        }

        [Fact]
        public void IsSplitAvailable_ShouldBeFalse_WhenSectionsEmpty()
        {
            var layout = new PlanLayout("골격", new Dictionary<string, string>(), new[] { Step("S01") }, null);

            Assert.False(layout.IsSplitAvailable);
        }

        [Fact]
        public void IsSplitAvailable_ShouldBeTrue_WhenSectionsPresent()
        {
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 본문" },
                new[] { Step("S01") },
                null);

            Assert.True(layout.IsSplitAvailable);
        }

        [Fact]
        public void ConsolidatedPipelineResult_ShouldDefaultLayoutToNull()
        {
            // 기본값이 있어야 기존 호출부 4곳이 변경 없이 컴파일된다.
            var result = new ConsolidatedPipelineResult("계획", null, null, VerificationOutcome.Passed);

            Assert.Null(result.Layout);
        }

        [Fact]
        public void ConsolidatedPipelineResult_ShouldCarryLayout_WhenProvided()
        {
            var layout = new PlanLayout("골격", new Dictionary<string, string> { ["S01"] = "본문" }, null, null);

            var result = new ConsolidatedPipelineResult("계획", null, null, VerificationOutcome.Passed, layout);

            Assert.Same(layout, result.Layout);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanLayoutTests"
```

Expected: 컴파일 오류 — `PlanLayout` 형식을 찾을 수 없음

- [ ] **Step 3: `PlanLayout`을 만든다**

`src/ReSet.Core/Models/PlanLayout.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;

namespace ReSet.Core.Models;

/// <summary>
/// 계획서가 어떤 조각들로 만들어졌는지를 산출물 작성부까지 나른다.
///
/// 조각을 나르는 이유는 본문을 다시 쓰기 위해서가 아니라 <b>경계를 알기 위해서</b>다.
/// split.Markdown이 나온 뒤에도 최종 문서는 L1 정제·자가 교정·구제 채택으로 계속
/// 바뀌므로(VerificationPipelineOrchestrator의 CleansedMarkdown/rescued 경로), 조각
/// 본문을 그대로 산출물에 실으면 BatchMigrationPlan.md와 steps/*.md의 내용이 조용히
/// 달라진다. Sections는 헤딩 앵커로만 쓰고 본문은 언제나 최종 문서에서 잘라낸다.
/// </summary>
/// <param name="Skeleton">개요·흐름도·검증 SQL·공통 규약. 단일 호출로 생성됐으면 null.</param>
/// <param name="Sections">단계 코드 → 단계 섹션 마크다운. 경계 앵커의 출처.</param>
/// <param name="Steps">목차가 선언한 단계 목록. 앵커 탐색이 실패했을 때의 2순위 근거이자 회차 정의.</param>
/// <param name="FloorViolations">단계 코드 → 하한 미달 사유. 해당 단계 파일에 배너로 실린다.</param>
public sealed record PlanLayout(
    string? Skeleton,
    IReadOnlyDictionary<string, string>? Sections,
    IReadOnlyList<BatchStepPlan>? Steps,
    IReadOnlyDictionary<string, string>? FloorViolations)
{
    /// <summary>
    /// 단계 분할을 시도할 수 있는가. Sections가 비어 있으면 앵커가 없으므로 시도 자체가 성립하지 않는다.
    /// </summary>
    public bool IsSplitAvailable => Sections is { Count: > 0 };
}
```

- [ ] **Step 4: `ConsolidatedPipelineResult`에 `Layout`을 추가한다**

`src/ReSet.Core/Models/ConsolidatedPipelineResult.cs`의 record 선언을 다음으로 바꾼다. XML 주석 블록에 `Layout` 항목을 추가한다.

```csharp
/// <param name="Layout">계획서를 만든 조각들. 산출물 분할의 경계 근거이며, 단일 호출로
/// 생성됐거나 파이프라인이 실패하면 null이다. 기본값이 null이므로 이 값을 쓰지 않는
/// 호출부는 변경할 필요가 없다.</param>
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome,
    PlanLayout? Layout = null);
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanLayoutTests"
```

Expected: PASS 5건

- [ ] **Step 6: 전체 빌드로 기존 호출부가 깨지지 않았는지 확인한다**

```bash
dotnet build ReSet.slnx -v q --nologo
```

Expected: 오류 0개. `VerificationPipelineOrchestrator.cs`의 `new ConsolidatedPipelineResult(...)` 4곳(`:1836`, `:2088`, `:2102`, `:2106`)이 기본값 덕분에 무변경으로 컴파일된다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Models/PlanLayout.cs src/ReSet.Core/Models/ConsolidatedPipelineResult.cs tests/ReSet.Core.Tests/PlanLayoutTests.cs
git commit -m "feat: carry plan generation slices to the artifact writer

The slices exist only to locate section boundaries - bodies must still
come from the final document, which keeps changing after the split
returns."
```

---

### Task 2: 펜스 인식 헤딩 탐색기를 공용화한다

`BatchPlanAssembler`는 이미 펜스(```) 안의 `## `를 헤딩으로 오인하지 않는 탐색기를 갖고 있고, 펜스가 닫히지 않았을 때의 폴백까지 구현되어 있다. `PlanBoundaryResolver`가 같은 문제를 풀어야 하므로 새로 만들지 않고 승격한다.

**Files:**
- Create: `src/ReSet.Core/Services/MarkdownSectionLocator.cs`
- Modify: `src/ReSet.Core/Services/BatchPlanAssembler.cs:70-140`
- Test: `tests/ReSet.Core.Tests/MarkdownSectionLocatorTests.cs`

**Interfaces:**
- Produces: `MarkdownSectionLocator.SplitLines(string?) -> List<string>`, `MarkdownSectionLocator.FindIndexOutsideFence(IReadOnlyList<string>, int, Func<string,bool>) -> int`, `MarkdownSectionLocator.LocateSection(IReadOnlyList<string>, string, string) -> (int HeaderIndex, int EndIndex)`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/MarkdownSectionLocatorTests.cs`:

```csharp
using System;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class MarkdownSectionLocatorTests
    {
        [Fact]
        public void SplitLines_ShouldNormalizeCrLf()
        {
            var lines = MarkdownSectionLocator.SplitLines("a\r\nb\nc");

            Assert.Equal(new[] { "a", "b", "c" }, lines);
        }

        [Fact]
        public void SplitLines_ShouldReturnSingleEmptyLine_ForNull()
        {
            var lines = MarkdownSectionLocator.SplitLines(null);

            Assert.Single(lines);
            Assert.Equal(string.Empty, lines[0]);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldIgnoreHeadingInsideCodeFence()
        {
            // 계획서의 공통 규약에는 SQL 블록이 실린다. 그 안의 "## "를 헤딩으로 읽으면
            // 섹션 경계가 코드 한복판에서 끊긴다.
            var lines = MarkdownSectionLocator.SplitLines(
                "본문\n```sql\n-- ## 가짜 헤딩\n```\n## 진짜 헤딩");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(4, index);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldRescan_WhenFenceNeverCloses()
        {
            // 모델이 닫는 펜스를 빠뜨리면 이후 전체가 "펜스 안"이 되어 미탐이 난다.
            // 미탐(문서 전체 삼킴)이 오탐(코드 안의 헤딩)보다 훨씬 나쁘므로 재스캔한다.
            var lines = MarkdownSectionLocator.SplitLines("```sql\nSELECT 1\n## 헤딩");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(2, index);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldReturnMinusOne_WhenNoMatch()
        {
            var lines = MarkdownSectionLocator.SplitLines("본문만 있다");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(-1, index);
        }

        [Fact]
        public void LocateSection_ShouldReturnHeaderAndNextBoundary()
        {
            var lines = MarkdownSectionLocator.SplitLines(
                "## 첫째\n내용1\n## 둘째\n내용2");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 첫째", "## ");

            Assert.Equal(0, header);
            Assert.Equal(2, end);
        }

        [Fact]
        public void LocateSection_ShouldEndAtDocumentEnd_WhenNoNextBoundary()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 유일\n내용");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 유일", "## ");

            Assert.Equal(0, header);
            Assert.Equal(2, end);
        }

        [Fact]
        public void LocateSection_ShouldReturnMinusOnePair_WhenHeadingMissing()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 다른 것\n내용");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 없는 헤딩", "## ");

            Assert.Equal(-1, header);
            Assert.Equal(-1, end);
        }

        [Fact]
        public void LocateSection_ShouldNotTreatH3AsBoundary()
        {
            // "### "는 인덱스 2가 '#'이라 StartsWith("## ")에 걸리지 않는다.
            // 이 성질이 깨지면 단계 헤딩이 H2 블록의 끝으로 오인된다.
            var lines = MarkdownSectionLocator.SplitLines("## 상위\n### 하위\n내용");

            var (_, end) = MarkdownSectionLocator.LocateSection(lines, "## 상위", "## ");

            Assert.Equal(3, end);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~MarkdownSectionLocatorTests"
```

Expected: 컴파일 오류 — `MarkdownSectionLocator` 형식을 찾을 수 없음

- [ ] **Step 3: `MarkdownSectionLocator`를 만든다**

`src/ReSet.Core/Services/MarkdownSectionLocator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 마크다운에서 헤딩 위치를 찾는다. 코드 펜스 안의 `#`을 헤딩으로 오인하지 않는다.
    ///
    /// BatchPlanAssembler의 private 헬퍼였던 것을 승격했다. PlanBoundaryResolver가
    /// 같은 문제(계획서 본문에 SQL 블록이 많고 그 안에 `##`이 등장한다)를 풀어야 하는데,
    /// 이 판정을 두 곳에서 각자 구현하면 한쪽만 펜스 미닫힘 폴백을 갖게 된다.
    /// </summary>
    public static class MarkdownSectionLocator
    {
        /// <summary>줄 바꿈을 정규화해 라인 배열로 만든다. null은 빈 줄 하나로 취급한다.</summary>
        public static List<string> SplitLines(string? markdown) =>
            (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();

        /// <summary>
        /// 펜스(```)로 둘러싸인 줄은 건너뛰고 조건을 만족하는 첫 줄의 인덱스를 찾는다.
        ///
        /// 펜스가 끝까지 닫히지 않으면(모델이 ``` 하나를 빠뜨린 경우) inFence가 참인 채로
        /// 스캔이 끝난다 - 그러면 이후 모든 줄이 "펜스 안"으로 오인되어 경계를 영영 못 찾고
        /// 한 섹션이 문서 나머지 전부를 삼킨다. 이 경우 펜스 상태를 신뢰할 수 없으므로
        /// 펜스를 무시하고 다시 스캔한다 - 오탐(코드 안의 헤딩)보다 미탐(전체 삼킴)이
        /// 훨씬 나쁘다.
        /// </summary>
        public static int FindIndexOutsideFence(
            IReadOnlyList<string> lines, int startIndex, Func<string, bool> predicate)
        {
            var inFence = false;
            for (var i = startIndex; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (!inFence && predicate(lines[i]))
                {
                    return i;
                }
            }

            if (inFence)
            {
                for (var i = startIndex; i < lines.Count; i++)
                {
                    if (predicate(lines[i]))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// 지정한 헤딩 줄의 인덱스와, 그 섹션이 끝나는(= 다음 경계 헤딩이 시작하는) 인덱스를
        /// 돌려준다. 헤딩이 없으면 (-1, -1). 다음 경계가 없으면 EndIndex는 문서 끝이다.
        /// </summary>
        /// <param name="headingLine">찾을 헤딩 줄 전체 (예: "## 단계별 이행 상세 및 의사코드").</param>
        /// <param name="boundaryPrefix">섹션의 끝을 정하는 헤딩 접두 (예: "## ").</param>
        public static (int HeaderIndex, int EndIndex) LocateSection(
            IReadOnlyList<string> lines, string headingLine, string boundaryPrefix)
        {
            var headerIndex = FindIndexOutsideFence(lines, 0, line => line.Trim() == headingLine);
            if (headerIndex < 0)
            {
                return (-1, -1);
            }

            var endIndex = FindIndexOutsideFence(
                lines,
                headerIndex + 1,
                line => line.TrimStart().StartsWith(boundaryPrefix, StringComparison.Ordinal));

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~MarkdownSectionLocatorTests"
```

Expected: PASS 9건

- [ ] **Step 5: `BatchPlanAssembler`가 새 클래스에 위임하도록 바꾼다**

`src/ReSet.Core/Services/BatchPlanAssembler.cs`에서:

1. `Normalize`를 다음으로 바꾼다.

```csharp
        private static List<string> Normalize(string? markdown)
        {
            var stripped = StepPlaceholderRegex.Replace(markdown ?? string.Empty, string.Empty);
            return MarkdownSectionLocator.SplitLines(stripped);
        }
```

2. `LocateStepDetailBlock`을 다음으로 바꾼다.

```csharp
        /// <summary>
        /// 단계 상세 H2의 헤더 줄 인덱스와, 그 블록이 끝나는(= 다음 H2가 시작하는)
        /// 인덱스를 돌려준다. 헤더가 없으면 (-1, -1).
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateStepDetailBlock(List<string> lines) =>
            MarkdownSectionLocator.LocateSection(lines, StepDetailHeader, "## ");
```

3. private `FindIndexOutsideFence` 메서드 전체를 삭제한다. `using System;`은 `Func` 사용이 사라져도 다른 곳(`StringComparison`)에서 쓰이므로 남긴다.

- [ ] **Step 6: 기존 `BatchPlanAssembler` 테스트가 회귀 없이 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~BatchPlanAssemblerTests"
```

Expected: 전건 PASS. 실패가 하나라도 나면 위임이 동작을 바꾼 것이므로 되돌리고 원인을 찾는다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/MarkdownSectionLocator.cs src/ReSet.Core/Services/BatchPlanAssembler.cs tests/ReSet.Core.Tests/MarkdownSectionLocatorTests.cs
git commit -m "refactor: promote the fence-aware heading locator out of the assembler

The boundary resolver needs the same judgement, and duplicating it would
leave one copy without the unclosed-fence rescan."
```

---

### Task 3: 단계 경계를 3단 폴백으로 결정한다

이 태스크가 설계의 핵심이다. **조각은 앵커로만 쓰고 본문은 최종 문서에서 잘라낸다.** 조각 본문을 그대로 쓰면 L1 정제·자가 교정·구제 채택이 반영되지 않은 옛 본문이 `steps/*.md`에 실려 `BatchMigrationPlan.md`와 조용히 달라진다.

**Files:**
- Create: `src/ReSet.Core/Services/PlanBoundaryResolver.cs`
- Test: `tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`

**Interfaces:**
- Consumes: `PlanLayout` (Task 1), `MarkdownSectionLocator` (Task 2), `BatchStepPlan` (기존)
- Produces: `StepBoundaryResult(IReadOnlyDictionary<string,string> Steps, bool Split, IReadOnlyList<string> Warnings)`, `PlanBoundaryResolver.ResolveSteps(string, PlanLayout?) -> StepBoundaryResult`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PlanBoundaryResolverTests
    {
        // 조각의 본문과 최종 문서의 본문을 일부러 다르게 둔다. 결과가 어느 쪽에서
        // 왔는지 구별할 수 없으면 이 설계의 핵심 성질을 검증할 수 없다.
        private const string FinalPlan = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### 공통 Tasklet 실행 계약

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

        private static PlanLayout LayoutWithSections() => new(
            "골격",
            new Dictionary<string, string>
            {
                ["S01"] = "### S01 스냅샷 생성\n\n조각에 남은 옛 S01 본문",
                ["S02"] = "### S02 원장 생성\n\n조각에 남은 옛 S02 본문",
            },
            new[] { Step("S01"), Step("S02") },
            null);

        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계", new[] { "UP_X" }, new[] { "dbo.T" }, new[] { "-1" }, false);

        [Fact]
        public void ResolveSteps_ShouldTakeBodiesFromFinalDocument_NotFromSections()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.True(result.Split);
            Assert.Contains("정제된 S01 본문", result.Steps["S01"]);
            Assert.DoesNotContain("옛 S01 본문", result.Steps["S01"]);
        }

        [Fact]
        public void ResolveSteps_ShouldStartEachSliceAtItsHeading()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.StartsWith("### S01 스냅샷 생성", result.Steps["S01"]);
            Assert.StartsWith("### S02 원장 생성", result.Steps["S02"]);
        }

        [Fact]
        public void ResolveSteps_ShouldNotLeakNextStepIntoPreviousSlice()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("S02", result.Steps["S01"]);
        }

        [Fact]
        public void ResolveSteps_ShouldEndLastSliceAtNextH2()
        {
            // 마지막 단계 뒤에는 검증 SQL H2가 온다. 그것까지 삼키면 안 된다.
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("검증 SQL 본문", result.Steps["S02"]);
            Assert.Contains("정제된 S02 본문", result.Steps["S02"]);
        }

        [Fact]
        public void ResolveSteps_ShouldNotIncludeSharedConventions()
        {
            // 공통 규약은 common/으로 가야지 특정 단계에 붙으면 안 된다.
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.All(result.Steps.Values, body => Assert.DoesNotContain("공통 규약 본문", body));
        }

        [Fact]
        public void ResolveSteps_ShouldFallBackToStepCodes_WhenAnchorsDoNotMatch()
        {
            // 정제가 헤딩 문구를 바꾸면 앵커가 어긋난다. 목차의 Code로 복구한다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 완전히 다른 제목\n본문",
                    ["S02"] = "### S02 완전히 다른 제목\n본문",
                },
                new[] { Step("S01"), Step("S02") },
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.True(result.Split);
            Assert.Contains("정제된 S01 본문", result.Steps["S01"]);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldFailWholly_WhenOneStepCannotBeLocated()
        {
            // 부분 분할은 하지 않는다. 빈 steps/*.md가 조용히 생기는 것이 최악이다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n본문",
                    ["S99"] = "### S99 문서에 없는 단계\n본문",
                },
                new[] { Step("S01"), Step("S99") },
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
            Assert.Empty(result.Steps);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldReturnNotSplit_WhenLayoutIsNull()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, null);

            Assert.False(result.Split);
            Assert.Empty(result.Steps);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldFail_WhenSectionHasNoHeading()
        {
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "헤딩 없는 조각 본문" },
                null,
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
        }

        [Fact]
        public void ResolveSteps_ShouldFail_WhenTwoSectionsShareOneHeading()
        {
            // 같은 헤딩을 가리키는 두 단계는 경계를 정할 수 없다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n본문",
                    ["S02"] = "### S01 스냅샷 생성\n본문",
                },
                null,
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
        }

        [Fact]
        public void ResolveSteps_ShouldIgnoreHeadingsInsideCodeFences()
        {
            var planWithFence = """
## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

```sql
-- ### S02 원장 생성
SELECT 1;
```

진짜 S01 본문

### S02 원장 생성

진짜 S02 본문
""";

            var result = PlanBoundaryResolver.ResolveSteps(planWithFence, LayoutWithSections());

            Assert.True(result.Split);
            Assert.Contains("진짜 S01 본문", result.Steps["S01"]);
            Assert.Contains("진짜 S02 본문", result.Steps["S02"]);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanBoundaryResolverTests"
```

Expected: 컴파일 오류 — `PlanBoundaryResolver` 형식을 찾을 수 없음

- [ ] **Step 3: `PlanBoundaryResolver`를 만든다**

`src/ReSet.Core/Services/PlanBoundaryResolver.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 경계 결정의 결과.
    /// </summary>
    /// <param name="Steps">단계 코드 → 최종 문서에서 잘라낸 본문. 실패하면 비어 있다.</param>
    /// <param name="Split">분할에 성공했는가. false면 호출부는 단일 파일 폴백을 취한다.</param>
    /// <param name="Warnings">사용자에게 보여줄 경고. 성공해도 2순위로 내려왔다면 비어 있지 않다.</param>
    public sealed record StepBoundaryResult(
        IReadOnlyDictionary<string, string> Steps,
        bool Split,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// 최종 계획서에서 단계별 경계를 찾아 본문을 잘라낸다.
    ///
    /// 핵심 규칙: <b>조각(PlanLayout.Sections)은 앵커로만 쓰고 본문은 언제나 최종
    /// 문서에서 잘라낸다.</b> split.Markdown이 나온 뒤에도 최종 문서는 L1 정제·자가
    /// 교정·구제 채택으로 계속 바뀌므로, 조각 본문을 실으면 BatchMigrationPlan.md와
    /// steps/*.md가 조용히 달라진다. 그 불일치는 코딩 에이전트가 옛 로직을 구현하게
    /// 만들고, 아무도 그것을 알아채지 못한다.
    ///
    /// 정규식으로 `### S\d\d` 같은 패턴을 잡지 않는다. BatchStepPlan의 주석이 이미
    /// 실측으로 반증했다 - 단계가 H3에 오기도 H4에 오기도 하고, 한 헤딩이 여러 단계를
    /// 묶기도 한다.
    /// </summary>
    public static class PlanBoundaryResolver
    {
        private static readonly IReadOnlyDictionary<string, string> NoSteps =
            new Dictionary<string, string>();

        public static StepBoundaryResult ResolveSteps(string finalPlanMarkdown, PlanLayout? layout)
        {
            var warnings = new List<string>();
            var lines = MarkdownSectionLocator.SplitLines(finalPlanMarkdown);

            // 1순위: 조각의 첫 헤딩을 앵커로 쓴다. 조각이 어느 단계에서 왔는지 알기
            // 때문에 중복 헤딩이나 순서 뒤바뀜을 스스로 검출할 수 있다.
            if (layout?.IsSplitAvailable == true)
            {
                var anchored = TryLocateByAnchor(lines, layout.Sections!, warnings);
                if (anchored != null)
                {
                    Log.Information("단계 경계를 조각 앵커로 결정했습니다 - 단계 수: {Count}개", anchored.Count);
                    return new StepBoundaryResult(anchored, true, warnings);
                }
            }

            // 2순위: 목차가 선언한 단계 코드로 헤딩을 찾는다. 정제가 헤딩 문구를
            // 바꿔 앵커가 어긋난 경우의 복구 경로다.
            if (layout?.Steps is { Count: > 0 })
            {
                var byCode = TryLocateByCode(lines, layout.Steps, warnings);
                if (byCode != null)
                {
                    Log.Information("단계 경계를 목차 단계 코드로 결정했습니다 - 단계 수: {Count}개", byCode.Count);
                    return new StepBoundaryResult(byCode, true, warnings);
                }
            }

            warnings.Add("단계 경계를 찾지 못했습니다. 계획서를 분할하지 않고 단일 파일로 유지합니다.");
            Log.Warning("단계 경계 결정 실패 - 단일 파일 폴백");
            return new StepBoundaryResult(NoSteps, false, warnings);
        }

        private static Dictionary<string, string>? TryLocateByAnchor(
            List<string> lines, IReadOnlyDictionary<string, string> sections, List<string> warnings)
        {
            var located = new List<(string Code, int Index)>();

            foreach (var pair in sections)
            {
                var heading = FirstHeadingLine(pair.Value);
                if (heading == null)
                {
                    warnings.Add($"단계 {pair.Key}의 조각에 헤딩이 없어 앵커를 만들 수 없습니다.");
                    return null;
                }

                var index = MarkdownSectionLocator.FindIndexOutsideFence(
                    lines, 0, line => line.Trim() == heading);
                if (index < 0)
                {
                    warnings.Add($"단계 {pair.Key}의 헤딩을 최종 문서에서 찾지 못했습니다: {heading}");
                    return null;
                }

                located.Add((pair.Key, index));
            }

            return Materialize(lines, located, warnings);
        }

        private static Dictionary<string, string>? TryLocateByCode(
            List<string> lines, IReadOnlyList<BatchStepPlan> steps, List<string> warnings)
        {
            var located = new List<(string Code, int Index)>();

            foreach (var step in steps)
            {
                var index = MarkdownSectionLocator.FindIndexOutsideFence(lines, 0, line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("#", StringComparison.Ordinal)
                        && trimmed.Contains(step.Code, StringComparison.OrdinalIgnoreCase);
                });

                if (index < 0)
                {
                    warnings.Add($"목차의 단계 {step.Code}에 해당하는 헤딩을 최종 문서에서 찾지 못했습니다.");
                    return null;
                }

                located.Add((step.Code, index));
            }

            // 목차 순서와 문서 순서가 어긋나면 코드 포함 판정이 엉뚱한 헤딩을 잡은 것이다
            // (예: "### S02 (S01 이후)"가 S01로 먼저 걸리는 경우). 신뢰할 수 없다.
            for (var i = 1; i < located.Count; i++)
            {
                if (located[i].Index <= located[i - 1].Index)
                {
                    warnings.Add("목차 순서와 문서의 헤딩 순서가 어긋나 단계 코드 탐색을 신뢰할 수 없습니다.");
                    return null;
                }
            }

            return Materialize(lines, located, warnings);
        }

        /// <summary>
        /// 찾아낸 시작 인덱스들로 실제 본문을 잘라낸다. 각 단계는 다음 단계의 시작 직전까지이며,
        /// 마지막 단계는 다음 H2(= 검증 SQL 세트)에서 끝난다.
        /// </summary>
        private static Dictionary<string, string>? Materialize(
            List<string> lines, List<(string Code, int Index)> located, List<string> warnings)
        {
            if (located.Count == 0)
            {
                warnings.Add("잘라낼 단계가 하나도 없습니다.");
                return null;
            }

            var ordered = located.OrderBy(item => item.Index).ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Index == ordered[i - 1].Index)
                {
                    warnings.Add(
                        $"단계 {ordered[i - 1].Code}와 {ordered[i].Code}가 같은 헤딩을 가리켜 경계를 정할 수 없습니다.");
                    return null;
                }
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < ordered.Count; i++)
            {
                var start = ordered[i].Index;
                int end;

                if (i + 1 < ordered.Count)
                {
                    end = ordered[i + 1].Index;
                }
                else
                {
                    // 마지막 단계는 다음 H2에서 끝난다. H2가 없으면 문서 끝까지.
                    var nextH2 = MarkdownSectionLocator.FindIndexOutsideFence(
                        lines, start + 1,
                        line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
                    end = nextH2 < 0 ? lines.Count : nextH2;
                }

                var body = string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
                if (body.Length == 0)
                {
                    warnings.Add($"단계 {ordered[i].Code}의 본문이 비어 있습니다.");
                    return null;
                }

                result[ordered[i].Code] = body;
            }

            return result;
        }

        /// <summary>조각에서 첫 헤딩 줄을 뽑는다. 이 줄이 최종 문서를 찾을 앵커가 된다.</summary>
        private static string? FirstHeadingLine(string? sectionMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(sectionMarkdown);
            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.TrimStart().StartsWith("#", StringComparison.Ordinal));

            return index < 0 ? null : lines[index].Trim();
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanBoundaryResolverTests"
```

Expected: PASS 11건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/PlanBoundaryResolver.cs tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs
git commit -m "feat: resolve step boundaries from generation slices as anchors

Bodies always come from the final document so cleansing and rescue stay
reflected; a single unlocatable step drops the whole split rather than
leaving an empty step file behind."
```

---

### Task 4: 골격을 `common/`과 `verification/`으로 나눈다

단계 상세와 달리 여기서는 **헤딩 이름이 고정이다.** `MechanicalValidator.RequiredConsolidatedHeaders`가 L1에서 H2 4개의 존재를 강제하기 때문이다. 다만 그 강제는 절대적이지 않으므로(검증기 자체 오류 시 소프트 패스, `L1Exhausted` 통과 경로) 실패 시 골격을 통짜로 남긴다. **골격 분할 실패는 단계 분할을 막지 않는다.**

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs:56` (private → public)
- Modify: `src/ReSet.Core/Services/PlanBoundaryResolver.cs` (Task 3에서 만든 파일)
- Test: `tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs` (Task 3 파일에 추가)

**Interfaces:**
- Consumes: `StepBoundaryResult` (Task 3), `MechanicalValidator.RequiredConsolidatedHeaders`
- Produces: `StepBoundaryResult`에 위치 파라미터 `int FirstStepLineIndex` 추가 (실패 시 `-1`), `PlanSlices(string Preamble, string Architecture, string? StepContract, string? Verification, IReadOnlyDictionary<string,string> Steps, bool SkeletonSplit, bool StepsSplit, IReadOnlyList<string> Warnings)`, `PlanBoundaryResolver.Resolve(string, PlanLayout?) -> PlanSlices`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`의 클래스 안에 다음을 추가한다. `FinalPlan`과 `LayoutWithSections()`는 Task 3에서 만든 것을 재사용한다.

```csharp
        [Fact]
        public void Resolve_ShouldPutOverviewAndMermaidIntoArchitecture()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.Contains("## 통합 배치 아키텍처 개요", slices.Architecture);
            Assert.Contains("개요 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldNotLeakStepDetailIntoArchitecture()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("공통 규약 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldExtractSharedConventionsIntoStepContract()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.StepContract);
            Assert.Contains("공통 규약 본문", slices.StepContract!);
            Assert.DoesNotContain("정제된 S01 본문", slices.StepContract!);
        }

        [Fact]
        public void Resolve_ShouldExtractVerificationSql()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.Verification);
            Assert.Contains("검증 SQL 본문", slices.Verification!);
            Assert.DoesNotContain("정제된 S02 본문", slices.Verification!);
        }

        [Fact]
        public void Resolve_ShouldCaptureBannerAsPreamble()
        {
            // L1Exhausted 배너는 문서 선두에 삽입된다. 첫 H2 앞의 내용을 버리면
            // 그 경고가 산출물에서 사라진다.
            var withBanner = "> 경고: 검증을 소진했습니다.\n\n" + FinalPlan;

            var slices = PlanBoundaryResolver.Resolve(withBanner, LayoutWithSections());

            Assert.Contains("검증을 소진했습니다", slices.Preamble);
            Assert.DoesNotContain("검증을 소진했습니다", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldKeepSkeletonWhole_WhenAnH2IsMissing()
        {
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.Null(slices.Verification);
            Assert.Null(slices.StepContract);
            Assert.Contains("개요 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldStillSplitSteps_WhenSkeletonSplitFails()
        {
            // 두 판정은 독립이다. 골격이 통짜로 남아도 회차당 입력에서 가장 큰 몫인
            // 단계 상세는 여전히 분리되어야 한다.
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);
            Assert.Equal(2, slices.Steps.Count);
        }

        [Fact]
        public void Resolve_ShouldKeepSkeletonWhole_WhenStepSplitFails()
        {
            // 단계 경계를 못 찾으면 StepContract를 잘라낼 기준점도 없다.
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, null);

            Assert.False(slices.StepsSplit);
            Assert.Null(slices.StepContract);
            Assert.Contains("정제된 S01 본문", slices.Architecture);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanBoundaryResolverTests"
```

Expected: 컴파일 오류 — `PlanBoundaryResolver.Resolve`와 `PlanSlices`를 찾을 수 없음

- [ ] **Step 3: `RequiredConsolidatedHeaders`를 public으로 승격한다**

`src/ReSet.Core/Services/MechanicalValidator.cs:56`의 선언을 다음으로 바꾼다.

```csharp
        /// <summary>
        /// 통합 계획서가 반드시 가져야 할 H2 네 개. L1이 이 존재를 강제하므로
        /// PlanBoundaryResolver가 골격을 자를 때 같은 목록을 근거로 삼는다.
        /// 두 곳이 서로 다른 이름을 말하면 분할이 조용히 실패한다.
        /// </summary>
        public static readonly string[] RequiredConsolidatedHeaders = new[]
```

- [ ] **Step 4: `StepBoundaryResult`에 첫 단계 위치를 추가한다**

`src/ReSet.Core/Services/PlanBoundaryResolver.cs`에서:

1. record 선언에 위치 파라미터를 추가한다.

```csharp
    /// <param name="FirstStepLineIndex">첫 단계 헤딩의 줄 인덱스. 공통 규약을 잘라내는
    /// 끝점이 된다. 분할에 실패하면 -1이다.</param>
    public sealed record StepBoundaryResult(
        IReadOnlyDictionary<string, string> Steps,
        bool Split,
        IReadOnlyList<string> Warnings,
        int FirstStepLineIndex);
```

2. `Materialize`의 반환형을 `(Dictionary<string,string> Steps, int FirstIndex)?`로 바꾸고, 성공 시 `(result, ordered[0].Index)`를 돌려준다. 실패 경로는 모두 `null`을 유지한다.

3. `ResolveSteps`의 세 반환문을 다음으로 바꾼다.

```csharp
                if (anchored != null)
                {
                    Log.Information("단계 경계를 조각 앵커로 결정했습니다 - 단계 수: {Count}개", anchored.Value.Steps.Count);
                    return new StepBoundaryResult(anchored.Value.Steps, true, warnings, anchored.Value.FirstIndex);
                }
```

```csharp
                if (byCode != null)
                {
                    Log.Information("단계 경계를 목차 단계 코드로 결정했습니다 - 단계 수: {Count}개", byCode.Value.Steps.Count);
                    return new StepBoundaryResult(byCode.Value.Steps, true, warnings, byCode.Value.FirstIndex);
                }
```

```csharp
            return new StepBoundaryResult(NoSteps, false, warnings, -1);
```

`TryLocateByAnchor`/`TryLocateByCode`의 반환형도 `(Dictionary<string,string> Steps, int FirstIndex)?`로 맞춘다.

- [ ] **Step 5: `PlanSlices`와 `Resolve`를 추가한다**

`src/ReSet.Core/Services/PlanBoundaryResolver.cs`에 다음을 추가한다.

```csharp
    /// <summary>
    /// 최종 계획서를 산출물 파일 단위로 자른 결과.
    /// </summary>
    /// <param name="Preamble">첫 H2 앞의 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="Architecture">개요 + Mermaid 흐름도. 골격 분할이 실패하면 계획서 전문.</param>
    /// <param name="StepContract">모든 단계가 공유하는 실행 계약. 잘라내지 못했으면 null.</param>
    /// <param name="Verification">정합성 검증 SQL 세트. 잘라내지 못했으면 null.</param>
    /// <param name="Steps">단계 코드 → 본문. 분할에 실패하면 비어 있다.</param>
    public sealed record PlanSlices(
        string Preamble,
        string Architecture,
        string? StepContract,
        string? Verification,
        IReadOnlyDictionary<string, string> Steps,
        bool SkeletonSplit,
        bool StepsSplit,
        IReadOnlyList<string> Warnings);
```

`PlanBoundaryResolver` 클래스에 다음 메서드를 추가한다.

```csharp
        /// <summary>
        /// 최종 계획서를 산출물 파일 단위로 자른다.
        ///
        /// 골격 분할과 단계 분할은 독립적으로 판정한다. 골격의 H2 하나를 못 찾았다고
        /// 단계 분할까지 포기하면, 회차당 입력에서 가장 큰 몫을 차지하는 단계 상세가
        /// 통짜로 남아 이 작업의 목적 자체가 사라진다.
        /// </summary>
        public static PlanSlices Resolve(string finalPlanMarkdown, PlanLayout? layout)
        {
            var lines = MarkdownSectionLocator.SplitLines(finalPlanMarkdown);
            var steps = ResolveSteps(finalPlanMarkdown, layout);
            var warnings = new List<string>(steps.Warnings);

            var headings = MechanicalValidator.RequiredConsolidatedHeaders;
            var positions = new int[headings.Length];
            var allFound = true;

            for (var i = 0; i < headings.Length; i++)
            {
                positions[i] = LocateH2(lines, headings[i]);
                if (positions[i] < 0)
                {
                    warnings.Add($"골격 H2를 찾지 못했습니다: {headings[i]}");
                    allFound = false;
                }
            }

            // 순서가 어긋나면 헤딩 판정이 엉뚱한 줄을 잡은 것이다.
            if (allFound)
            {
                for (var i = 1; i < positions.Length; i++)
                {
                    if (positions[i] <= positions[i - 1])
                    {
                        warnings.Add("골격 H2의 문서 내 순서가 기대와 달라 골격을 분할하지 않습니다.");
                        allFound = false;
                        break;
                    }
                }
            }

            var preamble = positions[0] > 0
                ? Join(lines, 0, positions[0])
                : string.Empty;

            if (!allFound)
            {
                Log.Warning("골격 H2 탐색 실패 - 골격을 통짜로 유지합니다. 단계 분할 여부: {StepsSplit}", steps.Split);
                return new PlanSlices(
                    preamble,
                    Join(lines, positions[0] > 0 ? positions[0] : 0, lines.Count),
                    null,
                    null,
                    steps.Steps,
                    SkeletonSplit: false,
                    StepsSplit: steps.Split,
                    warnings);
            }

            // 개요 + Mermaid = [H2①, H2③)
            var architecture = Join(lines, positions[0], positions[2]);

            // 공통 규약 = (H2③, 첫 단계 헤딩). 단계 경계를 모르면 끝점을 정할 수 없다.
            string? stepContract = null;
            if (steps.Split && steps.FirstStepLineIndex > positions[2])
            {
                stepContract = Join(lines, positions[2] + 1, steps.FirstStepLineIndex);
                if (stepContract.Length == 0)
                {
                    stepContract = null;
                }
            }

            // 검증 SQL = [H2④, 다음 H2 또는 문서 끝)
            var verificationEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, positions[3] + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
            var verification = Join(lines, positions[3], verificationEnd < 0 ? lines.Count : verificationEnd);

            Log.Information(
                "골격을 분할했습니다 - 공통 규약: {HasContract}, 검증 SQL: {HasVerification}",
                stepContract != null, verification.Length > 0);

            return new PlanSlices(
                preamble,
                architecture,
                stepContract,
                verification.Length > 0 ? verification : null,
                steps.Steps,
                SkeletonSplit: true,
                StepsSplit: steps.Split,
                warnings);
        }

        /// <summary>
        /// H2 헤딩을 찾는다. 정확 일치를 먼저 보고, 실패하면 이름 포함으로 완화한다.
        /// 정제가 헤딩에 번호나 이모지를 덧붙이는 경우가 있어 정확 일치만으로는 놓친다.
        /// </summary>
        private static int LocateH2(IReadOnlyList<string> lines, string headingName)
        {
            var exact = "## " + headingName;
            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == exact);
            if (index >= 0)
            {
                return index;
            }

            return MarkdownSectionLocator.FindIndexOutsideFence(lines, 0, line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("## ", StringComparison.Ordinal)
                    && trimmed.Contains(headingName, StringComparison.Ordinal);
            });
        }

        private static string Join(IReadOnlyList<string> lines, int start, int end)
        {
            if (start < 0 || end <= start)
            {
                return string.Empty;
            }

            return string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
        }
```

`Skip`/`Take`를 `IReadOnlyList<string>`에 쓰므로 `using System.Linq;`가 이미 있는지 확인한다 (Task 3에서 추가했다).

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PlanBoundaryResolverTests"
```

Expected: PASS 19건 (Task 3의 11건 + 이번 8건)

- [ ] **Step 7: 기존 `MechanicalValidator` 테스트가 회귀 없는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~MechanicalValidatorTests"
```

Expected: 전건 PASS

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/PlanBoundaryResolver.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs
git commit -m "feat: slice the plan skeleton into common and verification parts

The four H2 headings are what L1 already enforces, so they are the one
place in this document where heading names can be trusted. Skeleton and
step splits are judged independently."
```

---

### Task 5: 파이프라인이 `PlanLayout`을 채워 반환한다

`GenerateBySplitAsync`가 만든 조각은 지금 로컬 변수(`lastSkeleton`, `lastStepSections`, `stepFloorViolations`)에만 남고 호출부로 나가지 않는다. 성공 반환 2곳에서 이를 `PlanLayout`으로 묶는다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2088`, `:2102`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: `PlanLayout` (Task 1), `SplitGeneration` (기존 private record)
- Produces: `ConsolidatedPipelineResult.Layout`이 분할 경로에서 채워지고 단일 호출 경로에서 `null`이다

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 클래스 안에 다음을 추가한다.

```csharp
        // 목차가 단계 목록 JSON을 내야 분할 경로로 들어간다. BatchStepPlanParser는
        // ```json 블록 안의 Steps 배열만 읽는다.
        private const string PlanStructureWithSteps = """
## 최종 문서 목차

```json
{
  "Steps": [
    { "Code": "S01", "Name": "스냅샷 생성", "LegacyProcedures": ["UP_A"],
      "TargetTables": ["dbo.T1"], "ErrorCodes": ["-1"], "Chunkable": false },
    { "Code": "S02", "Name": "원장 생성", "LegacyProcedures": ["UP_B"],
      "TargetTables": ["dbo.T2"], "ErrorCodes": ["-2"], "Chunkable": false }
  ]
}
```
""";

        private const string SkeletonMarkdown = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### 공통 Tasklet 실행 계약

공통 규약 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

        private void ArrangeSplitGeneration()
        {
            _aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });

            _aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = PlanStructureWithSteps });

            _aiService.GenerateBatchPlanSkeletonAsync(
                    Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                    Arg.Any<List<(string FileName, string Content)>>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            _aiService.GenerateBatchStepSectionAsync(
                    Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(),
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var step = callInfo.ArgAt<BatchStepPlan>(0);
                    return Task.FromResult(new AiResult
                    {
                        Content = $"### {step.Code} {step.Name}\n\n{step.Code} 단계 본문"
                    });
                });

            _aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10,
                    ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
                }));

            _userInteraction.RequestHumanReviewAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(),
                    Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_ShouldExposeLayout_WhenPlanWasSplit()
        {
            ArrangeSplitGeneration();
            var specs = new List<(string, string)> { ("dbo.USP_A", "## 개요\n내용") };

            var result = await _orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Split", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.NotNull(result.Layout);
            Assert.Equal(SkeletonMarkdown, result.Layout!.Skeleton);
            Assert.NotNull(result.Layout.Sections);
            Assert.Equal(2, result.Layout.Sections!.Count);
            Assert.True(result.Layout.Sections.ContainsKey("S01"));
            Assert.True(result.Layout.Sections.ContainsKey("S02"));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_ShouldExposeStepsFromStructure()
        {
            ArrangeSplitGeneration();
            var specs = new List<(string, string)> { ("dbo.USP_A", "## 개요\n내용") };

            var result = await _orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Split", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.NotNull(result.Layout?.Steps);
            Assert.Equal(new[] { "S01", "S02" }, result.Layout!.Steps!.Select(s => s.Code));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_ShouldLeaveLayoutNull_WhenPlanWasNotSplit()
        {
            // 목차에 단계 목록 JSON이 없으면 단일 호출로 떨어지고, 그때는 조각이 없다.
            var specs = new List<(string, string)> { ("dbo.USP_A", "## 개요\n내용") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n"
                     + "## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "JSON 없는 목차" });
            _aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_NoSplit",
                    Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            _aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string, string)>>(), plan, "Job_NoSplit")
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10,
                    ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
                }));
            _userInteraction.RequestHumanReviewAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(),
                    Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await _orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_NoSplit", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.NotNull(result.Plan);
            Assert.Null(result.Layout);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

Expected: 새 테스트 3건 중 `ShouldExposeLayout`/`ShouldExposeStepsFromStructure` 2건이 `result.Layout`이 null이라 FAIL. `ShouldLeaveLayoutNull`은 이미 PASS일 수 있다. 기존 테스트는 전건 PASS여야 한다.

- [ ] **Step 3: 성공 반환에서 `PlanLayout`을 구성한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`에서 `:2088`과 `:2102`의 반환문 바로 앞에 다음 지역 함수 호출을 넣을 수 있도록, 두 반환문이 있는 메서드 안(해당 반환문들보다 위, 예컨대 `lastStepSections` 선언 근처)에 지역 함수를 추가한다.

```csharp
            // 조각을 호출부로 내보낸다. 산출물 분할이 이 값들을 경계 앵커로 쓴다.
            // splitMarkdown이 null이면 단일 호출 경로였다는 뜻이고, 그때는 조각이
            // 아예 없으므로 null을 그대로 내보내 호출부가 폴백을 취하게 한다.
            PlanLayout? BuildLayout() =>
                lastSkeleton == null || lastStepSections == null
                    ? null
                    : new PlanLayout(
                        lastSkeleton,
                        new Dictionary<string, string>(lastStepSections),
                        currentSteps,
                        new Dictionary<string, string>(stepFloorViolations));
```

그리고 두 반환문을 다음으로 바꾼다.

```csharp
                return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome, BuildLayout());
```

`:1836`과 `:2106`(실패 반환, `Plan`이 null)은 그대로 둔다. 계획서가 없으면 분할할 대상도 없다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

Expected: 전건 PASS. 기존 테스트에 회귀가 있으면 `BuildLayout`이 실패 경로까지 건드린 것이므로 되돌린다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: return the plan slices from the consolidated pipeline

They were confined to locals inside the retry loop; the artifact writer
needs them to locate section boundaries."
```

---

## Phase B — 번들을 쓴다

### Task 6: 진입점의 섹션 순서를 뒤집는다

이 작업 전체에서 가장 값싸고 효과가 큰 변경이다. 현재 실행 지침은 `MigrationInstructions.md:7759`, 경계 규칙은 `:7773`에 있고 Read는 2,000줄에서 잘린다. 분할 성공 여부와 무관하게 **항상** 적용한다.

**Files:**
- Create: `src/ReSet.Core/Services/InstructionEntryPointComposer.cs`
- Test: `tests/ReSet.Core.Tests/InstructionEntryPointComposerTests.cs`

**Interfaces:**
- Consumes: `VerificationOutcome` (기존), `DataAccessPolicy.InstructionRules` (기존)
- Produces: `IndexEntry(string Label, string RelativePath)`, `EntryPointInputs(...)`, `InstructionEntryPointComposer.Compose(EntryPointInputs) -> string`, `InstructionEntryPointComposer.PlanVerificationSection(VerificationOutcome) -> string`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/InstructionEntryPointComposerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class InstructionEntryPointComposerTests
    {
        private static EntryPointInputs Split() => new(
            JobName: "POQSettleProcDaily",
            TargetLanguage: "C#",
            PlanOutcome: VerificationOutcome.Passed,
            Preamble: string.Empty,
            StepsSplit: true,
            Steps: new List<IndexEntry> { new("S01 스냅샷 생성", "steps/S01.md") },
            Dependencies: new List<IndexEntry> { new("dbo.TClient", "raw/ddl/dbo.TClient.md") },
            Specs: new List<IndexEntry> { new("dbo.UP_A", "../../Procedures/dbo.UP_A/docs/Spec.md") },
            HasStepContract: true,
            HasVerification: true,
            SinglePlanRelativePath: null);

        private static EntryPointInputs Fallback() => Split() with
        {
            StepsSplit = false,
            Steps = new List<IndexEntry>(),
            HasStepContract = false,
            HasVerification = false,
            SinglePlanRelativePath = "../docs/BatchMigrationPlan.md",
        };

        [Fact]
        public void Compose_ShouldPlaceGuidelinesBeforeAnyPlanLink()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var stepsLink = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(guidelines >= 0, "지침 섹션이 없다");
            Assert.True(stepsLink >= 0, "단계 링크가 없다");
            Assert.True(guidelines < stepsLink, "지침이 계획 링크보다 뒤에 있다");
        }

        [Fact]
        public void Compose_ShouldPlaceGuidelinesBeforePlanLink_EvenInFallback()
        {
            // 분할이 실패해도 순서 교정만은 잃지 않는다.
            var markdown = InstructionEntryPointComposer.Compose(Fallback());

            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var planLink = markdown.IndexOf("BatchMigrationPlan.md", StringComparison.Ordinal);

            Assert.True(guidelines < planLink);
        }

        [Fact]
        public void Compose_ShouldPlaceBoundaryRulesBeforeAnyPlanLink()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            var rules = markdown.IndexOf("데이터 액세스 경계 규칙", StringComparison.Ordinal);
            var stepsLink = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(rules >= 0);
            Assert.True(rules < stepsLink);
        }

        [Fact]
        public void Compose_ShouldPutVerificationBannerFirst()
        {
            // L1Exhausted 경로는 경고 배너를 낸다. Passed 경로는 아래 별도 테스트에서 본다.
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { PlanOutcome = VerificationOutcome.L1Exhausted });

            var banner = markdown.IndexOf("이 계획서의 검증 상태", StringComparison.Ordinal);
            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);

            Assert.True(banner >= 0);
            Assert.True(banner < guidelines);
        }

        [Fact]
        public void Compose_ShouldIncludeReadingContract()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("읽기 계약", markdown);
            Assert.Contains("다른 Step 파일을 읽지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldCarryPreamble_WhenPresent()
        {
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { Preamble = "> 경고: 검증을 소진했습니다." });

            Assert.Contains("검증을 소진했습니다", markdown);
        }

        [Fact]
        public void Compose_ShouldLinkCommonFiles_WhenSkeletonWasSplit()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("common/00-architecture.md", markdown);
            Assert.Contains("common/01-step-contract.md", markdown);
            Assert.Contains("common/02-data-access-boundary.md", markdown);
            Assert.Contains("verification/integrity-sql.md", markdown);
        }

        [Fact]
        public void Compose_ShouldNotLinkMissingCommonFiles()
        {
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { HasStepContract = false, HasVerification = false });

            Assert.DoesNotContain("common/01-step-contract.md", markdown);
            Assert.DoesNotContain("verification/integrity-sql.md", markdown);
            // 경계 규칙 파일은 계획서가 아니라 DataAccessPolicy에서 오므로 항상 있다.
            Assert.Contains("common/02-data-access-boundary.md", markdown);
        }

        [Fact]
        public void Compose_ShouldListDependenciesAndSpecs()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("raw/ddl/dbo.TClient.md", markdown);
            Assert.Contains("Procedures/dbo.UP_A/docs/Spec.md", markdown);
        }

        [Fact]
        public void PlanVerificationSection_ShouldSpeakEvenWhenPassed()
        {
            // 표기 부재를 "검증됨"으로 추론하는 것이 이 계열 결함의 뿌리다.
            var section = InstructionEntryPointComposer.PlanVerificationSection(VerificationOutcome.Passed);

            Assert.Contains("이 계획서의 검증 상태", section);
            Assert.NotEmpty(section.Trim());
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~InstructionEntryPointComposerTests"
```

Expected: 컴파일 오류 — `InstructionEntryPointComposer` 형식을 찾을 수 없음

- [ ] **Step 3: `InstructionEntryPointComposer`를 만든다**

`src/ReSet.Core/Services/InstructionEntryPointComposer.cs`:

```csharp
using System.Collections.Generic;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>인덱스 한 줄. 표시 이름과 진입점 기준 상대 경로.</summary>
    public sealed record IndexEntry(string Label, string RelativePath);

    /// <param name="Preamble">최종 계획서의 첫 H2 앞 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="SinglePlanRelativePath">분할 실패 시 계획서 전문의 상대 경로. 분할했으면 null.</param>
    public sealed record EntryPointInputs(
        string JobName,
        string TargetLanguage,
        VerificationOutcome PlanOutcome,
        string Preamble,
        bool StepsSplit,
        IReadOnlyList<IndexEntry> Steps,
        IReadOnlyList<IndexEntry> Dependencies,
        IReadOnlyList<IndexEntry> Specs,
        bool HasStepContract,
        bool HasVerification,
        string? SinglePlanRelativePath);

    /// <summary>
    /// 진입점 `MigrationInstructions.md`를 조립한다.
    ///
    /// 이 클래스가 존재하는 유일한 이유는 <b>순서</b>다. 이전 지시서는 실행 지침을
    /// 7,759줄, 경계 규칙을 7,773줄에 두었는데 코딩 에이전트의 Read는 2,000줄에서
    /// 잘린다. 즉 에이전트는 지침을 보지 못한 채 계획 본문만 읽고 작업을 시작했다.
    /// 지침과 경계 규칙은 어떤 계획 링크보다도 앞에 와야 한다.
    /// </summary>
    public static class InstructionEntryPointComposer
    {
        public static string Compose(EntryPointInputs inputs)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# 🚀 Consolidated Migration Instructions for Coding Agent ({inputs.JobName})");
            sb.AppendLine();
            sb.AppendLine("본 문서는 복수의 SQL Server Stored Procedure를 하나의 통합 배치로 마이그레이션하기 위한 **진입점**입니다.");
            sb.AppendLine("이 파일을 끝까지 읽은 뒤, 배정된 작업 파일(`task-*.md`)이 지시하는 것만 읽고 구현하십시오.");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // 검증 상태가 맨 앞에 온다. 계획을 소비한 뒤에 경고를 만나면 이미 늦다.
            sb.AppendLine(PlanVerificationSection(inputs.PlanOutcome));

            if (!string.IsNullOrWhiteSpace(inputs.Preamble))
            {
                sb.AppendLine();
                sb.AppendLine(inputs.Preamble.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            AppendGuidelines(sb);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendReadingContract(sb, inputs);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendTechStack(sb, inputs);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendIndex(sb, inputs);

            return sb.ToString();
        }

        /// <summary>
        /// 계획서의 검증 상태 배너. `MetadataExporter.BuildPlanVerificationSection`을 그대로
        /// 옮겨 왔다. 통과일 때도 침묵하지 않는다 - "표기 부재 = 검증됨"이라는 추론이
        /// 이 계열 결함의 뿌리다.
        /// </summary>
        public static string PlanVerificationSection(VerificationOutcome planOutcome)
        {
            var label = VerificationDocumentFormatter.StatusLabel(planOutcome);
            var sb = new StringBuilder();

            if (planOutcome == VerificationOutcome.Passed)
            {
                sb.AppendLine("## ✅ 0. 이 계획서의 검증 상태");
                sb.AppendLine();
                sb.AppendLine($"**{label}** — L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과한 계획입니다.");
                return sb.ToString();
            }

            var reason = planOutcome switch
            {
                VerificationOutcome.QualityRejected =>
                    "L2 AI 교차 리뷰의 품질 기준을 통과하지 못한 계획입니다.",
                VerificationOutcome.ReviewNotRun =>
                    "L2 AI 교차 리뷰를 거치지 않은 계획입니다.",
                VerificationOutcome.L1Exhausted =>
                    "L1 기계 검증을 통과하지 못한 채 확정된 계획입니다.",
                _ =>
                    "검증 상태를 확인할 수 없는 계획입니다."
            };

            sb.AppendLine("## ⚠️ 0. 이 계획서의 검증 상태");
            sb.AppendLine();
            sb.AppendLine($"**{label}** — {reason}");
            sb.AppendLine("아래 계획을 그대로 구현하기 전에 사람의 검토가 필요합니다.");
            return sb.ToString();
        }

        private static void AppendGuidelines(StringBuilder sb)
        {
            sb.AppendLine("## 🔑 1. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");
            sb.AppendLine();
            sb.AppendLine("당신은 전문 코딩 에이전트입니다. 아래 지침은 모든 회차에 예외 없이 적용됩니다.");
            sb.AppendLine();
            sb.AppendLine("1. 전환 계획의 배치 단계 및 공통 모듈 설계 규칙을 엄격히 준수할 일.");
            sb.AppendLine("2. 생성할 파일 경로는 타겟 프로젝트의 아키텍처 규칙에 맞춰 작성할 일.");
            sb.AppendLine("3. 데이터 액세스 계층(Repository/DAO 등)은 3장의 데이터 액세스 경계 규칙을 준수하며 타겟 언어 및 프레임워크의 권장 패턴을 따를 일.");
            sb.AppendLine("4. 의존성 역전 원칙(DIP) 등을 준수하여 비즈니스 로직과 인프라스트럭처 결합도를 낮출 일.");
            sb.AppendLine("5. 트랜잭션 단위와 예외 처리(Rollback 등)를 명확히 설계하여 데이터 정합성을 보장할 일.");
            sb.AppendLine("6. 제공된 자가 검증용 단위 테스트 및 아키텍처 검증 코드를 통과(PASS)시키고 빌드가 성공함을 자체 점검할 일.");
            sb.AppendLine("7. **[중요]** 어떠한 경우에도 `// implementation omitted`, `// TODO`, `/* Build SQL */` 등의 주석으로 코드를 생략(Placeholder)하지 마십시오. 3장의 경계 규칙에 따라 SQL 경로로 분류된 DML은 명세서에 있는 원본 로직(조건절·집계식·에러 코드)을 축약 없이 파라미터 바인딩 SQL로 100% 완전하게 작성해야 하며, ORM은 3장의 허용 목록에 한해 사용해야 합니다.");
            sb.AppendLine("8. **[중요]** Worker 구성 시 반드시 명세된 모든 DB Factory 의존성을 `SettleContext`에 할당해야 합니다. 누락 시 런타임 예외가 발생하여 검증을 통과할 수 없습니다.");
            sb.AppendLine("9. **[중요]** 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.cs`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오.");
            sb.AppendLine();
            sb.AppendLine("**[경고] 원본 Stored Procedure(.sql) 파일은 레거시 코드이므로 절대 검색(find 명령어 등)하거나 직접 참조하지 마십시오. 모든 비즈니스 로직은 이미 분석 완료된 Spec.md 문서에 정의되어 있습니다.**");
            sb.AppendLine();
        }

        private static void AppendReadingContract(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 📖 2. 읽기 계약 (Reading Contract)");
            sb.AppendLine();
            sb.AppendLine("이 프로젝트는 **회차 단위**로 구현합니다. 한 회차는 작업 파일 하나(`task-*.md`)에 대응합니다.");
            sb.AppendLine();
            sb.AppendLine("1. 배정된 `task-*.md`와 그 파일이 링크한 것만 읽으십시오.");

            if (inputs.StepsSplit)
            {
                sb.AppendLine("2. **다른 Step 파일을 읽지 마십시오.** 다른 Step의 코드를 작성하지도 마십시오.");
            }
            else
            {
                sb.AppendLine("2. 계획서가 단일 파일로 제공됩니다. **배정된 회차에 해당하는 단계 절만 읽고 구현하십시오.** 다른 Step의 코드를 작성하지 마십시오.");
            }

            sb.AppendLine("3. `common/`이 정의한 공통 계약에 해당하는 기존 파일은 수정하지 마십시오.");
            sb.AppendLine("4. 진행 상태는 도구가 검증 결과를 근거로 기록합니다. `todo.md`를 직접 편집하지 마십시오.");
            sb.AppendLine();
        }

        private static void AppendTechStack(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 🛠️ 3. 기술 스택 및 데이터 액세스 경계");
            sb.AppendLine();
            sb.AppendLine(DataAccessPolicy.InstructionRules(inputs.TargetLanguage));
            sb.AppendLine();
            sb.AppendLine("전문은 [common/02-data-access-boundary.md](common/02-data-access-boundary.md)에 있습니다.");
            sb.AppendLine();
        }

        private static void AppendIndex(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 📂 4. 파일 인덱스");
            sb.AppendLine();
            sb.AppendLine("### 공통 (모든 회차에서 읽습니다)");
            sb.AppendLine();
            sb.AppendLine("- [common/00-architecture.md](common/00-architecture.md) — 아키텍처 개요와 실행 흐름");

            if (inputs.HasStepContract)
            {
                sb.AppendLine("- [common/01-step-contract.md](common/01-step-contract.md) — 모든 단계가 공유하는 실행 계약");
            }

            sb.AppendLine("- [common/02-data-access-boundary.md](common/02-data-access-boundary.md) — SQL/ORM 경계 규칙");
            sb.AppendLine();

            if (inputs.StepsSplit && inputs.Steps.Count > 0)
            {
                sb.AppendLine("### 단계별 상세 (배정된 것만 읽습니다)");
                sb.AppendLine();
                foreach (var step in inputs.Steps)
                {
                    sb.AppendLine($"- [{step.Label}]({step.RelativePath})");
                }
                sb.AppendLine();
            }
            else if (inputs.SinglePlanRelativePath != null)
            {
                sb.AppendLine("### 통합 배치 전환 계획 (단일 파일)");
                sb.AppendLine();
                sb.AppendLine("계획서를 단계별로 분할하지 못했습니다. 아래 파일에서 배정된 단계 절만 찾아 읽으십시오.");
                sb.AppendLine();
                sb.AppendLine($"- [BatchMigrationPlan.md]({inputs.SinglePlanRelativePath})");
                sb.AppendLine();
            }

            if (inputs.HasVerification)
            {
                sb.AppendLine("### 정합성 검증 SQL");
                sb.AppendLine();
                sb.AppendLine("- [verification/integrity-sql.md](verification/integrity-sql.md)");
                sb.AppendLine();
            }

            sb.AppendLine("### 의존 테이블·함수 스키마");
            sb.AppendLine();
            sb.AppendLine("데이터 액세스 계층 구현 시 아래에서 컬럼과 데이터 타입을 확인하십시오. 핵심 비즈니스 로직은 계획서와 명세서만 따르며, 원본 SQL 코드를 조회하려 해서는 안 됩니다.");
            sb.AppendLine();
            foreach (var dep in inputs.Dependencies)
            {
                sb.AppendLine($"- **{dep.Label}**: [{dep.RelativePath}]({dep.RelativePath})");
            }
            sb.AppendLine();

            sb.AppendLine("### 원본 설계 명세서");
            sb.AppendLine();
            sb.AppendLine("개별 프로시저의 세부 로직(UPDATE 수식 등)이 필요할 때만 해당 회차의 것을 참조하십시오.");
            sb.AppendLine();
            foreach (var spec in inputs.Specs)
            {
                sb.AppendLine($"- **{spec.Label}**: [Spec.md]({spec.RelativePath})");
            }
            sb.AppendLine();

            sb.AppendLine("### 진행 상태");
            sb.AppendLine();
            sb.AppendLine("- [todo.md](todo.md) — 도구가 갱신합니다. 읽기 전용으로 참고하십시오.");
            sb.AppendLine();
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~InstructionEntryPointComposerTests"
```

Expected: PASS 10건

`VerificationOutcome`(`src/ReSet.Core/Models/VerificationOutcome.cs`)의 멤버는 `ReviewNotRun`, `L1Exhausted`, `QualityRejected`, `Passed` 넷뿐이다. 위 코드는 이 넷만 쓴다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/InstructionEntryPointComposer.cs tests/ReSet.Core.Tests/InstructionEntryPointComposerTests.cs
git commit -m "feat: compose the entry point with guidelines ahead of the plan

The execution guidelines and boundary rules used to sit past line 7,700,
beyond where a coding agent's Read truncates."
```

---

### Task 7: 번들 파일을 디스크에 쓴다

**Files:**
- Create: `src/ReSet.Core/Services/InstructionBundleWriter.cs`
- Test: `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`

**Interfaces:**
- Consumes: `PlanSlices` (Task 4), `InstructionEntryPointComposer` (Task 6), `PlanLayout` (Task 1), `SpDefinition`/`OutputPathResolver` (기존)
- Produces: `BundleInputs(string JobName, string TargetLanguage, VerificationOutcome PlanOutcome, string FinalPlanMarkdown, PlanLayout? Layout, IReadOnlyList<SpDefinition> SpDefs, OutputPathResolver Paths, string JobOutputDir)`, `BundleResult(string EntryPointPath, IReadOnlyList<string> StepCodes, IReadOnlyList<string> Warnings, bool StepsSplit)`, `InstructionBundleWriter.WriteAsync(BundleInputs, CancellationToken) -> Task<BundleResult>`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class InstructionBundleWriterTests : IDisposable
    {
        private readonly string _outputRoot;
        private readonly string _jobDir;
        private readonly string _agentDir;

        public InstructionBundleWriterTests()
        {
            _outputRoot = Path.Combine(Path.GetTempPath(), "reset-bundle-" + Guid.NewGuid().ToString("N"));
            _jobDir = Path.Combine(_outputRoot, "Jobs", "TestJob");
            _agentDir = Path.Combine(_jobDir, "agent");
            Directory.CreateDirectory(_jobDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, recursive: true);
        }

        private const string FinalPlan = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### 공통 Tasklet 실행 계약

공통 규약 본문

### S01 스냅샷 생성

S01 본문

### S02 원장 생성

S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

        private static BatchStepPlan Step(string code, string name) =>
            new(code, name, new[] { "UP_" + code }, new[] { "dbo.T" }, new[] { "-1" }, false);

        private static PlanLayout Layout(IReadOnlyDictionary<string, string>? violations = null) => new(
            "골격",
            new Dictionary<string, string>
            {
                ["S01"] = "### S01 스냅샷 생성\n조각 본문",
                ["S02"] = "### S02 원장 생성\n조각 본문",
            },
            new[] { Step("S01", "스냅샷 생성"), Step("S02", "원장 생성") },
            violations);

        private static SpDefinition SpDef(string name) => new()
        {
            Schema = "dbo",
            Name = name,
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = string.Empty, Schema = "dbo", Name = "TClient", Type = "Table" }
            }
        };

        private BundleInputs Inputs(PlanLayout? layout) => new(
            JobName: "TestJob",
            TargetLanguage: "C#",
            PlanOutcome: VerificationOutcome.Passed,
            FinalPlanMarkdown: FinalPlan,
            Layout: layout,
            SpDefs: new List<SpDefinition> { SpDef("UP_A") },
            Paths: new OutputPathResolver("SettleDB", _outputRoot),
            JobOutputDir: _jobDir);

        [Fact]
        public async Task WriteAsync_ShouldPlaceEntryPointAtAgentRoot()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Equal(Path.Combine(_agentDir, "MigrationInstructions.md"), result.EntryPointPath);
            Assert.True(File.Exists(result.EntryPointPath));
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteOneFilePerStep()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.True(result.StepsSplit);
            Assert.Equal(new[] { "S01", "S02" }, result.StepCodes.OrderBy(c => c));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S01.md")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S02.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldTakeStepBodiesFromFinalDocument()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var s01 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S01.md"));

            Assert.Contains("S01 본문", s01);
            Assert.DoesNotContain("조각 본문", s01);
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteCommonAndVerificationFiles()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Contains("개요 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "00-architecture.md")));
            Assert.Contains("공통 규약 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "01-step-contract.md")));
            Assert.Contains("데이터 액세스 경계 규칙",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "02-data-access-boundary.md")));
            Assert.Contains("검증 SQL 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "verification", "integrity-sql.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldBannerOnlyTheViolatingStep()
        {
            var violations = new Dictionary<string, string> { ["S02"] = "의사코드가 없습니다." };

            await new InstructionBundleWriter().WriteAsync(Inputs(Layout(violations)), CancellationToken.None);

            var s01 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S01.md"));
            var s02 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S02.md"));

            Assert.DoesNotContain("품질 미달", s01);
            Assert.Contains("품질 미달", s02);
            Assert.Contains("의사코드가 없습니다", s02);
        }

        [Fact]
        public async Task WriteAsync_ShouldFallBackToSinglePlanFile_WhenLayoutMissing()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            Assert.False(result.StepsSplit);
            Assert.False(Directory.Exists(Path.Combine(_agentDir, "steps")));
            Assert.NotEmpty(result.Warnings);

            var entry = await File.ReadAllTextAsync(result.EntryPointPath);
            Assert.Contains("BatchMigrationPlan.md", entry);
        }

        [Fact]
        public async Task WriteAsync_ShouldKeepGuidelinesFirst_EvenInFallback()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            var entry = await File.ReadAllTextAsync(result.EntryPointPath);
            var guidelines = entry.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var planLink = entry.IndexOf("BatchMigrationPlan.md", StringComparison.Ordinal);

            Assert.True(guidelines >= 0 && guidelines < planLink);
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteDependencySchemaFiles()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(_jobDir, "raw", "ddl", "dbo.TClient.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldProduceRelativeLinksThatResolve()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);
            var entry = await File.ReadAllTextAsync(result.EntryPointPath);

            // 진입점이 가리키는 링크는 진입점 위치 기준으로 실제 파일에 닿아야 한다.
            // raw/ddl은 agent/가 아니라 Job 루트 아래에 있으므로 "../"로 올라간다 -
            // 이 경로가 어긋나면 에이전트는 스키마를 영영 읽지 못한다.
            foreach (var relative in new[]
            {
                "common/00-architecture.md", "common/01-step-contract.md",
                "common/02-data-access-boundary.md", "verification/integrity-sql.md",
                "steps/S01.md", "steps/S02.md", "../raw/ddl/dbo.TClient.md",
            })
            {
                Assert.Contains(relative, entry);

                var resolved = Path.GetFullPath(
                    Path.Combine(_agentDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(File.Exists(resolved), $"링크 대상이 없다: {relative}");
            }
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~InstructionBundleWriterTests"
```

Expected: 컴파일 오류 — `InstructionBundleWriter` 형식을 찾을 수 없음

- [ ] **Step 3: `InstructionBundleWriter`를 만든다**

`src/ReSet.Core/Services/InstructionBundleWriter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public sealed record BundleInputs(
        string JobName,
        string TargetLanguage,
        VerificationOutcome PlanOutcome,
        string FinalPlanMarkdown,
        PlanLayout? Layout,
        IReadOnlyList<SpDefinition> SpDefs,
        OutputPathResolver Paths,
        string JobOutputDir);

    /// <param name="StepCodes">실제로 파일이 쓰인 단계 코드. 회차 정의의 근거가 된다.</param>
    public sealed record BundleResult(
        string EntryPointPath,
        IReadOnlyList<string> StepCodes,
        IReadOnlyList<string> Warnings,
        bool StepsSplit);

    /// <summary>
    /// 코딩 에이전트에 넘길 `agent/` 번들을 디스크에 쓴다.
    ///
    /// 이전에는 계획서 전문(7,661줄)을 진입점 한 파일에 인라인했다. 그러면 에이전트가
    /// 읽어야 할 입력이 253k 토큰이 되어 코드를 쓰기 전에 컨텍스트가 찬다. 여기서
    /// 계획을 파일로 나누고, 진입점은 인덱스만 남긴다.
    /// </summary>
    public sealed class InstructionBundleWriter
    {
        public async Task<BundleResult> WriteAsync(BundleInputs inputs, CancellationToken cancellationToken = default)
        {
            var agentDir = Path.Combine(inputs.JobOutputDir, "agent");
            Directory.CreateDirectory(agentDir);

            var slices = PlanBoundaryResolver.Resolve(inputs.FinalPlanMarkdown, inputs.Layout);
            var warnings = new List<string>(slices.Warnings);

            var commonDir = Path.Combine(agentDir, "common");
            Directory.CreateDirectory(commonDir);

            await WriteAsync(Path.Combine(commonDir, "00-architecture.md"), slices.Architecture, cancellationToken);

            if (slices.StepContract != null)
            {
                await WriteAsync(Path.Combine(commonDir, "01-step-contract.md"), slices.StepContract, cancellationToken);
            }

            // 경계 규칙은 계획서가 아니라 DataAccessPolicy에서 온다. 계획 분할이 실패해도
            // 이 파일은 언제나 존재한다 - 규칙 없이 코드를 쓰게 두지 않는다.
            await WriteAsync(
                Path.Combine(commonDir, "02-data-access-boundary.md"),
                "# 데이터 액세스 경계 규칙\n\n" + DataAccessPolicy.InstructionRules(inputs.TargetLanguage),
                cancellationToken);

            if (slices.Verification != null)
            {
                var verificationDir = Path.Combine(agentDir, "verification");
                Directory.CreateDirectory(verificationDir);
                await WriteAsync(
                    Path.Combine(verificationDir, "integrity-sql.md"), slices.Verification, cancellationToken);
            }

            var stepCodes = new List<string>();
            var stepIndex = new List<IndexEntry>();

            if (slices.StepsSplit)
            {
                var stepsDir = Path.Combine(agentDir, "steps");
                Directory.CreateDirectory(stepsDir);

                foreach (var code in OrderedStepCodes(inputs.Layout, slices.Steps))
                {
                    var body = slices.Steps[code];
                    var banner = BuildFloorBanner(inputs.Layout, code);
                    await WriteAsync(
                        Path.Combine(stepsDir, $"{code}.md"), banner + body, cancellationToken);

                    stepCodes.Add(code);
                    stepIndex.Add(new IndexEntry(DescribeStep(inputs.Layout, code), $"steps/{code}.md"));
                }
            }

            var dependencies = await WriteDependencySchemasAsync(inputs, agentDir, cancellationToken);
            var specs = BuildSpecIndex(inputs, agentDir);

            var entryPoint = InstructionEntryPointComposer.Compose(new EntryPointInputs(
                inputs.JobName,
                inputs.TargetLanguage,
                inputs.PlanOutcome,
                slices.Preamble,
                slices.StepsSplit,
                stepIndex,
                dependencies,
                specs,
                HasStepContract: slices.StepContract != null,
                HasVerification: slices.Verification != null,
                SinglePlanRelativePath: slices.StepsSplit ? null : RelativeToAgent(agentDir,
                    Path.Combine(inputs.JobOutputDir, "docs", "BatchMigrationPlan.md"))));

            var entryPointPath = Path.Combine(agentDir, "MigrationInstructions.md");
            await WriteAsync(entryPointPath, entryPoint, cancellationToken);

            Log.Information(
                "지시서 번들을 작성했습니다 - Job: {JobName}, 단계 분할: {StepsSplit}, 단계 수: {StepCount}개, 경고: {WarningCount}건",
                inputs.JobName, slices.StepsSplit, stepCodes.Count, warnings.Count);

            return new BundleResult(entryPointPath, stepCodes, warnings, slices.StepsSplit);
        }

        /// <summary>
        /// 목차가 선언한 순서를 따른다. 사전 순으로 정렬하면 S10이 S2 앞에 오는 식으로
        /// 회차 순서가 실행 의존성과 어긋난다.
        /// </summary>
        private static IReadOnlyList<string> OrderedStepCodes(
            PlanLayout? layout, IReadOnlyDictionary<string, string> steps)
        {
            if (layout?.Steps is { Count: > 0 })
            {
                var ordered = layout.Steps
                    .Select(step => step.Code)
                    .Where(steps.ContainsKey)
                    .ToList();

                if (ordered.Count == steps.Count)
                {
                    return ordered;
                }
            }

            return steps.Keys.ToList();
        }

        private static string DescribeStep(PlanLayout? layout, string code)
        {
            var name = layout?.Steps?.FirstOrDefault(step =>
                string.Equals(step.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;

            return string.IsNullOrWhiteSpace(name) ? code : $"{code} {name}";
        }

        /// <summary>
        /// 하한 미달 기록이 있는 단계에만 배너를 붙인다. 이전에는 문서 전체 상단에
        /// 배너 하나만 있어 어느 단계가 부실한지 에이전트가 알 수 없었다.
        /// </summary>
        private static string BuildFloorBanner(PlanLayout? layout, string code)
        {
            if (layout?.FloorViolations == null ||
                !layout.FloorViolations.TryGetValue(code, out var reason) ||
                string.IsNullOrWhiteSpace(reason))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**");
            sb.AppendLine("> ");
            sb.AppendLine($"> {reason.Trim()}");
            sb.AppendLine("> ");
            sb.AppendLine("> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오.");
            sb.AppendLine();
            return sb.ToString();
        }

        private static async Task<List<IndexEntry>> WriteDependencySchemasAsync(
            BundleInputs inputs, string agentDir, CancellationToken cancellationToken)
        {
            var rawDdlDir = Path.Combine(inputs.JobOutputDir, "raw", "ddl");
            Directory.CreateDirectory(rawDdlDir);

            var distinct = inputs.SpDefs
                .SelectMany(sp => sp.Dependencies)
                .GroupBy(d => $"{d.Database}.{d.Schema}.{d.Name}")
                .Select(g => g.First())
                .ToList();

            var entries = new List<IndexEntry>();

            foreach (var dep in distinct)
            {
                var cleanName = string.IsNullOrEmpty(dep.Database)
                    ? $"{dep.Schema}.{dep.Name}"
                    : $"{dep.Database}.{dep.Schema}.{dep.Name}";

                var filePath = Path.Combine(rawDdlDir, $"{cleanName}.md");
                var sb = new StringBuilder();
                sb.AppendLine($"# {dep.Type}: {cleanName}");
                sb.AppendLine();

                if (dep.Columns.Count > 0)
                {
                    sb.AppendLine(MetadataExporter.FormatTableSchemaToMarkdown(dep));
                }

                if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                {
                    sb.AppendLine("## Referenced SQL DDL:");
                    sb.AppendLine("```sql");
                    sb.AppendLine(dep.ReferencedDdlText);
                    sb.AppendLine("```");
                }

                await WriteAsync(filePath, sb.ToString(), cancellationToken);
                entries.Add(new IndexEntry(cleanName, RelativeToAgent(agentDir, filePath)));
            }

            return entries;
        }

        private static List<IndexEntry> BuildSpecIndex(BundleInputs inputs, string agentDir)
        {
            var entries = new List<IndexEntry>();

            foreach (var spDef in inputs.SpDefs)
            {
                var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                    inputs.Paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                var specPath = inputs.Paths.ResolveSpecPath(objectKey);
                var label = $"{spDef.Schema}.{spDef.Name}";

                entries.Add(File.Exists(specPath)
                    ? new IndexEntry(label, RelativeToAgent(agentDir, specPath))
                    : new IndexEntry($"{label} (명세서 파일 없음 — 이 단계의 비즈니스 로직은 참조할 수 없습니다)", "#"));
            }

            return entries;
        }

        private static string RelativeToAgent(string agentDir, string absolutePath) =>
            Path.GetRelativePath(agentDir, absolutePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private static Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }
}
```

- [ ] **Step 4: `FormatTableSchemaToMarkdown`을 같은 어셈블리에 노출한다**

`src/ReSet.Core/Services/MetadataExporter.cs:822`의 선언을 바꾼다. 현재는 `private string FormatTableSchemaToMarkdown(DependencyInfo dep)`이다.

```csharp
        /// <summary>
        /// InstructionBundleWriter가 같은 스키마 표를 써야 한다. 표 형식이 두 벌이 되면
        /// 지시서와 다른 산출물이 같은 테이블을 다르게 보여준다.
        /// </summary>
        internal static string FormatTableSchemaToMarkdown(DependencyInfo dep)
```

`static`으로 바뀌어도 기존 인스턴스 호출부 두 곳(`:325`, `:474`)은 그대로 컴파일된다. 본문이 인스턴스 상태(`this`)를 참조하면 컴파일 오류가 나므로, 그 경우에만 참조를 파라미터로 올린다.

`InstructionBundleWriter`에서는 `MetadataExporter.FormatTableSchemaToMarkdown(dep)`로 호출한다 — Step 3 코드의 `FormatTableSchemaToMarkdownPublic` 호출을 이 이름으로 고친다.

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~InstructionBundleWriterTests"
```

Expected: PASS 9건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/InstructionBundleWriter.cs src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs
git commit -m "feat: write the agent bundle as separate common and step files

Per-step quality banners now land on the step that actually fell short
instead of one banner at the top of the whole document."
```

---

### Task 8: 회차별 작업 지시서를 만든다

**Files:**
- Create: `src/ReSet.Core/Services/TaskFileComposer.cs`
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs` (Task 7)
- Test: `tests/ReSet.Core.Tests/TaskFileComposerTests.cs`

**Interfaces:**
- Consumes: `IndexEntry` (Task 6)
- Produces: `StageKind { Bootstrap, Step, Assembly }`, `TaskFileInputs(StageKind Kind, string JobName, string TargetLanguage, string? StepCode, string? StepName, string? StepRelativePath, string? SpecRelativePath, IReadOnlyList<IndexEntry> Dependencies, bool HasStepContract, bool HasVerification, IReadOnlyList<string> FailedStepCodes, string? SinglePlanRelativePath)`, `TaskFileComposer.Compose(TaskFileInputs) -> string`, `TaskFileComposer.FileName(StageKind, int, string?) -> string`
- `BundleResult`에 `IReadOnlyList<string> TaskFilePaths` 추가

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/TaskFileComposerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class TaskFileComposerTests
    {
        private static TaskFileInputs StepInputs() => new(
            Kind: StageKind.Step,
            JobName: "TestJob",
            TargetLanguage: "C#",
            StepCode: "S01",
            StepName: "스냅샷 생성",
            StepRelativePath: "steps/S01.md",
            SpecRelativePath: "../../Procedures/dbo.UP_A/docs/Spec.md",
            Dependencies: new List<IndexEntry> { new("dbo.TClient", "../raw/ddl/dbo.TClient.md") },
            HasStepContract: true,
            HasVerification: true,
            FailedStepCodes: Array.Empty<string>(),
            SinglePlanRelativePath: null);

        [Fact]
        public void FileName_ShouldPlaceTaskFilesFlatUnderAgent()
        {
            // agent/ 직하가 아니면 ResolveJobDirectory(두 단계 위)가 {jobDir}을
            // agent/로 해석해 --add-dir이 raw/ddl과 Spec.md를 덮지 못한다.
            Assert.Equal("task-00-bootstrap.md", TaskFileComposer.FileName(StageKind.Bootstrap, 0, null));
            Assert.Equal("task-01-S01.md", TaskFileComposer.FileName(StageKind.Step, 1, "S01"));
            Assert.Equal("task-99-assembly.md", TaskFileComposer.FileName(StageKind.Assembly, 99, null));
            Assert.DoesNotContain("/", TaskFileComposer.FileName(StageKind.Step, 1, "S01"));
        }

        [Fact]
        public void FileName_ShouldPadOrdinalToTwoDigits()
        {
            // 파일 목록이 사전 순으로 보일 때 회차 순서와 어긋나지 않게 한다.
            Assert.Equal("task-02-S02.md", TaskFileComposer.FileName(StageKind.Step, 2, "S02"));
            Assert.Equal("task-12-S12.md", TaskFileComposer.FileName(StageKind.Step, 12, "S12"));
        }

        [Fact]
        public void Compose_ShouldLinkEntryPointFirst()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            var entry = markdown.IndexOf("MigrationInstructions.md", StringComparison.Ordinal);
            var step = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(entry >= 0 && entry < step);
        }

        [Fact]
        public void Compose_ShouldScopeToOneStepOnly()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.Contains("S01", markdown);
            Assert.Contains("이번 회차에서 구현할 것", markdown);
            Assert.Contains("다른 Step의 코드를 작성하지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldLinkTheStepSpecAndSchemas()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.Contains("steps/S01.md", markdown);
            Assert.Contains("Procedures/dbo.UP_A/docs/Spec.md", markdown);
            Assert.Contains("../raw/ddl/dbo.TClient.md", markdown);
            Assert.Contains("common/01-step-contract.md", markdown);
        }

        [Fact]
        public void Compose_ShouldTellBootstrapToBuildTheSkeletonOnly()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Bootstrap, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });

            Assert.Contains("공통 인프라", markdown);
            Assert.Contains("Tasklet을 구현하지 마십시오", markdown);
            Assert.DoesNotContain("steps/", markdown);
        }

        [Fact]
        public void Compose_ShouldTellAssemblyToSkipFailedSteps()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Assembly, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
                FailedStepCodes = new[] { "S05", "S09" },
            });

            Assert.Contains("S05", markdown);
            Assert.Contains("S09", markdown);
            Assert.Contains("손대지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldNotClaimAllStepsSucceeded_WhenNoneFailed()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Assembly, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });

            Assert.Contains("파이프라인", markdown);
            Assert.DoesNotContain("손대지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldPointAtSinglePlanFile_WhenNotSplit()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                StepRelativePath = null,
                SinglePlanRelativePath = "../docs/BatchMigrationPlan.md",
            });

            Assert.Contains("BatchMigrationPlan.md", markdown);
            Assert.Contains("S01", markdown);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TaskFileComposerTests"
```

Expected: 컴파일 오류 — `TaskFileComposer` 형식을 찾을 수 없음

- [ ] **Step 3: `TaskFileComposer`를 만든다**

`src/ReSet.Core/Services/TaskFileComposer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Services
{
    public enum StageKind
    {
        Bootstrap,
        Step,
        Assembly
    }

    public sealed record TaskFileInputs(
        StageKind Kind,
        string JobName,
        string TargetLanguage,
        string? StepCode,
        string? StepName,
        string? StepRelativePath,
        string? SpecRelativePath,
        IReadOnlyList<IndexEntry> Dependencies,
        bool HasStepContract,
        bool HasVerification,
        IReadOnlyList<string> FailedStepCodes,
        string? SinglePlanRelativePath);

    /// <summary>
    /// 회차 하나의 작업 지시서를 조립한다.
    ///
    /// 회차 전환은 코딩 엔진에 <b>다른 지시서 경로를 넘기는 것</b>으로 끝난다.
    /// ICodingEngine이 이미 경로를 파라미터로 받으므로 인자 템플릿과
    /// ArgumentTemplateResolver는 손대지 않는다.
    ///
    /// 파일은 반드시 agent/ 직하에 놓는다. 하위 디렉터리에 두면
    /// ArgumentTemplateResolver.ResolveJobDirectory(두 단계 위 = Job 루트)가
    /// {jobDir}을 agent/로 해석해 --add-dir이 raw/ddl과 Spec.md를 덮지 못한다.
    /// </summary>
    public static class TaskFileComposer
    {
        public static string FileName(StageKind kind, int ordinal, string? stepCode) => kind switch
        {
            StageKind.Bootstrap => "task-00-bootstrap.md",
            StageKind.Assembly => "task-99-assembly.md",
            _ => $"task-{ordinal:D2}-{stepCode}.md",
        };

        public static string Compose(TaskFileInputs inputs)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# {Title(inputs)}");
            sb.AppendLine();
            sb.AppendLine("## 먼저 읽을 것");
            sb.AppendLine();
            sb.AppendLine("1. [MigrationInstructions.md](MigrationInstructions.md) — 지침과 읽기 계약. **반드시 먼저 읽으십시오.**");
            sb.AppendLine("2. [common/00-architecture.md](common/00-architecture.md) — 아키텍처 개요");

            if (inputs.HasStepContract)
            {
                sb.AppendLine("3. [common/01-step-contract.md](common/01-step-contract.md) — 모든 단계가 공유하는 실행 계약");
            }

            sb.AppendLine($"{(inputs.HasStepContract ? 4 : 3)}. [common/02-data-access-boundary.md](common/02-data-access-boundary.md) — SQL/ORM 경계 규칙");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            switch (inputs.Kind)
            {
                case StageKind.Bootstrap:
                    AppendBootstrap(sb, inputs);
                    break;
                case StageKind.Assembly:
                    AppendAssembly(sb, inputs);
                    break;
                default:
                    AppendStep(sb, inputs);
                    break;
            }

            return sb.ToString();
        }

        private static string Title(TaskFileInputs inputs) => inputs.Kind switch
        {
            StageKind.Bootstrap => $"회차 0 — 공통 인프라 구성 ({inputs.JobName})",
            StageKind.Assembly => $"최종 회차 — Job 파이프라인 조립 ({inputs.JobName})",
            _ => $"회차 {inputs.StepCode} — {inputs.StepName} ({inputs.JobName})",
        };

        private static void AppendBootstrap(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine("이 회차는 **공통 인프라만** 만듭니다.");
            sb.AppendLine();
            sb.AppendLine("- 프로젝트 골격과 폴더 구조 (Hexagonal Architecture)");
            sb.AppendLine($"- 빌드 환경 구성 및 필수 패키지 설치 ({ToolingPackages(inputs.TargetLanguage)})");
            sb.AppendLine("- 의존성 주입 등록과 Worker 진입점");
            sb.AppendLine("- 커넥션 문자열 설정 파일과 `IDbConnectionFactory` 구현체");
            sb.AppendLine("- `ICheckpointRepository` 구현체");
            sb.AppendLine("- `src/AbstractSettleTasklet.cs`를 프로젝트에 배치 (내용은 수정 금지)");
            sb.AppendLine("- `tests/ArchitectureTests.cs`를 프로젝트에 배치하고 통과시킬 것");
            sb.AppendLine();
            sb.AppendLine("## 하지 말 것");
            sb.AppendLine();
            sb.AppendLine("- **어떤 Tasklet을 구현하지 마십시오.** 단계 구현은 이후 회차의 일입니다.");
            sb.AppendLine("- 단계 상세 문서를 읽지 마십시오.");
            sb.AppendLine();
            AppendDependencies(sb, inputs);
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 아키텍처 테스트가 통과한다. 이 시점에는 Tasklet이 없으므로 Tasklet 관련 규칙은 대상 0건으로 통과한다 — 그것을 검증 통과로 오해하지 마십시오.");
            sb.AppendLine();
        }

        private static void AppendStep(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine($"단계 **{inputs.StepCode} {inputs.StepName}** 하나만 구현합니다.");
            sb.AppendLine();

            if (inputs.StepRelativePath != null)
            {
                sb.AppendLine($"- 단계 상세: [{inputs.StepRelativePath}]({inputs.StepRelativePath})");
            }
            else if (inputs.SinglePlanRelativePath != null)
            {
                sb.AppendLine(
                    $"- 단계 상세: [BatchMigrationPlan.md]({inputs.SinglePlanRelativePath}) 안에서 `{inputs.StepCode}` 절을 찾아 그 절만 읽으십시오.");
            }

            if (inputs.SpecRelativePath != null)
            {
                sb.AppendLine($"- 원본 명세서: [Spec.md]({inputs.SpecRelativePath}) — UPDATE/INSERT 상세 매핑 수식이 필요할 때만 봅니다.");
            }

            sb.AppendLine();
            sb.AppendLine("`AbstractSettleTasklet`을 상속한 Tasklet 클래스 하나와, 그 단계가 필요로 하는 데이터 액세스 코드를 작성하십시오.");
            sb.AppendLine();
            sb.AppendLine("## 하지 말 것");
            sb.AppendLine();
            sb.AppendLine("- **다른 Step 파일을 읽지 마십시오.**");
            sb.AppendLine("- **다른 Step의 코드를 작성하지 마십시오.**");
            sb.AppendLine("- `common/`이 정의한 공통 계약 파일을 수정하지 마십시오.");
            sb.AppendLine("- Placeholder 주석(`// TODO`, `// implementation omitted`)을 남기지 마십시오.");
            sb.AppendLine();
            AppendDependencies(sb, inputs);
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 이 단계의 조건절·집계식·오류 코드가 명세서와 축약 없이 일치한다.");
            sb.AppendLine();
        }

        private static void AppendAssembly(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine("구현된 단계들을 하나의 Job 파이프라인으로 조립합니다.");
            sb.AppendLine();
            sb.AppendLine("- 단계 실행 순서와 선행 조건 검증");
            sb.AppendLine("- 단계 간 예외 전파와 트랜잭션 롤백 처리");
            sb.AppendLine("- 전체 빌드와 아키텍처 테스트 통과");
            sb.AppendLine();

            if (inputs.FailedStepCodes.Count > 0)
            {
                sb.AppendLine("## 미완성 단계");
                sb.AppendLine();
                sb.AppendLine("아래 단계는 검증을 통과하지 못했습니다. **손대지 마십시오.** 파이프라인에서 제외하고 조립하십시오.");
                sb.AppendLine();
                foreach (var code in inputs.FailedStepCodes)
                {
                    sb.AppendLine($"- `{code}`");
                }
                sb.AppendLine();
                sb.AppendLine("이 단계들이 빠졌으므로 최종 빌드가 깨질 수 있습니다. 그 사실을 숨기지 말고 그대로 두십시오.");
                sb.AppendLine();
            }

            if (inputs.HasVerification)
            {
                sb.AppendLine("## 정합성 검증");
                sb.AppendLine();
                sb.AppendLine("- [verification/integrity-sql.md](verification/integrity-sql.md)의 검증 SQL을 실행 가능한 형태로 배치하십시오.");
                sb.AppendLine();
            }
        }

        private static void AppendDependencies(StringBuilder sb, TaskFileInputs inputs)
        {
            if (inputs.Dependencies.Count == 0)
            {
                return;
            }

            sb.AppendLine("## 참조할 스키마");
            sb.AppendLine();
            foreach (var dep in inputs.Dependencies)
            {
                sb.AppendLine($"- **{dep.Label}**: [{dep.RelativePath}]({dep.RelativePath})");
            }
            sb.AppendLine();
        }

        private static string ToolingPackages(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "MyBatis, Spring Data JPA, Mockito, ArchUnit"
                : "Dapper, EF Core, Moq, NetArchTest";
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TaskFileComposerTests"
```

Expected: PASS 9건

- [ ] **Step 5: `InstructionBundleWriter`가 task 파일을 쓰게 한다**

`BundleResult`에 위치 파라미터를 추가한다.

```csharp
    /// <param name="TaskFilePaths">회차 순서대로의 작업 지시서 절대 경로. 회차 정의의 근거.</param>
    public sealed record BundleResult(
        string EntryPointPath,
        IReadOnlyList<string> StepCodes,
        IReadOnlyList<string> Warnings,
        bool StepsSplit,
        IReadOnlyList<string> TaskFilePaths);
```

`WriteAsync`에서 진입점을 쓴 직후에 다음을 추가하고, 반환값에 `taskFiles`를 싣는다.

```csharp
            var taskFiles = new List<string>();
            var singlePlanRelative = slices.StepsSplit
                ? null
                : RelativeToAgent(agentDir, Path.Combine(inputs.JobOutputDir, "docs", "BatchMigrationPlan.md"));

            async Task WriteTaskAsync(StageKind kind, int ordinal, string? code, string? name, string? specRelative)
            {
                var taskInputs = new TaskFileInputs(
                    Kind: kind,
                    JobName: inputs.JobName,
                    TargetLanguage: inputs.TargetLanguage,
                    StepCode: code,
                    StepName: name,
                    StepRelativePath: code != null && slices.StepsSplit ? $"steps/{code}.md" : null,
                    SpecRelativePath: specRelative,
                    Dependencies: dependencies,
                    HasStepContract: slices.StepContract != null,
                    HasVerification: slices.Verification != null,
                    // 회차 실행 전이므로 실패 단계는 아직 없다. 오케스트레이터가
                    // 조립 회차 직전에 이 파일을 다시 쓴다(Task 13).
                    FailedStepCodes: Array.Empty<string>(),
                    SinglePlanRelativePath: singlePlanRelative);

                var path = Path.Combine(agentDir, TaskFileComposer.FileName(kind, ordinal, code));
                await WriteAsync(path, TaskFileComposer.Compose(taskInputs), cancellationToken);
                taskFiles.Add(path);
            }

            await WriteTaskAsync(StageKind.Bootstrap, 0, null, null, null);

            var ordinal = 1;
            foreach (var code in stepCodes)
            {
                await WriteTaskAsync(
                    StageKind.Step, ordinal, code, DescribeStep(inputs.Layout, code), SpecPathForStep(inputs, agentDir, code));
                ordinal++;
            }

            await WriteTaskAsync(StageKind.Assembly, 99, null, null, null);
```

단계 코드에서 명세서 경로를 찾는 헬퍼를 추가한다. 목차의 `LegacyProcedures`가 그 단계가 어느 프로시저에서 왔는지 알려준다.

```csharp
        /// <summary>
        /// 단계가 유래한 레거시 프로시저의 Spec.md 경로. 목차의 LegacyProcedures가
        /// 그 대응을 갖고 있으므로 이름 추측을 하지 않는다. 찾지 못하면 null이며,
        /// 그때 작업 지시서는 명세서 링크 없이 단계 상세만 가리킨다.
        /// </summary>
        private static string? SpecPathForStep(BundleInputs inputs, string agentDir, string stepCode)
        {
            var step = inputs.Layout?.Steps?.FirstOrDefault(s =>
                string.Equals(s.Code, stepCode, StringComparison.OrdinalIgnoreCase));
            if (step == null || step.LegacyProcedures.Count == 0)
            {
                return null;
            }

            foreach (var procedure in step.LegacyProcedures)
            {
                var bare = procedure.Contains('.') ? procedure[(procedure.LastIndexOf('.') + 1)..] : procedure;

                var spDef = inputs.SpDefs.FirstOrDefault(sp =>
                    string.Equals(sp.Name, bare, StringComparison.OrdinalIgnoreCase));
                if (spDef == null)
                {
                    continue;
                }

                var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                    inputs.Paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                var specPath = inputs.Paths.ResolveSpecPath(objectKey);

                if (File.Exists(specPath))
                {
                    return RelativeToAgent(agentDir, specPath);
                }
            }

            return null;
        }
```

- [ ] **Step 6: 번들 작성 테스트에 회차 파일 검증을 추가한다**

`tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`에 추가한다.

```csharp
        [Fact]
        public async Task WriteAsync_ShouldWriteTaskFilesFlatUnderAgent()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Equal(4, result.TaskFilePaths.Count); // bootstrap + S01 + S02 + assembly
            foreach (var path in result.TaskFilePaths)
            {
                Assert.True(File.Exists(path));
                // agent/ 직하여야 ResolveJobDirectory가 Job 루트를 반환한다.
                Assert.Equal(_agentDir, Path.GetDirectoryName(path));
            }
        }

        [Fact]
        public async Task WriteAsync_ShouldOrderTaskFilesByStructureOrder()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var names = result.TaskFilePaths.Select(Path.GetFileName).ToList();

            Assert.Equal(
                new[] { "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md" },
                names);
        }

        [Fact]
        public async Task WriteAsync_ShouldStillWriteBootstrapAndAssembly_WhenNotSplit()
        {
            // 분할이 실패해도 회차 구조 자체는 유지한다 - 한 세션에 전부 몰아넣는
            // 것이 이 작업이 없애려는 바로 그 문제다.
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            var names = result.TaskFilePaths.Select(Path.GetFileName).ToList();

            Assert.Contains("task-00-bootstrap.md", names);
            Assert.Contains("task-99-assembly.md", names);
        }
```

- [ ] **Step 7: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~InstructionBundleWriterTests|FullyQualifiedName~TaskFileComposerTests"
```

Expected: PASS 21건

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/TaskFileComposer.cs src/ReSet.Core/Services/InstructionBundleWriter.cs tests/ReSet.Core.Tests/TaskFileComposerTests.cs tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs
git commit -m "feat: emit one task file per codegen stage

Stage switching is just handing the engine a different instructions path,
so the CLI argument templates stay untouched."
```

---

### Task 9: 진행 상태를 도구가 소유한다

현재 `todo.md`는 에이전트가 `[x]`로 갱신하도록 지시받는다(`MetadataExporter.cs:525`). 에이전트가 지키지 않으면 그만이고, 자기 보고를 검증 없이 신뢰하는 구조다. 검증 결과를 진실의 원천으로 바꾼다.

**Files:**
- Create: `src/ReSet.Core/Services/AgentProgressStore.cs`
- Test: `tests/ReSet.Core.Tests/AgentProgressStoreTests.cs`

**Interfaces:**
- Produces: `StageStatus { Pending, InProgress, Passed, Failed }`, `StageProgress(string Id, string? StepCode, string TaskFileName, StageStatus Status, int Attempts, string? LastGapSummary)`, `AgentProgressStore.Create(string, string, IReadOnlyList<StageProgress>)`, `.Load(string)`, `.Mark(string, StageStatus, int, string?)`, `.SaveAsync(CancellationToken)`, `.Stages`, `.FailedStepCodes`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/AgentProgressStoreTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class AgentProgressStoreTests : IDisposable
    {
        private readonly string _agentDir;

        public AgentProgressStoreTests()
        {
            _agentDir = Path.Combine(Path.GetTempPath(), "reset-progress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_agentDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_agentDir)) Directory.Delete(_agentDir, recursive: true);
        }

        private static IReadOnlyList<StageProgress> Initial() => new List<StageProgress>
        {
            new("00-bootstrap", null, "task-00-bootstrap.md", StageStatus.Pending, 0, null),
            new("01-S01", "S01", "task-01-S01.md", StageStatus.Pending, 0, null),
            new("02-S02", "S02", "task-02-S02.md", StageStatus.Pending, 0, null),
            new("99-assembly", null, "task-99-assembly.md", StageStatus.Pending, 0, null),
        };

        private AgentProgressStore NewStore() =>
            AgentProgressStore.Create(_agentDir, "TestJob", Initial());

        [Fact]
        public async Task SaveAsync_ShouldWriteBothProgressJsonAndTodo()
        {
            await NewStore().SaveAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(_agentDir, "progress.json")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "todo.md")));
        }

        [Fact]
        public async Task SaveAsync_ShouldRenderTodoFromStatus()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 1, null);
            store.Mark("02-S02", StageStatus.Failed, 3, "비즈니스 로직 불일치");
            await store.SaveAsync(CancellationToken.None);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("- [x] `S01`", todo);
            Assert.Contains("- [ ] `S02`", todo);
            Assert.Contains("검증 실패", todo);
            Assert.Contains("비즈니스 로직 불일치", todo);
        }

        [Fact]
        public async Task SaveAsync_ShouldStateThatTheToolOwnsTheFile()
        {
            // 에이전트가 이 파일을 편집해도 다음 저장에서 덮인다. 그 사실을 문서가 말해야 한다.
            await NewStore().SaveAsync(CancellationToken.None);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("도구가", todo);
            Assert.Contains("직접 편집하지", todo);
        }

        [Fact]
        public async Task Load_ShouldRoundTripStages()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 2, null);
            await store.SaveAsync(CancellationToken.None);

            var loaded = AgentProgressStore.Load(_agentDir);

            Assert.NotNull(loaded);
            var s01 = loaded!.Stages.Single(s => s.Id == "01-S01");
            Assert.Equal(StageStatus.Passed, s01.Status);
            Assert.Equal(2, s01.Attempts);
        }

        [Fact]
        public void Load_ShouldReturnNull_WhenFileMissing()
        {
            Assert.Null(AgentProgressStore.Load(_agentDir));
        }

        [Fact]
        public void FailedStepCodes_ShouldListOnlyFailedSteps()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 1, null);
            store.Mark("02-S02", StageStatus.Failed, 3, "gap");

            Assert.Equal(new[] { "S02" }, store.FailedStepCodes);
        }

        [Fact]
        public void FailedStepCodes_ShouldExcludeNonStepStages()
        {
            // Bootstrap과 Assembly는 StepCode가 없다. 조립 회차에 넘길 목록에 섞이면 안 된다.
            var store = NewStore();
            store.Mark("00-bootstrap", StageStatus.Failed, 1, "빌드 실패");

            Assert.Empty(store.FailedStepCodes);
        }

        [Fact]
        public void Mark_ShouldIgnoreUnknownStageId()
        {
            var store = NewStore();

            store.Mark("없는-회차", StageStatus.Passed, 1, null);

            Assert.All(store.Stages, s => Assert.Equal(StageStatus.Pending, s.Status));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentProgressStoreTests"
```

Expected: 컴파일 오류 — `AgentProgressStore` 형식을 찾을 수 없음

- [ ] **Step 3: `AgentProgressStore`를 만든다**

`src/ReSet.Core/Services/AgentProgressStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    public enum StageStatus
    {
        Pending,
        InProgress,
        Passed,
        Failed
    }

    /// <param name="Id">회차 식별자. task 파일 이름에서 접두와 확장자를 뗀 것과 같다.</param>
    /// <param name="StepCode">단계 회차면 그 코드, Bootstrap/Assembly면 null.</param>
    public sealed record StageProgress(
        string Id,
        string? StepCode,
        string TaskFileName,
        StageStatus Status,
        int Attempts,
        string? LastGapSummary);

    /// <summary>
    /// 회차 진행 상태를 소유한다.
    ///
    /// 이전에는 지시서가 에이전트에게 `todo.md`의 `[x]`를 직접 갱신하라고 요구했다.
    /// 그것은 에이전트의 자기 보고를 검증 없이 신뢰하는 구조이고, 지키지 않아도
    /// 아무 일도 일어나지 않았다. 이제 검증 결과만이 상태를 바꾸며, `todo.md`는
    /// 이 상태에서 렌더링되는 사람용 표시다.
    /// </summary>
    public sealed class AgentProgressStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _agentDir;
        private readonly string _jobName;
        private readonly List<StageProgress> _stages;

        private AgentProgressStore(string agentDir, string jobName, IEnumerable<StageProgress> stages)
        {
            _agentDir = agentDir;
            _jobName = jobName;
            _stages = stages.ToList();
        }

        public IReadOnlyList<StageProgress> Stages => _stages;

        /// <summary>검증을 통과하지 못한 단계 코드. 조립 회차가 제외할 목록이다.</summary>
        public IReadOnlyList<string> FailedStepCodes => _stages
            .Where(s => s.Status == StageStatus.Failed && s.StepCode != null)
            .Select(s => s.StepCode!)
            .ToList();

        public static AgentProgressStore Create(
            string agentDir, string jobName, IReadOnlyList<StageProgress> stages) =>
            new(agentDir, jobName, stages);

        public static AgentProgressStore? Load(string agentDir)
        {
            var path = Path.Combine(agentDir, "progress.json");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var document = JsonSerializer.Deserialize<ProgressDocument>(
                    File.ReadAllText(path), JsonOptions);

                if (document?.Stages == null)
                {
                    return null;
                }

                return new AgentProgressStore(agentDir, document.JobName ?? string.Empty, document.Stages);
            }
            catch (JsonException ex)
            {
                // 상태 파일이 깨졌다고 회차 실행을 막지 않는다. 처음부터 다시 도는 편이
                // 낫고, 그 사실은 로그로 남긴다.
                Log.Warning(ex, "진행 상태 파일을 읽지 못했습니다 - Path: {Path}", path);
                return null;
            }
        }

        public void Mark(string stageId, StageStatus status, int attempts, string? gapSummary)
        {
            var index = _stages.FindIndex(s => s.Id == stageId);
            if (index < 0)
            {
                Log.Warning("알 수 없는 회차 식별자입니다 - StageId: {StageId}", stageId);
                return;
            }

            _stages[index] = _stages[index] with
            {
                Status = status,
                Attempts = attempts,
                LastGapSummary = gapSummary,
            };
        }

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_agentDir);

            var json = JsonSerializer.Serialize(
                new ProgressDocument { JobName = _jobName, Stages = _stages }, JsonOptions);

            await File.WriteAllTextAsync(
                Path.Combine(_agentDir, "progress.json"), json, Encoding.UTF8, cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(_agentDir, "todo.md"), RenderTodo(), Encoding.UTF8, cancellationToken);
        }

        private string RenderTodo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 {_jobName} 통합 배치 마이그레이션 진행 상태");
            sb.AppendLine();
            sb.AppendLine("이 파일은 **도구가** 검증 결과를 근거로 갱신합니다. 직접 편집하지 마십시오 — 다음 회차에서 덮어씁니다.");
            sb.AppendLine();

            foreach (var stage in _stages)
            {
                var box = stage.Status == StageStatus.Passed ? "x" : " ";
                var label = stage.StepCode != null ? $"`{stage.StepCode}`" : stage.Id;
                var note = stage.Status switch
                {
                    StageStatus.Failed => $" — ❌ 검증 실패 ({stage.Attempts}회 시도)",
                    StageStatus.InProgress => " — ⏳ 진행 중",
                    StageStatus.Passed => $" — ✅ 통과 ({stage.Attempts}회 시도)",
                    _ => string.Empty,
                };

                sb.AppendLine($"- [{box}] {label}{note}");

                if (stage.Status == StageStatus.Failed && !string.IsNullOrWhiteSpace(stage.LastGapSummary))
                {
                    sb.AppendLine($"  - {stage.LastGapSummary!.Trim()}");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private sealed class ProgressDocument
        {
            public string? JobName { get; set; }
            public List<StageProgress>? Stages { get; set; }
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentProgressStoreTests"
```

Expected: PASS 8건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/AgentProgressStore.cs tests/ReSet.Core.Tests/AgentProgressStoreTests.cs
git commit -m "feat: let the tool own codegen progress instead of the agent

The old todo.md asked the agent to tick its own boxes, which trusted
self-reporting with nothing verifying it."
```

---

### Task 10: `MetadataExporter`가 번들 작성기에 위임한다

`ExportConsolidatedMigrationInstructionsAsync`는 지금 한 메서드가 180줄이고 진입점 조립·스키마 파일 쓰기·todo 생성·스텁 배치를 모두 한다. 앞선 태스크들이 그 책임을 나눠 가졌으므로 위임만 남긴다.

**Files:**
- Modify: `src/ReSet.Core/Services/IMetadataExporter.cs`
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:395-612`
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs` (기존 테스트 갱신)

**Interfaces:**
- Consumes: `InstructionBundleWriter` (Task 7~8), `AgentProgressStore` (Task 9)
- Produces: `IMetadataExporter.ExportConsolidatedMigrationInstructionsAsync(..., PlanLayout? layout = null, CancellationToken ct = default) -> Task<BundleResult>`

- [ ] **Step 1: 인터페이스 시그니처를 바꾼다**

`src/ReSet.Core/Services/IMetadataExporter.cs`:

```csharp
        /// <summary>
        /// 다중 SP와 통합 배치 전환 계획을 기반으로 코딩 에이전트용 번들을 저장한다.
        /// layout이 null이면 계획서를 분할하지 않고 단일 파일로 남긴다.
        /// </summary>
        Task<BundleResult> ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            VerificationOutcome planOutcome,
            string jobName,
            string baseOutputDir,
            string targetLanguage,
            OutputPathResolver paths,
            PlanLayout? layout = null,
            CancellationToken cancellationToken = default);
```

- [ ] **Step 2: 구현을 위임으로 바꾼다**

`src/ReSet.Core/Services/MetadataExporter.cs`의 `ExportConsolidatedMigrationInstructionsAsync` 본문 전체(`:395`~`:612`, `try` 블록 안의 진입점 조립·DDL 쓰기·todo 생성)를 다음으로 교체한다. 그 뒤에 이어지는 `AbstractSettleTasklet` 스텁 배치 블록(`:614`부터)은 **그대로 남긴다** — 그 코드는 여전히 이 메서드의 책임이다.

```csharp
        public async Task<BundleResult> ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            VerificationOutcome planOutcome,
            string jobName,
            string baseOutputDir,
            string targetLanguage,
            OutputPathResolver paths,
            PlanLayout? layout = null,
            CancellationToken cancellationToken = default)
        {
            Log.Information("통합 마이그레이션 지시서 번들 내보내기 시작 - JobName: {JobName}, OutputDir: {OutputDir}",
                jobName, baseOutputDir);

            var bundle = await new InstructionBundleWriter().WriteAsync(
                new BundleInputs(
                    jobName, targetLanguage, planOutcome, consolidatedPlan,
                    layout, spDefs, paths, baseOutputDir),
                cancellationToken);

            var agentFolder = Path.Combine(baseOutputDir, "agent");

            // 회차 목록은 번들이 실제로 쓴 task 파일에서 나온다. 두 곳이 각자
            // 회차를 세면 progress.json이 존재하지 않는 회차를 가리킬 수 있다.
            var stages = bundle.TaskFilePaths
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => new StageProgress(
                    Id: name.StartsWith("task-", StringComparison.Ordinal) ? name["task-".Length..] : name,
                    StepCode: ExtractStepCode(name),
                    TaskFileName: name + ".md",
                    Status: StageStatus.Pending,
                    Attempts: 0,
                    LastGapSummary: null))
                .ToList();

            await AgentProgressStore.Create(agentFolder, jobName, stages).SaveAsync(cancellationToken);

            foreach (var warning in bundle.Warnings)
            {
                Log.Warning("지시서 번들 경고 - {Warning}", warning);
            }

            // (기존 AbstractSettleTasklet 스텁 배치 블록은 여기 이어서 그대로 둔다.)

            return bundle;
        }

        /// <summary>
        /// "task-01-S01" → "S01". Bootstrap과 Assembly는 단계가 아니므로 null이다.
        /// </summary>
        private static string? ExtractStepCode(string taskFileBaseName)
        {
            var parts = taskFileBaseName.Split('-');
            if (parts.Length < 3)
            {
                return null;
            }

            var tail = string.Join("-", parts.Skip(2));
            return tail is "bootstrap" or "assembly" ? null : tail;
        }
```

- [ ] **Step 3: 기존 지시서 테스트를 새 구조에 맞게 갱신한다**

`tests/ReSet.Core.Tests/MetadataExporterTests.cs`에서 `ExportConsolidatedMigrationInstructionsAsync_ShouldCreateInstructionsFile_WithCorrectContent`의 단언을 바꾼다. **계획 전문이 진입점에 인라인된다는 단언(`Assert.Contains(consolidatedPlan, content)`)은 삭제한다.** 그것이 이 작업이 없애려는 바로 그 성질이다.

```csharp
            // Assert
            Assert.True(File.Exists(expectedPath));
            Assert.True(File.Exists(expectedTodoPath));

            var content = await File.ReadAllTextAsync(expectedPath);
            Assert.Contains($"# 🚀 Consolidated Migration Instructions for Coding Agent ({jobName})", content);

            // 계획 전문은 더 이상 진입점에 인라인되지 않는다. 진입점은 인덱스다.
            Assert.DoesNotContain(consolidatedPlan, content);

            // 지침이 어떤 계획 링크보다도 앞에 있어야 한다.
            var guidelines = content.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var index = content.IndexOf("파일 인덱스", StringComparison.Ordinal);
            Assert.True(guidelines >= 0 && guidelines < index);

            Assert.True(File.Exists(tableSchemasPath1));
            var context1 = await File.ReadAllTextAsync(tableSchemasPath1);
            Assert.DoesNotContain("CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;", context1);
            Assert.Contains("TBL_TestDep", context1);
            Assert.Contains("의존 테이블 설명", context1);

            Assert.Contains("raw/ddl/dbo.TBL_TestDep.md", content);
            Assert.Contains("todo.md", content);

            var todoContent = await File.ReadAllTextAsync(expectedTodoPath);
            Assert.Contains($"# 📋 {jobName} 통합 배치 마이그레이션 진행 상태", todoContent);
```

같은 파일의 `ExportConsolidatedMigrationInstructionsAsync_LinksExternalProcedureUnderExternalDirectory`와 `_WritesReasonWhenSpecFileIsMissing`도 진입점 내용에 의존한다. 전자는 Spec 링크 경로 단언이므로 그대로 통과해야 하고, 후자는 "명세서 파일을 찾을 수 없습니다"라는 문구를 본다 — `InstructionBundleWriter.BuildSpecIndex`가 내는 문구가 `"명세서 파일 없음"`이므로 테스트의 기대 문자열을 그에 맞춰 바꾼다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~MetadataExporterTests"
```

Expected: 전건 PASS

- [ ] **Step 5: 전체 테스트로 회귀를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo
```

Expected: 전건 PASS. `Program.cs`의 두 호출부는 `layout` 기본값 덕분에 아직 무변경이며 Task 15에서 배선한다.

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/IMetadataExporter.cs src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "refactor: delegate instruction export to the bundle writer

The entry point is now an index, not a 7,600-line inlined plan."
```

---

## Phase C — 회차를 돌린다

### Task 11: 검증 범위를 회차 단위로 좁힌다

`FileMappingService.cs:27`은 Job 경로에서 `BatchMigrationPlan.md` 하나만 찾는다. 즉 검증 대상이 "계획서 전문 ↔ 소스 전체"의 1쌍이고, L2 AI가 7,661줄과 프로젝트 전체를 한 번에 받는다. Gap 리포트도 Job 단위라 어느 단계가 틀렸는지 지목하지 못한다.

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/FileMappingService.cs`
- Modify: `src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs:43-49`
- Test: `tests/ReSet.Core.Tests/FileMappingServiceScopeTests.cs`

**Interfaces:**
- Produces: `ExplicitPair(string SpecFilePath, string MappedName, string? SourceFileNameHint)`, `FileMappingService.ResolveMappings(ValidatorConfig, IReadOnlyList<ExplicitPair>) -> List<ValidationResult>`, `CodeVerificationOrchestrator.RunVerificationAsync(bool, IReadOnlyList<ValidationResult>?, CancellationToken)`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/FileMappingServiceScopeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class FileMappingServiceScopeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _specDir;
        private readonly string _codeDir;

        public FileMappingServiceScopeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-mapping-" + Guid.NewGuid().ToString("N"));
            _specDir = Path.Combine(_root, "agent", "steps");
            _codeDir = Path.Combine(_root, "src");
            Directory.CreateDirectory(_specDir);
            Directory.CreateDirectory(_codeDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private ValidatorConfig Config() => new()
        {
            SpecDirectory = _specDir,
            SourceCodeDirectory = _codeDir,
            OutputDirectory = Path.Combine(_root, "validation"),
        };

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldMapOneStepToOneFile()
        {
            var spec = Path.Combine(_specDir, "S01.md");
            File.WriteAllText(spec, "### S01 스냅샷 생성");
            var code = Path.Combine(_codeDir, "SnapshotTasklet.cs");
            File.WriteAllText(code, "class SnapshotTasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(spec, "S01", "SnapshotTasklet") });

            var pair = Assert.Single(results);
            Assert.Equal(spec, pair.SpecFilePath);
            Assert.Equal(code, pair.SourceCodePath);
            Assert.Equal("S01", pair.MappedName);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldDropPairWhenSourceMissing()
        {
            // Tasklet이 생성되지 않은 것 자체가 실패 신호다. 소스 디렉터리 전체로
            // 폴백하면 L2에 프로젝트 전체가 들어가 회차 분할의 목적이 사라진다.
            var spec = Path.Combine(_specDir, "S01.md");
            File.WriteAllText(spec, "### S01");

            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(spec, "S01", "SnapshotTasklet") });

            Assert.Empty(results);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldDropPairWhenSpecMissing()
        {
            var results = new FileMappingService().ResolveMappings(
                Config(),
                new[] { new ExplicitPair(Path.Combine(_specDir, "없음.md"), "S01", "X") });

            Assert.Empty(results);
        }

        [Fact]
        public void ResolveMappings_WithExplicitPairs_ShouldFallBackToStepCodeInFileName()
        {
            // 힌트가 없으면 파일명에 단계 코드가 든 것을 찾는다.
            var spec = Path.Combine(_specDir, "S02.md");
            File.WriteAllText(spec, "### S02");
            var code = Path.Combine(_codeDir, "S02LedgerTasklet.cs");
            File.WriteAllText(code, "class S02LedgerTasklet {}");

            var results = new FileMappingService().ResolveMappings(
                Config(), new[] { new ExplicitPair(spec, "S02", null) });

            var pair = Assert.Single(results);
            Assert.Equal(code, pair.SourceCodePath);
        }

        [Fact]
        public void ResolveMappings_WithoutExplicitPairs_ShouldKeepLegacyBehaviour()
        {
            // 기존 오버로드는 BatchMigrationPlan.md 자동 탐색을 그대로 유지해야 한다.
            var planDir = Path.Combine(_root, "agent", "docs");
            Directory.CreateDirectory(planDir);
            File.WriteAllText(Path.Combine(planDir, "BatchMigrationPlan.md"), "## 개요");

            var config = new ValidatorConfig
            {
                SpecDirectory = Path.Combine(_root, "agent"),
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };
            Directory.CreateDirectory(Path.Combine(_codeDir, "docs"));

            var results = new FileMappingService().ResolveMappings(config);

            Assert.NotNull(results);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~FileMappingServiceScopeTests"
```

Expected: 컴파일 오류 — `ExplicitPair` 형식을 찾을 수 없음

- [ ] **Step 3: `ExplicitPair`와 오버로드를 추가한다**

`src/ReSet.Validator.Core/Services/FileMappingService.cs`의 네임스페이스 안, 클래스 밖에 record를 추가한다.

```csharp
    /// <param name="SourceFileNameHint">확장자를 뺀 예상 소스 파일명. null이면 MappedName으로 찾는다.</param>
    public sealed record ExplicitPair(string SpecFilePath, string MappedName, string? SourceFileNameHint);
```

`FileMappingService` 클래스에 오버로드를 추가한다.

```csharp
        /// <summary>
        /// 호출부가 지정한 쌍만 검증 대상으로 만든다.
        ///
        /// 무인자 오버로드는 Job 하나당 BatchMigrationPlan.md 1쌍만 매핑하므로,
        /// L2 AI가 계획서 전문과 프로젝트 전체 소스를 한 번에 받는다. 회차 단위
        /// 검증에서는 그 범위가 회차 분할의 이득을 그대로 되돌린다.
        ///
        /// 소스를 찾지 못한 쌍은 버린다 - 소스 디렉터리 전체로 폴백하면 범위를
        /// 좁힌 의미가 사라진다. Tasklet이 없다는 것 자체가 그 회차의 실패다.
        /// </summary>
        public List<ValidationResult> ResolveMappings(
            ValidatorConfig config, IReadOnlyList<ExplicitPair> explicitPairs)
        {
            var results = new List<ValidationResult>();

            if (!Directory.Exists(config.SourceCodeDirectory))
            {
                Log.Warning("소스코드 디렉토리가 없습니다 - Path: {Path}", config.SourceCodeDirectory);
                return results;
            }

            var sourceFiles = Directory
                .EnumerateFiles(config.SourceCodeDirectory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var pair in explicitPairs)
            {
                if (!File.Exists(pair.SpecFilePath))
                {
                    Log.Warning("검증 대상 설계서가 없습니다 - Name: {Name}, Path: {Path}",
                        pair.MappedName, pair.SpecFilePath);
                    continue;
                }

                var hint = pair.SourceFileNameHint;
                var matched = hint != null
                    ? sourceFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).Equals(hint, StringComparison.OrdinalIgnoreCase))
                    : null;

                // 힌트로 못 찾으면 파일명에 단계 코드가 든 것을 찾는다.
                matched ??= sourceFiles.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Contains(pair.MappedName, StringComparison.OrdinalIgnoreCase));

                if (matched == null)
                {
                    Log.Warning("검증 대상 소스를 찾지 못했습니다 - Name: {Name}", pair.MappedName);
                    continue;
                }

                results.Add(new ValidationResult
                {
                    SpecFilePath = pair.SpecFilePath,
                    SourceCodePath = matched,
                    MappedName = pair.MappedName,
                });
            }

            return results;
        }
```

`using Serilog;`와 `using System;`이 파일에 없으면 추가한다.

- [ ] **Step 4: `CodeVerificationOrchestrator`에 스코프 오버로드를 추가한다**

`src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs`의 `RunVerificationAsync`를 다음 형태로 바꾼다. 본문은 그대로 두고 매핑 획득만 분기한다.

```csharp
        public Task<List<ValidationResult>> RunVerificationAsync(
            bool isBatchMode, CancellationToken cancellationToken = default) =>
            RunVerificationAsync(isBatchMode, null, cancellationToken);

        /// <summary>
        /// explicitPairs가 주어지면 그 쌍만 검증한다. null이면 기존처럼 자동 탐색한다.
        /// 회차 단위 실행이 L2 입력을 Job 전체가 아니라 단계 하나로 좁히기 위한 통로다.
        /// </summary>
        public async Task<List<ValidationResult>> RunVerificationAsync(
            bool isBatchMode,
            IReadOnlyList<ExplicitPair>? explicitPairs,
            CancellationToken cancellationToken = default)
        {
            Log.Information("[코드검증] 검증 오케스트레이션 시작 - BatchMode: {IsBatchMode}, SpecDir: {SpecDir}, CodeDir: {CodeDir}, 명시적 쌍: {HasExplicit}",
                isBatchMode, _config.SpecDirectory, _config.SourceCodeDirectory, explicitPairs != null);

            _ui?.ShowInfo("1. 설계서 및 소스코드 매핑 구성 중...");
            var mappedPairs = explicitPairs != null
                ? _mappingService.ResolveMappings(_config, explicitPairs)
                : _mappingService.ResolveMappings(_config);

            // (이하 기존 본문 그대로)
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~FileMappingServiceScopeTests|FullyQualifiedName~CodeVerificationOrchestratorTests"
```

Expected: 전건 PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Validator.Core/Services/FileMappingService.cs src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs tests/ReSet.Core.Tests/FileMappingServiceScopeTests.cs
git commit -m "feat: allow verification to be scoped to one step

Job-wide mapping fed the whole plan and the whole source tree to L2 in a
single call, and its gap report could not name a step."
```

---

### Task 12: 회차 목록 모델

**Files:**
- Create: `src/ReSet.Validator.Core/Models/CodegenStage.cs`
- Test: `tests/ReSet.Core.Tests/CodegenStagePlanTests.cs`

**Interfaces:**
- Consumes: `StageProgress`/`StageKind` (Task 8~9), `BundleResult` (Task 8)
- Produces: `CodegenStage(string Id, StageKind Kind, string TaskFilePath, string? StepCode, string? StepSpecPath)`, `CodegenStagePlan.FromBundle(BundleResult, string agentDir) -> CodegenStagePlan`, `CodegenStagePlan.Stages`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CodegenStagePlanTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using ReSet.Validator.Core.Models;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodegenStagePlanTests
    {
        private static string Agent => Path.Combine(Path.GetTempPath(), "JobX", "agent");

        private static BundleResult Bundle() => new(
            EntryPointPath: Path.Combine(Agent, "MigrationInstructions.md"),
            StepCodes: new[] { "S01", "S02" },
            Warnings: Array.Empty<string>(),
            StepsSplit: true,
            TaskFilePaths: new[]
            {
                Path.Combine(Agent, "task-00-bootstrap.md"),
                Path.Combine(Agent, "task-01-S01.md"),
                Path.Combine(Agent, "task-02-S02.md"),
                Path.Combine(Agent, "task-99-assembly.md"),
            });

        [Fact]
        public void FromBundle_ShouldPreserveStageOrder()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(
                new[] { StageKind.Bootstrap, StageKind.Step, StageKind.Step, StageKind.Assembly },
                plan.Stages.Select(s => s.Kind));
        }

        [Fact]
        public void FromBundle_ShouldAttachStepCodesToStepStagesOnly()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Null(plan.Stages[0].StepCode);
            Assert.Equal("S01", plan.Stages[1].StepCode);
            Assert.Equal("S02", plan.Stages[2].StepCode);
            Assert.Null(plan.Stages[3].StepCode);
        }

        [Fact]
        public void FromBundle_ShouldPointStepSpecAtTheStepFile()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(Path.Combine(Agent, "steps", "S01.md"), plan.Stages[1].StepSpecPath);
            Assert.Null(plan.Stages[0].StepSpecPath);
        }

        [Fact]
        public void FromBundle_ShouldFallBackToPlanFile_WhenNotSplit()
        {
            // 분할이 실패하면 단계별 파일이 없다. 그때는 검증 대상 설계서가 계획서 전문이다.
            var bundle = Bundle() with
            {
                StepsSplit = false,
                StepCodes = Array.Empty<string>(),
                TaskFilePaths = new[]
                {
                    Path.Combine(Agent, "task-00-bootstrap.md"),
                    Path.Combine(Agent, "task-99-assembly.md"),
                },
            };

            var plan = CodegenStagePlan.FromBundle(bundle, Agent);

            Assert.Equal(2, plan.Stages.Count);
            Assert.All(plan.Stages, s => Assert.Null(s.StepSpecPath));
        }

        [Fact]
        public void FromBundle_ShouldDeriveIdFromTaskFileName()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(new[] { "00-bootstrap", "01-S01", "02-S02", "99-assembly" },
                plan.Stages.Select(s => s.Id));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CodegenStagePlanTests"
```

Expected: 컴파일 오류 — `CodegenStagePlan` 형식을 찾을 수 없음

- [ ] **Step 3: `CodegenStage`와 `CodegenStagePlan`을 만든다**

`src/ReSet.Validator.Core/Models/CodegenStage.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Validator.Core.Models
{
    /// <param name="Id">"01-S01" 형태. progress.json의 회차 식별자와 같다.</param>
    /// <param name="StepSpecPath">이 회차의 검증 대상 설계서. 단계 회차이고 분할에 성공했을 때만 값이 있다.</param>
    public sealed record CodegenStage(
        string Id,
        StageKind Kind,
        string TaskFilePath,
        string? StepCode,
        string? StepSpecPath);

    /// <summary>
    /// 회차 실행 순서. 번들이 실제로 쓴 task 파일에서 파생한다.
    ///
    /// 회차 수를 두 곳에서 각자 세지 않는다 - 파일이 없는 회차를 실행하거나
    /// 파일이 있는데 실행하지 않는 어긋남을 구조적으로 막는다.
    /// </summary>
    public sealed record CodegenStagePlan(IReadOnlyList<CodegenStage> Stages)
    {
        public static CodegenStagePlan FromBundle(BundleResult bundle, string agentDir)
        {
            var stages = new List<CodegenStage>();

            foreach (var taskPath in bundle.TaskFilePaths)
            {
                var baseName = Path.GetFileNameWithoutExtension(taskPath);
                var id = baseName.StartsWith("task-", StringComparison.Ordinal)
                    ? baseName["task-".Length..]
                    : baseName;

                var (kind, stepCode) = Classify(id);

                var specPath = kind == StageKind.Step && bundle.StepsSplit && stepCode != null
                    ? Path.Combine(agentDir, "steps", $"{stepCode}.md")
                    : null;

                stages.Add(new CodegenStage(id, kind, taskPath, stepCode, specPath));
            }

            return new CodegenStagePlan(stages);
        }

        private static (StageKind Kind, string? StepCode) Classify(string id)
        {
            var parts = id.Split('-');
            var tail = parts.Length > 1 ? string.Join("-", parts.Skip(1)) : id;

            return tail switch
            {
                "bootstrap" => (StageKind.Bootstrap, null),
                "assembly" => (StageKind.Assembly, null),
                _ => (StageKind.Step, tail),
            };
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CodegenStagePlanTests"
```

Expected: PASS 5건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Validator.Core/Models/CodegenStage.cs tests/ReSet.Core.Tests/CodegenStagePlanTests.cs
git commit -m "feat: derive the codegen stage list from the written task files

Counting stages twice would let the runner execute a stage whose file was
never written."
```

---

### Task 13: 회차를 순차로 돌린다

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs`
- Test: `tests/ReSet.Core.Tests/CodegenStagedWorkflowTests.cs`

**Interfaces:**
- Consumes: `CodegenStagePlan` (Task 12), `AgentProgressStore` (Task 9), `ExplicitPair` (Task 11), 기존 `RunSelfHealingWorkflowAsync`의 재시도 규율
- Produces: `StagedWorkflowResult(bool AllPassed, IReadOnlyList<string> FailedStepCodes, string? AbortReason)`, `CodegenWorkflowOrchestrator.RunStagedWorkflowAsync(string jobName, CodegenStagePlan, string agentDir, string codeDir, bool isBatchMode, CancellationToken) -> Task<StagedWorkflowResult>`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CodegenStagedWorkflowTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 회차 루프의 규율만 검증한다. L1/L2 판정 자체는 대상이 아니다.
    /// CodeVerificationOrchestrator는 구상 클래스라 목으로 감쌀 수 없으므로,
    /// SpecDirectory/SourceCodeDirectory를 빈 임시 폴더로 두어 매핑 0건 →
    /// 검증 통과가 되게 하고, 실패 회차는 엔진이 산출물을 남기지 않게 해서 만든다.
    /// </summary>
    public class CodegenStagedWorkflowTests : IDisposable
    {
        private readonly string _root;
        private readonly string _agentDir;
        private readonly string _codeDir;

        public CodegenStagedWorkflowTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-staged-" + Guid.NewGuid().ToString("N"));
            _agentDir = Path.Combine(_root, "agent");
            _codeDir = Path.Combine(_root, "src");
            Directory.CreateDirectory(_agentDir);
            Directory.CreateDirectory(_codeDir);

            foreach (var name in new[]
            {
                "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md",
            })
            {
                File.WriteAllText(Path.Combine(_agentDir, name), "# " + name);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private CodegenStagePlan Plan() => CodegenStagePlan.FromBundle(
            new BundleResult(
                Path.Combine(_agentDir, "MigrationInstructions.md"),
                new[] { "S01", "S02" },
                Array.Empty<string>(),
                StepsSplit: true,
                new[]
                {
                    Path.Combine(_agentDir, "task-00-bootstrap.md"),
                    Path.Combine(_agentDir, "task-01-S01.md"),
                    Path.Combine(_agentDir, "task-02-S02.md"),
                    Path.Combine(_agentDir, "task-99-assembly.md"),
                }),
            _agentDir);

        private CodegenWorkflowOrchestrator Build(ICodingEngine engine, int maxAttempts = 2)
        {
            var config = new ValidatorConfig
            {
                SpecDirectory = Path.Combine(_root, "empty-spec"),
                SourceCodeDirectory = _codeDir,
                OutputDirectory = Path.Combine(_root, "validation"),
            };
            Directory.CreateDirectory(config.SpecDirectory);

            var verifier = new CodeVerificationOrchestrator(
                config, Substitute.For<IAiClient>(), null, null);

            return new CodegenWorkflowOrchestrator(
                engine, verifier, new MetadataExporter(), maxAttempts);
        }

        /// <summary>산출물을 남기는(= 검증 단계까지 가는) 엔진.</summary>
        private ICodingEngine ProductiveEngine()
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var dir = callInfo.ArgAt<string>(2);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "Produced.cs"), "class Produced {}");
                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        /// <summary>특정 task 파일에 대해서만 산출물을 남기지 않는 엔진.</summary>
        private ICodingEngine EngineFailingOn(string taskFileName)
        {
            var engine = Substitute.For<ICodingEngine>();
            engine.Name.Returns("stub");
            engine.Command.Returns("stub");
            engine.GenerateCodeAsync(
                    Arg.Any<SpDefinition?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var instructions = callInfo.ArgAt<string>(1);
                    if (Path.GetFileName(instructions) == taskFileName)
                    {
                        return Task.FromResult(new CodegenRunResult(false, 1, CliFailureKind.Unknown, "산출물 없음"));
                    }

                    var dir = callInfo.ArgAt<string>(2);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "Produced.cs"), "class Produced {}");
                    return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
                });
            return engine;
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldPassOneTaskFilePerStage()
        {
            var engine = ProductiveEngine();

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var calls = engine.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICodingEngine.GenerateCodeAsync))
                .Select(c => Path.GetFileName((string)c.GetArguments()[1]!))
                .ToList();

            Assert.Equal(
                new[] { "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md" },
                calls);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldContinueAfterAFailedStep()
        {
            // 12개 중 하나가 까다로워도 나머지를 건진다.
            var engine = EngineFailingOn("task-01-S01.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.Equal(new[] { "S01" }, result.FailedStepCodes);

            var calls = engine.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICodingEngine.GenerateCodeAsync))
                .Select(c => Path.GetFileName((string)c.GetArguments()[1]!))
                .ToList();

            Assert.Contains("task-02-S02.md", calls);
            Assert.Contains("task-99-assembly.md", calls);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldAbortWhenBootstrapFails()
        {
            // 공통 계약이 없으면 이후 회차가 성립하지 않는다.
            var engine = EngineFailingOn("task-00-bootstrap.md");

            var result = await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.AllPassed);
            Assert.NotNull(result.AbortReason);

            var calls = engine.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICodingEngine.GenerateCodeAsync))
                .Select(c => Path.GetFileName((string)c.GetArguments()[1]!))
                .ToList();

            Assert.DoesNotContain("task-01-S01.md", calls);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldWriteProgressForEveryStage()
        {
            var engine = EngineFailingOn("task-01-S01.md");

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var progress = AgentProgressStore.Load(_agentDir);

            Assert.NotNull(progress);
            Assert.Equal(4, progress!.Stages.Count);
            Assert.Equal(StageStatus.Failed, progress.Stages.Single(s => s.Id == "01-S01").Status);
            Assert.Equal(StageStatus.Passed, progress.Stages.Single(s => s.Id == "02-S02").Status);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldRewriteAssemblyTaskWithFailedSteps()
        {
            // 조립 회차는 어떤 단계가 미완성인지 알아야 그것을 제외하고 조립한다.
            var engine = EngineFailingOn("task-01-S01.md");

            await Build(engine).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            var assembly = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-99-assembly.md"));

            Assert.Contains("S01", assembly);
            Assert.Contains("손대지 마십시오", assembly);
        }

        [Fact]
        public async Task RunStagedWorkflowAsync_ShouldReportAllPassed_WhenNothingFailed()
        {
            var result = await Build(ProductiveEngine()).RunStagedWorkflowAsync(
                "JobX", Plan(), _agentDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.AllPassed);
            Assert.Empty(result.FailedStepCodes);
            Assert.Null(result.AbortReason);
        }
    }
}
```

`CodegenRunResult`(`src/ReSet.Core/Models/CodegenRunResult.cs:17`)의 인자 순서는 `(bool ProducedArtifacts, int ExitCode, CliFailureKind FailureKind, string? Diagnostic)`이다. `CliFailureKind`(`src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs:6`)에는 `None`이 없고 `NotAuthenticated`, `QuotaExhausted`, `Timeout`, `ToolPermissionDenied`, `Unknown` 다섯뿐이다. `CodegenLoopPolicy.Decide`는 `ProducedArtifacts`를 보므로 성공 케이스의 `FailureKind` 값은 판정에 영향을 주지 않는다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CodegenStagedWorkflowTests"
```

Expected: 컴파일 오류 — `RunStagedWorkflowAsync`가 없음

- [ ] **Step 3: `RunStagedWorkflowAsync`를 추가한다**

`src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs`에 추가한다. 기존 `RunSelfHealingWorkflowAsync`는 **삭제하지 않는다** — 단일 SP 경로가 여전히 쓴다.

```csharp
        /// <param name="AbortReason">회차 0 실패 등으로 루프를 끊은 이유. 끝까지 돌았으면 null.</param>
        public sealed record StagedWorkflowResult(
            bool AllPassed,
            IReadOnlyList<string> FailedStepCodes,
            string? AbortReason);

        /// <summary>
        /// 회차를 순서대로 돌린다.
        ///
        /// 이전에는 Job 하나를 한 번의 기동으로 처리했다. 그러면 에이전트가 공통 인프라와
        /// 12개 단계와 조립을 한 세션에서 해야 하고, 중간에 컨텍스트 압축이 반드시 일어나
        /// 의사코드와 오류 코드가 요약으로 뭉개진다 - "축약 없이 100% 완전"이라는 지침을
        /// 구조적으로 지킬 수 없었다.
        ///
        /// 회차 전환은 코딩 엔진에 다른 지시서 경로를 넘기는 것으로 끝난다. 인자 템플릿은
        /// 손대지 않는다.
        ///
        /// 실패 정책: 회차 0(Bootstrap)이 실패하면 즉시 중단한다 - 공통 계약이 없으면
        /// 이후 회차가 성립하지 않는다. 단계 회차가 실패하면 Failed로 기록하고 다음으로
        /// 넘어간다 - 하나가 까다로워도 나머지를 건지고, 사람이 실패한 것만 손볼 수 있다.
        /// </summary>
        public async Task<StagedWorkflowResult> RunStagedWorkflowAsync(
            string jobName,
            CodegenStagePlan stagePlan,
            string agentDir,
            string codeDir,
            bool isBatchMode,
            CancellationToken cancellationToken)
        {
            var progress = AgentProgressStore.Create(
                agentDir,
                jobName,
                stagePlan.Stages
                    .Select(stage => new StageProgress(
                        stage.Id, stage.StepCode, Path.GetFileName(stage.TaskFilePath),
                        StageStatus.Pending, 0, null))
                    .ToList());

            await progress.SaveAsync(cancellationToken);

            foreach (var stage in stagePlan.Stages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 조립 회차 직전에 실패 목록을 확정해 작업 지시서를 다시 쓴다.
                // 번들 작성 시점에는 아직 아무 회차도 돌지 않아 이 목록이 비어 있었다.
                if (stage.Kind == StageKind.Assembly)
                {
                    await RewriteAssemblyTaskAsync(stage, progress.FailedStepCodes, cancellationToken);
                }

                progress.Mark(stage.Id, StageStatus.InProgress, 0, null);
                await progress.SaveAsync(cancellationToken);

                var outcome = await RunStageAsync(stage, codeDir, isBatchMode, cancellationToken);

                progress.Mark(
                    stage.Id,
                    outcome.Passed ? StageStatus.Passed : StageStatus.Failed,
                    outcome.Attempts,
                    outcome.GapSummary);
                await progress.SaveAsync(cancellationToken);

                if (!outcome.Passed && stage.Kind == StageKind.Bootstrap)
                {
                    var reason = $"[Staged] 회차 0(공통 인프라)이 실패해 이후 회차를 진행할 수 없습니다. {outcome.GapSummary}";
                    Log.Error("{Reason}", reason);
                    return new StagedWorkflowResult(false, progress.FailedStepCodes, reason);
                }

                if (!outcome.Passed)
                {
                    Log.Warning("[Staged] 회차 실패 - Id: {StageId}. 다음 회차로 넘어갑니다.", stage.Id);
                }
            }

            var failed = progress.FailedStepCodes;
            Log.Information(
                "[Staged] 회차 실행 완료 - 전체: {Total}개, 실패 단계: {FailedCount}개",
                stagePlan.Stages.Count, failed.Count);

            return new StagedWorkflowResult(failed.Count == 0, failed, null);
        }

        private sealed record StageOutcome(bool Passed, int Attempts, string? GapSummary);

        /// <summary>
        /// 회차 하나를 재시도와 함께 돌린다. 재시도 규율은 기존 자가 수정 루프와 같다 -
        /// 산출물이 없으면 검증을 건너뛰고, 연속 무산출물이 캡에 닿으면 그 회차를 접는다.
        /// </summary>
        private async Task<StageOutcome> RunStageAsync(
            CodegenStage stage, string codeDir, bool isBatchMode, CancellationToken cancellationToken)
        {
            var maxAttempts = _maxL2Attempts == -1 ? int.MaxValue : _maxL2Attempts;
            var consecutiveNoArtifact = 0;
            string? lastGap = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var run = await _codingEngine.GenerateCodeAsync(
                    null, stage.TaskFilePath, codeDir, cancellationToken);

                var decision = CodegenLoopPolicy.Decide(run);

                if (decision == CodegenLoopDecision.Abort)
                {
                    return new StageOutcome(false, attempt, BuildAbortReason(run));
                }

                if (decision == CodegenLoopDecision.RetryWithoutValidation)
                {
                    consecutiveNoArtifact++;
                    if (consecutiveNoArtifact >= MaxConsecutiveNoArtifactRetries)
                    {
                        return new StageOutcome(false, attempt, BuildAbortReason(run));
                    }

                    continue;
                }

                consecutiveNoArtifact = 0;

                var pairs = BuildVerificationPairs(stage);
                var results = await _verifier.RunVerificationAsync(isBatchMode, pairs, cancellationToken);

                var failures = results.Where(r => !r.L1Passed || !r.L2Passed).ToList();
                if (failures.Count == 0)
                {
                    return new StageOutcome(true, attempt, null);
                }

                lastGap = SummarizeGaps(failures);

                if (attempt < maxAttempts && File.Exists(stage.TaskFilePath))
                {
                    // 피드백은 회차 작업 파일에 붙는다. 80줄 안팎이라 파일 끝에 붙어도 읽힌다 -
                    // 이전에는 7,800줄 문서의 맨 끝, 가장 읽히지 않는 자리였다.
                    await _metadataExporter.AppendFeedbackToInstructionsAsync(
                        stage.TaskFilePath, BuildFeedback(stage, attempt, failures), cancellationToken);
                }
            }

            return new StageOutcome(false, maxAttempts, lastGap);
        }

        /// <summary>
        /// 단계 회차만 검증 범위를 좁힌다. Bootstrap과 Assembly는 대응하는 설계서가 없어
        /// L2 의미론 검증을 걸 수 없다 - 그때는 null을 넘겨 자동 탐색에 맡긴다.
        /// </summary>
        private static IReadOnlyList<ExplicitPair>? BuildVerificationPairs(CodegenStage stage)
        {
            if (stage.Kind != StageKind.Step || stage.StepSpecPath == null || stage.StepCode == null)
            {
                return null;
            }

            return new[] { new ExplicitPair(stage.StepSpecPath, stage.StepCode, null) };
        }

        private static string SummarizeGaps(IReadOnlyList<ValidationResult> failures) =>
            string.Join(" / ", failures.Select(f =>
                !f.L1Passed
                    ? $"{f.MappedName}: L1 {f.L1Message}"
                    : $"{f.MappedName}: L2 {f.GapReport?.OverallStatus}"));

        private static string BuildFeedback(
            CodegenStage stage, int attempt, IReadOnlyList<ValidationResult> failures)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## 🚨 [AI L1/L2 Critic Feedback - {stage.Id} 시도 {attempt}] 🚨");
            sb.AppendLine("다음은 방금 작성한 코드에 대한 자동 검증 결과입니다. 이 피드백을 바탕으로 코드를 수정하십시오.");
            sb.AppendLine();

            foreach (var result in failures)
            {
                sb.AppendLine($"### 결함 발견 파일: {result.MappedName}");

                if (!result.L1Passed)
                {
                    sb.AppendLine("**[L1 정적 검증 실패]**");
                    sb.AppendLine($"- 에러 메시지: {result.L1Message}");
                }

                if (!result.L2Passed && result.GapReport != null)
                {
                    var gap = result.GapReport;
                    sb.AppendLine("**[L2 AI 의미론적 검증 실패]**");
                    sb.AppendLine($"- 종합 상태: {gap.OverallStatus}");
                    sb.AppendLine($"- 입력 파라미터 불일치: {gap.InputParametersGap}");
                    sb.AppendLine($"- 출력 데이터셋 불일치: {gap.OutputResultSetsGap}");
                    sb.AppendLine($"- 비즈니스 로직 불일치: {gap.BusinessLogicGap}");
                    sb.AppendLine($"- 예외 및 트랜잭션 불일치: {gap.ExceptionHandlingGap}");
                    sb.AppendLine($"- 데이터 액세스 경계 위반: {gap.DataAccessBoundaryGap}");
                    sb.AppendLine($"- 💡 **수정 제안**: {gap.Suggestions}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 조립 회차의 작업 지시서에 실패 단계 목록을 실어 다시 쓴다. 파일 전체를
        /// 갈아엎지 않고 전용 마커 구간만 교체해, 피드백 append와 충돌하지 않게 한다.
        /// </summary>
        private static async Task RewriteAssemblyTaskAsync(
            CodegenStage stage, IReadOnlyList<string> failedStepCodes, CancellationToken cancellationToken)
        {
            if (failedStepCodes.Count == 0 || !File.Exists(stage.TaskFilePath))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("## 미완성 단계");
            sb.AppendLine();
            sb.AppendLine("아래 단계는 검증을 통과하지 못했습니다. **손대지 마십시오.** 파이프라인에서 제외하고 조립하십시오.");
            sb.AppendLine();
            foreach (var code in failedStepCodes)
            {
                sb.AppendLine($"- `{code}`");
            }
            sb.AppendLine();
            sb.AppendLine("이 단계들이 빠졌으므로 최종 빌드가 깨질 수 있습니다. 그 사실을 숨기지 말고 그대로 두십시오.");
            sb.AppendLine();

            await File.AppendAllTextAsync(stage.TaskFilePath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }
```

필요한 `using`을 파일 상단에 추가한다: `using ReSet.Core.Services;`(`AgentProgressStore`, `StageKind`), `using ReSet.Validator.Core.Models;`는 이미 있다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CodegenStagedWorkflowTests"
```

Expected: PASS 6건

- [ ] **Step 5: 기존 자가 수정 루프 테스트가 회귀 없는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CodegenWorkflowOrchestratorTests"
```

Expected: 전건 PASS. `RunSelfHealingWorkflowAsync`는 손대지 않았다.

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs tests/ReSet.Core.Tests/CodegenStagedWorkflowTests.cs
git commit -m "feat: run codegen as sequential stages with per-step isolation

A failing step is recorded and skipped instead of killing the run, and
the assembly stage is told which steps to leave out."
```

---

## Phase D — 계약과 배선

### Task 14: 아키텍처 테스트를 실제 규칙으로 채운다

`agent/tests/ArchitectureTests.cs`는 지금 본문이 전부 주석 처리되어 있어 아무것도 검증하지 못한다. 지침 8·9번이 "반드시"로 요구하는 것을 기계가 강제하게 만든다.

**이 태스크에서 L1 규칙을 새로 만들지 않는다.** 경계 규칙 조항 1(전달받은 `conn`/`tran`에 ORM을 참여시킬 것)은 `src/ReSet.Validator.Core/Plugins/TransactionEnlistmentCheck.cs`에 **이미 구현되어 있고** `tests/ReSet.Core.Tests/TransactionEnlistmentCheckTests.cs`가 이를 고정한다. 아키텍처 테스트는 그것을 중복하지 않고, 자기가 못 잡는 항목이 무엇이고 누가 잡는지를 주석으로 밝히기만 한다.

**Files:**
- Modify: `src/ReSet.Core/Services/DataAccessPolicy.cs`
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs` (스텁 배치 블록)
- Test: `tests/ReSet.Core.Tests/AgentContractStubTests.cs`

**Interfaces:**
- Produces: `DataAccessPolicy.ArchitectureTestStub(string targetLanguage) -> string`, `DataAccessPolicy.RepositoryContractStub(string targetLanguage) -> string`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/AgentContractStubTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class AgentContractStubTests
    {
        [Fact]
        public void ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut()
        {
            // 이전 스텁은 본문이 전부 주석이라 통과해도 아무것도 보장하지 않았다.
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("Assert.True", stub);
            Assert.DoesNotContain("// var result = Types.InCurrentDomain()", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldEnforceTaskletInheritance()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("ISettleStep", stub);
            Assert.Contains("AbstractSettleTasklet", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldForbidDirectConnectionCreation()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("SqlConnection", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldCheckStepIdentifiers()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("StepName", stub);
            Assert.Contains("SourceProcName", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldStateWhatItCannotCheck()
        {
            // UseTransaction 강제는 호출 그래프 분석이 필요해 여기서 못 잡는다.
            // 잡아준다고 착각하면 경계 위반이 조용히 통과한다.
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("UseTransaction", stub);
            Assert.Contains("L1", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldUseArchUnitForJava()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("Java");

            Assert.Contains("ArchUnit", stub);
            Assert.DoesNotContain("NetArchTest", stub);
        }

        [Fact]
        public void RepositoryContractStub_ShouldDeclareStepRegistration()
        {
            var stub = DataAccessPolicy.RepositoryContractStub("C#");

            Assert.Contains("ISettleStep", stub);
            Assert.Contains("Order", stub);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentContractStubTests"
```

Expected: 컴파일 오류 — `ArchitectureTestStub`가 없음

- [ ] **Step 3: `DataAccessPolicy`에 계약 스텁을 추가한다**

`src/ReSet.Core/Services/DataAccessPolicy.cs`에 추가한다. 기존 `InstructionRules`·`VerificationCriteria`·`TaskletOrmComment`는 손대지 않는다.

```csharp
        private const string CSharpArchitectureTests = @"using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace ReSet.Batch.Tests.Architecture
{
    /// <summary>
    /// 지시서가 ""반드시""라고 말한 것을 기계가 강제한다.
    ///
    /// [이 테스트가 잡지 못하는 것]
    /// 경계 규칙 조항 1의 후반부 - ORM(EF Core)을 쓸 때 RunBusinessSteps가 받은
    /// conn/tran에 UseTransaction으로 참여시켜야 한다는 요구 - 는 메서드 호출 그래프
    /// 분석이 필요해 여기서 검증할 수 없다. 그 항목은 도구 쪽 L1 정적 검증
    /// (TransactionEnlistmentCheck)이 본다.
    /// 이 테스트가 통과했다고 경계 규칙 전부를 지켰다고 결론짓지 마십시오.
    /// </summary>
    public class ArchitectureTests
    {
        private static Assembly Target => typeof(ReSet.Batch.Core.ISettleStep).Assembly;

        [Fact]
        public void EverySettleStep_MustInherit_AbstractSettleTasklet()
        {
            var offenders = Target.GetTypes()
                .Where(t => typeof(ReSet.Batch.Core.ISettleStep).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => !typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .ToList();

            Assert.True(offenders.Count == 0,
                ""AbstractSettleTasklet을 상속하지 않은 Step: "" + string.Join("", "", offenders));
        }

        [Fact]
        public void Tasklets_MustNotCreate_TheirOwnConnection()
        {
            // 새 커넥션을 만들면 검증기의 Rollback 격리가 깨져 정합성 대조가 오염된다.
            var result = Types.InAssembly(Target)
                .That().Inherit(typeof(ReSet.Batch.Core.AbstractSettleTasklet))
                .ShouldNot().HaveDependencyOn(""Microsoft.Data.SqlClient.SqlConnection"")
                .GetResult();

            Assert.True(result.IsSuccessful,
                ""SqlConnection을 직접 생성한 Tasklet: "" +
                string.Join("", "", result.FailingTypeNames ?? Array.Empty<string>()));
        }

        [Fact]
        public void Domain_MustNotDependOn_Infrastructure()
        {
            var result = Types.InAssembly(Target)
                .That().ResideInNamespaceStartingWith(""ReSet.Batch.Domain"")
                .ShouldNot().HaveDependencyOn(""ReSet.Batch.Infrastructure"")
                .GetResult();

            Assert.True(result.IsSuccessful,
                ""Infrastructure에 의존한 Domain 타입: "" +
                string.Join("", "", result.FailingTypeNames ?? Array.Empty<string>()));
        }

        [Fact]
        public void EveryTasklet_MustDeclare_StepNameAndSourceProcName()
        {
            // 검증기는 이 이름으로 설계서와 코드를 짝짓는다. 비어 있으면 매핑이 끊긴다.
            var offenders = Target.GetTypes()
                .Where(t => typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t =>
                {
                    var instance = (ReSet.Batch.Core.ISettleStep)Activator.CreateInstance(t)!;
                    return string.IsNullOrWhiteSpace(instance.StepName);
                })
                .Select(t => t.FullName)
                .ToList();

            Assert.True(offenders.Count == 0,
                ""StepName이 비어 있는 Tasklet: "" + string.Join("", "", offenders));
        }
    }
}
";

        private const string JavaArchitectureTests = @"package reset.batch.tests.architecture;

import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import com.tngtech.archunit.lang.syntax.ArchRuleDefinition;
import org.junit.jupiter.api.Test;

/**
 * 지시서가 ""반드시""라고 말한 것을 ArchUnit이 강제한다.
 *
 * [이 테스트가 잡지 못하는 것]
 * 경계 규칙 조항 1의 후반부 - ORM(JPA)을 전달받은 커넥션/트랜잭션에 참여시켜야 한다는
 * 요구 - 는 호출 그래프 분석이 필요해 여기서 검증할 수 없다. 그 항목은 도구의 L1
 * 정적 검증이 본다. 이 테스트 통과를 경계 규칙 전체 준수로 읽지 마십시오.
 */
class ArchitectureTests {

    private final JavaClasses classes = new ClassFileImporter().importPackages(""reset.batch"");

    @Test
    void everySettleStepMustExtendAbstractSettleTasklet() {
        ArchRuleDefinition.classes()
            .that().implement(reset.batch.core.ISettleStep.class)
            .and().areNotInterfaces().and().areNotAbstract()
            .should().beAssignableTo(reset.batch.core.AbstractSettleTasklet.class)
            .check(classes);
    }

    @Test
    void taskletsMustNotCreateTheirOwnConnection() {
        ArchRuleDefinition.noClasses()
            .that().areAssignableTo(reset.batch.core.AbstractSettleTasklet.class)
            .should().callMethod(javax.sql.DataSource.class, ""getConnection"")
            .check(classes);
    }

    @Test
    void domainMustNotDependOnInfrastructure() {
        ArchRuleDefinition.noClasses()
            .that().resideInAPackage(""..domain.."")
            .should().dependOnClassesThat().resideInAPackage(""..infrastructure.."")
            .check(classes);
    }
}
";

        /// <summary>
        /// 코딩 에이전트 프로젝트에 배치할 아키텍처 테스트. 이전 스텁은 본문이 전부
        /// 주석이라 통과해도 아무것도 보장하지 않았고, 지침 8·9번의 ""반드시""를
        /// 강제하는 장치가 어디에도 없었다.
        /// </summary>
        public static string ArchitectureTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaArchitectureTests
                : CSharpArchitectureTests;

        private const string CSharpRepositoryContract = @"using System.Collections.Generic;

namespace ReSet.Batch.Core
{
    /// <summary>
    /// 단계 실행 순서를 선언으로 고정한다. 회차마다 다른 프로세스가 Tasklet을
    /// 추가하므로, 순서를 조립 코드에 흩어 두면 회차 간에 어긋난다.
    /// </summary>
    public interface ISettleStepDescriptor
    {
        int Order { get; }
        ISettleStep Step { get; }
    }

    /// <summary>
    /// 데이터 액세스 계층의 최소 계약. 구현체는 회차 0에서 만든다.
    /// 대량 DML·집계·청킹은 이 인터페이스 뒤에서도 파라미터 바인딩 SQL로 작성한다.
    /// </summary>
    public interface ISettleRepository
    {
        int ExecuteNonQuery(string sql, object? parameters);
        IEnumerable<T> Query<T>(string sql, object? parameters);
    }
}
";

        private const string JavaRepositoryContract = @"package reset.batch.core;

import java.util.List;

/**
 * 단계 실행 순서를 선언으로 고정한다. 회차마다 다른 프로세스가 Tasklet을 추가하므로,
 * 순서를 조립 코드에 흩어 두면 회차 간에 어긋난다.
 */
public interface ISettleStepDescriptor {
    int getOrder();
    ISettleStep getStep();
}

/**
 * 데이터 액세스 계층의 최소 계약. 구현체는 회차 0에서 만든다.
 * 대량 DML·집계·청킹은 이 인터페이스 뒤에서도 파라미터 바인딩 SQL로 작성한다.
 */
interface ISettleRepository {
    int executeNonQuery(String sql, Object parameters);
    <T> List<T> query(String sql, Object parameters, Class<T> type);
}
";

        /// <summary>
        /// 회차들이 공유할 계약. ReSet이 인터페이스를 소유하고 구현체와 조립은
        /// 회차 0의 에이전트가 만든다 - 계약은 결정론적으로 고정하되 보일러플레이트는
        /// 에이전트의 유연성에 남긴다.
        ///
        /// 두 언어의 스텁을 각자 전문으로 둔다. 한쪽을 문자열 치환해 다른 쪽을 만들면
        /// 컴파일되지 않는 코드가 산출물로 나간다.
        /// </summary>
        public static string RepositoryContractStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaRepositoryContract
                : CSharpRepositoryContract;
```

Java는 한 파일에 public 타입을 하나만 둘 수 있으므로 `ISettleRepository`는 package-private으로 선언했다. 에이전트가 파일을 나누고 싶으면 나눠도 되며, 계약의 이름과 시그니처만 유지하면 된다.

- [ ] **Step 4: 스텁을 파일로 배치한다**

`src/ReSet.Core/Services/MetadataExporter.cs`의 `AbstractSettleTasklet` 스텁 배치 블록(`:614` 이후)에서, `tests/ArchitectureTests.cs`를 쓰는 부분의 내용을 `DataAccessPolicy.ArchitectureTestStub(targetLanguage)`로 바꾸고, `src/` 아래에 `SettleContracts.cs`(Java면 `ISettleStepDescriptor.java`)를 `DataAccessPolicy.RepositoryContractStub(targetLanguage)`로 추가한다. 기존 `StepLogicTests.cs` 배치는 그대로 둔다.

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentContractStubTests|FullyQualifiedName~DataAccessPolicyTests"
```

Expected: 전건 PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/DataAccessPolicy.cs src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/AgentContractStubTests.cs
git commit -m "feat: give the generated project architecture tests that assert

The old stub had every assertion commented out, so passing it proved
nothing about the two guidelines that say 'must'."
```

---

### Task 15: CLI 배선

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:895`, `:1414`, `RunCodegenEngineAsync`
- Test: 수동 확인 (CLI 경로는 자동 테스트 대상이 아니다)

**Interfaces:**
- Consumes: `ConsolidatedPipelineResult.Layout` (Task 5), `BundleResult` (Task 8), `CodegenStagePlan` (Task 12), `RunStagedWorkflowAsync` (Task 13)

- [ ] **Step 1: 두 호출부에서 `Layout`을 넘긴다**

`src/ReSet.Cli/Program.cs`의 `:895`와 `:1414`에 있는 `ExportConsolidatedMigrationInstructionsAsync` 호출에 인자를 추가하고 반환값을 받는다.

```csharp
                            var bundle = await metadataExporter.ExportConsolidatedMigrationInstructionsAsync(
                                spDefs,
                                consolidatedPlan,
                                pipelineResult.Outcome,
                                cliArgs.JobName,
                                jobsOutputDir,
                                targetLanguage,
                                new OutputPathResolver(resolvedDatabase, outputDir),
                                pipelineResult.Layout,
                                activeCts.Token);

                            foreach (var warning in bundle.Warnings)
                            {
                                AnsiConsole.MarkupLine($"[yellow]경고: {Markup.Escape(warning)}[/]");
                            }

                            AnsiConsole.MarkupLine(
                                $"[green]성공: 통합 마이그레이션 지시서 번들 생성 완료![/] {Markup.Escape(bundle.EntryPointPath)}");
```

`:1414` 쪽도 같은 형태로 바꾼다. 변수명(`cliArgs.JobName` vs `jobName`, `activeCts` 유무)은 각 호출부의 것을 쓴다.

기존의 `var instructionsPath = Path.Combine(jobsOutputDir, "agent", "MigrationInstructions.md");` 줄은 삭제하고 `bundle.EntryPointPath`를 쓴다 — 경로를 두 곳에서 각자 조립하면 한쪽만 바뀌었을 때 어긋난다.

- [ ] **Step 2: `RunCodegenEngineAsync`가 회차 계획을 받게 한다**

`RunCodegenEngineAsync`의 시그니처에 `BundleResult bundle`을 추가하고, `instructionsPath` 파라미터는 제거한다. 내부에서 다음처럼 회차 워크플로를 부른다.

```csharp
            var agentDir = Path.GetDirectoryName(bundle.EntryPointPath)!;
            var stagePlan = CodegenStagePlan.FromBundle(bundle, agentDir);

            var staged = await orchestrator.RunStagedWorkflowAsync(
                jobName, stagePlan, agentDir, targetProjectDir, isBatchMode, cancellationToken);

            if (staged.AbortReason != null)
            {
                AnsiConsole.MarkupLine($"[red]코드 생성 중단: {Markup.Escape(staged.AbortReason)}[/]");
            }
            else if (staged.FailedStepCodes.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]코드 생성 완료 — 검증을 통과하지 못한 단계 {staged.FailedStepCodes.Count}개: " +
                    $"{Markup.Escape(string.Join(", ", staged.FailedStepCodes))}[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]이 단계들은 파이프라인에서 제외되었으므로 최종 빌드가 깨져 있을 수 있습니다.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]코드 생성 완료 — 모든 회차가 검증을 통과했습니다.[/]");
            }
```

호출부 두 곳의 인자도 `instructionsPath` 대신 `bundle`로 바꾼다.

- [ ] **Step 3: 빌드가 통과하는지 확인한다**

```bash
dotnet build ReSet.slnx -v q --nologo
```

Expected: 오류 0개

- [ ] **Step 4: 전체 테스트로 회귀를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --nologo
```

Expected: 전건 PASS

- [ ] **Step 5: 실제 산출물로 손 검증한다**

기존 Job 산출물을 입력으로 번들을 다시 만들어 눈으로 확인한다. `CodegenSettings.Enabled`는 `false`로 두어 코딩 엔진은 기동하지 않는다.

확인할 것:

1. `output/Jobs/<job>/agent/MigrationInstructions.md`가 300줄 이하인가
2. 그 안에서 "에이전트 핵심 수행 지침"이 어떤 `steps/` 링크보다 앞에 있는가
3. `agent/steps/`에 단계 수만큼 파일이 있고, 각 파일 내용이 `docs/BatchMigrationPlan.md`의 해당 구간과 일치하는가
4. `agent/task-*.md`가 `agent/` 직하에 평평하게 있는가
5. `agent/progress.json`과 `agent/todo.md`가 생성되었는가
6. 진입점의 모든 상대 링크가 실제 파일에 닿는가

```bash
# 링크 해석 확인 예시
cd output/Jobs/<job>/agent
grep -o '](\([^)]*\.md\))' MigrationInstructions.md | sed 's/](//;s/)//' | while read -r p; do
  [ -f "$p" ] || echo "깨진 링크: $p"
done
```

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Cli/Program.cs
git commit -m "feat: run the staged codegen workflow from the CLI

The entry-point path now comes from the bundle result instead of being
reassembled at each call site."
```

---

## 완료 조건

- `dotnet build ReSet.slnx` 오류 0개
- `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` 전건 PASS
- 진입점 `MigrationInstructions.md`가 300줄 이하이고 실행 지침이 계획 링크보다 앞에 있다
- 회차당 코딩 에이전트 입력이 40k 토큰 안팎이다
- 분할이 실패해도 회차 구조와 지침 순서는 유지된다
