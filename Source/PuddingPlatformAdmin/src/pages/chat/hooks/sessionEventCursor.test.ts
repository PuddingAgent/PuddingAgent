import {
  advanceSessionEventCursor,
  beginSessionEventCursorHydration,
  createSessionEventCursorState,
  decideSessionEventStreamStart,
  invalidateSessionEventCursor,
  isCursorReadyForSession,
  markSessionEventCursorFailed,
  markSessionEventCursorReady,
} from './sessionEventCursor';

/**
 * B1 的纯逻辑门禁：游标状态机与开流起点决策。
 *
 * 核心回归：`sequence=0` + `phase!=='ready'` 不得开流（旧实现把它当成「从 0 全量回放」，
 * 长会话因此每次发送消息都重放整段历史）。
 */
describe('sessionEventCursor', () => {
  it('starts unknown so a bare zero can never be mistaken for an authoritative cursor', () => {
    const state = createSessionEventCursorState();

    expect(state).toEqual({ sessionId: null, phase: 'unknown', sequence: 0 });
    expect(isCursorReadyForSession(state, 'session-a')).toBe(false);
    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-a' }),
    ).toEqual({ kind: 'refused', reason: 'cursor-unknown-for-session' });
  });

  it('opens from the authoritative sequence only after the snapshot was applied', () => {
    const state = createSessionEventCursorState();

    beginSessionEventCursorHydration(state, 'session-a');
    // 同步中：既不能开流，也不能把数字当权威值。
    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-a' }),
    ).toEqual({ kind: 'needs-cursor-preparation' });

    markSessionEventCursorReady(state, 'session-a', 1644341);
    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-a' }),
    ).toEqual({
      kind: 'open',
      afterSequence: 1644341,
      cursorSource: 'authoritative',
    });
  });

  it('accepts a real zero once the session itself was confirmed ready', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-new', 0);

    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-new' }),
    ).toEqual({
      kind: 'open',
      afterSequence: 0,
      cursorSource: 'authoritative',
    });
  });

  it('keeps the explicit cursor contract for a server-confirmed new session', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-old', 9865);

    expect(
      decideSessionEventStreamStart({
        state,
        sessionId: 'session-new',
        explicitCursor: 0,
      }),
    ).toEqual({ kind: 'open', afterSequence: 0, cursorSource: 'explicit' });
  });

  it('never carries one session cursor over to another', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-a', 9865);

    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-b' }),
    ).toEqual({ kind: 'refused', reason: 'cursor-unknown-for-session' });
    expect(isCursorReadyForSession(state, 'session-b')).toBe(false);
  });

  it('does not mark ready when preparation fails', () => {
    const state = createSessionEventCursorState();
    beginSessionEventCursorHydration(state, 'session-a');

    expect(markSessionEventCursorFailed(state, 'session-a')).toBe(true);
    expect(state.phase).toBe('unknown');
    expect(isCursorReadyForSession(state, 'session-a')).toBe(false);
    expect(
      decideSessionEventStreamStart({ state, sessionId: 'session-a' }),
    ).toEqual({ kind: 'refused', reason: 'cursor-unknown-for-session' });
  });

  it('ignores a failure that belongs to another session', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-b', 222);

    expect(markSessionEventCursorFailed(state, 'session-a')).toBe(false);
    expect(isCursorReadyForSession(state, 'session-b')).toBe(true);
  });

  it('advances only forward and only for the same session', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-a', 10);

    expect(advanceSessionEventCursor(state, 'session-b', 99)).toBe(false);
    expect(state.sequence).toBe(10);

    expect(advanceSessionEventCursor(state, 'session-a', 9)).toBe(false);
    expect(state.sequence).toBe(10);

    expect(advanceSessionEventCursor(state, 'session-a', 11)).toBe(true);
    expect(state.sequence).toBe(11);
  });

  it('invalidates by session or wholesale', () => {
    const state = createSessionEventCursorState();
    markSessionEventCursorReady(state, 'session-a', 10);

    // 别的会话不得清掉当前会话的游标。
    expect(invalidateSessionEventCursor(state, 'session-b')).toBe(false);
    expect(isCursorReadyForSession(state, 'session-a')).toBe(true);

    expect(invalidateSessionEventCursor(state, 'session-a')).toBe(true);
    expect(state).toEqual({ sessionId: null, phase: 'unknown', sequence: 0 });

    // 幂等：已经是空的就没什么可清。
    expect(invalidateSessionEventCursor(state)).toBe(false);
  });
});
