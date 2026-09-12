import {
  projectSubAgentRunsToCards,
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from './subAgentReducer';

// ── SA-TRACE 回归 ─────────────────────────────────────────────
// 缺陷：reducer 的白名单只接受 subagent.run./round./llm./tool. 四个前缀，
// 而后端 canonical 事件族还在发 budget.notice / context.compacted /
// tool_discovery.stalled / output_contract.completed（见
// PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs
// 与 PuddingCore/Platform/ConversationContracts.cs:125-142）。
// 这些事件在 SSE 投影里被静默丢弃 → 运行中轨迹缺行。
//
// 事件形状严格对齐 SSE 消费形状：
//   projectConversationEventEnvelope(data, rawType, seq) =>
//   { ...data, ...data.payload, type: rawType, sequenceNum }
// 即 canonical 信封字段 runId 与 payload 展开同层。
const envelope = (
  type: string,
  eventId: string,
  payload: Record<string, unknown>,
) => ({
  ...payload,
  type,
  eventId,
  sequenceNum: 42,
  runId: 'run-trace',
});

const createdEvent = {
  eventId: 'event-created',
  type: 'subagent.run.created',
  occurredAt: '2026-09-13T00:00:00Z',
  run_id: 'run-trace',
  sub_agent_id: 'sub-trace',
  parent_session_id: 'conversation-1',
  task_summary: 'trace the run',
};

describe('subAgentReducer canonical trace events (SA-TRACE)', () => {
  it('accepts every canonical subagent family the runtime emits', () => {
    let state: SubAgentRunMap = reduceSubAgentRunEvent({}, createdEvent);
    const dropped: string[] = [];

    const cases: Array<[string, Record<string, unknown>]> = [
      [
        'subagent.budget.notice',
        {
          sub_agent_id: 'sub-trace',
          kind: 'grace_notice',
          round: 1,
          primary_max_rounds: 8,
          grace_rounds: 2,
          remaining_grace_rounds: 2,
          elapsed_ms: 1000,
        },
      ],
      [
        'subagent.context.compacted',
        { sub_agent_id: 'sub-trace', round: 1, trigger_ratio: 0.8 },
      ],
      [
        'subagent.tool_discovery.stalled',
        {
          sub_agent_id: 'sub-trace',
          round: 1,
          stalled_rounds: 3,
          message_preview: 'tool discovery stalled',
        },
      ],
      [
        'subagent.output_contract.completed',
        { sub_agent_id: 'sub-trace', round: 1, contract: 'plan' },
      ],
    ];

    for (const [type, payload] of cases) {
      const next = reduceSubAgentRunEvent(state, envelope(type, `e-${type}`, payload));
      // 早退（返回同一引用）即事件被丢弃。
      if (next === state) dropped.push(type);
      state = next;
    }

    expect(dropped).toEqual([]);
    expect(state['run-trace'].activities.map((activity) => activity.type)).toEqual(
      [
        'subagent.run.created',
        'subagent.budget.notice',
        'subagent.context.compacted',
        'subagent.tool_discovery.stalled',
        'subagent.output_contract.completed',
      ],
    );
  });

  it('labels the newly accepted families without claiming failure', () => {
    let state: SubAgentRunMap = reduceSubAgentRunEvent({}, createdEvent);
    state = reduceSubAgentRunEvent(state, {
      eventId: 'event-started',
      type: 'subagent.run.started',
      occurredAt: '2026-09-13T00:00:01Z',
      runId: 'run-trace',
      sub_agent_id: 'sub-trace',
    });
    for (const [type, payload] of [
      ['subagent.budget.notice', { round: 1 }],
      ['subagent.context.compacted', { round: 1 }],
      ['subagent.tool_discovery.stalled', { round: 1 }],
      ['subagent.output_contract.completed', { round: 1 }],
    ] as Array<[string, Record<string, unknown>]>) {
      state = reduceSubAgentRunEvent(state, envelope(type, `e-${type}`, payload));
    }
    const card = projectSubAgentRunsToCards(state)['sa-run-trace'];
    expect(card.status).toBe('running');
    expect(card.activities?.map((activity) => activity.label)).toEqual([
      '子代理已登记',
      '运行时已启动',
      '预算提示',
      '上下文已压缩',
      '工具发现停滞',
      '输出契约已提交',
    ]);
  });

  it('still rejects ADR-060 legacy frames without a stable runId', () => {
    let state: SubAgentRunMap = reduceSubAgentRunEvent({}, createdEvent);
    const before = state;
    for (const legacy of [
      'subagent.spawned',
      'subagent.delta',
      'subagent.thinking',
      'subagent.tool_call',
      'subagent.tool_result',
      'subagent.completed',
    ]) {
      const next = reduceSubAgentRunEvent(state, envelope(legacy, `e-${legacy}`, {}));
      expect(next).toBe(state);
      state = next;
    }
    expect(state).toBe(before);
  });
});
