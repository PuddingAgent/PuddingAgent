// ── turnContentBlocks 纯投影验收（AgentTurnCard 重构 2026-08-25）────────────
// 固定输入（验收基准）：
//   1  text A
//   2  reasoning R1
//   3  tool T1 requested
//   4  tool T1 completed
//   5  text B
//   6  reasoning R2
//   7  tool T2 requested/completed
//   8  tool T3 requested/completed
//   9  text C
//   10 reasoning R3
//   11 tool T4 requested
// 必须得到：Text A → Group(2–4) → Text B → Group(6–8) → Text C → Group(10–11)。
import { projectExecutionFlow } from './executionFlowProjector';
import type { ExecutionFlowEvent } from './executionFlowProjector';
import { buildTurnContentBlocks, deriveStatsFromProjection } from './turnContentBlocks';

const OCCURRED_AT = '2026-08-25T08:00:00.000Z';

function ev(type: string, seq: number, over: Record<string, unknown> = {}): any {
  return {
    eventId: `e${seq}`,
    sequence: seq,
    occurredAt: OCCURRED_AT,
    runId: 'run-1',
    turnId: 'turn-1',
    type,
    ...over,
  } as ExecutionFlowEvent;
}

const baseEvents: ExecutionFlowEvent[] = [
  ev('message.content.appended', 1, { delta: '文本A' }),
  ev('message.thinking_summary.appended', 2, { delta: 'R1' }),
  ev('tool.call.requested', 3, { toolCallId: 't1', name: 'x', arguments: '{}' }),
  ev('tool.call.completed', 4, { toolCallId: 't1', name: 'x', exitCode: 0, output: 'ok' }),
  ev('message.content.appended', 5, { delta: '文本B' }),
  ev('message.thinking_summary.appended', 6, { delta: 'R2' }),
  ev('tool.call.requested', 7, { toolCallId: 't2', name: 'y', arguments: '{}' }),
  ev('tool.call.completed', 7.5 as number, { toolCallId: 't2', name: 'y', exitCode: 0, output: 'ok' }),
  ev('tool.call.requested', 8, { toolCallId: 't3', name: 'z', arguments: '{}' }),
  ev('tool.call.completed', 8.5 as number, { toolCallId: 't3', name: 'z', exitCode: 0, output: 'ok' }),
  ev('message.content.appended', 9, { delta: '文本C' }),
  ev('message.thinking_summary.appended', 10, { delta: 'R3' }),
  ev('tool.call.requested', 11, { toolCallId: 't4', name: 'w', arguments: '{}' }),
];

function kindsOf(blocks: ReturnType<typeof buildTurnContentBlocks>): string[] {
  return blocks.map((block) =>
    block.kind === 'text' ? 'text' : `group(${block.nodes.map((n) => n.kind).join(',')})`,
  );
}

describe('buildTurnContentBlocks（固定 11 事件验收）', () => {
  it('正文 ⇄ 行为组按 sequence 交错；每个最大连续非正文序列成组', () => {
    const blocks = buildTurnContentBlocks(projectExecutionFlow(baseEvents).nodes);
    expect(kindsOf(blocks)).toEqual([
      'text',
      'group(reasoning,tool)',
      'text',
      'group(reasoning,tool,tool)',
      'text',
      'group(reasoning,tool)',
    ]);
    const texts = blocks.filter((b) => b.kind === 'text');
    expect(texts.map((b) => (b.kind === 'text' ? b.node.text : ''))).toEqual([
      '文本A',
      '文本B',
      '文本C',
    ]);
  });

  it('T4 result 到达：原位置更新，组 key 不变', () => {
    const before = buildTurnContentBlocks(projectExecutionFlow(baseEvents).nodes);
    const after = buildTurnContentBlocks(
      projectExecutionFlow([
        ...baseEvents,
        ev('tool.call.completed', 12, { toolCallId: 't4', name: 'w', exitCode: 0, output: 'done' }),
      ]).nodes,
    );
    expect(after.map((b) => b.key)).toEqual(before.map((b) => b.key));
    const tailGroup = after[after.length - 1];
    expect(tailGroup.kind).toBe('activity-group');
    if (tailGroup.kind === 'activity-group') {
      expect(tailGroup.nodes).toHaveLength(2);
      // tool result 只更新原 ToolNode，不得改变所在组或 sequence（§一规则）：
      // 组 lastSequence 仍锚定在 requested（11），不随后移。
      expect(tailGroup.lastSequence).toBe(11);
      expect(tailGroup.hasRunningNode).toBe(false);
    }
  });

  it('text D 到达：第三组变为非尾部（消费层折叠默认值随之翻转），Text D 追加末尾', () => {
    const blocks = buildTurnContentBlocks(
      projectExecutionFlow([
        ...baseEvents,
        ev('tool.call.completed', 12, { toolCallId: 't4', name: 'w', exitCode: 0, output: 'done' }),
        ev('message.content.appended', 13, { delta: '文本D' }),
      ]).nodes,
    );
    expect(blocks[blocks.length - 1].kind).toBe('text');
    // 尾部块不再是行为组：此前尾部组在消费层自动转为「历史组默认折叠」。
    expect(blocks[blocks.length - 2].kind).toBe('activity-group');
  });

  it('重放等价：同事件集乱序输入 → 块结构深度一致（SSE/刷新/重连三态同构）', () => {
    const canonical = buildTurnContentBlocks(projectExecutionFlow(baseEvents).nodes);
    const order = [5, 0, 10, 3, 8, 1, 6, 11, 4, 12, 2, 9, 7];
    expect(order).toHaveLength(baseEvents.length);
    const shuffled = projectExecutionFlow(
      order.map((i) => baseEvents[i]),
    ).nodes;
    expect(buildTurnContentBlocks(shuffled)).toEqual(canonical);
  });

  it('组摘要：段数/工具数/时长来自服务端事实；缺失时间不伪造', () => {
    const blocks = buildTurnContentBlocks(projectExecutionFlow(baseEvents).nodes);
    const group = blocks[1];
    expect(group.kind).toBe('activity-group');
    if (group.kind === 'activity-group') {
      expect(group.summary.reasoningCount).toBe(1);
      expect(group.summary.toolCount).toBe(1);
      expect(group.summary.durationMs).toBe(0); // 固定 occurredAt：同时刻差为 0
      expect(group.hasRunningNode).toBe(false);
    }
  });

  it('运行中工具/委派 → hasRunningNode=true（尾部组展开判据）', () => {
    const blocks = buildTurnContentBlocks(projectExecutionFlow(baseEvents).nodes);
    const tailGroup = blocks[blocks.length - 1];
    if (tailGroup.kind === 'activity-group') {
      expect(tailGroup.hasRunningNode).toBe(true); // T4 尚无 result
    }
  });

  it('terminal 节点不进块流；空文本段不产生正文块', () => {
    const blocks = buildTurnContentBlocks(
      projectExecutionFlow([
        ev('message.content.appended', 1, { delta: '' }),
        ev('turn.completed', 2, { reply: 'x' }),
      ]).nodes,
    );
    expect(blocks).toHaveLength(0);
  });

  it('统计：reasoning 段数与工具数（含子调用）', () => {
    const stats = deriveStatsFromProjection(projectExecutionFlow(baseEvents).nodes);
    expect(stats.reasoningSegments).toBe(3);
    expect(stats.toolCount).toBe(4);
  });
});
