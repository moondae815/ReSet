# ReSet Code Generation Quality Improvement Design

## Overview
This document outlines the design to improve the external coding agent's performance in the ReSet project. Due to the high complexity and volume of the target stored procedures (e.g., `Settle_Proc_Daily`), the code generation agent struggles with "attention drop", resulting in omitted code and skipped edge cases. 

We will implement three specific architectural improvements to control the agent's behavior and enhance code generation quality: Divide & Conquer prompting, Self-Healing feedback injection, and Base Class template provisioning.

## 1. Agentic Workflow Injection (Superpowers Skills)

### Problem
The agent receives a `todo.md` checklist containing up to 13 steps (one for each Phase + setup steps). Attempting to implement all steps linearly in a single generation turn leads to excessive placeholders (`// implementation omitted`) and dropped constraints (e.g., `bypassPreCheck`). Grouping them and forcing stops is rigid and breaks autonomy.

### Solution
Instead of rigid grouping, we will instruct the external coding agent (e.g., `agy`) to utilize advanced agentic workflows to distribute the complexity.
Update `MetadataExporter.cs` to embed explicit instructions in `MigrationInstructions.md`:
- **Subagent-Driven Development**: Instruct the lead agent to spawn isolated subagents for each major Phase (e.g., Phase0, Phase1a) to distribute context limits.
- **Test-Driven Development (TDD)**: Instruct the agent (or its subagents) to write unit tests (e.g., `PreCheck` logic) *before* implementing the Tasklet.
- **Requesting Code Review**: Instruct the lead agent to act as a reviewer, using a code-review workflow to verify the subagents' work against the Spec before moving to the next Phase.

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
