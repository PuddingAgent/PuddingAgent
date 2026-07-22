import { act, renderHook } from '@testing-library/react';
import { useRef, useState } from 'react';
import { listSessionMessages } from '@/services/platform/api';
import { useMessageHistoryPagination } from './useMessageHistoryPagination';

jest.mock('@/services/platform/api', () => ({
  listSessionMessages: jest.fn(),
}));

const messageApi = { error: jest.fn() };

function useHistoryHarness() {
  const [turns, setTurns] = useState<unknown[]>([]);
  const turnsRef = useRef<unknown[]>([]);
  const history = useMessageHistoryPagination({
    selectedSessionId: 'session-1',
    turnsRef: turnsRef as never,
    setTurns: setTurns as never,
    messageApi: messageApi as never,
  });
  return { ...history, turns };
}

describe('useMessageHistoryPagination', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('loads and prepends an older history page', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue({
      items: [{ id: 1 }],
      hasMore: false,
      oldestCreatedAt: 10,
    });
    const { result } = renderHook(() => useHistoryHarness());
    act(() =>
      result.current.bindHistoryProjector(() => [
        {
          turnId: 'turn-1',
          userMessage: {},
          assistant: {},
        } as never,
      ]),
    );
    act(() => {
      result.current.setHasMoreMessages(true);
      result.current.setOldestMessageCursor(20);
    });

    await act(async () => result.current.loadMoreMessages());

    expect(listSessionMessages).toHaveBeenCalledWith('session-1', 20, 20);
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.hasMoreMessages).toBe(false);
    expect(result.current.oldestMessageCursor).toBe(10);
    expect(result.current.loadingMore).toBe(false);
  });

  it('resets all pagination state', () => {
    const { result } = renderHook(() => useHistoryHarness());
    act(() => {
      result.current.setHistoryLoading(true);
      result.current.setHasMoreMessages(true);
      result.current.setOldestMessageCursor(20);
    });

    act(() => result.current.resetHistoryPagination());

    expect(result.current.historyLoading).toBe(false);
    expect(result.current.hasMoreMessages).toBe(false);
    expect(result.current.oldestMessageCursor).toBeNull();
  });
});
