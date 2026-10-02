# Agent-to-Agent Message Fabric V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the observable V1 path where one agent sends a public room-visible message to another agent, a direct delivery is persisted, and a subscription-driven dispatcher claims and executes the delivery when the target agent is idle.

**Architecture:** Keep `RoomMessage` as the user-visible room transcript and `MessageDelivery` as the durable execution fact. Extend the existing Message Fabric instead of replacing it: add atomic delivery claim/update operations, add a small agent availability abstraction, add a hosted dispatcher subscribed to `message.deliver`, expose `list_agents`, adjust `send_message` defaults, and inject a short workspace-agent roster into context assembly.

**Tech Stack:** .NET 10, EF Core SQLite, MSTest, existing `PuddingCore`, `PuddingPlatform`, `PuddingRuntime`, `PuddingAgent`, internal event bus, existing runtime activity telemetry.

---

## 2026-06-07 Revision: Implementation Baseline

This plan was partially executed and then revised. Use this section as the current source of truth before following the original task list below.

Completed implementation commits:

- `7c7a185 feat: add durable message delivery claims`
- `6c9eafd feat: add visible agent messaging tools`
- `a371174 feat: provide workspace agent roster`
- `62e0176 feat: dispatch agent message deliveries`
- `1946d2c feat: inject workspace agent roster context`
- `deceeec fix: upgrade message delivery claim schema safely`

Actual implemented files:

- `Source/PuddingCore/Models/MessageFabricModels.cs`
  - Added `MessageDeliveryStatuses.Retrying`, `MessageDeliveryStatuses.DeadLetter`, `MessageClaimRequest`, and delivery claim fields on `MessageInboxItem`.
- `Source/PuddingCore/Abstractions/IMessageInbox.cs`
  - Added `ClaimNextAsync`, execution-scoped `AckAsync`, `RetryAsync`, and `DeadLetterAsync`.
- `Source/PuddingCore/Abstractions/IAgentRosterProvider.cs`
  - Defines `IAgentRosterProvider` and `AgentRosterItem`.
- `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
  - Implements durable claim, ack, retry, and dead-letter transitions.
- `Source/PuddingPlatform/Services/MessageFabric/MessageFabricSchemaBootstrapper.cs`
  - Adds old-SQLite upgrade DDL for delivery claim columns before claim indexes are created.
- `Source/PuddingPlatform/Services/WorkspaceAgentRosterProvider.cs`
  - Builds the workspace agent roster from `WorkspaceAgentFileService` / `IWorkspaceAgentCatalog`.
- `Source/PuddingRuntime/Tools/BuiltIns/Messaging/SendMessageTool.cs`
  - Defaults message visibility to `public` and writes `intent` / `requires_response` metadata.
- `Source/PuddingRuntime/Tools/BuiltIns/Messaging/ListAgentsTool.cs`
  - Lists messageable workspace agents.
- `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`
  - Subscribes to `message.deliver`, claims durable agent deliveries, dispatches runtime execution, acks on success, retries on failure.
- `Source/PuddingRuntime/Services/WorkspaceAgentsContextBuilder.cs`
  - Formats the context layer for workspace agents.
- `Source/PuddingRuntime/Services/ContextPipeline.cs`
  - Injects `L0-AGENTS-ROSTER` after environment context and before tools.
- `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
  - Skips `message.deliver` because automatic agent delivery is owned by `MessageDeliveryDispatcher`.
- `Source/PuddingAgent/Program.cs`
  - Registers roster provider, message dispatcher, `list_agents`, and workspace agents context builder.

Verified tests:

- `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --logger "console;verbosity=minimal" --no-restore`
- `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricSchemaBootstrapperTests --logger "console;verbosity=minimal" --no-restore`
- `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter WorkspaceAgentRosterProviderTests --logger "console;verbosity=minimal" --no-restore`
- `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageToolsTests --logger "console;verbosity=minimal" --no-restore`
- `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore`
- `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter WorkspaceAgentsContextBuilderTests --logger "console;verbosity=minimal" --no-restore`

Known build blocker unrelated to this feature:

```text
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore
```

fails because `external/github.hyfree.GM/github.hyfree.GM/github.hyfree.GM.csproj` is missing, causing `GatewayAuthService` to fail resolving `github.*` and `GMService`.

Important corrections to the original plan:

- The implemented dispatcher is `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`, not `Source/PuddingAgent/Services/Events/AgentInboxDispatcher.cs`.
- The V1.0 dispatcher currently reacts to `message.deliver` and claims immediately. Idle-state gating and `agent.availability.changed` wakeup remain V1.1 work.
- The implemented roster provider is `Source/PuddingPlatform/Services/WorkspaceAgentRosterProvider.cs`, not `Source/PuddingPlatform/Services/MessageFabric/WorkspaceAgentRosterProvider.cs`.
- A separate `IAgentAvailabilityService` was not introduced in V1.0.
- Context roster formatting was factored into `WorkspaceAgentsContextBuilder` instead of embedding all formatting inside `ContextPipeline`.
- Dead-letter API exists, but the dispatcher currently retries failures and does not enforce a retry threshold.
- The old-SQLite schema upgrade must add `attempt_count`, `available_at`, `lease_until`, `claimed_by_execution_id`, and `last_error` before creating indexes that reference those columns.

---

## Revised Remaining Implementation Plan

The first slice is running. The remaining work should focus on making it correct under real execution pressure before adding collaboration guardrails.

### Task 7: Idle-Aware Delivery Dispatch

**Goal:** Ensure an agent-to-agent message does not start a target agent execution while that target is already busy or unavailable.

**Files:**
- Create: `Source/PuddingCore/Abstractions/IAgentExecutionAvailabilityProvider.cs`
- Modify: `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingRuntimeTests/Services/MessageDeliveryDispatcherTests.cs`

- [x] **Step 1: Add failing dispatcher tests**

Add tests:

```csharp
[TestMethod]
public async Task MessageDeliver_ForBusyAgent_DoesNotClaimDelivery()
{
    var inbox = new RecordingInbox();
    var runtime = new RecordingRuntimeDispatcher();
    var availability = new RecordingAvailabilityProvider("busy");
    var dispatcher = CreateDispatcher(inbox, runtime, availability);

    await dispatcher.HandleAsync(MessageDeliverEvent("d1", "agent-b"), CancellationToken.None);

    Assert.AreEqual(0, inbox.ClaimRequests.Count);
    Assert.AreEqual(0, runtime.Dispatches.Count);
}

[TestMethod]
public async Task MessageDeliver_ForIdleAgent_ClaimsAndExecutesDelivery()
{
    var inbox = new RecordingInbox { ClaimResult = InboxItem("d1", "agent-b") };
    var runtime = new RecordingRuntimeDispatcher { Result = RuntimeDispatchResult.Success("exec-1") };
    var availability = new RecordingAvailabilityProvider("idle");
    var dispatcher = CreateDispatcher(inbox, runtime, availability);

    await dispatcher.HandleAsync(MessageDeliverEvent("d1", "agent-b"), CancellationToken.None);

    Assert.AreEqual(1, inbox.ClaimRequests.Count);
    Assert.AreEqual(1, runtime.Dispatches.Count);
    Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
}
```

Expected before implementation: the busy-agent test fails because V1.0 claims immediately.

- [x] **Step 2: Add availability abstraction**

Create:

```csharp
namespace PuddingCode.Abstractions;

public interface IAgentExecutionAvailabilityProvider
{
    Task<AgentExecutionAvailability> GetAsync(string workspaceId, string agentId, CancellationToken ct);
}

public sealed record AgentExecutionAvailability(
    string WorkspaceId,
    string AgentId,
    string Status,
    string? CurrentExecutionId,
    string? CurrentTask)
{
    public bool CanStartMessageDelivery =>
        string.Equals(Status, "idle", StringComparison.OrdinalIgnoreCase);
}
```

Add a fallback implementation for single-process V1 that returns `idle` when no richer execution state source is available. The fallback keeps current behavior but makes the boundary explicit.

- [x] **Step 3: Gate dispatcher before claim**

In `MessageDeliveryDispatcher.HandleAsync`, before `ClaimNextAsync`:

```csharp
var availabilityProvider = scope.ServiceProvider.GetService<IAgentExecutionAvailabilityProvider>();
if (availabilityProvider is not null)
{
    var availability = await availabilityProvider.GetAsync(payload.WorkspaceId, payload.Target.Id, ct);
    if (!availability.CanStartMessageDelivery)
    {
        _logger.LogInformation(
            "[MessageDeliveryDispatcher] Skipped delivery target={AgentId} status={Status} delivery={DeliveryId}",
            payload.Target.Id,
            availability.Status,
            payload.DeliveryId);
        return;
    }
}
```

- [x] **Step 4: Verify and commit**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --logger "console;verbosity=minimal" --no-restore
```

Expected: PASS.

Implementation note, 2026-06-07: Task 7 is implemented. `IAgentExecutionAvailabilityProvider`
now gates `MessageDeliveryDispatcher` before durable claim; the default runtime provider returns
`idle` to preserve V1.0 behavior until a richer runtime execution-state source is wired in.
Focused dispatcher and message fabric store tests passed.

Commit:

```powershell
git add Source\PuddingCore\Abstractions Source\PuddingRuntime\Services\Messaging Source\PuddingRuntimeTests\Services\MessageDeliveryDispatcherTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: gate message delivery dispatch by agent availability"
```

### Task 8: Recovery Subscription and Lease Repair

**Goal:** Make queued/retrying deliveries resume when an agent becomes idle, and make expired delivering leases recover after process interruption.

**Files:**
- Modify: `Source/PuddingCore/Abstractions/IMessageInbox.cs`
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
- Modify: `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricStoreTests.cs`
- Test: `Source/PuddingRuntimeTests/Services/MessageDeliveryDispatcherTests.cs`

- [x] **Step 1: Add store recovery contract**

Add:

```csharp
Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default);
```

Expected behavior: deliveries with `status=delivering` and `lease_until < now` move to `retrying`, clear `claimed_by_execution_id`, set `available_at=now`, and keep `attempt_count`.

- [x] **Step 2: Subscribe to availability events**

In `MessageDeliveryDispatcher.StartAsync`, subscribe to:

```csharp
message.deliver
agent.availability.changed
```

On `agent.availability.changed` where status becomes `idle`, call the same claim/execute path for that agent.

- [x] **Step 3: Add periodic recovery**

Add a hosted loop or timer inside the dispatcher:

```text
every 10s: try queued/retrying deliveries for idle agents if target is known
every 60s: RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow)
```

Keep the scan conservative for V1.1: only agent-target deliveries in the current workspace, ordered by priority and created time.

Implementation note, 2026-06-07: Task 8 is implemented. `IMessageInbox` now exposes
`RecoverExpiredLeasesAsync`; `MessageFabricStore` requeues expired `delivering` leases as
`retrying` without incrementing `attempt_count`; `MessageDeliveryDispatcher` subscribes to
`agent.availability.changed` and claims when the target becomes `idle`. The periodic loop recovers
expired leases and retries only in-process known agent targets to avoid adding a broad scan API in
V1.1. Focused dispatcher and message fabric store tests passed.

- [x] **Step 4: Verify and commit**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source\PuddingCore\Abstractions\IMessageInbox.cs Source\PuddingPlatform\Services\MessageFabric\MessageFabricStore.cs Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs Source\PuddingPlatformTests\Services\MessageFabric\MessageFabricStoreTests.cs Source\PuddingRuntimeTests\Services\MessageDeliveryDispatcherTests.cs
git commit -m "feat: recover queued agent message deliveries"
```

### Task 9: Retry Threshold and Dead Letter Policy

**Goal:** Keep V1 simple but prevent infinite retry churn.

**Files:**
- Modify: `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`
- Test: `Source/PuddingRuntimeTests/Services/MessageDeliveryDispatcherTests.cs`

- [x] **Step 1: Add failing retry threshold test**

Add:

```csharp
[TestMethod]
public async Task RuntimeFailure_AfterThirdAttempt_DeadLettersDelivery()
{
    var inbox = new RecordingInbox { ClaimResult = InboxItem("d1", "agent-b", attemptCount: 3) };
    var runtime = new RecordingRuntimeDispatcher { Result = RuntimeDispatchResult.Failed("boom") };
    var dispatcher = CreateDispatcher(inbox, runtime, new RecordingAvailabilityProvider("idle"));

    await dispatcher.HandleAsync(MessageDeliverEvent("d1", "agent-b"), CancellationToken.None);

    Assert.AreEqual("d1", inbox.DeadLettered[0].DeliveryId);
    Assert.AreEqual(0, inbox.Retried.Count);
}
```

- [x] **Step 2: Implement threshold**

In dispatcher failure path:

```csharp
if (claimed.AttemptCount >= 3)
{
    await inbox.DeadLetterAsync(claimed.DeliveryId, executionId, error, ct);
    return;
}

await inbox.RetryAsync(claimed.DeliveryId, executionId, error, DateTimeOffset.UtcNow.AddSeconds(30), ct);
```

- [x] **Step 3: Verify and commit**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
```

Implementation note, 2026-06-07: Task 9 is implemented. Failed runtime delivery attempts now
retry below attempt 3 and dead-letter at attempt 3 or later. Focused dispatcher tests passed.

Commit:

```powershell
git add Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs Source\PuddingRuntimeTests\Services\MessageDeliveryDispatcherTests.cs
git commit -m "feat: dead-letter failed agent message deliveries"
```

### Task 10: Observability Completion

**Goal:** Make V2 design data-driven by recording message send, delivery transition, dispatcher decision, and runtime result.

**Files:**
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
- Modify: `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`
- Test: existing focused message fabric and dispatcher tests.

- [x] **Step 1: Add structured logs**

Add consistent log event names:

```text
[MessageFabric] send
[MessageFabric] delivery_transition
[MessageDeliveryDispatcher] decision
[MessageDeliveryDispatcher] execution_result
```

Each log should include:

```text
workspace_id
room_id
message_id
delivery_id
target_kind
target_id
status
attempt_count
execution_id
correlation_id
causation_id
```

- [x] **Step 2: Add telemetry metric sink where available**

Use existing telemetry abstractions only if they are already registered in the touched service. Do not introduce a new metrics subsystem in this pass.

Implementation note: completed with existing `ILogger` paths only; no telemetry metric sink was already present in the touched services, so this pass intentionally did not add a new metrics subsystem. Structured log events now cover send, delivery transition, dispatcher decision, and runtime execution result with the shared message fabric field set.

- [ ] **Step 3: Verify and commit**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabric --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
```

Verification note, 2026-06-08: focused MessageFabric and MessageDeliveryDispatcher tests passed, and `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore -o temp\puddingagent-build-check -p:UseSharedCompilation=false` passed with existing warnings. Commit is intentionally pending until requested.

Commit:

```powershell
git add Source\PuddingPlatform\Services\MessageFabric Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs
git commit -m "chore: complete agent message observability"
```

---

## Original Plan Archive

The original task list below is kept as an execution archive. Do not follow it mechanically without first checking the revision section above.

## File Map

- Modify: `Source/PuddingCore/Models/MessageFabricModels.cs`
  - Add `Retrying` and `DeadLetter` delivery statuses.
  - Add `MessageClaimRequest`.
  - Add optional delivery lease fields to `MessageInboxItem`.
- Modify: `Source/PuddingCore/Abstractions/IMessageInbox.cs`
  - Add `ClaimNextAsync`, `AckAsync(... executionId ...)`, `RetryAsync`, and `DeadLetterAsync`.
- Modify: `Source/PuddingPlatform/Data/Entities/MessageDeliveryEntity.cs`
  - Add `available_at`, `lease_until`, and `claimed_by_execution_id`.
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
  - Add dequeue-oriented indexes.
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricSchemaBootstrapper.cs`
  - Add idempotent `ALTER TABLE` statements for existing SQLite databases.
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
  - Implement atomic claim and terminal/retry state updates.
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`
  - Persist intent/requires-response metadata and record message send telemetry/logs.
- Create: `Source/PuddingPlatform/Services/MessageFabric/IAgentAvailabilityService.cs`
  - Defines `AgentAvailability` and the read interface.
- Create: `Source/PuddingPlatform/Services/MessageFabric/AgentAvailabilityService.cs`
  - Uses existing agent status projection to map running/failed/offline to V1 availability.
- Create: `Source/PuddingAgent/Services/Events/AgentInboxDispatcher.cs`
  - Hosted service that subscribes to `message.deliver`, checks availability, claims, executes, and updates delivery state.
- Modify: `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
  - Stop executing `message.deliver` directly; let the dispatcher own automatic message deliveries.
- Modify: `Source/PuddingAgent/Program.cs`
  - Register availability service, dispatcher, and new tool.
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Messaging/SendMessageTool.cs`
  - Default direct messages to public visibility and include intent/requires-response metadata.
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Messaging/ListAgentsTool.cs`
  - Agent-facing tool for current workspace/room roster.
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Messaging/ReceiveMessagesTool.cs`
  - Update test double compatibility after `IMessageInbox` grows.
- Modify: `Source/PuddingRuntime/Services/ContextPipeline.cs`
  - Add a short `WORKSPACE AGENTS` roster layer between environment and tools.
- Modify: `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
  - Ensure facade path can carry agent roster context when it bypasses the full pipeline.
- Tests:
  - Modify: `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricStoreTests.cs`
  - Create: `Source/PuddingPlatformTests/Services/MessageFabric/AgentAvailabilityServiceTests.cs`
  - Create: `Source/PuddingAgentTests/Services/Events/AgentInboxDispatcherTests.cs` if the test project exists; otherwise put dispatcher unit tests in `Source/PuddingRuntimeTests` only if dependencies can be isolated. If neither location is available, add a narrow integration-style test in `Source/PuddingPlatformTests` around the store and leave dispatcher verification to build/log smoke tests.
  - Modify: `Source/PuddingRuntimeTests/Tools/MessageToolsTests.cs`
  - Modify or create context pipeline tests in `Source/PuddingRuntimeTests`.

---

## Task 1: Durable Delivery Claim State

**Files:**
- Modify: `Source/PuddingCore/Models/MessageFabricModels.cs`
- Modify: `Source/PuddingCore/Abstractions/IMessageInbox.cs`
- Modify: `Source/PuddingPlatform/Data/Entities/MessageDeliveryEntity.cs`
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricSchemaBootstrapper.cs`
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricStoreTests.cs`

- [ ] **Step 1: Write failing store claim tests**

Add these tests to `MessageFabricStoreTests`:

```csharp
[TestMethod]
public async Task ClaimNextAsync_MarksDeliveryDelivering_WithLeaseAndExecutionId()
{
    using var temp = TemporaryDirectory.Create();
    var options = CreateOptions(temp.Path);

    await using var db = new PlatformDbContext(options);
    await db.Database.EnsureCreatedAsync();
    var store = new MessageFabricStore(db);
    await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

    var claimed = await store.ClaimNextAsync(new MessageClaimRequest
    {
        Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
        WorkspaceId = "default",
        RoomId = "room-default",
        ExecutionId = "exec-1",
        LeaseDuration = TimeSpan.FromMinutes(5),
    }, CancellationToken.None);

    Assert.IsNotNull(claimed);
    Assert.AreEqual("d1", claimed!.DeliveryId);
    Assert.AreEqual(MessageDeliveryStatuses.Delivering, claimed.Status);
    Assert.AreEqual("exec-1", claimed.ClaimedByExecutionId);
    Assert.IsNotNull(claimed.LeaseUntil);

    var second = await store.ClaimNextAsync(new MessageClaimRequest
    {
        Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
        WorkspaceId = "default",
        ExecutionId = "exec-2",
    }, CancellationToken.None);

    Assert.IsNull(second);
}

[TestMethod]
public async Task RetryAsync_RequeuesDeliveryAfterAvailableAt_AndDeadLetterAsyncStopsClaim()
{
    using var temp = TemporaryDirectory.Create();
    var options = CreateOptions(temp.Path);

    await using var db = new PlatformDbContext(options);
    await db.Database.EnsureCreatedAsync();
    var store = new MessageFabricStore(db);
    await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

    var claimed = await store.ClaimNextAsync(new MessageClaimRequest
    {
        Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
        WorkspaceId = "default",
        ExecutionId = "exec-1",
    }, CancellationToken.None);

    Assert.IsNotNull(claimed);
    await store.RetryAsync(claimed!.DeliveryId, "exec-1", "transient", DateTimeOffset.UtcNow.AddMilliseconds(-1), CancellationToken.None);

    var retry = await store.ClaimNextAsync(new MessageClaimRequest
    {
        Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
        WorkspaceId = "default",
        ExecutionId = "exec-2",
    }, CancellationToken.None);

    Assert.IsNotNull(retry);
    Assert.AreEqual(2, retry!.AttemptCount);

    await store.DeadLetterAsync(retry.DeliveryId, "exec-2", "terminal", CancellationToken.None);

    var afterDeadLetter = await store.ClaimNextAsync(new MessageClaimRequest
    {
        Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
        WorkspaceId = "default",
        ExecutionId = "exec-3",
    }, CancellationToken.None);

    Assert.IsNull(afterDeadLetter);
}
```

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --no-restore --logger "console;verbosity=minimal"
```

Expected: FAIL because `MessageClaimRequest`, `ClaimNextAsync`, `RetryAsync`, and `DeadLetterAsync` do not exist.

- [ ] **Step 3: Add core claim contracts**

In `MessageFabricModels.cs`, add statuses:

```csharp
public const string Delivering = "delivering";
public const string Retrying = "retrying";
public const string DeadLetter = "dead_letter";
```

Add:

```csharp
public sealed record MessageClaimRequest
{
    public required MessageAddress Endpoint { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public required string ExecutionId { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}
```

Extend `MessageInboxItem`:

```csharp
public int AttemptCount { get; init; }
public long? AvailableAt { get; init; }
public long? LeaseUntil { get; init; }
public string? ClaimedByExecutionId { get; init; }
public string? LastError { get; init; }
```

- [ ] **Step 4: Extend `IMessageInbox`**

Change `IMessageInbox` to:

```csharp
public interface IMessageInbox
{
    Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default);

    Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default);

    Task AckAsync(string deliveryId, CancellationToken ct = default);

    Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default);

    Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct = default);

    Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default);
}
```

Keep the old `AckAsync(string)` overload for `receive_messages` compatibility.

- [ ] **Step 5: Extend delivery entity and schema**

Add properties to `MessageDeliveryEntity`:

```csharp
[Column("available_at")]
public long? AvailableAt { get; set; }

[Column("lease_until")]
public long? LeaseUntil { get; set; }

[MaxLength(128), Column("claimed_by_execution_id")]
public string? ClaimedByExecutionId { get; set; }
```

Add indexes in `PlatformDbContext`:

```csharp
e.HasIndex(d => new { d.WorkspaceId, d.TargetKind, d.TargetId, d.Status, d.AvailableAt, d.Priority, d.CreatedAt });
e.HasIndex(d => d.LeaseUntil);
```

Add idempotent DDL strings to `MessageFabricSchemaBootstrapper`:

```sql
ALTER TABLE message_deliveries ADD COLUMN available_at INTEGER;
ALTER TABLE message_deliveries ADD COLUMN lease_until INTEGER;
ALTER TABLE message_deliveries ADD COLUMN claimed_by_execution_id TEXT;
CREATE INDEX IF NOT EXISTS idx_message_deliveries_claim ON message_deliveries(workspace_id, target_kind, target_id, status, available_at, priority, created_at);
CREATE INDEX IF NOT EXISTS idx_message_deliveries_lease_until ON message_deliveries(lease_until);
```

Wrap `ALTER TABLE` execution so duplicate-column failures are ignored only when the message contains `duplicate column name`.

- [ ] **Step 6: Implement store state transitions**

In `MessageFabricStore`, implement:

```csharp
public async Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var leaseUntil = DateTimeOffset.UtcNow.Add(request.LeaseDuration).ToUnixTimeMilliseconds();

    await using var transaction = await _db.Database.BeginTransactionAsync(ct);
    var delivery = await _db.MessageDeliveries
        .Where(d => d.TargetKind == request.Endpoint.Kind && d.TargetId == request.Endpoint.Id)
        .Where(d => string.IsNullOrWhiteSpace(request.WorkspaceId) || d.WorkspaceId == request.WorkspaceId)
        .Where(d => string.IsNullOrWhiteSpace(request.RoomId) || d.RoomId == request.RoomId)
        .Where(d => d.Status == MessageDeliveryStatuses.Queued || d.Status == MessageDeliveryStatuses.Retrying)
        .Where(d => d.AvailableAt == null || d.AvailableAt <= now)
        .OrderByDescending(d => d.Priority)
        .ThenBy(d => d.CreatedAt)
        .FirstOrDefaultAsync(ct);

    if (delivery is null)
        return null;

    delivery.Status = MessageDeliveryStatuses.Delivering;
    delivery.AttemptCount += 1;
    delivery.LeaseUntil = leaseUntil;
    delivery.ClaimedByExecutionId = request.ExecutionId;
    delivery.UpdatedAt = now;
    await _db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    return (await BuildInboxItemsAsync([delivery], ct)).SingleOrDefault();
}
```

Refactor the current `ListAsync` projection into a private helper such as `BuildInboxItemsAsync`.

Implement `AckAsync(deliveryId, executionId)`, `RetryAsync`, and `DeadLetterAsync` by checking the current `claimed_by_execution_id` before mutating when an execution id is supplied.

- [ ] **Step 7: Run tests and commit**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --no-restore --logger "console;verbosity=minimal"
```

Expected: PASS.

Commit:

```powershell
git add Source\PuddingCore\Models\MessageFabricModels.cs Source\PuddingCore\Abstractions\IMessageInbox.cs Source\PuddingPlatform\Data\Entities\MessageDeliveryEntity.cs Source\PuddingPlatform\Data\PlatformDbContext.cs Source\PuddingPlatform\Services\MessageFabric\MessageFabricSchemaBootstrapper.cs Source\PuddingPlatform\Services\MessageFabric\MessageFabricStore.cs Source\PuddingPlatformTests\Services\MessageFabric\MessageFabricStoreTests.cs
git commit -m "feat: add durable message delivery claims"
```

---

## Task 2: Message Tool Defaults and Agent Roster Tool

**Files:**
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Messaging/SendMessageTool.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Messaging/ListAgentsTool.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/MessageToolsTests.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Write failing tool tests**

In `MessageToolsTests`, update the existing direct send assertion:

```csharp
Assert.AreEqual(MessageVisibilities.Public, envelope.Visibility);
Assert.AreEqual("ask", envelope.Metadata["intent"]);
Assert.AreEqual("true", envelope.Metadata["requires_response"]);
```

Use parameters:

```csharp
["intent"] = "ask",
["requires_response"] = "true",
```

Add a test for explicit private override:

```csharp
[TestMethod]
public async Task SendMessageTool_Allows_PrivateVisibilityOverride()
{
    var fabric = new RecordingMessageSystem();
    var tool = new SendMessageTool(fabric);

    var result = await tool.ExecuteAsync(new SkillInvokeRequest
    {
        AgentInstanceId = "agent-a",
        WorkspaceId = "default",
        SessionId = "session-1",
        Input = "",
        Parameters = new Dictionary<string, string>
        {
            ["to"] = "agent:agent-b",
            ["content"] = "private diagnostic",
            ["visibility"] = MessageVisibilities.Private,
        },
    });

    Assert.IsTrue(result.Success, result.Error);
    Assert.AreEqual(MessageVisibilities.Private, fabric.Sent[0].Visibility);
}
```

Add `ListAgentsTool_ReturnsRosterFromProvider` using a small fake provider interface introduced in Step 3.

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageToolsTests --no-restore --logger "console;verbosity=minimal"
```

Expected: FAIL because current direct visibility defaults to private and `ListAgentsTool` does not exist.

- [ ] **Step 3: Add a roster provider abstraction**

Create an interface near `ListAgentsTool`:

```csharp
public interface IAgentRosterProvider
{
    Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
        string workspaceId,
        string roomId,
        bool includeBusy,
        bool includeFrozen,
        CancellationToken ct);
}

public sealed record AgentRosterItem(
    string AgentId,
    string DisplayName,
    string Address,
    string Status,
    bool AcceptsMessages,
    IReadOnlyList<string> Capabilities,
    string? CurrentTaskSummary);
```

The production provider can live in `PuddingPlatform.Services.MessageFabric` later, but the tool should depend only on this small abstraction.

- [ ] **Step 4: Update `SendMessageTool` defaults**

Change default visibility:

```csharp
var visibility = GetString(request, "visibility", MessageVisibilities.Public)!;
```

Add metadata:

```csharp
var intent = GetString(request, "intent", "inform")!;
var requiresResponse = GetString(
    request,
    "requires_response",
    intent is "ask" or "request_review" or "delegate" ? "true" : "false")!;
```

Set:

```csharp
Metadata = new Dictionary<string, string>
{
    ["source"] = "agent_tool",
    ["tool"] = SkillId,
    ["intent"] = intent,
    ["requires_response"] = requiresResponse,
}
```

- [ ] **Step 5: Implement `ListAgentsTool`**

Create `ListAgentsTool` with `SkillId => "list_agents"` and low permission. It should resolve:

```csharp
room_id = request.Parameters["room_id"] or "default"
include_busy = true by default
include_frozen = false by default
```

Output JSON:

```json
{
  "workspace_id": "default",
  "room_id": "default",
  "agents": []
}
```

- [ ] **Step 6: Register tool**

In `Program.cs`, add:

```csharp
builder.Services.AddPuddingAgentTool<ListAgentsTool>();
```

Also register the production `IAgentRosterProvider` in Task 3.

- [ ] **Step 7: Run tests and commit**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageToolsTests --no-restore --logger "console;verbosity=minimal"
```

Expected: PASS.

Commit:

```powershell
git add Source\PuddingRuntime\Tools\BuiltIns\Messaging Source\PuddingRuntimeTests\Tools\MessageToolsTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: expose agent messaging tools"
```

---

## Task 3: Agent Availability and Roster Provider

**Files:**
- Create: `Source/PuddingPlatform/Services/MessageFabric/IAgentAvailabilityService.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/AgentAvailabilityService.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/WorkspaceAgentRosterProvider.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/AgentAvailabilityServiceTests.cs`

- [ ] **Step 1: Write failing availability tests**

Create tests that verify:

```csharp
running -> busy
failed -> offline
idle -> idle
```

Use fake `IAgentRunProjectionService` returning `AgentStatusProjection` rows.

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter AgentAvailabilityServiceTests --no-restore --logger "console;verbosity=minimal"
```

Expected: FAIL because the services do not exist.

- [ ] **Step 3: Add availability contracts**

Create:

```csharp
public sealed record AgentAvailability(
    string WorkspaceId,
    string AgentId,
    string Status,
    string? SessionId,
    string? CurrentExecutionId,
    string? CurrentTaskSummary)
{
    public bool IsIdle => Status == AgentAvailabilityStatuses.Idle;
}

public static class AgentAvailabilityStatuses
{
    public const string Idle = "idle";
    public const string Busy = "busy";
    public const string WaitingApproval = "waiting_approval";
    public const string WaitingEvent = "waiting_event";
    public const string Frozen = "frozen";
    public const string Offline = "offline";
}

public interface IAgentAvailabilityService
{
    Task<AgentAvailability> GetAsync(string workspaceId, string agentId, CancellationToken ct);
}
```

- [ ] **Step 4: Implement availability mapping**

Use `IAgentRunProjectionService.GetWorkspaceAgentStatusesAsync(workspaceId, "single-user", ct)`.

Map:

```csharp
"running" -> busy
"failed" -> offline
"offline" -> offline
anything else -> idle
missing -> idle
```

- [ ] **Step 5: Implement production roster provider**

`WorkspaceAgentRosterProvider` should use `WorkspaceRoomParticipantProvider` plus `IAgentAvailabilityService`.

Return each agent as:

```csharp
new AgentRosterItem(
    participant.EndpointId,
    participant.DisplayName ?? participant.EndpointId,
    $"agent:{participant.EndpointId}",
    availability.Status,
    participant.CanReceive && availability.Status != AgentAvailabilityStatuses.Frozen,
    Array.Empty<string>(),
    availability.CurrentTaskSummary)
```

- [ ] **Step 6: Register services**

In `Program.cs`:

```csharp
builder.Services.AddScoped<IAgentAvailabilityService, AgentAvailabilityService>();
builder.Services.AddScoped<IAgentRosterProvider, WorkspaceAgentRosterProvider>();
```

- [ ] **Step 7: Run tests and commit**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter AgentAvailabilityServiceTests --no-restore --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS.

Commit:

```powershell
git add Source\PuddingPlatform\Services\MessageFabric Source\PuddingPlatformTests\Services\MessageFabric\AgentAvailabilityServiceTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: add agent availability roster"
```

---

## Task 4: Subscription-Driven Agent Inbox Dispatcher

**Files:**
- Create: `Source/PuddingAgent/Services/Events/AgentInboxDispatcher.cs`
- Modify: `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: focused unit tests where dependency isolation is practical.

- [ ] **Step 1: Write dispatcher behavior tests if practical**

If a `PuddingAgentTests` project exists, create `AgentInboxDispatcherTests`.

Test names:

```csharp
MessageDeliver_ForBusyAgent_DoesNotClaim
MessageDeliver_ForIdleAgent_ClaimsAndExecutes
ExecutionFailure_RetriesDelivery
```

Use fake `IMessageInbox`, `IAgentAvailabilityService`, and a fake execution adapter.

If no suitable test project exists, first add a small abstraction:

```csharp
public interface IAgentDeliveryExecutor
{
    Task<bool> ExecuteAsync(MessageInboxItem item, CancellationToken ct);
}
```

Then test the dispatcher decision class in `PuddingPlatformTests` without the full hosted service.

- [ ] **Step 2: Create dispatcher**

`AgentInboxDispatcher` implements `IHostedService`.

Dependencies:

```csharp
IInternalEventBus
IServiceScopeFactory
ILogger<AgentInboxDispatcher>
```

Subscribe to:

```csharp
message.deliver
agent.availability.changed
```

- [ ] **Step 3: Implement message.deliver handling**

On `message.deliver`:

```csharp
var payload = Deserialize<MessageDeliverEventPayload>(evt.Payload);
if (payload.Target.Kind != MessageEndpointKinds.Agent) return;
using var scope = _scopeFactory.CreateScope();
var availability = await scope.GetRequiredService<IAgentAvailabilityService>().GetAsync(payload.WorkspaceId, payload.Target.Id, ct);
if (!availability.IsIdle) { log skipped_busy; return; }
await TryClaimAndExecuteAsync(payload.WorkspaceId, payload.Target.Id, payload.RoomId, ct);
```

- [ ] **Step 4: Implement claim and execute**

Claim:

```csharp
var executionId = Guid.NewGuid().ToString("N");
var item = await inbox.ClaimNextAsync(new MessageClaimRequest
{
    Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = agentId, WorkspaceId = workspaceId },
    WorkspaceId = workspaceId,
    RoomId = roomId,
    ExecutionId = executionId,
}, ct);
```

Execute:

```csharp
var request = new RuntimeDispatchRequest
{
    SessionId = item.MessageId,
    WorkspaceId = item.WorkspaceId,
    AgentTemplateId = item.Target.Id,
    AgentInstanceId = item.Target.Id,
    MessageText = item.Content,
    MessageId = item.MessageId,
};
```

Call `AgentExecutionService.ExecuteStreamAsync(request, ct)` and consume frames until completion. On success call `AckAsync(item.DeliveryId, executionId)`. On exception call `RetryAsync` if `AttemptCount < 3`; otherwise `DeadLetterAsync`.

- [ ] **Step 5: Stop direct event handler execution for message.deliver**

In `AgentEventHandler.HandleAsync`, change the `message.deliver` branch to:

```csharp
if (evt.Type == "message.deliver")
{
    _logger.LogDebug("[AgentEventHandler] message.deliver is handled by AgentInboxDispatcher event={EventId}", evt.EventId);
    return true;
}
```

This avoids double execution because `EventIngressBridge` still routes all events into the priority event queue.

- [ ] **Step 6: Register dispatcher**

In `Program.cs` after `EventIngressBridge` registration:

```csharp
builder.Services.AddHostedService<AgentInboxDispatcher>();
```

- [ ] **Step 7: Build and commit**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS.

Commit:

```powershell
git add Source\PuddingAgent\Services\Events\AgentInboxDispatcher.cs Source\PuddingAgent\Services\Events\AgentEventHandler.cs Source\PuddingAgent\Program.cs
git commit -m "feat: dispatch agent inbox deliveries"
```

---

## Task 5: Workspace Agent Roster Context Layer

**Files:**
- Modify: `Source/PuddingRuntime/Services/ContextPipeline.cs`
- Modify: `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- Test: `Source/PuddingRuntimeTests` context pipeline tests.

- [ ] **Step 1: Write failing context test**

Add a test asserting the assembled prompt contains:

```text
--- LAYER: WORKSPACE AGENTS ---
Agent-to-agent messages are visible in the room timeline by default.
Use `list_agents` for fresh status before sending.
```

If constructor setup is too heavy, factor the formatting into an internal static method and test that method:

```csharp
var content = ContextPipeline.FormatWorkspaceAgentsLayer("default", "room-default", agents);
StringAssert.Contains(content, "agent:code-agent");
StringAssert.Contains(content, "visible in the room timeline");
```

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter ContextPipeline --no-restore --logger "console;verbosity=minimal"
```

Expected: FAIL because the roster layer is not present.

- [ ] **Step 3: Inject optional roster provider**

Add optional `IAgentRosterProvider? rosterProvider = null` to `ContextPipeline` constructor and store it in a private field.

- [ ] **Step 4: Add layer after environment**

After environment layer assembly, add:

```csharp
var rosterCtx = await BuildWorkspaceAgentsLayerAsync(request, ct);
var rosterTokens = EstimateTokens(rosterCtx);
AppendLayer(sb, rosterCtx);
usedBudget += rosterTokens;
layers.Add(new ContextLayerSnapshot("Workspace Agents", rosterTokens, (double)rosterTokens / totalBudget * 100));
layerInfos.Add(new ContextLayerInfo
{
    LayerName = "L0.5-WORKSPACE-AGENTS",
    TokenCount = rosterTokens,
    ContentPreview = BuildPreview(rosterCtx),
});
```

- [ ] **Step 5: Implement formatter**

Formatter output:

```text
--- LAYER: WORKSPACE AGENTS ---
Room: default
Agent-to-agent messages are visible in the room timeline by default.
Use `list_agents` for fresh status before sending.
Available message targets:
- `agent:code-agent` Code Agent, status=idle
Rules:
- Use `send_message` for concise coordination visible to the user.
- Use `visibility=private` for sensitive or diagnostic details.
- Do not repeatedly ping an agent that already has a queued or delivering request from you.
```

If provider is null or returns empty list, keep the rules and print `(No workspace agents discovered.)`.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter ContextPipeline --no-restore --logger "console;verbosity=minimal"
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: PASS.

Commit:

```powershell
git add Source\PuddingRuntime\Services\ContextPipeline.cs Source\PuddingRuntime\Services\ContextAssemblyService.cs Source\PuddingRuntimeTests
git commit -m "feat: inject workspace agents into context"
```

---

## Task 6: Observability and End-to-End Verification

**Files:**
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`
- Modify: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
- Modify: `Source/PuddingAgent/Services/Events/AgentInboxDispatcher.cs`
- Modify diagnostics docs if a user-facing query command is added.

- [ ] **Step 1: Add structured message send logging**

In `MessageSystem.SendAsync`, log:

```csharp
_logger.LogInformation(
    "[MessageFabric] send message={MessageId} workspace={WorkspaceId} room={RoomId} from={FromKind}:{FromId} audience={Audience} visibility={Visibility} intent={Intent} targets={TargetCount} contentLength={ContentLength}",
    envelope.MessageId,
    workspaceId,
    roomId,
    envelope.From.Kind,
    envelope.From.Id,
    envelope.Audience,
    envelope.Visibility,
    envelope.Metadata.TryGetValue("intent", out var intent) ? intent : "",
    plan.Deliveries.Count,
    envelope.Content.Length);
```

Add `ILogger<MessageSystem>` constructor dependency.

- [ ] **Step 2: Add delivery transition logging**

In `MessageFabricStore`, log status transitions for claim, ack, retry, and dead-letter:

```csharp
_logger.LogInformation(
    "[MessageFabric] delivery transition delivery={DeliveryId} message={MessageId} target={TargetKind}:{TargetId} old={OldStatus} new={NewStatus} attempt={AttemptCount} reason={Reason}",
    delivery.DeliveryId,
    delivery.MessageId,
    delivery.TargetKind,
    delivery.TargetId,
    oldStatus,
    delivery.Status,
    delivery.AttemptCount,
    reason);
```

Add `ILogger<MessageFabricStore>?` optional constructor parameter so existing tests can instantiate with just `PlatformDbContext`.

- [ ] **Step 3: Add dispatcher decision logging**

In `AgentInboxDispatcher`, log:

```csharp
"[AgentInboxDispatcher] decision delivery={DeliveryId} target={AgentId} status={AgentStatus} decision={Decision} queueAgeMs={QueueAgeMs}"
```

Log decisions:

```text
claimed
skipped_busy
skipped_frozen
skipped_offline
no_delivery
retry
dead_letter
delivered
```

- [ ] **Step 4: Run focused tests**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabric --no-restore --logger "console;verbosity=minimal"
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageToolsTests --no-restore --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 5: Run full build**

Run:

```powershell
dotnet build PuddingAgentNetwork.slnx --no-restore --nologo
```

Expected: PASS.

- [ ] **Step 6: Commit**

Commit:

```powershell
git add Source\PuddingPlatform\Services\MessageFabric Source\PuddingAgent\Services\Events\AgentInboxDispatcher.cs
git commit -m "chore: add message fabric observability"
```

---

## Self-Review

Spec coverage:

- Public agent-to-agent timeline: covered by Task 2 send defaults and existing `RoomMessage` persistence.
- Direct durable delivery: covered by Task 1.
- Subscription-driven dispatcher: covered by Task 4.
- Idle-only execution: covered by Task 3 availability and Task 4 dispatcher checks.
- `list_agents`: covered by Task 2 and Task 3.
- Roster context layer: covered by Task 5.
- V1 observability: covered by Task 6.
- V2 guardrails deferred: no task implements loop guard, hop policy, sliding windows, or confirmation gates.

No placeholders:

- This plan intentionally includes one conditional test-location branch for dispatcher tests because the current repository may not have `PuddingAgentTests`. The implementation still has a required build verification path.

Type consistency:

- `MessageClaimRequest`, `AgentAvailability`, `IAgentAvailabilityService`, and `IAgentRosterProvider` are introduced before later tasks use them.
- Existing `AckAsync(string)` is kept for compatibility with `ReceiveMessagesTool`.
