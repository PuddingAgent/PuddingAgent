# Runtime Steering Queue Design

## Goal

Allow users to keep interacting while an Agent is running. Normal messages wait in a visible queue; urgent guidance is injected into the next Agent LLM context assembly inside the current run.

## Behavior

- Normal send while the Agent is busy is submitted immediately to the canonical Turn API. The acceptance transaction writes `ChatMessages` and `chat_execution_commands`; `ChatExecutionWorker` continues FIFO execution without an open browser.
- The queue is represented above the composer by a compact, content-width pill. Expanding it opens a bounded floating panel above the pill rather than increasing composer/chat height; long queues scroll inside the panel.
- The floating panel closes on outside click or `Escape`. Accepted queue items are read-only projections; edit/delete/reorder require future server commands and must not be simulated in React state.
- `Enter` always uses durable Turn admission. `Ctrl/Cmd+Enter`, or the dedicated lightning action, posts steering immediately and does not create a second Turn.
- While an Agent Turn is active, the Composer also exposes a dedicated `⚡` steering button beside the send/stop action. It submits the current text through the same canonical steering route, never falls back to the normal pending queue, and clears the draft only after a definite `202`. The UI/file-level contract is frozen in `Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md`.
- Backend-owned Turn/delivery queue items cannot be converted in place until cancel-and-steer is atomic; the UI disables that action to prevent duplicate execution.
- Queue ownership ends at consumer claim. The default Composer projection contains only `message_deliveries=queued|retrying` and `chat_execution_commands=pending`; `delivering|leased|running|cancel_requested` are execution/timeline facts and appear only in explicit diagnostic queries.
- Agent-to-Agent deliveries and executable heartbeats use a durable handoff: Message Fabric claim -> canonical Turn acceptance -> delivery ACK. Acceptance creates the inbound message card; `turn.started` and subsequent canonical events create the same reasoning/tool trajectory card as a user Turn. Busy/foreground-blocked heartbeats are ACK/drop and never create a Turn.
- Runtime drains pending guidance before the next LLM call in the Agent loop and appends it to the in-memory message history as the latest user steering instruction.
- The currently running tool call is not cancelled. Guidance takes effect at the next model request after that tool call returns.
- Guidance accepted while a final model response is being produced is checked again at the late safe boundary; Runtime continues the same Turn for another model request instead of intentionally waiting for the next user Turn.
- If the active Turn finishes before admission, the API returns `409`; a local queued item stays queued and direct composer text is restored.

## Architecture

- Backend adds a durable `session_steering_messages` table and `SessionSteeringService`.
- The single admission route is `POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering` with `X-Workspace-Id`.
- `CreateSteeringHandler` resolves the canonical execution command, accepts only `Running`, fences workspace/Agent identity, and writes the Runtime-consumed durable queue bound to immutable `target_turn_id`.
- Local queue conversion carries stable `source_queue_item_id`: the UI permits one admission request in flight per item and the service returns an existing pending/consumed row on an ambiguous retry.
- `AgentExecutionService` checks `SessionSteeringService` before each LLM invocation, drains only items targeting its exact Turn, marks them consumed, and persists `steering.injected` diagnostics. A late row can expire but cannot leak into the next Turn.
- Clean databases receive the column from the EF model; existing SQLite databases are upgraded in place by `SessionSteeringSchemaBootstrapper`, which expires pre-contract pending rows instead of guessing their target.
- `MessageQueueProjectionService` is a unified read model over unclaimed `message_deliveries` and `chat_execution_commands`; the stores and schedulers remain separate. `queueKind` distinguishes `message_delivery` from `chat_turn`. `includeTerminal=true` remains the diagnostic escape hatch for claimed/running/terminal rows.
- `MessageDeliveryDispatcher` classifies Agent deliveries by server-owned `handling_mode`. `execute` work and allowed heartbeat deliveries transfer to `ISubmitTurnHandler` with stable delivery-derived idempotency IDs and trusted `message_fabric_*` metadata; before trust elevation the dispatcher removes sender-supplied reserved keys and rebuilds routing facts from the claimed durable row. `notify` deliveries bypass Turn/model admission, batch-claim up to 20 per target Agent across rooms, and append one independent canonical message/event per delivery before ACK. `ConversationReplyProjectionWorker` projects at most one committed terminal reply with a stable MessageId and passive `agent_reply` intent.
- Canonical `pudding-message` envelopes remain the Runtime input fact, but `AgentConversationProjectionService` unwraps `context.text` for the inbound Chat card and derives Agent/system identity from the envelope.
- Frontend owns only the composer draft and ephemeral steering admission feedback. It never owns executable pending messages and cannot clear/reorder an accepted Turn locally.

## Testing

- Service tests cover create and consume-once semantics.
- API integration tests cover `Running -> 202 + durable pending row` and terminal Turn `-> 409 + no row`.
- Projection tests cover `pending -> queued`, claimed/running exclusion from the default queue, explicit diagnostic inclusion, canonical user text, and stable `turn:{commandId}` identities.
- Dispatcher/reply tests cover delivery-derived idempotent acceptance, Agent/system card metadata, heartbeat busy ACK/drop, executable Agent-message isolation, passive notification batch claim with per-message facts, acceptance failure retry, and one-time passive terminal reply projection.
- UI tests prove busy normal sends call canonical admission immediately, create no `local_pending` item, preserve server-owned items when the page request is stopped, and retain direct `Ctrl+Enter`/failed-Steering text behavior.
- Dedicated-button tests cover active-Turn visibility, no normal-queue/new-Turn side effects, compare-and-clear draft safety, double-submit fencing, image fail-closed behavior, and `409` text preservation.
- Queue component tests cover collapsed/expanded accessibility state, outside-click close, and `Escape` close with focus restoration; the floating/capped geometry is owned by `composer.styles.ts`.

## Implementation status (2026-08-26)

- Implemented: browser-local executable queue removal, claim-boundary queue projection, Agent/heartbeat canonical Turn handoff, committed Agent reply projection, envelope payload card rendering, `queueKind` contract, corrected unattended-mode UI copy, backend projection tests, and queue hook/component tests. 2026-08-27 source hardening adds `execute/notify` delivery modes, default-passive `send_message`, fail-closed reply contracts, Busy-independent passive notification drain, and bounded batch claim without content batching.
- Build/test completion proves the source change only. The running Desktop/Core must be restarted onto the new build before an in-product unattended smoke can claim deployment acceptance.
