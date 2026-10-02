# Agent-First Main Session Chat Design

## Goal

Redesign `/admin/chat` around Agents as first-class product objects. Sessions become the work timeline behind each Agent or Group instead of the primary navigation object.

## Design Principles

1. Agent is the first-class citizen. A user opens an Agent or Group, not a raw session.
2. Workspace is the collaboration and tenant boundary. Each `workspace + agent` has exactly one main session shared by workspace collaborators.
3. The default UI should feel like a lightweight web chat app. Users should understand contacts, messages, status, and groups without learning Agent internals.
4. Advanced capability uses progressive disclosure. Reasoning, tools, sub-tasks, runtime events, and diagnostics remain available but stay folded by default.
5. Details are retained. Expert users can inspect structured reasoning summaries, process projections, tool steps, sub-agent traces, and raw diagnostics through deliberate interactions.
6. Group chat is a peer concept to Agent chat. Groups will become contact-list entries with their own workspace-scoped main timeline.

## Product Model

### Agent Contact

An Agent contact is the primary left-sidebar item for a workspace Agent.

- Identity: avatar, display name, short description.
- Status: idle, working, waiting for approval, blocked, offline.
- Summary: current task, recent action, active tools, active sub-task count, last activity.
- Entry action: click opens the Agent's main session.

### Group Contact

A Group contact represents a named set of Agents.

- Identity: group avatar or stacked Agent avatars, group name, member count.
- Status: aggregate group activity.
- Entry action: click opens the group's main session.
- MVP treatment: reserve the model and UI section. Full group routing can arrive after Agent contacts and main sessions are stable.

### Main Session

The main session is the persistent workspace timeline for one Agent or Group.

- One main session per `workspaceId + agentInstanceId`.
- It is the default chat target when the user clicks an Agent.
- It should not be deleted through the normal session delete action.
- It can be reset only through an explicit dangerous operation that archives the old main timeline and creates a new one.

### Task Session

A task session is a focused child timeline spawned from a main session.

- Used for long-running work, experiments, context isolation, or focused execution.
- Appears under the current Agent as task/history, not as the primary sidebar.
- Can write a summary, decisions, artifacts, and memories back to the main session.

### Branch Session

A branch session starts from a specific message or task point.

- Used for alternate approaches or rewind-and-rerun workflows.
- Keeps parent/root linkage for traceability.
- Appears under the current Agent or Group as a branch.

## Information Architecture

```text
/admin/chat
  Workspace selector
  Left sidebar: Agents / Groups contacts
    Agent contact
      avatar
      status tag
      unread count
      hover work summary
    Group contact
      grouped avatar
      aggregate status
      member count
  Center: selected contact main timeline
    message stream
    compact process summaries
    folded reasoning/tool/sub-task details
    composer
  Optional details surface
    current work summary
    task/branch/history list
    diagnostics and raw events
```

The old session list moves from primary navigation to a secondary surface scoped to the selected Agent or Group.

## UX Behavior

### Left Sidebar

- Replace the session-first list with an Agent-first contact list.
- The first section is Agents. The second section is Groups when group support is available.
- Search filters contacts by Agent name, description, active task, and group name.
- The selected Agent contact stays highlighted while its main session is open.
- Status is visible as a small tag or dot, not a large dashboard widget.
- Hover opens a compact summary popover:
  - current work title,
  - active tool or sub-task count,
  - waiting reason when blocked,
  - last activity time,
  - latest short answer or progress sentence.

### Main Timeline

- The center reads as a normal chat thread.
- User messages and Agent responses are the dominant content.
- Runtime details are projected into short natural-language process lines.
- Structured reasoning summaries and process projections are collapsed behind a small "thinking" entry or icon.
- Tool calls and sub-tasks are collapsed behind compact indicators.
- Completed process details remain inspectable from each response.

### Composer

- The composer remains visually simple.
- Capability indicators use low-noise icons or compact dots.
- The sub-task indicator should be small by default:
  - idle: small muted task icon,
  - active: task icon with count,
  - hover: active sub-task summary,
  - click: expandable sub-task list.
- Sending while the Agent is busy keeps the existing queue behavior, but the visible language should be "queued message" or "guidance" instead of runtime jargon.

### Progressive Disclosure

The UI uses three layers of detail:

1. Summary layer: visible by default. Examples: "正在检索文件", "等待确认", "3 个子任务运行中".
2. Structured detail layer: opened by click or hover. Shows reasoning summaries, steps, tool names, sub-task names, status, and durations.
3. Diagnostic layer: opened through "more details" actions. Shows raw event ids, trace ids, logs, token/cache information, and replay data.

This preserves expert observability while keeping the default page quiet.

## Backend Model

### Session Role

Add a role concept separate from the existing `SessionType`.

```csharp
public enum SessionRole
{
    Main,
    Task,
    Branch,
    Audit
}
```

`SessionType` can continue to describe runtime category. `SessionRole` describes product placement.

### Session Binding

Extend `SessionRecord` with fields that make the primary binding explicit.

```csharp
public SessionRole SessionRole { get; init; } = SessionRole.Task;
public string? ParentSessionId { get; init; }
public string? RootSessionId { get; init; }
public string? PrincipalKind { get; init; } // "agent" or "group"
public string? PrincipalId { get; init; }   // agentId or groupId
```

For Agent main sessions:

- `PrincipalKind = "agent"`
- `PrincipalId = agentInstanceId`
- `AgentInstanceId = agentInstanceId`
- `SessionRole = Main`
- unique key: `workspaceId + principalKind + principalId + sessionRole`

For future Group main sessions:

- `PrincipalKind = "group"`
- `PrincipalId = groupId`
- `SessionRole = Main`

### Main Session Creation

The backend should expose an idempotent ensure-main-session operation.

```http
POST /api/sessions/main
{
  "workspaceId": "default",
  "principalKind": "agent",
  "principalId": "agent-id",
  "agentTemplateId": "global:general-assistant",
  "title": "General Assistant"
}
```

Response is the existing main session when present, otherwise the newly created one.

### Chat Sending

When a user sends a message without a selected task or branch session:

1. Frontend ensures the selected Agent main session exists.
2. Frontend sends the main `sessionId`.
3. Backend records the message and events under that main session.
4. If no `sessionId` arrives, backend resolves the main session for `workspace + agentInstanceId` rather than creating a detached random session.

## Frontend State Model

`useChatState` should move from session-first selection toward contact-first selection.

```ts
interface SelectedChatContact {
  kind: 'agent' | 'group';
  id: string;
  mainSessionId?: string;
}
```

The selected contact drives:

- main session resolution,
- message history loading,
- session event stream subscription,
- unread count clearing,
- status summary aggregation.

The existing `selectedSessionId` remains during migration, but it should represent the active timeline behind the selected contact.

## Migration Strategy

1. Introduce session role and binding fields with defaults.
2. Treat existing active sessions as task/history sessions unless explicitly selected as a main session by migration logic.
3. On first Agent open, if no main session exists for `workspace + agent`, create it.
4. Keep old session list data readable through task/history panels.
5. Preserve transcript backfill behavior for legacy event-only sessions.

## MVP Scope

Included:

- Agent-first sidebar replacing the primary session list.
- Workspace-scoped Agent main session resolution.
- Agent contact status tag and hover summary.
- Main timeline loaded from the Agent main session.
- Task/sub-task/reasoning details folded by default and expandable.
- Group model reserved in API/types and sidebar section.

Excluded from MVP:

- Full group routing and multi-Agent group response policy.
- Cross-device account-level personal main sessions.
- Rewriting the entire chat runtime.
- Deleting historical sessions.
- Replacing diagnostics tooling.

## Acceptance Criteria

- Opening `/admin/chat?workspaceId=default` shows an Agent contact list as the primary left navigation.
- Clicking an Agent opens the same main session every time for that workspace and Agent.
- Switching Agents does not create arbitrary new sessions.
- The main chat area remains readable as a lightweight web chat interface.
- Structured reasoning summaries and process details are collapsed by default and expandable per response.
- The composer shows sub-task capability as a compact indicator rather than a verbose panel.
- Expert diagnostics remain reachable within two deliberate interactions from a message or status summary.
- Existing session messages and event replay continue to work for legacy sessions.
