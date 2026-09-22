import { render, screen } from '@testing-library/react';
import React from 'react';
import { TaskColumn } from './TaskColumn';
import type { TaskActions } from './TaskCard';
import type { ColumnSlice, TaskDto } from './types';

/**
 * 列头徽标语义测试（2026-09-22）
 *
 * 背景：徽标原本渲染 `slice.items.length`（**已加载条数**），而首屏每列只取一页
 * （`index.tsx` limit:100）、其后由 SSE 逐条累积 ⇒ 同一列在不同时刻显示不同数字。
 * 现改为渲染服务端真值 `slice.totalCount`（与 cursor 无关的完整过滤集大小）。
 *
 * ⚠️ jsdom 没有布局引擎：本文件只验证**列头徽标**的取值与提示文案，
 * 不验证滚动/虚拟化行为（那需要真实浏览器）。
 */
const originalResizeObserver = global.ResizeObserver;

beforeAll(() => {
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterAll(() => {
  global.ResizeObserver = originalResizeObserver;
});

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
    taskType: 'general',
    requiredCapabilityIds: [],
    allowAgentFallback: true,
    autoDispatchEnabled: false,
    sortOrder: 0,
    version: 1,
    createdAtUtc: '2026-08-16T00:00:00Z',
    updatedAtUtc: '2026-08-16T00:00:00Z',
    ...overrides,
  };
}

function makeSlice(overrides: Partial<ColumnSlice> = {}): ColumnSlice {
  return {
    items: [],
    nextCursor: null,
    totalCount: 0,
    loading: false,
    loadingMore: false,
    hasMore: false,
    ...overrides,
  };
}

const actions: TaskActions = {
  onOpen: jest.fn(),
  onEdit: jest.fn(),
  onAssign: jest.fn(),
  onRunNow: jest.fn(),
  onToggleAutoDispatch: jest.fn(),
  onCommand: jest.fn(),
};

describe('TaskColumn 列头徽标 = 服务端总数（不是已加载条数）', () => {
  it('已加载 2 条、服务端 184 条 ⇒ 徽标显示 184，并提示已加载 2', () => {
    render(
      <TaskColumn
        column="Backlog"
        slice={makeSlice({
          items: [
            makeTask({ taskId: 'a' }),
            makeTask({ taskId: 'b', title: '第二个任务' }),
          ],
          totalCount: 184,
          nextCursor: '5|a',
          hasMore: true,
        })}
        actions={actions}
        onLoadMore={jest.fn()}
      />,
    );

    // 徽标取值 = 服务端总数（若回退为 items.length，这里会得到 '2'）
    const badge = screen.getByTitle('已加载 2 / 共 184');
    expect(badge.textContent).toBe('184');
    // 已加载量单独提示，不冒充总数
    expect(screen.getByText('已加载 2')).toBeTruthy();
  });

  it('已加载量等于总数时不再重复提示', () => {
    render(
      <TaskColumn
        column="Todo"
        slice={makeSlice({
          items: [makeTask({ taskId: 'a', boardColumn: 'Todo', status: 'Ready' })],
          totalCount: 1,
        })}
        actions={actions}
        onLoadMore={jest.fn()}
      />,
    );

    expect(screen.getByTitle('已加载 1 / 共 1').textContent).toBe('1');
    expect(screen.queryByText('已加载 1')).toBeNull();
  });
});
