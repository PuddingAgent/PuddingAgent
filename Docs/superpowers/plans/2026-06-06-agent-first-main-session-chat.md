# Agent-First Main Session Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert `/admin/chat` from a session-first interface into an Agent-first lightweight chat experience with one workspace-scoped main session per Agent.

**Architecture:** Add explicit session role and principal binding to the shared session contract, expose an idempotent main-session API, and route chat state through selected Agent contacts. Replace the primary left session list with an Agent/Group contact list while preserving task/history sessions as secondary details.

**Tech Stack:** C#/.NET 10, ASP.NET Core controllers/services, Redis-backed `InMemorySessionRepository`, React, TypeScript, Ant Design, existing Pudding chat hooks and component tests.

---

## File Structure

- Modify `Source/PuddingCore/Platform/SessionRecord.cs`: add `SessionRole` and binding fields.
- Modify `Source/PuddingController/Controllers/SessionController.cs`: add main-session endpoint and request DTO.
- Modify `Source/PuddingController/Services/InMemorySessionRepository.cs`: add main-session lookup and uniqueness update path.
- Modify `Source/PuddingPlatform/Services/PlatformApiClient.cs`: add client method for ensure-main-session.
- Modify `Source/PuddingPlatform/Controllers/Api/SessionApiController.cs`: expose `/api/sessions/main`.
- Modify `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`: use main session fallback when chat arrives without an explicit session.
- Modify `Source/PuddingPlatformAdmin/src/services/platform/api.ts`: add session role/principal types and `ensureMainSession`.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`: make selected Agent contact drive main session loading.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentContactSidebar.tsx`: Agent/Group contact list.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.tsx`: swap the primary sidebar from sessions to contacts.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`: align header and secondary task/history entry points with selected contact.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/components/ComposerFeedbackStrip.tsx`: make sub-task/status indicators compact and progressively disclosed.
- Modify focused tests under `Source/PuddingWebApiTests`, `Source/PuddingPlatformAdmin/src/pages/chat`, and `Source/PuddingPlatformTests` as listed below.

---

### Task 1: Add Session Role And Principal Binding

**Files:**
- Modify: `Source/PuddingCore/Platform/SessionRecord.cs`
- Test: `Source/PuddingWebApiTests/SessionApiControllerTests.cs`

- [ ] **Step 1: Add failing API contract assertions**

Add a test that creates a main session through the new platform API endpoint and asserts role and principal fields.

```csharp
[TestMethod]
public async Task EnsureMainSession_ReturnsWorkspaceAgentMainSession()
{
    var payload = new
    {
        workspaceId = "default",
        principalKind = "agent",
        principalId = "agent-alpha",
        agentTemplateId = "global:general-assistant",
        title = "General Assistant"
    };

    var response = await _client.PostAsJsonAsync("/api/sessions/main", payload);

    response.EnsureSuccessStatusCode();
    var session = await response.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
    Assert.IsNotNull(session);
    Assert.AreEqual("default", session!.WorkspaceId);
    Assert.AreEqual("agent-alpha", session.AgentInstanceId);
    Assert.AreEqual("Main", session.SessionRole);
    Assert.AreEqual("agent", session.PrincipalKind);
    Assert.AreEqual("agent-alpha", session.PrincipalId);
}
```

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "EnsureMainSession_ReturnsWorkspaceAgentMainSession"
```

Expected: fail because `SessionRole`, `PrincipalKind`, `PrincipalId`, and `/api/sessions/main` do not exist yet.

- [ ] **Step 2: Extend the shared session contract**

Add this enum and fields to `Source/PuddingCore/Platform/SessionRecord.cs`.

```csharp
/// <summary>会话在产品信息架构中的角色。</summary>
public enum SessionRole
{
    Main,
    Task,
    Branch,
    Audit
}
```

```csharp
public SessionRole SessionRole { get; init; } = SessionRole.Task;
public string? ParentSessionId { get; init; }
public string? RootSessionId { get; init; }
public string? PrincipalKind { get; init; }
public string? PrincipalId { get; init; }
```

Keep the existing `SessionType` unchanged.

- [ ] **Step 3: Update test DTOs**

Add fields to the test DTO used by `Source/PuddingWebApiTests/SessionApiControllerTests.cs`.

```csharp
public string? SessionRole { get; set; }
public string? ParentSessionId { get; set; }
public string? RootSessionId { get; set; }
public string? PrincipalKind { get; set; }
public string? PrincipalId { get; set; }
```

- [ ] **Step 4: Run the targeted contract test**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "EnsureMainSession_ReturnsWorkspaceAgentMainSession"
```

Expected: still fail at the missing endpoint until Task 2 is complete.

---

### Task 2: Implement Idempotent Main Session Resolution

**Files:**
- Modify: `Source/PuddingController/Services/InMemorySessionRepository.cs`
- Modify: `Source/PuddingController/Controllers/SessionController.cs`
- Test: `Source\PuddingWebApiTests\SessionApiControllerTests.cs`

- [ ] **Step 1: Add idempotency test**

Add a second assertion to the Task 1 test or a separate test.

```csharp
[TestMethod]
public async Task EnsureMainSession_IsIdempotentForWorkspaceAgent()
{
    var payload = new
    {
        workspaceId = "default",
        principalKind = "agent",
        principalId = "agent-alpha",
        agentTemplateId = "global:general-assistant",
        title = "General Assistant"
    };

    var firstResp = await _client.PostAsJsonAsync("/api/sessions/main", payload);
    var secondResp = await _client.PostAsJsonAsync("/api/sessions/main", payload);

    firstResp.EnsureSuccessStatusCode();
    secondResp.EnsureSuccessStatusCode();

    var first = await firstResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
    var second = await secondResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

    Assert.AreEqual(first!.SessionId, second!.SessionId);
}
```

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "EnsureMainSession_IsIdempotentForWorkspaceAgent"
```

Expected: fail because the repository cannot find a main session by principal yet.

- [ ] **Step 2: Add repository lookup**

Add this method to `InMemorySessionRepository`.

```csharp
public async Task<SessionRecord?> FindMainAsync(
    string workspaceId,
    string principalKind,
    string principalId,
    CancellationToken ct = default)
{
    var sessions = await QueryAsync(workspaceId: workspaceId, ct: ct);
    return sessions
        .Where(s => s.SessionRole == SessionRole.Main)
        .Where(s => string.Equals(s.PrincipalKind, principalKind, StringComparison.OrdinalIgnoreCase))
        .Where(s => string.Equals(s.PrincipalId, principalId, StringComparison.Ordinal))
        .OrderByDescending(s => s.LastActiveAt)
        .FirstOrDefault();
}
```

If the repository interface requires the method, add the same signature to `ISessionRepository`.

- [ ] **Step 3: Add controller request**

Add a request record to `Source/PuddingController/Controllers/SessionController.cs`.

```csharp
public sealed record EnsureMainSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string PrincipalKind { get; init; }
    public required string PrincipalId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? Title { get; init; }
}
```

- [ ] **Step 4: Add internal controller endpoint**

Add this action to `SessionController`.

```csharp
[HttpPost("main")]
public async Task<ActionResult<SessionRecord>> EnsureMain(
    [FromBody] EnsureMainSessionRequest req,
    CancellationToken ct)
{
    var principalKind = req.PrincipalKind.Trim().ToLowerInvariant();
    if (principalKind is not "agent" and not "group")
        return BadRequest(new { message = "principalKind must be agent or group" });

    var existing = await _sessions.FindMainAsync(req.WorkspaceId, principalKind, req.PrincipalId, ct);
    if (existing is not null)
        return Ok(existing);

    var userId = User.Identity?.Name ?? "admin";
    var title = string.IsNullOrWhiteSpace(req.Title) ? "主线" : req.Title.Trim();
    var session = new SessionRecord
    {
        SessionId = Guid.NewGuid().ToString("N"),
        WorkspaceId = req.WorkspaceId,
        AgentTemplateId = req.AgentTemplateId,
        AgentInstanceId = principalKind == "agent" ? req.PrincipalId : null,
        ChannelId = "admin",
        OwnerUserId = userId,
        SessionType = SessionType.ServiceSession,
        SessionRole = SessionRole.Main,
        PrincipalKind = principalKind,
        PrincipalId = req.PrincipalId,
        RootSessionId = null,
        ParentSessionId = null,
        Status = SessionStatus.Active,
        Title = title,
    };
    await _sessions.CreateAsync(session, ct);
    return Ok(session);
}
```

- [ ] **Step 5: Run targeted tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "EnsureMainSession"
```

Expected: pass after Task 3 exposes the Platform wrapper endpoint.

---

### Task 3: Expose Platform Main Session API

**Files:**
- Modify: `Source/PuddingPlatform/Services/PlatformApiClient.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/SessionApiController.cs`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Test: `Source/PuddingWebApiTests/SessionApiControllerTests.cs`

- [ ] **Step 1: Add Platform API client request type**

Add this record near other session requests in `PlatformApiClient.cs` or a shared request file used by the client.

```csharp
public sealed record EnsureMainSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string PrincipalKind { get; init; }
    public required string PrincipalId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? Title { get; init; }
}
```

- [ ] **Step 2: Add Platform API client method**

Add this method to `PlatformApiClient`.

```csharp
public async Task<SessionRecord?> EnsureMainSessionAsync(
    EnsureMainSessionRequest request,
    CancellationToken ct = default)
{
    var resp = await _http.PostAsJsonAsync("/api/session/main", request, ct);
    if (!resp.IsSuccessStatusCode) return null;
    return await resp.Content.ReadFromJsonAsync<SessionRecord>(ct);
}
```

- [ ] **Step 3: Add public Platform endpoint**

Add this action to `SessionApiController`.

```csharp
[Authorize]
[HttpPost("main")]
public async Task<ActionResult<SessionRecord>> EnsureMain(
    [FromBody] Services.EnsureMainSessionRequest req,
    CancellationToken ct)
{
    var session = await _api.EnsureMainSessionAsync(req, ct);
    return session is null ? BadRequest() : Ok(session);
}
```

- [ ] **Step 4: Add frontend API types**

Update `Source/PuddingPlatformAdmin/src/services/platform/api.ts`.

```ts
export type SessionRole = 'Main' | 'Task' | 'Branch' | 'Audit';

export interface SessionRecord {
  sessionId: string;
  workspaceId: string;
  agentTemplateId: string;
  channelId: string;
  ownerUserId: string;
  sessionType: SessionType;
  sessionRole?: SessionRole;
  status: SessionStatus;
  runtimeNodeId?: string;
  agentInstanceId?: string;
  parentSessionId?: string;
  rootSessionId?: string;
  principalKind?: 'agent' | 'group';
  principalId?: string;
  title?: string;
  createdAt: string;
  lastActiveAt: string;
}
```

Add the request and function.

```ts
export interface EnsureMainSessionRequest {
  workspaceId: string;
  principalKind: 'agent' | 'group';
  principalId: string;
  agentTemplateId: string;
  title?: string;
}

export async function ensureMainSession(req: EnsureMainSessionRequest): Promise<SessionRecord> {
  return request('/api/sessions/main', { method: 'POST', data: req });
}
```

- [ ] **Step 5: Run session API tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "SessionApiControllerTests"
```

Expected: pass.

---

### Task 4: Route Chat State Through Selected Agent Contact

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.recovery.test.ts`

- [ ] **Step 1: Add focused hook utility tests**

Add tests for contact selection helpers in the existing hook test file.

```ts
import {
  resolveSelectedContact,
  shouldUseMainSessionForAgentChange,
} from './useChatState';

it('resolves agent contact from route selection', () => {
  expect(resolveSelectedContact({ workspaceId: 'default', agentId: 'agent-a' })).toEqual({
    kind: 'agent',
    id: 'agent-a',
  });
});

it('uses main session when agent changes without explicit session', () => {
  expect(shouldUseMainSessionForAgentChange({ agentId: 'agent-a', sessionId: undefined })).toBe(true);
  expect(shouldUseMainSessionForAgentChange({ agentId: 'agent-a', sessionId: 'task-1' })).toBe(false);
});
```

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/hooks/useChatState.recovery.test.ts --runInBand
```

Expected: fail because helpers are not exported.

- [ ] **Step 2: Export contact selection helpers**

Add to `useChatState.ts`.

```ts
export interface SelectedChatContact {
  kind: 'agent' | 'group';
  id: string;
  mainSessionId?: string;
}

export function resolveSelectedContact(selection: ChatRouteSelection): SelectedChatContact | null {
  if (selection.agentId) return { kind: 'agent', id: selection.agentId };
  return null;
}

export function shouldUseMainSessionForAgentChange(selection: ChatRouteSelection): boolean {
  return Boolean(selection.agentId && !selection.sessionId);
}
```

- [ ] **Step 3: Add ensure-main-session flow**

Import `ensureMainSession` and call it when `workspaceId` and `agentId` are resolved and the URL does not carry an explicit task session.

```ts
const ensureAgentMainSession = useCallback(async (
  nextWorkspaceId: string,
  nextAgentId: string,
): Promise<string | undefined> => {
  const agent = agents.find(a => a.agentId === nextAgentId);
  if (!agent) return undefined;

  const session = await ensureMainSession({
    workspaceId: nextWorkspaceId,
    principalKind: 'agent',
    principalId: nextAgentId,
    agentTemplateId: agent.sourceTemplateId ?? 'global:general-assistant',
    title: getAgentName(agent),
  });

  sessionIdRef.current = session.sessionId;
  setSelectedSessionId(session.sessionId);
  setMainSessionId(session.sessionId);
  return session.sessionId;
}, [agents]);
```

- [ ] **Step 4: Replace agent-change reset behavior**

In `onAgentChange` handling in `index.tsx` or the hook callback it calls, replace immediate `resetConversation` with the ensure-main-session flow.

```ts
const sessionId = await chat.ensureAgentMainSession(chat.workspaceId, v);
history.replace(buildChatPathWithQuery(
  { workspaceId: chat.workspaceId, agentId: v, sessionId },
  location.search,
));
```

Expose `ensureAgentMainSession` from the hook return value.

- [ ] **Step 5: Run focused frontend tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/hooks/useChatState.recovery.test.ts --runInBand
```

Expected: pass.

---

### Task 5: Replace Primary Session Sidebar With Agent Contacts

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentContactSidebar.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.test.tsx`

- [ ] **Step 1: Add rendering test**

Create or update `ChatLayout.test.tsx` so it renders agents in the left sidebar and does not show "新对话" as the primary action.

```tsx
it('renders agent contacts as the primary chat sidebar', () => {
  render(<ChatLayout {...baseProps} agents={[
    { agentId: 'agent-a', name: 'assistant', displayName: 'Assistant', isEnabled: true, isFrozen: false },
  ]} agentId="agent-a" />);

  expect(screen.getByText('Assistant')).toBeTruthy();
  expect(screen.queryByText('新对话')).toBeNull();
});
```

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/ChatLayout.test.tsx --runInBand
```

Expected: fail because `AgentContactSidebar` is not used.

- [ ] **Step 2: Create AgentContactSidebar component**

Use Ant Design `Avatar`, `Badge`, `Input`, `Popover`, and `Tooltip`.

```tsx
import { Avatar, Badge, Button, Input, Popover, Tooltip } from 'antd';
import { MenuFoldOutlined, SearchOutlined, TeamOutlined } from '@ant-design/icons';
import React, { useMemo, useState } from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import { useChatStyles } from '../styles';
import { getAgentName } from '../hooks/useChatState';

interface AgentContactSidebarProps {
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
  agents: WorkspaceAgentDto[];
  selectedAgentId?: string;
  loading: boolean;
  unreadCounts?: Record<string, number>;
  onSelectAgent: (agentId: string) => void;
}

const AgentContactSidebar: React.FC<AgentContactSidebarProps> = ({
  sidebarOpen,
  onToggleSidebar,
  agents,
  selectedAgentId,
  loading,
  unreadCounts,
  onSelectAgent,
}) => {
  const { styles, cx } = useChatStyles();
  const [query, setQuery] = useState('');
  const filtered = useMemo(() => agents.filter(agent => {
    const haystack = `${getAgentName(agent)} ${agent.description ?? ''}`.toLowerCase();
    return haystack.includes(query.trim().toLowerCase());
  }), [agents, query]);

  return (
    <aside className={cx(styles.sidebar, !sidebarOpen && styles.sidebarCollapsed)} aria-label="Agent contacts">
      <div className={styles.sidebarHeader}>
        <div className={styles.groupLabel}>Agents</div>
        <Tooltip title="收起">
          <Button type="text" size="small" icon={<MenuFoldOutlined />} onClick={onToggleSidebar} />
        </Tooltip>
      </div>
      <div className={styles.sidebarSearch}>
        <Input
          placeholder="搜索 Agent"
          allowClear
          size="small"
          value={query}
          onChange={event => setQuery(event.target.value)}
          prefix={<SearchOutlined />}
        />
      </div>
      <div className={styles.sessionList} aria-busy={loading}>
        {filtered.map(agent => {
          const name = getAgentName(agent);
          const unread = unreadCounts?.[agent.agentId] ?? 0;
          const summary = (
            <div>
              <div>{agent.description || '当前没有工作摘要'}</div>
              <div>状态：空闲</div>
            </div>
          );
          return (
            <Popover key={agent.agentId} content={summary} placement="right">
              <button
                type="button"
                className={cx(styles.sessionItem, agent.agentId === selectedAgentId && styles.sessionItemActive)}
                onClick={() => onSelectAgent(agent.agentId)}
                data-testid={`chat-agent-${agent.agentId}`}
              >
                <Badge count={unread} size="small">
                  <Avatar src={agent.avatarUrl}>{name.slice(0, 1)}</Avatar>
                </Badge>
                <span className={styles.sessionTitle}>{name}</span>
              </button>
            </Popover>
          );
        })}
        <div className={styles.groupLabel}>Groups</div>
        <div className={styles.sidebarEmpty}>
          <TeamOutlined /> 群组即将接入
        </div>
      </div>
    </aside>
  );
};

export default AgentContactSidebar;
```

- [ ] **Step 3: Wire ChatLayout**

Replace the primary `SessionSidebar` import and render path with `AgentContactSidebar`.

```tsx
<AgentContactSidebar
  sidebarOpen={props.sidebarOpen}
  onToggleSidebar={props.onToggleSidebar}
  agents={props.agents}
  selectedAgentId={props.agentId}
  loading={props.agentLoading}
  unreadCounts={props.unreadCounts}
  onSelectAgent={(agentId) => props.onAgentChange(agentId)}
/>
```

Keep `SessionSidebar` available for the later task/history panel instead of deleting it in this task.

- [ ] **Step 4: Run component test**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/ChatLayout.test.tsx --runInBand
```

Expected: pass.

---

### Task 6: Make Chat Details Progressive And Low Noise

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ComposerFeedbackStrip.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageProcessSummary.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentMessageBubble.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.test.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageProcessSummary.test.tsx`

- [ ] **Step 1: Add compact sub-task indicator test**

Update the composer feedback test expectations so sub-task state is visible as a compact capability indicator.

```tsx
expect(screen.getByLabelText('3 个子任务运行中')).toBeTruthy();
expect(screen.queryByText('子任务 3')).toBeNull();
```

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/IntentConsole.test.tsx --runInBand
```

Expected: fail because the current strip prints `子任务 3`.

- [ ] **Step 2: Compact ComposerFeedbackStrip**

Change sub-task rendering to an icon/count with an accessible label.

```tsx
items.push({
  label: state.subAgentsRunning > 0 ? `${state.subAgentsRunning}` : '',
  color: state.subAgentsRunning > 0 ? DOT_CLASSES.context.active : DOT_CLASSES.context.idle,
  show: true,
  ariaLabel: state.subAgentsRunning > 0
    ? `${state.subAgentsRunning} 个子任务运行中`
    : '没有子任务运行',
});
```

Render `aria-label` on the item and keep the visible label short.

- [ ] **Step 3: Preserve folded reasoning summaries**

Keep existing reasoning tests green:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/MessageProcessSummary.test.tsx --runInBand
```

Expected: pass. Unpublished private reasoning remains hidden; structured reasoning summaries and process projections stay folded until deliberate expansion.

- [ ] **Step 4: Add hover/click affordance labels**

For reasoning and process summary controls, use labels that describe user-facing behavior:

```tsx
aria-label="展开思考过程"
aria-label="查看运行细节"
aria-label="查看子任务"
```

Keep raw event ids outside the default collapsed view.

- [ ] **Step 5: Run focused UI tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/IntentConsole.test.tsx src/pages/chat/components/MessageProcessSummary.test.tsx --runInBand
```

Expected: pass.

---

### Task 7: Add Task And Branch Secondary Entry Points

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/SessionSidebar.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.test.tsx`

- [ ] **Step 1: Add test for secondary session language**

Add a test that the main header or secondary surface uses task language instead of primary session language.

```tsx
expect(screen.getByRole('button', { name: '新建任务' })).toBeTruthy();
expect(screen.queryByRole('button', { name: '新对话' })).toBeNull();
```

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/ChatMain.test.tsx --runInBand
```

Expected: fail before UI copy changes.

- [ ] **Step 2: Rename visible session actions**

Change user-facing labels:

```tsx
新对话 -> 新建任务
搜索会话 -> 搜索任务或历史
归档会话 -> 归档任务
删除会话 -> 删除任务
```

Keep internal function names unchanged in this task unless renaming is required for clarity.

- [ ] **Step 3: Scope session list to selected contact**

Filter or group sessions by:

```ts
session.principalKind === 'agent' && session.principalId === selectedAgentId
```

Fallback for legacy sessions:

```ts
session.agentInstanceId === selectedAgentId || session.agentTemplateId === selectedAgent?.sourceTemplateId
```

- [ ] **Step 4: Run focused UI test**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/ChatMain.test.tsx --runInBand
```

Expected: pass.

---

### Task 8: Backend Chat Fallback Uses Agent Main Session

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- Test: `Source/PuddingWebApiTests\MessageApiControllerTests.cs`

- [ ] **Step 1: Add fallback behavior test**

Add a test that posts chat without `sessionId` and verifies the returned session is the main session for the Agent.

```csharp
[TestMethod]
public async Task SendMessageWithoutSession_UsesAgentMainSession()
{
    var mainResp = await _client.PostAsJsonAsync("/api/sessions/main", new
    {
        workspaceId = "default",
        principalKind = "agent",
        principalId = "default",
        agentTemplateId = "global:general-assistant",
        title = "General Assistant"
    });
    mainResp.EnsureSuccessStatusCode();
    var main = await mainResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

    var payload = new
    {
        messageText = "你好",
        agentId = "default"
    };
    var sendResp = await _client.PostAsJsonAsync("/api/workspaces/default/chat/message", payload);
    sendResp.EnsureSuccessStatusCode();
    var body = await sendResp.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOpts);

    Assert.AreEqual(main!.SessionId, body!["sessionId"]);
}
```

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "SendMessageWithoutSession_UsesAgentMainSession"
```

Expected: fail until `ChatApiController` resolves main sessions.

- [ ] **Step 2: Resolve main session before dispatch**

In `ChatApiController`, before sending to `PlatformApiClient.SendMessageAsync`, ensure the request session id is set for normal Agent chat.

```csharp
if (string.IsNullOrWhiteSpace(req.SessionId) && !string.IsNullOrWhiteSpace(req.AgentId))
{
    var main = await apiClient.EnsureMainSessionAsync(new Services.EnsureMainSessionRequest
    {
        WorkspaceId = workspaceId,
        PrincipalKind = "agent",
        PrincipalId = req.AgentId,
        AgentTemplateId = primaryDispatch.Template.TemplateId,
        Title = primaryDispatch.DisplayName,
    }, ct);
    if (main is not null)
        req = req with { SessionId = main.SessionId };
}
```

Use the local dispatch/template properties that exist in the current file after reading `ResolveChatAgentDispatchAsync`.

- [ ] **Step 3: Run targeted backend test**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "SendMessageWithoutSession_UsesAgentMainSession"
```

Expected: pass.

---

### Task 9: Group Chat Model Reservation

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentContactSidebar.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentContactSidebar.test.tsx`

- [ ] **Step 1: Add group placeholder rendering test**

Add a test that the sidebar can render an empty Groups section without enabling group routing.

```tsx
expect(screen.getByText('Groups')).toBeTruthy();
expect(screen.getByText('群组即将接入')).toBeTruthy();
```

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/AgentContactSidebar.test.tsx --runInBand
```

Expected: fail until the component and test exist.

- [ ] **Step 2: Add group contact type**

Add to `api.ts`.

```ts
export interface AgentGroupContactDto {
  groupId: string;
  name: string;
  description?: string;
  avatarUrl?: string;
  memberAgentIds: string[];
  mainSessionId?: string;
  status?: 'idle' | 'working' | 'waiting' | 'blocked' | 'offline';
}
```

- [ ] **Step 3: Keep group UI inert in MVP**

Render the Groups section but do not wire message sending for groups.

```tsx
<div className={styles.groupLabel}>Groups</div>
<div className={styles.sidebarEmpty}>群组即将接入</div>
```

- [ ] **Step 4: Run group sidebar test**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/components/AgentContactSidebar.test.tsx --runInBand
```

Expected: pass.

---

### Task 10: Full Verification And Browser QA

**Files:**
- No production files.
- Evidence target: final implementation notes or task-card summary.

- [ ] **Step 1: Run backend focused tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "EnsureMainSession|SendMessageWithoutSession_UsesAgentMainSession"
```

Expected: pass.

- [ ] **Step 2: Run frontend focused tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
npx jest src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/components/ChatLayout.test.tsx src/pages/chat/components/IntentConsole.test.tsx src/pages/chat/components/MessageProcessSummary.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 3: Run broader build check**

Run:

```powershell
dotnet build PuddingAgentNetwork.slnx
```

Expected: pass. If pre-existing unrelated frontend TypeScript issues remain, record them separately and do not mix them with this change.

- [ ] **Step 4: Open the local chat page**

Run the app through the existing development path:

```powershell
.\dev-up.ps1 -Status
```

If not running:

```powershell
.\dev-up.ps1
```

Open:

```text
http://localhost/admin/chat?workspaceId=default
```

Expected:

- Left sidebar shows Agent contacts as the primary list.
- Clicking an Agent opens or restores the same main session.
- The page still reads like a normal chat app.
- Reasoning and process details are folded by default.
- Sub-task state is compact in the composer.
- Hover over an Agent contact shows a short work summary.
- No text overlaps at desktop and mobile widths.

- [ ] **Step 5: Verify repeated Agent switching**

Manual flow:

1. Open Agent A.
2. Send a short message.
3. Switch to Agent B.
4. Switch back to Agent A.
5. Confirm Agent A returns to the same main timeline.

Expected: no arbitrary new session appears from switching Agents.

---

## Self-Review

- Spec coverage: Agent-first navigation, workspace+agent main sessions, lightweight chat UI, progressive disclosure, compact sub-task indicators, and group reservation are covered by Tasks 1-10.
- Placeholder scan: the plan uses concrete files, commands, expected outcomes, and code snippets.
- Type consistency: `SessionRole`, `PrincipalKind`, `PrincipalId`, `SelectedChatContact`, and `ensureMainSession` names are consistent across backend, frontend API, and hook tasks.
