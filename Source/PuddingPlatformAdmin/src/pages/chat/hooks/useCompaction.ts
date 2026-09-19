import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useEffect, useRef, useState } from 'react';
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
import { logChatDiag } from '../utils/chatDiagnostics';

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

// An absolute event time stays truthful during long unattended runs without a timer.
const formatCompactionTime = (timestamp?: number): string =>
  timestamp === undefined
    ? '时间未知'
    : new Date(timestamp).toLocaleString('zh-CN', { hour12: false });

export const COMPACTION_RUNNING_LABEL = '正在压缩上下文…';

/**
 * 压缩活性 TTL：点亮运行态后若终态事件迟迟不来，超时即收敛为「未完成」终态。
 * 为什么需要：started 事件没有任何活性保证（重放孤儿/终态丢失都会留下僵尸），
 * 禁止「duration:0 弹了就不管」。
 */
export const COMPACTION_LIVENESS_TIMEOUT_MS = 10 * 60 * 1000;

export interface CompactionLifecycleOptions {
  allowSessionSwitch?: boolean;
  notify?: boolean;
  /** true = 事件来自历史/缺口重放而非实时 SSE：孤儿 started 不再冒充运行中。 */
  replay?: boolean;
  /** 重放判活：仅当 started 的 compactionId 与它一致时才点亮运行态。 */
  runningCompactionId?: string | null;
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
  /** 已点亮压缩的活性 TTL 定时器（见 COMPACTION_LIVENESS_TIMEOUT_MS）。 */
  const compactionLivenessTimerRef = useRef<ReturnType<
    typeof setTimeout
  > | null>(null);
  const compactedSessionSwitchRef = useRef<CompactedSessionSwitch>(() => {});
  const lastManualSwitchAtRef = useRef<number>(0);
  /** P0#3：供 ComposerContextBar 展示的压缩状态文案 */
  const [compactionStatus, setCompactionStatus] = useState<string | null>(null);

  /** Called by handleSelectSession to prevent compaction from overriding manual selection (RC-6). */
  const markManualSessionSwitch = useCallback(() => {
    lastManualSwitchAtRef.current = performance.now();
  }, []);

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
      facts?: { eventId?: string; occurredAtMs?: number },
    ) => {
      // 事件驱动路径使用 canonical 事实；手动压缩命令为客户端乐观身份（无服务端事实）。
      const now = facts?.occurredAtMs ?? Date.now();
      const eventId = facts?.eventId;
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
          id: eventId ? `umsg:${eventId}` : createId(),
          text: '',
          timestamp: now,
          status: 'success',
        },
        assistant: {
          id: eventId ? `amsg:${eventId}` : createId(),
          status: assistantStatus,
          timelineItems: [
            {
              id: eventId ? `item:${eventId}` : createId(),
              eventId,
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
          // 运行中不把「正在压缩上下文…」塞进 answerMarkdown：正文区是给用户看的答案，
          // 压缩进度应由状态行/进度项表达，否则会在卡片里多出一行带流式光标的花答。
          // （用户反馈 2026-09-19：压缩卡片显示效果乱。）
          answerMarkdown: result ? formatCompactAnswer(result) : '',
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
      facts?: { eventId?: string; occurredAtMs?: number },
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
          id:
            compactItemIndex >= 0
              ? items[compactItemIndex].id
              : facts?.eventId
                ? `item:${facts.eventId}`
                : createId(),
          eventId:
            facts?.eventId ??
            (compactItemIndex >= 0
              ? items[compactItemIndex].eventId
              : undefined),
          type: 'subconscious_step' as const,
          status: itemStatus,
          message,
          timestamp: facts?.occurredAtMs ?? Date.now(),
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

  const clearCompactionLivenessTimer = useCallback(() => {
    if (compactionLivenessTimerRef.current !== null) {
      clearTimeout(compactionLivenessTimerRef.current);
      compactionLivenessTimerRef.current = null;
    }
  }, []);

  /**
   * 活性兜底：把仍在 'executing' 的压缩 turn 收敛为「未完成」终态。
   * 为什么需要：started 没有活性保证——重放孤儿 started 或终态事件丢失都会留下
   * 「永远在压缩」的僵尸 turn 和永不消失的 loading toast，这里是唯一能保证
   * UI 最终一致性的收口（TTL 超时触发）。
   */
  const convergeStaleCompactions = useCallback(
    (reason: string) => {
      clearCompactionLivenessTimer();
      const staleTurnIds = Array.from(
        compactionLifecycleTurnsRef.current.entries(),
      )
        .filter(([, turn]) => turn.assistant.status === 'executing')
        .map(([turnId]) => turnId);
      if (staleTurnIds.length === 0) return;
      for (const turnId of staleTurnIds) {
        updateCompactTurn(turnId, 'error', '压缩未完成（无终态记录）');
        // updateCompactTurn 只修 turnsRef 里的 turn；lifecycle map 副本也必须同步收敛，
        // 否则 mergeCompactionLifecycleTurns 下次合并时僵尸状态会复活。
        const mapped = compactionLifecycleTurnsRef.current.get(turnId);
        if (mapped && mapped.assistant.status === 'executing') {
          compactionLifecycleTurnsRef.current.set(turnId, {
            ...mapped,
            assistant: {
              ...mapped.assistant,
              status: 'error',
              isStreaming: false,
              answerMarkdown: '压缩未完成（无终态记录）',
            },
          });
        }
      }
      activeCompactionTurnIdRef.current = null;
      setLoading(false);
      setCompactionStatus('上次压缩：未完成');
      messageApi.destroy('compaction-status');
      logChatDiag('compaction.staleConverged', {
        reason,
        count: staleTurnIds.length,
      });
    },
    [clearCompactionLivenessTimer, messageApi, setLoading, updateCompactTurn],
  );

  /** 只有真正点亮的压缩才挂活性 TTL；终态/重置/卸载时必须清除。 */
  const armCompactionLivenessTimer = useCallback(() => {
    clearCompactionLivenessTimer();
    compactionLivenessTimerRef.current = setTimeout(() => {
      compactionLivenessTimerRef.current = null;
      convergeStaleCompactions('compaction-liveness-timeout');
    }, COMPACTION_LIVENESS_TIMEOUT_MS);
  }, [clearCompactionLivenessTimer, convergeStaleCompactions]);

  // 组件卸载时清除活性定时器，防止卸载后 converge 触碰已卸载的状态。
  useEffect(() => clearCompactionLivenessTimer, [clearCompactionLivenessTimer]);

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
      const previous = compactionLifecycleTurnsRef.current.get(compactTurnId);
      if (
        compactionTurnIdsRef.current.has(compactionId) &&
        (previous?.assistant.status === 'success' ||
          previous?.assistant.status === 'error')
      ) {
        // Bootstrap and SSE can overlap. A late/replayed start must not revive a
        // terminal compaction or show its loading notification again.
        return;
      }
      // 重放路径判活门控：只有 started 的 compactionId 与 runningCompactionId 一致
      // （确实是最后一个未终态的 started）才允许点亮运行态；孤儿 started 必须整条忽略
      // ——不建 turn、不 setLoading、不弹 toast，否则刷新页面会把历史压缩
      // 复活成「正在压缩上下文」。历史完成/失败由紧随其后的终态事件按事实渲染。
      if (
        options?.replay === true &&
        event.type === 'context.compaction.started' &&
        compactionId !== (options.runningCompactionId ?? null)
      ) {
        return;
      }
      const eventConversationId =
        typeof raw.conversationId === 'string' ? raw.conversationId : null;
      const sourceSessionId =
        typeof raw.sourceSessionId === 'string' ? raw.sourceSessionId : null;
      const placeAtStart =
        event.type === 'context.compaction.completed' &&
        eventConversationId !== null &&
        sourceSessionId !== null &&
        eventConversationId !== sourceSessionId;

      const eventFacts = {
        eventId:
          typeof raw.eventId === 'string' && raw.eventId
            ? raw.eventId
            : undefined,
        occurredAtMs:
          typeof raw.occurredAt === 'string' &&
          Number.isFinite(Date.parse(raw.occurredAt))
            ? Date.parse(raw.occurredAt)
            : undefined,
      };

      if (!turnsRef.current.some((turn) => turn.turnId === compactTurnId)) {
        compactTurnId = appendCompactTurn(
          event.type === 'context.compaction.failed'
            ? String(raw.error || '上下文压缩失败')
            : '正在压缩上下文…',
          event.type === 'context.compaction.failed' ? 'error' : 'executing',
          undefined,
          compactTurnId,
          placeAtStart,
          eventFacts,
        );
      }
      compactionTurnIdsRef.current.set(compactionId, compactTurnId);
      activeCompactionTurnIdRef.current = compactTurnId;

      if (event.type === 'context.compaction.started') {
        // 真在跑的压缩（实时 SSE 或重放判活放行）：点亮运行态并挂活性 TTL，
        // 终态缺失时由 TTL 收敛，不允许「duration:0 弹了就不管」。
        armCompactionLivenessTimer();
        setLoading(true);
        setCompactionStatus(COMPACTION_RUNNING_LABEL);
        updateCompactTurn(
          compactTurnId,
          'executing',
          '正在压缩上下文…',
          undefined,
          eventFacts,
        );
        if (options?.notify !== false && options?.replay !== true) {
          // 重放路径永不弹 toast：刷新点亮真在跑的压缩是对的，
          // 但历史 loading toast 不该复活；只有实时 SSE 的 started 才弹。
          messageApi.loading({
            content: '正在压缩上下文…',
            key: 'compaction-status',
            duration: 0,
          });
        }
        return;
      }

      clearCompactionLivenessTimer();
      setLoading(false);
      if (options?.notify !== false) messageApi.destroy('compaction-status');
      if (event.type === 'context.compaction.failed') {
        const errorMessage = String(raw.error || '上下文压缩失败');
        updateCompactTurn(
          compactTurnId,
          'error',
          errorMessage,
          undefined,
          eventFacts,
        );
        setCompactionStatus(`压缩失败：${errorMessage}`);
        activeCompactionTurnIdRef.current = null;
        if (options?.notify !== false) messageApi.error(errorMessage, 4);
        return;
      }

      const compacted =
        raw.compaction && typeof raw.compaction === 'object'
          ? (raw.compaction as ContextCompactionResult)
          : undefined;
      updateCompactTurn(
        compactTurnId,
        'success',
        '上下文压缩完成',
        compacted,
        eventFacts,
      );
      setCompactionStatus(
        `上次压缩：${formatCompactionTime(
          eventFacts.occurredAtMs ??
            (options?.notify === false ? undefined : Date.now()),
        )}`,
      );
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
        // RC-6: Don't override a recent manual session switch
        const msSinceManualSwitch =
          performance.now() - lastManualSwitchAtRef.current;
        if (msSinceManualSwitch < 2000) {
          messageApi.info(
            `上下文压缩完成，新会话「${newSessionTitle}」已就绪（未自动切换）`,
            4,
          );
        } else {
          messageApi.success(
            `上下文压缩完成，已切换到「${newSessionTitle}」`,
            4,
          );
          compactedSessionSwitchRef.current(newSessionId, newSessionTitle);
        }
      }
    },
    [
      appendCompactTurn,
      armCompactionLivenessTimer,
      clearCompactionLivenessTimer,
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
    setCompactionStatus(COMPACTION_RUNNING_LABEL);
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
      setCompactionStatus(`上次压缩：${formatCompactionTime(Date.now())}`);
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
      setCompactionStatus(`压缩失败：${message}`);
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
    clearCompactionLivenessTimer();
    setCompactionStatus(null);
    messageApi.destroy('compaction-status');
  }, [clearCompactionLivenessTimer, messageApi]);

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
    markManualSessionSwitch,
    compactionStatus,
  };
}
