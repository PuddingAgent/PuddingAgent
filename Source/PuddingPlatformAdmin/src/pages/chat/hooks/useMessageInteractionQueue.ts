import type { MessageInstance } from 'antd/es/message/interface';
import type { KeyboardEvent, MutableRefObject } from 'react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  createChatSteeringMessage,
  getAgentMessageQueue,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/debug';
import type { ChatTurn } from '../types';
import {
  type ChatInteractionQueueItem,
  type ChatSendOptions,
  STEERING_INJECTED_QUEUE_RETENTION_MS,
} from '../types/chatStateTypes';
import {
  COMPACT_COMMAND,
  hasBlockingActiveTurn,
  removeInjectedSteeringQueueItem,
  toChatInteractionQueueItem,
} from '../utils/chatStateUtils';

type SendMessage = (text: string, options?: ChatSendOptions) => Promise<void>;

interface MessageQueueIdentityPort {
  workspaceId?: string;
  agentId?: string;
  selectedSessionId: string | null;
  sessionIdRef: MutableRefObject<string | undefined>;
}

interface MessageQueueExecutionPort {
  loading: boolean;
  turns: ChatTurn[];
  turnsRef: MutableRefObject<ChatTurn[]>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  handleCompactCommand: () => Promise<void>;
}

interface UseMessageInteractionQueueOptions {
  identity: MessageQueueIdentityPort;
  execution: MessageQueueExecutionPort;
  messageApi: MessageInstance;
}

/**
 * Owns the composer and backend-owned interaction queue projection.
 * Sending remains a bound command so this domain does not depend on SSE internals.
 */
export function useMessageInteractionQueue({
  identity,
  execution,
  messageApi,
}: UseMessageInteractionQueueOptions) {
  const { workspaceId, agentId, selectedSessionId, sessionIdRef } = identity;
  const {
    loading,
    turns,
    turnsRef,
    activeMessageIdsRef,
    messageIdToTurnIdRef,
    handleCompactCommand,
  } = execution;
  const [inputValue, setInputValue] = useState('');
  const [serverInteractionQueue, setServerInteractionQueue] = useState<
    ChatInteractionQueueItem[]
  >([]);
  const [steeringInteractionQueue, setSteeringInteractionQueue] = useState<
    ChatInteractionQueueItem[]
  >([]);
  const sendMessageRef = useRef<SendMessage>(async () => {});
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;
  const steeringInjectedDismissTimersRef = useRef<Map<string, number>>(
    new Map(),
  );

  const bindSendMessage = useCallback((handler: SendMessage) => {
    sendMessageRef.current = handler;
  }, []);

  const clearInjectedSteeringDismissTimer = useCallback(
    (steeringId: string) => {
      const timer = steeringInjectedDismissTimersRef.current.get(steeringId);
      if (timer != null) {
        window.clearTimeout(timer);
        steeringInjectedDismissTimersRef.current.delete(steeringId);
      }
    },
    [],
  );

  const clearInjectedSteeringDismissTimers = useCallback(() => {
    steeringInjectedDismissTimersRef.current.forEach((timer) => {
      window.clearTimeout(timer);
    });
    steeringInjectedDismissTimersRef.current.clear();
  }, []);

  const scheduleInjectedSteeringDismiss = useCallback(
    (steeringId: string) => {
      clearInjectedSteeringDismissTimer(steeringId);
      const timer = window.setTimeout(() => {
        steeringInjectedDismissTimersRef.current.delete(steeringId);
        setSteeringInteractionQueue((previous) =>
          removeInjectedSteeringQueueItem(previous, steeringId),
        );
        recordPerfEvent('chat.steering.dismissed', {
          steeringId,
          reason: 'injected-retention-elapsed',
          retentionMs: STEERING_INJECTED_QUEUE_RETENTION_MS,
        });
      }, STEERING_INJECTED_QUEUE_RETENTION_MS);
      steeringInjectedDismissTimersRef.current.set(steeringId, timer);
    },
    [clearInjectedSteeringDismissTimer],
  );

  const markSteeringInjected = useCallback(
    (event: {
      steeringId: string;
      injectedAt: number;
      injectedRound?: number;
      sessionId?: string;
      agentId?: string;
      messageChars?: number;
    }) => {
      setSteeringInteractionQueue((previous) =>
        previous.map((item) =>
          item.steeringId !== event.steeringId
            ? item
            : {
                ...item,
                status: 'steering_injected',
                injectedAt: event.injectedAt,
                injectedRound: event.injectedRound,
                injectionLatencyMs: item.submittedAt
                  ? Math.max(0, event.injectedAt - item.submittedAt)
                  : undefined,
              },
        ),
      );
      recordPerfEvent('chat.steering.injected', event);
      scheduleInjectedSteeringDismiss(event.steeringId);
    },
    [scheduleInjectedSteeringDismiss],
  );

  useEffect(
    () => () => {
      clearInjectedSteeringDismissTimers();
    },
    [clearInjectedSteeringDismissTimers],
  );

  const enqueueInteraction = useCallback(
    (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return null;
      recordPerfEvent(
        'chat.queue.localEnqueueIgnored',
        {
          reason: 'backend-owned-queue',
          messageChars: trimmed.length,
          hasMetadata: Boolean(options?.metadata),
        },
        { throttleMs: 1_000 },
      );
      void sendMessageRef.current(trimmed, options);
      return null;
    },
    [],
  );

  const submitInteraction = useCallback(
    async (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return;
      const localBusy =
        loading ||
        hasBlockingActiveTurn(
          turnsRef.current,
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
        );
      if (localBusy) {
        recordPerfEvent(
          'chat.submit.localBusyForwarded',
          {
            loading,
            activeMessageCount: activeMessageIdsRef.current.size,
            turnCount: turnsRef.current.length,
            messageChars: trimmed.length,
          },
          { throttleMs: 1_000 },
        );
      }
      await sendMessageRef.current(trimmed, options);
    },
    [activeMessageIdsRef, loading, messageIdToTurnIdRef, turnsRef],
  );

  const updateQueuedInteraction = useCallback(
    (id: string, text: string) => {
      recordPerfEvent(
        'chat.queue.localUpdateIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          messageChars: text.trim().length,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端管理，当前暂不支持本地编辑队列项');
    },
    [messageApi],
  );

  const refreshAgentMessageQueue = useCallback(
    async (reason: string) => {
      if (!workspaceId || !agentId) {
        setServerInteractionQueue([]);
        return;
      }
      const startedAt = performance.now();
      try {
        const snapshot = await getAgentMessageQueue(workspaceId, agentId, {
          limit: 20,
          includeTerminal: false,
        });
        const next = (snapshot.items ?? []).map(toChatInteractionQueueItem);
        setServerInteractionQueue(next);
        recordPerfEvent(
          'chat.queue.snapshot',
          {
            reason,
            workspaceId,
            agentId,
            itemCount: next.length,
            elapsedMs: Math.round(performance.now() - startedAt),
          },
          { throttleMs: 2_000 },
        );
      } catch (error) {
        recordPerfEvent(
          'chat.queue.snapshotFailed',
          {
            reason,
            workspaceId,
            agentId,
            error: error instanceof Error ? error.message : String(error),
            elapsedMs: Math.round(performance.now() - startedAt),
          },
          { throttleMs: 2_000 },
        );
      }
    },
    [agentId, workspaceId],
  );

  useEffect(() => {
    if (!workspaceId || !agentId) {
      setServerInteractionQueue([]);
      return;
    }
    void refreshAgentMessageQueue('selection');
    const timer = window.setInterval(
      () => {
        void refreshAgentMessageQueue('poll');
      },
      loading ? 1200 : 3500,
    );
    return () => window.clearInterval(timer);
  }, [agentId, loading, refreshAgentMessageQueue, workspaceId]);

  useEffect(() => {
    if (!workspaceId || !agentId) setSteeringInteractionQueue([]);
  }, [agentId, workspaceId]);

  const visibleInteractionQueue = useMemo(
    () => [...serverInteractionQueue, ...steeringInteractionQueue],
    [serverInteractionQueue, steeringInteractionQueue],
  );

  const findVisibleQueueItem = useCallback(
    (id: string) => visibleInteractionQueue.find((item) => item.id === id),
    [visibleInteractionQueue],
  );

  const deleteQueuedInteraction = useCallback(
    (id: string) => {
      const item = findVisibleQueueItem(id);
      if (item?.source === 'steering') {
        setSteeringInteractionQueue((previous) =>
          previous.filter((candidate) => candidate.id !== id),
        );
        return;
      }
      recordPerfEvent(
        'chat.queue.localDeleteIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          status: item?.status,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端管理，当前暂不支持本地删除队列项');
    },
    [findVisibleQueueItem, messageApi],
  );

  const sendQueuedInteractionNow = useCallback(
    async (id: string) => {
      const item = findVisibleQueueItem(id);
      recordPerfEvent(
        'chat.queue.sendNowIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          status: item?.status,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端调度，插队/立即发送需要后端队列命令接口');
    },
    [findVisibleQueueItem, messageApi],
  );

  const steerQueuedInteraction = useCallback(
    async (id: string) => {
      const item = findVisibleQueueItem(id);
      const sessionId = sessionIdRef.current ?? selectedSessionId;
      if (!item || item.status !== 'queued') return;
      if (!workspaceId || !sessionId) {
        messageApi.error('当前会话尚未建立，无法注入引导');
        return;
      }

      const submittedStartAt = Date.now();
      const localSteeringId = `steering-local-${id}`;
      setSteeringInteractionQueue((previous) => [
        ...previous.filter((candidate) => candidate.id !== localSteeringId),
        {
          id: localSteeringId,
          text: item.text,
          createdAt: submittedStartAt,
          status: 'steering_pending',
          source: 'steering',
          submittedAt: submittedStartAt,
          error: undefined,
        },
      ]);
      recordPerfEvent('chat.steering.submit', {
        queueItemId: item.id,
        sessionId,
        agentId,
        messageChars: item.text.length,
        queueAgeMs: Math.max(0, submittedStartAt - item.createdAt),
      });

      try {
        const response = await createChatSteeringMessage(
          workspaceId,
          sessionId,
          {
            messageText: item.text,
            agentId,
            sourceQueueItemId: item.id,
            priority: 1000,
          },
        );
        setSteeringInteractionQueue((previous) =>
          previous.map((candidate) =>
            candidate.id === localSteeringId
              ? {
                  ...candidate,
                  status: 'steering_pending',
                  steeringId: response.steeringId,
                  submittedAt: response.createdAt,
                }
              : candidate,
          ),
        );
        recordPerfEvent('chat.steering.submitted', {
          queueItemId: item.id,
          steeringId: response.steeringId,
          sessionId: response.sessionId,
          workspaceId: response.workspaceId,
          agentId: response.agentId,
          createdAt: response.createdAt,
          requestLatencyMs: Math.max(0, Date.now() - submittedStartAt),
        });
        messageApi.success('引导已提交，将在下一次模型请求前注入上下文');
      } catch (error: unknown) {
        const errorMessage =
          error instanceof Error ? error.message : '引导提交失败';
        setSteeringInteractionQueue((previous) =>
          previous.map((candidate) =>
            candidate.id === localSteeringId
              ? {
                  ...candidate,
                  status: 'steering_failed',
                  error: errorMessage,
                }
              : candidate,
          ),
        );
        recordPerfEvent('chat.steering.submitFailed', {
          queueItemId: item.id,
          sessionId,
          agentId,
          requestLatencyMs: Math.max(0, Date.now() - submittedStartAt),
          error: errorMessage,
        });
        messageApi.error(errorMessage);
      }
    },
    [
      agentId,
      findVisibleQueueItem,
      messageApi,
      selectedSessionId,
      sessionIdRef,
      workspaceId,
    ],
  );

  const handleKeyDown = useCallback(
    (event: KeyboardEvent<HTMLTextAreaElement>) => {
      const value = inputValueRef.current;
      if (
        event.key === 'Enter' &&
        (event.ctrlKey || event.metaKey || !event.shiftKey)
      ) {
        event.preventDefault();
        const trimmed = value.trim();
        if (!trimmed) return;
        setInputValue('');
        if (trimmed.toLowerCase() === COMPACT_COMMAND) {
          void handleCompactCommand();
        } else {
          void submitInteraction(trimmed);
        }
        return;
      }
      if (event.key === 'ArrowUp' && !value.trim()) {
        const lastTurn = turns[turns.length - 1];
        if (lastTurn?.userMessage?.text) {
          event.preventDefault();
          setInputValue(lastTurn.userMessage.text);
        }
      }
    },
    [handleCompactCommand, submitInteraction, turns],
  );

  useEffect(() => {
    const handler = () => {
      const text = inputValueRef.current.trim();
      if (!text) return;
      setInputValue('');
      void submitInteraction(text);
    };
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, [submitInteraction]);

  return {
    inputValue,
    setInputValue,
    interactionQueue: visibleInteractionQueue,
    enqueueInteraction,
    submitInteraction,
    updateQueuedInteraction,
    deleteQueuedInteraction,
    sendQueuedInteractionNow,
    steerQueuedInteraction,
    handleKeyDown,
    markSteeringInjected,
    bindSendMessage,
  };
}
