# Runtime Steering Queue Design

## Goal

Allow users to keep interacting while an Agent is running. Normal messages wait in a visible queue; urgent guidance is injected into the next Agent LLM context assembly inside the current run.

## Behavior

- Normal send while the Agent is busy enqueues the message in the client queue.
- The queue is visible above the composer. Users can edit, delete, and send the next queued item.
- When the active Agent turn finishes, the client sends the first queued message automatically.
- A queued item can be marked as guidance. Guidance is posted to the backend immediately and is not a normal user message.
- Runtime consumes pending guidance before the next LLM call in the Agent loop and appends it to the in-memory message history as the latest user steering instruction.
- The currently running tool call is not cancelled. Guidance takes effect at the next model request after that tool call returns.

## Architecture

- Backend adds a durable `session_steering_messages` table and `SessionSteeringService`.
- Chat API exposes `POST /api/workspaces/{workspaceId}/chat/sessions/{sessionId}/steering`.
- `AgentExecutionService` checks `SessionSteeringService` before each LLM invocation and marks the guidance consumed when injected.
- Session events `steering.created` and `steering.injected` are emitted for diagnostics and UI replay.
- Frontend stores the interaction queue in `useChatState` and passes queue actions down to `IntentConsole`.

## Testing

- Service tests cover create and consume-once semantics.
- Runtime injection is tested at the service boundary where practical.
- UI tests cover busy send enqueue, editing/deleting queue items, and guidance submission.
