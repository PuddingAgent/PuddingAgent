// ── useGoal：自适应轮询 / 终态停轮 / extend 语义确认 ─────────────────
// 注意：本文件含 jest.mock 工厂；当前 umi jest 转换链不允许「jest.mock +
// 导入类型用于类型注解」组合，故这里使用本地 phase 联合类型。
import { act, renderHook } from '@testing-library/react';
import { shouldStopGoalPolling, useGoal } from './useGoal';

const mockRequest = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => mockRequest(...args),
}));

type GoalPhase =
  | 'active'
  | 'paused'
  | 'blocked'
  | 'budget_exhausted'
  | 'completed'
  | 'cancelled'
  | 'failed';

const makeGoal = (
  phase: GoalPhase,
  overrides: Record<string, unknown> = {},
) => ({
  goalRunId: 'goal-1',
  conversationId: 'conv-1',
  agentInstanceId: 'agent-1',
  objective: '目标',
  objectiveVersion: 1,
  phase,
  blockedCode: null,
  statusReason: null,
  maxIterations: 32,
  iterationsStarted: 1,
  iterationsSettled: 1,
  activationEpoch: 1,
  aggregateVersion: 1,
  lastNextAction: null,
  createdAtUtc: '2026-09-16T00:00:00Z',
  updatedAtUtc: '2026-09-16T00:10:00Z',
  terminalAtUtc: null,
  ...overrides,
});

/** 排空微任务队列（不推进时钟）。 */
const flushMicrotasks = async () => {
  await act(async () => {
    await Promise.resolve();
  });
};

describe('shouldStopGoalPolling', () => {
  it('stops only for terminal phases and blocked', () => {
    expect(shouldStopGoalPolling('completed')).toBe(true);
    expect(shouldStopGoalPolling('cancelled')).toBe(true);
    expect(shouldStopGoalPolling('failed')).toBe(true);
    expect(shouldStopGoalPolling('budget_exhausted')).toBe(true);
    expect(shouldStopGoalPolling('blocked')).toBe(true);
    expect(shouldStopGoalPolling('active')).toBe(false);
    expect(shouldStopGoalPolling('paused')).toBe(false);
  });
});

describe('useGoal polling', () => {
  beforeEach(() => {
    mockRequest.mockReset();
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('polls an active goal adaptively between 3s and 5s', async () => {
    mockRequest
      .mockResolvedValueOnce({ goal: makeGoal('active') })
      .mockResolvedValueOnce({
        goal: makeGoal('active', { iterationsStarted: 2 }),
      })
      .mockResolvedValue({
        goal: makeGoal('active', { iterationsStarted: 3 }),
      });

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();
    expect(mockRequest).toHaveBeenCalledTimes(1);
    expect(result.current.goal?.phase).toBe('active');

    // 第一次轮询：初始延迟 3s
    await act(async () => {
      await jest.advanceTimersByTimeAsync(2999);
    });
    expect(mockRequest).toHaveBeenCalledTimes(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(mockRequest).toHaveBeenCalledTimes(2);

    // 自适应退避：第二轮延迟 4s
    await act(async () => {
      await jest.advanceTimersByTimeAsync(3999);
    });
    expect(mockRequest).toHaveBeenCalledTimes(2);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(mockRequest).toHaveBeenCalledTimes(3);

    // 第三轮延迟 5s（上限）
    await act(async () => {
      await jest.advanceTimersByTimeAsync(4999);
    });
    expect(mockRequest).toHaveBeenCalledTimes(3);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(mockRequest).toHaveBeenCalledTimes(4);
  });

  it('stops polling immediately once the goal reaches a terminal phase', async () => {
    mockRequest
      .mockResolvedValueOnce({ goal: makeGoal('active') })
      .mockResolvedValue({ goal: makeGoal('completed') });

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();
    expect(mockRequest).toHaveBeenCalledTimes(1);

    await act(async () => {
      await jest.advanceTimersByTimeAsync(3000);
    });
    expect(mockRequest).toHaveBeenCalledTimes(2);
    expect(result.current.goal?.phase).toBe('completed');

    const callsAfterTerminal = mockRequest.mock.calls.length;
    await act(async () => {
      await jest.advanceTimersByTimeAsync(60_000);
    });
    expect(mockRequest.mock.calls.length).toBe(callsAfterTerminal);
  });

  it('stops polling when the goal is blocked', async () => {
    mockRequest
      .mockResolvedValueOnce({ goal: makeGoal('active') })
      .mockResolvedValue({ goal: makeGoal('blocked') });

    renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();

    await act(async () => {
      await jest.advanceTimersByTimeAsync(3000);
    });
    const calls = mockRequest.mock.calls.length;
    expect(calls).toBe(2);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(60_000);
    });
    expect(mockRequest.mock.calls.length).toBe(calls);
  });

  it('clears pending poll timers on unmount', async () => {
    mockRequest.mockResolvedValue({ goal: makeGoal('active') });
    const { unmount } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();
    expect(mockRequest).toHaveBeenCalledTimes(1);

    unmount();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(60_000);
    });
    expect(mockRequest).toHaveBeenCalledTimes(1);
  });
});

describe('useGoal commands', () => {
  beforeEach(() => {
    mockRequest.mockReset();
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('refreshes immediately after a command returns', async () => {
    mockRequest
      .mockResolvedValueOnce({ goal: makeGoal('paused') }) // 初次加载
      .mockResolvedValueOnce({
        // 命令响应
        success: true,
        message: 'Goal 已恢复 active',
        goal: makeGoal('active'),
      })
      .mockResolvedValue({ goal: makeGoal('active') }); // 命令后的立即刷新

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();

    const text = await result.current.runCommand('resume');
    expect(text).toBe('Goal 已恢复 active');
    // 初次加载 1 次 + 命令 1 次 + 命令后立即刷新 1 次
    expect(mockRequest.mock.calls.length).toBeGreaterThanOrEqual(3);
  });

  it('reports conservative failure when extend silently degrades to status', async () => {
    const before = makeGoal('budget_exhausted', { maxIterations: 3 });
    mockRequest
      .mockResolvedValueOnce({ goal: before }) // 初次加载
      .mockResolvedValueOnce({
        // 服务端把未知 action 静默降级为 status：仍返回 budget_exhausted
        success: true,
        message: 'Goal 状态查询成功',
        goal: makeGoal('budget_exhausted', { maxIterations: 3 }),
      })
      .mockResolvedValue({ goal: before });

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();

    const text = await result.current.runCommand('extend', { rounds: 3 });
    expect(text).toContain('未确认生效');
  });

  it('returns server message when extend verifiably increases maxIterations', async () => {
    mockRequest
      .mockResolvedValueOnce({
        goal: makeGoal('budget_exhausted', { maxIterations: 3 }),
      })
      .mockResolvedValueOnce({
        success: true,
        message: '额度已延长',
        goal: makeGoal('active', { maxIterations: 6 }),
      })
      .mockResolvedValue({ goal: makeGoal('active', { maxIterations: 6 }) });

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();

    const text = await result.current.runCommand('extend', { rounds: 3 });
    expect(text).toBe('额度已延长');
  });

  it('reports conservative failure when extend response has no goal snapshot', async () => {
    mockRequest
      .mockResolvedValueOnce({ goal: makeGoal('budget_exhausted') })
      .mockResolvedValueOnce({ success: true, message: 'OK' })
      .mockResolvedValue({ goal: makeGoal('budget_exhausted') });

    const { result } = renderHook(() =>
      useGoal({ workspaceId: 'ws', conversationId: 'c1', agentId: 'a1' }),
    );
    await flushMicrotasks();

    const text = await result.current.runCommand('extend', { rounds: 3 });
    expect(text).toContain('未确认生效');
  });
});
