# Runtime State Observability Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first-stage runtime state and observability foundation for session events, agent execution, internal events, and sub-agent lifecycle.

**Architecture:** Add low-level trace/activity contracts in `PuddingCore`, persist observable activity in `PuddingPlatform`, and integrate the contracts through SSM, Chat API, event dispatch, and sub-agent manager. Keep the current in-memory event queue for this phase, but align metadata and logging with the future persistent queue.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core, SQLite, MSTest, existing `PuddingCore` / `PuddingPlatform` / `PuddingRuntime` / `PuddingAgent` projects.

---

## File Structure

- Create: `Source/PuddingCore/Observability/RuntimeTraceContext.cs`
  Defines trace/correlation identity and helper methods.
- Create: `Source/PuddingCore/Observability/RuntimeActivity.cs`
  Defines activity status, component names, activity DTO, filter DTO, and sink interface.
- Create: `Source/PuddingRuntime/Services/Observability/AmbientRuntimeTraceAccessor.cs`
  Provides AsyncLocal trace propagation for runtime code.
- Create: `Source/PuddingPlatform/Data/Entities/RuntimeActivityEntity.cs`
  Persists runtime activity for diagnostics.
- Create: `Source/PuddingPlatform/Services/RuntimeActivitySink.cs`
  Writes activity rows and structured logs.
- Create: `Source/PuddingPlatform/Controllers/Api/RuntimeDiagnosticsController.cs`
  Exposes authenticated runtime activity query API.
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
  Adds `RuntimeActivities` DbSet and mapping.
- Modify: `Source/PuddingPlatform/Data/Entities/SessionEventLogEntity.cs`
  Adds nullable trace metadata columns.
- Modify: `Source/PuddingPlatform/Services/SessionStateManager.cs`
  Records trace metadata on session events and emits runtime activity.
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
  Routes streamed frames into `ISessionStateManager` instead of `SessionEventHub`.
- Modify: `Source/PuddingRuntime/Services/Events/InternalEventBus.cs`
  Emits publish and handler runtime activities.
- Modify: `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
  Preserves trace metadata in queued events and emits enqueue activities.
- Modify: `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
  Emits dequeue/dispatch/handler result activities.
- Modify: `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs`
  Delegates async sub-agent lifecycle to `ISubAgentManager`.
- Modify: `Source/PuddingPlatform/Services/SubAgentManager.cs`
  Emits sub-agent lifecycle activities and guards terminal state idempotency.
- Modify: `Source/PuddingAgent/Program.cs`
  Registers observability services, removes destructive DDL, and adds non-destructive schema creation.
- Test: `Source/PuddingCoreTests/Observability/RuntimeTraceContextTests.cs`
  Verifies trace creation and child trace derivation.
- Test: `Source/PuddingWebApiTests/RuntimeDiagnosticsControllerTests.cs`
  Verifies diagnostics API requires auth and returns bounded results where feasible.

---

### Task 1: Core Trace And Activity Contracts

**Files:**
- Create: `Source/PuddingCore/Observability/RuntimeTraceContext.cs`
- Create: `Source/PuddingCore/Observability/RuntimeActivity.cs`
- Create: `Source/PuddingRuntime/Services/Observability/AmbientRuntimeTraceAccessor.cs`
- Test: `Source/PuddingCoreTests/Observability/RuntimeTraceContextTests.cs`

- [ ] **Step 1: Write the trace context test**

```csharp
using PuddingCode.Observability;

namespace PuddingCoreTests.Observability;

[TestClass]
public sealed class RuntimeTraceContextTests
{
    [TestMethod]
    public void CreateNew_generates_trace_and_correlation_ids()
    {
        var ctx = RuntimeTraceContext.CreateNew(
            sessionId: "s1",
            workspaceId: "w1",
            userId: "u1");

        Assert.IsFalse(string.IsNullOrWhiteSpace(ctx.TraceId));
        Assert.AreEqual(ctx.TraceId, ctx.CorrelationId);
        Assert.AreEqual("s1", ctx.SessionId);
        Assert.AreEqual("w1", ctx.WorkspaceId);
        Assert.AreEqual("u1", ctx.UserId);
    }

    [TestMethod]
    public void CreateChildExecution_preserves_trace_and_sets_parent()
    {
        var parent = RuntimeTraceContext.CreateNew(
            sessionId: "parent-session",
            workspaceId: "w1",
            executionId: "exec-parent");

        var child = parent.CreateChildExecution(
            sessionId: "child-session",
            executionId: "exec-child",
            subAgentId: "sub-1");

        Assert.AreEqual(parent.TraceId, child.TraceId);
        Assert.AreEqual(parent.CorrelationId, child.CorrelationId);
        Assert.AreEqual("exec-parent", child.ParentExecutionId);
        Assert.AreEqual("exec-child", child.ExecutionId);
        Assert.AreEqual("sub-1", child.SubAgentId);
    }
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter RuntimeTraceContextTests --logger "console;verbosity=minimal"`

Expected: FAIL because `PuddingCode.Observability` does not exist.

- [ ] **Step 3: Add core observability contracts**

Create `RuntimeTraceContext`:

```csharp
namespace PuddingCode.Observability;

public sealed record RuntimeTraceContext
{
    public required string TraceId { get; init; }
    public required string CorrelationId { get; init; }
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ExecutionId { get; init; }
    public string? ParentExecutionId { get; init; }
    public string? SubAgentId { get; init; }
    public string? EventId { get; init; }
    public string? ConnectorId { get; init; }
    public string? UserId { get; init; }

    public static RuntimeTraceContext CreateNew(
        string? sessionId = null,
        string? workspaceId = null,
        string? executionId = null,
        string? eventId = null,
        string? connectorId = null,
        string? userId = null,
        string? correlationId = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        return new RuntimeTraceContext
        {
            TraceId = traceId,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? traceId : correlationId,
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            ExecutionId = executionId,
            EventId = eventId,
            ConnectorId = connectorId,
            UserId = userId,
        };
    }

    public RuntimeTraceContext WithSession(string? sessionId, string? workspaceId = null) =>
        this with { SessionId = sessionId ?? SessionId, WorkspaceId = workspaceId ?? WorkspaceId };

    public RuntimeTraceContext WithEvent(string? eventId) =>
        this with { EventId = eventId ?? EventId };

    public RuntimeTraceContext CreateChildExecution(
        string? sessionId,
        string executionId,
        string? subAgentId = null) =>
        this with
        {
            SessionId = sessionId ?? SessionId,
            ExecutionId = executionId,
            ParentExecutionId = ExecutionId,
            SubAgentId = subAgentId ?? SubAgentId,
        };
}
```

Create `RuntimeActivity` contracts:

```csharp
namespace PuddingCode.Observability;

public static class RuntimeActivityComponents
{
    public const string Connector = "connector";
    public const string EventQueue = "event_queue";
    public const string EventDispatcher = "event_dispatcher";
    public const string SessionState = "session_state";
    public const string AgentExecution = "agent_execution";
    public const string ContextPipeline = "context_pipeline";
    public const string LlmGateway = "llm_gateway";
    public const string ToolRunner = "tool_runner";
    public const string SubAgent = "sub_agent";
    public const string Memory = "memory";
}

public static class RuntimeActivityStatuses
{
    public const string Started = "started";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Deferred = "deferred";
    public const string Retried = "retried";
}

public sealed record RuntimeActivity
{
    public string ActivityId { get; init; } = Guid.NewGuid().ToString("N");
    public required RuntimeTraceContext Trace { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string Severity { get; init; } = "info";
    public string? Summary { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record RuntimeActivityQuery
{
    public string? TraceId { get; init; }
    public string? SessionId { get; init; }
    public string? ExecutionId { get; init; }
    public string? Component { get; init; }
    public int Limit { get; init; } = 100;
}

public interface IRuntimeTraceAccessor
{
    RuntimeTraceContext? Current { get; set; }
}

public interface IRuntimeActivitySink
{
    Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default);
    Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default);
}
```

Create AsyncLocal accessor:

```csharp
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Observability;

public sealed class AmbientRuntimeTraceAccessor : IRuntimeTraceAccessor
{
    private static readonly AsyncLocal<RuntimeTraceContext?> CurrentHolder = new();

    public RuntimeTraceContext? Current
    {
        get => CurrentHolder.Value;
        set => CurrentHolder.Value = value;
    }
}
```

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter RuntimeTraceContextTests --logger "console;verbosity=minimal"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingCore\Observability Source\PuddingRuntime\Services\Observability Source\PuddingCoreTests\Observability
git commit -m "feat: add runtime trace contracts"
```

---

### Task 2: Runtime Activity Persistence And Diagnostics API

**Files:**
- Create: `Source/PuddingPlatform/Data/Entities/RuntimeActivityEntity.cs`
- Create: `Source/PuddingPlatform/Services/RuntimeActivitySink.cs`
- Create: `Source/PuddingPlatform/Controllers/Api/RuntimeDiagnosticsController.cs`
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Add the entity and DbContext mapping**

Create `RuntimeActivityEntity` with nullable trace fields and bounded summary metadata:

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("runtime_activity")]
public sealed class RuntimeActivityEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("activity_id")]
    public string ActivityId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("trace_id")]
    public string TraceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [MaxLength(64), Column("workspace_id")]
    public string? WorkspaceId { get; set; }

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("parent_execution_id")]
    public string? ParentExecutionId { get; set; }

    [MaxLength(64), Column("sub_agent_id")]
    public string? SubAgentId { get; set; }

    [MaxLength(64), Column("event_id")]
    public string? EventId { get; set; }

    [MaxLength(64), Column("connector_id")]
    public string? ConnectorId { get; set; }

    [MaxLength(128), Column("user_id")]
    public string? UserId { get; set; }

    [Required, MaxLength(64), Column("component")]
    public string Component { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("operation")]
    public string Operation { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = string.Empty;

    [Required, MaxLength(40), Column("started_at_utc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [MaxLength(40), Column("ended_at_utc")]
    public string? EndedAtUtc { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Required, MaxLength(16), Column("severity")]
    public string Severity { get; set; } = "info";

    [MaxLength(512), Column("summary")]
    public string? Summary { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    [MaxLength(128), Column("error_code")]
    public string? ErrorCode { get; set; }

    [MaxLength(512), Column("error_message")]
    public string? ErrorMessage { get; set; }
}
```

Add `DbSet<RuntimeActivityEntity> RuntimeActivities` and indexes on `TraceId`, `SessionId`, `ExecutionId`, `Component`, `StartedAtUtc`.

- [ ] **Step 2: Implement `RuntimeActivitySink`**

The sink maps DTOs to entity rows, logs a structured line, and never throws to callers. `QueryAsync` clamps `Limit` to 1-500 and returns newest rows descending.

- [ ] **Step 3: Add authenticated diagnostics controller**

Create `GET /api/runtime/activities` with filters `traceId`, `sessionId`, `executionId`, `component`, `limit`. Keep `[Authorize]`.

- [ ] **Step 4: Register services and schema**

In `Program.cs`:

```csharp
builder.Services.AddSingleton<IRuntimeTraceAccessor, AmbientRuntimeTraceAccessor>();
builder.Services.AddSingleton<RuntimeActivitySink>();
builder.Services.AddSingleton<IRuntimeActivitySink>(sp => sp.GetRequiredService<RuntimeActivitySink>());
```

Add non-destructive `CREATE TABLE IF NOT EXISTS runtime_activity (...)` and indexes to startup DDL.

- [ ] **Step 5: Build**

Run: `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`

Expected: build succeeds or only fails on pre-existing external dependency issues that must be documented.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingCore\Observability Source\PuddingRuntime\Services\Observability Source\PuddingPlatform\Data Source\PuddingPlatform\Services\RuntimeActivitySink.cs Source\PuddingPlatform\Controllers\Api\RuntimeDiagnosticsController.cs Source\PuddingAgent\Program.cs
git commit -m "feat: persist runtime activity diagnostics"
```

---

### Task 3: Startup Schema Safety

**Files:**
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingCoreTests/Observability/StartupSchemaSafetyTests.cs`

- [ ] **Step 1: Write a source guard test**

The test reads `Source/PuddingAgent/Program.cs` from the repo root and asserts it does not contain destructive DDL for `session_event_log` or `session_sub_agents`.

- [ ] **Step 2: Remove destructive DDL**

Delete these entries from `pendingTableDdl`:

```csharp
@"DROP TABLE IF EXISTS session_event_log;",
@"DROP TABLE IF EXISTS session_sub_agents;",
```

Keep `CREATE TABLE IF NOT EXISTS` and indexes.

- [ ] **Step 3: Add safe ALTER statements for trace columns**

Add idempotent `ALTER TABLE session_event_log ADD COLUMN ...` statements for:

- `trace_id`
- `correlation_id`
- `execution_id`
- `parent_execution_id`
- `sub_agent_id`
- `component`
- `operation`

Handle duplicate column exceptions the same way existing column DDL does.

- [ ] **Step 4: Run guard test**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter StartupSchemaSafetyTests --logger "console;verbosity=minimal"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingAgent\Program.cs Source\PuddingCoreTests\Observability\StartupSchemaSafetyTests.cs
git commit -m "fix: preserve session state tables on startup"
```

---

### Task 4: Session State Trace Metadata

**Files:**
- Modify: `Source/PuddingPlatform/Data/Entities/SessionEventLogEntity.cs`
- Modify: `Source/PuddingPlatform/Services/SessionStateManager.cs`
- Modify: `Source/PuddingCore/Abstractions/ISessionStateManager.cs`

- [ ] **Step 1: Add nullable entity fields**

Add nullable columns matching Task 3 trace metadata to `SessionEventLogEntity`.

- [ ] **Step 2: Add optional append metadata**

Add optional `RuntimeTraceContext? trace = null`, `string? component = null`, `string? operation = null` parameters to `AppendAsync`. Implementation should use explicit trace first, then ambient `IRuntimeTraceAccessor.Current`.

- [ ] **Step 3: Record activity on append**

Call `IRuntimeActivitySink.RecordAsync` with component `session_state`, operation `append:{frame.Event}`, status `succeeded`, and metadata `{ sequence, eventType }`.

- [ ] **Step 4: Preserve compatibility**

Update all compile errors from the interface signature change. Callers can omit new parameters.

- [ ] **Step 5: Build**

Run: `dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingCore\Abstractions\ISessionStateManager.cs Source\PuddingPlatform\Data\Entities\SessionEventLogEntity.cs Source\PuddingPlatform\Services\SessionStateManager.cs
git commit -m "feat: attach trace metadata to session events"
```

---

### Task 5: Chat API Uses SSM As Event Sink

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`

- [ ] **Step 1: Replace dependency**

Remove constructor dependency `SessionEventHub eventHub`; add `ISessionStateManager ssm` and `IRuntimeTraceAccessor traceAccessor`.

- [ ] **Step 2: Create trace context for request**

At request start:

```csharp
var trace = RuntimeTraceContext.CreateNew(
    sessionId: req.SessionId,
    workspaceId: workspaceId,
    userId: User.Identity?.Name ?? "admin");
traceAccessor.Current = trace;
```

- [ ] **Step 3: Append frames to SSM**

Replace:

```csharp
var hub = eventHub.GetOrCreate(streamSessionId);
hub.Writer.TryWrite(frame);
```

with:

```csharp
await ssm.AppendAsync(
    streamSessionId,
    workspaceId,
    frame,
    trace.WithSession(streamSessionId, workspaceId),
    component: RuntimeActivityComponents.AgentExecution,
    operation: $"chat.stream.{frame.Event}",
    ct: CancellationToken.None);
```

- [ ] **Step 4: Build**

Run: `dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingPlatform\Controllers\Api\ChatApiController.cs
git commit -m "refactor: route chat stream frames through session state"
```

---

### Task 6: Event System Observability

**Files:**
- Modify: `Source/PuddingCore/Models/InternalEvent.cs`
- Modify: `Source/PuddingRuntime/Services/Events/InternalEventBus.cs`
- Modify: `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
- Modify: `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`

- [ ] **Step 1: Add trace metadata to event DTOs**

Add `RuntimeTraceContext? Trace` to `InternalEvent`, `RawEvent`, `ProcessedEvent`, and `QueuedEvent`.

- [ ] **Step 2: Emit publish/enqueue/dequeue/dispatch activities**

Use `IRuntimeActivitySink` and `IRuntimeTraceAccessor` in bus, queue, and dispatcher. Prefer event `Trace`; fall back to ambient trace; create a new trace from event/session/workspace when missing.

- [ ] **Step 3: Preserve trace through queue serialization**

When `PriorityEventQueue.EnqueueAsync` builds `QueuedEvent`, copy `evt.Trace`. When `EventDispatcher.DeserializeEvent` rebuilds `InternalEvent`, copy `qe.Trace`.

- [ ] **Step 4: Build**

Run: `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingCore\Models\InternalEvent.cs Source\PuddingRuntime\Services\Events
git commit -m "feat: add event system runtime activities"
```

---

### Task 7: Sub-Agent Lifecycle Owner

**Files:**
- Modify: `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs`
- Modify: `Source/PuddingPlatform/Services/SubAgentManager.cs`
- Modify: `Source/PuddingPlatform/Services/SessionStateManager.cs`

- [ ] **Step 1: Add terminal-state idempotency**

In `TrackSubAgentCompleteAsync`, check current `SessionSubAgentEntity.Status`. If it is already `completed`, `failed`, `cancelled`, or `timed_out`, log and return without writing another terminal state.

- [ ] **Step 2: Route async `SubAgentTool` through manager**

For `sync == false`, build `SubAgentSpawnRequest` and call `ISubAgentManager.SpawnAsync`. Remove duplicate fire-and-forget execution code from `SubAgentTool` async path.

- [ ] **Step 3: Emit manager activities**

In `SubAgentManager.SpawnAsync`, `ExecuteSyncAsync`, completion, failure, and cancellation paths, record `sub_agent` activities with trace metadata.

- [ ] **Step 4: Build**

Run: `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingRuntime\Services\Skills\SubAgentTool.cs Source\PuddingPlatform\Services\SubAgentManager.cs Source\PuddingPlatform\Services\SessionStateManager.cs
git commit -m "refactor: centralize sub-agent lifecycle"
```

---

### Task 8: Verification Sweep

**Files:**
- Documentation update if needed: `Docs/QA/`

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "RuntimeTraceContextTests|StartupSchemaSafetyTests" --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 2: Build affected projects**

Run:

```powershell
dotnet build Source\PuddingCore\PuddingCore.csproj --no-restore --nologo
dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS, except any pre-existing external `github.hyfree.GM` issue must be documented.

- [ ] **Step 3: Inspect final diff**

Run:

```powershell
git status --short
git diff --stat HEAD
```

Expected: only planned files changed, plus pre-existing `external/github.hyfree.GM` remains untouched.

- [ ] **Step 4: Commit verification notes only if needed**

If verification uncovers documentation-only caveats, add a short QA note under `Docs/QA/` and commit it.

