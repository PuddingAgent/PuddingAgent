import type { MessageInstance } from 'antd/es/message/interface';
import type { KeyboardEvent, MutableRefObject } from 'react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  cancelConversationTurn,
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
  /**
   * 出站文本装饰（如「本轮待附加技能」提示）。
   * 必须在这一层应用：Enter 发送走 handleKeyDown，它**直接调 submitInteraction**，
   * 不经过 ChatPage 的 handleSend。若只在 handleSend 里拼接，按 Enter 就会丢掉装饰。
   */
  outgoingDecoration?: OutgoingDecoration;
}

/**
 * 出站文本装饰。把「本轮待附加」内容并入真正发出的文本。
 * 注入点固定在 submitInteraction，覆盖 Enter / 发送按钮 / 图片 / window 事件四条路径。
 */
export interface OutgoingDecoration {
  /** 纯函数：同输入必同输出（可能被重复调用，不得携带副作用）。 */
  apply: (text: string) => string;
  /** 原文为空时是否仍有可发内容（决定「只选技能、不打字」能否发送）。 */
  canSendEmpty: () => boolean;
  /** 装饰已被应用（本轮内容已消费），用于清空 chip。 */
  onConsumed: () => void;
}

/**
 * Owns the composer and backend-owned interaction queue projection.
 * Sending remains a bound command so this domain does not depend on SSE internals.
 */
export function useMessageInteractionQueue({
  identity,
  execution,
  messageApi,
  outgoingDecoration,
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
      const raw = text.trim();
      // 装饰应用点：所有发送路径最终都汇到这里（含 Enter，它不经过 handleSend）。
      const outgoing = outgoingDecoration
        ? outgoingDecoration.apply(raw).trim()
        : raw;
      if (!outgoing) return;
      outgoingDecoration?.onConsumed();
      dispatchInteraction(outgoing, options);
    },
    [dispatchInteraction, outgoingDecoration],
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

  /**
   * 请求服务端协作式取消当前正在运行的 canonical Turn（ADR-059）。
   * 返回成功受理的取消请求数。Turn 已结束/未受理（400）属正常竞态：
   * 本地 abort 与 SSE 终态事件已兜底，不向用户报错。
   */
  const requestActiveTurnCancel = useCallback(async (): Promise<number> => {
    const conversationId = sessionIdRef.current ?? selectedSessionId;
    if (!workspaceId || !conversationId) return 0;
    const activeTurnIds = new Set<string>();
    for (const messageId of activeMessageIdsRef.current) {
      const turnId = messageIdToTurnIdRef.current.get(messageId);
      if (turnId) activeTurnIds.add(turnId);
    }
    if (activeTurnIds.size === 0) return 0;

    let requested = 0;
    for (const turnId of activeTurnIds) {
      try {
        await cancelConversationTurn(workspaceId, conversationId, turnId);
        requested += 1;
        recordPerfEvent('chat.stop.cancelRequested', {
          conversationId,
          turnId,
        });
      } catch (error) {
        recordPerfEvent('chat.stop.cancelRequestFailed', {
          conversationId,
          turnId,
          error: error instanceof Error ? error.message : String(error),
        });
      }
    }
    if (requested > 0) {
      messageApi.success('停止请求已受理，当前执行将尽快中断');
    }
    return requested;
  }, [
    activeMessageIdsRef,
    messageApi,
    messageIdToTurnIdRef,
    selectedSessionId,
    sessionIdRef,
    workspaceId,
  ]);

  /** 取消全部：停止当前执行 + 清空未注入的本地补充投影。已受理 Turn 不会被前端丢弃。 */
  const stopQueue = useCallback(() => {
    cancelAllRef.current?.();
    void requestActiveTurnCancel();
    setSteeringInteractionQueue((previous) =>
      previous.filter((candidate) => candidate.status !== 'steering_pending'),
    );
    recordPerfEvent('chat.queue.stopAll', { droppedLocalCount: 0 });
  }, [requestActiveTurnCancel]);

  const handleKeyDown = useCallback(
    (event: KeyboardEvent<HTMLTextAreaElement>) => {
      const value = inputValueRef.current;
      if (
        event.key === 'Enter' &&
        (event.ctrlKey || event.metaKey || !event.shiftKey)
      ) {
        event.preventDefault();
        const trimmed = value.trim();
        // 原文为空但仍有装饰内容（如只选了技能没打字）时不得拦截发送。
        if (!trimmed && !(outgoingDecoration?.canSendEmpty() ?? false)) return;
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
      outgoingDecoration,
      submitInteraction,
      submitSteeringInteraction,
      turns,
      turnsRef,
    ],
  );

  useEffect(() => {
    const handler = () => {
      const text = inputValueRef.current.trim();
      // 同 handleKeyDown：空原文 + 有装饰内容时仍应发出。
      if (!text && !(outgoingDecoration?.canSendEmpty() ?? false)) return;
      setInputValue('');
      void submitInteraction(text);
    };
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, [submitInteraction, outgoingDecoration]);

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
    requestActiveTurnCancel,
    handleKeyDown,
    markSteeringInjected,
    bindSendMessage,
    bindCancelAll,
  };
}
