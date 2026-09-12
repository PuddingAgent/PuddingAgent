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
