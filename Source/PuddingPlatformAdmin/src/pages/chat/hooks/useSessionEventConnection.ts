import type { MutableRefObject } from 'react';
import { useCallback, useRef, useState } from 'react';
import { message } from 'antd';
import {
  type AdminChatStreamEvent,
  subscribeSessionEvents,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/perfEventRuntime';
import type { ChatTurn } from '../types';
import { logChatDiag } from '../utils/chatDiagnostics';
import { resolveSessionReplayPollInterval } from '../utils/chatStateUtils';
import { isSessionNotFoundError } from './sessionRuntimeCleanup';
import {
  type SessionEventCursorPreparation,
  type SessionEventCursorState,
  type SessionEventStreamEnsureResult,
  decideSessionEventStreamStart,
  isCursorReadyForSession,
} from './sessionEventCursor';

/**
 * startSessionEventStream 的可选覆盖项（S3：游标必须显式）。
 */
export interface StartSessionEventStreamOptions {
  /**
   * 显式订阅起点。省略时使用**该会话已就绪的权威游标**（bootstrap/history 写入）；
   * 未就绪时不再退回 0，而是拒绝开流（调用方须走 ensureSessionEventStream）。
   * 显式 0 表示**有意全量回放**，仅用于服务端刚创建、事件日志为空的新会话。
   */
  cursor?: number;
  /** 诊断用：为何以该游标开流。 */
  reason?: string;
}

export interface SessionEventConnectionPorts {
  applySessionEvent: (
    event: AdminChatStreamEvent,
    options?: { replay?: boolean },
  ) => void;
  handleSessionNotFound: (sessionId: string, reason: string) => void;
  pruneTrackedActiveMessages: (reason: string) => boolean;
  replayMissedSessionEvents: (
    sessionId: string,
    signal?: AbortSignal,
  ) => Promise<void>;
  replayMissedSessionEventsIfNeeded: (
    sessionId: string,
    options: {
      signal?: AbortSignal;
      reason: string;
      hasActiveMessages?: boolean;
    },
  ) => Promise<boolean>;
  resetStreamCursorForSessionChange: (
    previousSessionId?: string | null,
    nextSessionId?: string | null,
  ) => void;
  /**
   * 重取 `/bootstrap` 快照并把游标同步到快照位置。
   *
   * `resetCursor: true` 表示**强制**把游标置为快照位置——用于 410
   * `snapshot_required`：本地游标之后的事件已不可读，只能以快照为新起点；
   * 默认的 Math.max 语义会保留陈旧游标，导致重连后再次 410。
   *
   * 返回**判别联合**：`ok=false` 明确表示「准备失败/被取消/会话已过期」，
   * 调用方不得把失败当成「成功但没有 turns」。
   */
  syncCompletedHistoryEventCursor: (
    sessionId: string,
    signal?: AbortSignal,
    options?: { resetCursor?: boolean; isCurrent?: () => boolean },
  ) => Promise<SessionEventCursorPreparation>;
  flushPendingDeltas: () => void;
  syncSessionIdentity: () => void;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  lastSequenceNumRef: MutableRefObject<number>;
  /** 带会话身份与阶段的游标状态：开流决策的权威来源。 */
  sessionEventCursorRef: MutableRefObject<SessionEventCursorState>;
  streamStartAtRef: MutableRefObject<Map<string, number>>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  sessionIdRef: MutableRefObject<string | undefined>;
  turnsRef: MutableRefObject<ChatTurn[]>;
  /**
   * 该会话是否由消息/活动投影承担**主消息**实时更新。
   *
   * 注意：这不等于「不开流」。投影会话仍然必须订阅 canonical 事件 —— 主消息的重复
   * 投影由 `useSessionEventProjection` 的会话域过滤掉，而子代理事实只能走这条通道
   * （诊断 2026-10-06）。此端口现在只用于诊断标记该流的存在理由。
   */
  isProjectionOwnedSession: (sessionId: string) => boolean;
}

const noop = () => {};
const defaultPorts = {
  applySessionEvent: noop,
  handleSessionNotFound: noop,
  pruneTrackedActiveMessages: () => false,
  replayMissedSessionEvents: async () => {},
  replayMissedSessionEventsIfNeeded: async () => false,
  resetStreamCursorForSessionChange: noop,
  syncCompletedHistoryEventCursor: async () => ({
    ok: false,
    reason: 'failed',
  }),
  flushPendingDeltas: noop,
  syncSessionIdentity: noop,
  activeMessageIdsRef: { current: new Set<string>() },
  lastSequenceNumRef: { current: 0 },
  sessionEventCursorRef: {
    current: { sessionId: null, phase: 'unknown' as const, sequence: 0 },
  },
  streamStartAtRef: { current: new Map<string, number>() },
  selectedSessionIdRef: { current: null },
  sessionIdRef: { current: undefined },
  turnsRef: { current: [] },
  isProjectionOwnedSession: () => false,
} satisfies SessionEventConnectionPorts;

/** One in-flight "make the cursor ready, then open the stream" request. */
interface ConnectionRequest {
  sessionId: string;
  generation: number;
  controller: AbortController;
  promise: Promise<SessionEventStreamEnsureResult> | null;
}

/** Owns live SSE connection refs, reconnect scheduling, and replay polling. */
export function useSessionEventConnection() {
  const portsRef = useRef<SessionEventConnectionPorts>(defaultPorts);
  const sessionEventsAbortRef = useRef<AbortController | null>(null);
  const sessionEventsPollTimerRef = useRef<number | null>(null);
  const sessionEventsReconnectTimerRef = useRef<number | null>(null);
  const sessionEventsWatchdogTimerRef = useRef<number | null>(null);
  const sseSessionIdRef = useRef<string | null>(null);
  const lastSseEventAtRef = useRef<number | null>(null);
  const reconnectCountRef = useRef(0);
  const [reconnectCount, setReconnectCount] = useState(0);
  const connectionRequestRef = useRef<ConnectionRequest>({
    sessionId: '',
    generation: 0,
    controller: new AbortController(),
    promise: null,
  });
  /**
   * 重连兜底入口：重连时游标可能已被清空（会话切换/快照重置），此时必须先重新准备游标，
   * 而不是以 0 兜底开流。用 ref 传递以避免「先定义的 scheduleReconnect 引用后定义的 ensure」。
   */
  const ensureSessionEventStreamRef = useRef<
    ((
      sessionId: string,
      options?: { forceReconnect?: boolean },
    ) => Promise<SessionEventStreamEnsureResult>) | null
  >(null);

  const bindSessionEventConnection = useCallback(
    (ports: SessionEventConnectionPorts) => {
      portsRef.current = ports;
    },
    [],
  );

  const clearSessionEventTimers = useCallback(() => {
    if (sessionEventsPollTimerRef.current != null) {
      window.clearTimeout(sessionEventsPollTimerRef.current);
      sessionEventsPollTimerRef.current = null;
    }
    if (sessionEventsReconnectTimerRef.current != null) {
      window.clearTimeout(sessionEventsReconnectTimerRef.current);
      sessionEventsReconnectTimerRef.current = null;
    }
    if (sessionEventsWatchdogTimerRef.current != null) {
      window.clearTimeout(sessionEventsWatchdogTimerRef.current);
      sessionEventsWatchdogTimerRef.current = null;
    }
  }, []);

  const stopSessionEventStreamInternal = useCallback(
    (options?: { abortCursorPreparation?: boolean }) => {
      const ports = portsRef.current;
      ports.flushPendingDeltas();
      clearSessionEventTimers();
      sessionEventsAbortRef.current?.abort();
      sessionEventsAbortRef.current = null;
      sseSessionIdRef.current = null;
      lastSseEventAtRef.current = null;
      reconnectCountRef.current = 0;
      setReconnectCount(0);
      if (options?.abortCursorPreparation) {
        // 停止（会话切换/卸载）时也取消正在进行的游标准备，避免迟到的 bootstrap 回写游标。
        connectionRequestRef.current.controller.abort();
        connectionRequestRef.current = {
          ...connectionRequestRef.current,
          sessionId: '',
          promise: null,
        };
      }
      ports.syncSessionIdentity();
    },
    [clearSessionEventTimers],
  );

  const stopSessionEventStream = useCallback(() => {
    stopSessionEventStreamInternal({ abortCursorPreparation: true });
  }, [stopSessionEventStreamInternal]);

  const startSessionEventStream = useCallback(
    (sessionId: string, options?: StartSessionEventStreamOptions): boolean => {
      if (!sessionId) return false;
      const ports = portsRef.current;

      // 唯一的开流起点决策：未就绪的会话不再退回 0（那会把整段历史当成实时帧回放）。
      const decision = decideSessionEventStreamStart({
        state: ports.sessionEventCursorRef.current,
        sessionId,
        explicitCursor: options?.cursor,
      });

      if (decision.kind !== 'open') {
        recordPerfEvent(
          'chat.sse.startRefused',
          {
            sessionId,
            decision: decision.kind,
            reason:
              decision.kind === 'refused' ? decision.reason : 'cursor-hydrating',
            cursorSessionId: ports.sessionEventCursorRef.current.sessionId,
            cursorPhase: ports.sessionEventCursorRef.current.phase,
          },
          { throttleMs: 1_000 },
        );
        logChatDiag('sse.start.deferredWithoutAuthoritativeCursor', {
          sessionId,
          decision: decision.kind,
          cursorSessionId: ports.sessionEventCursorRef.current.sessionId,
          cursorPhase: ports.sessionEventCursorRef.current.phase,
          lastSequenceNum: ports.lastSequenceNumRef.current,
          requestReason: options?.reason,
        });
        return false;
      }

      const previousStreamSessionId = sseSessionIdRef.current;
      const previousReconnectCount =
        previousStreamSessionId === sessionId ? reconnectCountRef.current : 0;
      stopSessionEventStreamInternal();
      reconnectCountRef.current = previousReconnectCount;
      setReconnectCount(previousReconnectCount);
      // A first connection is opened only after history/bootstrap has advanced
      // lastSequenceNumRef. Do not erase that authoritative cursor merely
      // because there was no previous SSE instance. Explicit session switches
      // already reset the cursor before loading the new session.
      if (previousStreamSessionId) {
        ports.resetStreamCursorForSessionChange(
          previousStreamSessionId,
          sessionId,
        );
      }
      sseSessionIdRef.current = sessionId;
      ports.syncSessionIdentity();
      // S3：游标必须显式。显式 0 ＝调用方有意全量回放（仅限服务端刚创建、日志为空的新会话）；
      // 省略＝使用该会话**已就绪**的权威游标（上面的决策已保证这一点）。
      const afterSequence = decision.afterSequence;
      recordPerfEvent('chat.sse.start', { sessionId });
      logChatDiag('sse.start', {
        sessionId,
        previousStreamSessionId,
        selectedSessionId: ports.selectedSessionIdRef.current,
        sessionIdRef: ports.sessionIdRef.current,
        lastSequenceNum: afterSequence,
        cursorSource: decision.cursorSource,
        cursorReason: options?.reason,
        activeMessageCount: ports.activeMessageIdsRef.current.size,
        turnCount: ports.turnsRef.current.length,
      });

      const controller = new AbortController();
      sessionEventsAbortRef.current = controller;

      const stopForAuthError = (status?: number) => {
        if (status !== 401 && status !== 403) return false;
        if (controller.signal.aborted || sseSessionIdRef.current !== sessionId)
          return true;
        logChatDiag('sse.authRequired', { sessionId, httpStatus: status });
        stopSessionEventStream();
        // Do not dispose the conversation or erase unsent text on an auth failure.
        if (status === 401) localStorage.removeItem('pudding_token');
        message.error(
          status === 401
            ? '登录已失效，请重新登录后继续。'
            : '无权访问此会话，已停止自动重连。',
        );
        return true;
      };
      const errorStatus = (error: unknown): number | undefined => {
        const value = error as {
          status?: number;
          response?: { status?: number };
        } | null;
        return value?.response?.status ?? value?.status;
      };

      const scheduleReconnect = () => {
        if (controller.signal.aborted || sseSessionIdRef.current !== sessionId)
          return;
        if (sessionEventsReconnectTimerRef.current != null) return;
        reconnectCountRef.current += 1;
        setReconnectCount(reconnectCountRef.current);
        recordPerfEvent('chat.sse.reconnectScheduled', {
          sessionId,
          attempt: reconnectCountRef.current,
        });
        sessionEventsReconnectTimerRef.current = window.setTimeout(
          async () => {
            sessionEventsReconnectTimerRef.current = null;
            if (sseSessionIdRef.current !== sessionId) return;
            try {
              await portsRef.current.replayMissedSessionEvents(
                sessionId,
                controller.signal,
              );
            } catch (error) {
              if (stopForAuthError(errorStatus(error))) return;
              // Retry through the next compensation cycle.
            }
            if (
              !controller.signal.aborted &&
              sseSessionIdRef.current === sessionId
            ) {
              // 游标可能已被清空（例如刚做过快照重置）：先重新准备，绝不退回 0。
              // 重连必须强制重建流：旧连接此时的「健康」只说明还没被 abort。
              if (!startSessionEventStream(sessionId)) {
                void ensureSessionEventStreamRef.current?.(sessionId, {
                  forceReconnect: true,
                });
              }
            }
          },
          Math.min(
            30_000,
            1200 * 2 ** Math.min(reconnectCountRef.current - 1, 5),
          ),
        );
      };

      const onOnline = () => scheduleReconnect();
      window.addEventListener('online', onOnline);

      const scheduleReplayPoll = () => {
        const currentPorts = portsRef.current;
        if (
          sseSessionIdRef.current !== sessionId ||
          controller.signal.aborted
        ) {
          return;
        }
        const hasActiveMessages = currentPorts.pruneTrackedActiveMessages(
          'replay-poll-schedule',
        );
        const delayMs = resolveSessionReplayPollInterval(hasActiveMessages);
        recordPerfEvent(
          'chat.replay.pollScheduled',
          {
            sessionId,
            delayMs,
            activeMessageCount: currentPorts.activeMessageIdsRef.current.size,
            activeTurn: hasActiveMessages,
          },
          { throttleMs: 2_000 },
        );
        sessionEventsPollTimerRef.current = window.setTimeout(async () => {
          sessionEventsPollTimerRef.current = null;
          if (
            sseSessionIdRef.current !== sessionId ||
            controller.signal.aborted
          ) {
            return;
          }
          const pollStartedAt = performance.now();
          let shouldContinue = true;
          const pollPorts = portsRef.current;
          try {
            const ranReplay = await pollPorts.replayMissedSessionEventsIfNeeded(
              sessionId,
              {
                signal: controller.signal,
                reason: 'poll',
                hasActiveMessages: pollPorts.pruneTrackedActiveMessages(
                  'replay-poll-execute',
                ),
              },
            );
            recordPerfEvent('chat.replay.pollComplete', {
              sessionId,
              delayMs,
              activeMessageCount: pollPorts.activeMessageIdsRef.current.size,
              skipped: !ranReplay,
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
          } catch (error) {
            recordPerfEvent('chat.replay.pollError', {
              sessionId,
              delayMs,
              activeMessageCount: pollPorts.activeMessageIdsRef.current.size,
              aborted: controller.signal.aborted,
              error: error instanceof Error ? error.message : String(error),
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
            if (stopForAuthError(errorStatus(error))) {
              shouldContinue = false;
            } else if (isSessionNotFoundError(error)) {
              logChatDiag('events.replay.sessionNotFound', {
                sessionId,
                error: error.message,
              });
              pollPorts.handleSessionNotFound(sessionId, 'replay-poll-404');
              shouldContinue = false;
            }
          } finally {
            if (
              shouldContinue &&
              !controller.signal.aborted &&
              sseSessionIdRef.current === sessionId
            ) {
              scheduleReplayPoll();
            }
          }
        }, delayMs);
      };
      scheduleReplayPoll();

      const scheduleWatchdog = () => {
        sessionEventsWatchdogTimerRef.current = window.setTimeout(() => {
          if (
            sseSessionIdRef.current !== sessionId ||
            controller.signal.aborted
          ) {
            return;
          }
          const lastEvent = lastSseEventAtRef.current;
          if (lastEvent && performance.now() - lastEvent > 90_000) {
            recordPerfEvent('chat.sse.watchdog', {
              sessionId,
              idleMs: Math.round(performance.now() - lastEvent),
            });
            scheduleReconnect();
            return;
          }
          scheduleWatchdog();
        }, 30_000);
      };
      scheduleWatchdog();

      try {
        subscribeSessionEvents(
          sessionId,
          (event) => {
            if (
              controller.signal.aborted ||
              sseSessionIdRef.current !== sessionId
            ) {
              return;
            }
            lastSseEventAtRef.current = performance.now();
            if (reconnectCountRef.current !== 0) {
              reconnectCountRef.current = 0;
              setReconnectCount(0);
            }
            const currentPorts = portsRef.current;
            const rawEvent = event as Record<string, unknown>;
            const messageId =
              typeof rawEvent.messageId === 'string'
                ? rawEvent.messageId
                : null;
            if (
              messageId &&
              !currentPorts.streamStartAtRef.current.has(messageId)
            ) {
              currentPorts.streamStartAtRef.current.set(
                messageId,
                performance.now(),
              );
              recordPerfEvent('chat.sse.firstEvent', {
                sessionId,
                messageId,
                eventType: event.type,
                sequenceNum: (event as { sequenceNum?: number }).sequenceNum,
              });
            }
            // 帧上的 replay 标记（后端 Phase 1 历史追赶）是权威的「非实时」信号：
            // 交给投影层按 replay 语义处理，不再靠时间/序号启发式猜测。
            currentPorts.applySessionEvent(event, {
              replay: (event as { replay?: boolean }).replay === true,
            });
          },
          controller.signal,
          {
            onError: (_error, httpStatus, code) => {
              if (
                controller.signal.aborted ||
                sseSessionIdRef.current !== sessionId
              ) {
                return;
              }
              const currentPorts = portsRef.current;
              if (stopForAuthError(httpStatus)) return;
              // snapshot_required 同样是 410，但语义是「游标之后的事件已不可读，
              // 重取快照即可继续」——**可恢复**，不是「会话消失」。
              // 必须优先判定，否则会被下面的终态分支误判为 not-found。
              if (code === 'snapshot_required') {
                logChatDiag('sse.snapshotRequired', { sessionId, httpStatus });
                recordPerfEvent('chat.sse.snapshotRequired', { sessionId });
                void (async () => {
                  try {
                    // 重取快照并**强制**重置游标（快照是新的权威起点）。
                    await currentPorts.syncCompletedHistoryEventCursor(
                      sessionId,
                      controller.signal,
                      { resetCursor: true },
                    );
                  } catch (error) {
                    recordPerfEvent('chat.sse.snapshotResyncFailed', {
                      sessionId,
                      error:
                        error instanceof Error ? error.message : String(error),
                    });
                  }
                  if (
                    !controller.signal.aborted &&
                    sseSessionIdRef.current === sessionId
                  ) {
                    scheduleReconnect();
                  }
                })();
                return;
              }
              if (httpStatus === 404 || httpStatus === 410) {
                logChatDiag('sse.sessionTerminal', { sessionId, httpStatus });
                currentPorts.handleSessionNotFound(
                  sessionId,
                  `sse-${httpStatus}`,
                );
                return;
              }
              logChatDiag('sse.error.reconnect', { sessionId, httpStatus });
              scheduleReconnect();
            },
            afterSequence,
          },
        );
      } catch {
        scheduleReconnect();
      }

      const originalAbort = controller.abort.bind(controller);
      controller.abort = () => {
        window.removeEventListener('online', onOnline);
        if (sessionEventsWatchdogTimerRef.current != null) {
          window.clearTimeout(sessionEventsWatchdogTimerRef.current);
          sessionEventsWatchdogTimerRef.current = null;
        }
        recordPerfEvent('chat.sse.stop', { sessionId });
        logChatDiag('sse.stop', {
          sessionId,
          selectedSessionId: portsRef.current.selectedSessionIdRef.current,
          sessionIdRef: portsRef.current.sessionIdRef.current,
        });
        originalAbort();
      };

      return true;
    },
    [stopSessionEventStreamInternal],
  );

  /** 该会话是否已经有一条健康的 SSE（可复用，不必重建）。 */
  const hasHealthySessionEventStream = useCallback(
    (sessionId: string): boolean => {
      const controller = sessionEventsAbortRef.current;
      return (
        !!controller &&
        !controller.signal.aborted &&
        sseSessionIdRef.current === sessionId
      );
    },
    [],
  );

  /**
   * 统一的开流入口（B1）：先保证该会话有**权威游标**，再开流。
   * <para>
   * - 投影会话（Agent 投影承担主消息）同样开流：帧在投影层按会话域过滤，
   *   只消费 `subagent.*`（诊断 2026-10-06：此处曾经直接跳开，子代理时间线因此失联）；
   * - 已有健康连接 ⇒ 复用（`ok=true, opened=false`）；
   * - 游标未就绪 ⇒ 通过 `syncCompletedHistoryEventCursor` 同步（同会话请求合并，generation+AbortSignal
   *   复检防止 A 的迟到 bootstrap 修改 B 的游标）；
   * - 准备失败 ⇒ `ok=false`：调用方**不得**退回 0 兜底。
   * </para>
   */
  const ensureSessionEventStream = useCallback(
    async (
      sessionId: string,
      options?: { forceReconnect?: boolean },
    ): Promise<SessionEventStreamEnsureResult> => {
      if (!sessionId) return { ok: false, opened: false, reason: 'stale-request' };

      const ports = portsRef.current;

      // 诊断（2026-10-06）：这里曾对 projection-owned 会话直接 `return`，于是
      // **子代理事实**也失去了唯一的实时通道 —— 主消息由 Agent 投影负责，但
      // `subagent.*` 没有第二条通道（ADR-060 禁止活动期轮询运行归档），界面只剩
      // 状态端点物化的占位（启动中 / 0 轮 / 0 工具 / 暂无运行事件）。
      // 现在投影会话同样开流：主消息的重复投影由 useSessionEventProjection 的
      // 「会话域」过滤掉（只消费 subagent.*），不再靠「不开流」来避免重复。
      if (ports.isProjectionOwnedSession(sessionId)) {
        recordPerfEvent(
          'chat.sse.ensureSubagentChannel',
          { sessionId, reason: 'projection-owned' },
          { throttleMs: 1_000 },
        );
      }

      // 重连必须重新建立流：此时旧连接可能仍在（只是收到了 503/超时），复用等于什么都不做。
      if (!options?.forceReconnect && hasHealthySessionEventStream(sessionId)) {
        return { ok: true, opened: false, reason: 'already-connected' };
      }

      const pending = connectionRequestRef.current;
      if (pending.sessionId === sessionId && pending.promise) {
        return pending.promise;
      }

      // 换会话：取消上一个准备请求，避免它的迟到结果回写。
      pending.controller.abort();

      const generation = pending.generation + 1;
      const controller = new AbortController();
      const request: ConnectionRequest = {
        sessionId,
        generation,
        controller,
        promise: null,
      };
      connectionRequestRef.current = request;

      const isCurrent = () =>
        connectionRequestRef.current === request && !controller.signal.aborted;

      const promise = (async (): Promise<SessionEventStreamEnsureResult> => {
        const currentPorts = portsRef.current;

        if (
          !isCursorReadyForSession(
            currentPorts.sessionEventCursorRef.current,
            sessionId,
          )
        ) {
          const prepared = await currentPorts.syncCompletedHistoryEventCursor(
            sessionId,
            controller.signal,
            { isCurrent },
          );

          if (!isCurrent()) {
            return { ok: false, opened: false, reason: 'stale-request' };
          }

          if (!prepared.ok) {
            recordPerfEvent(
              'chat.sse.cursorPreparationFailed',
              { sessionId, reason: prepared.reason },
              { throttleMs: 1_000 },
            );
            return {
              ok: false,
              opened: false,
              reason: 'cursor-preparation-failed',
            };
          }
        }

        if (
          !isCurrent() ||
          !isCursorReadyForSession(
            portsRef.current.sessionEventCursorRef.current,
            sessionId,
          )
        ) {
          return { ok: false, opened: false, reason: 'cursor-not-ready' };
        }

        const opened = startSessionEventStream(sessionId);
        // `opened=false` 现在只剩「游标在准备与开流之间被清掉」一种可能，
        // 不再是「这是投影会话所以不开流」。
        return {
          ok: true,
          opened,
          reason: opened ? undefined : 'cursor-not-ready',
        };
      })();

      request.promise = promise;
      return promise;
    },
    [hasHealthySessionEventStream, startSessionEventStream],
  );

  ensureSessionEventStreamRef.current = ensureSessionEventStream;

  return {
    sessionEventsAbortRef,
    sessionEventsPollTimerRef,
    sessionEventsReconnectTimerRef,
    sseSessionIdRef,
    lastSseEventAtRef,
    reconnectCountRef,
    reconnectCount,
    startSessionEventStream,
    ensureSessionEventStream,
    stopSessionEventStream,
    bindSessionEventConnection,
  };
}
