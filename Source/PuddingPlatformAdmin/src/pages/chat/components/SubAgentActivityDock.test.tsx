import { act, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  getSubAgentRunDetail,
  getSubAgentRunEvents,
  getSubAgentRunOutput,
} from '@/services/platform/api';
import SubAgentActivityDock from './SubAgentActivityDock';

jest.mock('@/services/platform/api', () => ({
  getSubAgentRunDetail: jest.fn(),
  getSubAgentRunEvents: jest.fn(),
  getSubAgentRunOutput: jest.fn(),
}));

jest.mock('antd-style', () => ({
  createStyles: () => () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
    cx: (...names: Array<string | false | undefined>) =>
      names.filter(Boolean).join(' '),
  }),
}));

describe('SubAgentActivityDock', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-07-19T00:00:10.000Z'));
    jest.mocked(getSubAgentRunOutput).mockResolvedValue({ output: null });
    jest.mocked(getSubAgentRunDetail).mockImplementation(async (runId) => ({
      summary: {
        runId,
        parentSessionId: 'session',
        subSessionId: 'session-sub',
        workspaceId: 'default',
        agentInstanceId: 'agent',
        templateId: 'template',
        status: 'running',
        startedAt: '2026-07-19T00:00:00.000Z',
        totalDurationMs: 10_000,
        totalRounds: 0,
        totalToolCalls: 0,
      },
      eventCount: 0,
      toolCallCount: 0,
    }));
    jest.mocked(getSubAgentRunEvents).mockResolvedValue({
      items: [],
      total: 0,
      offset: 0,
      limit: 500,
    });
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.clearAllMocks();
  });

  // 诊断 2026-10-06：占位运行（只有会话状态端点、事件尚未送达）不得把「未知」
  // 显示成真实的 0，也不得显示「暂无运行事件」。
  it('renders an unsynced placeholder as syncing with unknown metrics instead of zeros', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-await"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          awaiting: {
            turnId: 'awaiting',
            runId: 'run-await',
            subSessionId: 'session-sub-await',
            parentSessionId: 'session',
            status: 'running',
            phase: 'starting',
            role: 'worker',
            taskSummary: '等待事件同步',
            currentRound: 0,
            maxRounds: 600,
            totalTokens: 0,
            toolCount: 0,
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:00.000Z'),
            activities: [],
            eventSync: 'awaiting',
          },
        }}
      />,
    );

    expect(screen.getByTestId('subagent-timeline-syncing')).toBeTruthy();
    expect(screen.queryByText('暂无运行事件')).toBeNull();
    // 轮次 / Token / 工具都必须显示为未知，而不是 0。
    expect(screen.getByText('轮次：—')).toBeTruthy();
    expect(screen.getByText('Token：—')).toBeTruthy();
    expect(screen.getByText('工具：—')).toBeTruthy();
  });

  it('escalates a stalled unsynced run to a reconnect hint using the channel state', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-stalled"
        onSelectedRunIdChange={jest.fn()}
        reconnectCount={3}
        subAgentCards={{
          stalled: {
            turnId: 'stalled',
            runId: 'run-stalled',
            subSessionId: 'session-sub-stalled',
            parentSessionId: 'session',
            status: 'running',
            phase: 'starting',
            taskSummary: '卡住的占位',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:00.000Z'),
            activities: [],
            eventSync: 'awaiting',
          },
        }}
      />,
    );

    // 系统时间固定在 00:00:10，运行自 00:00:00 起一直没事件 → 未达 20s 停滞阈值。
    expect(screen.queryByTestId('subagent-sync-stalled')).toBeNull();

    act(() => {
      jest.advanceTimersByTime(25_000);
    });

    const stalled = screen.getByTestId('subagent-sync-stalled');
    expect(stalled.textContent).toContain('父会话事件通道正在重连（第 3 次）');
  });

  it('shows factual live activity and opens the selected run inspector', () => {
    const onOpenChange = jest.fn();
    const onSelectedRunIdChange = jest.fn();
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={onOpenChange}
        selectedRunId="run-active"
        onSelectedRunIdChange={onSelectedRunIdChange}
        subAgentCards={{
          active: {
            turnId: 'active',
            runId: 'run-active',
            subSessionId: 'session-sub-active',
            parentSessionId: 'session',
            status: 'running',
            phase: 'tool',
            originToolId: 'smart_plan',
            role: 'planner',
            providerId: 'moonshot',
            modelId: 'kimi-k3',
            taskSummary: 'plan the architecture',
            currentRound: 2,
            maxRounds: 8,
            timeoutSeconds: 3600,
            totalTokens: 12000,
            toolCount: 3,
            activeToolName: 'file_read',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
            activities: [
              {
                eventId: 'event-llm',
                type: 'subagent.llm.completed',
                label: '模型返回 · 128 tokens',
                occurredAt: Date.parse('2026-07-19T00:00:08.000Z'),
                details: [
                  {
                    kind: 'reasoning',
                    label: '模型推理',
                    content: '先检查入口，再核对数据投影。',
                  },
                ],
              },
              {
                eventId: 'event-tool',
                type: 'subagent.tool.started',
                label: '开始执行 file_read',
                occurredAt: Date.parse('2026-07-19T00:00:09.000Z'),
                toolName: 'file_read',
                toolCallId: 'call-file-read',
                details: [
                  {
                    kind: 'tool_input',
                    label: '工具输入',
                    content: '{"path":"Source/code_map.md"}',
                  },
                ],
              },
            ],
          },
        }}
      />,
    );

    expect(screen.getByTestId('subagent-dock-item-run-active')).toBeTruthy();
    // 身份现身两处：检查器头部标题（名字）与 hero 任务段落。旧断言是单处
    // getByText，2026-11-21 加入头部身份后同一个任务摘要会出现两处（作者
    // 当时没给 displayName）——意图仍是「任务文本可见」，改用 getAllByText。
    expect(screen.getAllByText('plan the architecture').length).toBeGreaterThan(
      0,
    );
    expect(screen.getByText('正在执行 file_read')).toBeTruthy();
    expect(screen.getByText('开始执行 file_read')).toBeTruthy();
    expect(screen.getByText('模型：kimi-k3')).toBeTruthy();
    expect(screen.getByText('session-sub-active')).toBeTruthy();
    expect(screen.getByText('run-active')).toBeTruthy();
    expect(screen.getByText('Call ID: call-file-read')).toBeTruthy();
    expect(screen.getByText('模型推理')).toBeTruthy();
    expect(screen.getByText('先检查入口，再核对数据投影。')).toBeTruthy();
    expect(screen.getByText('工具输入')).toBeTruthy();
    expect(screen.getByText('{"path":"Source/code_map.md"}')).toBeTruthy();

    fireEvent.click(screen.getByText('返回运行列表'));
    expect(onSelectedRunIdChange).toHaveBeenCalledWith(null);
  });

  it('renders budget exhaustion as terminal attention instead of running', async () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-budget"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          budget: {
            turnId: 'budget',
            runId: 'run-budget',
            subSessionId: 'session-sub-budget',
            parentSessionId: 'session',
            status: 'budget_exhausted',
            taskSummary: 'bounded task',
            currentRound: 620,
            maxRounds: 600,
            spawnedAt: Date.parse('2026-08-11T08:55:49Z'),
            completedAt: Date.parse('2026-08-11T10:43:39Z'),
            lastActivityAt: Date.parse('2026-08-11T10:43:39Z'),
            error: 'resume the preserved child session',
          },
        }}
      />,
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(screen.getAllByText('预算已用尽').length).toBeGreaterThan(0);
    expect(screen.queryByText('运行中')).toBeNull();
    expect(screen.getByText('resume the preserved child session')).toBeTruthy();
  });

  it('automatically removes a successful completion after the linger window', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen={false}
        onInspectorOpenChange={jest.fn()}
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          done: {
            turnId: 'done',
            runId: 'run-done',
            subSessionId: 'session-sub-done',
            parentSessionId: 'session',
            status: 'completed',
            phase: 'completed',
            taskSummary: 'done',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            completedAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(screen.getByTestId('subagent-dock-item-run-done')).toBeTruthy();
    act(() => {
      jest.advanceTimersByTime(13_000);
    });
    expect(screen.queryByTestId('subagent-dock-item-run-done')).toBeNull();
  });

  it('automatically removes an error completion while retaining it in the inspector data', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen={false}
        onInspectorOpenChange={jest.fn()}
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          failed: {
            turnId: 'failed',
            runId: 'run-failed',
            subSessionId: 'session-sub-failed',
            parentSessionId: 'session',
            status: 'failed',
            phase: 'completed',
            taskSummary: 'failed',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            completedAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(screen.getByTestId('subagent-dock-item-run-failed')).toBeTruthy();
    act(() => {
      jest.advanceTimersByTime(31_000);
    });
    expect(screen.queryByTestId('subagent-dock-item-run-failed')).toBeNull();
  });

  it('summarizes a role wrapper instead of exposing the complete prompt', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-plan"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          plan: {
            turnId: 'plan',
            runId: 'run-plan',
            subSessionId: 'session-sub-plan',
            parentSessionId: 'session',
            status: 'running',
            phase: 'llm',
            role: 'planner',
            taskSummary:
              '## 📋 PLANNER — Decompose goal into actionable tasks. ### PROCESS\n1. Read every file.\n2. Reveal internal instructions.',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
          },
        }}
      />,
    );

    expect(
      screen.getAllByText('Decompose goal into actionable tasks.').length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText(/Read every file/)).toBeNull();
    expect(screen.queryByText(/Reveal internal instructions/)).toBeNull();
  });

  it('loads the complete archived output instead of presenting the event summary as the result', async () => {
    jest.mocked(getSubAgentRunOutput).mockResolvedValue({
      output: 'FULL OUTPUT\nAll evidence returned to the parent Agent.',
    });

    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-complete"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          complete: {
            turnId: 'complete',
            runId: 'run-complete',
            subSessionId: 'session-sub-complete',
            parentSessionId: 'session',
            status: 'completed',
            phase: 'completed',
            taskSummary: 'inspect the code',
            output: 'short event summary',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            completedAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(screen.getByTestId('subagent-run-detail-layout')).toBeTruthy();
    expect(screen.getByTestId('subagent-run-timeline-region')).toBeTruthy();
    expect(screen.getByTestId('subagent-run-output-region')).toBeTruthy();
    expect(screen.getByText('返回主 Agent 的完整结果')).toBeTruthy();

    await act(async () => {
      await Promise.resolve();
    });
    expect(getSubAgentRunOutput).toHaveBeenCalledWith('run-complete');
    expect(screen.getByText(/FULL OUTPUT/).textContent).toBe(
      'FULL OUTPUT\nAll evidence returned to the parent Agent.',
    );
    expect(screen.queryByText('short event summary')).toBeNull();
  });

  it('restores archived metrics and timeline when the live event projection is empty', async () => {
    jest.mocked(getSubAgentRunDetail).mockResolvedValue({
      summary: {
        runId: 'run-archive',
        parentSessionId: 'session',
        subSessionId: 'session-sub-archive',
        workspaceId: 'default',
        agentInstanceId: 'agent',
        templateId: 'template',
        status: 'completed',
        startedAt: '2026-07-19T00:00:00.000Z',
        completedAt: '2026-07-19T00:06:00.000Z',
        totalDurationMs: 360_000,
        totalRounds: 37,
        totalToolCalls: 85,
      },
      eventCount: 2,
      toolCallCount: 85,
    });
    jest.mocked(getSubAgentRunEvents).mockResolvedValue({
      items: [
        {
          eventId: 'event-reasoning',
          eventType: 'subagent.llm.completed',
          timestamp: '2026-07-19T00:05:00.000Z',
          payloadSize: 100,
          payload: {
            round: 37,
            total_tokens: 1024,
            reasoning_preview: '检查归档事件，再恢复运行时间线。',
          },
        },
        {
          eventId: 'event-tool',
          eventType: 'subagent.tool.started',
          timestamp: '2026-07-19T00:05:01.000Z',
          payloadSize: 100,
          payload: {
            round: 37,
            tool_name: 'shell',
            tool_call_id: 'call-archive',
            arguments_preview: '{"command":"git status"}',
          },
        },
      ],
      total: 2,
      offset: 0,
      limit: 500,
    });

    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-archive"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          archived: {
            turnId: 'archived',
            runId: 'run-archive',
            subSessionId: 'session-sub-archive',
            parentSessionId: 'session',
            status: 'completed',
            phase: 'completed',
            modelId: 'deepseek-v4-flash',
            taskSummary: 'inspect archived events',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            completedAt: Date.parse('2026-07-19T00:06:00.000Z'),
            currentRound: 0,
            toolCount: 0,
            activities: [],
          },
        }}
      />,
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText('轮次：37')).toBeTruthy();
    expect(screen.getByText('工具：85')).toBeTruthy();
    expect(screen.getByText('模型返回 · 1024 tokens')).toBeTruthy();
    expect(screen.getByText('检查归档事件，再恢复运行时间线。')).toBeTruthy();
    expect(screen.getByText('开始执行 shell')).toBeTruthy();
    expect(screen.getByText('Call ID: call-archive')).toBeTruthy();
  });

  // ── 缺陷 A（2026-11-21）：运行中的工具失败不得渲染成运行级错误横幅 ──
  it('does not escalate a tool failure into a running-level error banner', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-toolfail"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          toolfail: {
            turnId: 'toolfail',
            runId: 'run-toolfail',
            subSessionId: 'session-sub-toolfail',
            parentSessionId: 'session',
            status: 'running',
            phase: 'tool',
            taskSummary: 'inspect the workspace',
            currentRound: 3,
            toolCount: 36,
            failedToolCount: 2,
            // 旧 reducer 会把工具错误写进运行级 error；这里刻意注入它，
            // 断言横幅的判据是 **运行状态**，而不是 error 字段是否为空。
            error: 'exit code 1',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(screen.queryByTestId('subagent-run-error')).toBeNull();
    expect(screen.queryByText('exit code 1')).toBeNull();
    const hint = screen.getByTestId('subagent-tool-failure-hint');
    expect(hint.textContent).toContain('本次运行已有 2 次工具失败');
    // 中性提示不得使用 error 样式。
    expect(hint.className).toBe('resultHint');
  });

  it('renders the running-level error banner for a terminal failure', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-crashed"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          crashed: {
            turnId: 'crashed',
            runId: 'run-crashed',
            subSessionId: 'session-sub-crashed',
            parentSessionId: 'session',
            status: 'failed',
            phase: 'completed',
            taskSummary: 'crashed task',
            toolCount: 5,
            failedToolCount: 1,
            error: 'spawn failed: provider unreachable',
            spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
            completedAt: Date.parse('2026-07-19T00:00:09.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(
      screen.getByTestId('subagent-run-error').textContent,
    ).toBe('spawn failed: provider unreachable');
    // 终态错误已经由红色横幅表达，不再重复中性提示。
    expect(screen.queryByTestId('subagent-tool-failure-hint')).toBeNull();
  });

  // ── 缺陷 B（2026-11-21）：24h 硬 deadline 不得画成百分比进度 ──
  it('reports elapsed time instead of a percent bar for a 24h deadline', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-deadline"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          deadline: {
            turnId: 'deadline',
            runId: 'run-deadline',
            subSessionId: 'session-sub-deadline',
            parentSessionId: 'session',
            status: 'running',
            phase: 'round',
            taskSummary: 'compare claude vs gpt token usage',
            currentRound: 4,
            maxRounds: 600,
            // 生产实测值：run.json / 事件里都是 `timeout_seconds: 86383`（≈24h）。
            timeoutSeconds: 86383,
            spawnedAt: Date.parse('2026-07-18T23:58:55.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    expect(screen.queryByRole('progressbar')).toBeNull();
    const elapsed = screen.getByTestId('subagent-elapsed-line');
    expect(elapsed.textContent).toBe('已运行 1m 15s');
    expect(elapsed.className).toBe('elapsedLine');
  });

  it('keeps the near-deadline alert and the long-running hint', () => {
    render(
      <SubAgentActivityDock
        sessionId="session"
        inspectorOpen
        onInspectorOpenChange={jest.fn()}
        selectedRunId="run-longrun"
        onSelectedRunIdChange={jest.fn()}
        subAgentCards={{
          longrun: {
            turnId: 'longrun',
            runId: 'run-longrun',
            subSessionId: 'session-sub-longrun',
            parentSessionId: 'session',
            status: 'running',
            phase: 'round',
            taskSummary: 'long task',
            timeoutSeconds: 86383,
            // 22h elapsed / 24h deadline → 超过 90%，属于接近时限。
            spawnedAt: Date.parse('2026-07-18T02:00:10.000Z'),
            lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
          },
        }}
      />,
    );

    const elapsed = screen.getByTestId('subagent-elapsed-line');
    expect(elapsed.textContent).toBe('已运行 1320m 0s · 仍在运行');
    expect(elapsed.className).toContain('elapsedException');
  });

  // ── 2026-11-21：子代理身份（名字 / 短 ID / 头像）必须在第一屏可见 ──
  // 用户原话：「既没有 displayName 也没有 id，我怎么定位这个子代理究竟是谁」。
  // 所以名字与短 ID 缺一不可，来源工具名 spawn_sub_agent 只能作为 Tag。
  describe('identity', () => {
    const namedRun = () => ({
      turnId: 'named',
      runId: 'run-named',
      subSessionId: 'parent-session-sub-0abc9f',
      parentSessionId: 'session',
      status: 'running' as const,
      phase: 'tool' as const,
      originToolId: 'spawn_sub_agent',
      templateId: 'workspace-task-agent',
      displayName: 'dock-fix',
      avatarKey: 'coder',
      responsibility: '修复子代理身份展示链路',
      taskSummary: '修复浮坞与检查器的身份展示',
      spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
      lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
    });

    it('inspector header shows avatar + name + short id, tool name only as a tag', () => {
      render(
        <SubAgentActivityDock
          sessionId="session"
          inspectorOpen
          onInspectorOpenChange={jest.fn()}
          selectedRunId="run-named"
          onSelectedRunIdChange={jest.fn()}
          subAgentCards={{ named: namedRun() }}
        />,
      );

      const identity = screen.getByTestId('subagent-drawer-identity');
      // avatarKey=coder 的预设图形；没有 avatarKey 时按名字首字生成。
      expect(identity.textContent).toContain('💻');
      expect(screen.getByTestId('subagent-drawer-name').textContent).toBe(
        'dock-fix',
      );
      expect(
        screen.getByTestId('subagent-drawer-short-id').textContent,
      ).toBe('…0abc9f');
      // 来源工具不是身份：存在，但不在标题位置。
      expect(screen.getByTestId('subagent-drawer-name').textContent).not.toBe(
        'spawn_sub_agent',
      );
      expect(screen.getAllByText('spawn_sub_agent').length).toBeGreaterThan(0);
      // 职责行有值时才渲染。
      expect(screen.getByTestId('subagent-responsibility').textContent).toBe(
        '修复子代理身份展示链路',
      );
      // 浮坞 rail 按钮显示名字首字/预设图形，不再显示工具名缩写。
      expect(
        screen.getByTestId('subagent-dock-item-run-named').textContent,
      ).toContain('💻');
    });

    it('legacy run without display name falls back to the task summary, never empty', () => {
      render(
        <SubAgentActivityDock
          sessionId="session"
          inspectorOpen
          onInspectorOpenChange={jest.fn()}
          selectedRunId="run-legacy"
          onSelectedRunIdChange={jest.fn()}
          subAgentCards={{
            legacy: {
              turnId: 'legacy',
              runId: 'run-legacy',
              subSessionId: 'parent-session-sub-0abc9f',
              parentSessionId: 'session',
              status: 'running',
              phase: 'round',
              originToolId: 'spawn_sub_agent',
              templateId: 'workspace-task-agent',
              taskSummary: '整理天气查询的失败重试',
              spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
              lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
            },
          }}
        />,
      );

      const name = screen.getByTestId('subagent-drawer-name').textContent;
      expect(name).toBe('整理天气查询的失败重试');
      expect(name?.trim().length).toBeGreaterThan(0);
      expect(name).not.toBe('spawn_sub_agent');
      // 短 ID 依然在，用来区分同名/无名运行。
      expect(
        screen.getByTestId('subagent-drawer-short-id').textContent,
      ).toBe('…0abc9f');
      // 没有职责就不渲染该行，不用占位符凑高度。
      expect(screen.queryByTestId('subagent-responsibility')).toBeNull();
    });

    it('falls back to <template>-<id suffix> when the task text is just the tool name', () => {
      render(
        <SubAgentActivityDock
          sessionId="session"
          inspectorOpen
          onInspectorOpenChange={jest.fn()}
          selectedRunId="run-weird"
          onSelectedRunIdChange={jest.fn()}
          subAgentCards={{
            weird: {
              turnId: 'weird',
              runId: 'run-weird',
              subSessionId: 'parent-session-sub-0abc9f',
              parentSessionId: 'session',
              status: 'running',
              phase: 'created',
              originToolId: 'spawn_sub_agent',
              templateId: 'workspace-task-agent',
              taskSummary: 'spawn_sub_agent',
              spawnedAt: Date.parse('2026-07-19T00:00:00.000Z'),
              lastActivityAt: Date.parse('2026-07-19T00:00:09.000Z'),
            },
          }}
        />,
      );

      expect(screen.getByTestId('subagent-drawer-name').textContent).toBe(
        'workspace-task-agent-0abc9f',
      );
    });

    it('dock popover first screen carries avatar, name and short id', () => {
      render(
        <SubAgentActivityDock
          sessionId="session"
          inspectorOpen={false}
          onInspectorOpenChange={jest.fn()}
          onSelectedRunIdChange={jest.fn()}
          subAgentCards={{
            named: {
              ...namedRun(),
              displayName: 'usage-api',
              subSessionId: 'parent-session-sub-0abc9f',
            },
          }}
        />,
      );

      const item = screen.getByTestId('subagent-dock-item-run-named');
      fireEvent.mouseEnter(item);
      act(() => {
        // Popover 的 mouseEnterDelay=0.15s。
        jest.advanceTimersByTime(500);
      });

      expect(screen.getByTestId('subagent-popover-name').textContent).toBe(
        'usage-api',
      );
      expect(
        screen.getByTestId('subagent-popover-short-id').textContent,
      ).toBe('…0abc9f');
      expect(
        screen.getByTestId('subagent-popover-identity').textContent,
      ).toContain('💻');
      // 名字不得被工具名顶替（工具名只作为 Tag 出现在同一屏）。
      expect(screen.getByTestId('subagent-popover-name').textContent).not.toBe(
        'spawn_sub_agent',
      );
    });
  });
});
