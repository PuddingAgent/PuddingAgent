import { act, renderHook } from '@testing-library/react';
import { useRef, useState } from 'react';
import { getCompactionStatus } from '@/services/platform/api';
import { useCompaction } from './useCompaction';

jest.mock('@/services/platform/api', () => ({
  compactSession: jest.fn(),
  getCompactionStatus: jest.fn(() => new Promise(() => {})),
}));

const messageApi = {
  destroy: jest.fn(),
  error: jest.fn(),
  info: jest.fn(),
  loading: jest.fn(),
  success: jest.fn(),
};

const compactionEvent = (type: string, extra: Record<string, unknown> = {}) =>
  ({
    type,
    compactionId: 'compact-1',
    ...extra,
  }) as never;

function useCompactionHarness() {
  const [turns, setTurns] = useState<Array<{ assistant: { status: string } }>>(
    [],
  );
  const turnsRef = useRef<Array<{ assistant: { status: string } }>>([]);
  const latestTurnIdRef = useRef<string | null>(null);
  const sessionIdRef = useRef<string | undefined>('session-1');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const compaction = useCompaction({
    identity: {
      workspaceId: 'default',
      agentId: 'agent-1',
      selectedSessionId: 'session-1',
      sessionIdRef,
    },
    turns: {
      turnsRef: turnsRef as never,
      latestTurnIdRef,
      setTurns: setTurns as never,
    },
    status: { loading, setLoading, setError },
    messageApi: messageApi as never,
  });
  return { ...compaction, turns, loading, error };
}

describe('useCompaction', () => {
  it('does not merge a different compaction into the active identity', () => {
    const { result } = renderHook(() => useCompactionHarness());
    act(() => result.current.handleCompactionLifecycleEvent(compactionEvent('context.compaction.started')));
    act(() => result.current.handleCompactionLifecycleEvent(compactionEvent('context.compaction.completed', { compactionId: 'older' }), { replay: true, notify: false }));
    expect(result.current.turns).toHaveLength(2);
    expect(result.current.turns[0].assistant.status).toBe('executing');
  });
  it('server confirms exact identity, then stops animation when no work is running', async () => {
    jest.useFakeTimers();
    jest.mocked(getCompactionStatus).mockResolvedValueOnce({ activeCompaction: { compactionId: 'live', startedAt: new Date().toISOString() } }).mockResolvedValue({ activeCompaction: null });
    const { result, unmount } = renderHook(() => useCompactionHarness());
    await act(async () => {});
    expect((result.current.turns[0] as any).assistant.timelineItems[0].compaction.state).toBe('running');
    await act(async () => { jest.advanceTimersByTime(10_000); });
    expect((result.current.turns[0] as any).assistant.timelineItems[0].compaction.state).toBe('unknown');
    expect(result.current.loading).toBe(false);
    unmount(); jest.useRealTimers();
  });
  it('late terminal repairs an unknown state after the server stopped', async () => {
    jest.mocked(getCompactionStatus).mockResolvedValue({ activeCompaction: { compactionId: 'live', startedAt: new Date().toISOString() } });
    const { result } = renderHook(() => useCompactionHarness());
    await act(async () => {});
    act(() => result.current.handleCompactionLifecycleEvent(compactionEvent('context.compaction.completed', { compactionId: 'live', occurredAt: new Date().toISOString() })));
    expect((result.current.turns[0] as any).assistant.timelineItems[0].compaction.state).toBe('completed');
  });
  it('history replay cannot switch the current conversation', () => {
    const { result } = renderHook(() => useCompactionHarness());
    const switchSession = jest.fn();
    act(() => result.current.bindCompactedSessionSwitch(switchSession));
    act(() => result.current.handleCompactionLifecycleEvent(compactionEvent('context.compaction.completed', { newSessionId: 'old-successor' }), { replay: true }));
    expect(switchSession).not.toHaveBeenCalled();
  });
  beforeEach(() => {
    jest.clearAllMocks();
    jest.mocked(getCompactionStatus).mockImplementation(() => new Promise(() => {}));
  });

    it('projects compaction lifecycle events into one stable turn', () => {
    // RC-6 守卫依赖 performance.now() 与手动切换时刻的差值；固定时钟避免
    // 环境启动过快（<2s）导致的时序 flake（既有潜在问题，与 CU-02 无关）。
    const perfSpy = jest
      .spyOn(performance, 'now')
      .mockReturnValue(10_000);
    const onSwitchSession = jest.fn();
    const { result } = renderHook(() => useCompactionHarness());

    act(() => result.current.bindCompactedSessionSwitch(onSwitchSession));
    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
      ),
    );
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('executing');
    expect(result.current.loading).toBe(false);

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.completed', {
          newSessionId: 'session-2',
          newSessionTitle: '压缩后的会话',
        }),
      ),
    );
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('success');
    expect(result.current.loading).toBe(false);
        expect(onSwitchSession).toHaveBeenCalledWith('session-2', '压缩后的会话');
    perfSpy.mockRestore();
  });

  it('resets lifecycle turns without mutating ordinary turns', () => {
    const { result } = renderHook(() => useCompactionHarness());

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
        { notify: false },
      ),
    );
    expect(result.current.mergeCompactionLifecycleTurns([])).toHaveLength(1);

    act(() => result.current.resetCompaction());
    expect(result.current.mergeCompactionLifecycleTurns([])).toEqual([]);
    expect(messageApi.destroy).toHaveBeenCalledWith('compaction-status');
  });

  it('shows the original completion time when history is replayed', () => {
    const { result } = renderHook(() => useCompactionHarness());
    const occurredAt = '2026-09-14T03:43:33.553Z';
    act(() => result.current.handleCompactionLifecycleEvent(
      compactionEvent('context.compaction.completed', { occurredAt }),
      { notify: false, allowSessionSwitch: false },
    ));
    expect(result.current.compactionStatus).toBe(
      `上次压缩：${new Date(occurredAt).toLocaleString('zh-CN', { hour12: false })}`,
    );
    expect(messageApi.loading).not.toHaveBeenCalled();
  });

  it('does not invent a recent completion time for an undated history event', () => {
    const { result } = renderHook(() => useCompactionHarness());
    act(() => result.current.handleCompactionLifecycleEvent(
      compactionEvent('context.compaction.completed'),
      { notify: false, allowSessionSwitch: false },
    ));
    expect(result.current.compactionStatus).toBe('上次压缩：时间未知');
  });

  it.each(['completed', 'failed'])('does not revive a %s compaction on a late start', (terminal) => {
    const { result } = renderHook(() => useCompactionHarness());
    act(() => result.current.handleCompactionLifecycleEvent(
      compactionEvent(`context.compaction.${terminal}`),
      { notify: false, allowSessionSwitch: false },
    ));
    const status = result.current.compactionStatus;
    act(() => result.current.handleCompactionLifecycleEvent(
      compactionEvent('context.compaction.started'),
    ));
    expect(result.current.loading).toBe(false);
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe(terminal === 'failed' ? 'error' : 'success');
    expect(result.current.compactionStatus).toBe(status);
    expect(messageApi.loading).not.toHaveBeenCalled();
    // A genuinely new operation still starts normally.
    act(() => result.current.handleCompactionLifecycleEvent(
      compactionEvent('context.compaction.started', { compactionId: 'compact-2' }),
    ));
    expect(result.current.loading).toBe(false);
    expect(result.current.turns).toHaveLength(2);
  });

  it('replay: ignores an orphan started entirely (no turn, no loading, no toast)', () => {
    const { result } = renderHook(() => useCompactionHarness());

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
        { replay: true, runningCompactionId: null },
      ),
    );

    // 没有终态的孤儿 started 不再冒充“正在压缩”：不建 turn、不 setLoading、不弹 toast。
    expect(result.current.turns).toHaveLength(0);
    expect(result.current.loading).toBe(false);
    expect(result.current.compactionStatus).toBeNull();
    expect(messageApi.loading).not.toHaveBeenCalled();
  });

  it('replay: a started matching runningCompactionId lights up silently and closes on its terminal', () => {
    const { result } = renderHook(() => useCompactionHarness());

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
        { replay: true, runningCompactionId: 'compact-1' },
      ),
    );
    // 真在跑的压缩：复活运行态是对的，但重放不弹 toast。
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('executing');
    expect(result.current.loading).toBe(false);
    expect(messageApi.loading).not.toHaveBeenCalled();

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.completed'),
        { replay: true, notify: false, allowSessionSwitch: false },
      ),
    );
    expect(result.current.turns[0].assistant.status).toBe('success');
    expect(result.current.loading).toBe(false);
  });

  it('live start awaits authority without changing chat loading or showing a toast', () => {
    const { result } = renderHook(() => useCompactionHarness());

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
      ),
    );

    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('executing');
    expect(result.current.loading).toBe(false);
    expect(messageApi.loading).not.toHaveBeenCalled();
  });

  it('replay 帧上的新鲜 started 仍然点亮（短暂断线期间真的在压缩）', () => {
    // 按 id 的旧门控会在「无权限来源」时误杀真在运行的压缩，直到终态才可见；
    // 现在 replay 帧逐「确定新鲜」后点亮。
    const now = Date.parse('2026-09-19T22:00:00.000Z');
    const dateSpy = jest.spyOn(Date, 'now').mockReturnValue(now);
    try {
      const { result } = renderHook(() => useCompactionHarness());
      act(() =>
        result.current.handleCompactionLifecycleEvent(
          compactionEvent('context.compaction.started', {
            occurredAt: '2026-09-19T21:59:40.000Z',
            replay: true,
          }),
          { replay: true, notify: false, allowSessionSwitch: false },
        ),
      );
      expect(result.current.turns).toHaveLength(1);
      expect(result.current.turns[0].assistant.status).toBe('executing');
      expect(result.current.loading).toBe(false);
    } finally {
      dateSpy.mockRestore();
    }
  });

  it('ignores a stale started arriving on the live channel (no zombie card)', () => {
    // SSE 无游标时会从 sequence 0 全量重放历史，且 live 通道不带 replay 标记：
    // 8 天前的孤儿 started 必须被年龄门控拦下，不得点亮运行态。
    const now = Date.parse('2026-09-19T22:00:00.000Z');
    const dateSpy = jest.spyOn(Date, 'now').mockReturnValue(now);
    try {
      const { result } = renderHook(() => useCompactionHarness());
      act(() =>
        result.current.handleCompactionLifecycleEvent(
          compactionEvent('context.compaction.started', {
            occurredAt: '2026-09-11T14:16:16.000Z',
          }),
        ),
      );
      expect(result.current.turns).toHaveLength(0);
      expect(result.current.loading).toBe(false);
      expect(messageApi.loading).not.toHaveBeenCalled();
      // 后续终态事件仍按事实渲染，不回退成「假运行态」。
      act(() =>
        result.current.handleCompactionLifecycleEvent(
          compactionEvent('context.compaction.completed', {
            occurredAt: '2026-09-11T14:18:16.000Z',
          }),
          { notify: false, allowSessionSwitch: false },
        ),
      );
      expect(result.current.turns).toHaveLength(1);
      expect(result.current.turns[0].assistant.status).toBe('success');
    } finally {
      dateSpy.mockRestore();
    }
  });

  it('converges a running compaction to a terminal state after the liveness TTL', () => {
    jest.useFakeTimers();
    try {
      const { result } = renderHook(() => useCompactionHarness());

      act(() =>
        result.current.handleCompactionLifecycleEvent(
          compactionEvent('context.compaction.started'),
        ),
      );
      expect(result.current.loading).toBe(false);

      // 终态事件丢失：TTL 超时后必须收敛，禁止 duration:0 的 toast 永久悬挂。
      act(() => {
        jest.advanceTimersByTime(10 * 60 * 1000);
      });

      expect(result.current.turns).toHaveLength(1);
      expect(result.current.turns[0].assistant.status).toBe('cancelled');
      expect(result.current.loading).toBe(false);
      expect(result.current.compactionStatus).toBe('压缩状态待确认');
      expect(messageApi.destroy).toHaveBeenCalledWith('compaction-status');
    } finally {
      jest.useRealTimers();
    }
  });
});
