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
    expect(screen.getByText('plan the architecture')).toBeTruthy();
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
      screen.getByText('Decompose goal into actionable tasks.'),
    ).toBeTruthy();
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
});
