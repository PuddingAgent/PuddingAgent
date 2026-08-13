import type { MessageInstance } from 'antd/es/message/interface';
import type { KeyboardEvent, MutableRefObject } from 'react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  createChatSteeringMessage,
  getAgentMessageQueue,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/perfEventRuntime';
import type { ChatTurn } from '../types';
import {
  type ChatInteractionQueueItem,
  type ChatSendOptions,
  STEERING_INJECTED_QUEUE_RETENTION_MS,
} from '../types/chatStateTypes';
import {
  COMPACT_COMMAND,
  createId,
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
  /** P1#6：本地待发队列 — 用户 busy 期间输入的多条消息自动排队（可重排/删除/让位/取消） */
  const [pendingSendQueue, setPendingSendQueue] = useState<
    ChatInteractionQueueItem[]
  >([]);
  const pendingSendQueueRef = useRef<ChatInteractionQueueItem[]>([]);
  /** P0#9 后端队列快照指纹：相同快照短路，避免高频轮询触发无谓 React commit */
  const serverQueueSnapshotKeyRef = useRef<string>('');
  const sendMessageRef = useRef<SendMessage>(async () => {});
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;
  const steeringInjectedDismissTimersRef = useRef<Map<string, number>>(
    new Map(),
  );
  /** P1#6：取消全部时中止当前在途请求（由 useChatState 绑定 abortRef） */
  const cancelAllRef = useRef<() => void>(() => {});
  /** 本地待发队列排空锁，防止同一空闲窗口重复发送 */
  const drainLockRef = useRef(false);

  const bindSendMessage = useCallback((handler: SendMessage) => {
    sendMessageRef.current = handler;
  }, []);

  const bindCancelAll = useCallback((handler: () => void) => {
    cancelAllRef.current = handler;
  }, []);

  const updateLocalQueue = useCallback(
    (
      updater: (
        previous: ChatInteractionQueueItem[],
      ) => ChatInteractionQueueItem[],
    ) => {
      setPendingSendQueue((previous) => {
        const next = updater(previous);
        pendingSendQueueRef.current = next;
        return next;
      });
    },
    [],
  );

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

  /**
   * P1#6 三态消息队列核心分发：
   * - busy（loading 或存在阻塞中的活跃 turn）→ 本地自动排队（queue），返回本地项 id
   * - 空闲 → 立即发送（immediate）
   */
  const dispatchInteraction = useCallback(
    (trimmed: string, options?: ChatSendOptions): string | null => {
      const localBusy =
        loading ||
        hasBlockingActiveTurn(
          turnsRef.current,
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
        );
      if (localBusy) {
        const id = `local-${createId()}`;
        updateLocalQueue((previous) => [
          ...previous,
          {
            id,
            text: trimmed,
            createdAt: Date.now(),
            status: 'queued',
            source: 'local_pending',
            metadata: options?.metadata,
          },
        ]);
        recordPerfEvent(
          'chat.queue.autoQueued',
          {
            queueSize: pendingSendQueueRef.current.length + 1,
            loading,
            activeMessageCount: activeMessageIdsRef.current.size,
            messageChars: trimmed.length,
          },
          { throttleMs: 1_000 },
        );
        return id;
      }
      recordPerfEvent(
        'chat.queue.dispatch',
        {
          mode: 'immediate',
          messageChars: trimmed.length,
          hasMetadata: Boolean(options?.metadata),
        },
        { throttleMs: 1_000 },
      );
      void sendMessageRef.current(trimmed, options);
      return null;
    },
    [
      activeMessageIdsRef,
      loading,
      messageIdToTurnIdRef,
      turnsRef,
      updateLocalQueue,
    ],
  );

  const enqueueInteraction = useCallback(
    (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return null;
      return dispatchInteraction(trimmed, options);
    },
    [dispatchInteraction],
  );

  const submitInteraction = useCallback(
    async (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return;
      dispatchInteraction(trimmed, options);
    },
    [dispatchInteraction],
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
          includeTerminal: true,
        });
        const next = (snapshot.items ?? [])
          .map(toChatInteractionQueueItem)
          // Phase 2：按后端 position（0-based，priority desc + createdAt asc）升序；
          // 旧后端无 position 时回落 createdAt 升序。
          .sort((a, b) => {
            if (a.position != null && b.position != null) {
              return a.position - b.position;
            }
            return a.createdAt - b.createdAt;
          });
        // 快照短路：仅比较对显示有影响的字段，相同则跳过 setState，
        // 避免高频轮询期间对空/不变队列做无谓 React commit。
        // Phase 2：substate 参与指纹 —— fresh→waiting（status 不变）也需触发更新。
        const snapshotKey = JSON.stringify(
          next.map((item) => ({
            id: item.id,
            status: item.status,
            substate: item.substate,
            text: item.text,
            createdAt: item.createdAt,
            error: item.error,
          })),
        );
        const changed = snapshotKey !== serverQueueSnapshotKeyRef.current;
        serverQueueSnapshotKeyRef.current = snapshotKey;
        if (changed) {
          setServerInteractionQueue(next);
        }
        recordPerfEvent(
          'chat.queue.snapshot',
          {
            reason,
            workspaceId,
            agentId,
            itemCount: next.length,
            changed,
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
      loading ? 3000 : 5000,
    );
    return () => window.clearInterval(timer);
  }, [agentId, loading, refreshAgentMessageQueue, workspaceId]);

  useEffect(() => {
    if (!workspaceId || !agentId) setSteeringInteractionQueue([]);
  }, [agentId, workspaceId]);

  /**
   * P1#6：待发队列排空 — 空闲时按序发送下一条本地待发消息。
   * busy 判定与 dispatch 保持一致，避免在活跃 turn 期间误发。
   */
  useEffect(() => {
    const busy =
      loading ||
      hasBlockingActiveTurn(
        turnsRef.current,
        activeMessageIdsRef.current,
        messageIdToTurnIdRef.current,
      );
    if (busy || drainLockRef.current) return;
    const pending = pendingSendQueueRef.current;
    if (pending.length === 0) return;
    drainLockRef.current = true;
    const [next, ...rest] = pending;
    pendingSendQueueRef.current = rest;
    setPendingSendQueue(rest);
    recordPerfEvent('chat.queue.drain', {
      queueSizeBefore: pending.length,
      messageChars: next.text.length,
    });
    void sendMessageRef
      .current(
        next.text,
        next.metadata && Object.keys(next.metadata).length > 0
          ? { metadata: next.metadata }
          : undefined,
      )
      .finally(() => {
        drainLockRef.current = false;
      });
  }, [activeMessageIdsRef, loading, messageIdToTurnIdRef, turns, turnsRef]);

  const visibleInteractionQueue = useMemo(
    () => [
      ...serverInteractionQueue,
      ...steeringInteractionQueue,
      ...pendingSendQueue,
    ],
    [pendingSendQueue, serverInteractionQueue, steeringInteractionQueue],
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
      if (item?.source === 'local_pending') {
        updateLocalQueue((previous) =>
          previous.filter((candidate) => candidate.id !== id),
        );
        recordPerfEvent('chat.queue.localDelete', { queueItemId: id });
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
    [findVisibleQueueItem, messageApi, updateLocalQueue],
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
      if (item?.source === 'local_pending') {
        // P1#6 让位给下一条：与下一条交换；已是最后一条则轮转到队首
        updateLocalQueue((previous) => {
          const index = previous.findIndex((candidate) => candidate.id === id);
          if (index < 0) return previous;
          const next = [...previous];
          if (index < next.length - 1) {
            [next[index], next[index + 1]] = [next[index + 1], next[index]];
          } else if (next.length > 1) {
            const last = next.pop() as ChatInteractionQueueItem;
            next.unshift(last);
          }
          return next;
        });
        recordPerfEvent('chat.queue.steerYield', { queueItemId: id });
        return;
      }
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
      updateLocalQueue,
      workspaceId,
    ],
  );

  /** P1#6：本地待发队列内重排（拖拽），仅作用于 source=local_pending 的项 */
  const reorderQueuedInteraction = useCallback(
    (fromId: string, toId: string) => {
      if (fromId === toId) return;
      updateLocalQueue((previous) => {
        const fromIndex = previous.findIndex(
          (candidate) => candidate.id === fromId,
        );
        const toIndex = previous.findIndex(
          (candidate) => candidate.id === toId,
        );
        if (fromIndex < 0 || toIndex < 0) return previous;
        const next = [...previous];
        const [moved] = next.splice(fromIndex, 1);
        next.splice(toIndex, 0, moved);
        return next;
      });
      recordPerfEvent('chat.queue.reorder', { fromId, toId });
    },
    [updateLocalQueue],
  );

  /** P1#6：取消全部 — 中止在途请求 + 清空本地待发队列 + 移除未注入的 steering 项 */
  const stopQueue = useCallback(() => {
    cancelAllRef.current?.();
    updateLocalQueue(() => []);
    setSteeringInteractionQueue((previous) =>
      previous.filter((candidate) => candidate.status !== 'steering_pending'),
    );
    recordPerfEvent('chat.queue.stopAll', {
      droppedLocalCount: pendingSendQueueRef.current.length,
    });
  }, [updateLocalQueue]);

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
    reorderQueuedInteraction,
    stopQueue,
    handleKeyDown,
    markSteeringInjected,
    bindSendMessage,
    bindCancelAll,
    pendingSendQueue,
  };
}
