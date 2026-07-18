// ── subscribeSessionEvents 404/error 回调单测 ──────────────────────
// 锁定 SSE 连接遇到 HTTP 错误时通过 onError 通知调用方的行为。

import {
  projectConversationEventEnvelope,
  subscribeSessionEvents,
} from './api';

// Mock recordPerfEvent to avoid side effects
jest.mock('@/utils/debug', () => ({
  recordPerfEvent: jest.fn(),
}));

const flushPromises = () => new Promise<void>((resolve) => { setTimeout(resolve, 0); });

describe('subscribeSessionEvents error handling', () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it('calls onError with httpStatus 404 when fetch returns 404', async () => {
    const onEvent = jest.fn();
    const onError = jest.fn();

    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 404,
      body: null,
      headers: { get: () => null },
    });

    subscribeSessionEvents('session-1', onEvent, undefined, { onError });

    // flush microtask queue so fetch .then resolves
    await flushPromises();

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(Error);
    expect(onError.mock.calls[0][0].message).toContain('404');
    expect(onError.mock.calls[0][1]).toBe(404);
    expect(onEvent).not.toHaveBeenCalled();
  });

  it('calls onError with httpStatus 500 when fetch returns 500', async () => {
    const onEvent = jest.fn();
    const onError = jest.fn();

    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 500,
      body: null,
      headers: { get: () => null },
    });

    subscribeSessionEvents('session-1', onEvent, undefined, { onError });

    await flushPromises();

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][1]).toBe(500);
    expect(onEvent).not.toHaveBeenCalled();
  });

  it('does not call onEvent when response is not ok', async () => {
    const onEvent = jest.fn();
    const onError = jest.fn();

    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 403,
      body: null,
      headers: { get: () => null },
    });

    subscribeSessionEvents('session-1', onEvent, undefined, { onError });

    await flushPromises();

    expect(onEvent).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledTimes(1);
  });

  it('calls onError with httpStatus 410 when fetch returns 410 (frozen/archived)', async () => {
    const onEvent = jest.fn();
    const onError = jest.fn();

    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 410,
      body: null,
      headers: { get: () => null },
    });

    subscribeSessionEvents('session-1', onEvent, undefined, { onError });

    await flushPromises();

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(Error);
    expect(onError.mock.calls[0][0].message).toContain('410');
    expect(onError.mock.calls[0][1]).toBe(410);
    expect(onEvent).not.toHaveBeenCalled();
  });

  it('projects a canonical SSE envelope into the chat event shape', () => {
    expect(
      projectConversationEventEnvelope(
        {
          sequence: 23,
          turnId: 'turn-1',
          messageId: 'message-1',
          payload: { reply: 'OK' },
        },
        'turn.completed',
        23,
      ),
    ).toEqual(
      expect.objectContaining({
        type: 'done',
        sequenceNum: 23,
        turnId: 'turn-1',
        messageId: 'message-1',
        reply: 'OK',
      }),
    );
  });
});
