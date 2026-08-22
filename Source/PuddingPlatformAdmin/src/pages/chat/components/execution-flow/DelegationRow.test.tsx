// ── CU-09: DelegationRow 父级委派摘要测试 ──────────────────────────────────
// 验收（split-plan CU-09 验收标准 5）：
//  - 折叠态计数文案（子代理 · N 个运行中/已完成/失败）
//  - 展开态每子代理摘要+模型+状态+检查器入口
//  - 无子代理不渲染（空节点 → null）
//  - 键盘展开（Enter/Space，复用 ExecutionDisclosureRow chrome）
//  - 终态摘要保留（父 turn 完成后仍显示已完成/失败计数）
//  - 最长运行时间估算（基于 occurredAt，无时间源不伪造）
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { DelegationNode } from '../../projections/executionFlowProjector';
import { buildDelegationNodesFromProcessItems, DelegationRow } from './DelegationRow';
import type { TimelineItem } from '../../types';

const RUNNING_NODE: DelegationNode = {
  kind: 'delegation',
  key: 'delegation:run-1',
  firstEventId: 'evt-spawned-1',
  sequence: 0,
  occurredAt: new Date(Date.now() - 45_000).toISOString(),
  sourceEventIds: ['evt-spawned-1'],
  subAgentId: 'run-1',
  state: 'running',
  template: 'deepseek-v4-flash',
  model: 'deepseek-v4-flash',
  taskSummary: '检索仓库结构与调用链',
};

const COMPLETED_NODE: DelegationNode = {
  kind: 'delegation',
  key: 'delegation:run-2',
  firstEventId: 'evt-spawned-2',
  sequence: 1,
  occurredAt: new Date(Date.now() - 120_000).toISOString(),
  sourceEventIds: ['evt-spawned-2', 'evt-completed-2'],
  subAgentId: 'run-2',
  state: 'completed',
  template: 'bigmodel/glm-5.3',
  model: 'bigmodel/glm-5.3',
  taskSummary: '生成单元测试与构建验证',
  success: true,
  replySummary: '已生成 12 个用例，构建通过',
};

const FAILED_NODE: DelegationNode = {
  kind: 'delegation',
  key: 'delegation:run-3',
  firstEventId: 'evt-spawned-3',
  sequence: 2,
  occurredAt: new Date(Date.now() - 240_000).toISOString(),
  sourceEventIds: ['evt-spawned-3', 'evt-completed-3'],
  subAgentId: 'run-3',
  state: 'failed',
  model: 'deepseek-v4-flash',
  taskSummary: '执行回归测试',
  success: false,
  error: '断言失败：期望 3 个用例通过',
};

describe('DelegationRow', () => {
  it('折叠态：计数文案（运行中/已完成/失败）', () => {
    render(<DelegationRow nodes={[RUNNING_NODE, COMPLETED_NODE, FAILED_NODE]} />);
    const row = screen.getByTestId('delegation-row');
    expect(row.getAttribute('role')).toBe('button');
    expect(row.getAttribute('aria-expanded')).toBe('false');
    const title = screen.getByTestId('delegation-title');
    expect(title.textContent).toBe('子代理 · 1 个运行中/1 个已完成/1 个失败');
  });

  it('折叠态：仅单项计数时省略零项', () => {
    render(<DelegationRow nodes={[COMPLETED_NODE]} />);
    expect(screen.getByTestId('delegation-title').textContent).toBe(
      '子代理 · 1 个已完成',
    );
  });

  it('折叠态：最长运行时间基于 running 节点 occurredAt 估算（不伪造精确时长）', () => {
    const now = Date.now();
    const runningNode: DelegationNode = {
      ...RUNNING_NODE,
      occurredAt: new Date(now - 45_000).toISOString(),
    };
    render(<DelegationRow nodes={[runningNode]} now={now} />);
    const elapsed = screen.getByTestId('delegation-elapsed');
    expect(elapsed.textContent).toContain('45s');
  });

  it('折叠态：running 节点无 occurredAt 时不显示时长（仅状态文案）', () => {
    const noTimeNode: DelegationNode = { ...RUNNING_NODE, occurredAt: undefined };
    render(<DelegationRow nodes={[noTimeNode]} now={Date.now()} />);
    expect(screen.queryByTestId('delegation-elapsed')).toBeNull();
    expect(screen.getByTestId('delegation-title').textContent).toBe(
      '子代理 · 1 个运行中',
    );
  });

  it('展开态：每子代理显示摘要+模型+状态+检查器入口', () => {
    const onOpenInspector = jest.fn();
    render(
      <DelegationRow
        nodes={[RUNNING_NODE, COMPLETED_NODE, FAILED_NODE]}
        onOpenInspector={onOpenInspector}
      />,
    );
    fireEvent.click(screen.getByTestId('delegation-row'));
    const list = screen.getByTestId('delegation-list');
    expect(list).toBeTruthy();
    // 摘要
    expect(list.textContent).toContain('检索仓库结构与调用链');
    expect(list.textContent).toContain('生成单元测试与构建验证');
    // 模型
    expect(list.textContent).toContain('deepseek-v4-flash');
    expect(list.textContent).toContain('bigmodel/glm-5.3');
    // 状态
    expect(list.textContent).toContain('运行中');
    expect(list.textContent).toContain('已完成');
    expect(list.textContent).toContain('失败');
    // 检查器入口触发回调（runId/subAgentId）
    fireEvent.click(screen.getByTestId('delegation-inspector-run-2'));
    expect(onOpenInspector).toHaveBeenCalledWith('run-2');
  });

  it('展开态：不渲染子代理内部 reasoning/tool/完整结果（只含摘要字段）', () => {
    render(
      <DelegationRow
        nodes={[COMPLETED_NODE, FAILED_NODE]}
        onOpenInspector={() => undefined}
      />,
    );
    fireEvent.click(screen.getByTestId('delegation-row'));
    const list = screen.getByTestId('delegation-list');
    const listText = list.textContent ?? '';
    // 不得出现内部过程关键字（reasoning/tool 输出/完整错误堆栈）
    expect(listText).not.toContain('reasoning');
    expect(listText).not.toContain('tool_call');
    // replySummary 只用于 title 悬停，展开行主体显示 taskSummary
    expect(listText).toContain('生成单元测试与构建验证');
  });

  it('无子代理（空数组）不渲染', () => {
    const { container } = render(<DelegationRow nodes={[]} />);
    expect(screen.queryByTestId('delegation-row')).toBeNull();
    expect(container.textContent ?? '').toBe('');
  });

  it('键盘展开：Enter/Space 切换（复用 ExecutionDisclosureRow chrome）', () => {
    render(<DelegationRow nodes={[COMPLETED_NODE]} />);
    const row = screen.getByTestId('delegation-row');
    fireEvent.keyDown(row, { key: 'Enter' });
    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByTestId('delegation-list')).toBeTruthy();
    fireEvent.keyDown(row, { key: ' ' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByTestId('delegation-list')).toBeNull();
  });

  it('终态摘要保留：父 turn 完成后折叠态仍显示已完成/失败计数', () => {
    render(<DelegationRow nodes={[COMPLETED_NODE, FAILED_NODE]} />);
    expect(screen.getByTestId('delegation-title').textContent).toBe(
      '子代理 · 1 个已完成/1 个失败',
    );
  });
});

describe('buildDelegationNodesFromProcessItems', () => {
  const now = Date.now();

  const spawnedItem = (id: string, text: string): TimelineItem => ({
    id: `${id}-spawned`,
    eventId: `evt-${id}-spawned`,
    type: 'subagent_spawned',
    name: id,
    text,
    status: 'running',
    timestamp: now - 60_000,
    collapsed: true,
  });

  const progressItem = (id: string, text: string): TimelineItem => ({
    id: `${id}-progress`,
    eventId: `evt-${id}-progress`,
    type: 'subagent_progress',
    name: id,
    text,
    status: 'running',
    timestamp: now - 30_000,
    collapsed: true,
  });

  const completedItem = (id: string, output: string): TimelineItem => ({
    id: `${id}-completed`,
    eventId: `evt-${id}-completed`,
    type: 'subagent_completed',
    name: id,
    output,
    status: 'success',
    timestamp: now - 10_000,
    collapsed: true,
  });

  const failedItem = (id: string, message: string): TimelineItem => ({
    id: `${id}-failed`,
    eventId: `evt-${id}-failed`,
    type: 'subagent_completed',
    name: id,
    message,
    status: 'failed',
    timestamp: now - 10_000,
    collapsed: true,
  });

  it('spawned → running 节点（taskSummary 取 text）', () => {
    const nodes = buildDelegationNodesFromProcessItems([
      spawnedItem('run-a', '调研轮子'),
    ]);
    expect(nodes).toHaveLength(1);
    expect(nodes[0].subAgentId).toBe('run-a');
    expect(nodes[0].state).toBe('running');
    expect(nodes[0].taskSummary).toBe('调研轮子');
    expect(nodes[0].key).toBe('delegation:run-a');
  });

  it('completed → completed 终态；failed 状态 → failed 终态', () => {
    const nodes = buildDelegationNodesFromProcessItems([
      spawnedItem('run-a', '任务 A'),
      completedItem('run-a', '结果 A'),
      spawnedItem('run-b', '任务 B'),
      failedItem('run-b', '错误 B'),
    ]);
    const byId = new Map(nodes.map((node) => [node.subAgentId, node]));
    expect(byId.get('run-a')?.state).toBe('completed');
    expect(byId.get('run-a')?.success).toBe(true);
    expect(byId.get('run-a')?.replySummary).toBe('结果 A');
    expect(byId.get('run-b')?.state).toBe('failed');
    expect(byId.get('run-b')?.success).toBe(false);
    expect(byId.get('run-b')?.error).toBe('错误 B');
  });

  it('progress 保留 running 并合并最新摘要', () => {
    const nodes = buildDelegationNodesFromProcessItems([
      spawnedItem('run-a', '初始任务'),
      progressItem('run-a', '正在生成测试'),
    ]);
    expect(nodes[0].state).toBe('running');
    expect(nodes[0].taskSummary).toBe('正在生成测试');
  });

  it('无 subagent 条目 → 空数组', () => {
    const nodes = buildDelegationNodesFromProcessItems([
      {
        id: 't1',
        type: 'tool_call',
        name: 'file_read',
        timestamp: now,
        collapsed: true,
      } as TimelineItem,
    ]);
    expect(nodes).toHaveLength(0);
  });

  it('非 subagent 条目不进入节点（过滤 thinking/tool）', () => {
    const nodes = buildDelegationNodesFromProcessItems([
      spawnedItem('run-a', '任务 A'),
      {
        id: 't2',
        type: 'thinking',
        text: '思考',
        timestamp: now,
        collapsed: true,
      } as TimelineItem,
    ]);
    expect(nodes).toHaveLength(1);
    expect(nodes[0].subAgentId).toBe('run-a');
  });
});
