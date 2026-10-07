import {
  mergeSubAgentRunSnapshots,
  projectSubAgentRunsToCards,
  reconcileSubAgentRunStatuses,
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from './subAgentReducer';

/** 会话状态端点物化的占位：只有存在性，没有过程与用量。 */
const placeholderRun = (runId: string, lastActivityAt: number): SubAgentRunMap => ({
  [runId]: {
    runId,
    subSessionId: `${runId}-sub`,
    taskSummary: '占位任务',
    status: 'running',
    phase: 'starting',
    currentRound: 0,
    startedAt: lastActivityAt,
    lastActivityAt,
    llmDurationMs: 0,
    toolDurationMs: 0,
    promptTokens: 0,
    completionTokens: 0,
    totalTokens: 0,
    cacheHitTokens: 0,
    cacheMissTokens: 0,
    tools: [],
    activities: [],
    appliedEventIds: [],
    eventSync: 'awaiting',
  },
});

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
        reasoning_preview: '先读取代码地图，再核对当前实现。',
        reasoning_truncated: false,
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
        kind: 'reasoning',
        label: '模型推理',
        content: '先读取代码地图，再核对当前实现。',
        truncated: false,
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
        runId: 'run-stale',
        parentSessionId: 'parent-session',
        subSessionId: 'session-sub-stale',
        status: 'completed',
        taskSummary: 'stale run',
        spawnedAt: '2026-07-19T00:00:00Z',
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

  it('creates a missing active run from the durable session snapshot', () => {
    const reconciled = reconcileSubAgentRunStatuses({}, [
      {
        runId: 'run-live',
        parentSessionId: 'parent-session',
        subSessionId: 'parent-session-sub-live',
        status: 'running',
        templateId: 'workspace-task-agent',
        modelId: 'deepseek-v4-flash',
        taskSummary: 'inspect the heartbeat loop',
        spawnedAt: '2026-08-12T05:23:30Z',
      },
    ]);

    expect(reconciled['run-live']).toMatchObject({
      runId: 'run-live',
      parentSessionId: 'parent-session',
      subSessionId: 'parent-session-sub-live',
      status: 'running',
      phase: 'starting',
      modelId: 'deepseek-v4-flash',
      taskSummary: 'inspect the heartbeat loop',
    });
    expect(projectSubAgentRunsToCards(reconciled)['sa-run-live']).toMatchObject(
      {
        status: 'running',
        runId: 'run-live',
      },
    );
  });

  it('projects budget exhaustion as a resumable terminal state', () => {
    const running = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-started',
        type: 'subagent.run.started',
        occurredAt: '2026-08-11T08:55:49Z',
        run_id: 'run-budget',
        sub_agent_id: 'session-sub-budget',
        origin_tool_id: 'spawn_sub_agent',
        max_rounds: 600,
      },
    );
    const exhausted = reduceSubAgentRunEvent(running, {
      eventId: 'event-budget-exhausted',
      type: 'subagent.run.budget_exhausted',
      occurredAt: '2026-08-11T10:43:39Z',
      run_id: 'run-budget',
      sub_agent_id: 'session-sub-budget',
      total_rounds: 620,
      error: 'cleanup grace exhausted',
    });

    expect(exhausted['run-budget']).toMatchObject({
      status: 'budget_exhausted',
      phase: 'completed',
      currentRound: 620,
      completedAt: Date.parse('2026-08-11T10:43:39Z'),
      error: 'cleanup grace exhausted',
    });
    expect(projectSubAgentRunsToCards(exhausted)['sa-run-budget'].status).toBe(
      'budget_exhausted',
    );

    const staleRound = reduceSubAgentRunEvent(exhausted, {
      eventId: 'event-stale-round',
      type: 'subagent.round.started',
      occurredAt: '2026-08-11T10:43:34Z',
      run_id: 'run-budget',
      sub_agent_id: 'session-sub-budget',
      round: 620,
    });
    expect(staleRound).toBe(exhausted);
  });

  it('reconciles a budget-exhausted canonical snapshot as terminal', () => {
    const running = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-started',
        type: 'subagent.run.started',
        run_id: 'run-budget-snapshot',
        sub_agent_id: 'session-sub-budget-snapshot',
      },
    );

    const reconciled = reconcileSubAgentRunStatuses(running, [
      {
        runId: 'run-budget-snapshot',
        parentSessionId: 'parent-session',
        subSessionId: 'session-sub-budget-snapshot',
        status: 'budget_exhausted',
        taskSummary: 'resume task',
        spawnedAt: '2026-08-11T08:55:49Z',
        completedAt: '2026-08-11T10:43:39Z',
        resultSummary: 'resume the preserved child session',
      },
    ]);

    expect(reconciled['run-budget-snapshot']).toMatchObject({
      status: 'budget_exhausted',
      phase: 'completed',
      error: 'resume the preserved child session',
    });
  });

  it('projects invocation_mode and blocks_parent_turn onto the card (571fb2fa)', () => {
    const state = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-created-sync',
        type: 'subagent.run.created',
        occurredAt: '2026-07-19T00:00:00Z',
        run_id: 'run-sync',
        sub_agent_id: 'sub-sync',
        parent_session_id: 'conversation-1',
        invocation_mode: 'sync',
        blocks_parent_turn: true,
        task_summary: 'sync task',
      },
    );

    const card = projectSubAgentRunsToCards(state)['sa-run-sync'];
    expect(card.invocationMode).toBe('sync');
    expect(card.blocksParentTurn).toBe(true);
  });

  it('merges invocation_mode from a later event onto an existing run', () => {
    const created = reduceSubAgentRunEvent(
      {},
      {
        type: 'subagent.run.created',
        run_id: 'run-merge',
        sub_agent_id: 'sub-merge',
        invocation_mode: 'async',
      },
    );
    const started = reduceSubAgentRunEvent(created, {
      type: 'subagent.run.started',
      run_id: 'run-merge',
      sub_agent_id: 'sub-merge',
      invocationMode: 'sync',
    });

    expect(started['run-merge'].invocationMode).toBe('sync');
  });
});

// ── 诊断 2026-10-06：截图里的「启动中 / 0 轮 / 0 工具 / 暂无运行事件」 ──
// 会话状态端点只能证明 run 存在，不能证明它的过程与用量。占位必须自报
// `awaiting`，事件一到就转 `live`；快照合并也必须以证据量为准。
describe('subAgentReducer — 运行事实的同步状态', () => {
  it('状态端点物化的运行自报 awaiting，任何事件折入后转 live', () => {
    const reconciled = reconcileSubAgentRunStatuses({}, [
      {
        runId: 'run-await',
        parentSessionId: 'parent-session',
        subSessionId: 'parent-session-sub-await',
        status: 'running',
        taskSummary: '等待事件',
        spawnedAt: '2026-10-06T15:05:22Z',
      },
    ]);

    expect(reconciled['run-await']).toMatchObject({
      eventSync: 'awaiting',
      currentRound: 0,
      totalTokens: 0,
    });
    expect(
      projectSubAgentRunsToCards(reconciled)['sa-run-await'].eventSync,
    ).toBe('awaiting');

    const live = reduceSubAgentRunEvent(reconciled, {
      eventId: 'event-round-1',
      type: 'subagent.round.started',
      occurredAt: '2026-10-06T15:05:30Z',
      run_id: 'run-await',
      sub_agent_id: 'parent-session-sub-await',
      round: 1,
    });

    expect(live['run-await']).toMatchObject({
      eventSync: 'live',
      currentRound: 1,
    });
    expect(projectSubAgentRunsToCards(live)['sa-run-await'].eventSync).toBe(
      'live',
    );
  });

  it('同一 eventId 重复折入不重复累计 Token（快照/重放/live 重叠）', () => {
    const first = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-llm-1',
        type: 'subagent.llm.completed',
        occurredAt: '2026-10-06T15:05:30Z',
        run_id: 'run-dedupe',
        sub_agent_id: 'run-dedupe-sub',
        total_tokens: 1500,
      },
    );
    const replayed = reduceSubAgentRunEvent(first, {
      eventId: 'event-llm-1',
      type: 'subagent.llm.completed',
      occurredAt: '2026-10-06T15:05:30Z',
      run_id: 'run-dedupe',
      sub_agent_id: 'run-dedupe-sub',
      total_tokens: 1500,
    });

    expect(replayed).toBe(first);
    expect(replayed['run-dedupe'].totalTokens).toBe(1500);
    expect(replayed['run-dedupe'].activities).toHaveLength(1);
  });

  it('快照合并按证据量取舍：更新的占位不得顶掉更丰富的事件视图', () => {
    const eventDerived = reduceSubAgentRunEvent(
      reduceSubAgentRunEvent(
        {},
        {
          eventId: 'event-created',
          type: 'subagent.run.created',
          occurredAt: '2026-10-06T15:05:22Z',
          run_id: 'run-evidence',
          sub_agent_id: 'run-evidence-sub',
          task_summary: '真实任务',
        },
      ),
      {
        eventId: 'event-llm',
        type: 'subagent.llm.completed',
        occurredAt: '2026-10-06T15:05:25Z',
        run_id: 'run-evidence',
        sub_agent_id: 'run-evidence-sub',
        total_tokens: 900,
        round: 1,
      },
    );
    // 占位时间戳更新（例如状态端点在事件之后才被轮询到），但证据量为 0。
    const placeholder = placeholderRun(
      'run-evidence',
      Date.parse('2026-10-06T15:06:00Z'),
    );

    const merged = mergeSubAgentRunSnapshots(placeholder, eventDerived);

    expect(merged['run-evidence']).toMatchObject({
      eventSync: 'live',
      totalTokens: 900,
      taskSummary: '真实任务',
    });
    expect(merged['run-evidence'].appliedEventIds).toHaveLength(2);
  });

  it('快照合并保留只存在于本地的运行，不因快照缺项而丢视图', () => {
    const localOnly = placeholderRun('run-local', 1);
    const fromSnapshot = reduceSubAgentRunEvent(
      {},
      {
        eventId: 'event-created',
        type: 'subagent.run.created',
        occurredAt: '2026-10-06T15:05:22Z',
        run_id: 'run-snapshot',
        sub_agent_id: 'run-snapshot-sub',
      },
    );

    const merged = mergeSubAgentRunSnapshots(localOnly, fromSnapshot);

    expect(Object.keys(merged).sort()).toEqual(['run-local', 'run-snapshot']);
  });
});
