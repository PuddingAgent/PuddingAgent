import { recordPerfStep } from '@/utils/perfEventRuntime';
import type { AdminChatStreamEvent } from '@/services/platform/api';
import {
  detectUnconsumedProjectionEvidence,
  isConversationEquivalent,
  mergeCanonicalConversation,
} from './canonicalMerge';
import { DEFAULT_AGENT_CHAT_OWNER_ID } from './clientIdentity';
import type { AgentChatLocalCache } from './localCache';
import type { ConversationHistoryPage } from './conversationOpenTypes';
import { mapSnapshotHistoryItem } from './conversationSnapshotAdapter';
import type { AgentConversationView, AgentStatusProjection } from './types';

export interface AgentChatApiPort {
  listStatuses(workspaceId: string): Promise<AgentStatusProjection[]>;
  getConversation(
    workspaceId: string,
    agentId: string,
    knownCursor?: number,
    signal?: AbortSignal,
  ): Promise<AgentConversationView | null>;
  /**
   * 阶段 D2c：统一打开快照路径。提供时优先于 getConversation；
   * 旧端点/活动 Turn 的过渡回退由实现方负责。
   */
  getConversationFromSnapshot?(
    workspaceId: string,
    agentId: string,
    ownerUserId: string,
    signal?: AbortSignal,
  ): Promise<AgentConversationView | null>;
  /** 阶段 D2c2：统一历史分页（键集 createdAt+messageId）。 */
  getHistoryPage?(
    conversationId: string,
    beforeCreatedAt: number | null,
    beforeMessageId: string | null,
    limit: number,
    signal?: AbortSignal,
  ): Promise<ConversationHistoryPage>;
}

export interface AgentChatClientSnapshot {
  workspaceId?: string;
  ownerUserId: string;
  agentId?: string;
  statuses: AgentStatusProjection[];
  conversation: AgentConversationView | null;
  /** 首次打开且尚无任何可显示内容时使用的骨架条件；后台刷新不得置 true。 */
  isOpening: boolean;
  isRefreshing: boolean;
  /** 最近一次成功缝合的 canonical SSE 事件时间（毫秒）；用于降级健康期整页轮询。 */
  lastLiveEventAt: number | null;
  /** 是否还有更早历史页（统一快照/历史分页）。 */
  hasMoreHistory: boolean;
  /** 上滚加载旧页是否在途。 */
  isLoadingOlder: boolean;
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

type LiveActiveRun = NonNullable<AgentConversationView['activeRun']>;
type LiveProcessItem = LiveActiveRun['outputSnapshot']['processItems'][number];

const LIVE_ACTIVE_RUN_MARKDOWN_MAX_CHARS = 512 * 1024;
const LIVE_ACTIVE_RUN_PROCESS_ITEM_MAX = 64;
const LIVE_EVENT_ID_MAX = 4096;

const liveString = (
  event: AdminChatStreamEvent,
  ...keys: string[]
): string | null => {
  const record = event as Record<string, unknown>;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.length > 0) return value;
  }
  return null;
};

const liveNumber = (
  event: AdminChatStreamEvent,
  ...keys: string[]
): number | null => {
  const record = event as Record<string, unknown>;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value)) return value;
  }
  return null;
};

const boundLiveMarkdown = (markdown: string): string =>
  markdown.length > LIVE_ACTIVE_RUN_MARKDOWN_MAX_CHARS
    ? markdown.slice(0, LIVE_ACTIVE_RUN_MARKDOWN_MAX_CHARS)
    : markdown;

const appendLiveProcessItem = (
  activeRun: LiveActiveRun,
  item: LiveProcessItem,
): LiveActiveRun => ({
  ...activeRun,
  outputSnapshot: {
    ...activeRun.outputSnapshot,
    processItems: [
      ...activeRun.outputSnapshot.processItems,
      item,
    ].slice(-LIVE_ACTIVE_RUN_PROCESS_ITEM_MAX),
  },
});


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
  let conversationInFlight: {
    key: string;
    promise: Promise<AgentConversationView | null>;
  } | null = null;
  let selectionAbort: AbortController | null = null;
  let backgroundSyncAbort: AbortController | null = null;
  let liveSeenEventIds = new Set<string>();
  let snapshot: AgentChatClientSnapshot = {
    ownerUserId,
    statuses: [],
    conversation: null,
    isOpening: false,
    isRefreshing: false,
    lastLiveEventAt: null,
    hasMoreHistory: false,
    isLoadingOlder: false,
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

  /**
   * 同 scope+窗口+条件版本的打开请求合并（Plan §7.2）：并发选择/后台校正共用同一个 Promise，
   * 不再重复拉取。只合并完全相同的请求 key；不同 agent/knownCursor 不互相污染。
   */
  const fetchConversationOnce = (
    workspaceId: string,
    agentId: string,
    knownCursor?: number,
    signal?: AbortSignal,
  ): Promise<AgentConversationView | null> => {
    const key = `${workspaceId}\u0000${agentId}\u0000${knownCursor ?? 'none'}`;
    if (conversationInFlight?.key === key) {
      return conversationInFlight.promise;
    }

    const promise = (
      input.api.getConversationFromSnapshot
        ? input.api.getConversationFromSnapshot(
            workspaceId,
            agentId,
            ownerUserId,
            signal,
          )
        : input.api.getConversation(workspaceId, agentId, knownCursor, signal)
    ).finally(() => {
      if (conversationInFlight?.promise === promise) {
        conversationInFlight = null;
      }
    });
    conversationInFlight = { key, promise };
    return promise;
  };

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
      const sameSelection =
        snapshot.workspaceId === workspaceId && snapshot.agentId === agentId;
      // 重复选择当前 key：若首次打开/后台校准仍在途，加入既有请求；不增加 generation。
      if (sameSelection && (snapshot.isOpening || snapshot.isRefreshing)) {
        return;
      }

      const version = ++selectionVersion;
      backgroundSyncVersion += 1;
      const ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID;
      const existingConversation = sameSelection ? snapshot.conversation : null;

      // 会话切换取消旧选择与旧后台校正的在途请求；同一 key 重入不取消共享请求。
      if (!sameSelection) {
        selectionAbort?.abort();
        liveSeenEventIds.clear();
      }
      backgroundSyncAbort?.abort();
      backgroundSyncAbort = null;
      const abortController = new AbortController();
      selectionAbort = abortController;

      const commitStartedAt = performance.now();
      set({
        workspaceId,
        ownerUserId,
        agentId,
        conversation: existingConversation,
        isOpening: !existingConversation,
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

      const retainedConversation = cached ?? existingConversation;
      set({
        conversation: retainedConversation,
        isOpening: !retainedConversation,
        isRefreshing: true,
      });

      try {
        const apiStartedAt = performance.now();
        const fresh = await fetchConversationOnce(
          workspaceId,
          agentId,
          undefined,
          abortController.signal,
        );
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
          set({
            conversation: fresh,
            isOpening: false,
            isRefreshing: false,
            hasMoreHistory: fresh.hasMoreHistory ?? false,
            isLoadingOlder: false,
          });
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
            isOpening: false,
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
      backgroundSyncAbort?.abort();
      const syncAbortController = new AbortController();
      backgroundSyncAbort = syncAbortController;
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
        const fresh = await fetchConversationOnce(
          workspaceId,
          agentId,
          knownCursor,
          syncAbortController.signal,
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
          set({
            conversation: mergedConversation,
            hasMoreHistory: fresh.hasMoreHistory ?? snapshot.hasMoreHistory,
            error: null,
          });
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
        if (backgroundSyncAbort === syncAbortController) {
          backgroundSyncAbort = null;
        }
        syncInFlight = false;
      }
    },
    /**
     * 阶段 D2c2：向上加载更早一页（键集 createdAt+messageId）。
     * 只 prepend 当前页并按 messageId 去重；迟到/跨身份响应不得改写当前会话。
     */
    async loadOlder() {
      if (
        !input.api.getHistoryPage ||
        snapshot.isLoadingOlder ||
        !snapshot.hasMoreHistory
      ) {
        return;
      }
      const conversation = snapshot.conversation;
      const workspaceId = snapshot.workspaceId;
      const agentId = snapshot.agentId;
      if (!conversation || !workspaceId || !agentId) return;

      const version = selectionVersion;
      const oldest = conversation.messages[0];
      const oldestCreatedAt = oldest ? Date.parse(oldest.createdAt) : Number.NaN;
      const beforeCreatedAt =
        conversation.historyNextBeforeCreatedAt ??
        (Number.isFinite(oldestCreatedAt) ? oldestCreatedAt : null);
      const beforeMessageId =
        conversation.historyNextBeforeMessageId ?? oldest?.messageId ?? null;

      set({ isLoadingOlder: true });
      try {
        const page = await input.api.getHistoryPage(
          conversation.mainSessionId,
          beforeCreatedAt,
          beforeMessageId,
          20,
          selectionAbort?.signal,
        );
        if (
          version !== selectionVersion ||
          snapshot.workspaceId !== workspaceId ||
          snapshot.agentId !== agentId
        ) {
          return;
        }

        const existing = new Set(
          conversation.messages.map((message) => message.messageId),
        );
        const older = page.items
          .filter((item) => !existing.has(item.messageId))
          .map((item) => mapSnapshotHistoryItem(item, undefined));
        const nextConversation: AgentConversationView = {
          ...conversation,
          messages: [...older, ...conversation.messages],
          hasMoreHistory: page.hasMore,
          historyNextBeforeCreatedAt: page.nextBeforeCreatedAt ?? null,
          historyNextBeforeMessageId: page.nextBeforeMessageId ?? null,
        };
        set({
          conversation: nextConversation,
          hasMoreHistory: page.hasMore,
          isLoadingOlder: false,
        });
      } catch (error) {
        if (version === selectionVersion) {
          set({
            isLoadingOlder: false,
            error: error instanceof Error ? error.message : String(error),
          });
        }
      }
    },
    /**
     * 阶段 D：把 projection-owned 主会话的 canonical SSE 事件缝合进唯一 Store。
     * 只维护活动 Turn 的有界正文/过程与 eventCursor；终态消息由快照/轮询合并器负责，
     * 避免 SSE 与正文快照重复叠加（Plan §7.2）。
     */
    applyLiveEvent(
      workspaceId: string,
      agentId: string,
      event: AdminChatStreamEvent,
    ): boolean {
      const conversation = snapshot.conversation;
      if (
        !conversation ||
        snapshot.workspaceId !== workspaceId ||
        snapshot.agentId !== agentId
      ) {
        return false;
      }

      const eventType = String(event.type);
      const eventId = liveString(event, 'eventId');
      if (eventId && liveSeenEventIds.has(eventId)) return false;

      const sequence =
        liveNumber(event, 'sequence', 'sequenceNum') ??
        conversation.eventCursor + 1;
      // 快照已覆盖的事件不再重复投影；turn.started 允许作为新 Turn 起点。
      if (sequence <= conversation.eventCursor && eventType !== 'turn.started')
        return false;

      const now = new Date().toISOString();
      const turnId = liveString(event, 'turnId', 'turn_id');
      const runId = liveString(event, 'runId', 'run_id');
      let nextConversation: AgentConversationView | null = null;

      const ensureActiveRun = (): LiveActiveRun => {
        const existing = conversation.activeRun;
        if (existing && existing.status === 'running') return existing;
        return {
          runId: runId ?? `live-${turnId ?? sequence}`,
          workspaceId: conversation.workspaceId,
          ownerUserId: conversation.ownerUserId,
          agentId: conversation.agentId,
          mainSessionId: conversation.mainSessionId,
          status: 'running',
          statusText: '运行中',
          summary: '',
          eventCursor: sequence,
          outputSnapshot: { markdown: '', processItems: [] },
          startedAt: now,
          updatedAt: now,
          completedAt: null,
        };
      };

      switch (eventType) {
        case 'turn.started': {
          nextConversation = {
            ...conversation,
            activeRun: ensureActiveRun(),
            eventCursor: Math.max(conversation.eventCursor, sequence),
            updatedAt: now,
          };
          break;
        }
        case 'message.content.appended': {
          const delta = liveString(event, 'delta') ?? '';
          if (!delta) return false;
          const activeRun = ensureActiveRun();
          nextConversation = {
            ...conversation,
            activeRun: {
              ...activeRun,
              outputSnapshot: {
                ...activeRun.outputSnapshot,
                markdown: boundLiveMarkdown(
                  activeRun.outputSnapshot.markdown + delta,
                ),
              },
              eventCursor: Math.max(activeRun.eventCursor, sequence),
              updatedAt: now,
            },
            eventCursor: Math.max(conversation.eventCursor, sequence),
            updatedAt: now,
          };
          break;
        }
        case 'message.thinking_summary.appended': {
          const delta = liveString(event, 'delta') ?? '';
          if (!delta) return false;
          const activeRun = ensureActiveRun();
          const withProcess = appendLiveProcessItem(activeRun, {
            id: eventId ?? `thinking-${sequence}`,
            kind: 'thinking',
            status: 'succeeded',
            text: delta,
            timestamp: now,
            sequence,
          });
          nextConversation = {
            ...conversation,
            activeRun: {
              ...withProcess,
              eventCursor: Math.max(activeRun.eventCursor, sequence),
              updatedAt: now,
            },
            eventCursor: Math.max(conversation.eventCursor, sequence),
            updatedAt: now,
          };
          break;
        }
        case 'tool.call.requested':
        case 'tool.call.completed':
        case 'tool.call.failed': {
          const activeRun = ensureActiveRun();
          const toolCallId =
            liveString(event, 'toolCallId', 'tool_call_id') ?? undefined;
          const name = liveString(event, 'name') ?? undefined;
          const output = liveString(event, 'output') ?? undefined;
          const error = liveString(event, 'error', 'message') ?? undefined;
          const status =
            eventType === 'tool.call.completed'
              ? 'succeeded'
              : eventType === 'tool.call.failed'
                ? 'failed'
                : 'running';
          const kind =
            eventType === 'tool.call.requested' ? 'tool_call' : 'tool_result';
          const withProcess = appendLiveProcessItem(activeRun, {
            id: eventId ?? `${kind}-${sequence}`,
            kind,
            status,
            text:
              eventType === 'tool.call.requested'
                ? (name ?? '')
                : (output ?? error ?? name ?? ''),
            timestamp: now,
            sequence,
            toolCallId,
            name,
            arguments: liveString(event, 'arguments') ?? undefined,
            output,
            exitCode:
              liveNumber(event, 'exitCode', 'exit_code') ?? undefined,
            message: error,
          });
          nextConversation = {
            ...conversation,
            activeRun: {
              ...withProcess,
              eventCursor: Math.max(activeRun.eventCursor, sequence),
              updatedAt: now,
            },
            eventCursor: Math.max(conversation.eventCursor, sequence),
            updatedAt: now,
          };
          break;
        }
        case 'turn.completed':
        case 'turn.failed':
        case 'turn.cancelled': {
          const status =
            eventType === 'turn.completed'
              ? 'succeeded'
              : eventType === 'turn.failed'
                ? 'failed'
                : 'cancelled';
          const activeRun = conversation.activeRun;
          nextConversation = {
            ...conversation,
            activeRun: activeRun
              ? {
                  ...activeRun,
                  status,
                  statusText:
                    status === 'succeeded'
                      ? '已完成'
                      : status === 'failed'
                        ? '失败'
                        : '已取消',
                  eventCursor: Math.max(activeRun.eventCursor, sequence),
                  updatedAt: now,
                  completedAt: now,
                  outputSnapshot: {
                    ...activeRun.outputSnapshot,
                    processSummary: {
                      ...(activeRun.outputSnapshot.processSummary ?? {
                        totalItems: 0,
                        thinkingRounds: 0,
                        thinkingSteps: 0,
                        toolCalls: 0,
                        toolResults: 0,
                        failedTools: 0,
                        durationMs: 0,
                        hasDetails: false,
                      }),
                      totalItems: activeRun.outputSnapshot.processItems.length,
                    },
                  },
                }
              : null,
            eventCursor: Math.max(conversation.eventCursor, sequence),
            updatedAt: now,
          };
          break;
        }
        default:
          return false;
      }

      if (!nextConversation) return false;
      if (eventId) {
        liveSeenEventIds.add(eventId);
        if (liveSeenEventIds.size > LIVE_EVENT_ID_MAX) {
          liveSeenEventIds = new Set([eventId]);
        }
      }
      set({ conversation: nextConversation, lastLiveEventAt: Date.now() });
      return true;
    },
  };

  return store;
}
