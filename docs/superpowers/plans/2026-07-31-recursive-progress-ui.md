# Recursive Progress UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 참조 SP/UDF 재귀 분석에서도 스피너·경과시간과 생성/검증 상태를 표시한다.

**Architecture:** 재귀 UI 어댑터가 일반 CLI UI에 상태와 진행 스코프를 위임한다. 의존성 그래프의 단일 텍스트 시작 알림은 제거해 진행 UI와 충돌하지 않게 한다.

**Tech Stack:** .NET 10, xUnit, Spectre.Console.

## Global Constraints

- 재귀 객체별 L3 검토 위임과 실패 격리는 유지한다.
- Core는 CLI UI에 직접 의존하지 않는다.
- 새 동작은 자동화된 단위 테스트로 보호한다.

---

### Task 1: 재귀 UI 어댑터의 상태와 진행 표시 위임

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:425-440, 1832-1858`
- Test: `tests/ReSet.Core.Tests/CliArgsTests.cs`

**Interfaces:**
- Consumes: `IVerificationUserInteraction.NotifyStatus(string)` 및 `CreateProgressScope(string)`
- Produces: 재귀 파이프라인에서 활성 `ConsoleProgressScope`와 상태 메시지

- [x] **Step 1: Write the failing test**

```csharp
var adapter = CreateRecursiveInteractionAdapter(interaction);
adapter.NotifyStatus("UDF: dbo.Child - 검증 중...");
Assert.Equal("UDF: dbo.Child - 검증 중...", interaction.LastStatus);
Assert.Same(interaction.ProgressScope, adapter.CreateProgressScope("Critic 검토"));
```

Create the internal nested adapter through `typeof(Program).GetNestedType("RecursiveAnalysisUserInteraction", BindingFlags.NonPublic)` so the CLI production API remains unchanged.

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~RecursiveAnalysisUserInteraction`

Expected: FAIL because the adapter currently drops the status and returns `NullProgressScope`.

- [x] **Step 3: Write minimal implementation**

```csharp
public void NotifyStatus(string message) => _interactiveUserInteraction.NotifyStatus(message);
public IMultiProgressScope CreateProgressScope(string title) =>
    _interactiveUserInteraction.CreateProgressScope(title);
```

Remove the `Progress = RenderDependencyAnalysisProgress` assignment and the unused text renderer so only the pipeline progress UI writes active-state output.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~RecursiveAnalysisUserInteraction`

Expected: PASS.

- [x] **Step 5: Run regression verification and commit**

Run: `dotnet test ReSet.slnx --no-restore --verbosity minimal` and `dotnet build ReSet.slnx --no-restore --verbosity minimal`.

Commit: `fix: show recursive analysis progress`
