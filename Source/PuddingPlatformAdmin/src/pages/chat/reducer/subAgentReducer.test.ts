import {
  projectSubAgentRunsToCards,
  reconcileSubAgentRunStatuses,
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from './subAgentReducer';

describe('subAgentReducer', () => {
  it('projects a complete Smart tool child run by stable runId', () => {
    const events = [
      {
        eventId: 'event-created',
        type: 'subagent.run.created',
        occurredAt: '2026-07-19T00:00:00Z',
        run_id: 'run-1',
        sub_agent_id: 'sub-1',
        parent_session_id: 'conversation-1',
        origin_tool_id: 'smart_plan',
        role: 'planner',
        model_id: 'kimi-k3',
        timeout_seconds: 3600,
        max_rounds: 8,
        task_summary: 'plan architecture',
      },
      {
        eventId: 'event-round-1',
        type: 'subagent.round.started',
        occurredAt: '2026-07-19T00:00:01Z',
        runId: 'run-1',
        sub_agent_id: 'sub-1',
        round: 1,
      },
      {
        eventId: 'event-llm-1',
        type: 'subagent.llm.completed',
        occurredAt: '2026-07-19T00:00:03Z',
        runId: 'run-1',
        sub_agent_id: 'sub-1',
        round: 1,
        duration_ms: 2000,
        prompt_tokens: 1200,
        completion_tokens: 300,
        total_tokens: 1500,
        message_preview: 'I will inspect the architecture next.',
        message_truncated: false,
        reasoning_available: true,
        reasoning_chars: 2048,
      },
      {
        eventId: 'event-tool-started-1',
        type: 'subagent.tool.started',
        occurredAt: '2026-07-19T00:00:04Z',
        runId: 'run-1',
        sub_agent_id: 'sub-1',
        round: 1,
        tool_call_id: 'tool-1',
        tool_name: 'file_read',
        arguments_preview: '{"path":"Source/code_map.md"}',
      },
      {
        eventId: 'event-tool-completed-1',
        type: 'subagent.tool.completed',
        occurredAt: '2026-07-19T00:00:05Z',
        runId: 'run-1',
        sub_agent_id: 'sub-1',
        round: 1,
        tool_call_id: 'tool-1',
        tool_name: 'file_read',
        duration_ms: 1000,
        output_length: 500,
        output_preview: '# code map',
        output_truncated: true,
      },
      {
        eventId: 'event-completed',
        type: 'subagent.run.completed',
        occurredAt: '2026-07-19T00:00:06Z',
        runId: 'run-1',
        sub_agent_id: 'sub-1',
        total_rounds: 1,
        reply: 'done',
      },
    ];

    const state = events.reduce<SubAgentRunMap>(
      (current, event) => reduceSubAgentRunEvent(current, event),
      {},
    );
    const run = state['run-1'];
    expect(run.status).toBe('completed');
    expect(run.currentRound).toBe(1);
    expect(run.totalTokens).toBe(1500);
    expect(run.tools).toHaveLength(1);
    expect(run.tools[0].status).toBe('completed');
    expect(run.activities.map((activity) => activity.label)).toEqual([
      '子代理已登记',
      '第 1 轮开始',
      '模型返回 · 1500 tokens',
      '开始执行 file_read',
      'file_read 执行完成',
      '子代理执行完成',
    ]);

    const card = projectSubAgentRunsToCards(state)['sa-run-1'];
    expect(card.originToolId).toBe('smart_plan');
    expect(card.modelId).toBe('kimi-k3');
    expect(card.totalTokens).toBe(1500);
    expect(card.output).toBe('done');
    expect(card.activities).toHaveLength(6);
    expect(card.subSessionId).toBe('sub-1');
    expect(card.runId).toBe('run-1');
    expect(card.activities?.[2].details).toEqual([
      {
        kind: 'model_message',
        label: '模型消息输出',
        content: 'I will inspect the architecture next.',
        truncated: false,
      },
      {
        kind: 'reasoning_notice',
        label: '内部推理',
        content:
          '模型产生了内部推理（2048 字符）。为避免泄露隐藏思维链，仅展示可审计的模型消息与执行事实。',
      },
    ]);
    expect(card.activities?.[3].details?.[0]).toMatchObject({
      kind: 'tool_input',
      content: '{"path":"Source/code_map.md"}',
    });
    expect(card.activities?.[4].details?.[0]).toMatchObject({
      kind: 'tool_output',
      content: '# code map',
      truncated: true,
    });

    const replayed = reduceSubAgentRunEvent(state, events[2]);
    expect(replayed).toBe(state);
    expect(replayed['run-1'].totalTokens).toBe(1500);
  });

  it('ignores legacy frames that cannot provide a stable terminal state', () => {
    const state = reduceSubAgentRunEvent(
      {},
      {
        type: 'subagent.spawned',
        sub_agent_id: 'sub-1',
        task_summary: 'task',
      },
    );

    expect(state).toEqual({});
  });

  it('reconciles an active event snapshot with the canonical session terminal status', () => {
    const running = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-started',
        type: 'subagent.run.started',
        occurredAt: '2026-07-19T00:00:00Z',
        run_id: 'run-stale',
        sub_agent_id: 'session-sub-stale',
        task_summary: 'stale run',
      },
    );

    const reconciled = reconcileSubAgentRunStatuses(running, [
      {
        subSessionId: 'session-sub-stale',
        status: 'completed',
        completedAt: '2026-07-19T00:01:00Z',
        resultSummary: 'canonical result',
      },
    ]);

    expect(reconciled['run-stale']).toMatchObject({
      status: 'completed',
      phase: 'completed',
      completedAt: Date.parse('2026-07-19T00:01:00Z'),
      output: 'canonical result',
    });
  });
});
