import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useRef } from 'react';
import {
  type AdminChatStreamEvent,
  type ContextCompactionResult,
  compactSession,
} from '@/services/platform/api';
import type { AssistantStatus, ChatTurn } from '../types';
import {
  COMPACTION_TURN_PREFIX,
  compactionTurnId,
  createId,
  formatCompactSuccessMessage,
  mergeHistoryWithLifecycleTurns,
} from '../utils/chatStateUtils';

interface CompactionIdentityPort {
  workspaceId?: string;
  agentId?: string;
  selectedSessionId: string | null;
  sessionIdRef: MutableRefObject<string | undefined>;
}

interface CompactionTurnsPort {
  turnsRef: MutableRefObject<ChatTurn[]>;
  latestTurnIdRef: MutableRefObject<string | null>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
}

interface CompactionStatusPort {
  loading: boolean;
  setLoading: Dispatch<SetStateAction<boolean>>;
  setError: Dispatch<SetStateAction<string | null>>;
}

interface UseCompactionOptions {
  identity: CompactionIdentityPort;
  turns: CompactionTurnsPort;
  status: CompactionStatusPort;
  messageApi: MessageInstance;
}

type CompactedSessionSwitch = (
  sessionId: string,
  title?: string | null,
) => void;

export interface CompactionLifecycleOptions {
  allowSessionSwitch?: boolean;
  notify?: boolean;
}

/**
 * Owns compaction lifecycle state and commands.
 *
 * The three ports are deliberately grouped by domain. The hook owns all
 * compaction indexes, while the composition layer remains the authority for
 * session identity, visible turns, and page-level status.
 */
export function useCompaction({
  identity,
  turns,
  status,
  messageApi,
}: UseCompactionOptions) {
  const { workspaceId, agentId, selectedSessionId, sessionIdRef } = identity;
  const { turnsRef, latestTurnIdRef, setTurns } = turns;
  const { loading, setLoading, setError } = status;
  const compactionTurnIdsRef = useRef<Map<string, string>>(new Map());
  const compactionLifecycleTurnsRef = useRef<Map<string, ChatTurn>>(new Map());
  const activeCompactionTurnIdRef = useRef<string | null>(null);
  const compactedSessionSwitchRef = useRef<CompactedSessionSwitch>(() => {});

  const formatCompactAnswer = useCallback(
    (result: ContextCompactionResult) => formatCompactSuccessMessage(result),
    [],
  );

  const appendCompactTurn = useCallback(
    (
      text: string,
      assistantStatus: AssistantStatus,
      result?: ContextCompactionResult,
      stableTurnId?: string,
      placeAtStart = false,
    ) => {
      const now = Date.now();
      const turnId = stableTurnId ?? createId();
      const existing = turnsRef.current.find((turn) => turn.turnId === turnId);
      if (existing) {
        if (existing.turnId.startsWith(COMPACTION_TURN_PREFIX)) {
          compactionLifecycleTurnsRef.current.set(existing.turnId, existing);
        }
        return existing.turnId;
      }

      const compactTurn: ChatTurn = {
        turnId,
        userMessage: {
          id: createId(),
          text: '',
          timestamp: now,
          status: 'success',
        },
        assistant: {
          id: createId(),
          status: assistantStatus,
          timelineItems: [
            {
              id: createId(),
              type: 'subconscious_step',
              status:
                assistantStatus === 'error'
                  ? 'error'
                  : assistantStatus === 'success'
                    ? 'success'
                    : 'compacting',
              message: text,
              timestamp: now,
              collapsed: false,
            },
          ],
          answerMarkdown: result ? formatCompactAnswer(result) : text,
          isStreaming:
            assistantStatus === 'executing' || assistantStatus === 'thinking',
          renderMode: 'structured',
        },
      };
      const nextTurns = placeAtStart
        ? [compactTurn, ...turnsRef.current]
        : [...turnsRef.current, compactTurn];
      if (compactTurn.turnId.startsWith(COMPACTION_TURN_PREFIX)) {
        compactionLifecycleTurnsRef.current.set(
          compactTurn.turnId,
          compactTurn,
        );
      }
      turnsRef.current = nextTurns;
      setTurns(nextTurns);
      return turnId;
    },
    [formatCompactAnswer, setTurns, turnsRef],
  );

  const updateCompactTurn = useCallback(
    (
      turnId: string,
      assistantStatus: AssistantStatus,
      message: string,
      result?: ContextCompactionResult,
    ) => {
      const nextTurns = turnsRef.current.map((turn) => {
        if (turn.turnId !== turnId) return turn;
        const items = turn.assistant.timelineItems ?? [];
        const itemStatus =
          assistantStatus === 'error'
            ? 'error'
            : assistantStatus === 'success'
              ? 'success'
              : 'compacting';
        const compactItemIndex = items.findIndex(
          (item) => item.type === 'subconscious_step',
        );
        const nextItem = {
          id: compactItemIndex >= 0 ? items[compactItemIndex].id : createId(),
          type: 'subconscious_step' as const,
          status: itemStatus,
          message,
          timestamp: Date.now(),
          collapsed: false,
        };
        const nextItems =
          compactItemIndex >= 0
            ? items.map((item, index) =>
                index === compactItemIndex ? nextItem : item,
              )
            : [...items, nextItem];
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: assistantStatus,
            isStreaming:
              assistantStatus === 'executing' || assistantStatus === 'thinking',
            renderMode: 'structured' as const,
            answerMarkdown: result ? formatCompactAnswer(result) : message,
            timelineItems: nextItems,
          },
        };
      });
      const updatedLifecycleTurn = nextTurns.find(
        (turn) =>
          turn.turnId === turnId &&
          turn.turnId.startsWith(COMPACTION_TURN_PREFIX),
      );
      if (updatedLifecycleTurn) {
        compactionLifecycleTurnsRef.current.set(turnId, updatedLifecycleTurn);
      }
      turnsRef.current = nextTurns;
      setTurns(nextTurns);
    },
    [formatCompactAnswer, setTurns, turnsRef],
  );

  const handleCompactionLifecycleEvent = useCallback(
    (event: AdminChatStreamEvent, options?: CompactionLifecycleOptions) => {
      const raw = event as Record<string, unknown>;
      const compactionId =
        typeof raw.compactionId === 'string' && raw.compactionId
          ? raw.compactionId
          : 'unidentified-compaction';
      let compactTurnId =
        compactionTurnIdsRef.current.get(compactionId) ??
        activeCompactionTurnIdRef.current ??
        compactionTurnId(compactionId);
      const eventConversationId =
        typeof raw.conversationId === 'string' ? raw.conversationId : null;
      const sourceSessionId =
        typeof raw.sourceSessionId === 'string' ? raw.sourceSessionId : null;
      const placeAtStart =
        event.type === 'context.compaction.completed' &&
        eventConversationId !== null &&
        sourceSessionId !== null &&
        eventConversationId !== sourceSessionId;

      if (!turnsRef.current.some((turn) => turn.turnId === compactTurnId)) {
        compactTurnId = appendCompactTurn(
          event.type === 'context.compaction.failed'
            ? String(raw.error || '上下文压缩失败')
            : '正在压缩上下文…',
          event.type === 'context.compaction.failed' ? 'error' : 'executing',
          undefined,
          compactTurnId,
          placeAtStart,
        );
      }
      compactionTurnIdsRef.current.set(compactionId, compactTurnId);
      activeCompactionTurnIdRef.current = compactTurnId;

      if (event.type === 'context.compaction.started') {
        setLoading(true);
        updateCompactTurn(compactTurnId, 'executing', '正在压缩上下文…');
        if (options?.notify !== false) {
          messageApi.loading({
            content: '正在压缩上下文…',
            key: 'compaction-status',
            duration: 0,
          });
        }
        return;
      }

      setLoading(false);
      if (options?.notify !== false) messageApi.destroy('compaction-status');
      if (event.type === 'context.compaction.failed') {
        const errorMessage = String(raw.error || '上下文压缩失败');
        updateCompactTurn(compactTurnId, 'error', errorMessage);
        activeCompactionTurnIdRef.current = null;
        if (options?.notify !== false) messageApi.error(errorMessage, 4);
        return;
      }

      const compacted =
        raw.compaction && typeof raw.compaction === 'object'
          ? (raw.compaction as ContextCompactionResult)
          : undefined;
      updateCompactTurn(compactTurnId, 'success', '上下文压缩完成', compacted);
      activeCompactionTurnIdRef.current = null;

      const newSessionId =
        typeof raw.newSessionId === 'string' ? raw.newSessionId : null;
      const newSessionTitle =
        typeof raw.newSessionTitle === 'string'
          ? raw.newSessionTitle
          : '新会话';
      if (
        options?.allowSessionSwitch !== false &&
        newSessionId &&
        sessionIdRef.current !== newSessionId
      ) {
        messageApi.success(`上下文压缩完成，已切换到「${newSessionTitle}」`, 4);
        compactedSessionSwitchRef.current(newSessionId, newSessionTitle);
      }
    },
    [
      appendCompactTurn,
      messageApi,
      sessionIdRef,
      setLoading,
      turnsRef,
      updateCompactTurn,
    ],
  );

  const handleCompactCommand = useCallback(async () => {
    const currentSessionId = sessionIdRef.current ?? selectedSessionId;
    if (!currentSessionId || !workspaceId) {
      messageApi.info('当前没有可压缩的会话');
      return;
    }
    if (loading) {
      messageApi.info('当前会话正在执行，请稍后再压缩');
      return;
    }

    setError(null);
    setLoading(true);
    const compactionId = createId();
    const compactTurnId = appendCompactTurn(
      '正在压缩上下文…',
      'executing',
      undefined,
      compactionTurnId(compactionId),
    );
    compactionTurnIdsRef.current.set(compactionId, compactTurnId);
    activeCompactionTurnIdRef.current = compactTurnId;
    latestTurnIdRef.current = compactTurnId;
    try {
      const response = await compactSession(currentSessionId, {
        workspaceId,
        agentId,
        level: 'Full',
        reason: 'manual slash command',
        compactionId,
      });
      setLoading(false);
      const responseTurnId =
        compactionTurnIdsRef.current.get(response.compactionId) ??
        compactTurnId;
      updateCompactTurn(
        responseTurnId,
        'success',
        '上下文压缩完成',
        response.compaction,
      );
      activeCompactionTurnIdRef.current = null;

      if (
        response.newSessionId &&
        sessionIdRef.current !== response.newSessionId
      ) {
        compactedSessionSwitchRef.current(
          response.newSessionId,
          response.newSessionTitle,
        );
      }
    } catch (error: unknown) {
      setLoading(false);
      const message = error instanceof Error ? error.message : '上下文压缩失败';
      setError(message);
      updateCompactTurn(compactTurnId, 'error', message);
      activeCompactionTurnIdRef.current = null;
      messageApi.error(message);
    }
  }, [
    agentId,
    appendCompactTurn,
    latestTurnIdRef,
    loading,
    messageApi,
    selectedSessionId,
    sessionIdRef,
    setError,
    setLoading,
    updateCompactTurn,
    workspaceId,
  ]);

  const resetCompaction = useCallback(() => {
    compactionTurnIdsRef.current.clear();
    compactionLifecycleTurnsRef.current.clear();
    activeCompactionTurnIdRef.current = null;
  }, []);

  const mergeCompactionLifecycleTurns = useCallback(
    (baseTurns: ChatTurn[]) =>
      mergeHistoryWithLifecycleTurns(
        baseTurns,
        Array.from(compactionLifecycleTurnsRef.current.values()),
      ),
    [],
  );

  const bindCompactedSessionSwitch = useCallback(
    (handler: CompactedSessionSwitch) => {
      compactedSessionSwitchRef.current = handler;
    },
    [],
  );

  return {
    handleCompactionLifecycleEvent,
    handleCompactCommand,
    resetCompaction,
    mergeCompactionLifecycleTurns,
    bindCompactedSessionSwitch,
  };
}
