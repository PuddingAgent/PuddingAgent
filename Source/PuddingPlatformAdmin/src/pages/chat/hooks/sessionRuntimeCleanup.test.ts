// ── P0 v2: sessionRuntimeCleanup 单元测试 ──────────────────────────
// 验证有作用域的清理函数不会误伤非当前 session。

import type { SessionRuntimeRefs } from './sessionRuntimeCleanup';
import {
  clearDeletedSessionReferences,
  disposeCurrentSessionRuntime,
  isSessionNotFoundError,
  SessionNotFoundError,
} from './sessionRuntimeCleanup';

/** 创建一个干净的 mock refs 对象，用于独立测试 */
function createMockRefs(
  overrides: Partial<SessionRuntimeRefs> = {},
): SessionRuntimeRefs {
  return {
    sessionIdRef: { current: undefined },
    sseSessionIdRef: { current: null },
    lastSequenceNumRef: { current: 0 },
    messageIdToTurnIdRef: { current: new Map() },
    activeMessageIdsRef: { current: new Set() },
    projectionOwnedSessionIdsRef: { current: new Set() },
    turnsRef: { current: [] },
    completedTurnsRef: { current: new Set() },
    latestTurnIdRef: { current: null },
    forceNewSessionRef: { current: false },
    sessionEventsAbortRef: { current: null },
    sessionEventsPollTimerRef: { current: null },
    sessionEventsReconnectTimerRef: { current: null },
    deltaFlushTimerRef: { current: null },
    thinkingFlushTimerRef: { current: null },
    pendingDeltaRef: { current: new Map() },
    pendingThinkingRef: { current: new Map() },
    streamStartAtRef: { current: new Map() },
    messageIdToAgentIdsRef: { current: new Map() },
    duplicateDeltaReplayOffsetRef: { current: new Map() },
    eventCountsRef: { current: new Map() },
    ...overrides,
  };
}

describe('SessionNotFoundError', () => {
  it('isSessionNotFoundError returns true for SessionNotFoundError', () => {
    const err = new SessionNotFoundError('s1', 'test');
    expect(isSessionNotFoundError(err)).toBe(true);
    expect(err.sessionId).toBe('s1');
    expect(err.message).toContain('s1');
  });

  it('isSessionNotFoundError returns false for generic Error', () => {
    expect(isSessionNotFoundError(new Error('generic'))).toBe(false);
  });

  it('isSessionNotFoundError returns false for non-error values', () => {
    expect(isSessionNotFoundError(null)).toBe(false);
    expect(isSessionNotFoundError('404')).toBe(false);
  });
});

describe('disposeCurrentSessionRuntime', () => {
  it('aborts SSE and clears active refs when sessionId matches sseSessionIdRef', () => {
    const sessionId = 'session-a';
    const abortSpy = jest.fn();
    const refs = createMockRefs({
      sseSessionIdRef: { current: sessionId },
      sessionIdRef: { current: sessionId },
      sessionEventsAbortRef: {
        current: { abort: abortSpy } as unknown as AbortController,
      },
      lastSequenceNumRef: { current: 99 },
      activeMessageIdsRef: { current: new Set(['msg-1', 'msg-2']) },
      messageIdToTurnIdRef: { current: new Map([['msg-1', 'turn-1']]) },
      projectionOwnedSessionIdsRef: { current: new Set([sessionId]) },
      turnsRef: { current: [{ turnId: 'turn-1' }] },
    });

    // Add a timer so we can verify it's cleared
    const clearTimeoutSpy = jest.spyOn(window, 'clearTimeout');

    const result = disposeCurrentSessionRuntime(sessionId, refs, 'test');

    expect(result).toBe(true);
    expect(abortSpy).toHaveBeenCalled();
    expect(refs.activeMessageIdsRef.current.size).toBe(0);
    expect(refs.messageIdToTurnIdRef.current.size).toBe(0);
    expect(refs.sseSessionIdRef.current).toBeNull();
    expect(refs.sessionIdRef.current).toBeUndefined();
    expect(refs.lastSequenceNumRef.current).toBe(0);
    expect(refs.projectionOwnedSessionIdsRef.current.has(sessionId)).toBe(
      false,
    );
    expect(refs.turnsRef.current).toEqual([]);
    expect(refs.sessionEventsAbortRef.current).toBeNull();

    clearTimeoutSpy.mockRestore();
  });

  it('does NOT abort SSE when sessionId matches sessionIdRef but NOT sseSessionIdRef', () => {
    const sessionId = 'session-a';
    const abortSpy = jest.fn();
    const refs = createMockRefs({
      sseSessionIdRef: { current: 'session-b' }, // different session on SSE
      sessionIdRef: { current: sessionId },
      sessionEventsAbortRef: {
        current: { abort: abortSpy } as unknown as AbortController,
      },
      projectionOwnedSessionIdsRef: { current: new Set([sessionId]) },
    });

    const result = disposeCurrentSessionRuntime(sessionId, refs, 'test');

    // P0 v2 修正：只有 sessionIdRef 匹配时不应 abort 当前 SSE
    expect(result).toBe(false);
    expect(abortSpy).not.toHaveBeenCalled();
    expect(refs.sessionIdRef.current).toBeUndefined();
    expect(refs.projectionOwnedSessionIdsRef.current.has(sessionId)).toBe(
      false,
    );
    // SSE unchanged because it's a different session
    expect(refs.sseSessionIdRef.current).toBe('session-b');
  });

  it('does NOT abort SSE when sessionId matches NEITHER sseSessionIdRef nor sessionIdRef', () => {
    const sessionId = 'session-a';
    const abortSpy = jest.fn();
    const refs = createMockRefs({
      sseSessionIdRef: { current: 'session-b' },
      sessionIdRef: { current: 'session-b' },
      sessionEventsAbortRef: {
        current: { abort: abortSpy } as unknown as AbortController,
      },
      activeMessageIdsRef: { current: new Set(['msg-1']) },
    });

    const result = disposeCurrentSessionRuntime(sessionId, refs, 'test');

    // P0 v2 修正：非当前 session 不应 abort
    expect(result).toBe(false);
    expect(abortSpy).not.toHaveBeenCalled();
    expect(refs.activeMessageIdsRef.current.size).toBe(1); // unchanged
    expect(refs.sseSessionIdRef.current).toBe('session-b'); // unchanged
  });

  it('clears timers when disposing current session', () => {
    const sessionId = 'session-a';
    const clearTimeoutSpy = jest.spyOn(window, 'clearTimeout');
    const refs = createMockRefs({
      sseSessionIdRef: { current: sessionId },
      sessionEventsPollTimerRef: { current: 123 },
      sessionEventsReconnectTimerRef: { current: 456 },
    });

    disposeCurrentSessionRuntime(sessionId, refs, 'test');

    expect(clearTimeoutSpy).toHaveBeenCalledWith(123);
    expect(clearTimeoutSpy).toHaveBeenCalledWith(456);
    expect(refs.sessionEventsPollTimerRef.current).toBeNull();
    expect(refs.sessionEventsReconnectTimerRef.current).toBeNull();

    clearTimeoutSpy.mockRestore();
  });
});

describe('clearDeletedSessionReferences', () => {
  it('removes sessionId from projectionOwned without touching SSE/active refs', () => {
    const sessionId = 'session-a';
    const refs = createMockRefs({
      sseSessionIdRef: { current: 'session-b' },
      projectionOwnedSessionIdsRef: {
        current: new Set([sessionId, 'session-c']),
      },
      activeMessageIdsRef: { current: new Set(['msg-1']) },
    });

    clearDeletedSessionReferences(sessionId, refs);

    // Should remove from projectionOwned
    expect(refs.projectionOwnedSessionIdsRef.current.has(sessionId)).toBe(
      false,
    );
    expect(refs.projectionOwnedSessionIdsRef.current.has('session-c')).toBe(
      true,
    );

    // Should NOT touch SSE or active messages
    expect(refs.sseSessionIdRef.current).toBe('session-b');
    expect(refs.activeMessageIdsRef.current.size).toBe(1);
  });

  it('clears sessionIdRef if it matches the deleted session', () => {
    const sessionId = 'session-a';
    const refs = createMockRefs({
      sessionIdRef: { current: sessionId },
      sseSessionIdRef: { current: 'session-b' }, // different stream
    });

    clearDeletedSessionReferences(sessionId, refs);

    expect(refs.sessionIdRef.current).toBeUndefined();
    // SSE unchanged because it's a different session
    expect(refs.sseSessionIdRef.current).toBe('session-b');
  });
});
