# Agent Chat Client Architecture Redesign

## Goal

Redesign `/admin/chat` as an Agent-first client application built with Web technology. The browser page must behave like a SPA client with its own state machine, durable local cache, recovery model, and React rendering strategy. The server remains the source of facts, but the UI experience is local-first, stable during switching, and resilient to stream disconnects.

## Context

The current Agent-first work already moved navigation from session-first to Agent-first. The latest failures show the next boundary:

- Switching Agents can briefly show an empty state because the UI clears local turns before the next authoritative view is ready.
- If Agent A is outputting while the user switches to Agent B and back, the UI may fail to recover the output state because the browser tries to rebuild a turn from event fragments.
- The status tag can be wrong if it is inferred from the currently selected message instead of a real Agent work-state projection.
- Performance diagnostics currently reveal that rendering, replay, SSE aborts, and long tasks are mixed together in the same client controller.

These are architectural symptoms. More conditional logic in `useChatState` can reduce incidents, but it will not create a stable product model.

## Design Principles

1. `/admin/chat` is a client, not a server-rendered page. It uses Web technology, but its data lifecycle should be designed like a desktop or mobile chat client.
2. Agent is the first-class product object. Session is a backing timeline and persistence boundary.
3. Server facts are authoritative. Client snapshots are experience accelerators, not independent truth.
4. The first visible state after switching must come from a stable local or server read model, not from replaying raw events.
5. SSE/WebSocket is an incremental sync channel. It must not be the primary UI state source.
6. React should render stable derived views through selectors and scheduling, not mutate chat UI directly from every event frame.
7. Diagnostics, runtime traces, tool events, and natural-language chat output are separate channels. Expert observability remains available but must not pollute normal chat rendering.
8. The browser client sends commands and subscribes to message/projection updates. Agent execution and output generation are server-side lifecycles and must not depend on the browser staying open.
9. The current product remains single-user. Multiple browser clients for the same single-user instance must not interfere with each other's local UI state, but they must observe the same Agent main-session facts.

## Target Product Model

### Agent Main Session

There is exactly one main session for each `workspace + agent` in the current single-user product.

```text
workspaceId + principalKind(agent) + principalId(agentId) + ownerUserId + sessionRole(Main)
```

`ownerUserId` is a forward-compatible internal dimension, not a requirement to design or ship multi-user behavior now. In the current implementation it should use one stable default owner such as `single-user`. The important architectural rule is that schema, unique keys, projection APIs, and client cache keys preserve this field so a future account model does not need to reinterpret existing main-session ownership.

Workspace remains the tenant and collaboration boundary. The primary Agent conversation and memory context are single-user by product design today, with `ownerUserId` stored as a reserved compatibility field. Future group/collaboration views can intentionally project shared room state on top of this baseline.

### Agent Run

An `AgentRun` represents one unit of Agent work started by a user message, queued interaction, steering message, scheduled task, or Agent-to-Agent message.

```ts
type AgentRunStatus =
  | 'queued'
  | 'running'
  | 'waiting'
  | 'succeeded'
  | 'failed'
  | 'cancelled';

interface AgentRunView {
  runId: string;
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  commandClientId?: string;
  inputMessageId?: string;
  outputMessageId?: string;
  status: AgentRunStatus;
  statusText: string;
  summary: string;
  startedAt: string;
  updatedAt: string;
  completedAt?: string;
  eventCursor: number;
  outputSnapshot: AgentOutputSnapshot;
}
```

The UI status tag reads from `AgentRunView` or `AgentStatusProjection`, never from the selected React turn.

### Agent Status Projection

The server exposes one workspace-scoped projection for the contact list.

```ts
interface AgentStatusProjection {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  status: 'idle' | 'running' | 'waiting' | 'failed' | 'offline';
  activeRunId?: string;
  summary: string;
  unreadCount: number;
  eventCursor: number;
  updatedAt: string;
}
```

This projection powers the left contact list, hover summary, status tag, unread count, and group aggregation.

### Conversation Read Model

The server exposes a view that can be rendered directly without replaying event logs on first load.

```ts
interface AgentConversationView {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  messages: ConversationMessageView[];
  activeRun?: AgentRunView;
  eventCursor: number;
  updatedAt: string;
}

interface ConversationMessageView {
  messageId: string;
  runId?: string;
  role: 'user' | 'agent' | 'system';
  sourceId: string;
  sourceName: string;
  createdAt: string;
  content: string;
  status: 'sending' | 'sent' | 'streaming' | 'succeeded' | 'failed' | 'cancelled';
  processSummary?: ProcessSummaryView;
}
```

`session_event_log` remains the append-only fact log. The read model is the UI projection of those facts.

## Server Architecture

### Fact Layer

Server facts remain append-only or explicitly versioned:

- `SessionRecord` identifies main/task/branch session role, principal binding, and owner user binding.
- `ChatMessage` stores user and final assistant transcript messages.
- `SessionEventLog` stores raw execution/event facts.
- `AgentRunRecord` stores lifecycle, owner user, issuing client id, and cursor for a run.

### Server-Side Agent Autonomy

After the server accepts a user command, Agent execution is independent from the browser connection:

```text
Browser Client
  -> POST command/message
  -> server validates user/workspace/agent/main session
  -> server creates AgentRun and records accepted command
  -> browser may disconnect, refresh, or fail
  -> server continues Agent execution
  -> server writes messages/events/projections
  -> any browser attached to the same single-user instance can later subscribe or reload
```

The browser must not own execution cancellation unless it explicitly sends a cancel command. Closing a tab, navigating away, aborting a fetch, or losing SSE must only detach that browser observer.

### Projection Layer

Projection services consume facts and produce UI-ready models:

- `IAgentStatusProjectionService`
- `IAgentConversationProjectionService`
- `IAgentRunProjectionService`

The projection layer is allowed to denormalize output snapshots for fast reads.

### API Surface

The web client should use these high-level endpoints:

```http
GET /api/workspaces/{workspaceId}/agents/status
GET /api/workspaces/{workspaceId}/agents/{agentId}/conversation
GET /api/workspaces/{workspaceId}/agents/{agentId}/runs/{runId}
POST /api/workspaces/{workspaceId}/agents/{agentId}/messages
GET /api/workspaces/{workspaceId}/agents/{agentId}/events?after={cursor}
GET /api/workspaces/{workspaceId}/agents/{agentId}/events/stream?after={cursor}&clientId={clientId}
```

The current implementation resolves one default owner internally and does not expose user selection in routes or UI. `clientId` identifies one browser/client instance for observer state, diagnostics, idempotent retries, and local pending-message reconciliation; it is not part of the main-session ownership key.

Existing session endpoints can remain for compatibility and diagnostics, but `/admin/chat` should move to the Agent endpoint surface.

## Client Architecture

### Client Store

The client owns a local state model with explicit stores:

```text
AgentDirectoryStore
  agent identity, status projection, unread count

AgentSelectionStore
  selected workspace, selected agent, route synchronization

ConversationStore
  current conversation view, active run, message list

RunStore
  run lifecycle, output snapshot, process summary

SyncStore
  stream state, replay cursor, backoff, online/offline state

LocalCacheStore
  IndexedDB persistence and memory cache hydration

ClientIdentityStore
  browser client id, local pending command ids, per-window UI state
```

React components read through selectors. They do not mutate these stores directly from transport callbacks.

### Local Storage

Use browser storage intentionally:

| Storage | Data |
|---|---|
| `localStorage` | workspace/agent preference, UI preferences, debug flags, browser `clientId` generated once per browser profile |
| `IndexedDB` | single-user agent status snapshots, conversation snapshots, run snapshots, event cursor, pending outbound messages; cache keys still include the internal `ownerUserId` compatibility field |
| memory store | active workspace/agent/conversation/run render state |

IndexedDB is not the authority. It is the instant-start cache that prevents blank UI during route changes, reloads, and reconnects.

### Multiple Browser Clients

Each browser profile or tab group has a stable `clientId` stored locally. Browser-local state includes:

- selected workspace and Agent,
- scroll position and viewport window,
- open or closed inspector state,
- pending command retry metadata for commands issued by that browser,
- local performance capture state.

Server-shared state includes:

- main session facts,
- AgentRun lifecycle,
- messages,
- status projection,
- event cursor.

If Browser A sends a command and Browser B is open against the same single-user instance, Browser B receives the resulting Agent messages through projection refresh or stream subscription. Browser B must not inherit Browser A's scroll position, input draft, open inspector panel, or local perf capture state.

### Agent Switch Flow

```text
User clicks Agent B
  -> AgentSelectionStore sets selectedAgentId
  -> ConversationStore hydrates B from IndexedDB immediately
  -> UI renders cached B snapshot or a stable loading shell
  -> client fetches AgentConversationView(B)
  -> client reconciles local cache with server projection
  -> SyncStore attaches stream from projection.eventCursor
  -> incoming events patch RunStore and ConversationStore
```

The UI must never clear the active visible conversation before either cached or server projection data for the new Agent is available.

### Output-While-Switching Flow

```text
Agent A is running
  -> server continues AgentRun even if all browser streams disconnect
  -> stream updates any attached browser RunStore and IndexedDB
User switches to B
  -> A stream may stay attached in background if low cost, or pause after saving cursor
  -> B snapshot renders from cache/projection
User switches back to A
  -> A snapshot renders from IndexedDB
  -> server projection refresh reconciles activeRun/outputSnapshot
  -> stream resumes from last cursor
```

The state is recovered from `AgentRunView.outputSnapshot`, not from local messageId-to-turnId maps.

## React Rendering Strategy

### Component Boundaries

Recommended component structure:

```text
ChatClientProvider
  AgentDirectoryPane
  ConversationViewport
    MessageWindow
      StableMessageList
      ActiveRunOutput
    Composer
  InspectorPanel
```

### Rendering Rules

- Stable historical messages are memoized and rendered as immutable segments.
- Active output is rendered in a separate `ActiveRunOutput` component.
- Markdown rendering is split into stable blocks and active tail.
- Streaming deltas are batched with `requestAnimationFrame` or a fixed scheduler.
- Long message lists use virtualization or windowing when message count crosses a threshold.
- Scroll anchoring belongs to `ConversationViewport`, not individual message components.
- Runtime Inspector consumes diagnostic stores, not the main conversation render path.

### React APIs

Use React deliberately:

- `useSyncExternalStore` for store subscriptions.
- `useMemo` and selectors for derived message lists.
- `startTransition` for non-urgent projection refreshes.
- `useDeferredValue` for expensive Markdown and inspector rendering.
- `React.memo` for message rows and status chips.
- Suspense-like loading boundaries can be used for cold starts, but switching between cached Agents should not suspend the whole chat surface.

## Migration Strategy

### Stabilization Layer

The current fixes remain valid as a temporary layer:

- stale switch guards,
- replay recovery for empty history,
- status tag based on explicit working ids,
- expected abort filtering in diagnostics.

These reduce active incidents while the architecture is rebuilt.

### Target Migration

1. Introduce server Agent status and run projections.
2. Add frontend stores and IndexedDB cache behind the current UI.
3. Switch Agent contact list to read from status projection.
4. Switch conversation viewport to read from `AgentConversationView`.
5. Move SSE/replay handling into `SyncStore`.
6. Split active output rendering from stable message history.
7. Remove session-first recovery logic from `useChatState`.

## Error Handling

- If IndexedDB fails, the client falls back to memory cache and server projection.
- If the conversation projection fetch fails, the cached snapshot stays visible with a non-blocking stale indicator.
- If the stream disconnects, `SyncStore` records the cursor and enters reconnect/replay mode.
- If replay detects a cursor gap, the client refetches the authoritative conversation projection.
- If projection and local snapshot conflict, server projection wins and the local cache is rewritten.

## Testing Strategy

### Backend

- Unit tests for `AgentRunRecord` lifecycle.
- Server execution continues after browser disconnect.
- API tests for idempotent main session and Agent status projection.
- Main session uniqueness includes the reserved default `ownerUserId` dimension.
- Same single-user instance multi-browser projection visibility.
- Compatibility tests prove `ownerUserId` is present in contracts and keys without adding user-management behavior.
- Projection tests from `SessionEventLog` to `AgentConversationView`.
- Replay gap tests that force projection refresh.

### Frontend

- Store tests for selection, cache hydration, projection reconciliation, and stream patching.
- IndexedDB adapter tests with fake-indexeddb or a repository-local equivalent.
- React tests for no blank state on Agent switching.
- React tests for output recovery after switching away and back.
- Rendering tests that assert stable message rows do not rerender for active-tail deltas.
- Perf diagnostic tests that keep normal aborts separate from real failures.

## Non-Goals

- This redesign does not require mobile clients in the first implementation.
- This redesign does not replace `session_event_log`; it defines how UI projections use it.
- This redesign does not implement full Group chat immediately. It reserves the same projection/store architecture for Groups.
- This redesign does not make IndexedDB authoritative.
- This redesign does not use browser connection lifetime as Agent execution lifetime.

## Acceptance Criteria

1. Switching Agents never shows a blank ready state when cached or server projection exists.
2. An Agent output can continue while not selected, and switching back restores the output snapshot and status.
3. Left contact status tags reflect server/client run state, not selected UI state.
4. Refreshing the page restores the selected Agent and latest cached conversation before network fetch completes.
5. Stream reconnect uses cursor replay and projection refresh instead of rebuilding UI from raw event fragments.
6. Runtime Inspector remains available but does not drive normal conversation rendering.
7. React rendering cost is bounded: active output updates do not rerender stable historical messages.
8. Closing, refreshing, or crashing a browser after sending a message does not stop server-side Agent execution.
9. Two browsers attached to the same single-user instance see the same Agent messages and run status for `workspace + agent`.
10. Data contracts, unique keys, and cache keys include the internal default `ownerUserId`; current single-user mode does not expose user selection or permission behavior.

## Self-Review

- No unresolved placeholders remain in this design.
- The server fact/projection/client-store boundaries align with ADR-016 and ADR-045.
- The design is scoped to `/admin/chat` client architecture, with Group chat reserved but not implemented in the first slice.
- The current stabilization patches are explicitly treated as temporary compatibility, not target architecture.
