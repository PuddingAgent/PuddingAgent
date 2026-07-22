import {
  clearPerfEvents,
  getPerfEvents,
  setPerfDiagnosticsEnabled,
} from '@/utils/debug';
import { createAgentChatClientStore } from './chatClientStore';
import { createMemoryAgentChatCache } from './localCache';
import type { AgentConversationView } from './types';

describe('agent chat client store', () => {
  beforeEach(() => {
    clearPerfEvents();
    setPerfDiagnosticsEnabled(true);
  });

  afterEach(() => {
    setPerfDiagnosticsEnabled(false);
  });

  it('keeps a cached conversation visible immediately when switching agents', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-b',
      mainSessionId: 'session-b',
      messages: [
        {
          messageId: 'm1',
          role: 'agent',
          sourceId: 'agent-b',
          sourceName: 'Agent B',
          createdAt: '2026-06-07T00:00:00.000Z',
          content: 'cached answer',
          status: 'succeeded',
          processItems: [],
        },
      ],
      eventCursor: 8,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    let resolveFresh: (value: AgentConversationView) => void = () => undefined;
    const freshPromise = new Promise<AgentConversationView>((resolve) => {
      resolveFresh = resolve;
    });

    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async () => freshPromise,
      },
    });

    const refresh = store.selectAgent('default', 'agent-b');
    await Promise.resolve();
    await Promise.resolve();

    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe(
      'cached answer',
    );
    expect(store.getSnapshot().isRefreshing).toBe(true);

    resolveFresh({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-b',
      mainSessionId: 'session-b',
      messages: [],
      eventCursor: 9,
      updatedAt: '2026-06-07T00:00:01.000Z',
    });
    await refresh;

    expect(store.getSnapshot().isRefreshing).toBe(false);
    expect(store.getSnapshot().conversation?.eventCursor).toBe(9);
  });

  it('marks the selected agent before IndexedDB cache returns', async () => {
    let resolveCached: (value: AgentConversationView | null) => void = () =>
      undefined;
    const cache = createMemoryAgentChatCache();
    const delayedCache = {
      ...cache,
      loadConversation: () =>
        new Promise<AgentConversationView | null>((resolve) => {
          resolveCached = resolve;
        }),
    };
    const store = createAgentChatClientStore({
      cache: delayedCache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [],
          eventCursor: 1,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });

    const selecting = store.selectAgent('default', 'agent-b');

    expect(store.getSnapshot()).toMatchObject({
      workspaceId: 'default',
      agentId: 'agent-b',
      isRefreshing: true,
    });

    resolveCached(null);
    await selecting;
  });

  it('ignores stale refreshes after switching to another agent', async () => {
    const cache = createMemoryAgentChatCache();
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [],
          eventCursor: agentId === 'agent-a' ? 1 : 2,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });

    await Promise.all([
      store.selectAgent('default', 'agent-a'),
      store.selectAgent('default', 'agent-b'),
    ]);

    expect(store.getSnapshot().agentId).toBe('agent-b');
    expect(store.getSnapshot().conversation?.agentId).toBe('agent-b');
  });

  it('refreshes and caches Agent status projections', async () => {
    const cache = createMemoryAgentChatCache();
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [
          {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-a',
            status: 'running',
            activeRunId: 'run-a',
            summary: '正在执行',
            unreadCount: 0,
            eventCursor: 3,
            updatedAt: '2026-06-07T00:00:00.000Z',
          },
        ],
        getConversation: async () => {
          throw new Error('not used');
        },
      },
    });

    await store.refreshStatuses('default');

    expect(store.getSnapshot().statuses).toHaveLength(1);
    await expect(cache.loadStatuses('default')).resolves.toMatchObject([
      { agentId: 'agent-a', status: 'running' },
    ]);
  });

  it('syncs Agent statuses in the background without cache-first rollback', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveStatuses('default', [
      {
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-a',
        mainSessionId: 'session-a',
        status: 'idle',
        summary: '旧状态',
        unreadCount: 0,
        eventCursor: 1,
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
    ]);
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [
          {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-a',
            status: 'running',
            summary: '新状态',
            unreadCount: 0,
            eventCursor: 2,
            updatedAt: '2026-06-07T00:00:01.000Z',
          },
        ],
        getConversation: async () => {
          throw new Error('not used');
        },
      },
    });

    await store.syncStatuses('default');

    expect(store.getSnapshot().statuses).toMatchObject([
      { status: 'running', summary: '新状态' },
    ]);
    await expect(cache.loadStatuses('default')).resolves.toMatchObject([
      { status: 'running' },
    ]);
  });

  it('syncs the currently selected Agent conversation in the background', async () => {
    const cache = createMemoryAgentChatCache();
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [
            {
              messageId: 'agent-1',
              role: 'agent',
              sourceId: agentId,
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'background recovered output',
              status: 'streaming',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 12,
          updatedAt: '2026-06-07T00:00:01.000Z',
        }),
      },
    });

    await store.selectAgent('default', 'agent-a');
    await store.syncSelectedAgent();

    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe(
      'background recovered output',
    );
    await expect(
      cache.loadConversation('default', 'agent-a'),
    ).resolves.toMatchObject({ eventCursor: 12 });
  });

  it('forces a full catch-up when a terminal cursor snapshot still ends with the user message', async () => {
    const cache = createMemoryAgentChatCache();
    const knownCursors: Array<number | undefined> = [];
    let callCount = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId, knownCursor) => {
          knownCursors.push(knownCursor);
          callCount += 1;
          const messages: AgentConversationView['messages'] = [
            {
              messageId: 'user-terminal-race',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'long task',
              status: 'sent',
              processItems: [],
            },
          ];
          if (callCount > 1) {
            messages.push({
              messageId: 'agent-terminal-race',
              runId: 'run-terminal-race',
              role: 'agent',
              sourceId: agentId,
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'terminal answer',
              status: 'succeeded',
              processItems: [],
            });
          }
          return {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId,
            mainSessionId: `session-${agentId}`,
            messages,
            activeRun: null,
            eventCursor: 12,
            updatedAt: '2026-06-07T00:00:01.000Z',
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    await store.syncSelectedAgent();

    expect(knownCursors).toEqual([undefined, undefined]);
    expect(store.getSnapshot().conversation?.messages.at(-1)).toMatchObject({
      role: 'agent',
      content: 'terminal answer',
    });
  });

  it('ignores stale background sync after switching agents', async () => {
    const cache = createMemoryAgentChatCache();
    let resolveAgentA: (value: AgentConversationView) => void = () => undefined;
    const agentAPromise = new Promise<AgentConversationView>((resolve) => {
      resolveAgentA = resolve;
    });
    let agentACalls = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => {
          if (agentId === 'agent-a') {
            agentACalls += 1;
            if (agentACalls > 1) return agentAPromise;
          }
          return {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId,
            mainSessionId: `session-${agentId}`,
            messages: [],
            eventCursor: 2,
            updatedAt: '2026-06-07T00:00:00.000Z',
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    const staleSync = store.syncSelectedAgent();
    await store.selectAgent('default', 'agent-b');
    resolveAgentA({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-agent-a',
      messages: [],
      eventCursor: 99,
      updatedAt: '2026-06-07T00:00:03.000Z',
    });
    await staleSync;

    expect(store.getSnapshot().agentId).toBe('agent-b');
    expect(store.getSnapshot().conversation?.agentId).toBe('agent-b');
  });

  it('records workflow step timings while selecting an agent', async () => {
    const cache = createMemoryAgentChatCache();
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [
            {
              messageId: 'm1',
              role: 'agent',
              sourceId: agentId,
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'fresh',
              status: 'succeeded',
              processItems: [],
            },
          ],
          eventCursor: 3,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });

    await store.selectAgent('default', 'agent-a');

    const workflowSteps = getPerfEvents().filter(
      (event) => event.name === 'chat.workflow.step',
    );
    expect(workflowSteps.map((event) => event.payload?.step)).toEqual(
      expect.arrayContaining([
        'select.commit',
        'cache.loadConversation',
        'api.getConversation',
        'cache.saveConversation',
        'select.finish',
      ]),
    );
    expect(
      new Set(workflowSteps.map((event) => event.payload?.traceId)).size,
    ).toBe(1);
    expect(
      workflowSteps.every(
        (event) => typeof event.payload?.durationMs === 'number',
      ),
    ).toBe(true);
  });
});
