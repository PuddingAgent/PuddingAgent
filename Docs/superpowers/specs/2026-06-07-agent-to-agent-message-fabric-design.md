# Agent-to-Agent Message Fabric V1 Design

## Goal

Build a minimal observable agent-to-agent message path.

V1 should let one agent send a visible room message to another agent, persist a direct delivery for the target agent, and let a subscription-driven dispatcher claim and execute that delivery when the target agent is idle.

V1 is not a collaboration safety system. It is a running first slice with enough telemetry, logs, and durable state to make V2 decisions from real behavior instead of guesses.

## 2026-06-07 Revision

This revision incorporates the first implementation slice and tightens the design around what is already true in the codebase versus what remains as follow-up hardening.

Implemented V1.0 baseline:

- `send_message` defaults agent-to-agent messages to `public`, so the room timeline is the primary user-visible collaboration surface.
- `list_agents` is a separate tool backed by `IAgentRosterProvider`.
- `RoomMessage` remains the transcript fact and `MessageDelivery` remains the durable per-target delivery fact.
- `MessageDeliveryDispatcher` in `PuddingRuntime.Services.Messaging` subscribes to `message.deliver`, claims a durable delivery, dispatches runtime execution, and acks or retries.
- `AgentEventHandler` deliberately skips `message.deliver` so the generic event path does not duplicate message execution.
- `WorkspaceAgentsContextBuilder` injects a compact `WORKSPACE AGENTS` layer into context assembly.
- SQLite bootstrap now upgrades existing `message_deliveries` tables by adding claim columns before creating indexes that reference them.

## 2026-07-18 Reliability Correction

The durable delivery table is the recovery authority. `message.deliver` and the in-memory known-target set are latency optimizations only.

- `MessageDeliveryDispatcher` must be registered as a hosted service; registering only the singleton leaves the durable inbox without a consumer.
- Each recovery pass discovers distinct agent targets with `queued` or `retrying` deliveries from `IMessageInbox`, then applies the normal claim/availability/dispatch path.
- Expired delivery leases are recovered periodically so a process failure cannot strand `delivering` rows.
- Runtime `Busy` is a transient scheduling race: defer the delivery without applying the business-failure dead-letter threshold.
- A generated reply is a new outbound message transaction. Reply routing failure must not roll an already successful inbound execution back to `retrying`; batch transitions must cover every claimed delivery.
- The Chat interaction-queue projection excludes `visibility=system` by default. Diagnostic callers may opt in, but canonical `pudding-message` envelopes are projected as `context.text`, not raw protocol JSON.
- `AgentEventHandler` continues to skip `message.deliver`; there is exactly one automatic delivery owner.

## 2026-07-18 Runtime Session Integrity Correction

Message delivery reliability and Conversation Turn reliability remain separate durable concerns, but they converge on the same mutable Runtime session. Therefore:

- `AgentExecutionService` is protected by a process-wide `ISessionExecutionGate` keyed by `sessionId`. Dispatcher execution, Conversation execution, heartbeat and direct Runtime calls cannot concurrently mutate one session.
- `ChatExecutionWorker`'s per-Conversation lock is not the Runtime-wide correctness boundary; its database run lease/fence remains the cross-process Conversation authority.
- Tool-call history is committed as one atomic batch only after every assistant `tool_call_id` has a matching Tool result.
- Cancellation, timeout, Fuse and tool exceptions discard the unpublished tool round instead of leaving invalid provider history.
- `ContextWindowManager` repairs a richer in-memory snapshot before deciding whether to keep it over a shorter persisted snapshot.
- `LlmInvocationService` records repairs, while `OpenAiLlmGateway` normalizes again as the final OpenAI-compatible wire-protocol guard.

Still pending after V1.0:

- Idle-state gating before claim and execution.
- `agent.availability.changed` subscription.
- A retry-to-dead-letter policy threshold.
- Structured telemetry for every dispatcher decision and status transition.
- Timeline projection for queued/delivering/delivered/dead-letter delivery states.

The current implementation proves the path can run. The next implementation pass should make the dispatcher respect execution availability and improve observability before adding collaboration guardrails.

## Non-Goals

V1 does not implement:

- Loop guard.
- Hop count or max hop policy.
- Sliding-window counters.
- Similar-content detection.
- Automatic collaboration depth policy.
- Complex `@all` rate limiting.
- User confirmation gates for suspicious chains.

V1 still preserves correlation fields so V2 can add these controls without rewriting the message path.

## Architecture

```text
Agent / User / Connector
  -> IMessageSystem.SendAsync
  -> MessageRouter
  -> RoomMessage + MessageDelivery
  -> message.deliver event
  -> MessageDeliveryDispatcher
  -> ClaimNextAsync
  -> Runtime execution
  -> Ack / Retry / DeadLetter
  -> Timeline and diagnostics projections
```

V1 keeps the existing distinction between message facts and event mechanics:

- `RoomMessage` is the room transcript fact.
- `MessageDelivery` is the durable per-target delivery fact.
- `message.deliver` is a wakeup signal, not the authoritative delivery state.
- `MessageDeliveryDispatcher` subscribes to wakeup signals and claims durable deliveries.
- `Runtime` executes only after a successful claim.

## Default Visibility

Agent-to-agent messages are visible in the room timeline by default.

For `agent:A -> agent:B`:

```text
RoomMessage.visibility = public
RoomMessage.from = agent:A
RoomMessage.to = agent:B
MessageDelivery.target = agent:B
```

The user sees the collaboration message, but only the target agent receives a delivery and can be woken by the dispatcher.

Private or system messages remain possible, but they are explicit overrides.

## Message Protocol

`send_message` remains a send-only tool. It should not list agents, claim deliveries, inspect runtime state, or apply collaboration policies.

V1 tool parameters:

```text
to: string
content: string
intent?: inform | ask | request_review | delegate | report_result
requires_response?: bool
visibility?: public | private | system
reply_to_message_id?: string
```

V1 defaults:

```text
visibility = public
requires_response = true for ask, request_review, delegate
requires_response = false for inform, report_result
```

The message envelope should preserve:

```text
message_id
room_id
from
to
audience
visibility
content_type
content
reply_to_message_id
correlation_id
causation_id
metadata.intent
metadata.requires_response
```

V1 does not expose `max_hops` or compute hop policy.

## Agent Discovery Tool

Add a separate `list_agents` tool.

This avoids overloading `send_message` with query behavior and gives agents a clear way to refresh target status before sending.

Suggested parameters:

```text
room_id?: string
include_busy?: bool = true
include_frozen?: bool = false
```

Suggested output:

```json
{
  "workspace_id": "default",
  "room_id": "default",
  "agents": [
    {
      "agent_id": "code-agent",
      "display_name": "Code Agent",
      "address": "agent:code-agent",
      "status": "idle",
      "accepts_messages": true,
      "capabilities": ["code", "debug", "test"],
      "current_task_summary": null
    }
  ]
}
```

`receive_messages` can remain available for manual pull and diagnostics, but it is not the primary automatic collaboration path.

## Delivery Lifecycle

V1 uses a compact durable state machine:

```text
queued
  -> delivering
  -> delivered

delivering
  -> retrying
  -> queued

queued/delivering/retrying
  -> dead_letter
```

Suggested delivery fields:

```text
delivery_id
message_id
workspace_id
room_id
target_kind
target_id
status
priority
attempt_count
available_at
lease_until
claimed_by_execution_id
last_error
created_at
updated_at
```

`ClaimNextAsync` must be atomic at the database boundary. The dispatcher must not use `ListAsync -> Execute -> AckAsync` as the automatic path because that can duplicate work under concurrency or restart recovery.

Suggested inbox interface additions:

```csharp
Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct);
Task AckAsync(string deliveryId, string executionId, CancellationToken ct);
Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct);
Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct);
```

## Durable Dispatcher

The dispatcher is subscription-driven for low latency and database-driven for recovery. Both paths converge on the same atomic claim and execution method.

Implemented V1.0 path:

```text
on message.deliver(target=B):
  if target is an agent:
    ClaimNextAsync(B)
    execute runtime
    AckAsync on success
    RetryAsync on failure
```

Required V1.1 hardening:

```text
on message.deliver(target=B):
  if B is idle:
    ClaimNextAsync(B)
    execute runtime
  else:
    leave delivery queued and record skipped reason
```

Primary subscriptions:

```text
message.deliver
agent.availability.changed
```

Availability-triggered path:

```text
on agent.availability.changed(B -> idle):
  ClaimNextAsync(B)
  execute runtime
```

Recovery path:

```text
every 10s:
  list distinct agent targets with queued/retrying deliveries
  remember target scope
  run normal availability + atomic claim + dispatch path

every 60s:
  recover expired delivering leases
```

Subscriptions provide timely response. Durable delivery state provides reliability across missed wakeups and process restarts. Atomic claim provides concurrency safety.

The dispatcher should stay in Runtime, close to `IRuntimeAgentDispatcher`, because delivery consumption creates runtime work. Platform owns message persistence and routing; Runtime owns execution.

## Agent Availability

Agent availability should come from the execution layer, not from message state.

V1 statuses:

```text
idle
busy
waiting_approval
waiting_event
frozen
offline
```

V1 claim rules:

| Status | Claim? | Notes |
| --- | --- | --- |
| `idle` | yes | Normal automatic consumption. |
| `busy` | no | Keep queued until idle. |
| `waiting_approval` | no | Do not bypass approval. |
| `waiting_event` | no | V1 keeps this conservative. |
| `frozen` | no | Requires manual or governance action. |
| `offline` | no | Keep queued. |

## Context Injection

Add a short workspace-agent roster layer to context assembly.

Implemented placement:

```text
L0 STATIC
L0 ENVIRONMENT
L0-AGENTS-ROSTER / WORKSPACE AGENTS
L1 TOOLS
```

Suggested content:

```text
--- LAYER: WORKSPACE AGENTS ---
Messageable agents in this workspace:
- Audit Agent address=agent:agent-b status=idle can_receive=true capabilities=[template:audit-agent]
Use send_message with an agent address when another agent should receive a visible room timeline message. Use list_agents to refresh the roster.
```

The roster should stay short. It should not inject full agent persona prompts.

## Observability

V1 success depends on evidence. Every major transition should be queryable through logs, metrics, or durable tables.

### Message Send

Record:

```text
message_id
workspace_id
room_id
from_kind
from_id
target_kind
target_id
audience
visibility
intent
content_length
correlation_id
causation_id
created_at
```

Use this to analyze target selection, intent distribution, public/private ratios, and message size.

### Delivery Lifecycle

Record each status transition:

```text
delivery_id
message_id
target_agent_id
old_status
new_status
attempt_count
reason
latency_from_message_created_ms
updated_at
```

Use this to analyze queue latency, busy-agent delay, retries, and dead letters.

### Dispatcher Decision

Record lightweight dispatcher decisions, including skipped decisions:

```text
delivery_id
target_agent_id
agent_status
decision = claimed | skipped_busy | skipped_frozen | skipped_offline | no_delivery
queue_age_ms
```

Use this to tune V2 scheduling, priority, batching, and idle detection.

### Execution Result

Record:

```text
delivery_id
execution_id
target_agent_id
session_id
started_at
ended_at
duration_ms
success
error_type
tool_call_count
output_message_count
```

Use this to analyze cost, quality, failure modes, and whether agent-to-agent messages actually produce useful work.

### Chain Correlation

Preserve:

```text
correlation_id
causation_id
reply_to_message_id
source_message_id
produced_message_ids
```

V1 does not enforce loop controls, but V2 needs this data for sliding windows, loop detection, collaboration graphs, and quality analysis.

## Timeline Projection

The main room timeline should show user-understandable collaboration state:

```text
sent
queued
delivering
delivered
failed
dead_letter
```

Internal events such as raw `message.deliver`, lease renewal, retry calculation, and claim attempts belong in Inspector or diagnostics, not the main timeline.

Example:

```text
Code Agent -> Audit Agent
Please review the MessageDelivery state machine for duplicate execution risk.
[queued · Audit Agent busy]
```

After claim:

```text
[delivering · Audit Agent]
```

After success:

```text
[delivered]
```

## V1 Success Criteria

- An agent can call `send_message` to send a public room-visible message to another agent.
- The room timeline shows the agent-to-agent message.
- The target agent receives a direct durable delivery.
- The subscription-driven dispatcher claims and executes a target-agent delivery from `message.deliver`.
- Successful execution marks the delivery delivered.
- Failed execution retries according to the simple V1 state machine.
- Logs and durable tables can reconstruct message send, delivery claims, dispatch attempts, retries, and acknowledgements.
- V1 does not implement loop guard or sliding-window controls.

V1.1 success criteria:

- If the target agent is busy, the delivery remains queued.
- When the target agent becomes idle, the subscription-driven dispatcher claims and executes the delivery.
- Failed execution eventually dead-letters according to a simple threshold.
- Logs or metrics can reconstruct skipped dispatcher decisions and execution result.

## Deferred V2 Work

V2 should be designed from V1 telemetry.

Deferred topics:

- Sliding-window message counters.
- Loop and ping-pong detection.
- Similar-content detection.
- Dynamic collaboration depth policy.
- Priority and fairness scheduling.
- Batch consumption for idle agents.
- Smarter `waiting_event` wakeup matching.
- `@all` rate limiting.
- Collaboration quality scoring.
- Agent-to-agent graph and diagnostics UI.
