import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import SubAgentIndicator from './SubAgentIndicator';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

describe('SubAgentIndicator', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-06-14T00:00:00.000Z'));
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.clearAllMocks();
  });

  it('uses the SSE projection without polling and filters historical runs', async () => {
    render(
      <SubAgentIndicator
        sessionId="session"
        open
        renderTrigger={false}
        subAgentCards={{
          recent: {
            turnId: 'recent',
            runId: 'run-recent',
            subSessionId: 'session-sub-recent',
            parentSessionId: 'session',
            status: 'completed',
            modelId: 'deepseek-v4-flash',
            taskSummary: 'recent task',
            spawnedAt: Date.parse('2026-06-13T00:00:00.000Z'),
            completedAt: Date.parse('2026-06-13T00:01:00.000Z'),
          },
          old: {
            turnId: 'old',
            runId: 'run-old',
            subSessionId: 'session-sub-old',
            parentSessionId: 'session',
            status: 'completed',
            modelId: 'deepseek-v4-flash',
            taskSummary: 'old task',
            spawnedAt: Date.parse('2026-06-01T00:00:00.000Z'),
            completedAt: Date.parse('2026-06-01T00:01:00.000Z'),
          },
        }}
      />,
    );

    expect(screen.getByText('recent task')).toBeTruthy();
    expect(screen.queryByText('old task')).toBeNull();
    expect(screen.getByText('全部 (1)')).toBeTruthy();

    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: '子代理时间范围' }),
    );
    fireEvent.click(await screen.findByText('全部记录'));

    await waitFor(() => {
      expect(screen.getByText('old task')).toBeTruthy();
    });
    expect(screen.getByText('全部 (2)')).toBeTruthy();
  });

  it('shows live phase, model, limits, token and tool state', () => {
    render(
      <SubAgentIndicator
        sessionId="session"
        open
        renderTrigger={false}
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
            spawnedAt: Date.now() - 5_000,
          },
        }}
      />,
    );

    expect(screen.getByText('plan the architecture')).toBeTruthy();
    expect(screen.getByText('smart_plan')).toBeTruthy();
    expect(screen.getByText('planner')).toBeTruthy();
    expect(screen.getByText('模型：kimi-k3')).toBeTruthy();
    expect(screen.getByText('轮次：2/8')).toBeTruthy();
    expect(screen.getByText('正在调用：')).toBeTruthy();
    expect(screen.getByText('file_read')).toBeTruthy();
  });
});
