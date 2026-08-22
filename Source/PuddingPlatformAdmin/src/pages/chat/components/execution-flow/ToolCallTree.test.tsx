// ── CU-07: ToolCallTree 递归调用树测试 ────────────────────────────────────
// 验收（CU-07 任务书验收 2/4）：
//  - 递归渲染父子调用树（ToolNode.children 嵌套缩进）
//  - 占位过滤：placeholder=true 且无真实结果（output/error/exitCode）时隐藏
//  - 多根：多个顶层工具节点均渲染
//  - 消费点适配 buildToolTreeFromProcessItems：TimelineItem[] → ToolNode[]，
//    保留旧配对语义（toolCallId 精确配对 / 乱序正确 / 缺失 id 不猜测 / 孤儿 result 不吞）
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import type { ToolNode } from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import {
  buildToolNodesFromProcessItems,
  buildToolTreeFromProcessItems,
  pairToolCallItems,
  ToolCallTree,
} from './ToolCallTree';

const makeNode = (extra: Partial<ToolNode> = {}): ToolNode => ({
  kind: 'tool',
  key: `tool:${extra.toolCallId ?? 'call-1'}`,
  sequence: 1,
  sourceEventIds: [],
  toolCallId: 'call-1',
  state: 'completed',
  placeholder: false,
  name: 'shell',
  arguments: '{"command":"git status"}',
  output: 'On branch master',
  children: [],
  ...extra,
});

const makeCall = (
  id: string,
  name: string,
  args = '{}',
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  toolCallId: `call-${id.replace(/^c/, '')}`,
  type: 'tool_call',
  name,
  arguments: args,
  timestamp: 1000,
  collapsed: false,
  ...extra,
});

const makeResult = (
  id: string,
  name: string,
  output: string,
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  toolCallId: `call-${id.replace(/^r/, '')}`,
  type: 'tool_result',
  name,
  output,
  timestamp: 2000,
  collapsed: false,
  ...extra,
});

describe('ToolCallTree', () => {
  it('无可见根（空数组）返回 null，不占用布局', () => {
    const { container } = render(<ToolCallTree nodes={[]} />);
    expect(container.querySelector('[data-testid="toolcall-list"]')).toBeNull();
  });

  it('多根：多个顶层工具节点均渲染', () => {
    render(
      <ToolCallTree
        nodes={[
          makeNode({ key: 'tool:a', toolCallId: 'a', name: 'list_dir' }),
          makeNode({ key: 'tool:b', toolCallId: 'b', name: 'file_patch' }),
        ]}
      />,
    );
    const rows = screen.getAllByTestId('toolcall-row');
    expect(rows).toHaveLength(2);
    expect(rows[0].getAttribute('data-toolname')).toBe('list_dir');
    expect(rows[1].getAttribute('data-toolname')).toBe('file_patch');
  });

  it('递归渲染父子调用树：父行 + 缩进子列表（验收 2）', () => {
    const parent = makeNode({
      key: 'tool:parent',
      toolCallId: 'parent',
      name: 'orchestrator',
    });
    const child = makeNode({
      key: 'tool:child',
      toolCallId: 'child',
      name: 'list_dir',
    });
    const grandChild = makeNode({
      key: 'tool:grandchild',
      toolCallId: 'grandchild',
      name: 'file_read',
    });
    parent.children = [child];
    child.children = [grandChild];

    render(<ToolCallTree nodes={[parent]} />);
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(3);
    // 每层有 children 的分支各渲染一个子列表容器（parent→list_dir→file_read 共 2 层）
    expect(screen.getAllByTestId('toolcall-tree-children')).toHaveLength(2);
    const rows = screen.getAllByTestId('toolcall-row');
    expect(rows[0].getAttribute('data-toolname')).toBe('orchestrator');
    expect(rows[1].getAttribute('data-toolname')).toBe('list_dir');
    expect(rows[2].getAttribute('data-toolname')).toBe('file_read');
    // 子列表有缩进/竖线样式（注入 CSS 断言）
    const css = Array.from(document.querySelectorAll('style'))
      .map((el) => el.textContent ?? '')
      .join('\n');
    expect(css).toContain('border-left');
  });

  it('占位过滤：placeholder=true 且无真实结果（无 output/error/exitCode）隐藏；有真实结果时保留', () => {
    const placeholderVoid = makeNode({
      key: 'tool:void',
      toolCallId: 'void',
      placeholder: true,
      state: 'running',
      name: 'search',
      output: undefined,
      error: undefined,
      exitCode: undefined,
    });
    const placeholderWithOutput = makeNode({
      key: 'tool:kept',
      toolCallId: 'kept',
      placeholder: true,
      state: 'completed',
      name: 'search',
      output: 'hits: 3',
    });
    const normal = makeNode({ key: 'tool:normal', toolCallId: 'normal' });

    render(
      <ToolCallTree nodes={[placeholderVoid, placeholderWithOutput, normal]} />,
    );
    const rows = screen.getAllByTestId('toolcall-row');
    expect(rows).toHaveLength(2);
    expect(rows[0].getAttribute('data-toolname')).toBe('search');
    expect(rows[1].getAttribute('data-toolname')).toBe('shell');
  });

  it('全部为占位空节点 → 返回 null', () => {
    const { container } = render(
      <ToolCallTree
        nodes={[
          makeNode({
            key: 'tool:void',
            toolCallId: 'void',
            placeholder: true,
            state: 'running',
            output: undefined,
            error: undefined,
            exitCode: undefined,
          }),
        ]}
      />,
    );
    expect(container.querySelector('[data-testid="toolcall-list"]')).toBeNull();
  });
});

describe('buildToolTreeFromProcessItems（消费点适配）', () => {
  it('仅 tool_call 生成节点；thinking/subagent 不进入', () => {
    const nodes = buildToolNodesFromProcessItems([
      { id: 't1', type: 'thinking', text: 'x', timestamp: 1, collapsed: false },
      makeCall('c1', 'shell', '{"command":"git status"}'),
      makeResult('r1', 'shell', 'On branch master'),
    ]);
    expect(nodes).toHaveLength(1);
    expect(nodes[0].toolCallId).toBe('call-1');
    expect(nodes[0].state).toBe('completed');
  });

  it('按 toolCallId 精确配对（乱序正确）：call/result 配对、未配对=running、孤儿 result 不吞', () => {
    const nodes = buildToolNodesFromProcessItems([
      makeCall('c1', 'shell', '{"command":"git status"}', {
        toolCallId: 'call-1',
      }),
      makeCall('c2', 'search', '{"query":"retention"}', {
        toolCallId: 'call-2',
      }),
      // 乱序：search 的结果先到
      makeResult('r1', 'search', 'result for search', {
        toolCallId: 'call-2',
      }),
      makeResult('r2', 'shell', 'result for shell', {
        toolCallId: 'call-1',
      }),
      // 孤儿 result（无对应 call）不生成节点
      makeResult('r3', 'file_patch', 'orphan', { toolCallId: 'call-9' }),
    ]);
    expect(nodes).toHaveLength(2);
    const shell = nodes.find((node) => node.toolCallId === 'call-1');
    const search = nodes.find((node) => node.toolCallId === 'call-2');
    expect(shell?.state).toBe('completed');
    expect(shell?.output).toBe('result for shell');
    expect(search?.state).toBe('completed');
    expect(search?.output).toBe('result for search');
  });

  it('缺失 toolCallId 不猜测配对：call 保持 running', () => {
    const nodes = buildToolNodesFromProcessItems([
      makeCall('c1', 'shell', '{"command":"git status"}', {
        toolCallId: 'call-1',
      }),
      makeResult('r1', 'shell', 'On branch master', {
        toolCallId: undefined,
      }),
    ]);
    expect(nodes[0].state).toBe('running');
    expect(nodes[0].output).toBeUndefined();
  });

  it('显式非零 exitCode → failed；status 文案含 error/fail/cancel → failed', () => {
    const nodes = buildToolNodesFromProcessItems([
      makeCall('c1', 'file_patch', '{"path":"a.ts"}'),
      makeResult('r1', 'file_patch', 'patch rejected', {
        exitCode: 2,
        status: 'error',
      }),
      makeCall('c2', 'search', '{"query":"x"}'),
      makeResult('r2', 'search', 'hits', { exitCode: 0 }),
    ]);
    expect(nodes.find((node) => node.toolCallId === 'call-1')?.state).toBe(
      'failed',
    );
    expect(nodes.find((node) => node.toolCallId === 'call-2')?.state).toBe(
      'completed',
    );
  });

  it('parentToolCallId 明确存在且父节点存在时成树；无父挂根', () => {
    const roots = buildToolTreeFromProcessItems([
      makeCall('c1', 'orchestrator', '{"task":"run"}', {
        toolCallId: 'call-parent',
      }),
      makeCall('c2', 'list_dir', '.', {
        toolCallId: 'call-child',
        parentToolCallId: 'call-parent',
      }),
      makeCall('c3', 'file_read', 'a.ts', {
        toolCallId: 'call-child2',
        parentToolCallId: 'call-parent',
      }),
      makeCall('c4', 'top', '{}', { toolCallId: 'call-top' }),
    ]);
    expect(roots).toHaveLength(2);
    const parent = roots.find((node) => node.toolCallId === 'call-parent');
    const top = roots.find((node) => node.toolCallId === 'call-top');
    expect(parent?.children).toHaveLength(2);
    expect(
      parent?.children.map((child) => child.toolCallId),
    ).toEqual(['call-child', 'call-child2']);
    expect(top?.children).toHaveLength(0);
  });

  it('pairToolCallItems 保留 call 输入顺序且 result 不重复消费', () => {
    const pairs = pairToolCallItems([
      makeCall('c1', 'shell', 'a', { toolCallId: 'call-1' }),
      makeCall('c2', 'shell', 'b', { toolCallId: 'call-1' }),
      makeResult('r1', 'shell', 'out', { toolCallId: 'call-1' }),
    ]);
    expect(pairs).toHaveLength(2);
    expect(pairs[0].result?.id).toBe('r1');
    expect(pairs[1].result).toBeUndefined();
  });
});
