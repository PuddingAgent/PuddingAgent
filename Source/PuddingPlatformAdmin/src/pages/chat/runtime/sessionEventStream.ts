// ── Session Event Stream Runtime [EXPERIMENTAL] ─────────────────
// ADR-054 Step 1 scaffold. 尚未接入生产主链路，仅被测试引用。
// ────────────────────────────────────────────────────────────────
// 独立管理 SSE/replay poll/reconnect timer/AbortController 生命周期。
// 不直接调用 React setState，通过 callbacks 注入具体行为。

import type { AdminChatStreamEvent } from '@/services/platform/api';

// ── 类型定义 ────────────────────────────────────────────

export interface SessionEventStreamRuntime {
  /** 启动事件流（SSE + replay poll） */
  start(sessionId: string): void;
  /** 停止事件流，清理所有 timer 和 abort controller */
  stop(reason?: string): void;
  /** 获取当前流绑定的 sessionId */
  getCurrentSessionId(): string | null;
}

export interface SessionEventStreamRuntimeOptions {
  /** 订阅 session SSE 事件 */
  subscribeSessionEvents: (
    sessionId: string,
    onEvent: (ev: AdminChatStreamEvent) => void,
    signal: AbortSignal,
    opts?: { onError?: (error: Error, httpStatus?: number) => void },
  ) => void;
  /** 补偿拉取 missed events */
  replayMissedEvents(sessionId: string, signal: AbortSignal): Promise<boolean>;
  /** 将 SSE 事件应用到状态 */
  applySessionEvent(event: AdminChatStreamEvent): void;
  /** 会话不再存在的终态处理 */
  onTerminal(sessionId: string, status: 404 | 410): void;
  /** 诊断/性能事件记录（可选） */
  onDiag?(name: string, payload: Record<string, unknown>): void;
}

interface StreamInternals {
  sessionId: string | null;
  abortController: AbortController | null;
  pollTimer: ReturnType<typeof setTimeout> | null;
  reconnectTimer: ReturnType<typeof setTimeout> | null;
  onlineHandler: (() => void) | null;
  stopped: boolean;
}

// ── 默认配置 ────────────────────────────────────────────

const RECONNECT_DELAY_MS = 1200;
const POLL_ACTIVE_INTERVAL_MS = 2000;
const POLL_IDLE_INTERVAL_MS = 8000;

// ── 工厂函数 ────────────────────────────────────────────

export function createSessionEventStreamRuntime(
  options: SessionEventStreamRuntimeOptions,
): SessionEventStreamRuntime {
  const {
    subscribeSessionEvents,
    replayMissedEvents,
    applySessionEvent,
    onTerminal,
    onDiag,
  } = options;

  const internals: StreamInternals = {
    sessionId: null,
    abortController: null,
    pollTimer: null,
    reconnectTimer: null,
    onlineHandler: null,
    stopped: false,
  };

  // ── 内部工具函数 ──────────────────────────────────────

  function diag(name: string, payload: Record<string, unknown>): void {
    onDiag?.(name, payload);
  }

  function clearAllTimers(): void {
    if (internals.pollTimer != null) {
      clearTimeout(internals.pollTimer);
      internals.pollTimer = null;
    }
    if (internals.reconnectTimer != null) {
      clearTimeout(internals.reconnectTimer);
      internals.reconnectTimer = null;
    }
  }

  function clearAll(): void {
    clearAllTimers();

    if (internals.onlineHandler) {
      if (typeof window !== 'undefined') {
        window.removeEventListener('online', internals.onlineHandler);
      }
      internals.onlineHandler = null;
    }

    if (internals.abortController) {
      internals.abortController.abort();
      internals.abortController = null;
    }

    internals.sessionId = null;
  }

  /** 检查当前流是否仍然有效 */
  function isCurrentSession(sessionId: string): boolean {
    return (
      !internals.stopped &&
      internals.sessionId === sessionId &&
      internals.abortController != null &&
      !internals.abortController.signal.aborted
    );
  }

  // ── Replay Poll ──────────────────────────────────────

  function scheduleReplayPoll(sessionId: string, ctrl: AbortController): void {
    if (!isCurrentSession(sessionId)) return;

    // 简化：使用固定间隔（完整版需根据 active message 状态动态调整）
    const delayMs = POLL_IDLE_INTERVAL_MS;

    diag('stream.poll.scheduled', { sessionId, delayMs });

    internals.pollTimer = setTimeout(async () => {
      internals.pollTimer = null;
      if (!isCurrentSession(sessionId)) return;

      let shouldContinue = true;
      try {
        const ranReplay = await replayMissedEvents(sessionId, ctrl.signal);
        diag('stream.poll.complete', {
          sessionId,
          ranReplay,
        });
      } catch (error) {
        diag('stream.poll.error', {
          sessionId,
          error: error instanceof Error ? error.message : String(error),
        });
        // 404/410 → 终态：通知外部并停止 runtime 自身
        const httpStatus = (error as { status?: number }).status;
        const terminalStatus = httpStatus === 410 ? 410 : 404;
        shouldContinue = false;
        if (isCurrentSession(sessionId)) {
          onTerminal(sessionId, terminalStatus);
          stop(`replay-poll-${terminalStatus}`);
        }
      }

      if (shouldContinue && isCurrentSession(sessionId)) {
        scheduleReplayPoll(sessionId, ctrl);
      }
    }, delayMs);
  }

  // ── Reconnect ────────────────────────────────────────

  function scheduleReconnect(sessionId: string): void {
    if (!isCurrentSession(sessionId)) return;
    if (internals.reconnectTimer != null) return; // 已有 pending reconnect

    diag('stream.reconnect.scheduled', { sessionId });

    internals.reconnectTimer = setTimeout(async () => {
      internals.reconnectTimer = null;
      if (!isCurrentSession(sessionId)) return;

      try {
        await replayMissedEvents(sessionId, internals.abortController!.signal);
      } catch {
        // 网络波动，静默忽略
      }

      if (isCurrentSession(sessionId)) {
        // 重新启动 SSE stream
        startSseStream(sessionId, internals.abortController!);
      }
    }, RECONNECT_DELAY_MS);
  }

  // ── SSE 流启动 ──────────────────────────────────────

  function startSseStream(sessionId: string, ctrl: AbortController): void {
    if (!isCurrentSession(sessionId)) return;

    diag('stream.sse.start', { sessionId });

    // 设置 online 事件监听
    if (!internals.onlineHandler) {
      internals.onlineHandler = () => {
        if (isCurrentSession(sessionId)) {
          scheduleReconnect(sessionId);
        }
      };
      if (typeof window !== 'undefined') {
        window.addEventListener('online', internals.onlineHandler);
      }
    }

    try {
      subscribeSessionEvents(
        sessionId,
        (ev) => {
          if (!isCurrentSession(sessionId)) return;
          applySessionEvent(ev);
        },
        ctrl.signal,
        {
          onError: (_error, httpStatus) => {
            if (!isCurrentSession(sessionId)) return;

            if (httpStatus === 404 || httpStatus === 410) {
              diag('stream.sse.terminal', { sessionId, httpStatus });
              onTerminal(sessionId, httpStatus as 404 | 410);
              stop(`sse-${httpStatus}`);
              return;
            }

            // 非终态错误 → 调度重连
            diag('stream.sse.error', { sessionId, httpStatus });
            scheduleReconnect(sessionId);
          },
        },
      );
    } catch {
      scheduleReconnect(sessionId);
    }
  }

  // ── 公开 API ─────────────────────────────────────────

  function start(sessionId: string): void {
    // 停止当前流
    stop(`start(${sessionId})`);

    internals.stopped = false;
    internals.sessionId = sessionId;
    internals.abortController = new AbortController();

    diag('stream.start', { sessionId });

    // 启动 replay poll 和 SSE
    scheduleReplayPoll(sessionId, internals.abortController);
    startSseStream(sessionId, internals.abortController);
  }

  function stop(reason?: string): void {
    diag('stream.stop', {
      sessionId: internals.sessionId ?? null,
      reason: reason ?? 'unknown',
    });
    clearAll();
    internals.stopped = true;
  }

  function getCurrentSessionId(): string | null {
    return internals.sessionId;
  }

  return { start, stop, getCurrentSessionId };
}
