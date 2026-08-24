// ── TurnSurfaceStore 单测（chat UI 行为链重构 2026-08-24）──────────────────
// 覆盖：processItems→flow events 适配、跨源 eventId 幂等、别名归并、
// 交错节点顺序、toolCallId 配对、委派跨事件分组、终态轨迹不消失。
import {
  processItemsToFlowEvents,
  TurnSurfaceStore,
} from './turnSurfaceStore';
import type { ProcessSummaryItem } from '../client/types';

const ts = (i: number) => new Date(Date.UTC(2026, 7, 24, 10, 0, i)).toISOString();

const item = (overrides: Partial<ProcessSummaryItem> & { id: string }): ProcessSummaryItem => ({
  kind: 'text',
  status: 'done',
  text: '',
  timestamp: ts(0),
  ...overrides,
});

describe('processItemsToFlowEvents', () => {
  it('maps text/thinking/tool/delegation kinds to canonical flow events', () => {
    const events = processItemsToFlowEvents(
      [
        item({ id: 'e1', kind: 'text', text: '第一段' }),
        item({ id: 'e2', kind: 'thinking', text: '思考一下' }),
        item({ id: 'e3', kind: 'tool_call', name: 'shell', toolCallId: 'call-1', text: 'shell echo' }),
        item({ id: 'e4', kind: 'tool_result', name: 'shell', toolCallId: 'call-1', status: 'success', output: 'ok', exitCode: 0, text: 'ok' }),
        item({ id: 'e5', kind: 'text', text: '第二段' }),
        item({ id: 'e6', kind: 'delegation', status: 'running', name: '前端审查', delegationRunId: 'sub-1', text: '审查计划' }),
        item({ id: 'e7', kind: 'delegation', status: 'success', delegationRunId: 'sub-1', text: '审查完成' }),
      ],
      { turnId: 'turn-1' },
    );
    expect(events.map((e) => e.type)).toEqual([
      'message.content.appended',
      'message.thinking_summary.appended',
      'tool.call.requested',
      'tool.call.completed',
      'message.content.appended',
      'subagent.spawned',
      'subagent.completed',
    ]);
    // sequence 单调（base + index），顺序事实保留。
    const sequences = events.map((e) => e.sequence);
    expect(sequences).toEqual([...sequences].sort((a, b) => a - b));
    // 未知 kind 不投影。
    const unknown = processItemsToFlowEvents(
      [item({ id: 'x', kind: 'future_kind', text: '??' })],
      { turnId: 'turn-1' },
    );
    expect(unknown).toHaveLength(0);
  });
});

describe('TurnSurfaceStore', () => {
  it('builds interleaved node stream from process items and pairs tools by toolCallId', () => {
    const store = new TurnSurfaceStore();
    store.applyEvents(
      processItemsToFlowEvents(
        [
          item({ id: 'e1', kind: 'text', text: '文本1' }),
          item({ id: 'e2', kind: 'thinking', text: '思考1' }),
          item({ id: 'e3', kind: 'tool_call', name: 'git', toolCallId: 'c1', text: 'git status' }),
          item({ id: 'e4', kind: 'tool_result', name: 'git', toolCallId: 'c1', status: 'success', exitCode: 0, output: 'clean', text: 'clean' }),
          item({ id: 'e5', kind: 'text', text: '文本2' }),
        ],
        { turnId: 'turn-1' },
      ),
    );
    const projection = store.getProjection('turn-1');
    expect(projection).toBeDefined();
    expect(projection!.nodes.map((n) => n.kind)).toEqual([
      'message',
      'reasoning',
      'tool',
      'message',
    ]);
    const tool = projection!.nodes.find((n) => n.kind === 'tool');
    expect(tool && tool.kind === 'tool' && tool.state).toBe('completed');
  });

  it('groups delegation spawn/terminal into one node by delegationRunId', () => {
    const store = new TurnSurfaceStore();
    store.applyEvents(
      processItemsToFlowEvents(
        [
          item({ id: 's1', kind: 'delegation', status: 'running', name: 'tpl-a', delegationRunId: 'sub-9', text: '任务' }),
          item({ id: 's2', kind: 'delegation', status: 'error', delegationRunId: 'sub-9', text: '失败原因' }),
        ],
        { turnId: 'turn-1' },
      ),
    );
    const projection = store.getProjection('turn-1')!;
    const delegations = projection.nodes.filter((n) => n.kind === 'delegation');
    expect(delegations).toHaveLength(1);
    expect(delegations[0]).toMatchObject({ subAgentId: 'sub-9', state: 'failed' });
  });

  it('dedupes events by eventId across repeated applies (bootstrap + live replay)', () => {
    const store = new TurnSurfaceStore();
    const events = processItemsToFlowEvents(
      [item({ id: 'e1', kind: 'thinking', text: 'a' }), item({ id: 'e2', kind: 'thinking', text: 'b' })],
      { turnId: 'turn-1' },
    );
    const first = store.applyEvents(events);
    const second = store.applyEvents(events);
    expect(first.applied).toBe(2);
    expect(second.applied).toBe(0);
    const projection = store.getProjection('turn-1')!;
    expect(projection.nodes).toHaveLength(1);
  });

  it('resolves aliases (messageId/runId/commandClientId) to the same surface', () => {
    const store = new TurnSurfaceStore();
    store.linkAlias('turn-1', 'msg-1');
    store.linkAlias('turn-1', 'run-1');
    store.applyEvents(
      processItemsToFlowEvents([item({ id: 'e1', kind: 'thinking', text: 'x' })], {
        turnId: 'turn-1',
      }),
    );
    expect(store.resolveTurnId('msg-1')).toBe('turn-1');
    expect(store.getProjection('run-1')).toBe(store.getProjection('turn-1'));
  });

  it('keeps node stream after terminal (late events never clear nodes)', () => {
    const store = new TurnSurfaceStore();
    store.applyEvents([
      {
        type: 'message.content.appended',
        eventId: 'live-1',
        sequence: 1,
        occurredAt: ts(1),
        runId: 'run-1',
        turnId: 'turn-1',
        delta: '回答正文',
      },
      {
        type: 'tool.call.requested',
        eventId: 'live-2',
        sequence: 2,
        occurredAt: ts(2),
        runId: 'run-1',
        turnId: 'turn-1',
        name: 'shell',
        toolCallId: 'c9',
      },
      {
        type: 'turn.completed',
        eventId: 'live-3',
        sequence: 3,
        occurredAt: ts(3),
        runId: 'run-1',
        turnId: 'turn-1',
      },
    ]);
    expect(store.get('turn-1')!.status).toBe('completed');
    // 历史明细水合到达（负高段 sequence，排在 live 事件与终态之前，
    // 不被终态单调守卫忽略；同事实靠 eventId 去重）：节点只增不减，
    // 绝不因终态清空。
    store.applyEvents(
      processItemsToFlowEvents(
        [item({ id: 'h1', kind: 'thinking', text: '历史思考' })],
        { turnId: 'turn-1', baseSequence: -1_000_000 },
      ),
      { turnIdHint: 'turn-1' },
    );
    const projection = store.getProjection('turn-1')!;
    const kinds = projection.nodes.map((n) => n.kind);
    expect(kinds).toContain('message');
    expect(kinds).toContain('tool');
    expect(kinds).toContain('reasoning');
    expect(store.get('turn-1')!.status).toBe('completed');
  });

  it('routes untagged events via turnIdHint (activeRun snapshot without turnId)', () => {
    const store = new TurnSurfaceStore();
    store.linkAlias('turn-1', 'client-msg-1');
    const events = processItemsToFlowEvents(
      [item({ id: 'a1', kind: 'tool_call', name: 'shell', toolCallId: 'c1', text: 'shell' })],
      { turnId: 'placeholder' },
    ).map(
      (event) => ({ ...event, turnId: undefined }) as unknown as typeof event,
    );
    store.applyEvents(events, { turnIdHint: 'client-msg-1' });
    expect(store.getProjection('client-msg-1')!.nodes).toHaveLength(1);
  });
});
