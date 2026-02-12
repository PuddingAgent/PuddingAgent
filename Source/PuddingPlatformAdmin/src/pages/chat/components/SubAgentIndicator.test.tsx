import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { getSessionSubAgents } from '../../../services/platform/api';
import SubAgentIndicator from './SubAgentIndicator';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

jest.mock('../../../services/platform/api', () => ({
  getSessionSubAgents: jest.fn(),
}));

describe('SubAgentIndicator', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-06-14T00:00:00.000Z'));
    (getSessionSubAgents as jest.Mock).mockResolvedValue([
      {
        subSessionId: 'session-sub-recent',
        status: 'completed',
        templateId: 'workspace-task-agent',
        modelId: 'deepseek-v4-flash',
        taskSummary: 'recent task',
        spawnedAt: '2026-06-13T00:00:00.000Z',
        completedAt: '2026-06-13T00:01:00.000Z',
        resultSummary: 'recent result',
        success: true,
      },
      {
        subSessionId: 'session-sub-old',
        status: 'completed',
        templateId: 'workspace-task-agent',
        modelId: 'deepseek-v4-flash',
        taskSummary: 'old task',
        spawnedAt: '2026-06-01T00:00:00.000Z',
        completedAt: '2026-06-01T00:01:00.000Z',
        resultSummary: 'old result',
        success: true,
      },
    ]);
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.clearAllMocks();
  });

  it('defaults the manager to recent 7 days and can switch to all records', async () => {
    render(
      <SubAgentIndicator sessionId="session" open renderTrigger={false} />,
    );

    await waitFor(() => {
      expect(screen.getByText('recent task')).toBeTruthy();
    });

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
});
