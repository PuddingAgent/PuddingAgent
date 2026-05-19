# Architecture Foundation Hardening Roadmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the runtime infrastructure delivered by ADR-019 and ADR-021 so that sub-agent run archives, event schemas, diagnostics APIs, permissions, and E2E tests are stable enough for Admin observability UI work.

**Architecture:** Keep file-backed configuration and file-backed run archives as the user-visible source of truth, with SQLite used as query indexes. Establish explicit contracts for JSON/JSONL serialization, sub-agent run terminal ownership, event schema scopes, diagnostic DTOs, workspace guards, and layered verification.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core SQLite, MSTest, System.Text.Json, React/TypeScript Admin UI, Docker, Playwright or Python browser automation.

---

## File Map

### Core Contracts

- Create: `Source/PuddingCore/Serialization/PuddingJsonContracts.cs`
  - Shared serializer options for pretty JSON and JSONL.
- Modify: `Source/PuddingCore/Abstractions/ISubAgentRunStore.cs`
  - Make terminal completion return an idempotency result.
- Modify: `Source/PuddingCore/SubAgents/SubAgentRunModels.cs`
  - Add terminal write result and DTO-safe archive models.
- Modify: `Source/PuddingCore/Events/EventSchemaRegistry.cs`
  - Add schema scope and scoped key uniqueness.
- Create: `Source/PuddingCore/Agents/IAgentWorkspaceGuard.cs`
  - Permission decision contract for file/tool execution.
- Create: `Source/PuddingCore/Agents/AgentWorkspaceGuardModels.cs`
  - Guard decision and normalized permission rules.

### Platform Services

- Modify: `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs`
  - Use JSONL serializer, terminal idempotency, recoverable JSONL parsing.
- Modify: `Source/PuddingPlatform/Services/SubAgentManager.cs`
  - Remove terminal write ownership for detailed run completion.
- Modify: `Source/PuddingPlatform/Controllers/Api/SubAgentRunController.cs`
  - Return stable DTOs instead of EF entities or raw archive internals.
- Modify: `Source/PuddingPlatform/Controllers/Api/EventDiagnosticsController.cs`
  - Add schema scope/category filters and DTO versioning.
- Modify: `Source/PuddingPlatform/Services/SessionStateManager.cs`
  - Normalize trace-report token usage parsing.

### Runtime Tooling

- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`
  - Own sub-agent terminal run completion.
- Modify: `Source/PuddingCore/Tools/FileTool.cs`
  - Apply workspace guard before read/write/list.
- Modify: `Source/PuddingCore/Tools/ShellTool.cs`
  - Apply workspace guard before command execution where applicable.
- Modify: `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs`
  - Apply tool allow/deny decisions when spawning child agents.

### Tests

- Create: `Source/PuddingCoreTests/Serialization/PuddingJsonContractsTests.cs`
- Create: `Source/PuddingCoreTests/Events/EventSchemaRegistryScopeTests.cs`
- Create: `Source/PuddingCoreTests/Agents/AgentWorkspaceGuardTests.cs`
- Create: `Source/PuddingPlatformTests/SubAgents/FileSubAgentRunStoreTests.cs` or place in existing compatible test project if no Platform test project exists.
- Modify: `Source/PuddingCoreTests/SubAgents/SubAgentRunModelsTests.cs`
- Modify: `Source/PuddingWebApiTests/FakeLlmControllerTests.cs`

### Docs

- Modify: `Docs/07架构/22架构基础设施硬化与行动路线ADR.md`
- Modify: `Docs/Tasks.md`
- Create: `Docs/QA/QA-2026-05-19-Architecture-Hardening-Plan.md`

---

## Architecture Contracts

### Contract 1: JSON vs JSONL

```csharp
namespace PuddingCode.Serialization;

public static class PuddingJsonContracts
{
    public static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly JsonSerializerOptions JsonLines = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

Rules:

- `.json` files may be indented.
- `.jsonl` files must use `JsonLines`.
- JSONL reader must return partial results plus parse errors.

### Contract 2: Sub-Agent Terminal Ownership

```csharp
public enum SubAgentRunTerminalWriteResult
{
    Applied,
    AlreadyTerminal,
    NotFound
}
```

```csharp
public interface ISubAgentRunStore
{
    Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default);
    Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default);
    Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default);
    Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default);
    Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default);
}
```

Rules:

- `AgentExecutionService` owns detailed terminal completion.
- `SubAgentManager` owns parent session summary and SSM status.
- Terminal states are `completed`, `failed`, `cancelled`.
- Terminal writes are idempotent and do not overwrite existing terminal metrics.

### Contract 3: Event Schema Scope

```csharp
public enum EventSchemaScope
{
    Internal,
    SessionFrame
}

public sealed record EventSchemaDefinition(
    string EventType,
    int CurrentVersion,
    EventSchemaScope Scope,
    string Category,
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string>? OptionalFields = null);
```

Rules:

- `Internal/subagent.run.completed` is different from `SessionFrame/subagent.completed`.
- Duplicate `(Scope, EventType)` is invalid.
- Event diagnostics APIs expose scope and category.

### Contract 4: Diagnostic DTOs

```csharp
public sealed record SubAgentRunSummaryDto
{
    public required string RunId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long TotalDurationMs { get; init; }
    public int TotalRounds { get; init; }
    public int TotalToolCalls { get; init; }
    public string? ErrorMessage { get; init; }
}
```

Rules:

- Controllers do not return EF entities.
- Payload previews are capped and sanitized.
- API dates use ISO-8601 strings or `DateTimeOffset`.

### Contract 5: Workspace Guard

```csharp
public interface IAgentWorkspaceGuard
{
    WorkspaceGuardDecision CanRead(string agentInstanceId, string workspaceId, string path);
    WorkspaceGuardDecision CanWrite(string agentInstanceId, string workspaceId, string path);
    WorkspaceGuardDecision CanExecuteTool(string agentInstanceId, string workspaceId, string toolId);
}

public sealed record WorkspaceGuardDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? MatchedRule { get; init; }
}
```

Rules:

- Default deny: `../**`, `data/config/**`, `data/databases/**`.
- Default allow write only under the agent workspace.
- Denied actions emit RuntimeActivity and session diagnostic event.

---

## Tasks

### Task 1: JSON/JSONL Contract Hardening

**Files:**
- Create: `Source/PuddingCore/Serialization/PuddingJsonContracts.cs`
- Modify: `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs`
- Test: `Source/PuddingCoreTests/Serialization/PuddingJsonContractsTests.cs`

- [ ] **Step 1: Add failing JSONL test**

Create a test proving `JsonLines` writes one physical line:

```csharp
[TestMethod]
public void JsonLines_Does_Not_Write_Indented_Multiline_Json()
{
    var payload = new { eventType = "subagent.run.started", payload = new { value = "x" } };

    var json = JsonSerializer.Serialize(payload, PuddingJsonContracts.JsonLines);

    Assert.IsFalse(json.Contains(Environment.NewLine));
    Assert.IsFalse(json.Contains("\n"));
}
```

- [ ] **Step 2: Implement `PuddingJsonContracts`**

Use the contract above.

- [ ] **Step 3: Update `FileSubAgentRunStore`**

Use:

```csharp
JsonSerializer.Serialize(payload, PuddingJsonContracts.JsonLines)
```

for `events.jsonl`, `tools.jsonl`, and `errors.jsonl`.

- [ ] **Step 4: Add archive roundtrip test**

Test sequence:

```text
CreateRunAsync -> AppendEventAsync -> AppendToolAuditAsync -> CompleteRunAsync -> GetRunArchiveAsync
```

Expected:

- archive is not null.
- events count is 1.
- tools count is 1.
- output equals expected.
- `events.jsonl` has exactly one line per appended event.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "PuddingJsonContractsTests|SubAgentRunModelsTests" --logger "console;verbosity=minimal"
```

Expected: PASS.

### Task 2: Single Terminal Writer for Sub-Agent Runs

**Files:**
- Modify: `Source/PuddingCore/Abstractions/ISubAgentRunStore.cs`
- Modify: `Source/PuddingCore/SubAgents/SubAgentRunModels.cs`
- Modify: `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs`
- Modify: `Source/PuddingPlatform/Services/SubAgentManager.cs`
- Modify: `Source/PuddingRuntime/Services/AgentExecutionService.cs`

- [ ] **Step 1: Add terminal result enum**

Add `SubAgentRunTerminalWriteResult`.

- [ ] **Step 2: Change `CompleteRunAsync` signature**

Return `Task<SubAgentRunTerminalWriteResult>`.

- [ ] **Step 3: Make terminal writes idempotent**

If `run.json.Status` is `completed`, `failed`, or `cancelled`, return `AlreadyTerminal` and do not overwrite metrics.

- [ ] **Step 4: Remove duplicate detailed completion from `SubAgentManager`**

`SubAgentManager` should not overwrite run metrics after `DispatchChildAgentAsync` returns. It may write SSM status and parent session SSE only.

- [ ] **Step 5: Keep AgentExecutionService as detailed completion owner**

`AgentExecutionService` writes:

- status
- output
- error
- total rounds
- total tool calls
- total duration

- [ ] **Step 6: Verify with focused test**

Add test:

```text
CompleteRunAsync(first completion with rounds=3) -> Applied
CompleteRunAsync(second completion with rounds=0) -> AlreadyTerminal
GetRunArchiveAsync -> still rounds=3
```

### Task 3: Event Schema Scope

**Files:**
- Modify: `Source/PuddingCore/Events/EventSchemaRegistry.cs`
- Test: `Source/PuddingCoreTests/Events/EventSchemaRegistryScopeTests.cs`
- Modify: `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/EventDiagnosticsController.cs`

- [ ] **Step 1: Add scope enum and scoped schema definition**

Use `EventSchemaScope`.

- [ ] **Step 2: Register internal and session schemas separately**

Examples:

```csharp
new EventSchemaDefinition("subagent.run.completed", 1, EventSchemaScope.Internal, ...)
new EventSchemaDefinition("subagent.completed", 1, EventSchemaScope.SessionFrame, ...)
```

- [ ] **Step 3: Add duplicate scoped key test**

Expected:

- duplicate same scope fails.
- same event type in different scopes is allowed only when intentionally registered through scoped key.

- [ ] **Step 4: Update dispatcher compatibility check**

Dispatcher uses `EventSchemaScope.Internal`.

### Task 4: Diagnostic DTO Stabilization

**Files:**
- Create: `Source/PuddingPlatform/Controllers/Api/DiagnosticsDtos.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/SubAgentRunController.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/EventDiagnosticsController.cs`
- Test: `Source/PuddingWebApiTests/SubAgentRunControllerTests.cs`

- [ ] **Step 1: Define DTOs**

Add:

- `SubAgentRunSummaryDto`
- `SubAgentRunDetailDto`
- `SubAgentRunEventDto`
- `SubAgentRunToolAuditDto`
- `PagedResultDto<T>`

- [ ] **Step 2: Map EF entity to DTO**

No API returns `SubAgentRunEntity` directly.

- [ ] **Step 3: Add pagination validation**

Rules:

- `limit` 1-500.
- `offset >= 0`.

- [ ] **Step 4: Add API tests**

Tests:

- list returns DTO fields.
- invalid limit returns 400.
- missing run returns 404.

### Task 5: Workspace Guard

**Files:**
- Create: `Source/PuddingCore/Agents/IAgentWorkspaceGuard.cs`
- Create: `Source/PuddingCore/Agents/AgentWorkspaceGuard.cs`
- Test: `Source/PuddingCoreTests/Agents/AgentWorkspaceGuardTests.cs`
- Modify: `Source/PuddingCore/Tools/FileTool.cs`
- Modify: `Source/PuddingCore/Tools/ShellTool.cs`

- [ ] **Step 1: Define guard decision model**

Use `WorkspaceGuardDecision`.

- [ ] **Step 2: Implement path normalization**

Rules:

- resolve to full path.
- reject traversal outside workspace.
- reject config/database paths.

- [ ] **Step 3: Add tests**

Required tests:

- write under workspace allowed.
- write `data/config/system.json` denied.
- path with `..` denied.
- tool deny rule blocks `shell.execute`.

- [ ] **Step 4: Wire FileTool**

Before read/write/list, call guard with current agent/workspace context.

- [ ] **Step 5: Wire ShellTool**

Before execution, verify shell tool is allowed and working directory is inside permitted workspace.

### Task 6: Trace Report and Token Usage Compatibility

**Files:**
- Modify: `Source/PuddingPlatform/Services/SessionStateManager.cs`
- Test: appropriate Platform or Web API test project.

- [ ] **Step 1: Add parser helper**

Support:

- `promptTokens`
- `completionTokens`
- `totalTokens`
- `inputTokens`
- `outputTokens`
- `PromptTokens`
- `CompletionTokens`
- `TotalTokens`

- [ ] **Step 2: Add unit test**

Given usage payload in different naming styles, trace-report total tokens is correct.

### Task 7: E2E Baseline Preparation

**Files:**
- Create: `Tests/e2e/README.md`
- Create: `Tests/e2e/healthcheck.ps1`
- Create: `Tests/e2e/chat-smoke.spec.ts` or `Tests/e2e/chat_smoke.py`
- Modify: `build-and-up.ps1`

- [ ] **Step 1: Fix test output isolation**

Use a unique test results/output directory so `PuddingWebApiTests` does not collide with running dotnet processes.

- [ ] **Step 2: Add healthcheck**

Check:

- app responds.
- Fake LLM responds.
- `/api/diagnostics/events/stats` responds.

- [ ] **Step 3: Add browser smoke**

Flow:

- open Admin.
- login.
- create/open session.
- send message.
- assert assistant response arrives.
- assert trace id or session event exists.

### Task 8: Documentation and QA

**Files:**
- Modify: `Docs/Tasks.md`
- Create: `Docs/QA/QA-2026-05-19-Architecture-Hardening-Plan.md`

- [ ] **Step 1: Add task IDs**

Add:

- `ARCH-HARDEN-001` JSONL contract
- `ARCH-HARDEN-002` single terminal writer
- `ARCH-HARDEN-003` event schema scope
- `ARCH-HARDEN-004` diagnostic DTOs
- `ARCH-HARDEN-005` workspace guard
- `ARCH-HARDEN-006` trace usage compatibility
- `ARCH-HARDEN-007` E2E baseline

- [ ] **Step 2: Add QA report**

Record verification commands and residual risks.

---

## Verification Commands

Run after each phase:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "SubAgent|EventSchema|WorkspaceGuard|PuddingJsonContracts" --logger "console;verbosity=minimal"
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter "SubAgentRun|FakeLlm" --logger "console;verbosity=minimal"
```

Expected:

- build: 0 errors.
- focused core tests: pass.
- web API tests: pass or documented file-lock issue with isolated output follow-up.

---

## Execution Order

Recommended order:

1. Task 1 JSON/JSONL contract.
2. Task 2 single terminal writer.
3. Task 3 event schema scope.
4. Task 4 diagnostic DTOs.
5. Task 6 trace usage compatibility.
6. Task 5 workspace guard.
7. Task 7 E2E baseline.
8. Task 8 docs and QA.

This order keeps data correctness before UI and permissions, and keeps API contracts stable before Admin implementation.

