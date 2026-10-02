import { message } from 'antd';
import { act, renderHook } from '@testing-library/react';
import { subscribeSessionEvents } from '@/services/platform/api';
import { useSessionEventConnection } from './useSessionEventConnection';
import {
  createSessionEventCursorState,
  markSessionEventCursorReady,
} from './sessionEventCursor';

jest.mock('@/services/platform/api', () => ({
  subscribeSessionEvents: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  recordPerfEvent: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

/** 结构性最小接口：本文件只用绑定端口与 ensure 两个入口。 */
interface ConnectionHookView {
  // 端口对象的完整类型留在实现模块内；这里用 never 形参保持逆变可赋值且不引入类型导入。
  bindSessionEventConnection: (ports: never) => void;
  ensureSessionEventStream: (sessionId: string) => Promise<{
    ok: boolean;
    opened: boolean;
    reason?: string;
  }>;
}

const readyCursor = (sessionId: string, sequence: number) => {
  const state = createSessionEventCursorState();
  markSessionEventCursorReady(state, sessionId, sequence);
  return { current: state };
};

describe('useSessionEventConnection', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('starts the first SSE connection from the cursor synchronized by history', () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const resetStreamCursorForSessionChange = jest.fn();

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange,
        syncCompletedHistoryEventCursor: jest.fn(async () => ({
          ok: true as const,
          cursor: 9865,
          turns: [],
        })),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 9865 },
        sessionEventCursorRef: readyCursor('session-a', 9865),
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-a' },
        sessionIdRef: { current: 'session-a' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });
      result.current.startSessionEventStream('session-a');
    });

    expect(resetStreamCursorForSessionChange).not.toHaveBeenCalled();
    expect(subscribeSessionEvents).toHaveBeenCalledWith(
      'session-a',
      expect.any(Function),
      expect.any(AbortSignal),
      expect.objectContaining({ afterSequence: 9865 }),
    );

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('refuses to open a stream for a session whose cursor is not ready', () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor: jest.fn(async () => ({
          ok: true as const,
          cursor: 0,
          turns: [],
        })),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        // 数值是 0 且从未同步过：这是「未初始化」，不是「权威 0」。
        lastSequenceNumRef: { current: 0 },
        sessionEventCursorRef: { current: createSessionEventCursorState() },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-a' },
        sessionIdRef: { current: 'session-a' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });

      // 旧行为会以 afterSequence=0 打开整段历史回放；现在必须拒绝。
      expect(result.current.startSessionEventStream('session-a')).toBe(false);
    });

    expect(subscribeSessionEvents).not.toHaveBeenCalled();
    expect(result.current.sseSessionIdRef.current).toBeNull();

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('refuses to carry another session cursor over to a session switch', () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor: jest.fn(async () => ({
          ok: true as const,
          cursor: 0,
          turns: [],
        })),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 400 },
        // A 的游标是 ready，但当前要开的是 B：绝不能把 A 的 400 带给 B。
        sessionEventCursorRef: readyCursor('session-a', 400),
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-b' },
        sessionIdRef: { current: 'session-b' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });

      expect(result.current.startSessionEventStream('session-b')).toBe(false);
    });

    expect(subscribeSessionEvents).not.toHaveBeenCalled();

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('honours an explicit cursor over the authoritative state', () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor: jest.fn(async () => ({
          ok: true as const,
          cursor: 0,
          turns: [],
        })),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 9865 },
        sessionEventCursorRef: readyCursor('session-old', 9865),
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-new' },
        sessionIdRef: { current: 'session-new' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });
      // S3：显式 0 ＝「有意全量回放」（服务端刚创建、日志为空的新会话）。
      result.current.startSessionEventStream('session-new', {
        cursor: 0,
        reason: 'compaction-successor',
      });
    });

    expect(subscribeSessionEvents).toHaveBeenCalledWith(
      'session-new',
      expect.any(Function),
      expect.any(AbortSignal),
      expect.objectContaining({ afterSequence: 0 }),
    );

    act(() => result.current.stopSessionEventStream());
    unmount();
  });
});

describe('useSessionEventConnection ensureSessionEventStream', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  const bindPorts = (
    result: { current: ConnectionHookView },
    overrides: Record<string, unknown> = {},
  ) => {
    const ports: Record<string, unknown> = {
      applySessionEvent: jest.fn(),
      handleSessionNotFound: jest.fn(),
      pruneTrackedActiveMessages: jest.fn(() => false),
      replayMissedSessionEvents: jest.fn(async () => {}),
      replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
      resetStreamCursorForSessionChange: jest.fn(),
      syncCompletedHistoryEventCursor: jest.fn(async () => ({
        ok: true as const,
        cursor: 0,
        turns: [],
      })),
      flushPendingDeltas: jest.fn(),
      syncSessionIdentity: jest.fn(),
      activeMessageIdsRef: { current: new Set<string>() },
      lastSequenceNumRef: { current: 0 },
      sessionEventCursorRef: { current: createSessionEventCursorState() },
      streamStartAtRef: { current: new Map<string, number>() },
      selectedSessionIdRef: { current: null },
      sessionIdRef: { current: undefined },
      turnsRef: { current: [] },
      isProjectionOwnedSession: () => false,
      ...overrides,
    };
    act(() => {
      result.current.bindSessionEventConnection(ports as never);
    });
    return ports;
  };

  it('prepares the cursor before opening, then subscribes from the prepared snapshot cursor', async () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const cursorRef = { current: createSessionEventCursorState() };
    const syncCompletedHistoryEventCursor = jest.fn(
      async (sessionId: string) => {
        // 真实 replay hook 的行为：成功时把该会话标为 ready。
        markSessionEventCursorReady(cursorRef.current, sessionId, 1644341);
        return { ok: true as const, cursor: 1644341, turns: [] };
      },
    );

    bindPorts(result, {
      sessionEventCursorRef: cursorRef,
      syncCompletedHistoryEventCursor,
    });

    let ensureResult: { ok: boolean; opened: boolean } | undefined;
    await act(async () => {
      ensureResult = await result.current.ensureSessionEventStream('session-long');
    });

    expect(syncCompletedHistoryEventCursor).toHaveBeenCalledTimes(1);
    expect(ensureResult).toEqual({ ok: true, opened: true, reason: undefined });
    // 核心断言：订阅用的是 bootstrap 的权威游标，而不是 0。
    expect(subscribeSessionEvents).toHaveBeenCalledWith(
      'session-long',
      expect.any(Function),
      expect.any(AbortSignal),
      expect.objectContaining({ afterSequence: 1644341 }),
    );

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('does not open with a 0 fallback when cursor preparation fails', async () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const cursorRef = { current: createSessionEventCursorState() };
    const syncCompletedHistoryEventCursor = jest.fn(async () => ({
      ok: false as const,
      reason: 'failed' as const,
    }));

    bindPorts(result, {
      sessionEventCursorRef: cursorRef,
      syncCompletedHistoryEventCursor,
    });

    let ensureResult: { ok: boolean; opened: boolean } | undefined;
    await act(async () => {
      ensureResult = await result.current.ensureSessionEventStream('session-long');
    });

    expect(ensureResult).toEqual({
      ok: false,
      opened: false,
      reason: 'cursor-preparation-failed',
    });
    expect(subscribeSessionEvents).not.toHaveBeenCalled();

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('merges concurrent same-session preparations into one bootstrap request', async () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const cursorRef = { current: createSessionEventCursorState() };
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const syncCompletedHistoryEventCursor = jest.fn(async (sessionId: string) => {
      await gate;
      markSessionEventCursorReady(cursorRef.current, sessionId, 1234);
      return { ok: true as const, cursor: 1234, turns: [] };
    });

    bindPorts(result, {
      sessionEventCursorRef: cursorRef,
      syncCompletedHistoryEventCursor,
    });

    const first = result.current.ensureSessionEventStream('session-long');
    const second = result.current.ensureSessionEventStream('session-long');
    release?.();
    await act(async () => {
      await Promise.all([first, second]);
    });

    expect(syncCompletedHistoryEventCursor).toHaveBeenCalledTimes(1);
    expect(subscribeSessionEvents).toHaveBeenCalledTimes(1);

    act(() => result.current.stopSessionEventStream());
    unmount();
  });

  it('never opens a raw SSE for a projection-owned session and still reports ok', async () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const cursorRef = { current: createSessionEventCursorState() };
    const syncCompletedHistoryEventCursor = jest.fn(async () => ({
      ok: true as const,
      cursor: 0,
      turns: [],
    }));

    bindPorts(result, {
      sessionEventCursorRef: cursorRef,
      syncCompletedHistoryEventCursor,
      isProjectionOwnedSession: (sessionId: string) =>
        sessionId === 'session-projected',
    });

    let ensureResult: { ok: boolean; opened: boolean; reason?: string } | undefined;
    await act(async () => {
      ensureResult = await result.current.ensureSessionEventStream(
        'session-projected',
      );
    });

    expect(ensureResult).toEqual({
      ok: true,
      opened: false,
      reason: 'projection-owned',
    });
    expect(syncCompletedHistoryEventCursor).not.toHaveBeenCalled();
    expect(subscribeSessionEvents).not.toHaveBeenCalled();
    unmount();
  });

  it('does not let a late bootstrap of session A open A nor overwrite session B', async () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());
    const cursorRef = { current: createSessionEventCursorState() };
    const pending: Array<{ sessionId: string; resolve: () => void }> = [];
    const syncCompletedHistoryEventCursor = jest.fn(
      (
        sessionId: string,
        _signal?: AbortSignal,
        options?: { isCurrent?: () => boolean },
      ) =>
        new Promise<{
          ok: boolean;
          reason?: string;
          cursor?: number;
          turns?: unknown[];
        }>((resolve) => {
            pending.push({
              sessionId,
              resolve: () => {
                // 模拟真实实现：请求过期就既不写游标，也不返回成功。
                if (options?.isCurrent && !options.isCurrent()) {
                  resolve({ ok: false, reason: 'stale-session' });
                  return;
                }
                markSessionEventCursorReady(
                  cursorRef.current,
                  sessionId,
                  sessionId === 'session-a' ? 111 : 222,
                );
                resolve({
                  ok: true,
                  cursor: sessionId === 'session-a' ? 111 : 222,
                  turns: [],
                });
              },
            });
          }),
    );

    bindPorts(result, {
      sessionEventCursorRef: cursorRef,
      syncCompletedHistoryEventCursor,
    });

    const ensureA = result.current.ensureSessionEventStream('session-a');
    const ensureB = result.current.ensureSessionEventStream('session-b');

    // A 的 bootstrap 迟到：必须被丢弃，且不得把 A 的游标写给 B。
    pending.find((item) => item.sessionId === 'session-a')?.resolve();
    await act(async () => {
      await ensureA;
    });
    expect(subscribeSessionEvents).not.toHaveBeenCalled();
    // A 的迟到 bootstrap 不得把 A 写进游标（B 才是当前会话）。
    expect(cursorRef.current.sessionId).not.toBe('session-a');
    expect(cursorRef.current.phase).toBe('unknown');

    pending.find((item) => item.sessionId === 'session-b')?.resolve();
    await act(async () => {
      await ensureB;
    });

    expect(subscribeSessionEvents).toHaveBeenCalledTimes(1);
    expect(subscribeSessionEvents).toHaveBeenCalledWith(
      'session-b',
      expect.any(Function),
      expect.any(AbortSignal),
      expect.objectContaining({ afterSequence: 222 }),
    );

    act(() => result.current.stopSessionEventStream());
    unmount();
  });
});

jest.mock('antd', () => ({ message: { error: jest.fn() } }));

describe('session event connection recovery', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.clearAllMocks();
  });
  afterEach(() => {
    jest.clearAllTimers();
    jest.useRealTimers();
  });

  it.each([
    401, 403,
  ])('stops all connection timers on %s without forgetting the conversation', async (status) => {
    const { result } = renderHook(() => useSessionEventConnection());
    act(() => result.current.startSessionEventStream('session-1', { cursor: 120, reason: 'test' }));
    const call = (subscribeSessionEvents as jest.Mock).mock.calls[0];
    act(() => call[3].onError(new Error('auth'), status));
    expect(call[2].aborted).toBe(true);
    expect(result.current.sessionEventsPollTimerRef.current).toBeNull();
    expect(result.current.sessionEventsReconnectTimerRef.current).toBeNull();
    expect(result.current.reconnectCount).toBe(0);
    expect(message.error).toHaveBeenCalledTimes(1);
    window.dispatchEvent(new Event('online'));
    await act(async () => {
      await jest.advanceTimersByTimeAsync(120_000);
    });
    expect(subscribeSessionEvents).toHaveBeenCalledTimes(1);
  });

  it('recovers from 410 snapshot_required by resyncing the snapshot instead of treating the session as gone', async () => {
    const { result } = renderHook(() => useSessionEventConnection());
    const handleSessionNotFound = jest.fn();
    const cursorRef = { current: createSessionEventCursorState() };
    // 真实 replay hook 成功后会标记 ready；重连走的统一入口依赖这一点。
    const syncCompletedHistoryEventCursor = jest.fn(
      async (
        sessionId: string,
        _signal?: AbortSignal,
        options?: { resetCursor?: boolean },
      ) => {
        const cursor = options?.resetCursor ? 130 : 120;
        markSessionEventCursorReady(cursorRef.current, sessionId, cursor);
        return { ok: true as const, cursor, turns: [] };
      },
    );

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound,
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor,
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 0 },
        sessionEventCursorRef: cursorRef,
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-1' },
        sessionIdRef: { current: 'session-1' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });
      result.current.startSessionEventStream('session-1', {
        cursor: 120,
        reason: 'test',
      });
    });

    const call = (subscribeSessionEvents as jest.Mock).mock.calls[0];
    act(() =>
      call[3].onError(
        new Error('SSE stream failed: HTTP 410'),
        410,
        'snapshot_required',
      ),
    );

    // 关键区分：可恢复的服务端指示不得被当成「会话消失」。
    expect(handleSessionNotFound).not.toHaveBeenCalled();

    await act(async () => {
      await Promise.resolve();
    });
    expect(syncCompletedHistoryEventCursor).toHaveBeenCalledWith(
      'session-1',
      expect.anything(),
      { resetCursor: true },
    );

    // 快照同步完成后仍应安排重连（新游标）。
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1200);
    });
    expect((subscribeSessionEvents as jest.Mock).mock.calls.length).toBeGreaterThan(1);

    act(() => result.current.stopSessionEventStream());
  });

  it('treats a terminal 410 without a code as session-gone', async () => {
    const { result } = renderHook(() => useSessionEventConnection());
    const handleSessionNotFound = jest.fn();

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound,
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor: jest.fn(async () => ({ ok: true as const, cursor: 0, turns: [] })),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 0 },
        sessionEventCursorRef: { current: createSessionEventCursorState() },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-1' },
        sessionIdRef: { current: 'session-1' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });
      result.current.startSessionEventStream('session-1', {
        cursor: 120,
        reason: 'test',
      });
    });

    const call = (subscribeSessionEvents as jest.Mock).mock.calls[0];
    act(() =>
      call[3].onError(
        new Error('SSE stream failed: HTTP 410'),
        410,
        'conversation_frozen',
      ),
    );

    expect(handleSessionNotFound).toHaveBeenCalledWith('session-1', 'sse-410');
  });

  it('backs off consecutive transient errors and resets only after an event', async () => {
    const { result } = renderHook(() => useSessionEventConnection());
    const backoffCursorRef = { current: createSessionEventCursorState() };
    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        // 重连走统一入口：真实 replay hook 会在这里把游标重新标为 ready。
        syncCompletedHistoryEventCursor: jest.fn(async (sessionId: string) => {
          markSessionEventCursorReady(backoffCursorRef.current, sessionId, 120);
          return { ok: true as const, cursor: 120, turns: [] };
        }),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 0 },
        sessionEventCursorRef: backoffCursorRef,
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-1' },
        sessionIdRef: { current: 'session-1' },
        turnsRef: { current: [] },
        isProjectionOwnedSession: () => false,
      });
      result.current.startSessionEventStream('session-1', {
        cursor: 120,
        reason: 'test',
      });
    });
    const calls = (subscribeSessionEvents as jest.Mock).mock.calls;
    act(() => calls[0][3].onError(new Error('network'), 503));
    expect(result.current.reconnectCount).toBe(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1200);
    });
    expect(calls).toHaveLength(2);
    act(() => calls[1][3].onError(new Error('network'), 503));
    expect(result.current.reconnectCount).toBe(2);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1200);
    });
    expect(calls).toHaveLength(2);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1200);
    });
    expect(calls).toHaveLength(3);
    act(() => calls[2][1]({ type: 'heartbeat' }));
    expect(result.current.reconnectCountRef.current).toBe(0);
    expect(result.current.reconnectCount).toBe(0);
    act(() => result.current.stopSessionEventStream());
  });
});
