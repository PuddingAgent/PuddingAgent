import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useEffect, useRef } from 'react';
import {
  listSessionMessages,
  type MessageListResponse,
} from '@/services/platform/api';
import { recordPerfStep } from '@/utils/debug';
import type { ChatTurn, SessionListItem } from '../types';
import type { ChatRouteSelection } from '../types/chatStateTypes';
import { MESSAGE_PAGE_SIZE } from '../types/chatStateTypes';
import { logChatDiag } from '../utils/chatDiagnostics';
import {
  isActiveAssistantTurn,
  shouldReplayEventsAfterHistory,
} from '../utils/chatStateUtils';
import type { ScrollIntent } from '../viewport/types';

interface SessionSelectionCatalogPort {
  workspaceId?: string;
  agentId?: string;
  routeSelection: ChatRouteSelection;
  sessions: SessionListItem[];
  selectedSessionId: string | null;
  mainSessionId: string | null;
  setSelectedSessionId: Dispatch<SetStateAction<string | null>>;
  sessionIdRef: MutableRefObject<string | undefined>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  forceNewSessionRef: MutableRefObject<boolean>;
}

interface SessionSelectionTurnsPort {
  turns: ChatTurn[];
  turnsRef: MutableRefObject<ChatTurn[]>;
  latestTurnIdRef: MutableRefObject<string | null>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  setLoading: Dispatch<SetStateAction<boolean>>;
  reconcileWorkingAgents: (
    sessionId: string,
    agentIds: string[],
    isWorking: boolean,
  ) => void;
}

interface SessionSelectionHistoryPort {
  toTurnsFromHistory: (response: MessageListResponse) => ChatTurn[];
  setHistoryLoading: Dispatch<SetStateAction<boolean>>;
  setHasMoreMessages: Dispatch<SetStateAction<boolean>>;
  setOldestMessageCursor: Dispatch<SetStateAction<number | null>>;
  resetHistoryPagination: () => void;
}

interface SessionSelectionStreamPort {
  stop: () => void;
  start: (sessionId: string) => void;
  replayLatestTurn: (
    sessionId: string,
    turns: ChatTurn[],
    signal?: AbortSignal,
  ) => Promise<void>;
  syncCompletedCursor: (
    sessionId: string,
    signal?: AbortSignal,
  ) => Promise<void>;
  projectionOwnedSessionIdsRef: MutableRefObject<Set<string>>;
  lastSequenceNumRef: MutableRefObject<number>;
}

interface SessionSelectionLifecyclePort {
  clearSessionUnread: (sessionId: string) => void;
  clearRuntimeEvents: () => void;
  resetCompaction: () => void;
}

interface UseSessionSelectionOptions {
  catalog: SessionSelectionCatalogPort;
  turns: SessionSelectionTurnsPort;
  history: SessionSelectionHistoryPort;
  stream: SessionSelectionStreamPort;
  lifecycle: SessionSelectionLifecyclePort;
  setViewportScrollIntent: Dispatch<SetStateAction<ScrollIntent>>;
  messageApi: MessageInstance;
  onManualSwitch?: () => void;
}

/** Owns the atomic session-selection transaction and route-driven selection. */
export function useSessionSelection({
  catalog,
  turns,
  history,
  stream,
  lifecycle,
  setViewportScrollIntent,
  messageApi,
  onManualSwitch,
}: UseSessionSelectionOptions) {
  const historyAbortRef = useRef<AbortController | null>(null);
  const {
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
  } = catalog;
  const {
    turns: currentTurns,
    turnsRef,
    latestTurnIdRef,
    messageIdToTurnIdRef,
    setTurns,
    setLoading,
    reconcileWorkingAgents,
  } = turns;
  const {
    toTurnsFromHistory,
    setHistoryLoading,
    setHasMoreMessages,
    setOldestMessageCursor,
    resetHistoryPagination,
  } = history;
  const {
    stop,
    start,
    replayLatestTurn,
    syncCompletedCursor,
    projectionOwnedSessionIdsRef,
    lastSequenceNumRef,
  } = stream;
  const { clearSessionUnread, clearRuntimeEvents, resetCompaction } = lifecycle;

  const handleSelectSession = useCallback(
    async (sessionId: string, options?: { agentId?: string }) => {
      // RC-6: Mark manual switch to prevent compaction from overriding
      onManualSwitch?.();

      if (sessionId === selectedSessionId && currentTurns.length > 0) {
        logChatDiag('session.select.noop', {
          sessionId,
          selectedSessionId,
          sessionIdRef: sessionIdRef.current,
          turnCount: currentTurns.length,
        });
        return currentTurns.length;
      }

      const traceId = `session-select-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      const selectStartedAt = performance.now();
      const sessionAgentId =
        options?.agentId ??
        sessions.find((session) => session.sessionId === sessionId)
          ?.principalId ??
        agentId;
      const shouldRestartSameSessionStream = sessionId === selectedSessionId;
      logChatDiag('session.select.start', {
        traceId,
        sessionId,
        previousSelectedSessionId: selectedSessionId,
        previousSessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSelection.sessionId,
        sessionAgentId,
        previousTurnCount: turnsRef.current.length,
        restartStream: shouldRestartSameSessionStream,
      });

      clearSessionUnread(sessionId);
      historyAbortRef.current?.abort();
      stop();
      projectionOwnedSessionIdsRef.current.delete(sessionId);
      setSelectedSessionId(sessionId);
      sessionIdRef.current = sessionId;
      forceNewSessionRef.current = false;
      lastSequenceNumRef.current = 0;
      messageIdToTurnIdRef.current.clear();
      resetCompaction();
      latestTurnIdRef.current = null;
      turnsRef.current = [];
      setTurns([]);
      clearRuntimeEvents();
      resetHistoryPagination();
      setHistoryLoading(true);
      recordPerfStep('session.select', 'state.reset', selectStartedAt, {
        traceId,
        sessionId,
        agentId: sessionAgentId,
        previousSessionId: selectedSessionId,
        restartStream: shouldRestartSameSessionStream,
      });

      const controller = new AbortController();
      historyAbortRef.current = controller;
      try {
        const listStartedAt = performance.now();
        const response = await listSessionMessages(
          sessionId,
          undefined,
          MESSAGE_PAGE_SIZE,
        );
        recordPerfStep(
          'session.select',
          'api.listSessionMessages',
          listStartedAt,
          {
            traceId,
            sessionId,
            agentId: sessionAgentId,
            messageCount: response.items?.length ?? 0,
            hasMore: response.hasMore,
            oldestCreatedAt: response.oldestCreatedAt ?? null,
          },
        );
        if (controller.signal.aborted) return;

        const mapStartedAt = performance.now();
        const loadedTurns = toTurnsFromHistory(response);
        logChatDiag('session.select.history.loaded', {
          traceId,
          sessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          messageCount: response.items?.length ?? 0,
          loadedTurns: loadedTurns.length,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
          loadedLatestStatus:
            loadedTurns[loadedTurns.length - 1]?.assistant.status,
        });
        recordPerfStep('session.select', 'history.toTurns', mapStartedAt, {
          traceId,
          sessionId,
          agentId: sessionAgentId,
          turnCount: loadedTurns.length,
          activeTurnCount: loadedTurns.filter(isActiveAssistantTurn).length,
        });

        const activeCount = turnsRef.current.filter(
          isActiveAssistantTurn,
        ).length;
        if (activeCount > 0) {
          logChatDiag('session.select.preserveActiveTurns', {
            traceId,
            sessionId,
            activeCount,
            loadedTurns: loadedTurns.length,
            currentLatestUser:
              turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
            loadedLatestUser:
              loadedTurns[loadedTurns.length - 1]?.userMessage.text,
          });
          await syncCompletedCursor(sessionId, controller.signal);
          return;
        }

        turnsRef.current = loadedTurns;
        setTurns(loadedTurns);
        latestTurnIdRef.current =
          loadedTurns[loadedTurns.length - 1]?.turnId ?? null;
        const isWorking = loadedTurns.some(isActiveAssistantTurn);
        reconcileWorkingAgents(
          sessionId,
          sessionAgentId ? [sessionAgentId] : [],
          isWorking,
        );
        setLoading(isWorking);
        setHasMoreMessages(response.hasMore);
        if (response.oldestCreatedAt != null) {
          setOldestMessageCursor(response.oldestCreatedAt);
        }

        if (shouldReplayEventsAfterHistory(loadedTurns)) {
          const replayStartedAt = performance.now();
          await replayLatestTurn(sessionId, loadedTurns, controller.signal);
          recordPerfStep(
            'session.select',
            'events.replayLatestTurn',
            replayStartedAt,
            {
              traceId,
              sessionId,
              agentId: sessionAgentId,
              turnCount: loadedTurns.length,
            },
          );
        } else {
          const cursorStartedAt = performance.now();
          await syncCompletedCursor(sessionId, controller.signal);
          recordPerfStep(
            'session.select',
            'events.syncCompletedCursor',
            cursorStartedAt,
            {
              traceId,
              sessionId,
              agentId: sessionAgentId,
              turnCount: loadedTurns.length,
            },
          );
        }

        recordPerfStep('session.select', 'select.finish', selectStartedAt, {
          traceId,
          sessionId,
          agentId: sessionAgentId,
          turnCount: turnsRef.current.length,
        });
        logChatDiag('session.select.finish', {
          traceId,
          sessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          turnCount: turnsRef.current.length,
          latestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          latestStatus:
            turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
        });
        setViewportScrollIntent({ type: 'manual-bottom', behavior: 'auto' });
        return turnsRef.current.length;
      } catch (error) {
        logChatDiag('session.select.error', {
          traceId,
          sessionId,
          aborted: controller.signal.aborted,
          error,
        });
        recordPerfStep('session.select', 'select.error', selectStartedAt, {
          traceId,
          sessionId,
          agentId: sessionAgentId,
          status: controller.signal.aborted ? 'aborted' : 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (!controller.signal.aborted) messageApi.error('加载历史消息失败');
        return undefined;
      } finally {
        if (historyAbortRef.current === controller) {
          historyAbortRef.current = null;
          setHistoryLoading(false);
        }
        if (
          shouldRestartSameSessionStream &&
          !controller.signal.aborted &&
          sessionIdRef.current === sessionId
        ) {
          start(sessionId);
        }
      }
    },
    [
      agentId,
      clearRuntimeEvents,
      clearSessionUnread,
      currentTurns.length,
      forceNewSessionRef,
      lastSequenceNumRef,
      latestTurnIdRef,
      mainSessionId,
      messageApi,
      messageIdToTurnIdRef,
      projectionOwnedSessionIdsRef,
      reconcileWorkingAgents,
      replayLatestTurn,
      resetCompaction,
      resetHistoryPagination,
      routeSelection.sessionId,
      selectedSessionId,
      selectedSessionIdRef,
      sessionIdRef,
      sessions,
      setHasMoreMessages,
      setHistoryLoading,
      setLoading,
      setOldestMessageCursor,
      setSelectedSessionId,
      setTurns,
      setViewportScrollIntent,
      start,
      stop,
      syncCompletedCursor,
      toTurnsFromHistory,
      turnsRef,
      onManualSwitch,
    ],
  );

  useEffect(() => {
    const requestedSessionId = routeSelection.sessionId;
    if (
      !requestedSessionId ||
      !workspaceId ||
      selectedSessionId === requestedSessionId
    ) {
      return;
    }
    if (
      routeSelection.agentId &&
      agentId &&
      routeSelection.agentId !== agentId
    ) {
      return;
    }
    if (
      sessions.length > 0 &&
      !sessions.some((session) => session.sessionId === requestedSessionId)
    ) {
      return;
    }
    void handleSelectSession(requestedSessionId, {
      agentId: routeSelection.agentId ?? agentId,
    });
  }, [
    agentId,
    handleSelectSession,
    routeSelection.agentId,
    routeSelection.sessionId,
    selectedSessionId,
    sessions,
    workspaceId,
  ]);

  return { handleSelectSession };
}
