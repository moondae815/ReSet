# Validator AI Provider Effort Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure `ValidatorAiService` uses the configured `effort` value for all AI requests.

**Architecture:** Replace explicit `null` effort parameters with the instance field `_effort` in all `IAiClient.ChatAsync` calls within `ValidatorAiService`.

**Tech Stack:** .NET Core, C#

## Global Constraints
- Only modify the `effort` parameter in `ChatAsync` calls.
- Do not change method signatures or existing logic.
- Ensure `_effort` is passed as is (it may be null, which is the intended default).

---

### Task 1: Apply Effort Parameter to AI Requests

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/ValidatorAiService.cs`

**Interfaces:**
- Consumes: `IAiClient.ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort, CancellationToken cancellationToken)`
- Produces: Correct passing of `_effort` field to the AI client.

- [ ] **Step 1: Update VerifyCodeAsync**
Modify line 63:
```csharp
// From:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort: null, cancellationToken: cancellationToken);
// To:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort: _effort, cancellationToken: cancellationToken);
```

- [ ] **Step 2: Update GenerateTestParametersAsync**
Modify line 158:
```csharp
// From:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
// To:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: _effort, cancellationToken: cancellationToken);
```

- [ ] **Step 3: Update GenerateMockTableDataAsync**
Modify line 228:
```csharp
// From:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
// To:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: _effort, cancellationToken: cancellationToken);
```

- [ ] **Step 4: Update GenerateUnitTestCodeAsync**
Modify line 282:
```csharp
// From:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
// To:
var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: _effort, cancellationToken: cancellationToken);
```

- [ ] **Step 5: Build and Verify**
Run: `dotnet build`
Expected: Build success.

- [ ] **Step 6: Commit**
```bash
git add src/ReSet.Validator.Core/Services/ValidatorAiService.cs
git commit -m "feat(validator): use configured effort in AI requests"
```
