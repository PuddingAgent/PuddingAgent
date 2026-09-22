/**
 * TaskTable 结构契约测试（任务看板列表视图：表头固定 + 表格独立滚动）。
 *
 * ⚠️ 诚实声明（必须保留）：jsdom **没有布局引擎**，也不应用 CSS 的布局效果。
 *    因此本测试**无法证明滚动行为正确**——「表头是否真的吸顶」「内容是否真的在
 *    自身区域内滚动」「滚动容器高度是否真的被 flex 撑开」都只能在真实浏览器里
 *    人工确认。它能防住的只有结构性回归：
 *      ① 滚动容器（data-testid="tasks-table-scroll-host"）被误删；
 *      ② 滚动容器被改成不可滚（overflow != auto）；
 *      ③ 高度兜底（minHeight）被删掉 ⇒ 祖先高度未解析时容器塌成 0、表格看不见；
 *      ④ 表格数据被挪出滚动宿主（例如被包进另一个空容器）。
 */
import { render, screen } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { TaskTable } from './TaskTable';
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

const noop = () => {};

function renderTable(items: TaskDto[]) {
  return render(
    <App>
      <TaskTable
        items={items}
        onOpen={noop}
        onBatchRemove={noop}
        onBatchStatus={noop}
      />
    </App>,
  );
}

describe('TaskTable 结构契约（表头固定 + 表格独立滚动）', () => {
  it('滚动宿主存在、可滚且保留 minHeight 兜底（祖先高度未解析也不会塌成 0）', () => {
    renderTable([makeTask({ title: '契约任务' })]);

    const host = screen.getByTestId('tasks-table-scroll-host');
    expect(host).toBeTruthy();
    expect(host.style.overflow).toBe('auto');
    // 兜底高度：设计上要求 ≥240，绝不允许出现「滚动容器高度塌成 0、表格看不见」。
    expect(Number.parseInt(host.style.minHeight, 10)).toBeGreaterThanOrEqual(240);
    // flex 收缩/增长声明：表格占满内容槽剩余高度（jsdom 无法验证布局效果）。
    expect(host.getAttribute('style')).toContain('flex');
  });

  it('表格数据仍可见，且位于滚动宿主内部（没有被藏进空容器里）', () => {
    renderTable([makeTask({ title: '契约任务' })]);

    const host = screen.getByTestId('tasks-table-scroll-host');
    const title = screen.getByText('契约任务');
    expect(title).toBeTruthy();
    expect(host.contains(title)).toBe(true);
  });

  it('滚动宿主不依赖 calc 魔法数字（祖先高度失效时仍靠 minHeight 可用）', () => {
    renderTable([makeTask({ title: '契约任务' })]);

    const host = screen.getByTestId('tasks-table-scroll-host');
    const styleAttr = host.getAttribute('style') ?? '';
    expect(styleAttr).not.toContain('calc(');
  });
});
