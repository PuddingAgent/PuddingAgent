# Hook Event Subconscious Learning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move subconscious learning after Agent completion onto the unified event pipeline, with persistent background jobs and idle-aware execution.

**Architecture:** Agent lifecycle hooks publish `agent.loop.completed` events into `IInternalEventBus`. The existing event pipeline persists and dispatches them to `SubconsciousEventHandler`, which creates durable `SubconsciousJobs`. `SubconsciousWorkerService` leases jobs only when runtime idle signals allow background LLM work, then calls `ISubconsciousOrchestrator` and writes memory-library source pointers.

**Tech Stack:** .NET 10, EF Core, SQLite, existing `PuddingCore`, `PuddingRuntime`, `PuddingMemoryEngine`, `PuddingAgent` dependency injection.

---

## File Map

- Create: `Source/PuddingCore/Models/AgentLifecycleEventPayloads.cs`
  - Owns serializable payload records for agent lifecycle events.
- Create: `Source/PuddingCore/Models/SubconsciousJobModels.cs`
  - Owns durable job DTOs, job status constants, queue stats, and job result records.
- Create: `Source/PuddingCore/Abstractions/ISubconsciousJobQueue.cs`
  - Owns persistent job queue abstraction.
- Create: `Source/PuddingCore/Abstractions/IRuntimeIdleSignal.cs`
  - Owns runtime idleness snapshot abstraction.
- Create: `Source/PuddingRuntime/Services/AgentLoop/AgentLoopEventPublisherHook.cs`
  - Publishes lifecycle events from `IAgentLoopHook`.
- Create: `Source/PuddingRuntime/Services/Events/SubconsciousEventHandler.cs`
  - Converts lifecycle events into subconscious jobs.
- Create: `Source/PuddingMemoryEngine/Entities/SubconsciousJobEntity.cs`
  - EF entity for `SubconsciousJobs`.
- Create: `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`
  - SQLite-backed implementation of `ISubconsciousJobQueue`.
- Create: `Source/PuddingRuntime/Services/Background/RuntimeIdleSignal.cs`
  - Initial conservative idle signal implementation.
- Modify: `Source/PuddingMemoryEngine/Data/MemoryDbContext.cs`
  - Register `SubconsciousJobEntity` and indexes.
- Modify: `Source/PuddingMemoryEngine/Schema/init_memory.sql`
  - Add non-destructive DDL for `SubconsciousJobs`.
- Modify: `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
  - Lease from persistent queue and gate on idle snapshot.
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
  - Register new hook, handler, queue, idle signal.
- Modify: `Source/PuddingAgent/Program.cs`
  - Mirror registrations for application host.
- Modify: `Source/PuddingCoreTests/PuddingCoreTests.csproj`
  - Add test-only references to `PuddingRuntime` and `PuddingMemoryEngine` for the new integration-facing tests.
- Test: `Source/PuddingCoreTests/Events/AgentLoopEventPublisherHookTests.cs`
- Test: `Source/PuddingCoreTests/Events/SubconsciousEventHandlerTests.cs`
- Test: `Source/PuddingCoreTests/Memory/SubconsciousJobQueueTests.cs`
- Test: `Source/PuddingCoreTests/Memory/SubconsciousWorkerServiceTests.cs`

---

## Task 1: Agent Lifecycle Event Payloads

**Files:**
- Create: `Source/PuddingCore/Models/AgentLifecycleEventPayloads.cs`

- [ ] **Step 1: Add payload record**

```csharp
namespace PuddingCode.Models;

public sealed record AgentLoopCompletedPayload
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string UserMessage { get; init; }
    public required string FinalMessage { get; init; }
    public required string StopReason { get; init; }
    public int MaxRounds { get; init; }
    public string? MessageRangeStartId { get; init; }
    public string? MessageRangeEndId { get; init; }
    public string? ConversationHash { get; init; }
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Source\PuddingCore\PuddingCore.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 2: Event Publishing Hook

**Files:**
- Create: `Source/PuddingRuntime/Services/AgentLoop/AgentLoopEventPublisherHook.cs`
- Modify: `Source/PuddingCoreTests/PuddingCoreTests.csproj`
- Test: `Source/PuddingCoreTests/Events/AgentLoopEventPublisherHookTests.cs`

- [ ] **Step 1: Add test project references**

Add references required by the new tests:

```xml
<ProjectReference Include="..\PuddingRuntime\PuddingRuntime.csproj" />
<ProjectReference Include="..\PuddingMemoryEngine\PuddingMemoryEngine.csproj" />
```

- [ ] **Step 2: Write test for done event publishing**

```csharp
[TestMethod]
public async Task OnLoopCompleteAsync_Publishes_AgentLoopCompleted_Event()
{
    var bus = new RecordingInternalEventBus();
    var logger = NullLogger<AgentLoopEventPublisherHook>.Instance;
    var hook = new AgentLoopEventPublisherHook(bus, logger);

    var context = new AgentLoopContext
    {
        SessionId = "s1",
        WorkspaceId = "w1",
        AgentInstanceId = "a1",
        AgentTemplateId = "tpl1",
        UserMessage = "hello",
        MaxRounds = 8,
    };

    await hook.OnLoopCompleteAsync(context, "done", AgentLoopStopReason.Done);

    Assert.AreEqual(1, bus.Published.Count);
    Assert.AreEqual("agent.loop.completed", bus.Published[0].Type);
    Assert.AreEqual(EventPriorityLevel.Normal, bus.Published[0].Priority);
    Assert.AreEqual(EventIsolationMode.Isolated, bus.Published[0].Isolation);
}
```

- [ ] **Step 3: Implement hook**

```csharp
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime.Services.AgentLoop;

public sealed class AgentLoopEventPublisherHook : IAgentLoopHook
{
    private readonly IInternalEventBus _eventBus;
    private readonly ILogger<AgentLoopEventPublisherHook> _logger;

    public AgentLoopEventPublisherHook(
        IInternalEventBus eventBus,
        ILogger<AgentLoopEventPublisherHook> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new AgentLoopCompletedPayload
            {
                SessionId = context.SessionId,
                WorkspaceId = context.WorkspaceId,
                AgentId = context.AgentInstanceId,
                AgentTemplateId = context.AgentTemplateId,
                UserMessage = context.UserMessage,
                FinalMessage = finalMessage,
                StopReason = stopReason.ToString(),
                MaxRounds = context.MaxRounds,
            };

            await _eventBus.PublishAsync(new InternalEvent
            {
                Type = "agent.loop.completed",
                Priority = EventPriorityLevel.Normal,
                Isolation = EventIsolationMode.Isolated,
                Source = new EventSource { SourceType = "internal", SourceId = "agent-loop-hook" },
                WorkspaceId = context.WorkspaceId,
                AgentId = context.AgentInstanceId,
                SessionId = context.SessionId,
                Payload = payload,
                Metadata = new Dictionary<string, string>
                {
                    ["agentTemplateId"] = context.AgentTemplateId,
                    ["stopReason"] = stopReason.ToString(),
                },
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[AgentLoopEventPublisherHook] Failed to publish loop completed event session={SessionId}",
                context.SessionId);
        }
    }
}
```

- [ ] **Step 4: Run test**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter AgentLoopEventPublisherHookTests --logger "console;verbosity=minimal"`

Expected: test passes.

---

## Task 3: Subconscious Job Models and Queue Contract

**Files:**
- Create: `Source/PuddingCore/Models/SubconsciousJobModels.cs`
- Create: `Source/PuddingCore/Abstractions/ISubconsciousJobQueue.cs`

- [ ] **Step 1: Add job models**

```csharp
namespace PuddingCode.Models;

public static class SubconsciousJobTypes
{
    public const string ConsolidateSession = "consolidate-session";
    public const string MaintainSkillProposal = "maintain-skill-proposal";
}

public static class SubconsciousJobStatuses
{
    public const string Pending = "pending";
    public const string Leased = "leased";
    public const string Retry = "retry";
    public const string Completed = "completed";
    public const string DeadLetter = "dead_letter";
    public const string Skipped = "skipped";
}

public sealed record SubconsciousJob
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public required string IdempotencyKey { get; init; }
    public required string JobType { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceEventType { get; init; }
    public required string PayloadJson { get; init; }
    public int Priority { get; init; }
    public int MaxRetries { get; init; } = 3;
}

public sealed record SubconsciousJobResult
{
    public int FactsExtracted { get; init; }
    public int FactsMerged { get; init; }
    public int FactsDiscarded { get; init; }
    public int ChaptersCreated { get; init; }
    public string? Summary { get; init; }
}

public sealed record SubconsciousQueueStats
{
    public int Pending { get; init; }
    public int Leased { get; init; }
    public int Retry { get; init; }
    public int Completed { get; init; }
    public int DeadLetter { get; init; }
}
```

- [ ] **Step 2: Add queue abstraction**

```csharp
using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface ISubconsciousJobQueue
{
    Task<string> EnqueueAsync(SubconsciousJob job, CancellationToken ct = default);
    Task<SubconsciousJob?> DequeueAsync(string workerId, CancellationToken ct = default);
    Task MarkCompletedAsync(string jobId, SubconsciousJobResult result, CancellationToken ct = default);
    Task MarkRetryAsync(string jobId, string reason, TimeSpan delay, CancellationToken ct = default);
    Task MarkDeadLetterAsync(string jobId, string reason, CancellationToken ct = default);
    Task<SubconsciousQueueStats> GetStatsAsync(string? workspaceId = null, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Source\PuddingCore\PuddingCore.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 4: Persistent Job Entity and DbContext

**Files:**
- Create: `Source/PuddingMemoryEngine/Entities/SubconsciousJobEntity.cs`
- Modify: `Source/PuddingMemoryEngine/Data/MemoryDbContext.cs`
- Modify: `Source/PuddingMemoryEngine/Schema/init_memory.sql`

- [ ] **Step 1: Add entity**

```csharp
using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

public sealed class SubconsciousJobEntity
{
    [Key]
    [MaxLength(32)]
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(256)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string JobType { get; set; } = string.Empty;

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? AgentTemplateId { get; set; }

    [MaxLength(32)]
    public string? SourceEventId { get; set; }

    [MaxLength(128)]
    public string? SourceEventType { get; set; }

    public string PayloadJson { get; set; } = "{}";
    public int Priority { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    [MaxLength(64)]
    public string? LeaseOwner { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public DateTime AvailableAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
    public string? ResultJson { get; set; }
}
```

- [ ] **Step 2: Register DbSet and indexes**

Add to `MemoryDbContext`:

```csharp
public DbSet<SubconsciousJobEntity> SubconsciousJobs => Set<SubconsciousJobEntity>();
```

Add in `OnModelCreating`:

```csharp
modelBuilder.Entity<SubconsciousJobEntity>(entity =>
{
    entity.ToTable("SubconsciousJobs");
    entity.HasIndex(e => e.IdempotencyKey).IsUnique();
    entity.HasIndex(e => new { e.Status, e.Priority, e.AvailableAtUtc, e.CreatedAtUtc })
          .HasDatabaseName("IX_SubconsciousJobs_Dequeue");
    entity.HasIndex(e => new { e.WorkspaceId, e.Status, e.CreatedAtUtc })
          .HasDatabaseName("IX_SubconsciousJobs_WorkspaceStatus");
    entity.HasIndex(e => e.SessionId)
          .HasDatabaseName("IX_SubconsciousJobs_Session");
});
```

- [ ] **Step 3: Add SQL schema**

Append the DDL from ADR-027 section 5 to `Source/PuddingMemoryEngine/Schema/init_memory.sql`.

- [ ] **Step 4: Build**

Run: `dotnet build Source\PuddingMemoryEngine\PuddingMemoryEngine.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 5: SQLite Subconscious Job Queue

**Files:**
- Create: `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`
- Test: `Source/PuddingCoreTests/Memory/SubconsciousJobQueueTests.cs`

- [ ] **Step 1: Write idempotent enqueue test**

```csharp
[TestMethod]
public async Task EnqueueAsync_DoesNotDuplicate_Active_IdempotencyKey()
{
    var queue = CreateQueue();
    var job = NewJob("same-key");

    var first = await queue.EnqueueAsync(job);
    var second = await queue.EnqueueAsync(job with { JobId = Guid.NewGuid().ToString("N") });

    Assert.AreEqual(first, second);
    var stats = await queue.GetStatsAsync("w1");
    Assert.AreEqual(1, stats.Pending);
}
```

- [ ] **Step 2: Implement queue**

Implementation requirements:

- `EnqueueAsync` checks `IdempotencyKey`; returns existing non-terminal `JobId`.
- `DequeueAsync` selects pending/retry job where `AvailableAtUtc <= now` and no active job is leased for the same workspace.
- `MarkRetryAsync` increments retry count and sets `AvailableAtUtc`.
- If retry count exceeds `MaxRetries`, mark `dead_letter`.
- `GetStatsAsync` counts statuses.

- [ ] **Step 3: Run tests**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter SubconsciousJobQueueTests --logger "console;verbosity=minimal"`

Expected: queue tests pass.

---

## Task 6: Subconscious Event Handler

**Files:**
- Create: `Source/PuddingRuntime/Services/Events/SubconsciousEventHandler.cs`
- Test: `Source/PuddingCoreTests/Events/SubconsciousEventHandlerTests.cs`

- [ ] **Step 1: Write event-to-job test**

```csharp
[TestMethod]
public async Task HandleAsync_Creates_ConsolidateSession_Job()
{
    var queue = new RecordingSubconsciousJobQueue();
    var handler = new SubconsciousEventHandler(queue, NullLogger<SubconsciousEventHandler>.Instance);

    var payload = new AgentLoopCompletedPayload
    {
        SessionId = "s1",
        WorkspaceId = "w1",
        AgentId = "a1",
        AgentTemplateId = "tpl1",
        UserMessage = "u",
        FinalMessage = "done",
        StopReason = "Done",
    };

    var ok = await handler.HandleAsync(new InternalEvent
    {
        EventId = "evt1",
        Type = "agent.loop.completed",
        WorkspaceId = "w1",
        AgentId = "a1",
        SessionId = "s1",
        Payload = payload,
    }, CancellationToken.None);

    Assert.IsTrue(ok);
    Assert.AreEqual(SubconsciousJobTypes.ConsolidateSession, queue.Enqueued[0].JobType);
}
```

- [ ] **Step 2: Implement handler**

```csharp
public sealed class SubconsciousEventHandler : IEventHandler
{
    private readonly ISubconsciousJobQueue _queue;
    private readonly ILogger<SubconsciousEventHandler> _logger;

    public string EventTypePattern => "agent.loop.completed";
    public bool SupportsInterruption => false;

    public SubconsciousEventHandler(
        ISubconsciousJobQueue queue,
        ILogger<SubconsciousEventHandler> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<AgentLoopCompletedPayload>(
                JsonSerializer.Serialize(evt.Payload));

            if (payload is null)
                return false;

            var rangeOrContent = payload.ConversationHash
                ?? $"{payload.MessageRangeStartId}:{payload.MessageRangeEndId}:{payload.UserMessage}:{payload.FinalMessage}";
            var rangeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rangeOrContent)));
            var idempotencyKey = string.Join(":",
                payload.WorkspaceId,
                payload.SessionId,
                payload.AgentId,
                rangeHash,
                SubconsciousJobTypes.ConsolidateSession);

            await _queue.EnqueueAsync(new SubconsciousJob
            {
                IdempotencyKey = idempotencyKey,
                JobType = SubconsciousJobTypes.ConsolidateSession,
                WorkspaceId = payload.WorkspaceId,
                SessionId = payload.SessionId,
                AgentId = payload.AgentId,
                AgentTemplateId = payload.AgentTemplateId,
                SourceEventId = evt.EventId,
                SourceEventType = evt.Type,
                PayloadJson = JsonSerializer.Serialize(payload),
                Priority = (int)evt.Priority,
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SubconsciousEventHandler] Failed event={EventId} type={Type}",
                evt.EventId,
                evt.Type);
            return false;
        }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter SubconsciousEventHandlerTests --logger "console;verbosity=minimal"`

Expected: handler tests pass.

---

## Task 7: Idle Signal

**Files:**
- Create: `Source/PuddingCore/Abstractions/IRuntimeIdleSignal.cs`
- Create: `Source/PuddingRuntime/Services/Background/RuntimeIdleSignal.cs`

- [ ] **Step 1: Add abstraction**

```csharp
namespace PuddingCode.Abstractions;

public interface IRuntimeIdleSignal
{
    Task<RuntimeIdleSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed record RuntimeIdleSnapshot
{
    public required int ActiveSessions { get; init; }
    public required int ActiveLlmCalls { get; init; }
    public required int PendingUrgentEvents { get; init; }
    public required int PendingImportantEvents { get; init; }
    public required double CpuLoadHint { get; init; }
    public required bool IsIdleEnoughForBackgroundLlm { get; init; }
}
```

- [ ] **Step 2: Add conservative implementation**

Initial implementation may return idle when event queue has no urgent or important pending events. Keep CPU and active LLM counts as hints until the runtime activity counters are wired.

- [ ] **Step 3: Build**

Run: `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 8: Worker Persistent Queue Consumption

**Files:**
- Modify: `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- Test: `Source/PuddingCoreTests/Memory/SubconsciousWorkerServiceTests.cs`

- [ ] **Step 1: Write busy-skip test**

```csharp
[TestMethod]
public async Task ExecuteAsync_DoesNotLease_WhenRuntimeBusy()
{
    var queue = new RecordingSubconsciousJobQueue();
    var idle = new FixedIdleSignal(false);
    var worker = CreateWorker(queue, idle);

    await worker.RunOneTickForTestAsync(CancellationToken.None);

    Assert.AreEqual(0, queue.DequeueCalls);
}
```

- [ ] **Step 2: Modify worker dependencies**

Constructor adds:

```csharp
ISubconsciousJobQueue jobQueue,
IRuntimeIdleSignal idleSignal
```

Keep the existing `Channel<ConsolidationJob>` constructor path only as a temporary compatibility overload if needed by current DI.

- [ ] **Step 3: Implement one job execution path**

Worker loop:

```text
snapshot = idleSignal.GetSnapshotAsync()
if !snapshot.IsIdleEnoughForBackgroundLlm:
    delay and continue
job = jobQueue.DequeueAsync(workerId)
if job is null:
    delay and continue
deserialize AgentLoopCompletedPayload
resolve memory config
call orchestrator.ConsolidateAsync(consolidationJob, mode, memoryLlmConfig)
mark completed
catch transient -> retry
catch permanent -> dead_letter
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter SubconsciousWorkerServiceTests --logger "console;verbosity=minimal"`

Expected: worker tests pass.

---

## Task 9: Dependency Injection

**Files:**
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Register services**

Add:

```csharp
services.AddSingleton<IAgentLoopHook, AgentLoopEventPublisherHook>();
services.AddSingleton<IEventHandler, SubconsciousEventHandler>();
services.AddSingleton<ISubconsciousJobQueue, SubconsciousJobQueue>();
services.AddSingleton<IRuntimeIdleSignal, RuntimeIdleSignal>();
```

Keep existing `SubconsciousConsolidationHook` registration only behind a compatibility flag:

```text
PUDDING_SUBCONSCIOUS_LEGACY_CHANNEL_HOOK=true
```

- [ ] **Step 2: Build app hosts**

Run:

```powershell
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: both builds succeed.

---

## Task 10: Verification

**Commands:**

```powershell
dotnet build Source\PuddingCore\PuddingCore.csproj --no-restore --nologo
dotnet build Source\PuddingMemoryEngine\PuddingMemoryEngine.csproj --no-restore --nologo
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "AgentLoopEventPublisherHookTests|SubconsciousEventHandlerTests|SubconsciousJobQueueTests|SubconsciousWorkerServiceTests" --logger "console;verbosity=minimal"
```

Expected: all builds and focused tests pass.

Manual smoke:

```text
1. Start PuddingAgent.
2. Send one normal chat message that reaches done.
3. Confirm event_queue contains/completed agent.loop.completed.
4. Confirm SubconsciousJobs contains one consolidate-session job.
5. Wait for idle worker.
6. Confirm job completed or retried with diagnostic error.
7. Confirm SubconsciousJobLogs references the session.
```
