# ReSet Multi-Step Agentic Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor ReSet's `GenerateConsolidatedBatchPlanAsync` pipeline to use a 3-step agentic workflow (Brainstorm -> Plan -> Execute) with intermediate file persistence.

**Architecture:** Introduce `BrainstormBatchPlanAsync` and `DraftBatchPlanStructureAsync` into `IAiService` and `AiService`. Modify `VerificationPipelineOrchestrator` to orchestrate these 3 phases sequentially and write outputs to `output/Jobs/{jobName}/raw/`. Update related tests.

**Tech Stack:** C# .NET Core, xUnit, NSubstitute.

## Global Constraints
- Do not remove or alter `IMetadataExporter` or existing TUI workflows.
- Must persist intermediate states to `output/Jobs/{jobName}/raw/Brainstorming.md` and `output/Jobs/{jobName}/raw/PlanStructure.md`.
- Gracefully handle API failures (return nulls if failed).

---

### Task 1: Update IAiService Interface

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs`

**Interfaces:**
- Produces: `Task<AiResult> BrainstormBatchPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);`
- Produces: `Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);`
- Produces (Modified): `Task<AiResult> GenerateConsolidatedBatchPlanAsync(string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);`

- [ ] **Step 1: Write minimal interface implementation changes**
Modify `src/ReSet.Core/Services/IAiService.cs` to add the two new methods and update the signature of `GenerateConsolidatedBatchPlanAsync`.

- [ ] **Step 2: Compile to verify failure (AiService will complain about missing interface methods)**
Run: `dotnet build src/ReSet.Core/ReSet.Core.csproj`
Expected: FAIL due to `AiService` not implementing the new interface methods.

- [ ] **Step 3: Commit**
```bash
git add src/ReSet.Core/Services/IAiService.cs
git commit -m "refactor(core): update IAiService for multi-step agentic workflow"
```

---

### Task 2: Implement Multi-Step AI Methods in AiService

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs`

**Interfaces:**
- Consumes: `IAiService` signatures from Task 1.

- [ ] **Step 1: Implement `BrainstormBatchPlanAsync`**
In `AiService.cs`, implement the method. The `systemPrompt` should instruct the AI to act as an architect and ONLY output brainstorming text (no markdown documents yet). Use `_aiClient.ChatAsync`. Return `AiResult`.

- [ ] **Step 2: Implement `DraftBatchPlanStructureAsync`**
In `AiService.cs`, implement the method. The `systemPrompt` should instruct the AI to create a strict Markdown table of contents and implementation steps based on the provided `brainstormingResult`. Use `_aiClient.ChatAsync`. Return `AiResult`.

- [ ] **Step 3: Update `GenerateConsolidatedBatchPlanAsync`**
In `AiService.cs`, add `string planStructure` to the signature. Update the prompt to state: "You MUST strictly follow this plan structure: {planStructure}" and provide it in the user prompt.

- [ ] **Step 4: Build to verify compilation**
Run: `dotnet build src/ReSet.Core/ReSet.Core.csproj`
Expected: PASS (if tests aren't built yet, otherwise tests will fail).

- [ ] **Step 5: Commit**
```bash
git add src/ReSet.Core/Services/AiService.cs
git commit -m "feat(core): implement 3-phase AI methods in AiService"
```

---

### Task 3: Update VerificationPipelineOrchestrator

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`

**Interfaces:**
- Consumes: The 3 new/modified methods from `AiService`.

- [ ] **Step 1: Update `RunConsolidatedPipelineAsync` logic**
In `VerificationPipelineOrchestrator.cs` around line 1220, replace the single `GenerateConsolidatedBatchPlanAsync` call.
Instead, do:
1. Create `progressScope` for Phase 1.
2. `var brainstormResult = await _consolidatorService.BrainstormBatchPlanAsync(specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken);`
3. Write `brainstormResult.Content` to `output/Jobs/{jobName}/raw/Brainstorming.md`. (Ensure directory exists).
4. Update `progressScope` for Phase 2.
5. `var planResult = await _consolidatorService.DraftBatchPlanStructureAsync(brainstormResult.Content, targetLanguage, jobName, _consolidatorEffort, cancellationToken);`
6. Write `planResult.Content` to `output/Jobs/{jobName}/raw/PlanStructure.md`.
7. Update `progressScope` for Phase 3.
8. `var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(planResult.Content, specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken);`
Proceed with existing L1/L2 validation using `aiResult.Content`.

- [ ] **Step 2: Handle failures gracefully**
If `brainstormResult` or `planResult` is null or empty, log via `_userInteraction.NotifyError` and break the loop or return `(null, null)`.

- [ ] **Step 3: Build to verify compilation**
Run: `dotnet build src/ReSet.Core/ReSet.Core.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**
```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "feat(core): orchestrate 3-step superpowers workflow for batch plan"
```

---

### Task 4: Fix Unit Tests

**Files:**
- Modify: `tests/ReSet.Core.Tests/AiServiceTests.cs`
- Modify: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `IAiService` and `VerificationPipelineOrchestrator` changes.

- [ ] **Step 1: Update `AiServiceTests.cs`**
Update all calls to `GenerateConsolidatedBatchPlanAsync` to include a dummy `planStructure` string (e.g., `"Dummy Plan"`). Add basic tests for `BrainstormBatchPlanAsync` and `DraftBatchPlanStructureAsync` testing exception throwing on empty API keys.

- [ ] **Step 2: Update `VerificationPipelineOrchestratorTests.cs`**
Update NSubstitute setups (`_aiService.GenerateConsolidatedBatchPlanAsync(...)`) to expect the new signature. Add mocked returns for `BrainstormBatchPlanAsync` and `DraftBatchPlanStructureAsync` so the pipeline can proceed without throwing null reference exceptions during tests.

- [ ] **Step 3: Run Tests**
Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**
```bash
git add tests/ReSet.Core.Tests/
git commit -m "test: update unit tests for multi-step AI workflow"
```
