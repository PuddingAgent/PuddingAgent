// ── ADR-074 Goal 顶部状态入口组件测试 ────────────────────────────────
// 注意：本文件含 jest.mock 工厂；当前 umi jest 转换链不允许「jest.mock +
// 导入类型用于类型注解」组合，故这里使用本地结构类型而非 import type。
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import GoalBanner from './GoalBanner';

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
  overrides: Partial<{
    phase: GoalPhase;
    objective: string;
    statusReason: string | null;
    terminalAtUtc: string | null;
    iterationsStarted: number;
    maxIterations: number;
  }> = {},
) => ({
  goalRunId: 'goal-1',
  conversationId: 'conv-1',
  agentInstanceId: 'agent-1',
  objective: '修复全部失败测试并保持公开 API 不变',
  objectiveVersion: 1,
  phase: 'active' as const,
  blockedCode: null,
  statusReason: null,
  maxIterations: 256,
  iterationsStarted: 18,
  iterationsSettled: 17,
  activationEpoch: 1,
  aggregateVersion: 1,
  lastNextAction: null,
  createdAtUtc: '2026-08-24T00:00:00Z',
  updatedAtUtc: '2026-08-24T00:10:00Z',
  terminalAtUtc: null,
  ...overrides,
});

const openDetails = () =>
  fireEvent.click(screen.getByRole('button', { name: /Goal .*查看详情/ }));

describe('GoalBanner', () => {
  beforeEach(() => {
    mockRequest.mockReset();
    // GoalStepsPanel 会在详情 Popover 打开时拉取步骤；默认拒绝以免测试触网。
    mockRequest.mockRejectedValue(new Error('steps endpoint not deployed'));
  });

  it('offers a start control when the conversation has no goal', async () => {
    const onCommand = jest.fn().mockResolvedValue('Goal 已创建');
    render(
      <GoalBanner goal={null} commandRunning={false} onCommand={onCommand} />,
    );
    fireEvent.click(screen.getByRole('button', { name: '开始 Goal' }));
    fireEvent.change(await screen.findByPlaceholderText(/描述要持续完成的目标/), {
      target: { value: '完成调度器控制台并通过测试' },
    });
    fireEvent.click(screen.getByRole('button', { name: /^开\s*始$/ }));

    await waitFor(() =>
      expect(onCommand).toHaveBeenCalledWith('set', {
        objective: '完成调度器控制台并通过测试',
        rounds: 32,
      }),
    );
  });

  it('renders a compact active status button and keeps details in popover', async () => {
    render(
      <GoalBanner
        goal={makeGoal()}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );

    const statusButton = screen.getByRole('button', {
      name: /Goal 运行中.*18\/256.*查看详情/,
    });
    expect(statusButton.getAttribute('data-goal-phase')).toBe('active');
    expect(screen.queryByRole('dialog', { name: 'Goal 详情' })).toBeNull();

    openDetails();

    expect(
      await screen.findByRole('dialog', { name: 'Goal 详情' }),
    ).toBeTruthy();
    expect(
      screen.getAllByText(/修复全部失败测试并保持公开 API 不变/).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /暂停/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /停止/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /恢复/ })).toBeNull();
  });

  it('does not render a long task objective until the status opens', async () => {
    const longObjective =
      'Workspace Task: P0 统一 Scheduler 内核\n\nDescription:\n' +
      '很长的任务说明 '.repeat(80);
    render(
      <GoalBanner
        goal={makeGoal({
          objective: longObjective,
          phase: 'failed',
          statusReason: 'Iteration ended as failed.',
          terminalAtUtc: '2026-08-24T01:00:00Z',
        })}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );

    const statusButton = screen.getByRole('button', {
      name: /Goal 失败.*查看详情/,
    });
    expect(statusButton.getAttribute('data-goal-phase')).toBe('failed');
    expect(screen.queryByText(/很长的任务说明/)).toBeNull();

    openDetails();

    expect(await screen.findByLabelText('Goal 目标详情')).toBeTruthy();
    expect(screen.getByText(/原因：Iteration ended as failed/)).toBeTruthy();
    expect(screen.getByText(/终止于/)).toBeTruthy();
  });

  it('shows resume for paused goal and calls command', async () => {
    const onCommand = jest.fn().mockResolvedValue('Goal 已恢复 active');
    render(
      <GoalBanner
        goal={makeGoal({ phase: 'paused', statusReason: 'user' })}
        commandRunning={false}
        onCommand={onCommand}
      />,
    );
    openDetails();

    fireEvent.click(await screen.findByRole('button', { name: /恢复/ }));
    await screen.findByText(/Goal 已恢复 active/);
    expect(onCommand).toHaveBeenCalledWith('resume', undefined);
  });

  it('hides controls for terminal goal', async () => {
    render(
      <GoalBanner
        goal={makeGoal({
          phase: 'completed',
          terminalAtUtc: '2026-08-24T01:00:00Z',
        })}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );
    openDetails();
    await screen.findByRole('dialog', { name: 'Goal 详情' });

    expect(screen.queryByRole('button', { name: /暂停/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /恢复/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /停止/ })).toBeNull();
    expect(screen.getByRole('button', { name: /新建 Goal/ })).toBeTruthy();
  });

  it('renders exhausted budget as terminal with new and clear actions', async () => {
    render(
      <GoalBanner
        goal={makeGoal({ phase: 'budget_exhausted', iterationsStarted: 3, maxIterations: 3 })}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );
    expect(screen.getByRole('button', { name: /Goal 额度耗尽.*3\/3.*查看详情/ })).toBeTruthy();
    openDetails();
    await screen.findByRole('dialog', { name: 'Goal 详情' });
    expect(screen.queryByRole('button', { name: /暂停|恢复|停止/ })).toBeNull();
    expect(screen.getByRole('button', { name: /新建 Goal/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /清除记录/ })).toBeTruthy();
  });

  it.each(['budgetexhausted', 'future_phase', 'constructor', '__proto__'])(
    'keeps unknown phase %s readable without offering state transitions',
    async (phase) => {
      render(
        <GoalBanner
          goal={makeGoal({ phase: phase as GoalPhase })}
          commandRunning={false}
          onCommand={jest.fn()}
        />,
      );
      expect(screen.getByRole('button', { name: `Goal 未知状态（${phase}），Iteration 18/256，查看详情` })).toBeTruthy();
      openDetails();
      expect(await screen.findByLabelText('Goal 目标详情')).toBeTruthy();
      expect(screen.queryByRole('button', { name: /暂停|恢复|停止|新建 Goal|清除记录/ })).toBeNull();
    },
  );

  it('disables controls while a command is running', async () => {
    render(
      <GoalBanner goal={makeGoal()} commandRunning onCommand={jest.fn()} />,
    );
    openDetails();

    expect(
      (
        (await screen.findByRole('button', {
          name: /暂停/,
        })) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });

  it('offers extend for budget_exhausted goal and sends the extend command with rounds', async () => {
    const onCommand = jest.fn().mockResolvedValue('额度已延长');
    render(
      <GoalBanner
        goal={makeGoal({
          phase: 'budget_exhausted',
          iterationsStarted: 3,
          maxIterations: 3,
        })}
        commandRunning={false}
        onCommand={onCommand}
      />,
    );
    openDetails();

    fireEvent.click(await screen.findByRole('button', { name: /延长额度/ }));
    // antd Modal 打开期间会对兄弟节点标记 aria-hidden，role 查询不可靠，
    // 改用 DOM 查询主按钮。
    const modalOk = await waitFor(() => {
      const btn = document.querySelector(
        '.ant-modal .ant-btn-primary',
      ) as HTMLButtonElement | null;
      expect(btn).toBeTruthy();
      return btn as HTMLButtonElement;
    });
    fireEvent.click(modalOk);

    await waitFor(() =>
      expect(onCommand).toHaveBeenCalledWith('extend', { rounds: 3 }),
    );
    await screen.findByText(/额度已延长/);
  });

  it('does not offer extend for non budget_exhausted goals', async () => {
    render(
      <GoalBanner
        goal={makeGoal({ phase: 'completed' })}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );
    openDetails();
    await screen.findByRole('dialog', { name: 'Goal 详情' });

    expect(screen.queryByRole('button', { name: /延长额度/ })).toBeNull();
  });

  it('renders the steps panel inside the goal popover', async () => {
    mockRequest.mockResolvedValue({
      goalRunId: 'goal-1',
      phase: 'active',
      planVersion: 1,
      hasPlan: true,
      progress: {
        stepsTotal: 1,
        stepsPassed: 0,
        stepsFailed: 0,
        stepsInProgress: 1,
        currentStepId: 'node-1',
      },
      steps: [
        {
          nodeId: 'node-1',
          sequenceNo: 1,
          kind: 'plan',
          title: '制定计划',
          status: 'in_progress',
          startedAtUtc: null,
          completedAtUtc: null,
          blockerCode: null,
          evidenceRefs: [],
        },
      ],
      checks: [],
    });
    render(
      <GoalBanner
        goal={makeGoal()}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );
    openDetails();

    expect(await screen.findByLabelText('Goal 步骤')).toBeTruthy();
    expect(await screen.findByText('制定计划')).toBeTruthy();
    expect(mockRequest).toHaveBeenCalledWith('/api/v1/goals/goal-1/steps', {
      method: 'GET',
      skipErrorHandler: true,
    });
  });
});
