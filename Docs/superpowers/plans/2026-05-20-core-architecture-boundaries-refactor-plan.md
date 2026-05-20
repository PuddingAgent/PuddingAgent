# Core Architecture Boundaries Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gradually split Pudding's execution engine into clear contracts and focused services without rewriting the working runtime loop.

**Architecture:** Add Core contracts first, then introduce facade services around existing behavior. `AgentExecutionService` remains the orchestrator while context assembly, LLM invocation, tool invocation, sub-agent invocation, session output, and lifecycle recording move behind stable interfaces.

**Tech Stack:** .NET 10, MSTest, ASP.NET Core DI, EF Core/SQLite via existing Platform services, existing RuntimeActivity/Event/SubAgentRun infrastructure.

---

## File Structure

Create:

- `Source/PuddingCore/Runtime/ExecutionLifecycleContracts.cs`
- `Source/PuddingCore/Runtime/ContextAssemblyContracts.cs`
- `Source/PuddingCore/Runtime/LlmInvocationContracts.cs`
- `Source/PuddingCore/Runtime/ToolInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SessionOutputContracts.cs`
- `Source/PuddingCoreTests/Runtime/RuntimeContractTests.cs`
- `Source/PuddingPlatform/Services/RuntimeActivityExecutionLifecycleRecorder.cs`
- `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- `Source/PuddingRuntime/Services/LlmInvocationService.cs`
- `Source/PuddingRuntime/Services/ToolInvocationService.cs`
- `Source/PuddingRuntime/Services/SubAgentInvocationService.cs`
- `Source/PuddingPlatform/Services/SessionOutputWriter.cs`

Modify:

- `Source/PuddingAgent/Program.cs`
- `Source/PuddingRuntime/DependencyInjection.cs`
- `Source/PuddingRuntime/Services/AgentExecutionService.cs`
- `Source/PuddingRuntime/Services/ContextPipeline.cs` only if a minimal adapter hook is required
- `Source/PuddingPlatform/Services/SessionStateManager.cs` only if `SessionOutputWriter` needs a public adapter method
- `Source/PuddingCore/Observability/RuntimeActivity.cs` only if existing component/status constants need additions

Test:

- `Source/PuddingCoreTests/Runtime/RuntimeContractTests.cs`
- `Source/PuddingPlatformTests/Services/RuntimeActivityExecutionLifecycleRecorderTests.cs`
- Existing focused tests:
  - `Source/PuddingCoreTests/PuddingCoreTests.csproj`
  - `Source/PuddingPlatformTests/PuddingPlatformTests.csproj`
  - `Source/PuddingWebApiTests/PuddingWebApiTests.csproj`

---

## Task 1: Add Core Runtime Contracts

**Files:**
- Create: `Source/PuddingCore/Runtime/ExecutionLifecycleContracts.cs`
- Create: `Source/PuddingCore/Runtime/ContextAssemblyContracts.cs`
- Create: `Source/PuddingCore/Runtime/LlmInvocationContracts.cs`
- Create: `Source/PuddingCore/Runtime/ToolInvocationContracts.cs`
- Create: `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs`
- Create: `Source/PuddingCore/Runtime/SessionOutputContracts.cs`
- Test: `Source/PuddingCoreTests/Runtime/RuntimeContractTests.cs`

- [ ] **Step 1: Write contract model tests**

Create `RuntimeContractTests.cs` with assertions that records can be instantiated, required fields are enforced by constructors/object initializers at compile time, and default collection properties are non-null.

```csharp
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class RuntimeContractTests
{
    [TestMethod]
    public void ExecutionLifecycleRecord_Default_Metadata_Is_Not_Null()
    {
        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_1",
            TraceId = "trace_1",
            WorkspaceId = "default",
            SessionId = "session_1",
            AgentInstanceId = "agent_1",
            Component = "agent_execution",
            Operation = "execute",
            Status = "started",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        Assert.IsNotNull(record.Metadata);
        Assert.AreEqual(0, record.Metadata.Count);
    }

    [TestMethod]
    public void LlmInvocationRequest_Default_Tools_Is_Not_Null()
    {
        var request = new LlmInvocationRequest
        {
            WorkspaceId = "default",
            SessionId = "session_1",
            AgentInstanceId = "agent_1",
            AgentTemplateId = "general-assistant",
            ProfileId = "conscious.default",
            Messages = Array.Empty<ChatMessage>(),
        };

        Assert.IsNotNull(request.Tools);
        Assert.AreEqual(0, request.Tools.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter RuntimeContractTests --logger "console;verbosity=minimal"
```

Expected: compile fails because `PuddingCode.Runtime` contracts do not exist.

- [ ] **Step 3: Add contract files**

Create the contracts exactly as specified in ADR-024 sections 3.2 through 3.7. Use namespace:

```csharp
namespace PuddingCode.Runtime;
```

Use existing Core types:

```csharp
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
```

- [ ] **Step 4: Run contract tests**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter RuntimeContractTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingCore\Runtime Source\PuddingCoreTests\Runtime
git commit -m "feat: add runtime boundary contracts"
```

---

## Task 2: Add Execution Lifecycle Recorder

**Files:**
- Create: `Source/PuddingPlatform/Services/RuntimeActivityExecutionLifecycleRecorder.cs`
- Test: `Source/PuddingPlatformTests/Services/RuntimeActivityExecutionLifecycleRecorderTests.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Write recorder tests**

Test that `RecordInstantAsync` writes a `RuntimeActivity` with matching trace/session/component/status through the existing `IRuntimeActivitySink`.

Use a fake sink:

```csharp
private sealed class CapturingRuntimeActivitySink : IRuntimeActivitySink
{
    public List<RuntimeActivity> Records { get; } = new();

    public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
    {
        Records.Add(activity);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Records);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter RuntimeActivityExecutionLifecycleRecorderTests --logger "console;verbosity=minimal"
```

Expected: compile fails because recorder does not exist.

- [ ] **Step 3: Implement recorder**

Implement `IExecutionLifecycleRecorder` by mapping `ExecutionLifecycleRecord` to `RuntimeActivity`.

Mapping:

- `ExecutionLifecycleRecord.TraceId` -> `RuntimeActivity.TraceId`
- `SessionId` -> `RuntimeActivity.SessionId`
- `ExecutionId` -> `RuntimeActivity.ExecutionId`
- `Component` -> `RuntimeActivity.Component`
- `Operation` -> `RuntimeActivity.Operation`
- `Status` -> `RuntimeActivity.Status`
- `DurationMs` -> `RuntimeActivity.DurationMs`
- `Summary` -> `RuntimeActivity.Summary`
- `Error` -> `RuntimeActivity.Error`
- `Metadata` -> `RuntimeActivity.Metadata`

- [ ] **Step 4: Register DI**

In `Source/PuddingAgent/Program.cs`, register:

```csharp
builder.Services.AddSingleton<IExecutionLifecycleRecorder, RuntimeActivityExecutionLifecycleRecorder>();
```

- [ ] **Step 5: Run tests and build**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter RuntimeActivityExecutionLifecycleRecorderTests --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS and 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingPlatform\Services\RuntimeActivityExecutionLifecycleRecorder.cs Source\PuddingPlatformTests\Services\RuntimeActivityExecutionLifecycleRecorderTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: record execution lifecycle via runtime activity"
```

---

## Task 3: Add Context Assembly Facade

**Files:**
- Create: `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Add a facade test around existing ContextPipeline behavior**

If creating a direct Runtime test project is too large, add a focused test in the existing memory engine test area that constructs `ContextAssemblyService` with the same dependencies as `ContextPipeline` helpers. The assertion must verify `ContextAssemblyResult.Messages` is non-empty and `Layers` contains at least a final assembled context summary.

- [ ] **Step 2: Run the focused test to verify failure**

Run the selected test command and confirm compile failure because `ContextAssemblyService` does not exist.

- [ ] **Step 3: Implement `ContextAssemblyService`**

Wrap existing `ContextPipeline` calls. Do not move internal layer logic yet.

Output:

- `Messages`: existing assembled messages.
- `EstimatedTokens`: existing estimate if available, otherwise conservative length-based estimate.
- `Layers`: at minimum one `ContextLayerSummary` named `assembled`.
- `CompactionMode`: existing compaction mode if available.
- `MemoryRecallMode`: existing memory recall mode if available.

- [ ] **Step 4: Register DI**

In `Source/PuddingRuntime/DependencyInjection.cs`:

```csharp
services.AddSingleton<IContextAssemblyService, ContextAssemblyService>();
```

- [ ] **Step 5: Replace one call site in `AgentExecutionService`**

Replace only the non-streaming context assembly call first. Keep streaming path unchanged in this task.

- [ ] **Step 6: Run focused tests and build**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingRuntime\Services\ContextAssemblyService.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: route context assembly through facade"
```

---

## Task 4: Add LLM Invocation Facade

**Files:**
- Create: `Source/PuddingRuntime/Services/LlmInvocationService.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Write a fake-client LLM invocation test**

Use a fake `IRuntimeLlmClient` that returns a deterministic `LlmResponse`. Assert `LlmInvocationService.InvokeAsync` returns `Success=true`, `ReplyText`, and `Usage`.

- [ ] **Step 2: Run test to verify failure**

Expected: compile fails because `LlmInvocationService` does not exist.

- [ ] **Step 3: Implement `LlmInvocationService`**

Responsibilities:

- Resolve profile metadata already supplied by caller.
- Call existing `IRuntimeLlmClient.ChatAsync`.
- Convert success and failure to `LlmInvocationResult`.
- Record lifecycle through `IExecutionLifecycleRecorder`.

- [ ] **Step 4: Register DI**

```csharp
services.AddSingleton<ILlmInvocationService, LlmInvocationService>();
```

- [ ] **Step 5: Migrate non-streaming LLM call path**

In `AgentExecutionService`, replace the first non-streaming direct `_llmClient.ChatAsync` call with `ILlmInvocationService.InvokeAsync`.

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS and 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingRuntime\Services\LlmInvocationService.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: route llm calls through invocation service"
```

---

## Task 5: Add Tool Invocation Facade

**Files:**
- Create: `Source/PuddingRuntime/Services/ToolInvocationService.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Write tool invocation tests**

Tests:

- Successful tool call returns `Success=true`, `DurationMs >= 0`, `ArgsHash` not empty.
- Failed tool call returns `Success=false`, `Error` not empty.
- Permission denied returns `Success=false` and records a lifecycle failure.

- [ ] **Step 2: Run tests to verify failure**

Expected: compile fails because `ToolInvocationService` does not exist.

- [ ] **Step 3: Implement service as adapter**

Initially call the same tool execution code currently used by `AgentExecutionService`. Keep behavior unchanged.

Add:

- SHA256 hash of `ArgumentsJson`.
- duration measurement.
- output length.
- lifecycle record.

- [ ] **Step 4: Migrate one tool call path**

Replace one repeated tool execution block in `AgentExecutionService` with `IToolInvocationService.InvokeAsync`. Leave the second path for a later commit if it is structurally different.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingRuntime\Services\ToolInvocationService.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: route tool calls through invocation service"
```

---

## Task 6: Add Sub-Agent Invocation Facade

**Files:**
- Create: `Source/PuddingRuntime/Services/SubAgentInvocationService.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Write sub-agent invocation tests**

Use a fake `ISubAgentManager` and assert:

- async invocation returns `Status=running` or spawned status with `SubSessionId`.
- sync invocation returns completed/failed result as provided by fake manager.
- trace metadata is passed through.

- [ ] **Step 2: Run test to verify failure**

Expected: compile fails because `SubAgentInvocationService` does not exist.

- [ ] **Step 3: Implement service**

Wrap `ISubAgentManager` calls. Do not change `SubAgentManager` behavior.

- [ ] **Step 4: Register DI**

```csharp
services.AddSingleton<ISubAgentInvocationService, SubAgentInvocationService>();
```

- [ ] **Step 5: Replace direct sub-agent manager usage in execution path**

`AgentExecutionService` should no longer directly encode sub-agent invocation semantics.

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS and 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingRuntime\Services\SubAgentInvocationService.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: route subagent calls through invocation service"
```

---

## Task 7: Add Session Output Writer

**Files:**
- Create: `Source/PuddingPlatform/Services/SessionOutputWriter.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Write adapter test**

Use a fake `ISessionStateManager` and assert `SessionOutputWriter.WriteFrameAsync` calls `AppendAsync` with the supplied session/workspace/frame/trace.

- [ ] **Step 2: Run test to verify failure**

Expected: compile fails because `SessionOutputWriter` does not exist.

- [ ] **Step 3: Implement writer**

Implementation is a thin adapter over `ISessionStateManager.AppendAsync`.

- [ ] **Step 4: Register DI**

```csharp
builder.Services.AddSingleton<ISessionOutputWriter, SessionOutputWriter>();
```

- [ ] **Step 5: Replace selected direct SSM writes**

Replace simple direct writes in `AgentExecutionService` first. Do not migrate complex stream flow in this task.

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingPlatform\Services\SessionOutputWriter.cs Source\PuddingAgent\Program.cs Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: route session frames through output writer"
```

---

## Task 8: Reduce AgentExecutionService Responsibility

**Files:**
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`
- Test: existing focused tests and build

- [ ] **Step 1: Identify direct responsibilities left in AgentExecutionService**

Use:

```powershell
Select-String -Path Source\PuddingRuntime\Services\AgentExecutionService.cs -Pattern "_llmClient|_contextPipeline|_ssm|_subAgentManager|RecordActivityAsync|ExecuteTool|ToolRunner"
```

Expected: direct usage remains only where migration is intentionally deferred.

- [ ] **Step 2: Move repeated lifecycle recording to recorder**

Replace duplicated `RecordActivityAsync` blocks where semantics match `IExecutionLifecycleRecorder`.

- [ ] **Step 3: Move remaining duplicated tool path**

If two tool loops exist, migrate the second loop to `IToolInvocationService`.

- [ ] **Step 4: Move streaming LLM path**

Route stream invocation through `ILlmInvocationService.InvokeStreamAsync` after non-streaming path is stable.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS and 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingRuntime\Services\AgentExecutionService.cs
git commit -m "refactor: slim agent execution orchestration"
```

---

## Task 9: Connect to ADR-023 Timeline

**Files:**
- Modify: `Source/PuddingCore/Observability/RuntimeActivity.cs`
- Modify: `Source/PuddingPlatform/Services/RuntimeActivitySink.cs`
- Modify: ADR-023 implementation files when they exist

- [ ] **Step 1: Ensure lifecycle metadata is timeline-safe**

Metadata keys must be stable:

```text
profile_id
provider_id
model_id
context_tokens_estimated
context_layer_count
tool_name
tool_args_hash
subagent_run_id
```

- [ ] **Step 2: Add tests for metadata projection**

Timeline tests should assert metadata survives through `RuntimeActivitySink`.

- [ ] **Step 3: Verify timeline compatibility**

Run ADR-023 diagnostics timeline tests once implemented.

- [ ] **Step 4: Commit**

```powershell
git add Source\PuddingCore\Observability Source\PuddingPlatform\Services
git commit -m "feat: expose execution lifecycle metadata for timeline"
```

---

## Task 10: QA and Documentation

**Files:**
- Create: `Docs/QA/QA-2026-05-20-Core-Architecture-Boundaries.md`
- Modify: `Docs/Tasks.md`
- Modify: `Docs/07架构/24核心架构组件边界与执行引擎拆分ADR.md`

- [ ] **Step 1: Write QA report**

Include:

- completed tasks
- changed files
- test commands
- pass/fail result
- residual risks
- next recommended phase

- [ ] **Step 2: Update task board**

Add status rows for:

- `ARCH-CORE-001` contracts
- `ARCH-CORE-002` lifecycle recorder
- `ARCH-CORE-003` context facade
- `ARCH-CORE-004` LLM invocation facade
- `ARCH-CORE-005` tool invocation facade
- `ARCH-CORE-006` sub-agent invocation facade
- `ARCH-CORE-007` session output writer
- `ARCH-CORE-008` execution service slimming

- [ ] **Step 3: Final verification**

Run:

```powershell
dotnet build PuddingAgentNetwork.slnx --no-restore --nologo
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```powershell
git add Docs\QA Docs\Tasks.md Docs\07架构\24核心架构组件边界与执行引擎拆分ADR.md
git commit -m "docs: record core architecture boundary refactor QA"
```

