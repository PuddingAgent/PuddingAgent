import { message } from 'antd';
import { act, renderHook } from '@testing-library/react';
import { subscribeSessionEvents } from '@/services/platform/api';
import { useSessionEventConnection } from './useSessionEventConnection';

jest.mock('@/services/platform/api', () => ({
  subscribeSessionEvents: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  recordPerfEvent: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

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
        syncCompletedHistoryEventCursor: jest.fn(async () => undefined),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 9865 },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-a' },
        sessionIdRef: { current: 'session-a' },
        turnsRef: { current: [] },
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

  it('honours an explicit cursor over the authoritative ref', () => {
    const { result, unmount } = renderHook(() => useSessionEventConnection());

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange: jest.fn(),
        syncCompletedHistoryEventCursor: jest.fn(async () => undefined),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 9865 },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-new' },
        sessionIdRef: { current: 'session-new' },
        turnsRef: { current: [] },
      });
      // S3：显式 0 ＝「有意全量回放」（刚创建的新会话），必须压过 ref 里的旧游标。
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
    act(() => result.current.startSessionEventStream('session-1'));
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
    const syncCompletedHistoryEventCursor = jest.fn(async () => undefined);

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
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-1' },
        sessionIdRef: { current: 'session-1' },
        turnsRef: { current: [] },
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
        syncCompletedHistoryEventCursor: jest.fn(async () => undefined),
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 0 },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-1' },
        sessionIdRef: { current: 'session-1' },
        turnsRef: { current: [] },
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
    act(() => result.current.startSessionEventStream('session-1'));
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
