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
  isActiveAssistantTurn,
  removeInjectedSteeringQueueItem,
  toChatInteractionQueueItem,
} from '../utils/chatStateUtils';

type SendMessage = (text: string, options?: ChatSendOptions) => Promise<void>;

const ACTIVE_BACKEND_QUEUE_STATUSES = new Set([
  'queued',
  'retrying',
]);

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
  /** P0#9 后端队列快照指纹：相同快照短路，避免高频轮询触发无谓 React commit */
  const serverQueueSnapshotKeyRef = useRef<string>('[]');
  const sendMessageRef = useRef<SendMessage>(async () => {});
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;
  const steeringInjectedDismissTimersRef = useRef<Map<string, number>>(
    new Map(),
  );
  /** 同一来源项只允许一个 steering admission 请求在途。 */
  const steeringSubmissionIdsRef = useRef<Set<string>>(new Set());
  /** 取消全部时中止当前在途请求（由 useChatState 绑定 abortRef） */
  const cancelAllRef = useRef<() => void>(() => {});

  const bindSendMessage = useCallback((handler: SendMessage) => {
    sendMessageRef.current = handler;
  }, []);

  const bindCancelAll = useCallback((handler: () => void) => {
    cancelAllRef.current = handler;
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

  /**
   * 普通消息无论 Agent 是否 busy，都立即提交 canonical Turn API。
   * API 受理后由 chat_execution_commands + ChatExecutionWorker 持久化排队，
   * 不再把可执行消息留在 React 内存里等待页面排空。
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
      recordPerfEvent(
        'chat.queue.dispatch',
        {
          mode: 'durable-server',
          localBusy,
          messageChars: trimmed.length,
          hasMetadata: Boolean(options?.metadata),
        },
        { throttleMs: 1_000 },
      );
      void sendMessageRef.current(trimmed, options);
      return null;
    },
    [activeMessageIdsRef, loading, messageIdToTurnIdRef, turnsRef],
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
          includeTerminal: false,
        });
        const next = (snapshot.items ?? [])
          // Composer 只展示尚未被消费者认领的 queued/retrying 项。delivery
          // 已认领或 Turn 已 leased/running 后，所有权已转入会话时间线，不能
          // 再以“处理中”副本占据消息队列。即使旧后端忽略
          // includeTerminal=false，也在客户端边界再次过滤。
          .filter((item) => ACTIVE_BACKEND_QUEUE_STATUSES.has(item.status))
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

  /**
   * 将文本注入当前正在运行的 Turn。Steering 不创建第二个并发 Turn；Runtime 会在
   * 当前工具或模型步骤结束后的下一次 LLM 请求前消费它。
   */
  const submitSteeringInteraction = useCallback(
    async (text: string, sourceQueueItemId?: string): Promise<boolean> => {
      const trimmed = text.trim();
      if (!trimmed) return false;
      if (
        sourceQueueItemId &&
        steeringSubmissionIdsRef.current.has(sourceQueueItemId)
      ) {
        return false;
      }

      const sessionId = sessionIdRef.current ?? selectedSessionId;
      const trackedTurnIds = new Set(
        [...activeMessageIdsRef.current]
          .map((messageId) => messageIdToTurnIdRef.current.get(messageId))
          .filter((turnId): turnId is string => Boolean(turnId)),
      );
      const activeTurn = [...turnsRef.current]
        .reverse()
        .find(
          (turn) =>
            isActiveAssistantTurn(turn) || trackedTurnIds.has(turn.turnId),
        );

      if (!workspaceId || !sessionId || !activeTurn) {
        messageApi.error('当前没有可插嘴的运行中 Agent，消息仍保留在队列中');
        return false;
      }

      if (sourceQueueItemId) {
        steeringSubmissionIdsRef.current.add(sourceQueueItemId);
      }

      const submittedStartAt = Date.now();
      const localSteeringId = `steering-local-${sourceQueueItemId ?? createId()}`;
      setSteeringInteractionQueue((previous) => [
        ...previous.filter((candidate) => candidate.id !== localSteeringId),
        {
          id: localSteeringId,
          text: trimmed,
          createdAt: submittedStartAt,
          status: 'steering_pending',
          source: 'steering',
          submittedAt: submittedStartAt,
          error: undefined,
        },
      ]);
      recordPerfEvent('chat.steering.submit', {
        queueItemId: sourceQueueItemId,
        sessionId,
        turnId: activeTurn.turnId,
        agentId,
        messageChars: trimmed.length,
      });

      try {
        const response = await createChatSteeringMessage(
          workspaceId,
          sessionId,
          activeTurn.turnId,
          {
            messageText: trimmed,
            agentId,
            sourceQueueItemId,
            priority: 1000,
          },
        );
        const acceptedAt = Date.now();
        setSteeringInteractionQueue((previous) =>
          previous.map((candidate) =>
            candidate.id === localSteeringId
              ? {
                  ...candidate,
                  status: 'steering_pending',
                  steeringId: response.steeringId,
                  submittedAt: acceptedAt,
                }
              : candidate,
          ),
        );
        recordPerfEvent('chat.steering.submitted', {
          queueItemId: sourceQueueItemId,
          steeringId: response.steeringId,
          sessionId,
          workspaceId,
          agentId,
          turnId: activeTurn.turnId,
          createdAt: acceptedAt,
          requestLatencyMs: Math.max(0, acceptedAt - submittedStartAt),
        });
        messageApi.success(
          '插嘴已受理，将在当前步骤结束后的下一次模型请求前生效',
        );
        return true;
      } catch (error: unknown) {
        const errorMessage =
          error instanceof Error ? error.message : '插嘴提交失败';
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
          queueItemId: sourceQueueItemId,
          sessionId,
          turnId: activeTurn.turnId,
          agentId,
          requestLatencyMs: Math.max(0, Date.now() - submittedStartAt),
          error: errorMessage,
        });
        messageApi.error(errorMessage);
        return false;
      } finally {
        if (sourceQueueItemId) {
          steeringSubmissionIdsRef.current.delete(sourceQueueItemId);
        }
      }
    },
    [
      activeMessageIdsRef,
      agentId,
      messageApi,
      messageIdToTurnIdRef,
      selectedSessionId,
      sessionIdRef,
      turnsRef,
      workspaceId,
    ],
  );

  const steerQueuedInteraction = useCallback(
    async (id: string) => {
      const item = findVisibleQueueItem(id);
      recordPerfEvent('chat.queue.steerIgnored', {
        reason: 'backend-owned-queue',
        queueItemId: id,
        status: item?.status,
      });
      messageApi.info('后端投递队列项暂不能安全转换为插嘴，避免消息重复执行');
    },
    [findVisibleQueueItem, messageApi],
  );

  /** 服务端队列尚未开放重排命令；不在前端伪造顺序。 */
  const reorderQueuedInteraction = useCallback(
    (fromId: string, toId: string) => {
      if (fromId === toId) return;
      recordPerfEvent('chat.queue.reorderIgnored', {
        reason: 'backend-owned-queue',
        fromId,
        toId,
      });
    },
    [],
  );

  /** 取消当前请求并移除未注入的 steering 投影。已受理 Turn 不会被前端丢弃。 */
  const stopQueue = useCallback(() => {
    cancelAllRef.current?.();
    setSteeringInteractionQueue((previous) =>
      previous.filter((candidate) => candidate.status !== 'steering_pending'),
    );
    recordPerfEvent('chat.queue.stopAll', { droppedLocalCount: 0 });
  }, []);

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
        } else if (
          (event.ctrlKey || event.metaKey) &&
          (loading ||
            hasBlockingActiveTurn(
              turnsRef.current,
              activeMessageIdsRef.current,
              messageIdToTurnIdRef.current,
            ))
        ) {
          void submitSteeringInteraction(trimmed).then((accepted) => {
            if (!accepted) {
              // 不覆盖用户在请求期间新输入的内容；空输入框才恢复失败的插嘴文本。
              setInputValue((current) => current || trimmed);
            }
          });
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
    [
      activeMessageIdsRef,
      handleCompactCommand,
      loading,
      messageIdToTurnIdRef,
      submitInteraction,
      submitSteeringInteraction,
      turns,
      turnsRef,
    ],
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
    submitSteeringInteraction,
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
  };
}
