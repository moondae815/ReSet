# Design Spec: ReSet Multi-Step Agentic Workflow (Superpowers Integration)

## 1. Overview
The goal is to transplant the multi-step agentic workflow logic (Brainstorming ➔ Planning ➔ Execution) from the `/superpowers` plugin natively into the ReSet C# application. This will be applied to the "2. 기분석 명세서 통합 배치 전환 계획 수립" (Establish Integrated Batch Migration Plan) feature.

## 2. Approach
We will use **Option A (Orchestrator-Driven Stateful Pipeline)** with intermediate file persistence.

## 3. Architecture & Components

### 3.1. `IAiService.cs` & `AiService.cs`
We will introduce two new methods to support the 3-phase pipeline, effectively breaking down the existing monolithic call:

*   **Phase 1: `BrainstormBatchPlanAsync`**
    *   **Input**: List of `Spec.md` contents, Target Language, Job Name.
    *   **Prompt Duty**: Do not write code or markdown plans. Analyze the specifications, identify common domain logic, and propose an overarching batch architecture (e.g., Tasklet vs. Chunk).
    *   **Output**: `AiResult` containing the brainstorming text.

*   **Phase 2: `DraftBatchPlanStructureAsync`**
    *   **Input**: Brainstorming result from Phase 1, Target Language, Job Name.
    *   **Prompt Duty**: Based on the brainstorming result, create a detailed Table of Contents (TOC) and step-by-step structural plan for the final migration document.
    *   **Output**: `AiResult` containing the document structure text.

*   **Phase 3: `GenerateConsolidatedBatchPlanAsync` (Modification)**
    *   **Input**: Adding `planStructure` as a new parameter.
    *   **Prompt Duty**: Follow the provided `planStructure` strictly to generate the final `BatchMigrationPlan.md` markdown content.

### 3.2. `VerificationPipelineOrchestrator.cs`
The `RunConsolidatedPipelineAsync` method will be heavily refactored to orchestrate this 3-step pipeline.

*   **Step 1: Execute Phase 1 (Brainstorm)**
    *   Call `_consolidatorService.BrainstormBatchPlanAsync`.
    *   Persist the result to `output/Jobs/{JobName}/raw/Brainstorming.md`.
*   **Step 2: Execute Phase 2 (Plan)**
    *   Call `_consolidatorService.DraftBatchPlanStructureAsync` passing the Brainstorming output.
    *   Persist the result to `output/Jobs/{JobName}/raw/PlanStructure.md`.
*   **Step 3: Execute Phase 3 (Generate)**
    *   Call `_consolidatorService.GenerateConsolidatedBatchPlanAsync` passing the Plan Structure.
    *   Proceed with the existing L1 Mechanical Validation and L2 Critic feedback loop on the generated document.

## 4. Error Handling & State Management
*   If Phase 1 or Phase 2 fails (e.g., API timeout or empty response), the orchestrator will catch the exception, log an error to `AnsiConsole`, and abort the pipeline gracefully returning `(null, null)`.
*   The ReSet Progress scope (`_userInteraction.CreateProgressScope`) will be updated to display the current sub-phase (e.g., "1/3: 브레인스토밍", "2/3: 목차 설계", "3/3: 문서 생성").

## 5. Scope Validation
*   No breaking changes to the external CLI engine or TUI rendering mechanisms.
*   The `AiClientFactory` and LLM configurations remain unchanged.
