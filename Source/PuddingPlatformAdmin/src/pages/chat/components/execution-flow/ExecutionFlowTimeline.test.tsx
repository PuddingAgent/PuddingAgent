// ── 行为链 P2: ExecutionFlowTimeline 交错时间线测试 ───────────────────────
// 验收（Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md §3.4）：
//  - 路径 B（投影 nodes）：reasoning → tool → reasoning → delegation 按真实
//    sequence 顺序交错，message/terminal 不进时间线；
//  - 路径 A（processItems adapter）：同一 entry 结构、同一渲染顺序；
//  - 尾部连续 reasoning 段标记当前段（run 活跃），其余段完成态；
//  - 占位空壳工具过滤；相邻委派聚合为一个 DelegationRow；
//  - 统计派生：段数 / 工具数（含子调用）。
import { render } from '@testing-library/react';
import * as React from 'react';
import type { ExecutionFlowEvent } from '../../projections/executionFlowProjector';
import { projectExecutionFlow } from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import {
  buildEntriesFromProcessItems,
  buildEntriesFromProjection,
  deriveStatsFromProcessItems,
  deriveStatsFromProjection,
  deriveTrailingMessageNode,
  ExecutionFlowTimeline,
} from './ExecutionFlowTimeline';

const seqEvent = (
  sequence: number,
  extra: Record<string, unknown>,
): ExecutionFlowEvent =>
  ({
    eventId: `evt-${sequence}`,
    sequence,
    occurredAt: new Date(1000 + sequence * 100).toISOString(),
    runId: 'run-1',
    turnId: 'turn-1',
    ...extra,
  }) as ExecutionFlowEvent;

/** 典型交错 turn：推理 → 工具 → 再推理 → 工具 → 委派 → 正文 → 终态。 */
const makeEvents = (): ExecutionFlowEvent[] => [
  seqEvent(1, { type: 'message.thinking_summary.appended', delta: '先想想' }),
  seqEvent(2, {
    type: 'tool.call.requested',
    name: 'shell',
    toolCallId: 'call-1',
    arguments: '{"command":"ls"}',
  }),
  seqEvent(3, {
    type: 'tool.call.completed',
    name: 'shell',
    toolCallId: 'call-1',
    output: 'file-a\nfile-b',
    durationMs: 800,
  }),
  seqEvent(4, { type: 'message.thinking_summary.appended', delta: '看到了文件' }),
  seqEvent(5, {
    type: 'tool.call.requested',
    name: 'file_read',
    toolCallId: 'call-2',
    arguments: '{"path":"a.ts"}',
  }),
  seqEvent(6, {
    type: 'tool.call.completed',
    name: 'file_read',
    toolCallId: 'call-2',
    output: 'const a = 1;',
    durationMs: 120,
  }),
  seqEvent(7, { type: 'subagent.spawned', subAgentId: 'sub-1', task: '调研子任务' }),
  seqEvent(8, { type: 'message.content.appended', delta: '最终回答' }),
  seqEvent(9, { type: 'turn.completed' }),
];

const makeTimelineItem = (
  id: string,
  type: TimelineItem['type'],
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  type,
  timestamp: Date.parse('2026-08-23T00:00:00Z') + Number(id.split('-')[1] ?? 0) * 100,
  collapsed: false,
  ...extra,
});

describe('ExecutionFlowTimeline（路径 B：投影 nodes）', () => {
  it('按真实 sequence 交错：reasoning → tool → reasoning → tool → delegation；message/terminal 不进时间线', () => {
    const projection = projectExecutionFlow(makeEvents());
    const entries = buildEntriesFromProjection(projection, false);
    expect(entries.map((entry) => entry.kind)).toEqual([
      'reasoning',
      'tool',
      'reasoning',
      'tool',
      'delegation',
    ]);
  });

  it('run 活跃且尾部为 reasoning：仅尾部段 isCurrent（扫光 + 最新行摘要），先前段完成态', () => {
    const events = [
      seqEvent(1, { type: 'message.thinking_summary.appended', delta: '第一段' }),
      seqEvent(2, { type: 'tool.call.requested', name: 'shell', toolCallId: 'c1' }),
      seqEvent(3, { type: 'tool.call.completed', name: 'shell', toolCallId: 'c1' }),
      seqEvent(4, { type: 'message.thinking_summary.appended', delta: '第二段' }),
    ];
    const entries = buildEntriesFromProjection(projectExecutionFlow(events), true);
    const reasoning = entries.filter((entry) => entry.kind === 'reasoning');
    expect(reasoning).toHaveLength(2);
    expect(reasoning[0].isCurrent).toBe(false);
    expect(reasoning[1].isCurrent).toBe(true);
    // run 结束后全部完成态
    const settled = buildEntriesFromProjection(projectExecutionFlow(events), false);
    expect(
      settled.filter((entry) => entry.kind === 'reasoning').every((entry) => !entry.isCurrent),
    ).toBe(true);
  });

  it('渲染交错 DOM：testid 顺序 = reasoning → toolcall → reasoning → delegation', () => {
    render(
      <ExecutionFlowTimeline
        projection={projectExecutionFlow(makeEvents())}
        isRunActive={false}
      />,
    );
    const order = Array.from(
      document.querySelectorAll(
        '[data-testid="reasoning-disclosure-row"], [data-testid="toolcall-row"], [data-testid="delegation-row"]',
      ),
    ).map((el) => el.getAttribute('data-testid'));
    expect(order).toEqual([
      'reasoning-disclosure-row',
      'toolcall-row',
      'reasoning-disclosure-row',
      'toolcall-row',
      'delegation-row',
    ]);
  });

  it('空投影 / 无可渲染节点 → null（不占用布局）', () => {
    const { container } = render(
      <ExecutionFlowTimeline projection={projectExecutionFlow([])} />,
    );
    expect(container.firstChild).toBeNull();
  });
});

describe('ExecutionFlowTimeline（正文分段交错：messageSegments）', () => {
  /** 交错 turn：文本1 → 推理 → 工具 → 文本2(尾段) → 终态。 */
  const makeSegmentEvents = (): ExecutionFlowEvent[] => [
    seqEvent(1, { type: 'message.content.appended', delta: '先说明一下' }),
    seqEvent(2, { type: 'message.thinking_summary.appended', delta: '思考' }),
    seqEvent(3, {
      type: 'tool.call.requested',
      name: 'shell',
      toolCallId: 'call-1',
      arguments: '{"command":"ls"}',
    }),
    seqEvent(4, {
      type: 'tool.call.completed',
      name: 'shell',
      toolCallId: 'call-1',
      output: 'ok',
      durationMs: 100,
    }),
    seqEvent(5, { type: 'message.content.appended', delta: '最终回答' }),
    seqEvent(6, { type: 'turn.completed' }),
  ];

  it('deriveTrailingMessageNode：最后一个 message 且其后仅 terminal → 尾段', () => {
    const projection = projectExecutionFlow(makeSegmentEvents());
    const trailing = deriveTrailingMessageNode(projection);
    expect(trailing?.kind).toBe('message');
    expect(trailing?.text).toBe('最终回答');
  });

  it('deriveTrailingMessageNode：最后文本之后还有工具（run 进行中）→ 无尾段', () => {
    const projection = projectExecutionFlow([
      seqEvent(1, { type: 'message.content.appended', delta: '文本1' }),
      seqEvent(2, {
        type: 'tool.call.requested',
        name: 'shell',
        toolCallId: 'call-1',
      }),
    ]);
    expect(deriveTrailingMessageNode(projection)).toBeUndefined();
  });

  it('buildEntriesFromProjection 带 messageSegments：中间文本段内联，尾段跳过', () => {
    const projection = projectExecutionFlow(makeSegmentEvents());
    const trailing = deriveTrailingMessageNode(projection);
    const entries = buildEntriesFromProjection(projection, false, {
      trailingKey: trailing?.key,
    });
    expect(entries.map((entry) => entry.kind)).toEqual([
      'message',
      'reasoning',
      'tool',
    ]);
    const segment = entries.find((entry) => entry.kind === 'message');
    expect(segment?.kind === 'message' && segment.node.text).toBe('先说明一下');
  });

  it('未提供 messageSegments：message 不进时间线（回退整块正文行为）', () => {
    const projection = projectExecutionFlow(makeSegmentEvents());
    const entries = buildEntriesFromProjection(projection, false);
    expect(entries.some((entry) => entry.kind === 'message')).toBe(false);
  });

  it('渲染交错 DOM：文本段 → 推理行 → 工具行（尾段不出现）', () => {
    const projection = projectExecutionFlow(makeSegmentEvents());
    const trailing = deriveTrailingMessageNode(projection);
    render(
      <ExecutionFlowTimeline
        projection={projection}
        isRunActive={false}
        messageSegments={{ trailingKey: trailing?.key }}
      />,
    );
    const order = Array.from(
      document.querySelectorAll(
        '[data-testid="timeline-message-segment"], [data-testid="reasoning-disclosure-row"], [data-testid="toolcall-row"], [data-testid="delegation-row"]',
      ),
    ).map((el) => el.getAttribute('data-testid'));
    expect(order).toEqual([
      'timeline-message-segment',
      'reasoning-disclosure-row',
      'toolcall-row',
    ]);
  });
});

describe('ExecutionFlowTimeline（路径 A：processItems adapter）', () => {
  const items: TimelineItem[] = [
    makeTimelineItem('t-1', 'thinking', { text: '先想想' }),
    makeTimelineItem('t-2', 'tool_call', {
      name: 'shell',
      toolCallId: 'call-1',
      arguments: '{"command":"ls"}',
    }),
    makeTimelineItem('t-3', 'tool_result', {
      toolCallId: 'call-1',
      output: 'file-a',
      status: 'success',
    }),
    makeTimelineItem('t-4', 'thinking', { text: '看到了文件' }),
    makeTimelineItem('t-5', 'subagent_spawned', { name: 'sub-1', text: '调研子任务' }),
    makeTimelineItem('t-6', 'subagent_completed', { name: 'sub-1', status: 'success' }),
  ];

  it('与路径 B 同构：reasoning → tool → reasoning → delegation', () => {
    const entries = buildEntriesFromProcessItems(items, false);
    expect(entries.map((entry) => entry.kind)).toEqual([
      'reasoning',
      'tool',
      'reasoning',
      'delegation',
    ]);
  });

  it('连续 thinking 合并为一段；run 活跃时尾部段 current', () => {
    const entries = buildEntriesFromProcessItems(
      [
        makeTimelineItem('t-1', 'thinking', { text: '行一' }),
        makeTimelineItem('t-2', 'thinking', { text: '行二' }),
      ],
      true,
    );
    expect(entries).toHaveLength(1);
    expect(entries[0].kind === 'reasoning' && entries[0].isCurrent).toBe(true);
  });

  it('相邻委派聚合为一个 delegation entry', () => {
    const entries = buildEntriesFromProcessItems(
      [
        makeTimelineItem('t-1', 'subagent_spawned', { name: 'sub-1', text: '任务一' }),
        makeTimelineItem('t-2', 'subagent_spawned', { name: 'sub-2', text: '任务二' }),
      ],
      false,
    );
    expect(entries).toHaveLength(1);
    expect(entries[0].kind === 'delegation' && entries[0].nodes).toHaveLength(2);
  });

  it('渲染路径 A DOM 交错顺序', () => {
    render(<ExecutionFlowTimeline processItems={items} isRunActive={false} />);
    const order = Array.from(
      document.querySelectorAll(
        '[data-testid="reasoning-disclosure-row"], [data-testid="toolcall-row"], [data-testid="delegation-row"]',
      ),
    ).map((el) => el.getAttribute('data-testid'));
    expect(order).toEqual([
      'reasoning-disclosure-row',
      'toolcall-row',
      'reasoning-disclosure-row',
      'delegation-row',
    ]);
  });
});

describe('时间线统计派生（TurnStatsLine 数据源）', () => {
  it('投影统计：2 段思考 / 2 工具；processItems 统计一致', () => {
    const projection = projectExecutionFlow(makeEvents());
    expect(deriveStatsFromProjection(projection)).toEqual({
      reasoningSegments: 2,
      toolCount: 2,
    });
    expect(deriveStatsFromProcessItems([])).toEqual({
      reasoningSegments: 0,
      toolCount: 0,
    });
  });

  it('工具树子调用计入 toolCount', () => {
    const events = [
      seqEvent(1, {
        type: 'tool.call.requested',
        name: 'parent',
        toolCallId: 'root-1',
      }),
      seqEvent(2, {
        type: 'tool.call.requested',
        name: 'child',
        toolCallId: 'child-1',
        parentToolCallId: 'root-1',
      }),
      seqEvent(3, {
        type: 'tool.call.completed',
        name: 'child',
        toolCallId: 'child-1',
      }),
      seqEvent(4, {
        type: 'tool.call.completed',
        name: 'parent',
        toolCallId: 'root-1',
      }),
    ];
    expect(deriveStatsFromProjection(projectExecutionFlow(events))).toEqual({
      reasoningSegments: 0,
      toolCount: 2,
    });
  });
});
