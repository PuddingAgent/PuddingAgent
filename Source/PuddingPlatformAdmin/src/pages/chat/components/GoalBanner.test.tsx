// ── ADR-074 Goal 顶部状态入口组件测试 ────────────────────────────────
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import type { GoalSnapshot } from '@/services/platform/api';
import GoalBanner from './GoalBanner';

const makeGoal = (overrides: Partial<GoalSnapshot> = {}): GoalSnapshot => ({
  goalRunId: 'goal-1',
  conversationId: 'conv-1',
  agentInstanceId: 'agent-1',
  objective: '修复全部失败测试并保持公开 API 不变',
  objectiveVersion: 1,
  phase: 'active',
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
});
