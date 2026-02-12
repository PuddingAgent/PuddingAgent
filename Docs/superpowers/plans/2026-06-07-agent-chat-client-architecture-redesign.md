# Agent Chat Client Architecture Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild `/admin/chat` as an Agent-first single-user SPA client with server-owned Agent execution, local cache, explicit run state, stable React rendering, and a reserved owner field for future compatibility.

**Architecture:** Add server-side Agent status/run/conversation projections over the existing session and event facts. Product behavior is keyed by `workspace + agent` because Pudding is currently single-user; internally, schemas, DTOs, unique keys, and cache keys also carry one stable default `ownerUserId` such as `single-user` so a later account model has a compatible field. Add a frontend client store, browser `clientId`, and IndexedDB cache so Agent switching hydrates from snapshots first and streams only patch state from a cursor.

**Tech Stack:** C#/.NET 10, ASP.NET Core controllers/services, EF Core/SQLite contexts already used by Pudding, React 19, TypeScript, Ant Design, Jest, React Testing Library, browser IndexedDB.

---

## File Structure

- Create `Source/PuddingCore/Platform/AgentRunRecord.cs`: shared run lifecycle record with owner user and issuing client id.
- Create `Source/PuddingCore/Platform/AgentProjectionDtos.cs`: shared DTOs for status, run, and conversation projections.
- Modify `Source/PuddingPlatform/Data/ProjectDbContext.cs`: register run records if platform DB owns the table.
- Modify `Source/PuddingPlatform/Data/ProjectSQliteContext.cs`: register run records for local SQLite mode if both contexts are active.
- Create `Source/PuddingPlatform/Services/AgentChat/AgentRunProjectionService.cs`: builds `AgentRunView` and `AgentStatusProjection`.
- Create `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs`: builds `AgentConversationView`.
- Create `Source/PuddingPlatform/Controllers/Api/AgentChatApiController.cs`: exposes Agent-first client API.
- Test `Source/PuddingWebApiTests/AgentChatApiControllerTests.cs`: API and projection behavior.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/types.ts`: client-side domain types.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/agentChatApi.ts`: Agent-first API wrapper.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/clientIdentity.ts`: browser `clientId` manager.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/localCache.ts`: IndexedDB/localStorage adapter.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts`: external store and selectors.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts`: stream/replay coordinator.
- Create `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useAgentChatClient.ts`: React hook facade over the store.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/index.tsx`: select Agent through the client store.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/components/SessionSidebar.tsx`: read Agent statuses from projection.
- Modify `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`: receive conversation view and active run output.
- Create tests under `Source/PuddingPlatformAdmin/src/pages/chat/client/*.test.ts`.

---

### Task 1: Define Agent Run And Projection Contracts

**Files:**
- Create: `Source/PuddingCore/Platform/AgentRunRecord.cs`
- Create: `Source/PuddingCore/Platform/AgentProjectionDtos.cs`
- Test: `Source/PuddingWebApiTests/AgentChatApiControllerTests.cs`

- [ ] **Step 1: Write the failing DTO contract test**

Create `Source/PuddingWebApiTests/AgentChatApiControllerTests.cs` with this first test:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PuddingWebApiTests;

[TestClass]
public sealed class AgentChatApiControllerTests
{
    [TestMethod]
    public async Task AgentStatusEndpoint_ReturnsWorkspaceAgentProjectionShape()
    {
        using var app = await TestWebApplicationFactory.CreateAsync();
        var client = app.CreateClient();

        var response = await client.GetAsync("/api/workspaces/default/agents/status");

        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>();
        Assert.IsNotNull(list);
    }

    private sealed record AgentStatusProjectionDto(
        string WorkspaceId,
        string OwnerUserId,
        string AgentId,
        string MainSessionId,
        string Status,
        string Summary,
        long EventCursor,
        string UpdatedAt);
}
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter AgentStatusEndpoint_ReturnsWorkspaceAgentProjectionShape --no-restore -p:BaseOutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\codex-test-bin\
```

Expected: fail because `/api/workspaces/default/agents/status` does not exist.

- [ ] **Step 3: Add shared run record**

Create `Source/PuddingCore/Platform/AgentRunRecord.cs`:

```csharp
namespace PuddingCore.Platform;

/// <summary>Agent work lifecycle record used by Agent-first chat clients.</summary>
public sealed record AgentRunRecord
{
    public required string RunId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string AgentId { get; init; }
    public required string MainSessionId { get; init; }
    public string? CommandClientId { get; init; }
    public string? InputMessageId { get; init; }
    public string? OutputMessageId { get; init; }
    public required string Status { get; init; }
    public string StatusText { get; init; } = "";
    public string Summary { get; init; } = "";
    public long EventCursor { get; init; }
    public string OutputSnapshotJson { get; init; } = "{}";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
```

- [ ] **Step 4: Add shared projection DTOs**

Create `Source/PuddingCore/Platform/AgentProjectionDtos.cs`:

```csharp
namespace PuddingCore.Platform;

public sealed record AgentStatusProjection(
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    string Status,
    string? ActiveRunId,
    string Summary,
    int UnreadCount,
    long EventCursor,
    DateTimeOffset UpdatedAt);

public sealed record AgentRunView(
    string RunId,
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    string? CommandClientId,
    string Status,
    string StatusText,
    string Summary,
    long EventCursor,
    AgentOutputSnapshot OutputSnapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AgentOutputSnapshot(
    string Markdown,
    IReadOnlyList<ProcessSummaryItem> ProcessItems);

public sealed record ProcessSummaryItem(
    string Id,
    string Kind,
    string Status,
    string Text,
    DateTimeOffset Timestamp);

public sealed record AgentConversationView(
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    IReadOnlyList<ConversationMessageView> Messages,
    AgentRunView? ActiveRun,
    long EventCursor,
    DateTimeOffset UpdatedAt);

public sealed record ConversationMessageView(
    string MessageId,
    string? RunId,
    string Role,
    string SourceId,
    string SourceName,
    DateTimeOffset CreatedAt,
    string Content,
    string Status,
    IReadOnlyList<ProcessSummaryItem> ProcessItems);
```

- [ ] **Step 5: Run a compile check**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter AgentChatApiControllerTests --no-restore -p:BaseOutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\codex-test-bin\
```

Expected: still fail at missing endpoint, not at missing DTO types.

---

### Task 2: Add Agent Status Projection API

**Files:**
- Create: `Source/PuddingPlatform/Services/AgentChat/AgentRunProjectionService.cs`
- Create: `Source/PuddingPlatform/Controllers/Api/AgentChatApiController.cs`
- Modify: `Source/PuddingPlatform/Program.cs` if service registration is not assembly-scanned
- Test: `Source/PuddingWebApiTests/AgentChatApiControllerTests.cs`

- [ ] **Step 1: Add the projection service interface and implementation**

Create `Source/PuddingPlatform/Services/AgentChat/AgentRunProjectionService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PuddingCore.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.AgentChat;

public interface IAgentRunProjectionService
{
    Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(string workspaceId, string ownerUserId, CancellationToken ct);
}

public sealed class AgentRunProjectionService(ProjectDbContext db) : IAgentRunProjectionService
{
    private const string DefaultOwnerUserId = "single-user";

    public async Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(string workspaceId, string ownerUserId, CancellationToken ct)
    {
        ownerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? DefaultOwnerUserId : ownerUserId;
        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Where(s => s.OwnerUserId == ownerUserId)
            .Where(s => s.SessionRole == "Main")
            .Where(s => s.PrincipalKind == "agent")
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync(ct);

        return sessions.Select(s => new AgentStatusProjection(
            s.WorkspaceId,
            ownerUserId,
            s.PrincipalId ?? s.AgentTemplateId,
            s.SessionId,
            "idle",
            null,
            "",
            0,
            0,
            s.LastActiveAt)).ToList();
    }
}
```

- [ ] **Step 2: Add the API controller**

Create `Source/PuddingPlatform/Controllers/Api/AgentChatApiController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using PuddingCore.Platform;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Route("api/workspaces/{workspaceId}/agents")]
public sealed class AgentChatApiController(IAgentRunProjectionService projection) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<IReadOnlyList<AgentStatusProjection>>> GetStatuses(
        string workspaceId,
        CancellationToken ct)
    {
        var ownerUserId = ResolveSingleUserOwnerId();
        var statuses = await projection.GetWorkspaceAgentStatusesAsync(workspaceId, ownerUserId, ct);
        return Ok(statuses);
    }

    private static string ResolveSingleUserOwnerId() => "single-user";
}
```

- [ ] **Step 3: Register the service**

If `Program.cs` does not already register services by convention, add:

```csharp
builder.Services.AddScoped<IAgentRunProjectionService, AgentRunProjectionService>();
```

- [ ] **Step 4: Run the endpoint test**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter AgentStatusEndpoint_ReturnsWorkspaceAgentProjectionShape --no-restore -p:BaseOutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\codex-test-bin\
```

Expected: pass or fail only because the test factory needs seeded main sessions. If it returns an empty list with HTTP 200, the contract is acceptable for this task.

---

### Task 3: Add Conversation Projection API

**Files:**
- Create: `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/AgentChatApiController.cs`
- Test: `Source/PuddingWebApiTests/AgentChatApiControllerTests.cs`

- [ ] **Step 1: Add failing conversation endpoint test**

Add to `AgentChatApiControllerTests`:

```csharp
[TestMethod]
public async Task AgentConversationEndpoint_ReturnsRenderableConversationView()
{
    using var app = await TestWebApplicationFactory.CreateAsync();
    var client = app.CreateClient();

    var response = await client.GetAsync("/api/workspaces/default/agents/agent-alpha/conversation");

    Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
    response.EnsureSuccessStatusCode();
}
```

- [ ] **Step 2: Add projection service**

Create `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PuddingCore.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.AgentChat;

public interface IAgentConversationProjectionService
{
    Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);
}

public sealed class AgentConversationProjectionService(ProjectDbContext db) : IAgentConversationProjectionService
{
    private const string DefaultOwnerUserId = "single-user";

    public async Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct)
    {
        ownerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? DefaultOwnerUserId : ownerUserId;
        var main = await db.Sessions
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Where(s => s.OwnerUserId == ownerUserId)
            .Where(s => s.SessionRole == "Main")
            .Where(s => s.PrincipalKind == "agent")
            .Where(s => s.PrincipalId == agentId)
            .OrderByDescending(s => s.LastActiveAt)
            .FirstOrDefaultAsync(ct);

        if (main is null)
        {
            return new AgentConversationView(
                workspaceId,
                ownerUserId,
                agentId,
                "",
                Array.Empty<ConversationMessageView>(),
                null,
                0,
                DateTimeOffset.UtcNow);
        }

        var messages = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == main.SessionId)
            .OrderBy(m => m.CreatedAt)
            .Take(100)
            .Select(m => new ConversationMessageView(
                m.Id.ToString(),
                null,
                m.Role,
                agentId,
                main.Title ?? agentId,
                m.CreatedAt,
                m.Content,
                "succeeded",
                Array.Empty<ProcessSummaryItem>()))
            .ToListAsync(ct);

        return new AgentConversationView(
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            messages,
            null,
            0,
            main.LastActiveAt);
    }
}
```

- [ ] **Step 3: Register and expose the endpoint**

Register:

```csharp
builder.Services.AddScoped<IAgentConversationProjectionService, AgentConversationProjectionService>();
```

Add to `AgentChatApiController`:

```csharp
[HttpGet("{agentId}/conversation")]
public async Task<ActionResult<AgentConversationView>> GetConversation(
    string workspaceId,
    string agentId,
    [FromServices] IAgentConversationProjectionService conversation,
    CancellationToken ct)
{
    var ownerUserId = ResolveSingleUserOwnerId();
    return Ok(await conversation.GetConversationAsync(workspaceId, ownerUserId, agentId, ct));
}
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter AgentChatApiControllerTests --no-restore -p:BaseOutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\codex-test-bin\
```

Expected: pass for endpoint shape.

---

### Task 4: Add Frontend Agent Chat API Types

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/types.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/agentChatApi.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/clientIdentity.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/client/agentChatApi.test.ts`

- [ ] **Step 1: Add TypeScript domain types**

Create `types.ts`:

```ts
export type AgentRunStatus = 'queued' | 'running' | 'waiting' | 'succeeded' | 'failed' | 'cancelled';

export interface AgentStatusProjection {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  status: 'idle' | 'running' | 'waiting' | 'failed' | 'offline';
  activeRunId?: string | null;
  summary: string;
  unreadCount: number;
  eventCursor: number;
  updatedAt: string;
}

export interface ProcessSummaryItem {
  id: string;
  kind: string;
  status: string;
  text: string;
  timestamp: string;
}

export interface AgentOutputSnapshot {
  markdown: string;
  processItems: ProcessSummaryItem[];
}

export interface AgentRunView {
  runId: string;
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  status: AgentRunStatus;
  statusText: string;
  summary: string;
  eventCursor: number;
  outputSnapshot: AgentOutputSnapshot;
  startedAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface ConversationMessageView {
  messageId: string;
  runId?: string | null;
  role: 'user' | 'agent' | 'system';
  sourceId: string;
  sourceName: string;
  createdAt: string;
  content: string;
  status: 'sending' | 'sent' | 'streaming' | 'succeeded' | 'failed' | 'cancelled';
  processItems: ProcessSummaryItem[];
}

export interface AgentConversationView {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  messages: ConversationMessageView[];
  activeRun?: AgentRunView | null;
  eventCursor: number;
  updatedAt: string;
}
```

- [ ] **Step 2: Add single-user owner and browser client identity helper**

Create `clientIdentity.ts`:

```ts
export const DEFAULT_AGENT_CHAT_OWNER_ID = 'single-user';

const CLIENT_ID_KEY = 'pudding.agentChat.clientId';

export function getBrowserClientId(): string {
  if (typeof localStorage === 'undefined') return 'test-client';
  const existing = localStorage.getItem(CLIENT_ID_KEY);
  if (existing) return existing;
  const next = `client-${crypto.randomUUID()}`;
  localStorage.setItem(CLIENT_ID_KEY, next);
  return next;
}
```

This helper keeps the current product single-user. `DEFAULT_AGENT_CHAT_OWNER_ID` is only an internal compatibility field for DTOs, cache keys, and future schema migration.

- [ ] **Step 3: Add API wrapper**

Create `agentChatApi.ts`:

```ts
import { request } from '@umijs/max';
import type { AgentConversationView, AgentStatusProjection } from './types';

export async function listAgentStatuses(workspaceId: string): Promise<AgentStatusProjection[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/status`, { method: 'GET' });
}

export async function getAgentConversation(workspaceId: string, agentId: string): Promise<AgentConversationView> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation`,
    { method: 'GET' },
  );
}
```

- [ ] **Step 4: Add API path test**

Create `agentChatApi.test.ts`:

```ts
import { getAgentConversation, listAgentStatuses } from './agentChatApi';

const requestMock = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => requestMock(...args),
}));

describe('agentChatApi', () => {
  beforeEach(() => requestMock.mockReset());

  it('uses Agent-first status and conversation endpoints', async () => {
    requestMock.mockResolvedValueOnce([]);
    await listAgentStatuses('default');
    expect(requestMock).toHaveBeenCalledWith('/api/workspaces/default/agents/status', { method: 'GET' });

    requestMock.mockResolvedValueOnce({ messages: [] });
    await getAgentConversation('default', 'agent/a');
    expect(requestMock).toHaveBeenCalledWith('/api/workspaces/default/agents/agent%2Fa/conversation', { method: 'GET' });
  });
});
```

- [ ] **Step 5: Run the test**

Run:

```powershell
npx jest src/pages/chat/client/agentChatApi.test.ts --runInBand
```

Expected: pass.

---

### Task 5: Add IndexedDB Local Cache Adapter

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/localCache.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/client/localCache.test.ts`

- [ ] **Step 1: Add memory-backed cache test first**

Create `localCache.test.ts`:

```ts
import { createMemoryAgentChatCache } from './localCache';

describe('agent chat local cache', () => {
  it('hydrates the last cached conversation for an agent', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      eventCursor: 12,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    await expect(cache.loadConversation('default', 'agent-a')).resolves.toMatchObject({
      ownerUserId: 'single-user',
      mainSessionId: 'session-a',
      eventCursor: 12,
    });
  });
});
```

- [ ] **Step 2: Implement cache interface and memory adapter**

Create `localCache.ts`:

```ts
import type { AgentConversationView, AgentStatusProjection } from './types';
import { DEFAULT_AGENT_CHAT_OWNER_ID } from './clientIdentity';

export interface AgentChatLocalCache {
  loadConversation(workspaceId: string, agentId: string, ownerUserId?: string): Promise<AgentConversationView | null>;
  saveConversation(view: AgentConversationView): Promise<void>;
  loadStatuses(workspaceId: string, ownerUserId?: string): Promise<AgentStatusProjection[]>;
  saveStatuses(workspaceId: string, statuses: AgentStatusProjection[], ownerUserId?: string): Promise<void>;
}

const normalizeOwner = (ownerUserId?: string) => ownerUserId || DEFAULT_AGENT_CHAT_OWNER_ID;
const conversationKey = (workspaceId: string, ownerUserId: string | undefined, agentId: string) =>
  `${workspaceId}::${normalizeOwner(ownerUserId)}::${agentId}`;
const statusKey = (workspaceId: string, ownerUserId?: string) => `${workspaceId}::${normalizeOwner(ownerUserId)}`;

export function createMemoryAgentChatCache(): AgentChatLocalCache {
  const conversations = new Map<string, AgentConversationView>();
  const statuses = new Map<string, AgentStatusProjection[]>();
  return {
    async loadConversation(workspaceId, agentId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return conversations.get(conversationKey(workspaceId, ownerUserId, agentId)) ?? null;
    },
    async saveConversation(view) {
      conversations.set(conversationKey(view.workspaceId, view.ownerUserId, view.agentId), view);
    },
    async loadStatuses(workspaceId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return statuses.get(statusKey(workspaceId, ownerUserId)) ?? [];
    },
    async saveStatuses(workspaceId, list, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      statuses.set(statusKey(workspaceId, ownerUserId), list);
    },
  };
}
```

- [ ] **Step 3: Add IndexedDB adapter**

Extend `localCache.ts` with:

```ts
export function createIndexedDbAgentChatCache(): AgentChatLocalCache {
  if (typeof indexedDB === 'undefined') return createMemoryAgentChatCache();

  const open = () =>
    new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open('pudding-agent-chat', 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('conversations')) db.createObjectStore('conversations');
        if (!db.objectStoreNames.contains('statuses')) db.createObjectStore('statuses');
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });

  const read = async <T>(storeName: string, key: string): Promise<T | null> => {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(storeName, 'readonly');
      const request = tx.objectStore(storeName).get(key);
      request.onsuccess = () => resolve((request.result as T | undefined) ?? null);
      request.onerror = () => reject(request.error);
      tx.oncomplete = () => db.close();
      tx.onerror = () => db.close();
    });
  };

  const write = async <T>(storeName: string, key: string, value: T): Promise<void> => {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(storeName, 'readwrite');
      tx.objectStore(storeName).put(value, key);
      tx.oncomplete = () => {
        db.close();
        resolve();
      };
      tx.onerror = () => {
        db.close();
        reject(tx.error);
      };
    });
  };

  return {
    loadConversation(workspaceId, agentId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return read<AgentConversationView>('conversations', conversationKey(workspaceId, ownerUserId, agentId));
    },
    saveConversation(view) {
      return write('conversations', conversationKey(view.workspaceId, view.ownerUserId, view.agentId), view);
    },
    async loadStatuses(workspaceId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return (await read<AgentStatusProjection[]>('statuses', statusKey(workspaceId, ownerUserId))) ?? [];
    },
    saveStatuses(workspaceId, list, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return write('statuses', statusKey(workspaceId, ownerUserId), list);
    },
  };
}
```

- [ ] **Step 4: Run cache tests**

Run:

```powershell
npx jest src/pages/chat/client/localCache.test.ts --runInBand
```

Expected: pass.

---

### Task 6: Add Client Store With No-Blank Agent Switching

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.test.ts`

- [ ] **Step 1: Write no-blank switch test**

Create `chatClientStore.test.ts`:

```ts
import { createAgentChatClientStore } from './chatClientStore';
import { createMemoryAgentChatCache } from './localCache';

describe('agent chat client store', () => {
  it('keeps a cached conversation visible immediately when switching agents', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-b',
      mainSessionId: 'session-b',
      messages: [{
        messageId: 'm1',
        role: 'agent',
        sourceId: 'agent-b',
        sourceName: 'Agent B',
        createdAt: '2026-06-07T00:00:00.000Z',
        content: 'cached answer',
        status: 'succeeded',
        processItems: [],
      }],
      eventCursor: 8,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async () => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-b',
          mainSessionId: 'session-b',
          messages: [],
          eventCursor: 9,
          updatedAt: '2026-06-07T00:00:01.000Z',
        }),
      },
    });

    await store.selectAgent('default', 'agent-b');

    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe('cached answer');
    expect(store.getSnapshot().isRefreshing).toBe(true);
  });
});
```

- [ ] **Step 2: Implement external store**

Create `chatClientStore.ts`:

```ts
import type { AgentConversationView, AgentStatusProjection } from './types';
import { DEFAULT_AGENT_CHAT_OWNER_ID } from './clientIdentity';
import type { AgentChatLocalCache } from './localCache';

export interface AgentChatApiPort {
  listStatuses(workspaceId: string): Promise<AgentStatusProjection[]>;
  getConversation(workspaceId: string, agentId: string): Promise<AgentConversationView>;
}

export interface AgentChatClientSnapshot {
  workspaceId?: string;
  ownerUserId: string;
  agentId?: string;
  statuses: AgentStatusProjection[];
  conversation: AgentConversationView | null;
  isRefreshing: boolean;
  error: string | null;
}

export function createAgentChatClientStore(input: { cache: AgentChatLocalCache; api: AgentChatApiPort }) {
  let snapshot: AgentChatClientSnapshot = {
    ownerUserId: DEFAULT_AGENT_CHAT_OWNER_ID,
    statuses: [],
    conversation: null,
    isRefreshing: false,
    error: null,
  };
  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach(listener => listener());
  const set = (next: Partial<AgentChatClientSnapshot>) => {
    snapshot = { ...snapshot, ...next };
    emit();
  };

  return {
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSnapshot() {
      return snapshot;
    },
    async selectAgent(workspaceId: string, agentId: string) {
      const ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID;
      const cached = await input.cache.loadConversation(workspaceId, agentId, ownerUserId);
      set({ workspaceId, ownerUserId, agentId, conversation: cached, isRefreshing: true, error: null });
      try {
        const fresh = await input.api.getConversation(workspaceId, agentId);
        await input.cache.saveConversation(fresh);
        if (snapshot.workspaceId === workspaceId && snapshot.ownerUserId === ownerUserId && snapshot.agentId === agentId) {
          set({ conversation: fresh, isRefreshing: false });
        }
      } catch (error) {
        if (snapshot.workspaceId === workspaceId && snapshot.ownerUserId === ownerUserId && snapshot.agentId === agentId) {
          set({ isRefreshing: false, error: error instanceof Error ? error.message : String(error) });
        }
      }
    },
  };
}
```

- [ ] **Step 3: Run store tests**

Run:

```powershell
npx jest src/pages/chat/client/chatClientStore.test.ts --runInBand
```

Expected: pass.

---

### Task 7: Add React Hook Facade

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useAgentChatClient.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useAgentChatClient.test.tsx`

- [ ] **Step 1: Add hook test**

Create `useAgentChatClient.test.tsx`:

```tsx
import { renderHook, waitFor } from '@testing-library/react';
import { useAgentChatClient } from './useAgentChatClient';
import { createMemoryAgentChatCache } from '../client/localCache';

describe('useAgentChatClient', () => {
  it('exposes selected cached conversation through React', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      eventCursor: 1,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    const { result } = renderHook(() => useAgentChatClient({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async () => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [],
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:01.000Z',
        }),
      },
    }));

    await result.current.selectAgent('default', 'agent-a');
    await waitFor(() => expect(result.current.snapshot.conversation?.mainSessionId).toBe('session-a'));
  });
});
```

- [ ] **Step 2: Implement hook**

Create `useAgentChatClient.ts`:

```ts
import { useMemo, useSyncExternalStore } from 'react';
import { createAgentChatClientStore, type AgentChatApiPort } from '../client/chatClientStore';
import type { AgentChatLocalCache } from '../client/localCache';

export function useAgentChatClient(input: { cache: AgentChatLocalCache; api: AgentChatApiPort }) {
  const store = useMemo(() => createAgentChatClientStore(input), [input]);
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  return {
    snapshot,
    selectAgent: store.selectAgent,
  };
}
```

- [ ] **Step 3: Run hook test**

Run:

```powershell
npx jest src/pages/chat/hooks/useAgentChatClient.test.tsx --runInBand
```

Expected: pass.

---

### Task 8: Migrate Contact Status To Projection

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/SessionSidebar.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/SessionSidebar.test.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.test.tsx`

- [ ] **Step 1: Add status projection prop test**

Extend `SessionSidebar.test.tsx`:

```tsx
it('renders working status from Agent status projection', () => {
  render(<SessionSidebar
    {...baseProps}
    workingAgentIds={[]}
    agentStatuses={{
      planner: {
        status: 'running',
        summary: '正在整理日志',
      },
    }}
  />);

  expect(screen.getByRole('button', { name: '规划 Agent 当前 工作中' })).toBeTruthy();
});
```

- [ ] **Step 2: Add prop type**

In `SessionSidebar.tsx` add:

```ts
type AgentStatusChipProjection = {
  status: 'idle' | 'running' | 'waiting' | 'failed' | 'offline';
  summary?: string;
};

agentStatuses?: Record<string, AgentStatusChipProjection>;
```

- [ ] **Step 3: Prefer projection status over legacy working ids**

In the Agent list mapping:

```ts
const projected = agentStatuses[agent.agentId];
const isWorking = projected
  ? projected.status === 'running' || projected.status === 'waiting'
  : workingAgentIds.includes(agent.agentId);
```

- [ ] **Step 4: Run component tests**

Run:

```powershell
npx jest src/pages/chat/components/SessionSidebar.test.tsx src/pages/chat/components/ChatLayout.test.tsx --runInBand
```

Expected: pass.

---

### Task 9: Split Stable Messages From Active Output

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/ActiveRunOutput.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx`

- [ ] **Step 1: Add render isolation test**

Extend `MessageList.test.tsx` with a mock active output component count:

```tsx
it('renders active run output separately from stable history', () => {
  render(<MessageList
    {...baseProps}
    conversationView={{
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      activeRun: {
        runId: 'run-a',
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-a',
        mainSessionId: 'session-a',
        status: 'running',
        statusText: '正在输出',
        summary: '',
        eventCursor: 1,
        outputSnapshot: { markdown: 'active output', processItems: [] },
        startedAt: '2026-06-07T00:00:00.000Z',
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
      eventCursor: 1,
      updatedAt: '2026-06-07T00:00:00.000Z',
    }}
  />);

  expect(screen.getByText('active output')).toBeTruthy();
});
```

- [ ] **Step 2: Create `ActiveRunOutput`**

Create:

```tsx
import React from 'react';
import ReactMarkdown from 'react-markdown';
import type { AgentRunView } from '../client/types';

export default React.memo(function ActiveRunOutput({ run }: { run: AgentRunView }) {
  return (
    <section aria-label="当前输出">
      <ReactMarkdown>{run.outputSnapshot.markdown}</ReactMarkdown>
    </section>
  );
});
```

- [ ] **Step 3: Wire into `MessageList` behind optional prop**

Add optional prop:

```ts
conversationView?: AgentConversationView | null;
```

Render `ActiveRunOutput` when `conversationView?.activeRun` exists.

- [ ] **Step 4: Run MessageList tests**

Run:

```powershell
npx jest src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected: pass.

---

### Task 10: Add Sync Engine Skeleton

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.test.ts`

- [ ] **Step 1: Add cursor replay test**

Create `syncEngine.test.ts`:

```ts
import { createAgentChatSyncEngine } from './syncEngine';

describe('agent chat sync engine', () => {
  it('requests events after the current cursor', async () => {
    const calls: string[] = [];
    const engine = createAgentChatSyncEngine({
      fetchEvents: async (_workspaceId, _agentId, after) => {
        calls.push(String(after));
        return { events: [], nextCursor: after };
      },
      applyEvents: () => undefined,
    });

    await engine.replay('default', 'agent-a', 42);

    expect(calls).toEqual(['42']);
  });
});
```

- [ ] **Step 2: Implement sync engine**

Create `syncEngine.ts`:

```ts
export interface AgentChatEventPage {
  events: unknown[];
  nextCursor: number;
}

export function createAgentChatSyncEngine(input: {
  fetchEvents(workspaceId: string, agentId: string, after: number): Promise<AgentChatEventPage>;
  applyEvents(events: unknown[]): void;
}) {
  return {
    async replay(workspaceId: string, agentId: string, after: number) {
      const page = await input.fetchEvents(workspaceId, agentId, after);
      input.applyEvents(page.events);
      return page.nextCursor;
    },
  };
}
```

- [ ] **Step 3: Run sync test**

Run:

```powershell
npx jest src/pages/chat/client/syncEngine.test.ts --runInBand
```

Expected: pass.

---

### Task 11: Integrate Behind A Feature Flag

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/index.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- Test: existing chat tests plus new client tests

- [ ] **Step 1: Add feature flag helper**

In `index.tsx`:

```ts
const useAgentClientArchitecture = localStorage.getItem('pudding-agent-client-arch') === '1';
```

- [ ] **Step 2: Route only Agent selection through the new store when enabled**

When flag is enabled, call `agentClient.selectAgent(workspaceId, agentId)` before legacy `ensureAgentMainSession`.

- [ ] **Step 3: Preserve legacy behavior when disabled**

Keep current flow for users without the flag. No existing test should change behavior unless it explicitly enables the flag.

- [ ] **Step 4: Run combined frontend tests**

Run:

```powershell
npx jest src/pages/chat/client src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/hooks/useChatState.selection.test.tsx src/pages/chat/components/SessionSidebar.test.tsx src/pages/chat/components/ChatLayout.test.tsx src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected: pass.

---

### Task 12: Verification And Cleanup

**Files:**
- Modify docs only if implementation diverges from this plan.

- [ ] **Step 1: Run backend focused tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --filter "AgentChatApiControllerTests|SessionApiControllerTests" --no-restore -p:BaseOutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\codex-test-bin\
```

Expected: pass.

- [ ] **Step 2: Run frontend focused tests**

Run:

```powershell
npx jest src/pages/chat/client src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/hooks/useChatState.selection.test.tsx src/pages/chat/components/SessionSidebar.test.tsx src/pages/chat/components/ChatLayout.test.tsx src/pages/chat/components/MessageList.test.tsx src/utils/debug.test.ts --runInBand
```

Expected: pass.

- [ ] **Step 3: Run TypeScript check**

Run:

```powershell
npm run tsc
```

Expected: pass after existing unrelated type errors in `src/components/PuddingAdminShell`, `src/components/PuddingEntityCard`, `src/components/PuddingPageHeader`, `src/components/PuddingStatusBadge`, `src/components/PuddingToolbar`, and `src/pages/agent-template-settings/sections/CapabilitySkillSection.test.tsx` are resolved or excluded by a separate cleanup.

- [ ] **Step 4: Manual browser validation**

Open:

```text
http://localhost/admin/chat?workspaceId=default
```

Validate:

- Agent A output continues while switching to Agent B.
- Switching back to Agent A shows cached output immediately.
- Status tags match server/client run projection, not selection.
- Runtime Inspector diagnostics remain separate from normal chat output.
- The message viewport does not flash the ready empty state during Agent switches.

- [ ] **Step 5: Commit**

Commit implementation slices separately:

```powershell
git add Source/PuddingCore/Platform/AgentRunRecord.cs Source/PuddingCore/Platform/AgentProjectionDtos.cs Source/PuddingPlatform/Services/AgentChat Source/PuddingPlatform/Controllers/Api/AgentChatApiController.cs Source/PuddingWebApiTests/AgentChatApiControllerTests.cs
git commit -m "feat(chat): add agent chat projection API"

git add Source/PuddingPlatformAdmin/src/pages/chat/client Source/PuddingPlatformAdmin/src/pages/chat/hooks/useAgentChatClient.ts Source/PuddingPlatformAdmin/src/pages/chat/hooks/useAgentChatClient.test.tsx
git commit -m "feat(chat): add agent chat client store"

git add Source/PuddingPlatformAdmin/src/pages/chat
git commit -m "feat(chat): integrate agent client architecture"
```

## Self-Review

- The plan covers server projection contracts, frontend API types, local cache, store, hook facade, status integration, active output isolation, sync engine, and verification.
- No task requires replacing the whole chat page at once.
- Existing stabilization fixes remain compatible because integration is feature-flagged before removal.
- The plan contains explicit file paths, test commands, and code snippets for each implementation slice.
