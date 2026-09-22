import { recordPerfStep } from '@/utils/perfEventRuntime';
import {
  detectUnconsumedProjectionEvidence,
  isConversationEquivalent,
  mergeCanonicalConversation,
} from './canonicalMerge';
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

/**
 * 是否还有**未消费**的「待物化 / 待追平」证据（设计文档 §4、§12 落地顺序第 2 项）。
 *
 * 旧启发式只看末条 role：当终态事件已到达、assistant 行已出现但正文尚未物化时，
 * 它会把快照当成「已追平」，轮询直接短路，界面只能等用户「再发一条」才恢复
 * （= 用户报告的「晚一条」）。证据判定现收敛在 canonicalMerge，本函数只是页面
 * 轮询档位（1200ms/5000ms）的提示，不再是「已追平」的唯一依据。
 *
 * 取舍与残余风险：末条是 user 时的「等待回复」在本地依然无法证伪服务端永不再回复
 * （与旧实现一致，只是不再是唯一判据）；证据来源列表见
 * canonicalMerge.detectUnconsumedProjectionEvidence，入口短路门的取舍见
 * syncSelectedAgent 内的注释。
 */
export const conversationNeedsProjectionCatchUp = (
  conversation: AgentConversationView | null | undefined,
): boolean => detectUnconsumedProjectionEvidence(conversation).length > 0;

const statusProjectionEquals = (
  previous: AgentStatusProjection,
  next: AgentStatusProjection,
): boolean =>
  previous.workspaceId === next.workspaceId &&
  previous.ownerUserId === next.ownerUserId &&
  previous.agentId === next.agentId &&
  previous.mainSessionId === next.mainSessionId &&
  previous.status === next.status &&
  previous.activeRunId === next.activeRunId &&
  previous.summary === next.summary &&
  previous.unreadCount === next.unreadCount &&
  previous.eventCursor === next.eventCursor &&
  previous.updatedAt === next.updatedAt;

const statusProjectionsEqual = (
  previous: AgentStatusProjection[],
  next: AgentStatusProjection[],
): boolean =>
  previous.length === next.length &&
  previous.every((status, index) => statusProjectionEquals(status, next[index]));

export function createAgentChatClientStore(input: {
  cache: AgentChatLocalCache;
  api: AgentChatApiPort;
  ownerUserId?: string;
}) {
  const ownerUserId = input.ownerUserId || 'single-user';
  let selectionVersion = 0;
  let backgroundSyncVersion = 0;
  let syncInFlight = false;
  let statusesSyncInFlight = false;
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

  // P0-perf: 只有**可判定的等价**（scope + message identity + 内容/状态/过程指纹 + cursor
  // 全相等）才跳过 IndexedDB 写入与 React setState。禁止再用 messages.length 当等价判据
  // （设计文档 §6 根因 A）：同一 messageId 的正文增长条数不变，旧判据会把新正文丢掉。
  const isConversationSame = (
    a: AgentConversationView | null,
    b: AgentConversationView,
  ) => isConversationEquivalent(a, b);

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
      if (statusesSyncInFlight) return;
      statusesSyncInFlight = true;
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
        if (
          snapshot.workspaceId === workspaceId &&
          snapshot.ownerUserId === ownerUserId &&
          statusProjectionsEqual(snapshot.statuses, fresh)
        ) {
          recordPerfStep(
            'agent.status',
            'sync.skipped.same',
            apiStartedAt,
            {
              traceId,
              workspaceId,
              ownerUserId,
              statusCount: fresh.length,
            },
          );
          return;
        }
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
      } finally {
        statusesSyncInFlight = false;
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
      // 只要还有未消费的「待物化 / 待追平」证据就不得短路。新的跳过条件是旧条件
      // （cursor 相同 + 无 activeRun + 末条不是 user）的真子集 ⇒ 相对旧行为只会更常
      // 发请求，不会引入新的停滞。残余风险：若服务端在同一 messageId 上追加正文却既不
      // 推进 eventCursor 也不改状态，本地字段无法证伪「已追平」；缓解 = 合并器在拿到
      // 200 响应时按完整性单调采纳更长正文，且状态游标推进 / activeRun 出现时必然全量
      // 回拉，终态一次回拉由后续切片的 completion hydration 补齐。
      const projectionEvidence = detectUnconsumedProjectionEvidence(existingConv);
      const projectionCatchUpPending = projectionEvidence.length > 0;
      if (
        matchingStatus &&
        existingConv &&
        projectionEvidence.length === 0 &&
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
        // Do not use conditional GET while the read model is visibly behind
        // the terminal event. A cursor-only 304 would otherwise make the
        // incomplete user-only snapshot permanent until a page refresh.
        const knownCursor =
          projectionCatchUpPending || existingConv?.activeRun
            ? undefined
            : existingConv?.eventCursor;
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

        // 唯一合并入口：候选快照必须经过 canonical merge 的单调证据比较，不得在 store 里
        // 再用「看起来一样」直接丢弃权威响应（设计 §5：SSE/轮询/物化共用一个 merge port）。
        const mergeResult = mergeCanonicalConversation(
          snapshot.conversation,
          fresh,
        );
        for (const diagnostic of mergeResult.diagnostics) {
          recordPerfStep(
            'agent.selectedSync',
            `merge.${diagnostic.code}`,
            syncStartedAt,
            {
              traceId,
              workspaceId,
              agentId,
              ownerUserId,
              messageId: diagnostic.messageId,
              currentCursor: diagnostic.currentCursor,
              candidateCursor: diagnostic.candidateCursor,
              detail: diagnostic.detail,
            },
          );
        }
        const mergedConversation = mergeResult.conversation;

        // P0-perf: 只有可判定的等价（scope + message identity + 内容/状态/过程指纹 + cursor）
        // 才跳过 IndexedDB 写入和 React setState。
        if (isConversationSame(snapshot.conversation, mergedConversation)) {
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
                eventCursor: mergedConversation.eventCursor,
                mergeChanged: mergeResult.changed,
              },
            );
          }
          return;
        }

        const saveStartedAt = performance.now();
        await input.cache.saveConversation(mergedConversation);
        recordPerfStep(
          'agent.selectedSync',
          'cache.saveConversation',
          saveStartedAt,
          {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: mergedConversation.mainSessionId,
            messageCount: mergedConversation.messages.length,
            eventCursor: mergedConversation.eventCursor,
          },
        );
        if (
          version === backgroundSyncVersion &&
          snapshot.workspaceId === workspaceId &&
          snapshot.agentId === agentId &&
          snapshot.ownerUserId === ownerUserId
        ) {
          set({ conversation: mergedConversation, error: null });
          recordPerfStep('agent.selectedSync', 'sync.finish', syncStartedAt, {
            traceId,
            workspaceId,
            agentId,
            ownerUserId,
            sessionId: mergedConversation.mainSessionId,
            eventCursor: mergedConversation.eventCursor,
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
