import { renderHook, waitFor } from '@testing-library/react';
import { getSessionSubAgents } from '@/services/platform/api';
import { useSessionEventReplay } from './useSessionEventReplay';

jest.mock('@/services/platform/api', () => ({
  getConversationBootstrap: jest.fn(),
  getSessionSubAgents: jest.fn(),
}));

jest.mock('@/utils/perfEventRuntime', () => ({
  recordPerfEvent: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

describe('useSessionEventReplay', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('reconciles durable sub-agent status even when the local run map is empty', async () => {
    jest.mocked(getSessionSubAgents).mockResolvedValue([
      {
        runId: 'run-live',
        parentSessionId: 'session-a',
        subSessionId: 'session-a-sub-live',
        status: 'running',
        taskSummary: 'continue implementation',
        spawnedAt: '2026-08-12T05:00:00Z',
      },
    ]);
    const setSubAgentRuns = jest.fn();

    const { unmount } = renderHook(() =>
      useSessionEventReplay({
        identity: {
          lastSequenceNumRef: { current: 0 },
          sseSessionIdRef: { current: null },
          lastSseEventAtRef: { current: null },
          activeMessageIdsRef: { current: new Set() },
          selectedSessionIdRef: { current: 'session-a' },
          sessionIdRef: { current: 'session-a' },
          hydrateSessionReplayRef: { current: false },
        },
        projection: {
          applySessionEvent: jest.fn(),
          handleCompactionLifecycleEvent: jest.fn(),
          setSubAgentRuns,
          subAgentRuns: {},
          pruneTrackedActiveMessages: jest.fn(() => false),
        },
      }),
    );

    await waitFor(() => {
      expect(getSessionSubAgents).toHaveBeenCalledWith('session-a');
      expect(setSubAgentRuns).toHaveBeenCalled();
    });

    const update = setSubAgentRuns.mock.calls[0][0];
    expect(update({})['run-live']).toMatchObject({
      status: 'running',
      subSessionId: 'session-a-sub-live',
    });

    unmount();
  });
});
