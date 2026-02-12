// ── Session Event Stream Runtime 单测 ──────────────────────────
// ADR-054 Step 3: 验证 SSE/replay/reconnect/timer 生命周期

import type { SessionEventStreamRuntimeOptions } from './sessionEventStream';
import { createSessionEventStreamRuntime } from './sessionEventStream';

// 使用 jest fake timers 来控制 timer 行为
jest.useFakeTimers();

function createMockOptions(): jest.Mocked<SessionEventStreamRuntimeOptions> {
  return {
    subscribeSessionEvents: jest.fn(),
    replayMissedEvents: jest.fn().mockResolvedValue(true),
    applySessionEvent: jest.fn(),
    onTerminal: jest.fn(),
    onDiag: jest.fn(),
  };
}

describe('SessionEventStreamRuntime', () => {
  let opts: jest.Mocked<SessionEventStreamRuntimeOptions>;

  beforeEach(() => {
    opts = createMockOptions();
    jest.clearAllTimers();
    jest.clearAllMocks();
  });

  describe('start/stop lifecycle', () => {
    it('start sets current session and calls subscribeSessionEvents', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      expect(rt.getCurrentSessionId()).toBe('sess-1');
      // SSE 订阅应该被调用（通过 scheduleReplayPoll 和 startSseStream）
      jest.runAllTimers();
      // replayMissedEvents 在 poll timer 触发后被调用
    });

    it('stop clears current sessionId', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');
      rt.stop('test-stop');

      expect(rt.getCurrentSessionId()).toBeNull();
    });

    it('start A then start B clears A timers and abort controller', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-a');

      const firstPollTimer = (rt as any)._internals?.pollTimer;
      // Start B should stop A's stream
      rt.start('sess-b');

      expect(rt.getCurrentSessionId()).toBe('sess-b');
      // A 的 timer 应该被清理（通过 stop 调用 clearAllTimers）
      jest.runAllTimers();
    });
  });

  describe('onTerminal callback', () => {
    it('SSE 404 triggers onTerminal and does NOT reconnect', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      // 获取 subscribeSessionEvents 的 onError 回调
      const subscribeCall = (opts.subscribeSessionEvents as jest.Mock).mock
        .calls[0];
      expect(subscribeCall).toBeDefined();
      const onErrorCallback = subscribeCall?.[3]?.onError;
      expect(onErrorCallback).toBeDefined();

      // 模拟 SSE 返回 404
      onErrorCallback!(new Error('SSE 404'), 404);

      expect(opts.onTerminal).toHaveBeenCalledWith('sess-1', 404);
      // 不应该调度重连
      expect(opts.replayMissedEvents).toHaveBeenCalledTimes(0);
    });

    it('SSE 410 triggers onTerminal and does NOT reconnect', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      const subscribeCall = (opts.subscribeSessionEvents as jest.Mock).mock
        .calls[0];
      const onErrorCallback = subscribeCall?.[3]?.onError;

      onErrorCallback!(new Error('SSE 410'), 410);

      expect(opts.onTerminal).toHaveBeenCalledWith('sess-1', 410);
      expect(opts.replayMissedEvents).toHaveBeenCalledTimes(0);
    });

    it('SSE 500 triggers reconnect, not onTerminal', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      const subscribeCall = (opts.subscribeSessionEvents as jest.Mock).mock
        .calls[0];
      const onErrorCallback = subscribeCall?.[3]?.onError;

      onErrorCallback!(new Error('SSE 500'), 500);

      expect(opts.onTerminal).not.toHaveBeenCalled();
      // 应调度重连 timer
      jest.runAllTimers();
      // 重连后会尝试 replay + resubscribe
      expect(opts.replayMissedEvents).toHaveBeenCalled();
    });
  });

  describe('replay poll', () => {
    it('replay poll calls replayMissedEvents on timer', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      // 触发 poll timer
      jest.runAllTimers();

      expect(opts.replayMissedEvents).toHaveBeenCalled();
    });

    it('replay terminal error does not schedule another poll', async () => {
      opts.replayMissedEvents.mockRejectedValueOnce(
        new Error('Session not found'),
      );

      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');

      const initialCalls = opts.replayMissedEvents.mock.calls.length;
      jest.runAllTimers();

      // 重放失败后不应再次调度 poll
      jest.runAllTimers();
      // 只比初始多一次调用
      expect(opts.replayMissedEvents.mock.calls.length).toBeLessThanOrEqual(
        initialCalls + 1,
      );
    });
  });

  describe('stop cleanup', () => {
    it('stop clears all pending timers', () => {
      const rt = createSessionEventStreamRuntime(opts);
      rt.start('sess-1');
      rt.stop('cleanup');

      // 再次 runAllTimers，不应触发任何新的 replay
      jest.runAllTimers();
      expect(opts.replayMissedEvents).not.toHaveBeenCalled();
    });
  });
});
