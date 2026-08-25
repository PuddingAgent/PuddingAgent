// ── ADR-074 G1 GoalBanner 组件测试 ─────────────────
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { GoalSnapshot } from '@/services/platform/api';
import GoalBanner from './GoalBanner';

const makeGoal = (
  overrides: Partial<GoalSnapshot> = {},
): GoalSnapshot => ({
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

describe('GoalBanner', () => {
  it('renders nothing when goal is null', () => {
    const { container } = render(
      <GoalBanner goal={null} commandRunning={false} onCommand={jest.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders active goal with iteration progress and controls', () => {
    render(
      <GoalBanner
        goal={makeGoal()}
        commandRunning={false}
        onCommand={jest.fn()}
      />,
    );
    expect(screen.getByText(/Goal 运行中/)).toBeTruthy();
    expect(screen.getByText(/18\/256/)).toBeTruthy();
    expect(
      screen.getByText(/修复全部失败测试并保持公开 API 不变/),
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: /暂停/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /取消/ })).toBeTruthy();
    // active 状态不显示恢复按钮
    expect(screen.queryByRole('button', { name: /恢复/ })).toBeNull();
  });

  it('shows resume instead of pause for paused goal and calls command', async () => {
    const onCommand = jest.fn().mockResolvedValue('Goal 已恢复 active');
    render(
      <GoalBanner
        goal={makeGoal({ phase: 'paused', statusReason: 'user' })}
        commandRunning={false}
        onCommand={onCommand}
      />,
    );
    expect(screen.getByText(/Goal 已暂停/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /暂停/ })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /恢复/ }));
    await screen.findByText(/Goal 已恢复 active/);
    expect(onCommand).toHaveBeenCalledWith('resume', undefined);
  });

  it('hides controls for terminal goal and shows terminal time', () => {
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
    expect(screen.getByText(/Goal 已完成/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /暂停/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /恢复/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /取消/ })).toBeNull();
  });

  it('disables buttons while a command is running', () => {
    render(
      <GoalBanner
        goal={makeGoal()}
        commandRunning
        onCommand={jest.fn()}
      />,
    );
    expect(
      (screen.getByRole('button', { name: /暂停/ }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });
});
