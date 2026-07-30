# Codegen Quality Improvement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed `superpowers` agentic workflows (Subagent-Driven Development, TDD, Code Review) into `MigrationInstructions.md`, refactor self-healing feedback injection to avoid duplication, and provide a pre-written C# base class template to enforce standard exception handling.

**Architecture:** 
1. Update `MetadataExporter.cs` to embed advanced workflow instructions and generate `AbstractSettleTasklet.cs` inside `agent/src/`. 
2. Update `CodegenWorkflowOrchestrator.cs` and `ReSet.Cli/Program.cs` to manage iterative L2 feedback using `IMetadataExporter` instead of blindly appending.

**Tech Stack:** C#, .NET 10

## Global Constraints
- Target Language: C#
- Do not remove existing logic unrelated to this feature (e.g., Markdown generation format).

---

### Task 1: Embed Agentic Workflow Instructions in `MigrationInstructions.md`

**Files:**
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs`

**Interfaces:**
- Consumes: N/A
- Produces: Generates an updated `agent/todo.md` with explicit instructions to use subagents and TDD.

- [ ] **Step 1: Modify `ExportConsolidatedMigrationInstructionsAsync` for `todo.md` Generation**
Find the block where `todoSb` is populated (around line 289). Update the "필수 행동 수칙: SP 구현 5단계 루프" to instruct the agent to use superpowers skills.

Modify the text to explicitly require:
```csharp
todoSb.AppendLine("## ⚠️ [필수 행동 수칙: Agentic Workflow 루프]");
todoSb.AppendLine("각 Step(`SP_NAME`)을 구현할 때, 반드시 아래의 **Superpowers Skills** 워크플로우를 활용하십시오.");
todoSb.AppendLine("1. **Subagent-Driven Development**: 복잡한 Phase(Tasklet) 구현 시, 주 에이전트가 직접 모든 코드를 작성하지 말고 `invoke_subagent` 도구를 사용해 서브에이전트에게 구현을 위임하십시오.");
todoSb.AppendLine("2. **Test-Driven Development (TDD)**: 서브에이전트는 반드시 비즈니스 로직(예: PreCheck)을 작성하기 전에 실패하는 XUnit 테스트를 먼저 작성하고 통과시켜야 합니다.");
todoSb.AppendLine("3. **Requesting Code Review**: 서브에이전트가 구현을 완료하면, 주 에이전트는 코드 리뷰를 수행하여 Spec.md의 모든 예외 처리 및 쿼리 조건이 누락 없이 반영되었는지 검증하십시오.");
```

- [ ] **Step 2: Commit**
```bash
git add src/ReSet.Core/Services/MetadataExporter.cs
git commit -m "feat: Embed agentic workflow instructions in MigrationInstructions"
```

---

### Task 2: Provide `AbstractSettleTasklet` Base Class Template

**Files:**
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs`

**Interfaces:**
- Consumes: N/A
- Produces: Creates `AbstractSettleTasklet.cs` in `agent/src/` folder during instruction generation.

- [ ] **Step 1: Generate `agent/src` Folder and Base Class Template**
In `MetadataExporter.cs` inside `ExportConsolidatedMigrationInstructionsAsync`, just before writing the test stubs (around line 314), add logic to create an `agent/src` folder and write `AbstractSettleTasklet.cs`.

```csharp
var agentSrcFolder = Path.Combine(agentFolder, "src");
if (!Directory.Exists(agentSrcFolder))
{
    Directory.CreateDirectory(agentSrcFolder);
}

if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
{
    var baseClassStub = @"using System;
using System.Data;

namespace ReSet.Batch.Core
{
    public interface ISettleStep
    {
        string StepName { get; }
        StepResult Execute(SettleContext context);
    }

    public abstract class AbstractSettleTasklet : ISettleStep
    {
        public abstract string StepName { get; }
        protected abstract string SourceProcName { get; }

        public StepResult Execute(SettleContext context)
        {
            if (context.Checkpoint.IsStepCompleted(StepName, context.Ymd))
            {
                return new StepResult { Code = 0, Message = ""이미 완료된 Step 재시작 스킵"", SourceProcName = SourceProcName };
            }

            int stateCode = 0;
            using var conn = context.MainDb.CreateConnection();
            conn.Open();
            using (var cmdIso = conn.CreateCommand())
            {
                cmdIso.CommandText = ""SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"";
                cmdIso.ExecuteNonQuery();
            }

            try
            {
                var preCheckFail = PreCheck(conn, context, ref stateCode);
                if (preCheckFail != null) return preCheckFail;

                using var tran = conn.BeginTransaction();
                try
                {
                    RunBusinessSteps(conn, tran, context, ref stateCode);
                    tran.Commit();
                    context.Checkpoint.MarkStepCompleted(StepName, context.Ymd);
                    return new StepResult { Code = 0, Message = ""정상 완료"", SourceProcName = SourceProcName };
                }
                catch
                {
                    if (tran.Connection != null) tran.Rollback();
                    OnFailureCompensation(context, stateCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new StepResult { Code = stateCode, Message = ex.Message, SourceProcName = SourceProcName };
            }
        }

        protected abstract StepResult PreCheck(IDbConnection conn, SettleContext context, ref int stateCode);
        protected abstract void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode);
        protected virtual void OnFailureCompensation(SettleContext context, int failedStateCode) { }
    }

    public class SettleContext
    {
        public string Ymd { get; set; }
        public bool BypassPreCheck { get; set; }
        public IDbConnectionFactory MainDb { get; set; }
        public IDbConnectionFactory PaymentDb { get; set; }
        public IDbConnectionFactory SettleCardDb { get; set; }
        public IDbConnectionFactory PlCardDb { get; set; }
        public ICheckpointRepository Checkpoint { get; set; }
    }

    public class StepResult
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public string SourceProcName { get; set; }
        public string PoStrErrMsg { get; set; }
        public bool IsSuccess => Code == 0;
    }

    public interface IDbConnectionFactory { IDbConnection CreateConnection(); }
    public interface ICheckpointRepository 
    { 
        bool IsStepCompleted(string stepName, string ymd);
        void MarkStepCompleted(string stepName, string ymd);
    }
}";
    File.WriteAllText(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"), baseClassStub, Encoding.UTF8);
}
```

- [ ] **Step 2: Mention the template in Instructions**
In the same method, where `sb.AppendLine("## 🔑 4. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");` is populated, add an instruction about the base class:

```csharp
sb.AppendLine("9. [중요] 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.cs`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오.");
```

- [ ] **Step 3: Commit**
```bash
git add src/ReSet.Core/Services/MetadataExporter.cs
git commit -m "feat: Generate AbstractSettleTasklet.cs template during instruction export"
```

---

### Task 3: Refactor Self-Healing Feedback Injection

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs`
- Modify: `src/ReSet.Cli/Program.cs`

**Interfaces:**
- Consumes: `IMetadataExporter.AppendFeedbackToInstructionsAsync`

- [ ] **Step 1: Inject `IMetadataExporter` into `CodegenWorkflowOrchestrator`**
In `CodegenWorkflowOrchestrator.cs`, update the constructor to accept `IMetadataExporter metadataExporter` and save it to a readonly field `_metadataExporter`.

- [ ] **Step 2: Use `AppendFeedbackToInstructionsAsync`**
In `CodegenWorkflowOrchestrator.cs` line ~107, replace the `File.AppendAllTextAsync` logic with:
```csharp
if (File.Exists(instructionsFilePath))
{
    await _metadataExporter.AppendFeedbackToInstructionsAsync(instructionsFilePath, feedbackBuilder.ToString());
}
```

- [ ] **Step 3: Update `Program.cs` Instantiation**
In `src/ReSet.Cli/Program.cs` line ~1563, instantiate `MetadataExporter` and pass it to `CodegenWorkflowOrchestrator`:
```csharp
var metadataExporter = new ReSet.Core.Services.MetadataExporter();
var codegenWorkflowOrchestrator = new CodegenWorkflowOrchestrator(engine, codeVerificationOrchestrator, metadataExporter, maxL2Attempts);
```

- [ ] **Step 4: Build and Test**
Run `dotnet build` to ensure there are no compilation errors with the new constructor signatures.

- [ ] **Step 5: Commit**
```bash
git add src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs src/ReSet.Cli/Program.cs
git commit -m "refactor: Use MetadataExporter for clean feedback injection in self-healing loop"
```
