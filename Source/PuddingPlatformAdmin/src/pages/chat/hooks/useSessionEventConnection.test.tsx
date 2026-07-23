import { act, renderHook } from '@testing-library/react';
import { subscribeSessionEvents } from '@/services/platform/api';
import { useSessionEventConnection } from './useSessionEventConnection';

jest.mock('@/services/platform/api', () => ({
  subscribeSessionEvents: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  recordPerfEvent: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

describe('useSessionEventConnection', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('starts the first SSE connection from the cursor synchronized by history', () => {
    const { result, unmount } = renderHook(() =>
      useSessionEventConnection(),
    );
    const resetStreamCursorForSessionChange = jest.fn();

    act(() => {
      result.current.bindSessionEventConnection({
        applySessionEvent: jest.fn(),
        handleSessionNotFound: jest.fn(),
        pruneTrackedActiveMessages: jest.fn(() => false),
        replayMissedSessionEvents: jest.fn(async () => {}),
        replayMissedSessionEventsIfNeeded: jest.fn(async () => false),
        resetStreamCursorForSessionChange,
        flushPendingDeltas: jest.fn(),
        syncSessionIdentity: jest.fn(),
        activeMessageIdsRef: { current: new Set() },
        lastSequenceNumRef: { current: 9865 },
        streamStartAtRef: { current: new Map() },
        selectedSessionIdRef: { current: 'session-a' },
        sessionIdRef: { current: 'session-a' },
        turnsRef: { current: [] },
      });
      result.current.startSessionEventStream('session-a');
    });

    expect(resetStreamCursorForSessionChange).not.toHaveBeenCalled();
    expect(subscribeSessionEvents).toHaveBeenCalledWith(
      'session-a',
      expect.any(Function),
      expect.any(AbortSignal),
      expect.objectContaining({ afterSequence: 9865 }),
    );

    act(() => result.current.stopSessionEventStream());
    unmount();
  });
});
