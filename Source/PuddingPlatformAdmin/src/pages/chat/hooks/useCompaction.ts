import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  type AdminChatStreamEvent,
  type ContextCompactionResult,
  compactSession,
  getCompactionStatus,
} from '@/services/platform/api';
import type { AssistantStatus, ChatTurn } from '../types';
import {
  COMPACTION_TURN_PREFIX,
  compactionTurnId,
  createId,
  formatCompactSuccessMessage,
  isStaleCompactionStarted,
  isFreshCompactionStarted,
  mergeHistoryWithLifecycleTurns,
  resolveEventOccurredAtMs,
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
 * 压缩请求兜底 TTL：长期没有状态确认时，收敛为「状态待确认」，不推断后端失败。
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
  verifiedAt?: number;
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
  const lifecycleEpochRef = useRef(0);
  const [checkVersion, setCheckVersion] = useState(0);
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
      facts?: { eventId?: string; occurredAtMs?: number; verifiedAt?: number },
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
              compaction: { id: turnId, state: assistantStatus === 'executing' ? 'checking' : assistantStatus === 'error' ? 'failed' : 'completed', startedAt: undefined },
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
      facts?: { eventId?: string; occurredAtMs?: number; verifiedAt?: number },
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
          compaction: {
            id: turnId,
            state: (assistantStatus === 'cancelled' ? 'unknown' : assistantStatus === 'error' ? 'failed' : assistantStatus === 'success' ? (result?.outcome && result.outcome !== 'Applied' ? 'skipped' : 'completed') : facts?.verifiedAt ? 'running' : 'checking') as import('../types').CompactionPresentation['state'],
            startedAt: assistantStatus === 'executing' ? (items[compactItemIndex]?.compaction?.startedAt ?? facts?.occurredAtMs) : items[compactItemIndex]?.compaction?.startedAt,
            verifiedAt: facts?.verifiedAt,
            endedAt: assistantStatus === 'success' || assistantStatus === 'error' ? facts?.occurredAtMs : undefined,
          },
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
            answerMarkdown: '',
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
   * 活性兜底：把未经确认的执行投影收敛为 unknown，不制造后端终态。
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
        updateCompactTurn(turnId, 'cancelled', '未收到结束记录，当前状态待确认');
        // updateCompactTurn 只修 turnsRef 里的 turn；lifecycle map 副本也必须同步收敛，
        // 否则 mergeCompactionLifecycleTurns 下次合并时僵尸状态会复活。
        const mapped = compactionLifecycleTurnsRef.current.get(turnId);
        if (mapped && mapped.assistant.status === 'executing') {
          compactionLifecycleTurnsRef.current.set(turnId, {
            ...mapped,
            assistant: {
              ...mapped.assistant,
              status: 'cancelled',
              isStreaming: false,
              answerMarkdown: '',
            },
          });
        }
      }
      activeCompactionTurnIdRef.current = null;
      setCompactionStatus('压缩状态待确认');
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
        compactionTurnId(compactionId);
      const previous = compactionLifecycleTurnsRef.current.get(compactTurnId);
      if (
        event.type === 'context.compaction.started' &&
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
        event.type === 'context.compaction.started'
      ) {
        // 有权限来源（bootstrap 带 compactionRunning）：按 id 精确判活；
        // 无来源（缺口重放 / 帧标记为 replay 的历史追赶）：要求「确定新鲜」才点亮——
        // 否则短暂断线期间真的启动了压缩会被误杀，直到终态才可见。
        const hasAuthority = options.runningCompactionId !== undefined;
        const drop = hasAuthority
          ? compactionId !== (options.runningCompactionId ?? null)
          : !isFreshCompactionStarted(raw);
        if (drop) {
          logChatDiag('compaction.startedIgnoredReplay', {
            compactionId,
            hasAuthority,
          });
          return;
        }
      }
      // 路径无关的陈旧 started 门控（2026-09-19）：live 通道不带 replay 标记，
      // SSE 无游标全量重放历史时会把多天前的孤儿 started 当实时事件送来，
      // 按 id 的判活门控拦不住——见 chatStateUtils.isStaleCompactionStarted。
      if (
        event.type === 'context.compaction.started' &&
        options?.verifiedAt === undefined &&
        isStaleCompactionStarted(raw)
      ) {
        logChatDiag('compaction.startedIgnoredStale', {
          compactionId,
          replay: options?.replay === true,
        });
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
        occurredAtMs: resolveEventOccurredAtMs(raw),
        verifiedAt: options?.verifiedAt,
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
      if (event.type === 'context.compaction.started') {
        if (options?.verifiedAt === undefined) setCheckVersion(value => value + 1);
        // 真在跑的压缩（实时 SSE 或重放判活放行）：点亮运行态并挂活性 TTL，
        // 终态缺失时由 TTL 收敛，不允许「duration:0 弹了就不管」。
        activeCompactionTurnIdRef.current = compactTurnId;
        armCompactionLivenessTimer();
        setCompactionStatus(options?.verifiedAt ? COMPACTION_RUNNING_LABEL : '正在确认压缩状态');
        updateCompactTurn(
          compactTurnId,
          'executing',
          '正在压缩上下文…',
          undefined,
          eventFacts,
        );
        return;
      }

      const closesActive = activeCompactionTurnIdRef.current === compactTurnId;
      if (closesActive) clearCompactionLivenessTimer();
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
        if (closesActive) {
          setCompactionStatus(`压缩失败：${errorMessage}`);
          activeCompactionTurnIdRef.current = null;
        }
        if (options?.notify !== false && options?.replay !== true) messageApi.error(errorMessage, 4);
        return;
      }

      const compacted =
        raw.compaction && typeof raw.compaction === 'object'
          ? (raw.compaction as ContextCompactionResult)
          : typeof raw.outcome === 'string' ? ({ outcome: raw.outcome } as ContextCompactionResult) : undefined;
      updateCompactTurn(
        compactTurnId,
        'success',
        compacted?.compactedMessageCount ? `已整理 ${compacted.compactedMessageCount} 条历史消息` : '上下文整理完成',
        compacted,
        eventFacts,
      );
      if (!activeCompactionTurnIdRef.current || closesActive) setCompactionStatus(
        `上次压缩：${formatCompactionTime(
          eventFacts.occurredAtMs ??
            (options?.notify === false ? undefined : Date.now()),
        )}`,
      );
      if (closesActive) activeCompactionTurnIdRef.current = null;

      const newSessionId =
        typeof raw.newSessionId === 'string' ? raw.newSessionId : null;
      const newSessionTitle =
        typeof raw.newSessionTitle === 'string'
          ? raw.newSessionTitle
          : '新会话';
      if (
        options?.replay !== true &&
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

  useEffect(() => {
    const session = selectedSessionId ?? sessionIdRef.current;
    if (!session) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const reconcile = async () => {
      try {
        const epoch = lifecycleEpochRef.current;
        const activeAtRequest = activeCompactionTurnIdRef.current;
        const snapshot = await getCompactionStatus(session);
        if (disposed || epoch !== lifecycleEpochRef.current || activeAtRequest !== activeCompactionTurnIdRef.current) return;
        const active = snapshot.activeCompaction;
        if (active) {
          handleCompactionLifecycleEvent({
            type: 'context.compaction.started', compactionId: active.compactionId,
            occurredAt: active.startedAt,
          }, { notify: false, verifiedAt: Date.now() });
        } else if (activeAtRequest === activeCompactionTurnIdRef.current) {
          convergeStaleCompactions('server-not-running');
        }
      } catch {
        if (!disposed) convergeStaleCompactions('status-unavailable');
        // Connectivity failure is not a failed compaction. The presentation's
        // short verification lease expires to "status unknown" without a spinner.
      } finally {
        if (!disposed) timer = setTimeout(reconcile, 10_000);
      }
    };
    void reconcile();
    return () => { disposed = true; clearTimeout(timer); };
  }, [selectedSessionId, sessionIdRef, handleCompactionLifecycleEvent, convergeStaleCompactions, checkVersion]);

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

    const epoch = lifecycleEpochRef.current;
    setError(null);
    setLoading(true);
    setCompactionStatus('已请求压缩，等待执行');
    const compactionId = createId();
    const compactTurnId = appendCompactTurn(
      '正在压缩上下文…',
      'executing',
      undefined,
      compactionTurnId(compactionId),
    );
    compactionTurnIdsRef.current.set(compactionId, compactTurnId);
    activeCompactionTurnIdRef.current = compactTurnId;
    armCompactionLivenessTimer();
    try {
      const response = await compactSession(currentSessionId, {
        workspaceId,
        agentId,
        level: 'Full',
        reason: 'manual slash command',
        compactionId,
      });
      if (epoch !== lifecycleEpochRef.current) return;
      clearCompactionLivenessTimer();
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
      if (epoch !== lifecycleEpochRef.current) return;
      clearCompactionLivenessTimer();
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
    armCompactionLivenessTimer,
    clearCompactionLivenessTimer,
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
    lifecycleEpochRef.current += 1;
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
