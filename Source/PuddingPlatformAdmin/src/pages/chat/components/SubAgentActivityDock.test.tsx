import { act, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { getSubAgentRunOutput } from '@/services/platform/api';
import SubAgentActivityDock from './SubAgentActivityDock';

jest.mock('@/services/platform/api', () => ({
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

    expect(screen.getByText('plan the architecture')).toBeTruthy();
    expect(screen.getByText('正在执行 file_read')).toBeTruthy();
    expect(screen.getByText('开始执行 file_read')).toBeTruthy();
    expect(screen.getByText('模型：kimi-k3')).toBeTruthy();
    expect(screen.getByText('session-sub-active')).toBeTruthy();
    expect(screen.getByText('run-active')).toBeTruthy();
    expect(screen.getByText('Call ID: call-file-read')).toBeTruthy();
    expect(screen.getByText('工具输入')).toBeTruthy();
    expect(screen.getByText('{"path":"Source/code_map.md"}')).toBeTruthy();

    fireEvent.click(screen.getByText('返回运行列表'));
    expect(onSelectedRunIdChange).toHaveBeenCalledWith(null);
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
});
