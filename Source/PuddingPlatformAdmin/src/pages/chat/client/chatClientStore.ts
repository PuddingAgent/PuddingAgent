import { recordPerfStep } from '@/utils/debug';
import { DEFAULT_AGENT_CHAT_OWNER_ID } from './clientIdentity';
import type { AgentChatLocalCache } from './localCache';
import type { AgentConversationView, AgentStatusProjection } from './types';

export interface AgentChatApiPort {
  listStatuses(workspaceId: string): Promise<AgentStatusProjection[]>;
  getConversation(
    workspaceId: string,
    agentId: string,
    knownCursor?: number,
  ): Promise<AgentConversationView | null>;
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

export function createAgentChatClientStore(input: {
  cache: AgentChatLocalCache;
  api: AgentChatApiPort;
  ownerUserId?: string;
}) {
  const ownerUserId = input.ownerUserId || 'single-user';
  let selectionVersion = 0;
  let backgroundSyncVersion = 0;
  let syncInFlight = false;
  let snapshot: AgentChatClientSnapshot = {
    ownerUserId,
    statuses: [],
    conversation: null,
    isRefreshing: false,
    error: null,
  };

  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach((listener) => listener());
  const set = (next: Partial<AgentChatClientSnapshot>) => {
    snapshot = { ...snapshot, ...next };
    emit();
  };
  const createTraceId = (prefix: string) =>
    `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

  // P0-perf: 比较两个 conversation 是否语义相同，避免无意义的 IndexedDB 写入和 React setState
  const isConversationSame = (
    a: AgentConversationView | null,
    b: AgentConversationView,
  ) => {
    if (!a) return false;
    return (
      a.eventCursor === b.eventCursor &&
      a.messages.length === b.messages.length &&
      (a.activeRun?.runId ?? null) === (b.activeRun?.runId ?? null) &&
      a.mainSessionId === b.mainSessionId
    );
  };

  const store = {
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSnapshot() {
      return snapshot;
    },
    async refreshStatuses(workspaceId: string) {
      const traceId = createTraceId('agent-status-refresh');
      const ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID;
      const cacheStartedAt = performance.now();
      const cached = await input.cache.loadStatuses(workspaceId, ownerUserId);
      recordPerfStep('agent.status', 'cache.loadStatuses', cacheStartedAt, {
        traceId,
        workspaceId,
        ownerUserId,
        statusCount: cached.length,
      });
      set({ workspaceId, ownerUserId, statuses: cached, error: null });

      try {
        const apiStartedAt = performance.now();
        const fresh = await input.api.listStatuses(workspaceId);
        recordPerfStep('agent.status', 'api.listStatuses', apiStartedAt, {
          traceId,
          workspaceId,
          ownerUserId,
          statusCount: fresh.length,
        });
        const saveStartedAt = performance.now();
        await input.cache.saveStatuses(workspaceId, fresh, ownerUserId);
        recordPerfStep('agent.status', 'cache.saveStatuses', saveStartedAt, {
          traceId,
          workspaceId,
          ownerUserId,
          statusCount: fresh.length,
        });
        if (
          snapshot.workspaceId === workspaceId &&
          snapshot.ownerUserId === ownerUserId
        ) {
          set({ statuses: fresh });
        }
      } catch (error) {
        recordPerfStep('agent.status', 'refresh.error', cacheStartedAt, {
          traceId,
          workspaceId,
          ownerUserId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (
          snapshot.workspaceId === workspaceId &&
          snapshot.ownerUserId === ownerUserId
        ) {
          set({
            error: error instanceof Error ? error.message : String(error),
          });
        }
      }
    },
    async syncStatuses(workspaceId: string) {
      const traceId = createTraceId('agent-status-sync');
      const ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID;
      try {
        const apiStartedAt = performance.now();
        const fresh = await input.api.listStatuses(workspaceId);
        recordPerfStep('agent.status', 'api.syncStatuses', apiStartedAt, {
          traceId,
          workspaceId,
          ownerUserId,
          statusCount: fresh.length,
        });
        const saveStartedAt = performance.now();
        await input.cache.saveStatuses(workspaceId, fresh, ownerUserId);
        recordPerfStep(
          'agent.status',
          'cache.saveSyncedStatuses',
          saveStartedAt,
          {
            traceId,
            workspaceId,
            ownerUserId,
            statusCount: fresh.length,
          },
        );
        if (snapshot.workspaceId === workspaceId || !snapshot.workspaceId) {
          set({ workspaceId, ownerUserId, statuses: fresh, error: null });
        }
      } catch (error) {
        recordPerfStep('agent.status', 'sync.error', performance.now(), {
          traceId,
          workspaceId,
          ownerUserId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (snapshot.workspaceId === workspaceId || !snapshot.workspaceId) {
          set({
            workspaceId,
            ownerUserId,
            error: error instanceof Error ? error.message : String(error),
          });
        }
      }
    },
    async selectAgent(workspaceId: string, agentId: string) {
      const traceId = createTraceId('agent-select');
      const selectStartedAt = performance.now();
      const version = ++selectionVersion;
      backgroundSyncVersion += 1;
      const ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID;
      const existingConversation =
        snapshot.workspaceId === workspaceId && snapshot.agentId === agentId
          ? snapshot.conversation
          : null;
      const commitStartedAt = performance.now();
      set({
        workspaceId,
        ownerUserId,
        agentId,
        conversation: existingConversation,
        isRefreshing: true,
        error: null,
      });
      recordPerfStep('agent.select', 'select.commit', commitStartedAt, {
        traceId,
        workspaceId,
        agentId,
        ownerUserId,
        retainedConversation: Boolean(existingConversation),
      });

      const cacheStartedAt = performance.now();
      const cached = await input.cache.loadConversation(
        workspaceId,
        agentId,
        ownerUserId,
      );
      recordPerfStep('agent.select', 'cache.loadConversation', cacheStartedAt, {
        traceId,
        workspaceId,
        agentId,
        ownerUserId,
        cacheHit: Boolean(cached),
        cachedMessageCount: cached?.messages.length ?? 0,
        cachedCursor: cached?.eventCursor ?? 0,
      });
      if (version !== selectionVersion) {
        recordPerfStep(
          'agent.select',
          'select.staleAfterCache',
          selectStartedAt,
          {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            status: 'stale',
          },
        );
        return;
      }

      set({ conversation: cached, isRefreshing: true });

      try {
        const apiStartedAt = performance.now();
        const fresh = await input.api.getConversation(workspaceId, agentId);
        if (!fresh) {
          throw new Error(
            'Conversation response unexpectedly empty during agent selection.',
          );
        }
        recordPerfStep('agent.select', 'api.getConversation', apiStartedAt, {
          traceId,
          workspaceId,
          agentId,
          ownerUserId,
          sessionId: fresh.mainSessionId,
          messageCount: fresh.messages.length,
          eventCursor: fresh.eventCursor,
          hasActiveRun: Boolean(fresh.activeRun),
        });
        const saveStartedAt = performance.now();
        await input.cache.saveConversation(fresh);
        recordPerfStep(
          'agent.select',
          'cache.saveConversation',
          saveStartedAt,
          {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: fresh.mainSessionId,
            messageCount: fresh.messages.length,
            eventCursor: fresh.eventCursor,
          },
        );
        if (version === selectionVersion) {
          set({ conversation: fresh, isRefreshing: false });
          recordPerfStep('agent.select', 'select.finish', selectStartedAt, {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: fresh.mainSessionId,
            messageCount: fresh.messages.length,
            eventCursor: fresh.eventCursor,
          });
        } else {
          recordPerfStep(
            'agent.select',
            'select.staleAfterApi',
            selectStartedAt,
            {
              traceId,
              workspaceId,
              agentId,
              ownerUserId,
              status: 'stale',
            },
          );
        }
      } catch (error) {
        recordPerfStep('agent.select', 'select.error', selectStartedAt, {
          traceId,
          workspaceId,
          agentId,
          ownerUserId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (version === selectionVersion) {
          set({
            isRefreshing: false,
            error: error instanceof Error ? error.message : String(error),
          });
        }
      }
    },
    async syncSelectedAgent() {
      // P0-perf: 防止重叠请求 — 上一轮未完成不发起新一轮
      if (syncInFlight) return;
      const workspaceId = snapshot.workspaceId;
      const agentId = snapshot.agentId;
      const ownerUserId = snapshot.ownerUserId || DEFAULT_AGENT_CHAT_OWNER_ID;
      if (!workspaceId || !agentId) return;

      // P0-perf: 用 status 的 eventCursor 短路 — 如果 cursor 相同且无 activeRun，跳过 API 调用
      const matchingStatus = snapshot.statuses.find(
        (s) => s.agentId === agentId,
      );
      const existingConv = snapshot.conversation;
      if (
        matchingStatus &&
        existingConv &&
        matchingStatus.eventCursor === existingConv.eventCursor &&
        !matchingStatus.activeRunId &&
        !existingConv.activeRun
      ) {
        recordPerfStep(
          'agent.selectedSync',
          'sync.skipped.cursorMatch',
          performance.now(),
          {
            traceId: createTraceId('agent-selected-sync'),
            workspaceId,
            agentId,
            ownerUserId,
            eventCursor: existingConv.eventCursor,
          },
        );
        return;
      }

      syncInFlight = true;
      const version = ++backgroundSyncVersion;
      const traceId = createTraceId('agent-selected-sync');
      const syncStartedAt = performance.now();
      try {
        const apiStartedAt = performance.now();
        const knownCursor = snapshot.conversation?.eventCursor;
        const fresh = await input.api.getConversation(
          workspaceId,
          agentId,
          knownCursor,
        );
        if (!fresh) {
          recordPerfStep(
            'agent.selectedSync',
            'sync.skipped.notModified',
            syncStartedAt,
            {
              traceId,
              workspaceId,
              agentId,
              ownerUserId,
              eventCursor: knownCursor ?? 0,
            },
          );
          return;
        }
        recordPerfStep(
          'agent.selectedSync',
          'api.getConversation',
          apiStartedAt,
          {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: fresh.mainSessionId,
            messageCount: fresh.messages.length,
            eventCursor: fresh.eventCursor,
            hasActiveRun: Boolean(fresh.activeRun),
          },
        );

        // P0-perf: 如果 conversation 语义相同，跳过 IndexedDB 写入和React setState
        if (isConversationSame(snapshot.conversation, fresh)) {
          if (version === backgroundSyncVersion) {
            recordPerfStep(
              'agent.selectedSync',
              'sync.skipped.same',
              syncStartedAt,
              {
                traceId,
                workspaceId,
                agentId,
                ownerUserId,
                eventCursor: fresh.eventCursor,
              },
            );
          }
          return;
        }

        const saveStartedAt = performance.now();
        await input.cache.saveConversation(fresh);
        recordPerfStep(
          'agent.selectedSync',
          'cache.saveConversation',
          saveStartedAt,
          {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: fresh.mainSessionId,
            eventCursor: fresh.eventCursor,
          },
        );
        if (
          version === backgroundSyncVersion &&
          snapshot.workspaceId === workspaceId &&
          snapshot.agentId === agentId &&
          snapshot.ownerUserId === ownerUserId
        ) {
          set({ conversation: fresh, error: null });
          recordPerfStep('agent.selectedSync', 'sync.finish', syncStartedAt, {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: fresh.mainSessionId,
            eventCursor: fresh.eventCursor,
          });
        } else {
          recordPerfStep('agent.selectedSync', 'sync.stale', syncStartedAt, {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            status: 'stale',
          });
        }
      } catch (error) {
        recordPerfStep('agent.selectedSync', 'sync.error', syncStartedAt, {
          traceId,
          workspaceId,
          agentId,
          ownerUserId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (
          version === backgroundSyncVersion &&
          snapshot.workspaceId === workspaceId &&
          snapshot.agentId === agentId &&
          snapshot.ownerUserId === ownerUserId
        ) {
          set({
            error: error instanceof Error ? error.message : String(error),
          });
        }
      } finally {
        syncInFlight = false;
      }
    },
  };

  return store;
}
