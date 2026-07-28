# Design Spec: Validator AI Provider Effort Integration

## 1. Overview
This design ensures that the `effort` (reasoning effort) specified during the initialization of `ValidatorAiService` is consistently passed to all AI requests. This aligns the Validator's AI behavior with the existing implementation in `ReSet.Core`.

## 2. Target Component
- **Class**: `ReSet.Validator.Core.Services.ValidatorAiService`
- **File**: `src/ReSet.Validator.Core/Services/ValidatorAiService.cs`

## 3. Changes
The `_effort` field, which is already populated via the constructor, will be used instead of `null` in all `_aiClient.ChatAsync` calls.

### Modified Methods:
1. `VerifyCodeAsync`: Update `ChatAsync` call to use `effort: _effort`.
2. `GenerateTestParametersAsync`: Update `ChatAsync` call to use `effort: _effort`.
3. `GenerateMockTableDataAsync`: Update `ChatAsync` call to use `effort: _effort`.
4. `GenerateUnitTestCodeAsync`: Update `ChatAsync` call to use `effort: _effort`.

## 4. Impact & Verification
- **Expected Behavior**: The `IAiClient` will receive the specified effort level (e.g., "high", "medium", "low"), allowing the LLM to adjust its reasoning depth for validation tasks.
- **Verification**: 
    - Code review to ensure all `ChatAsync` calls are updated.
    - Run existing `ValidatorAiService` related tests to ensure no regressions.
