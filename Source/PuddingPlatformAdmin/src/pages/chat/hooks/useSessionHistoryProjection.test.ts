import { act, renderHook } from '@testing-library/react';
import { listSessionMessages } from '@/services/platform/api';
import { useSessionHistoryProjection } from './useSessionHistoryProjection';

jest.mock('@/services/platform/api', () => ({
  listSessionMessages: jest.fn(),
}));

const historyPage = {
  items: [
    {
      id: 1,
      messageId: 'user-message',
      turnId: 'turn-1',
      role: 'user' as const,
      content: 'question',
      createdAt: 1,
    },
    {
      id: 2,
      messageId: 'assistant-message',
      turnId: 'turn-1',
      role: 'agent' as const,
      content: 'answer',
      createdAt: 2,
      thinking: [{ text: 'reasoning', timestamp: 2 }],
    },
  ],
  hasMore: false,
  oldestCreatedAt: null,
};

describe('useSessionHistoryProjection', () => {
  it('binds a projector that pairs persisted user and assistant messages', () => {
    let projector: ((response: typeof historyPage) => any[]) | undefined;
    const { result } = renderHook(() =>
      useSessionHistoryProjection({
        identity: {
          selectedSessionIdRef: { current: 'session-1' },
          sessionIdRef: { current: 'session-1' },
        },
        turns: {
          turnsRef: { current: [] },
          setTurns: jest.fn(),
          latestTurnIdRef: { current: null },
          setLoading: jest.fn(),
        },
        integrations: {
          mergeCompactionLifecycleTurns: (turns) => turns,
          syncCompletedHistoryEventCursor: jest.fn(),
          bindHistoryProjector: (nextProjector) => {
            projector = nextProjector as typeof projector;
          },
        },
      }),
    );

    const turns = projector?.(historyPage) ?? [];
    expect(result.current.toTurnsFromHistory(historyPage)).toEqual(turns);
    expect(turns).toHaveLength(1);
    expect(turns[0]).toMatchObject({
      turnId: 'turn-1',
      userMessage: { text: 'question', status: 'success' },
      assistant: {
        id: 'assistant-message',
        answerMarkdown: 'answer',
        status: 'success',
        renderMode: 'structured',
      },
    });
  });

  it('reconciles completed history and advances the history cursor', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(historyPage);
    const turnsRef = { current: [] as any[] };
    const setTurns = jest.fn();
    const setLoading = jest.fn();
    const syncCursor = jest.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() =>
      useSessionHistoryProjection({
        identity: {
          selectedSessionIdRef: { current: 'session-1' },
          sessionIdRef: { current: 'session-1' },
        },
        turns: {
          turnsRef,
          setTurns,
          latestTurnIdRef: { current: null },
          setLoading,
        },
        integrations: {
          mergeCompactionLifecycleTurns: (turns) => turns,
          syncCompletedHistoryEventCursor: syncCursor,
          bindHistoryProjector: jest.fn(),
        },
      }),
    );

    await act(async () => {
      await result.current.reconcileCompletedSessionMessages('session-1');
    });

    expect(turnsRef.current).toHaveLength(1);
    expect(setTurns).toHaveBeenCalledWith(turnsRef.current);
    expect(setLoading).toHaveBeenCalledWith(false);
    expect(syncCursor).toHaveBeenCalledWith('session-1');
  });
});
