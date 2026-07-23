// ── 聊天页状态管理 Hook ──────────────────────────────────────
//
// ADR-057 迁移计划：
//   · activeMessageIds / messageIdToTurnId / completedTurnsRef → 替换为 conversationStore selectors
//   · reconcileCompletedSessionMessages / replay poll → 替换为 gapRecoveryEngine
//   · SSE 生命周期 → 替换为 useConversation hook
//   · 新模块（state/conversationStore, connection/gapRecoveryEngine, hooks/useConversation）
//     已就绪，渐进迁移中。
// ─────────────────────────────────────────────────────────────
import { App } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  createSession,
  ensureMainSession,
  submitConversationTurn,
} from '@/services/platform/api';
import {
  installPerfDiagnostics,
  recordPerfEvent,
  recordPerfStep,
} from '@/utils/debug';
import {
  getThinkingRawText,
  sanitizeProcessText,
} from '../components/processPreview';
import { flushOutbox } from '../outbox/commandOutbox';
import { createSessionTerminalEvent } from '../runtime/sessionLifecycleStore';
import type { ChatTurn } from '../types';
import { assistantStatusLabel } from '../types';
import type {
  ChatInteractionQueueItem,
  ChatInteractionQueueStatus,
  ChatInteractionRuntimeEvent,
  ChatInteractionRuntimeType,
  ChatRouteSelection,
  ChatSendOptions,
  UseChatStateReturn,
} from '../types/chatStateTypes';
import { STEERING_INJECTED_QUEUE_RETENTION_MS } from '../types/chatStateTypes';
import {
  formatChatErrorDiagnostic,
  isChatStreamErrorEvent,
  logChatDiag,
  looksLikePersistedErrorDiagnostic,
} from '../utils/chatDiagnostics';
import {
  applyBufferedDeltaToTurn,
  buildAgentMainSessionRequest,
  buildSessionEventReplayUrl,
  canBindUnknownMetadataToTurn,
  confirmOptimisticTurn,
  filterSubAgentCardsForSession,
  formatCompactSuccessMessage,
  formatTime,
  getAgentName,
  getChatRouteSelectionFromSearch,
  getHistoryReconcileBlockReason,
  getSessionEventSequenceNum,
  getStepTone,
  getTrackedActiveMessageIds,
  groupSessions,
  hasBlockingActiveTurn,
  hasTrackedActiveSessionMessages,
  inferParentSessionIdFromSubSessionId,
  isActiveAssistantTurn,
  mergeHistoryWithLifecycleTurns,
  parseSessionEventTimestampMs,
  removeInjectedSteeringQueueItem,
  removeTrackedActiveMessageIdsForTurn,
  resolveActiveSessionReplayFromSequence,
  resolveInitialAgentId,
  resolveInitialWorkspaceId,
  resolveSessionReplayCursorSequence,
  resolveSessionReplayPollInterval,
  resolveSubAgentTaskSummary,
  resolveSubAgentTerminalOutput,
  resolveTerminalAssistantMarkdown,
  resolveTurnIdForEvent,
  shouldAdvanceSequenceForSessionEvent,
  shouldHydrateSessionEventReplay,
  shouldReplayEventsAfterHistory,
  shouldResetSequenceForSessionChange,
  shouldRunSessionReplayCompensation,
  stringToColor,
  toChatInteractionQueueItem,
  toChatInteractionRuntimeEvent,
  toSessionListItem,
} from '../utils/chatStateUtils';
import type { ScrollIntent } from '../viewport/types';
import {
  clearDeletedSessionReferences,
  disposeCurrentSessionRuntime,
  type SessionRuntimeRefs,
} from './sessionRuntimeCleanup';
import { useChatModals } from './useChatModals';
import { useChatRuntimeEvents } from './useChatRuntimeEvents';
import { useCompaction } from './useCompaction';
import { useMessageHistoryPagination } from './useMessageHistoryPagination';
import { useMessageInteractionQueue } from './useMessageInteractionQueue';
import { useMessageSend } from './useMessageSend';
import { useSessionCatalog } from './useSessionCatalog';
import { useSessionEventBuffers } from './useSessionEventBuffers';
import { useSessionEventConnection } from './useSessionEventConnection';
import { useSessionEventProjection } from './useSessionEventProjection';
import { useSessionEventReplay } from './useSessionEventReplay';
import { useSessionHistoryProjection } from './useSessionHistoryProjection';
import { useSessionSelection } from './useSessionSelection';
import { useWorkspaceAgentSelection } from './useWorkspaceAgentSelection';
import { useWorkspaceNotifications } from './useWorkspaceNotifications';

export type {
  ChatInteractionQueueItem,
  ChatInteractionQueueStatus,
  ChatInteractionRuntimeEvent,
  ChatInteractionRuntimeType,
  ChatRouteSelection,
  ChatSendOptions,
  UseChatStateReturn,
};
export {
  applyBufferedDeltaToTurn,
  buildAgentMainSessionRequest,
  buildSessionEventReplayUrl,
  canBindUnknownMetadataToTurn,
  confirmOptimisticTurn,
  filterSubAgentCardsForSession,
  formatChatErrorDiagnostic,
  formatCompactSuccessMessage,
  getAgentName,
  getChatRouteSelectionFromSearch,
  getHistoryReconcileBlockReason,
  getSessionEventSequenceNum,
  getTrackedActiveMessageIds,
  groupSessions,
  hasBlockingActiveTurn,
  hasTrackedActiveSessionMessages,
  inferParentSessionIdFromSubSessionId,
  isActiveAssistantTurn,
  isChatStreamErrorEvent,
  looksLikePersistedErrorDiagnostic,
  mergeHistoryWithLifecycleTurns,
  parseSessionEventTimestampMs,
  removeInjectedSteeringQueueItem,
  removeTrackedActiveMessageIdsForTurn,
  resolveActiveSessionReplayFromSequence,
  resolveInitialAgentId,
  resolveInitialWorkspaceId,
  resolveSessionReplayCursorSequence,
  resolveSessionReplayPollInterval,
  resolveSubAgentTaskSummary,
  resolveSubAgentTerminalOutput,
  resolveTerminalAssistantMarkdown,
  resolveTurnIdForEvent,
  STEERING_INJECTED_QUEUE_RETENTION_MS,
  shouldAdvanceSequenceForSessionEvent,
  shouldHydrateSessionEventReplay,
  shouldReplayEventsAfterHistory,
  shouldResetSequenceForSessionChange,
  shouldRunSessionReplayCompensation,
  stringToColor,
  toChatInteractionQueueItem,
  toChatInteractionRuntimeEvent,
  toSessionListItem,
};
export function useChatState(routeSearch?: string): UseChatStateReturn {
  const { message: messageApi } = App.useApp();
  const [error, setError] = useState<string | null>(null);
  const {
    routeSelection,
    workspaces,
    setWorkspaces,
    workspaceId,
    setWorkspaceId,
    workspaceLoading,
    agents,
    setAgents,
    agentId,
    setAgentId,
    agentLoading,
    selectedAgent,
    wsOpts,
    agOpts,
    creatingSession,
    setCreatingSession,
    suppressMainSessionEnsure,
    consumeMainSessionEnsureSuppression,
    resetMainSessionEnsureSuppression,
  } = useWorkspaceAgentSelection({ routeSearch, onError: setError });
  const {
    createSceneOpen,
    setCreateSceneOpen,
    createSceneLoading,
    createSceneForm,
    renameModalOpen,
    setRenameModalOpen,
    renameTitle,
    setRenameTitle,
    renameSessionId,
    openRenameModal,
    closeRenameModal,
  } = useChatModals();
  const {
    sidebarOpen,
    setSidebarOpen,
    sessions,
    setSessions,
    selectedSessionId,
    setSelectedSessionId,
    sessionsLoading,
    mainSessionId,
    setMainSessionId,
    sessionIdRef,
    selectedSessionIdRef,
    mainSessionIdRef,
    forceNewSessionRef,
    refreshSessions,
    handleDeleteSession,
    handleArchiveSession,
    handleRenameSubmit,
    bindSessionNotFoundHandler,
  } = useSessionCatalog({
    workspaceId,
    renameSessionId,
    renameTitle,
    closeRenameModal,
    messageApi,
  });

  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [viewportScrollIntent, setViewportScrollIntent] =
    useState<ScrollIntent>({ type: 'none' });
  const clearViewportScrollIntent = useCallback(() => {
    setViewportScrollIntent({ type: 'none' });
  }, []);
  const {
    chatInteractionRuntimeEvents,
    appendChatInteractionRuntimeEvent,
    clearChatInteractionRuntimeEvents,
  } = useChatRuntimeEvents();
  const [loading, setLoading] = useState(false);
  const messageListRef = useRef<HTMLDivElement>(null);
  const listEndRef = useRef<HTMLDivElement>(null);
  const completedTurnsRef = useRef<Set<string>>(new Set());
  const latestTurnIdRef = useRef<string | null>(null);
  const messageIdToTurnIdRef = useRef<Map<string, string>>(new Map());
  const lastSequenceNumRef = useRef<number>(0);
  const projectionOwnedSessionIdsRef = useRef<Set<string>>(new Set());
  const {
    sessionEventsAbortRef,
    sessionEventsPollTimerRef,
    sessionEventsReconnectTimerRef,
    sseSessionIdRef,
    lastSseEventAtRef,
    reconnectCountRef,
    startSessionEventStream,
    stopSessionEventStream,
    bindSessionEventConnection,
  } = useSessionEventConnection();

  // ADR-fix1-004 Phase 1: 收敛 4 个 session ref 为单一身份对象（逐步迁移）
  interface SessionIdentity {
    route: string | undefined;
    selected: string | null;
    stream: string | null;
    main: string | null;
  }
  const sessionIdentityRef = useRef<SessionIdentity>({
    route: undefined,
    selected: null,
    stream: null,
    main: null,
  });
  const syncSessionIdentity = useCallback(() => {
    sessionIdentityRef.current = {
      route: sessionIdRef.current,
      selected: selectedSessionIdRef.current,
      stream: sseSessionIdRef.current,
      main: mainSessionIdRef.current,
    };
  }, []);

  const loadingRef = useRef(false);
  const turnsRef = useRef<ChatTurn[]>([]);
  const activeMessageIdsRef = useRef<Set<string>>(new Set());
  const {
    pendingDeltaRef,
    deltaFlushTimerRef,
    pendingThinkingRef,
    thinkingFlushTimerRef,
    enqueueDelta,
    flushPendingDeltas,
    enqueueThinking,
    flushPendingThinking,
    resetSessionEventBuffers,
    prepareForNewMessage,
  } = useSessionEventBuffers({ setTurns, completedTurnsRef });

  const {
    handleCompactionLifecycleEvent,
    handleCompactCommand,
    resetCompaction,
    mergeCompactionLifecycleTurns,
    bindCompactedSessionSwitch,
    markManualSessionSwitch,
  } = useCompaction({
    identity: {
      workspaceId,
      agentId,
      selectedSessionId,
      sessionIdRef,
    },
    turns: {
      turnsRef,
      latestTurnIdRef,
      setTurns,
    },
    status: {
      loading,
      setLoading,
      setError,
    },
    messageApi,
  });
  const {
    inputValue,
    setInputValue,
    interactionQueue,
    enqueueInteraction,
    submitInteraction,
    updateQueuedInteraction,
    deleteQueuedInteraction,
    sendQueuedInteractionNow,
    steerQueuedInteraction,
    handleKeyDown,
    markSteeringInjected,
    bindSendMessage,
  } = useMessageInteractionQueue({
    identity: {
      workspaceId,
      agentId,
      selectedSessionId,
      sessionIdRef,
    },
    execution: {
      loading,
      turns,
      turnsRef,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      handleCompactCommand,
    },
    messageApi,
  });
  const {
    historyLoading,
    setHistoryLoading,
    hasMoreMessages,
    setHasMoreMessages,
    setOldestMessageCursor,
    loadingMore,
    loadMoreMessages,
    resetHistoryPagination,
    bindHistoryProjector,
  } = useMessageHistoryPagination({
    selectedSessionId,
    turnsRef,
    setTurns,
    messageApi,
  });

  const {
    workingAgentIds,
    subAgentRuns,
    setSubAgentRuns,
    subAgentCards,
    latestUsage,
    setLatestUsage,
    sessionCacheHitTokens,
    setSessionCacheHitTokens,
    sessionCacheMissTokens,
    setSessionCacheMissTokens,
    hydrateSessionReplayRef,
    duplicateDeltaReplayOffsetRef,
    eventCountsRef,
    streamStartAtRef,
    messageIdToAgentIdsRef,
    sessionIdToAgentIdsRef,
    setAgentIdsWorking,
    reconcileSessionWorkingAgents,
    resetStreamCursorForSessionChange,
    pruneTrackedActiveMessages,
    applySessionEvent,
  } = useSessionEventProjection({
    identity: {
      agentId,
      selectedAgent,
      mainSessionId,
      selectedSessionId,
      sseSessionIdRef,
      selectedSessionIdRef,
      sessionIdRef,
    },
    turns: {
      turnsRef,
      setTurns,
      completedTurnsRef,
      latestTurnIdRef,
      messageIdToTurnIdRef,
      lastSequenceNumRef,
      activeMessageIdsRef,
    },
    buffers: {
      pendingDeltaRef,
      pendingThinkingRef,
      enqueueDelta,
      enqueueThinking,
      flushPendingDeltas,
      flushPendingThinking,
      resetSessionEventBuffers,
    },
    integrations: {
      setLoading,
      appendRuntimeEvent: appendChatInteractionRuntimeEvent,
      markSteeringInjected,
      handleCompactionLifecycleEvent,
    },
  });

  useEffect(() => {
    installPerfDiagnostics();
  }, []);

  useEffect(() => {
    turnsRef.current = turns;
  }, [turns]);

  useEffect(() => {
    loadingRef.current = loading;
  }, [loading]);

  useEffect(() => {
    syncSessionIdentity();
    logChatDiag('selectedSessionId.synced', {
      selectedSessionId,
      sessionIdRef: sessionIdRef.current,
      mainSessionId,
      routeSessionId: routeSelection.sessionId,
      agentId,
      turnCount: turnsRef.current.length,
      url: typeof window === 'undefined' ? undefined : window.location.href,
    });
  }, [agentId, mainSessionId, routeSelection.sessionId, selectedSessionId]);

  useEffect(() => {
    syncSessionIdentity();
  }, [mainSessionId]);

  useEffect(() => {
    const flushPendingCommands = () => {
      void flushOutbox(async (record) => {
        await submitConversationTurn(
          record.workspaceId,
          record.conversationId,
          {
            clientRequestId: record.id,
            clientMessageId: record.clientMessageId,
            recipients: { type: 'agent', agentIds: record.agentIds },
            content: [{ type: 'text', text: record.messageText }],
            metadata: record.metadata,
          },
        );
      });
    };

    flushPendingCommands();
    window.addEventListener('online', flushPendingCommands);
    return () => window.removeEventListener('online', flushPendingCommands);
  }, []);

  // P0 v2: 有作用域的 session runtime 清理 + 统一通知入口
  const runtimeRefs: SessionRuntimeRefs = useMemo(
    () => ({
      sessionIdRef,
      sseSessionIdRef,
      lastSequenceNumRef,
      messageIdToTurnIdRef,
      activeMessageIdsRef,
      projectionOwnedSessionIdsRef,
      turnsRef,
      completedTurnsRef,
      latestTurnIdRef,
      forceNewSessionRef,
      sessionEventsAbortRef,
      sessionEventsPollTimerRef,
      sessionEventsReconnectTimerRef,
      deltaFlushTimerRef,
      thinkingFlushTimerRef,
      pendingDeltaRef,
      pendingThinkingRef,
      streamStartAtRef,
      messageIdToAgentIdsRef,
      duplicateDeltaReplayOffsetRef,
      eventCountsRef,
    }),
    [],
  );

  /**
   * P0 v2 统一入口：处理 session 不再存在（删除/归档/404）。
   *
   * 所有路径（replay 404 / SSE 404 / handleDeleteSession / handleArchiveSession）最终都走这里。
   * 根据 sessionId 与当前 selected/stream/main 的匹配关系，做不同层次的清理。
   */
  const handleSessionNotFound = useCallback(
    (sessionId: string, reason: string) => {
      const isSelected = selectedSessionIdRef.current === sessionId;
      const isCurrentStream = sseSessionIdRef.current === sessionId;
      const isMain = mainSessionIdRef.current === sessionId;

      // ADR-054: lifecycle 事件已创建，后续 MR 需要：
      // 1. 用 useState/useRef 维护 lifecycleState
      // 2. 用 reduceSessionLifecycle(lifecycleState, event) 更新状态
      // 3. 让清理逻辑依赖 lifecycleState.phase 而非手动 ref 检查
      const _terminalEvent = createSessionTerminalEvent(sessionId, reason);
      void _terminalEvent; // TODO(ADR-054): dispatch to lifecycle state machine

      console.debug('[Pudding Chat] handleSessionNotFound', {
        sessionId,
        reason,
        isSelected,
        isCurrentStream,
        isMain,
        currentSelected: selectedSessionIdRef.current,
        currentSse: sseSessionIdRef.current,
        currentMain: mainSessionIdRef.current,
      });

      // 1. 如果是当前流 → 停止 SSE + 清除 active refs
      // 注意：只有 isCurrentStream 才能 abort SSE，isSelected 不能（可能 SSE 已切到别的 session）
      if (isCurrentStream) {
        disposeCurrentSessionRuntime(sessionId, runtimeRefs, reason);
        stopSessionEventStream();
      }

      // 2. 清除非流引用（projectionOwned 等）
      clearDeletedSessionReferences(sessionId, runtimeRefs);

      // 3. React 层清理
      if (isSelected) {
        setSelectedSessionId(null);
        turnsRef.current = [];
        setTurns([]);
        clearChatInteractionRuntimeEvents();
        setSubAgentRuns({});
        resetHistoryPagination();
        messageIdToTurnIdRef.current.clear();
        lastSequenceNumRef.current = 0;
      }

      // 4. 如果是 main session → 清除 mainSessionId 和 agent 缓存，并抑制自动重建
      if (isMain) {
        suppressMainSessionEnsure();
        setMainSessionId(null);
        setAgents((prev) =>
          prev.map((a) =>
            a.mainSessionId === sessionId
              ? { ...a, mainSessionId: undefined }
              : a,
          ),
        );
      }

      // 5. 从列表移除（或标记 stale）
      setSessions((prev) => prev.filter((s) => s.sessionId !== sessionId));

      logChatDiag('session.notFound', {
        sessionId,
        reason,
        isSelected,
        isCurrentStream,
        isMain,
      });
    },
    [
      runtimeRefs,
      stopSessionEventStream,
      suppressMainSessionEnsure,
      clearChatInteractionRuntimeEvents,
      resetHistoryPagination,
    ],
  );
  bindSessionNotFoundHandler(handleSessionNotFound);
  const {
    syncCompletedHistoryEventCursor,
    replayMissedSessionEvents,
    replayMissedSessionEventsIfNeeded,
    replayLatestTurnSessionEvents,
  } = useSessionEventReplay({
    identity: {
      lastSequenceNumRef,
      sseSessionIdRef,
      lastSseEventAtRef,
      activeMessageIdsRef,
      selectedSessionIdRef,
      sessionIdRef,
      hydrateSessionReplayRef,
    },
    projection: {
      applySessionEvent,
      handleCompactionLifecycleEvent,
      setSubAgentRuns,
      subAgentRuns,
      pruneTrackedActiveMessages,
    },
  });

  bindSessionEventConnection({
    applySessionEvent,
    handleSessionNotFound,
    pruneTrackedActiveMessages,
    replayMissedSessionEvents,
    replayMissedSessionEventsIfNeeded,
    resetStreamCursorForSessionChange,
    flushPendingDeltas,
    syncSessionIdentity,
    activeMessageIdsRef,
    lastSequenceNumRef,
    streamStartAtRef,
    selectedSessionIdRef,
    sessionIdRef,
    turnsRef,
  });

  const { toTurnsFromHistory, reconcileCompletedSessionMessages } =
    useSessionHistoryProjection({
      identity: { selectedSessionIdRef, sessionIdRef },
      turns: { turnsRef, setTurns, latestTurnIdRef, setLoading },
      integrations: {
        mergeCompactionLifecycleTurns,
        syncCompletedHistoryEventCursor,
        bindHistoryProjector,
      },
    });

  // ── resetConversation ──────────────────────────────────────
  const resetConversation = useCallback(
    async (nextWorkspaceId?: string, nextAgentId?: string) => {
      if (creatingSession) return undefined;
      flushPendingDeltas();
      abortRef.current?.abort();
      abortRef.current = null;
      stopSessionEventStream();
      turnsRef.current = [];
      setTurns([]);
      setError(null);
      setLatestUsage(undefined);
      setLoading(false);
      clearChatInteractionRuntimeEvents();
      resetHistoryPagination();
      lastSequenceNumRef.current = 0;
      latestTurnIdRef.current = null;
      messageIdToTurnIdRef.current.clear();
      resetCompaction();
      const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
      const targetAgentId = nextAgentId ?? agentId;
      if (!targetWorkspaceId || !targetAgentId) return undefined;
      setCreatingSession(true);
      try {
        const selected = agents.find((a) => a.agentId === targetAgentId);
        const agName = selected ? getAgentName(selected) : '新对话';
        const templateId =
          selected?.sourceTemplateId || `global:${targetAgentId}`;
        const session = await createSession(
          targetWorkspaceId,
          templateId,
          undefined,
          agName,
        );
        sessionIdRef.current = session.sessionId;
        forceNewSessionRef.current = false;
        setSelectedSessionId(session.sessionId);
        setSessions((prev) => [toSessionListItem(session, agName), ...prev]);
        return session.sessionId;
      } catch {
        messageApi.error('创建会话失败');
        return undefined;
      } finally {
        setCreatingSession(false);
      }
    },
    [
      workspaceId,
      agentId,
      agents,
      creatingSession,
      messageApi,
      stopSessionEventStream,
      clearChatInteractionRuntimeEvents,
      resetCompaction,
      resetHistoryPagination,
    ],
  );

  const switchToCompactedSessionPreservingTurns = useCallback(
    (sessionId: string, title?: string | null) => {
      if (!sessionId) return;
      const previousSessionId =
        sessionIdRef.current ?? selectedSessionIdRef.current ?? null;
      if (
        previousSessionId === sessionId &&
        selectedSessionIdRef.current === sessionId
      ) {
        logChatDiag('compact.switch.noop', {
          sessionId,
          title,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
        });
        return;
      }
      logChatDiag('compact.switch.apply', {
        sessionId,
        title,
        previousSessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        turnCount: turnsRef.current.length,
      });

      sessionIdRef.current = sessionId;
      selectedSessionIdRef.current = sessionId;
      setSelectedSessionId(sessionId);
      setMainSessionId(sessionId);
      forceNewSessionRef.current = false;
      // Conversation event sequences are scoped per conversation. Carrying the
      // source cursor into the successor would skip its compaction-origin event
      // and every early event whose sequence is lower than the old cursor.
      lastSequenceNumRef.current = 0;
      messageIdToTurnIdRef.current.clear();
      projectionOwnedSessionIdsRef.current.delete(sessionId);
      setSessions((prev) => {
        const idx = prev.findIndex((item) => item.sessionId === sessionId);
        const timestamp = Date.now();
        if (idx >= 0) {
          const updated = { ...prev[idx], timestamp, mainSessionId: sessionId };
          if (title && (!updated.title || updated.title === '对话')) {
            updated.title = title;
          }
          return [updated, ...prev.slice(0, idx), ...prev.slice(idx + 1)];
        }
        return [
          {
            sessionId,
            title: title || '压缩后的新会话',
            timestamp,
            mainSessionId: sessionId,
          },
          ...prev,
        ];
      });
      startSessionEventStream(sessionId);
      refreshSessions({ preserveSessionId: sessionId });
    },
    [refreshSessions, startSessionEventStream],
  );

  bindCompactedSessionSwitch(switchToCompactedSessionPreservingTurns);

  const {
    sessionUnreadCounts,
    startWorkspaceNotificationStream,
    stopWorkspaceNotificationStream,
    clearSessionUnread,
  } = useWorkspaceNotifications({ onSessionCreated: refreshSessions });

  // 记录最新 turn，供持久 SSE 的异步事件路由使用。
  useEffect(() => {
    if (turns.length === 0) {
      latestTurnIdRef.current = null;
      return;
    }
    latestTurnIdRef.current = turns[turns.length - 1].turnId;
  }, [turns.length]);

  // ── handleSelectSession ────────────────────────────────────
  const { handleSelectSession } = useSessionSelection({
    catalog: {
      workspaceId,
      agentId,
      routeSelection,
      sessions,
      selectedSessionId,
      mainSessionId,
      setSelectedSessionId,
      sessionIdRef,
      selectedSessionIdRef,
      forceNewSessionRef,
    },
    turns: {
      turns,
      turnsRef,
      latestTurnIdRef,
      messageIdToTurnIdRef,
      setTurns,
      setLoading,
      reconcileWorkingAgents: reconcileSessionWorkingAgents,
    },
    history: {
      toTurnsFromHistory,
      setHistoryLoading,
      setHasMoreMessages,
      setOldestMessageCursor,
      resetHistoryPagination,
    },
    stream: {
      stop: stopSessionEventStream,
      start: startSessionEventStream,
      replayLatestTurn: replayLatestTurnSessionEvents,
      syncCompletedCursor: syncCompletedHistoryEventCursor,
      projectionOwnedSessionIdsRef,
      lastSequenceNumRef,
    },
    lifecycle: {
      clearSessionUnread,
      clearRuntimeEvents: clearChatInteractionRuntimeEvents,
      resetCompaction,
    },
    onManualSwitch: markManualSessionSwitch,
    setViewportScrollIntent,
    messageApi,
  });

  const ensureAgentMainSession = useCallback(
    async (
      nextWorkspaceId?: string,
      nextAgentId?: string,
      options?: { isCurrent?: () => boolean; selectSession?: boolean },
    ) => {
      const isCurrent = options?.isCurrent ?? (() => true);
      const shouldSelectSession = options?.selectSession ?? true;
      const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
      const targetAgentId = nextAgentId ?? agentId;
      const targetAgent = agents.find((item) => item.agentId === targetAgentId);
      const request = buildAgentMainSessionRequest(
        targetWorkspaceId,
        targetAgent,
      );
      if (!request) return undefined;
      const traceId = `agent-main-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      const ensureStartedAt = performance.now();

      resetMainSessionEnsureSuppression('explicit-ensure');
      setCreatingSession(true);
      try {
        const apiStartedAt = performance.now();
        const session = await ensureMainSession(request);
        logChatDiag('main.ensure.returned', {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          returnedSessionId: session.sessionId,
          returnedTitle: session.title,
          previousSelectedSessionId: selectedSessionIdRef.current,
          previousSessionIdRef: sessionIdRef.current,
          routeSessionId: routeSelection.sessionId,
          shouldSelectSession,
        });
        recordPerfStep(
          'agent.mainSession',
          'api.ensureMainSession',
          apiStartedAt,
          {
            traceId,
            workspaceId: targetWorkspaceId,
            agentId: targetAgentId,
            sessionId: session.sessionId,
            title: session.title ?? null,
          },
        );
        if (!isCurrent()) return undefined;
        const title =
          session.title?.trim() || request.title || targetAgentId || '主线';
        setMainSessionId(session.sessionId);
        if (targetAgentId) {
          setAgents((prev) =>
            prev.map((item) =>
              item.agentId === targetAgentId
                ? { ...item, mainSessionId: session.sessionId }
                : item,
            ),
          );
        }
        setSessionCacheHitTokens(0);
        setSessionCacheMissTokens(0);
        const mainSessionItem = toSessionListItem(session, title);
        setSessions((prev) => {
          const idx = prev.findIndex(
            (item) => item.sessionId === session.sessionId,
          );
          if (idx >= 0)
            return [
              mainSessionItem,
              ...prev.slice(0, idx),
              ...prev.slice(idx + 1),
            ];
          return [mainSessionItem, ...prev];
        });
        if (!isCurrent()) return undefined;
        if (!shouldSelectSession) {
          projectionOwnedSessionIdsRef.current.add(session.sessionId);
          sessionIdRef.current = session.sessionId;
          setSelectedSessionId(session.sessionId);
          setTurns([]);
          turnsRef.current = [];
          resetHistoryPagination();
          logChatDiag('main.ensure.selectSkipped', {
            traceId,
            sessionId: session.sessionId,
            reason: 'agent projection owns message loading',
          });
          recordPerfStep(
            'agent.mainSession',
            'session.selectSkipped',
            ensureStartedAt,
            {
              traceId,
              workspaceId: targetWorkspaceId,
              agentId: targetAgentId,
              sessionId: session.sessionId,
              reason: 'agent projection owns message loading',
            },
          );
          recordPerfStep(
            'agent.mainSession',
            'ensure.finish',
            ensureStartedAt,
            {
              traceId,
              workspaceId: targetWorkspaceId,
              agentId: targetAgentId,
              sessionId: session.sessionId,
              reason: 'session selection skipped',
            },
          );
          return session.sessionId;
        }
        const selectStartedAt = performance.now();
        const loadedTurnCount = await handleSelectSession(session.sessionId, {
          agentId: targetAgentId,
        });
        logChatDiag('main.ensure.selectMain.finish', {
          traceId,
          sessionId: session.sessionId,
          loadedTurnCount: loadedTurnCount ?? null,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          isCurrent: isCurrent(),
        });
        recordPerfStep(
          'agent.mainSession',
          'session.selectMain',
          selectStartedAt,
          {
            traceId,
            workspaceId: targetWorkspaceId,
            agentId: targetAgentId,
            sessionId: session.sessionId,
            loadedTurnCount: loadedTurnCount ?? null,
            status: isCurrent() ? 'ok' : 'stale',
          },
        );
        if (!isCurrent()) return undefined;
        recordPerfStep('agent.mainSession', 'ensure.finish', ensureStartedAt, {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          sessionId: session.sessionId,
          loadedTurnCount: loadedTurnCount ?? null,
        });
        return session.sessionId;
      } catch (error) {
        logChatDiag('main.ensure.error', {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          error,
        });
        recordPerfStep('agent.mainSession', 'ensure.error', ensureStartedAt, {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        messageApi.error('打开主线会话失败');
        return undefined;
      } finally {
        setCreatingSession(false);
      }
    },
    [
      agentId,
      agents,
      handleSelectSession,
      messageApi,
      resetMainSessionEnsureSuppression,
      resetHistoryPagination,
      routeSelection.sessionId,
      workspaceId,
    ],
  );

  useEffect(() => {
    if (!workspaceId || !agentId || routeSelection.sessionId) return;
    if (consumeMainSessionEnsureSuppression()) {
      logChatDiag('main.ensure.effectSkipped.suppressed', {
        workspaceId,
        agentId,
        reason: 'session-not-found',
      });
      return;
    }
    if (selectedSessionId) {
      logChatDiag('main.ensure.effectSkipped.selectedSession', {
        workspaceId,
        agentId,
        selectedSessionId,
        sessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSelection.sessionId,
        turns: turnsRef.current.length,
      });
      return;
    }
    void ensureAgentMainSession(workspaceId, agentId);
  }, [
    agentId,
    consumeMainSessionEnsureSuppression,
    ensureAgentMainSession,
    mainSessionId,
    routeSelection.sessionId,
    selectedSessionId,
    workspaceId,
  ]);

  // ── handleSetMainSession ──────────────────────────────────
  const handleSetMainSession = useCallback((sessionId: string) => {
    setMainSessionId(sessionId);
    setSessionCacheHitTokens(0);
    setSessionCacheMissTokens(0);
  }, []);

  // 持久 SSE：跟随当前会话生命周期自动切换。
  useEffect(() => {
    if (!selectedSessionId) {
      stopSessionEventStream();
      return;
    }
    if (projectionOwnedSessionIdsRef.current.has(selectedSessionId)) {
      stopSessionEventStream();
      recordPerfEvent(
        'chat.sse.skipped',
        {
          sessionId: selectedSessionId,
          reason: 'agent projection owns message loading',
        },
        { throttleMs: 1_000 },
      );
      return;
    }
    startSessionEventStream(selectedSessionId);
    return () => {
      stopSessionEventStream();
    };
  }, [selectedSessionId, startSessionEventStream, stopSessionEventStream]);

  // ── handleRenameStart ──────────────────────────────────────
  const handleRenameStart = openRenameModal;

  const { abortRef, sendMessage } = useMessageSend({
    identity: {
      workspaceId,
      agentId,
      agents,
      mainSessionId,
      routeSessionId: routeSelection.sessionId,
      sessionIdRef,
      selectedSessionIdRef,
      mainSessionIdRef,
      forceNewSessionRef,
      resetMainSessionEnsureSuppression,
    },
    turns: {
      loading,
      setLoading,
      loadingRef,
      turnsRef,
      setTurns,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      latestTurnIdRef,
      completedTurnsRef,
      setViewportScrollIntent,
    },
    sessions: {
      setMainSessionId,
      setSelectedSessionId,
      setSessions,
      refreshSessions,
    },
    stream: {
      startSessionEventStream,
      resetStreamCursorForSessionChange,
      replayMissedSessionEvents,
      replayMissedSessionEventsIfNeeded,
      reconcileCompletedSessionMessages,
      pendingDeltaRef,
      pendingThinkingRef,
      duplicateDeltaReplayOffsetRef,
      streamStartAtRef,
      prepareForNewMessage,
    },
    activity: {
      setAgentIdsWorking,
      messageIdToAgentIdsRef,
      sessionIdToAgentIdsRef,
    },
    feedback: { setError, messageApi, handleCompactCommand },
  });

  bindSendMessage(sendMessage);

  // ── handleExport ───────────────────────────────────────────
  const handleExport = useCallback(() => {
    if (turns.length === 0) {
      messageApi.info('无对话');
      return;
    }
    const md = turns
      .map((t) => {
        const blocks: string[] = [];
        if (t.userMessage.text.trim())
          blocks.push(
            `## User · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.userMessage.text}`,
          );
        const items = t.assistant.timelineItems ?? [];
        const thinking = items.filter((i) => i.type === 'thinking');
        const steps = items.filter((i) => i.type !== 'thinking');
        const rawThinking = getThinkingRawText(thinking);
        if (rawThinking) blocks.push(`## Reasoning\n\n${rawThinking}`);
        if (steps.length > 0) {
          const stepLines = steps
            .map((i) => {
              const detail = sanitizeProcessText(
                i.message || i.output || i.arguments,
                { maxLength: 240 },
              );
              return `- [${i.status || i.type}] ${detail || i.name || i.type}`;
            })
            .join('\n');
          if (stepLines) blocks.push(`## Steps\n\n${stepLines}`);
        }
        blocks.push(
          `## Agent · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.assistant.answerMarkdown}`,
        );
        return blocks.join('\n\n');
      })
      .join('\n\n---\n\n');
    const b = new Blob([md], { type: 'text/markdown;charset=utf-8' });
    const u = URL.createObjectURL(b);
    const a = document.createElement('a');
    a.href = u;
    a.download = `pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`;
    a.click();
    URL.revokeObjectURL(u);
  }, [turns, messageApi]);

  // ── onDeleteTurn / onToggleReasoning ──────────────────────
  const onDeleteTurn = useCallback((turnId: string) => {
    setTurns((p) => p.filter((t) => t.turnId !== turnId));
  }, []);

  const onToggleReasoning = useCallback((turnId: string, itemId: string) => {
    setTurns((prev) =>
      prev.map((t) =>
        t.turnId === turnId
          ? {
              ...t,
              assistant: {
                ...t.assistant,
                timelineItems: (t.assistant.timelineItems ?? []).map((item) =>
                  item.id === itemId
                    ? { ...item, collapsed: !item.collapsed }
                    : item,
                ),
              },
            }
          : t,
      ),
    );
  }, []);

  // ── derived ────────────────────────────────────────────────
  const groups = useMemo(
    () =>
      groupSessions(sessions).map((g) => ({
        ...g,
        items: g.items.map((s) => ({
          ...s,
          unreadCount: sessionUnreadCounts[s.sessionId] || undefined,
        })),
      })),
    [sessions, sessionUnreadCounts],
  );
  const tLimit = latestUsage?.contextWindowTokens ?? 0;
  const tUsed = latestUsage?.totalTokens ?? 0;
  const tPct =
    tLimit > 0 ? Math.min(100, Math.round((tUsed / tLimit) * 100)) : 0;

  const cacheTotalTokens = sessionCacheHitTokens + sessionCacheMissTokens;
  const cacheHitRate =
    cacheTotalTokens > 0
      ? Math.round((sessionCacheHitTokens / cacheTotalTokens) * 100)
      : undefined;
  const visibleSubAgentCards = useMemo(
    () => filterSubAgentCardsForSession(subAgentCards, selectedSessionId),
    [subAgentCards, selectedSessionId],
  );
  const visibleTurns = useMemo(
    () => mergeCompactionLifecycleTurns(turns),
    [mergeCompactionLifecycleTurns, turns],
  );

  return {
    workspaces,
    workspaceId,
    workspaceLoading,
    setWorkspaceId,
    setWorkspaces,
    agents,
    agentId,
    agentLoading,
    setAgentId,
    selectedAgent,
    sidebarOpen,
    setSidebarOpen,
    sessions,
    selectedSessionId,
    sessionsLoading,
    groups,
    turns: visibleTurns,
    chatInteractionRuntimeEvents,
    historyLoading,
    hasMoreMessages,
    loadingMore,
    inputValue,
    setInputValue,
    loading,
    workingAgentIds,
    interactionQueue,
    error,
    setError,
    latestUsage,
    tLimit,
    tUsed,
    tPct,
    mainSessionId,
    sessionCacheHitTokens,
    sessionCacheMissTokens,
    cacheHitRate,
    handleSetMainSession,
    subAgentCards: visibleSubAgentCards,
    sessionUnreadCounts,
    startWorkspaceNotificationStream,
    stopWorkspaceNotificationStream,
    clearSessionUnread,
    createSceneOpen,
    setCreateSceneOpen,
    createSceneLoading,
    createSceneForm,
    renameModalOpen,
    setRenameModalOpen,
    renameTitle,
    setRenameTitle,
    renameSessionId,
    handleSelectSession,
    handleDeleteSession,
    handleArchiveSession,
    handleRenameStart,
    handleRenameSubmit,
    ensureAgentMainSession,
    sendMessage,
    viewportScrollIntent,
    clearViewportScrollIntent,
    submitInteraction,
    enqueueInteraction,
    updateQueuedInteraction,
    deleteQueuedInteraction,
    sendQueuedInteractionNow,
    steerQueuedInteraction,
    handleKeyDown,
    loadMoreMessages,
    resetConversation,
    handleExport,
    onDeleteTurn,
    onToggleReasoning,
    messageListRef,
    listEndRef,
    abortRef,
    formatTime,
    getStepTone,
    assistantStatusLabel,
    getAgentName,
    stringToColor,
    wsOpts,
    agOpts,
    creatingSession,
    reconnectCountRef,
  };
}
