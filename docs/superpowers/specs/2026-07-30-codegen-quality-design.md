# ReSet Code Generation Quality Improvement Design

## Overview
This document outlines the design to improve the external coding agent's performance in the ReSet project. Due to the high complexity and volume of the target stored procedures (e.g., `Settle_Proc_Daily`), the code generation agent struggles with "attention drop", resulting in omitted code and skipped edge cases. 

We will implement three specific architectural improvements to control the agent's behavior and enhance code generation quality: Divide & Conquer prompting, Self-Healing feedback injection, and Base Class template provisioning.

## 1. Divide & Conquer Prompting (Checklist Grouping)

### Problem
The agent receives a `todo.md` checklist containing up to 13 steps (one for each Phase + setup steps). Attempting to implement all steps in a single generation turn leads to excessive placeholders (`// implementation omitted`) and dropped constraints (e.g., `bypassPreCheck`).

### Solution
Update `MetadataExporter.cs` to group the checklist into logical batches (e.g., Setup, Phase 0-1, Phase 2-3, etc.).
- Modify `MigrationInstructions.md` to instruct the agent to **STOP** after completing a single group and request user permission before continuing.
- Grouping logic:
  - Group 1: Setup & Data Access (Steps 0-3)
  - Group 2: Phase 0 ~ Phase 1
  - Group 3: Phase 2 ~ Phase 3
  - Group 4: Phase 4 ~ Phase 5, Final Integration

## 2. Self-Healing Feedback Injection

### Problem
Currently, `CodegenWorkflowOrchestrator` uses `File.AppendAllTextAsync` to blindly append L1/L2 AI verification feedback (`ValidationReport.md`) to the bottom of `MigrationInstructions.md`. If multiple attempts fail, the feedback history grows indefinitely, confusing the agent with outdated instructions.

### Solution
Inject `IMetadataExporter` into `CodegenWorkflowOrchestrator` to manage feedback cleanly.
- Change constructor: `public CodegenWorkflowOrchestrator(ICodingEngine codingEngine, CodeVerificationOrchestrator verifier, IMetadataExporter metadataExporter, int maxL2Attempts)`
- Update DI / instantiation in `ReSet.Cli/Program.cs` to pass an instance of `MetadataExporter`.
- In `CodegenWorkflowOrchestrator`, replace the `File.AppendAllTextAsync` call with `await _metadataExporter.AppendFeedbackToInstructionsAsync(instructionsFilePath, feedbackBuilder.ToString());`.
- This ensures feedback is neatly replaced within the `<!-- FEEDBACK_START -->` and `<!-- FEEDBACK_END -->` markers.

## 3. Base Class Template Provisioning

### Problem
The agent frequently implements custom, ad-hoc exception handling (e.g., throwing `-9` or `-10`), violating the explicit error mapping requirements of the batch migration plan.

### Solution
Provide a strict, pre-written C# base class template (`AbstractSettleTasklet.cs`) directly in the agent's working directory.
- Update `MetadataExporter.ExportConsolidatedMigrationInstructionsAsync` to generate the file `AbstractSettleTasklet.cs` inside the `agent/src/` or `agent/tests/` scaffolding directory.
- Update `MigrationInstructions.md` to explicitly state: "모든 Tasklet은 사전에 제공된 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 하며, 임의의 상태 코드를 던져서는 안 됩니다."

## Implementation Plan
1. **MetadataExporter.cs Refactoring**:
   - Update `todo.md` string building logic to emit grouped checklists.
   - Insert the `AbstractSettleTasklet` template string and write it to `agent/src/AbstractSettleTasklet.cs`.
   - Update `MigrationInstructions.md` to mention the base class and grouping rules.
2. **CodegenWorkflowOrchestrator.cs Refactoring**:
   - Update constructor.
   - Use `_metadataExporter.AppendFeedbackToInstructionsAsync`.
3. **Program.cs (ReSet.Cli)**:
   - Update `CodegenWorkflowOrchestrator` instantiation to include `IMetadataExporter`.
