import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { TaskCard } from './TaskCard';
import type { TaskActions } from './TaskCard';
import type { TaskDto } from './types';

function makeTask(overrides: Partial<TaskDto> = {}): TaskDto {
  return {
    taskId: 'task-1',
    workspaceId: 'default',
    title: '示例任务',
    status: 'Backlog',
    boardColumn: 'Backlog',
    allowedTransitions: [],
    priority: 'p3',
    executionWindow: 'inherit',
    sortOrder: 0,
    version: 1,
    createdAtUtc: '2026-08-16T00:00:00Z',
    updatedAtUtc: '2026-08-16T00:00:00Z',
    ...overrides,
  };
}

const actions: TaskActions = {
  onOpen: jest.fn(),
  onEdit: jest.fn(),
  onAssign: jest.fn(),
  onRunNow: jest.fn(),
  onCommand: jest.fn(),
};

describe('TaskCard（Quiet UI + 动作显隐）', () => {
  it('渲染标题、优先级、状态徽标（结论优先）', () => {
    render(<TaskCard task={makeTask({ title: '修 Bug', priority: 'p0' })} actions={actions} />);
    expect(screen.getByText('修 Bug')).toBeTruthy();
    expect(screen.getByText('P0')).toBeTruthy();
    expect(screen.getByText('待规划')).toBeTruthy();
  });

  it('Failed 卡显示失败原因（原因可见）', () => {
    render(
      <TaskCard
        task={makeTask({
          status: 'Failed',
          boardColumn: 'Failed',
          failureCode: 'agent.unavailable',
          failureReason: 'Agent 离线',
        })}
        actions={actions}
      />,
    );
    expect(screen.getByText(/Agent 离线/)).toBeTruthy();
    expect(screen.getByText('已失败')).toBeTruthy();
  });

  it('Blocked 卡显示阻塞原因', () => {
    render(
      <TaskCard
        task={makeTask({
          status: 'Blocked',
          boardColumn: 'InProgress',
          blockerKind: 'approval_required',
          blockerReason: '等待审批',
        })}
        actions={actions}
      />,
    );
    expect(screen.getByText(/等待审批/)).toBeTruthy();
    expect(screen.getByText('已阻塞')).toBeTruthy();
  });

  it('动作菜单按状态显隐：Backlog 仅显示编辑/详情，不显示执行/指派', async () => {
    render(<TaskCard task={makeTask({ status: 'Backlog' })} actions={actions} />);
    fireEvent.click(screen.getByLabelText('任务操作'));

    expect(await screen.findByText('查看详情')).toBeTruthy();
    expect(await screen.findByText('编辑')).toBeTruthy();
    expect(screen.queryByText('执行')).toBeNull();
    expect(screen.queryByText('指派')).toBeNull();
  });

  it('Failed 卡动作菜单显示「重新打开」', async () => {
    render(
      <TaskCard
        task={makeTask({ status: 'Failed', boardColumn: 'Failed' })}
        actions={actions}
      />,
    );
    fireEvent.click(screen.getByLabelText('任务操作'));

    expect(await screen.findByText('重新打开')).toBeTruthy();
    expect(await screen.findByText('归档')).toBeTruthy();
  });
});
