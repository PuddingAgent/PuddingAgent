import {
  clearPerfEvents,
  getPerfEvents,
  setPerfDiagnosticsEnabled,
} from '@/utils/debug';
import type { AdminChatStreamEvent } from '@/services/platform/api';
import { mergeActiveRunIntoTurns, mergeProjectedMessageIntoTurns } from '../components/MessageList';
import { buildMessageBlocks, type ChatTurn } from '../types';
import { createAgentChatClientStore } from './chatClientStore';
import { createMemoryAgentChatCache } from './localCache';
import type { AgentConversationView } from './types';

describe('agent chat client store', () => {
  it('shows a failed turn with no model text and preserves its diagnostic through message projection', async () => {
    const store = createAgentChatClientStore({
      cache: createMemoryAgentChatCache(),
      api: {
        listStatuses: async () => [],
        getConversation: async () => ({
          workspaceId: 'default', ownerUserId: 'single-user', agentId: 'agent-a',
          mainSessionId: 'session-a', eventCursor: 10, updatedAt: '2026-10-20T00:00:00Z',
          messages: [],
        }),
      },
    });
    await store.selectAgent('default', 'agent-a');
    const diagnostic = { causeTitle: '模型服务连接中断', reportText: 'redacted upload report', retryable: true };
    store.applyLiveEvent('default', 'agent-a', {
      type: 'turn.failed', eventId: 'error-11', sequence: 11, occurredAt: '2026-10-20T00:00:01Z',
      turnId: 'turn-1', runId: 'run-1', messageId: 'assistant-1',
      errorMessage: 'upload failed', diagnostic,
    } as unknown as AdminChatStreamEvent);
    const failed = store.getSnapshot().conversation!.messages[0];
    expect(failed.turnOutcome?.diagnostic).toEqual(diagnostic);
    const turns: ChatTurn[] = [];
    mergeProjectedMessageIntoTurns(turns, failed, 'Pudding');
    expect(buildMessageBlocks(turns).find(block => block.role === 'agent')).toMatchObject({
      status: 'error', executionError: 'upload failed', errorDiagnostic: diagnostic,
    });
  });

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
    expect(store.getSnapshot().isOpening).toBe(false);
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

  it('uses the configured owner identity for cache reads and writes', async () => {
    const cache = createMemoryAgentChatCache();
    const loadConversation = jest.spyOn(cache, 'loadConversation');
    const saveConversation = jest.spyOn(cache, 'saveConversation');
    const store = createAgentChatClientStore({
      cache,
      ownerUserId: 'admin',
      api: {
        listStatuses: async () => [],
        getConversation: jest.fn(),
        getConversationFromSnapshot: async (_workspaceId, agentId, owner) => ({
          workspaceId: 'default',
          ownerUserId: owner,
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [],
          eventCursor: 1,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });

    await store.selectAgent('default', 'agent-a');

    expect(loadConversation).toHaveBeenCalledWith('default', 'agent-a', 'admin');
    expect(saveConversation).toHaveBeenCalledWith(
      expect.objectContaining({ ownerUserId: 'admin' }),
    );
  });

  it('clears opening state before the cache write finishes', async () => {
    let releaseSave: () => void = () => undefined;
    let markSaveStarted: () => void = () => undefined;
    const saveStarted = new Promise<void>((resolve) => {
      markSaveStarted = resolve;
    });
    const cache = createMemoryAgentChatCache();
    const delayedCache = {
      ...cache,
      saveConversation: async (view: Parameters<typeof cache.saveConversation>[0]) => {
        markSaveStarted();
        await new Promise<void>((resolve) => {
          releaseSave = resolve;
        });
        return cache.saveConversation(view);
      },
    };
    const store = createAgentChatClientStore({
      cache: delayedCache,
      ownerUserId: 'admin',
      api: {
        listStatuses: async () => [],
        getConversation: jest.fn(),
        getConversationFromSnapshot: async (_workspaceId, agentId, owner) => ({
          workspaceId: 'default',
          ownerUserId: owner,
          agentId,
          mainSessionId: `session-${agentId}`,
          messages: [],
          eventCursor: 1,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });

    const selecting = store.selectAgent('default', 'agent-a');
    await saveStarted;

    expect(store.getSnapshot().isOpening).toBe(false);
    expect(store.getSnapshot().conversation?.mainSessionId).toBe(
      'session-agent-a',
    );

    releaseSave();
    await selecting;
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
      isOpening: true,
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

  it('does not rewrite cache or notify subscribers when status polling is unchanged', async () => {
    const status = {
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      status: 'idle' as const,
      summary: 'ready',
      unreadCount: 0,
      eventCursor: 4,
      updatedAt: '2026-06-07T00:00:00.000Z',
    };
    const memoryCache = createMemoryAgentChatCache();
    const saveStatuses = jest.spyOn(memoryCache, 'saveStatuses');
    const store = createAgentChatClientStore({
      cache: memoryCache,
      api: {
        listStatuses: async () => [{ ...status }],
        getConversation: async () => {
          throw new Error('not used');
        },
      },
    });

    await store.refreshStatuses('default');
    const listener = jest.fn();
    const unsubscribe = store.subscribe(listener);

    await store.syncStatuses('default');

    expect(saveStatuses).toHaveBeenCalledTimes(1);
    expect(listener).not.toHaveBeenCalled();
    unsubscribe();
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

  it('adopts a longer body for the same messageId when the message count is unchanged', async () => {
    const cache = createMemoryAgentChatCache();
    const shortBody = 'partial answer';
    const longBody = 'partial answer with the materialized tail';
    let callCount = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => {
          callCount += 1;
          return {
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
                content: callCount === 1 ? shortBody : longBody,
                status: 'succeeded',
                processItems: [],
              },
            ],
            activeRun: null,
            eventCursor: 12,
            updatedAt: '2026-06-07T00:00:01.000Z',
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe(
      shortBody,
    );

    await store.syncSelectedAgent();

    // 旧实现的 isConversationSame 只比 eventCursor / messages.length /
    // activeRun / mainSessionId：全等 ⇒ 判定「没变」跳过提交，同一 messageId 的
    // 正文增长被丢掉（= 用户报告的「晚一条」最小复现）。
    expect(callCount).toBe(2);
    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe(
      longBody,
    );
    await expect(
      cache.loadConversation('default', 'agent-a'),
    ).resolves.toMatchObject({
      messages: [{ messageId: 'agent-1', content: longBody }],
    });
  });

  it('does not short-circuit the poll while the terminal agent row has no materialized body', async () => {
    const cache = createMemoryAgentChatCache();
    const knownCursors: Array<number | undefined> = [];
    let callCount = 0;
    const buildConversation = (agentId: string, materialized: boolean) => ({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId,
      mainSessionId: `session-${agentId}`,
      messages: [
        {
          messageId: 'user-1',
          role: 'user' as const,
          sourceId: 'admin',
          sourceName: 'Pudding Admin',
          createdAt: '2026-06-07T00:00:00.000Z',
          content: 'long task',
          status: 'sent' as const,
          processItems: [],
        },
        {
          messageId: 'agent-1',
          role: 'agent' as const,
          sourceId: agentId,
          sourceName: 'Agent A',
          createdAt: '2026-06-07T00:00:01.000Z',
          content: materialized ? 'terminal answer' : '',
          status: 'succeeded' as const,
          processItems: [],
        },
      ],
      activeRun: null,
      eventCursor: 12,
      updatedAt: '2026-06-07T00:00:01.000Z',
    });
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [
          {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-agent-a',
            status: 'idle' as const,
            summary: 'idle',
            unreadCount: 0,
            eventCursor: 12,
            updatedAt: '2026-06-07T00:00:02.000Z',
          },
        ],
        getConversation: async (_workspaceId, agentId, knownCursor) => {
          knownCursors.push(knownCursor);
          callCount += 1;
          return buildConversation(agentId, callCount > 1);
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    await store.syncStatuses('default');
    expect(callCount).toBe(1);

    await store.syncSelectedAgent();

    // 旧实现：status 与 conversation 的 eventCursor 相同、无 activeRun，
    // 且末条不是 user ⇒ 入口短路、根本不发请求；只有「再发一条」走发送路径
    // 的定时器才把正文捞出来。
    expect(callCount).toBe(2);
    expect(knownCursors[1]).toBeUndefined();
    expect(
      store.getSnapshot().conversation?.messages.at(-1)?.content,
    ).toBe('terminal answer');
  });

  it('deduplicates concurrent open requests for the same agent scope', async () => {
    const cache = createMemoryAgentChatCache();
    let callCount = 0;
    let resolveFresh: (value: AgentConversationView) => void = () => undefined;
    const freshPromise = new Promise<AgentConversationView>((resolve) => {
      resolveFresh = resolve;
    });

    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async () => {
          callCount += 1;
          return freshPromise;
        },
      },
    });

    const first = store.selectAgent('default', 'agent-a');
    const second = store.selectAgent('default', 'agent-a');
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(callCount).toBe(1);

    resolveFresh({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-agent-a',
      messages: [],
      eventCursor: 3,
      updatedAt: '2026-06-07T00:00:03.000Z',
    });
    await Promise.all([first, second]);

    expect(callCount).toBe(1);
    expect(store.getSnapshot().isOpening).toBe(false);
    expect(store.getSnapshot().isRefreshing).toBe(false);
  });

  it('aborts the previous open request when switching agents', async () => {
    const cache = createMemoryAgentChatCache();
    const signals: AbortSignal[] = [];
    let resolveA: (value: AgentConversationView) => void = () => undefined;
    let resolveB: (value: AgentConversationView) => void = () => undefined;

    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId, _cursor, signal) => {
          if (signal) signals.push(signal);
          if (agentId === 'agent-a') {
            return new Promise<AgentConversationView>((resolve) => {
              resolveA = resolve;
            });
          }
          return new Promise<AgentConversationView>((resolve) => {
            resolveB = resolve;
          });
        },
      },
    });

    const openA = store.selectAgent('default', 'agent-a');
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(signals[0]?.aborted).toBe(false);

    const openB = store.selectAgent('default', 'agent-b');
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(signals[0]?.aborted).toBe(true);

    const build = (
      agentId: string,
      sessionId: string,
      cursor: number,
    ): AgentConversationView => ({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId,
      mainSessionId: sessionId,
      messages: [],
      eventCursor: cursor,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });
    resolveA(build('agent-a', 'session-a', 1));
    resolveB(build('agent-b', 'session-b', 2));
    await Promise.all([openA, openB]);

    expect(store.getSnapshot().agentId).toBe('agent-b');
    expect(store.getSnapshot().conversation?.mainSessionId).toBe('session-b');
  });

  it('keeps live content received while an older newest-first page is loading', async () => {
    let resolvePage!: (page: any) => void;
    const store = createAgentChatClientStore({
      cache: createMemoryAgentChatCache(),
      api: {
        listStatuses: async () => [], getConversation: jest.fn(),
        getConversationFromSnapshot: async () => ({
          workspaceId: 'default', ownerUserId: 'single-user', agentId: 'agent-a',
          mainSessionId: 'session-a', messages: [], eventCursor: 10,
          updatedAt: new Date(3000).toISOString(), hasMoreHistory: true,
          historyNextBeforeCreatedAt: 3000, historyNextBeforeMessageId: 'boundary',
        }),
        getHistoryPage: () => new Promise((resolve) => { resolvePage = resolve; }),
      },
    });
    await store.selectAgent('default', 'agent-a');
    const pending = store.loadOlder();
    store.applyLiveEvent('default', 'agent-a', {
      type: 'message.content.appended', eventId: 'live', sequence: 11,
      turnId: 'current-turn', runId: 'current-run', delta: '刚收到的回复',
    } as unknown as AdminChatStreamEvent);
    resolvePage({
      items: [2, 1].map((number) => ({ messageId: `old-${number}`, role: 'agent',
        createdAt: number * 1000, preview: '历史', hasMoreContent: false,
        contentLength: 6, hasContentParts: false })), hasMore: false,
      nextBeforeCreatedAt: null, nextBeforeMessageId: null,
    });
    await pending;
    expect(store.getSnapshot().conversation?.messages.map((message) => message.messageId)).toEqual(['old-1', 'old-2']);
    expect(store.getSnapshot().conversation?.eventCursor).toBe(11);
    expect(store.getSnapshot().conversation?.activeRun?.outputSnapshot.markdown).toBe('刚收到的回复');
  });

  it('expands a truncated message on demand through bounded content segments', async () => {
    const cache = createMemoryAgentChatCache();
    let contentCalls = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: jest.fn(),
        getConversationFromSnapshot: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: 'session-agent-a',
          messages: [
            {
              messageId: 'msg-long',
              role: 'agent',
              sourceId: 'agent',
              sourceName: 'Agent',
              createdAt: new Date(1_000).toISOString(),
              content:
                'preview\n\n[展开全文](#pudding-expand-msg-long)',
              status: 'succeeded',
              processItems: [],
              hasMoreContent: true,
              contentLength: 100,
            },
          ],
          eventCursor: 10,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
        getMessageContent: async () => {
          contentCalls += 1;
          if (contentCalls === 1) {
            return {
              conversationId: 'session-agent-a',
              messageId: 'msg-long',
              offsetBytes: 0,
              nextOffsetBytes: 50,
              totalBytes: 100,
              content: 'a'.repeat(50),
              hasMore: true,
            };
          }
          return {
            conversationId: 'session-agent-a',
            messageId: 'msg-long',
            offsetBytes: 50,
            nextOffsetBytes: null,
            totalBytes: 100,
            content: 'b'.repeat(50),
            hasMore: false,
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    await store.expandMessage('msg-long');

    expect(contentCalls).toBe(2);
    const message = store.getSnapshot().conversation?.messages[0];
    expect(message?.content).toBe('a'.repeat(50) + 'b'.repeat(50));
    expect(message?.hasMoreContent).toBe(false);

    await store.expandMessage('msg-long');
    expect(contentCalls).toBe(2);
  });

  it('loads older history page and prepends without duplicates', async () => {
    const cache = createMemoryAgentChatCache();
    let historyCalls = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: jest.fn(),
        getConversationFromSnapshot: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: 'session-agent-a',
          messages: [
            {
              messageId: 'msg-2',
              role: 'agent',
              sourceId: 'agent',
              sourceName: 'Agent',
              createdAt: new Date(1_100).toISOString(),
              content: 'newer',
              status: 'succeeded',
              processItems: [],
            },
          ],
          eventCursor: 12,
          updatedAt: '2026-06-07T00:00:00.000Z',
          hasMoreHistory: true,
          historyNextBeforeCreatedAt: 1_100,
          historyNextBeforeMessageId: 'msg-2',
        }),
        getHistoryPage: async (
          _conversationId,
          beforeCreatedAt,
          beforeMessageId,
          _limit,
        ) => {
          historyCalls += 1;
          expect(beforeCreatedAt).toBe(1_100);
          expect(beforeMessageId).toBe('msg-2');
          return {
            items: [
              {
                messageId: 'msg-1',
                role: 'user',
                turnId: null,
                createdAt: 1_000,
                preview: 'older',
                hasMoreContent: false,
                contentLength: 5,
                hasContentParts: false,
              },
              {
                messageId: 'msg-2',
                role: 'agent',
                turnId: null,
                createdAt: 1_100,
                preview: 'newer',
                hasMoreContent: false,
                contentLength: 5,
                hasContentParts: false,
              },
            ],
            nextBeforeCreatedAt: null,
            nextBeforeMessageId: null,
            hasMore: false,
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    expect(store.getSnapshot().hasMoreHistory).toBe(true);

    await store.loadOlder();

    const conversation = store.getSnapshot().conversation;
    expect(conversation?.messages.map((message) => message.messageId)).toEqual([
      'msg-1',
      'msg-2',
    ]);
    expect(store.getSnapshot().hasMoreHistory).toBe(false);
    expect(store.getSnapshot().isLoadingOlder).toBe(false);

    await store.loadOlder();
    expect(historyCalls).toBe(1);
  });

  it('does not repeat an immediate same-agent open after the first one completed', async () => {
    const cache = createMemoryAgentChatCache();
    let callCount = 0;
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: jest.fn(),
        getConversationFromSnapshot: async (_workspaceId, agentId) => {
          callCount += 1;
          return {
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId,
            mainSessionId: 'session-agent-a',
            messages: [],
            eventCursor: 10,
            updatedAt: '2026-06-07T00:00:00.000Z',
          };
        },
      },
    });

    await store.selectAgent('default', 'agent-a');
    await store.selectAgent('default', 'agent-a');

    expect(callCount).toBe(1);
  });

  it('prefers the unified snapshot open path when the port provides it', async () => {
    const cache = createMemoryAgentChatCache();
    const legacy = jest.fn();
    const snapshotOpen = jest.fn(async (_workspaceId, agentId) => ({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId,
      mainSessionId: 'session-snapshot',
      messages: [],
      eventCursor: 12,
      updatedAt: '2026-06-07T00:00:00.000Z',
    }));

    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: legacy,
        getConversationFromSnapshot: snapshotOpen,
      },
    });

    await store.selectAgent('default', 'agent-a');

    expect(snapshotOpen).toHaveBeenCalledTimes(1);
    expect(legacy).not.toHaveBeenCalled();
    expect(store.getSnapshot().conversation?.mainSessionId).toBe(
      'session-snapshot',
    );
  });

  it('stitches live main-session events into the active run without duplicate body', async () => {
    const cache = createMemoryAgentChatCache();
    const store = createAgentChatClientStore({
      cache,
      api: {
        listStatuses: async () => [],
        getConversation: async (_workspaceId, agentId) => ({
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId,
          mainSessionId: 'session-agent-a',
          messages: [],
          eventCursor: 10,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }),
      },
    });
    await store.selectAgent('default', 'agent-a');

    const live = (overrides: Record<string, unknown>) =>
      ({
        eventId: `e-${overrides.sequence}`,
        ...overrides,
      }) as unknown as AdminChatStreamEvent;

    expect(
      store.applyLiveEvent(
        'default',
        'agent-a',
        live({ type: 'turn.started', sequence: 11, turnId: 't1', runId: 'r1' }),
      ),
    ).toBe(true);
    store.applyLiveEvent(
      'default',
      'agent-a',
      live({ type: 'message.content.appended', sequence: 12, delta: '你' }),
    );
    store.applyLiveEvent(
      'default',
      'agent-a',
      live({
        type: 'message.thinking_summary.appended',
        sequence: 13,
        delta: '思考',
      }),
    );
    store.applyLiveEvent(
      'default',
      'agent-a',
      live({ type: 'message.content.appended', sequence: 14, delta: '好' }),
    );
    store.applyLiveEvent(
      'default',
      'agent-a',
      live({
        type: 'tool.call.completed',
        sequence: 15,
        toolCallId: 'call-1',
        name: 'read',
        output: 'ok',
        exitCode: 0,
      }),
    );
    store.applyLiveEvent(
      'default',
      'agent-a',
      live({ type: 'turn.completed', sequence: 16 }),
    );

    const conversation = store.getSnapshot().conversation;
    expect(conversation?.activeRun?.outputSnapshot.markdown).toBe('你好');
    expect(conversation?.activeRun?.status).toBe('succeeded');
    expect(conversation?.activeRun?.completedAt).toBeTruthy();
    expect(conversation?.activeRun?.outputSnapshot.processItems).toHaveLength(2);
    expect(conversation?.eventCursor).toBe(16);
    expect(store.getSnapshot().lastLiveEventAt).toBeGreaterThan(0);

    // 同一 eventId 重放不再叠加正文。
    expect(
      store.applyLiveEvent(
        'default',
        'agent-a',
        live({ type: 'message.content.appended', sequence: 12, delta: '你' }),
      ),
    ).toBe(false);
    expect(store.getSnapshot().conversation?.activeRun?.outputSnapshot.markdown).toBe(
      '你好',
    );

    // 旧身份的事件不得污染当前 Store。
    expect(
      store.applyLiveEvent(
        'default',
        'agent-b',
        live({ type: 'message.content.appended', sequence: 17, delta: 'X' }),
      ),
    ).toBe(false);
  });

  it('keeps the completed reply between user messages when the next Turn starts before a snapshot refresh', async () => {
    const store = createAgentChatClientStore({
      cache: createMemoryAgentChatCache(),
      api: {
        listStatuses: async () => [],
        getConversation: async () => ({
          workspaceId: 'default', ownerUserId: 'single-user', agentId: 'agent-a',
          mainSessionId: 'session-a', eventCursor: 10, updatedAt: '2026-10-20T00:00:00Z',
          messages: ['t1', 't2'].map((turnId, index) => ({
            messageId: `user-${turnId}`, turnId, role: 'user' as const,
            sourceId: 'user', sourceName: '我', content: `问题${index + 1}`,
            createdAt: `2026-10-20T00:00:0${index * 3}Z`,
            status: 'succeeded' as const, processItems: [],
          })),
        }),
      },
    });
    await store.selectAgent('default', 'agent-a');
    const apply = (event: Record<string, unknown>) => store.applyLiveEvent(
      'default', 'agent-a', {
        eventId: `event-${event.sequence}`, occurredAt: '2026-10-20T00:00:02Z', ...event,
      } as unknown as AdminChatStreamEvent,
    );
    apply({ type: 'turn.started', turnId: 't1', runId: 'r1', messageId: 'a1', sequence: 11 });
    apply({ type: 'message.content.appended', turnId: 't1', runId: 'r1', messageId: 'a1', sequence: 12, delta: '部分' });
    apply({ type: 'turn.completed', turnId: 't1', runId: 'r1', messageId: 'a1', sequence: 13, reply: '第一轮完整回复' });
    apply({ type: 'turn.started', turnId: 't2', runId: 'r2', messageId: 'a2', sequence: 14 });
    apply({ type: 'message.content.appended', turnId: 't2', runId: 'r2', messageId: 'a2', sequence: 15, delta: '第二轮' });

    const conversation = store.getSnapshot().conversation!;
    expect(conversation.messages.map((item) => item.content)).toEqual([
      '问题1', '第一轮完整回复', '问题2',
    ]);
    expect(conversation.messages[1]).toMatchObject({
      messageId: 'a1', turnId: 't1', runId: 'r1', status: 'succeeded',
      createdAt: '2026-10-20T00:00:02Z',
    });
    expect(conversation.activeRun).toMatchObject({
      runId: 'r2',
      outputSnapshot: { markdown: '第二轮', window: { turnId: 't2' } },
    });
    const turns: ChatTurn[] = [];
    for (const message of conversation.messages) {
      mergeProjectedMessageIntoTurns(turns, message, 'Pudding');
    }
    expect(buildMessageBlocks(
      mergeActiveRunIntoTurns(turns, conversation.activeRun, 'Pudding'),
    ).map((block) => block.content)).toEqual([
      '问题1', '第一轮完整回复', '问题2', '第二轮',
    ]);
    // A delayed terminal event for t1 must not complete t2 or duplicate its reply.
    apply({ type: 'turn.completed', turnId: 't1', runId: 'r1', messageId: 'a1', sequence: 16, reply: '第一轮完整回复' });
    expect(store.getSnapshot().conversation?.activeRun?.status).toBe('running');
    expect(store.getSnapshot().conversation?.messages.filter((item) => item.messageId === 'a1')).toHaveLength(1);
    expect(apply({
      type: 'turn.started', turnId: 't1', runId: 'r1', messageId: 'a1', sequence: 11,
      eventId: 'replayed-start',
    })).toBe(false);
    expect(store.getSnapshot().conversation?.activeRun?.runId).toBe('r2');
  });

  it('does not roll back a completed reply received while saving a background snapshot', async () => {
    const cache = createMemoryAgentChatCache();
    let releaseSave!: () => void;
    let notifySaving!: () => void;
    const saving = new Promise<void>((resolve) => { notifySaving = resolve; });
    const saved = new Promise<void>((resolve) => { releaseSave = resolve; });
    const view: AgentConversationView = {
      workspaceId: 'default', ownerUserId: 'single-user', agentId: 'agent-a',
      mainSessionId: 'session-a', messages: [], eventCursor: 10,
      updatedAt: '2026-10-20T00:00:00Z',
    };
    const store = createAgentChatClientStore({
      cache,
      api: { listStatuses: async () => [], getConversation: async () => ({ ...view }) },
    });
    await store.selectAgent('default', 'agent-a');
    view.eventCursor = 11;
    jest.spyOn(cache, 'saveConversation').mockImplementation(async () => {
      notifySaving();
      await saved;
    });
    const refresh = store.syncSelectedAgent();
    await saving;
    store.applyLiveEvent('default', 'agent-a', {
      type: 'turn.completed', sequence: 12, eventId: 'done-12',
      turnId: 't1', runId: 'r1', messageId: 'a1', reply: '完整回复',
      occurredAt: '2026-10-20T00:00:02Z',
    } as unknown as AdminChatStreamEvent);
    releaseSave();
    await refresh;
    expect(store.getSnapshot().conversation?.eventCursor).toBe(12);
    expect(store.getSnapshot().conversation?.messages[0]?.content).toBe('完整回复');
  });
});
