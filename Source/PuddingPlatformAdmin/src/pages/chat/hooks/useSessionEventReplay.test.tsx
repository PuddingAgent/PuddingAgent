import { renderHook, waitFor } from '@testing-library/react';
import { act } from '@testing-library/react';
import {
  getConversationBootstrap,
  getSessionSubAgents,
} from '@/services/platform/api';
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

  it('replay passes replay semantics with the resolved runningCompactionId to lifecycle events', async () => {
    // 回放不复活孤儿 started：bootstrap 里最后一个未终态的 started 才是 runningCompactionId；
    // 之前的 started（compact-1）与终态已配对，不会被误判为运行中。
    jest.mocked(getConversationBootstrap).mockResolvedValue({
      turns: [],
      snapshotCursor: 10,
      activeCompaction: { compactionId: 'compact-2', startedAt: new Date().toISOString() },
      lifecycleEvents: [
        {
          type: 'context.compaction.started',
          payload: JSON.stringify({ compactionId: 'compact-1' }),
        },
        {
          type: 'context.compaction.completed',
          payload: JSON.stringify({ compactionId: 'compact-1' }),
        },
        {
          type: 'context.compaction.started',
          payload: JSON.stringify({ compactionId: 'compact-2' }),
        },
      ],
    } as never);
    jest.mocked(getSessionSubAgents).mockResolvedValue([] as never);
    const handleCompactionLifecycleEvent = jest.fn();

    const { result } = renderHook(() =>
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
          handleCompactionLifecycleEvent,
          setSubAgentRuns: jest.fn(),
          subAgentRuns: {},
          pruneTrackedActiveMessages: jest.fn(() => false),
        },
      }),
    );

    await act(async () => {
      await result.current.syncCompletedHistoryEventCursor('session-a');
    });

    expect(handleCompactionLifecycleEvent).toHaveBeenCalledTimes(3);
    // 孤儿 started（compact-1，早已有终态）：拿到的 runningCompactionId 是 compact-2，
    // id 不匹配会被 useCompaction 整条忽略，刷新后不会复活成「正在压缩上下文」。
    expect(handleCompactionLifecycleEvent).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({ type: 'context.compaction.started', compactionId: 'compact-1' }),
      expect.objectContaining({
        allowSessionSwitch: false,
        notify: false,
        replay: true,
        runningCompactionId: 'compact-2',
      }),
    );
    expect(handleCompactionLifecycleEvent).toHaveBeenLastCalledWith(
      expect.objectContaining({ type: 'context.compaction.started', compactionId: 'compact-2' }),
      expect.objectContaining({
        allowSessionSwitch: false,
        notify: false,
        replay: true,
        runningCompactionId: 'compact-2',
      }),
    );
  });
});
