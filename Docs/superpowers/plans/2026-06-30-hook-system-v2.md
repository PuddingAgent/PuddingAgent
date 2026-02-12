# Hook System v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first shippable Hook System v2 slice: a typed hook publisher, a registered `session.compressed` event, and an observable internal memory-maintenance handler that does not call the subconscious LLM directly.

**Architecture:** Hook publishers create typed lifecycle events and publish them through the existing `IInternalEventBus`, which is already bridged into `IPriorityEventQueue` and `EventDispatcher`. `session.compressed` is the first hook event; its handler creates an observable framework-owned memory-maintenance signal without blocking compaction. Durable `SubconsciousJobs` now provide the first persistent R4 job boundary, with Trace and Metrics evidence for queue transitions.

**Tech Stack:** .NET 10, MSTest, EF Core/SQLite existing event queue, `PuddingCore`, `PuddingRuntime`, `PuddingPlatform`, `PuddingWebApiTests`.

---

## Current Implementation Notes

2026-06-30 slice status:

- Completed: Hook core contracts, event schemas, `hook_system` activity component, `HookPublisher`, DI registration, `session.compressed` publication from `ContextCompactionService`, and `SessionCompressedMemoryMaintenanceHook`.
- Actual publication point is `ContextCompactionService`, not `SessionEventsController`, so automatic compaction and manual compaction share the same framework hook.
- Actual memory-maintenance bridge now uses durable `SubconsciousJobs` for `session.compressed`. The existing `Channel<ConsolidationJob>` remains only as an explicit compatibility path for legacy producers.
- The implemented handler is `SessionCompressedMemoryMaintenanceHook`, not the earlier planned `SessionCompressedMemoryHookHandler`.
- This branch intentionally has no content-hash semantic dedupe. Hash-like strings are used only as idempotency metadata for exact source operations.
- Durable job queue state transitions now emit bounded `RuntimeActivity` records for trace evidence and `TelemetryMetric` rows for aggregate facts.
- `Tools/Diagnostics/query_metrics.py subconscious-jobs` summarizes durable subconscious job metrics by `job_type + source_hook_name`, including enqueue/lease/complete/retry/dead_letter counts and completion/retry/dead-letter rates.
- Legacy duplicate-learning prevention is implemented: `Subconscious:EnableLegacyConsolidationHook` and `Subconscious:EnableLegacyAgentExecutionFallback` default to false, so old direct channel producers do not run unless explicitly enabled.

---

## File Map

- Create: `Source/PuddingCore/Abstractions/IHookPublisher.cs`
  - Owns Hook v2 publishing abstraction and publish options.
- Create: `Source/PuddingCore/Models/HookEventNames.cs`
  - Owns canonical hook event names and typed payload records.
- Modify: `Source/PuddingCore/Events/EventSchemaRegistry.cs`
  - Registers `session.compressed` as an internal event type.
- Modify: `Source/PuddingCore/Observability/RuntimeActivity.cs`
  - Adds `RuntimeActivityComponents.HookSystem`.
- Create: `Source/PuddingRuntime/Services/Hooks/HookPublisher.cs`
  - Maps Hook v2 publish calls to `InternalEvent` and `IInternalEventBus`.
- Create: `Source/PuddingRuntime/Services/Hooks/HookPayloadJson.cs`
  - Small JSON helper for hook event payload deserialization in handlers.
- Create: `Source/PuddingRuntime/Services/Hooks/SessionCompressedMemoryHookHandler.cs`
  - Handles `session.compressed`, records diagnostics, and provides the first internal R4 bridge.
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
  - Registers Hook v2 publisher and handler.
- Modify: `Source/PuddingAgent/Program.cs`
  - Mirrors runtime registrations used by the app host.
- Modify: `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`
  - Publishes `session.compressed` after successful manual compaction.
- Test: `Source/PuddingRuntimeTests/Services/HookPublisherTests.cs`
  - Covers typed publishing and metadata mapping.
- Test: `Source/PuddingRuntimeTests/Services/SessionCompressedMemoryHookHandlerTests.cs`
  - Covers handler behavior and payload validation.
- Test: `Source/PuddingWebApiTests/SessionEventsControllerTests.cs`
  - Covers `session.compressed` publication after compaction success if the existing test host can stub dependencies cheaply.
- Modify: `Docs/Config/hooks.md`
  - Notes v1 external hooks remain read-only future integration for Hook v2.
- Modify: `memory/memory-system-v2-requirements.md`
  - Updates R4 implementation status after code lands.
- Modify: `goal.md`
  - Appends the implementation decision and verification evidence.

---

## Task 1: Hook Core Contracts

**Files:**
- Create: `Source/PuddingCore/Abstractions/IHookPublisher.cs`
- Create: `Source/PuddingCore/Models/HookEventNames.cs`
- Modify: `Source/PuddingCore/Events/EventSchemaRegistry.cs`
- Modify: `Source/PuddingCore/Observability/RuntimeActivity.cs`
- Test: `Source/PuddingRuntimeTests/Services/HookPublisherTests.cs`

- [ ] **Step 1: Add a failing contract smoke test**

Create `Source/PuddingRuntimeTests/Services/HookPublisherTests.cs` with this first test. It intentionally references types that do not exist yet.

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class HookPublisherTests
{
    [TestMethod]
    public void HookEventNames_ShouldExpose_SessionCompressed()
    {
        Assert.AreEqual("session.compressed", HookEventNames.SessionCompressed.Value);
    }

    [TestMethod]
    public void SessionCompressedHookPayload_ShouldCarry_CompactionIdentity()
    {
        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = "ws-1",
            OriginalSessionId = "s-old",
            NewSessionId = "s-new",
            AgentId = "agent-1",
            AgentTemplateId = "tpl-1",
            CompactionId = "cmp-1",
            Mode = "Manual",
            Level = "Full",
            Reason = "manual compact",
        };

        Assert.AreEqual("ws-1", payload.WorkspaceId);
        Assert.AreEqual("s-old", payload.OriginalSessionId);
        Assert.AreEqual("cmp-1", payload.CompactionId);
    }
}
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests" --logger "console;verbosity=minimal"
```

Expected: build fails because `HookEventNames` and `SessionCompressedHookPayload` do not exist.

- [ ] **Step 3: Add Hook publisher abstraction**

Create `Source/PuddingCore/Abstractions/IHookPublisher.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Publishes framework lifecycle hooks into Pudding's internal event pipeline.
/// Implementations must stay lightweight and must not run long business logic.
/// </summary>
public interface IHookPublisher
{
    Task<string> PublishAsync<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>Canonical Hook v2 event name wrapper.</summary>
public readonly record struct HookEventName(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Options used when mapping Hook v2 events to InternalEvent.</summary>
public sealed record HookPublishOptions
{
    public EventPriorityLevel Priority { get; init; } = EventPriorityLevel.Normal;
    public string SourceType { get; init; } = "framework";
    public string? SourceId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? CausationId { get; init; }
    public string? SessionId { get; init; }
    public string WorkspaceId { get; init; } = "default";
    public string? AgentId { get; init; }
}
```

- [ ] **Step 4: Add canonical names and `session.compressed` payload**

Create `Source/PuddingCore/Models/HookEventNames.cs`:

```csharp
using PuddingCode.Abstractions;

namespace PuddingCode.Models;

/// <summary>Canonical Hook v2 event names.</summary>
public static class HookEventNames
{
    public static readonly HookEventName SessionCompressed = new("session.compressed");
    public static readonly HookEventName SessionCompactionFailed = new("session.compaction_failed");
    public static readonly HookEventName AgentLoopCompleted = new("agent.loop.completed");
}

/// <summary>Payload emitted after a session compaction completes successfully.</summary>
public sealed record SessionCompressedHookPayload
{
    public required string WorkspaceId { get; init; }
    public required string OriginalSessionId { get; init; }
    public string? NewSessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string CompactionId { get; init; }
    public required string Mode { get; init; }
    public required string Level { get; init; }
    public required string Reason { get; init; }
    public int? OriginalMessageCount { get; init; }
    public int? PreservedMessageCount { get; init; }
    public int? DroppedMessageCount { get; init; }
    public string? SummaryPreview { get; init; }
    public DateTimeOffset CompressedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Register hook event schemas**

In `Source/PuddingCore/Events/EventSchemaRegistry.cs`, inside `BuildAllSchemas()` under the Internal section, add:

```csharp
        // Hook v2 lifecycle events
        yield return new EventSchemaDefinition("session.compressed", 1, "hook",
            "Session context compaction completed", ["workspace_id", "original_session_id", "compaction_id"]);
        yield return new EventSchemaDefinition("session.compaction_failed", 1, "hook",
            "Session context compaction failed", ["workspace_id", "session_id", "compaction_id", "error"]);
        yield return new EventSchemaDefinition("agent.loop.completed", 1, "hook",
            "Agent loop completed and emitted a lifecycle hook", ["session_id", "agent_id"]);
```

- [ ] **Step 6: Add Hook system runtime activity component**

In `Source/PuddingCore/Observability/RuntimeActivity.cs`, add this constant to `RuntimeActivityComponents`:

```csharp
    public const string HookSystem = "hook_system";
```

- [ ] **Step 7: Run the contract tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests" --logger "console;verbosity=minimal"
```

Expected: tests pass or fail only because implementation tests from later tasks are not present yet.

- [ ] **Step 8: Commit Task 1**

```powershell
git add Source\PuddingCore\Abstractions\IHookPublisher.cs Source\PuddingCore\Models\HookEventNames.cs Source\PuddingCore\Events\EventSchemaRegistry.cs Source\PuddingCore\Observability\RuntimeActivity.cs Source\PuddingRuntimeTests\Services\HookPublisherTests.cs
git commit -m "feat: add hook system v2 contracts"
```

---

## Task 2: Hook Publisher Implementation

**Files:**
- Create: `Source/PuddingRuntime/Services/Hooks/HookPublisher.cs`
- Modify: `Source/PuddingRuntimeTests/Services/HookPublisherTests.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Extend the failing tests for event mapping**

Append this test support and test to `Source/PuddingRuntimeTests/Services/HookPublisherTests.cs`:

```csharp
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingRuntime.Services.Hooks;

// Add inside HookPublisherTests
[TestMethod]
public async Task PublishAsync_ShouldMap_Hook_To_InternalEvent()
{
    var bus = new RecordingInternalEventBus();
    var sink = new RecordingRuntimeActivitySink();
    var traceAccessor = new RuntimeTraceAccessor();
    var publisher = new HookPublisher(bus, sink, traceAccessor);

    var payload = new SessionCompressedHookPayload
    {
        WorkspaceId = "ws-1",
        OriginalSessionId = "s-old",
        NewSessionId = "s-new",
        AgentId = "agent-1",
        AgentTemplateId = "tpl-1",
        CompactionId = "cmp-1",
        Mode = "Manual",
        Level = "Full",
        Reason = "manual compact",
    };

    var eventId = await publisher.PublishAsync(
        HookEventNames.SessionCompressed,
        payload,
        new HookPublishOptions
        {
            WorkspaceId = "ws-1",
            SessionId = "s-old",
            AgentId = "agent-1",
            SourceId = "cmp-1",
            IdempotencyKey = "session.compressed:ws-1:s-old:cmp-1",
        });

    Assert.IsFalse(string.IsNullOrWhiteSpace(eventId));
    Assert.HasCount(1, bus.Events);
    Assert.AreEqual("session.compressed", bus.Events[0].Type);
    Assert.AreEqual("ws-1", bus.Events[0].WorkspaceId);
    Assert.AreEqual("s-old", bus.Events[0].SessionId);
    Assert.AreEqual("agent-1", bus.Events[0].AgentId);
    Assert.IsNotNull(bus.Events[0].Metadata);
    Assert.AreEqual("session.compressed:ws-1:s-old:cmp-1", bus.Events[0].Metadata!["hook.idempotency_key"]);
    Assert.IsTrue(sink.Activities.Any(a => a.Component == RuntimeActivityComponents.HookSystem
        && a.Operation == "hook.publish"
        && a.Status == RuntimeActivityStatuses.Succeeded));
}

private sealed class RecordingInternalEventBus : IInternalEventBus
{
    public List<InternalEvent> Events { get; } = [];

    public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }

    public Task<IEventSubscriptionHandle> SubscribeAsync(
        string eventTypePattern,
        Func<InternalEvent, Task> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
        => Task.CompletedTask;
}

private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
{
    public List<RuntimeActivity> Activities { get; } = [];

    public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
    {
        Activities.Add(activity);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
}

private sealed class RuntimeTraceAccessor : IRuntimeTraceAccessor
{
    public RuntimeTraceContext? Current { get; set; }
}
```

- [ ] **Step 2: Run the failing publisher test**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests.PublishAsync_ShouldMap_Hook_To_InternalEvent" --logger "console;verbosity=minimal"
```

Expected: build fails because `HookPublisher` does not exist.

- [ ] **Step 3: Implement `HookPublisher`**

Create `Source/PuddingRuntime/Services/Hooks/HookPublisher.cs`:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Hooks;

/// <summary>
/// Hook v2 publisher that maps framework lifecycle hooks onto the internal event pipeline.
/// </summary>
public sealed class HookPublisher : IHookPublisher
{
    private readonly IInternalEventBus _eventBus;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;

    public HookPublisher(
        IInternalEventBus eventBus,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor)
    {
        _eventBus = eventBus;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
    }

    public async Task<string> PublishAsync<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name.Value))
            throw new ArgumentException("Hook event name cannot be empty.", nameof(name));

        options ??= new HookPublishOptions();
        var eventId = Guid.NewGuid().ToString("N");
        var trace = _traceAccessor.Current
            ?? RuntimeTraceContext.CreateNew(
                sessionId: options.SessionId,
                workspaceId: options.WorkspaceId,
                eventId: eventId);
        trace = trace.WithEvent(eventId);
        _traceAccessor.Current = trace;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hook.name"] = name.Value,
        };
        if (!string.IsNullOrWhiteSpace(options.IdempotencyKey))
            metadata["hook.idempotency_key"] = options.IdempotencyKey!;

        var evt = new InternalEvent
        {
            EventId = eventId,
            Type = name.Value,
            Priority = options.Priority,
            Source = new EventSource
            {
                SourceType = options.SourceType,
                SourceId = options.SourceId,
            },
            WorkspaceId = options.WorkspaceId,
            SessionId = options.SessionId,
            AgentId = options.AgentId,
            Payload = payload,
            Metadata = metadata,
            CausationId = options.CausationId,
            TraceId = trace.TraceId,
            CorrelationId = trace.CorrelationId,
            Trace = trace,
        };

        try
        {
            await _eventBus.PublishAsync(evt, ct);
            await RecordAsync(trace, name.Value, eventId, RuntimeActivityStatuses.Succeeded, null, ct);
            return eventId;
        }
        catch (Exception ex)
        {
            await RecordAsync(trace, name.Value, eventId, RuntimeActivityStatuses.Failed, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private Task RecordAsync(
        RuntimeTraceContext trace,
        string hookName,
        string eventId,
        string status,
        string? error,
        CancellationToken ct)
    {
        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.HookSystem,
            Operation = "hook.publish",
            Status = status,
            Summary = status == RuntimeActivityStatuses.Succeeded
                ? $"Published hook {hookName}"
                : $"Failed to publish hook {hookName}",
            Metadata = new Dictionary<string, string>
            {
                ["hookName"] = hookName,
                ["eventId"] = eventId,
            },
            ErrorMessage = error,
        }, ct);
    }
}
```

- [ ] **Step 4: Register `HookPublisher` in runtime DI**

In `Source/PuddingRuntime/DependencyInjection.cs`, add the namespace:

```csharp
using PuddingRuntime.Services.Hooks;
```

Then register near the event/runtime infrastructure registrations:

```csharp
        services.AddSingleton<IHookPublisher, HookPublisher>();
```

- [ ] **Step 5: Register `HookPublisher` in app host DI**

In `Source/PuddingAgent/Program.cs`, add this namespace with the other runtime service namespaces:

```csharp
using PuddingRuntime.Services.Hooks;
```

Then add the same registration in the service-registration area where event services are registered:

```csharp
builder.Services.AddSingleton<IHookPublisher, HookPublisher>();
```

- [ ] **Step 6: Run publisher tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests" --logger "console;verbosity=minimal"
```

Expected: all `HookPublisherTests` pass.

- [ ] **Step 7: Build runtime and app host**

Run:

```powershell
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: both builds pass with only known warnings.

- [ ] **Step 8: Commit Task 2**

```powershell
git add Source\PuddingRuntime\Services\Hooks\HookPublisher.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingAgent\Program.cs Source\PuddingRuntimeTests\Services\HookPublisherTests.cs
git commit -m "feat: publish hook events through internal event bus"
```

---

## Task 3: Publish `session.compressed` After Compaction

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`
- Test: `Source/PuddingWebApiTests/SessionEventsControllerTests.cs`

- [ ] **Step 1: Add a focused publishing test**

Extend `Source\PuddingWebApiTests\SessionEventsControllerTests.cs` with this test:

```csharp
[TestMethod]
public async Task Compact_ShouldPublish_SessionCompressed_Hook_WhenCompactionSucceeds()
{
    var capture = new CapturingCompactionService();
    var hooks = new RecordingHookPublisher();
    using var factory = _factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IContextCompactionService>();
            services.AddSingleton<IContextCompactionService>(capture);
            services.RemoveAll<IAgentRuntimeProfileResolver>();
            services.AddSingleton<IAgentRuntimeProfileResolver>(new FixedAgentRuntimeProfileResolver());
            services.RemoveAll<IHookPublisher>();
            services.AddSingleton<IHookPublisher>(hooks);
        });
    });
    using var client = factory.CreateClient();
    JwtHelper.SetBearerToken(client);

    var createResp = await client.PostAsJsonAsync("/api/sessions", new
    {
        workspaceId = "default",
        agentTemplateId = "global:research-assistant",
        title = "compact hook test"
    });
    createResp.EnsureSuccessStatusCode();
    var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

    var compactResp = await client.PostAsJsonAsync($"/api/sessions/{created!.SessionId}/compact", new
    {
        workspaceId = "default",
        agentId = "default.global_research-assistant.c1",
        reason = "manual slash command"
    });

    Assert.AreEqual(HttpStatusCode.OK, compactResp.StatusCode);
    Assert.IsTrue(hooks.Published.Any(e =>
        e.Name.Value == "session.compressed"
        && e.Payload is SessionCompressedHookPayload payload
        && payload.OriginalSessionId == created.SessionId
        && payload.WorkspaceId == "default"
        && payload.CompactionId == capture.LastRequest!.CompactionId));
}

private sealed class RecordingHookPublisher : IHookPublisher
{
    public List<(HookEventName Name, object? Payload, HookPublishOptions? Options)> Published { get; } = [];

    public Task<string> PublishAsync<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions? options = null,
        CancellationToken ct = default)
    {
        Published.Add((name, payload, options));
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }
}
```

Add these usings if the file does not already contain them:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;
```

- [ ] **Step 2: Run the failing test**

Run the narrowest relevant command:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "FullyQualifiedName~SessionEventsControllerTests.Compact_ShouldPublish_SessionCompressed_Hook_WhenCompactionSucceeds" --logger "console;verbosity=minimal"
```

Expected: fails because `SessionEventsController` does not publish a hook.

- [ ] **Step 3: Inject `IHookPublisher` into `SessionEventsController`**

In `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`, add:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;
```

Add a field:

```csharp
    private readonly IHookPublisher _hookPublisher;
```

Add `IHookPublisher hookPublisher` to the constructor and assign it:

```csharp
        _hookPublisher = hookPublisher;
```

- [ ] **Step 4: Publish after successful compaction and session switch diagnostics**

In the successful `Compact` path, after:

```csharp
result = WithSessionSwitchDiagnostics(result, newSessionId, newSession?.Title);
var response = new CompactSessionResponse(result, newSessionId, newSession?.Title);
```

insert:

```csharp
            await PublishSessionCompressedHookAsync(
                workspaceId,
                sessionId,
                newSessionId,
                agentId,
                agentTemplateId,
                compactRequest,
                result,
                ct);
```

Add this private method to the controller:

```csharp
    private async Task PublishSessionCompressedHookAsync(
        string workspaceId,
        string originalSessionId,
        string? newSessionId,
        string? agentId,
        string? agentTemplateId,
        ContextCompactionRequest request,
        ContextCompactionResult result,
        CancellationToken ct)
    {
        var diagnostics = result.Diagnostics;
        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = workspaceId,
            OriginalSessionId = originalSessionId,
            NewSessionId = newSessionId,
            AgentId = agentId,
            AgentTemplateId = agentTemplateId,
            CompactionId = request.CompactionId ?? Guid.NewGuid().ToString("N"),
            Mode = request.Mode.ToString(),
            Level = request.Level.ToString(),
            Reason = request.Reason,
            OriginalMessageCount = diagnostics?.OriginalMessageCount,
            PreservedMessageCount = diagnostics?.PreservedMessageCount,
            DroppedMessageCount = diagnostics?.DroppedMessageCount,
            SummaryPreview = TruncateForHook(result.SummaryPreview, 240),
        };

        try
        {
            await _hookPublisher.PublishAsync(
                HookEventNames.SessionCompressed,
                payload,
                new HookPublishOptions
                {
                    WorkspaceId = workspaceId,
                    SessionId = originalSessionId,
                    AgentId = agentId,
                    SourceId = request.CompactionId,
                    IdempotencyKey = $"session.compressed:{workspaceId}:{originalSessionId}:{request.CompactionId}",
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SessionEvents] session.compressed hook publish failed session={SessionId} compaction={CompactionId}",
                originalSessionId,
                request.CompactionId);
        }
    }

    private static string? TruncateForHook(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Length <= maxChars ? value : value[..maxChars];
    }
```

- [ ] **Step 5: Run the compaction hook test**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "FullyQualifiedName~SessionEventsControllerTests.Compact_ShouldPublish_SessionCompressed_Hook_WhenCompactionSucceeds" --logger "console;verbosity=minimal"
```

Expected: the new test passes.

- [ ] **Step 6: Run existing SessionEventsController tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "FullyQualifiedName~SessionEventsControllerTests" --logger "console;verbosity=minimal"
```

Expected: all session event controller tests pass.

- [ ] **Step 7: Commit Task 3**

```powershell
git add Source\PuddingPlatform\Controllers\Api\SessionEventsController.cs Source\PuddingWebApiTests\SessionEventsControllerTests.cs
git commit -m "feat: publish session compressed hook"
```

---

## Task 4: Internal Memory-Maintenance Hook Handler

**Files:**
- Create: `Source/PuddingRuntime/Services/Hooks/HookPayloadJson.cs`
- Create: `Source/PuddingRuntime/Services/Hooks/SessionCompressedMemoryHookHandler.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingRuntimeTests/Services/SessionCompressedMemoryHookHandlerTests.cs`

- [ ] **Step 1: Add failing handler tests**

Create `Source/PuddingRuntimeTests/Services/SessionCompressedMemoryHookHandlerTests.cs`:

```csharp
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingRuntime.Services.Hooks;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SessionCompressedMemoryHookHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_ShouldRecord_MemoryMaintenanceSignal()
    {
        var sink = new RecordingRuntimeActivitySink();
        var handler = new SessionCompressedMemoryHookHandler(sink);
        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = "ws-1",
            OriginalSessionId = "s-old",
            AgentId = "agent-1",
            AgentTemplateId = "tpl-1",
            CompactionId = "cmp-1",
            Mode = "Manual",
            Level = "Full",
            Reason = "manual compact",
        };

        var evt = new InternalEvent
        {
            EventId = "evt-1",
            Type = "session.compressed",
            WorkspaceId = "ws-1",
            SessionId = "s-old",
            AgentId = "agent-1",
            Payload = JsonSerializer.SerializeToElement(payload),
        };

        var handled = await handler.HandleAsync(evt, CancellationToken.None);

        Assert.IsTrue(handled);
        Assert.IsTrue(sink.Activities.Any(a =>
            a.Component == RuntimeActivityComponents.HookSystem
            && a.Operation == "hook.memory_maintenance.requested"
            && a.Status == RuntimeActivityStatuses.Succeeded
            && a.Metadata is not null
            && a.Metadata["hookName"] == "session.compressed"
            && a.Metadata["compactionId"] == "cmp-1"));
    }

    [TestMethod]
    public async Task HandleAsync_ShouldComplete_InvalidPayload_AsSkipped()
    {
        var sink = new RecordingRuntimeActivitySink();
        var handler = new SessionCompressedMemoryHookHandler(sink);
        var evt = new InternalEvent
        {
            EventId = "evt-1",
            Type = "session.compressed",
            WorkspaceId = "ws-1",
            Payload = JsonSerializer.SerializeToElement(new { wrong = true }),
        };

        var handled = await handler.HandleAsync(evt, CancellationToken.None);

        Assert.IsTrue(handled);
        Assert.IsTrue(sink.Activities.Any(a =>
            a.Operation == "hook.memory_maintenance.skipped"
            && a.Status == RuntimeActivityStatuses.Deferred));
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Activities { get; } = [];

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
    }
}
```

- [ ] **Step 2: Run the failing handler tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SessionCompressedMemoryHookHandlerTests" --logger "console;verbosity=minimal"
```

Expected: build fails because `SessionCompressedMemoryHookHandler` does not exist.

- [ ] **Step 3: Add payload helper**

Create `Source/PuddingRuntime/Services/Hooks/HookPayloadJson.cs`:

```csharp
using System.Text.Json;

namespace PuddingRuntime.Services.Hooks;

internal static class HookPayloadJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryRead<T>(object? payload, out T? value)
    {
        value = default;
        if (payload is null)
            return false;

        try
        {
            if (payload is T typed)
            {
                value = typed;
                return true;
            }

            if (payload is JsonElement element)
            {
                value = element.Deserialize<T>(JsonOptions);
                return value is not null;
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Add handler implementation**

Create `Source/PuddingRuntime/Services/Hooks/SessionCompressedMemoryHookHandler.cs`:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Hooks;

/// <summary>
/// First R4 bridge: observes session.compressed and records an internal memory-maintenance signal.
/// This handler intentionally does not call LLMs or write MemoryLibrary directly.
/// </summary>
public sealed class SessionCompressedMemoryHookHandler : IEventHandler
{
    private readonly IRuntimeActivitySink _activitySink;

    public SessionCompressedMemoryHookHandler(IRuntimeActivitySink activitySink)
    {
        _activitySink = activitySink;
    }

    public string EventTypePattern => HookEventNames.SessionCompressed.Value;

    public bool SupportsInterruption => false;

    public async Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct)
    {
        if (!HookPayloadJson.TryRead<SessionCompressedHookPayload>(evt.Payload, out var payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.WorkspaceId)
            || string.IsNullOrWhiteSpace(payload.OriginalSessionId)
            || string.IsNullOrWhiteSpace(payload.CompactionId))
        {
            await RecordSkippedAsync(evt, "Invalid session.compressed payload", ct);
            return true;
        }

        var trace = evt.Trace
            ?? RuntimeTraceContext.CreateNew(
                sessionId: payload.OriginalSessionId,
                workspaceId: payload.WorkspaceId,
                eventId: evt.EventId);

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.HookSystem,
            Operation = "hook.memory_maintenance.requested",
            Status = RuntimeActivityStatuses.Succeeded,
            Summary = "Session compression requested memory maintenance",
            Metadata = new Dictionary<string, string>
            {
                ["hookName"] = HookEventNames.SessionCompressed.Value,
                ["eventId"] = evt.EventId,
                ["workspaceId"] = payload.WorkspaceId,
                ["sessionId"] = payload.OriginalSessionId,
                ["newSessionId"] = payload.NewSessionId ?? "",
                ["agentId"] = payload.AgentId ?? "",
                ["agentTemplateId"] = payload.AgentTemplateId ?? "",
                ["compactionId"] = payload.CompactionId,
                ["jobType"] = "memory.consolidate_session",
            },
        }, ct);

        return true;
    }

    private Task RecordSkippedAsync(InternalEvent evt, string reason, CancellationToken ct)
    {
        var trace = evt.Trace
            ?? RuntimeTraceContext.CreateNew(
                sessionId: evt.SessionId,
                workspaceId: evt.WorkspaceId,
                eventId: evt.EventId);

        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.HookSystem,
            Operation = "hook.memory_maintenance.skipped",
            Status = RuntimeActivityStatuses.Deferred,
            Summary = reason,
            Metadata = new Dictionary<string, string>
            {
                ["hookName"] = HookEventNames.SessionCompressed.Value,
                ["eventId"] = evt.EventId,
            },
        }, ct);
    }
}
```

- [ ] **Step 5: Register the handler in runtime DI**

In `Source/PuddingRuntime/DependencyInjection.cs`, add:

```csharp
        services.AddSingleton<SessionCompressedMemoryHookHandler>();
        services.AddSingleton<IEventHandler>(sp => sp.GetRequiredService<SessionCompressedMemoryHookHandler>());
```

- [ ] **Step 6: Register the handler in app host DI**

In `Source/PuddingAgent/Program.cs`, add:

```csharp
builder.Services.AddSingleton<SessionCompressedMemoryHookHandler>();
builder.Services.AddSingleton<IEventHandler>(sp => sp.GetRequiredService<SessionCompressedMemoryHookHandler>());
```

- [ ] **Step 7: Run handler tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SessionCompressedMemoryHookHandlerTests" --logger "console;verbosity=minimal"
```

Expected: all handler tests pass.

- [ ] **Step 8: Run runtime tests for hooks**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests|FullyQualifiedName~SessionCompressedMemoryHookHandlerTests" --logger "console;verbosity=minimal"
```

Expected: all Hook v2 runtime tests pass.

- [ ] **Step 9: Commit Task 4**

```powershell
git add Source\PuddingRuntime\Services\Hooks\HookPayloadJson.cs Source\PuddingRuntime\Services\Hooks\SessionCompressedMemoryHookHandler.cs Source\PuddingRuntime\DependencyInjection.cs Source\PuddingAgent\Program.cs Source\PuddingRuntimeTests\Services\SessionCompressedMemoryHookHandlerTests.cs
git commit -m "feat: handle session compressed memory hook"
```

---

## Task 5: Event Pipeline and Documentation Verification

**Files:**
- Modify: `Docs/Config/hooks.md`
- Modify: `memory/memory-system-v2-requirements.md`
- Modify: `goal.md`

- [ ] **Step 1: Run focused Hook tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~HookPublisherTests|FullyQualifiedName~SessionCompressedMemoryHookHandlerTests" --logger "console;verbosity=minimal"
```

Expected: all Hook v2 runtime tests pass.

- [ ] **Step 2: Run SessionEvents controller tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "FullyQualifiedName~SessionEventsControllerTests" --logger "console;verbosity=minimal"
```

Expected: all SessionEvents controller tests pass.

- [ ] **Step 3: Build affected projects**

Run:

```powershell
dotnet build Source\PuddingCore\PuddingCore.csproj --no-restore --nologo
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: builds pass with only known warnings.

- [ ] **Step 4: Update external hook documentation**

In `Docs/Config/hooks.md`, add this section after the v1 config example:

```markdown
## Hook System v2 direction

Hook v2 separates internal framework hooks from external user-configured hooks.

- Internal hooks are mandatory framework lifecycle events and use `IHookPublisher` plus the existing persistent event pipeline.
- External hooks are read-only, async, timeout-bound, and cannot block or mutate internal mandatory hooks.
- The first v2 internal hook is `session.compressed`, used by Memory System v2 R4.

The existing `metrics`, `audit_file`, and `external` settings remain v1-compatible. A later migration will map those external targets onto Hook v2 as read-only subscribers.
```

- [ ] **Step 5: Update Memory v2 requirements status**

In `memory/memory-system-v2-requirements.md`, add a progress table entry:

```markdown
| 2026-06-30 | R4 Hook System v2 第一实现 | partial | 新增 `IHookPublisher`、`session.compressed` payload/schema、HookPublisher、SessionEvents compact 成功发布、SessionCompressedMemoryHookHandler；Hook 只发布事件并记录/转交维护信号，不直接调用潜意识 LLM |
```

Also update the R4 current-code conclusion to say that the first implementation is in place and durable `SubconsciousJobs` remain the next phase.

- [ ] **Step 6: Update goal log**

Append to `goal.md`:

```markdown
- 2026-06-30: R4 Hook System v2 第一实现 — `session.compressed` 已通过 `IHookPublisher` 进入现有内部事件管道，压缩成功后发布 Hook 事件，内置 handler 记录 `memory.consolidate_session` 维护信号；该阶段不直接调用潜意识 LLM，后续接 ADR-027/task42 的持久 `SubconsciousJobs`。
```

- [ ] **Step 7: Run whitespace check**

Run:

```powershell
git diff --check
```

Expected: no whitespace errors. LF/CRLF warnings are acceptable if they match existing repository behavior.

- [ ] **Step 8: Commit Task 5**

```powershell
git add Docs\Config\hooks.md memory\memory-system-v2-requirements.md goal.md
git commit -m "docs: record hook system v2 implementation status"
```

---

## Follow-Up Plan After This Slice

After this first Hook v2 slice is green, continue with ADR-027/task42 durable background learning:

1. Add `SubconsciousJobs` entity, schema, and `ISubconsciousJobQueue`.
2. Change `SessionCompressedMemoryHookHandler` from recording a maintenance signal to enqueueing a durable job.
3. Move `SubconsciousWorkerService` from `Channel<ConsolidationJob>` to lease-based durable jobs.
4. Put legacy `SubconsciousConsolidationHook` behind a compatibility flag.
5. Add idle-aware scheduling and workspace concurrency limits.

This follow-up must preserve the same rule: Hook publishers do not call LLMs or write MemoryLibrary directly.
