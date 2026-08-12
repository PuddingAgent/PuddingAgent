import { act, renderHook } from '@testing-library/react';
import { useRef } from 'react';
import { getAgentMessageQueue } from '@/services/platform/api';
import { useMessageInteractionQueue } from './useMessageInteractionQueue';

jest.mock('@/services/platform/api', () => ({
  createChatSteeringMessage: jest.fn(),
  getAgentMessageQueue: jest.fn(),
}));

jest.mock('@/utils/perfEventRuntime', () => ({
  recordPerfEvent: jest.fn(),
}));

const messageApi = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
};

interface QueueHarnessOptions {
  loading?: boolean;
  busyTurns?: boolean;
}

function useQueueHarness(
  workspaceId?: string,
  options: QueueHarnessOptions = {},
) {
  const sessionIdRef = useRef<string | undefined>('session-1');
  const turnsRef = useRef<any[]>(
    options.busyTurns
      ? [
          {
            assistant: { isStreaming: true, status: 'streaming' },
          },
        ]
      : [],
  );
  const activeMessageIdsRef = useRef(new Set<string>());
  const messageIdToTurnIdRef = useRef(new Map<string, string>());
  const handleCompactCommandRef = useRef(jest.fn(async () => {}));
  const handleCompactCommand = handleCompactCommandRef.current;
  const queue = useMessageInteractionQueue({
    identity: {
      workspaceId,
      agentId: workspaceId ? 'agent-1' : undefined,
      selectedSessionId: 'session-1',
      sessionIdRef,
    },
    execution: {
      loading: options.loading ?? false,
      turns: turnsRef.current,
      turnsRef: turnsRef as never,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      handleCompactCommand,
    },
    messageApi: messageApi as never,
  });
  return { ...queue, handleCompactCommand, turnsRef };
}

describe('useMessageInteractionQueue', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (getAgentMessageQueue as jest.Mock).mockResolvedValue({ items: [] });
  });

  it('forwards trimmed interaction commands through the bound sender', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() => useQueueHarness());

    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('  hello  '));

    expect(sendMessage).toHaveBeenCalledWith('hello', undefined);
  });

  it('routes the compact command without invoking the message sender', () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() => useQueueHarness());
    act(() => result.current.bindSendMessage(sendMessage));
    act(() => result.current.setInputValue('/compact'));

    const preventDefault = jest.fn();
    act(() =>
      result.current.handleKeyDown({
        key: 'Enter',
        ctrlKey: false,
        metaKey: false,
        shiftKey: false,
        preventDefault,
      } as never),
    );

    expect(preventDefault).toHaveBeenCalled();
    expect(result.current.handleCompactCommand).toHaveBeenCalled();
    expect(sendMessage).not.toHaveBeenCalled();
  });

  it('P1#6: auto-queues messages locally while busy and drains them in order when idle', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result, rerender } = renderHook(
      ({ loading }: { loading: boolean }) =>
        useQueueHarness('ws-1', { loading }),
      { initialProps: { loading: true } },
    );

    act(() => result.current.bindSendMessage(sendMessage));
    // busy → both messages go to the local pending queue, not the sender
    await act(async () => result.current.submitInteraction('first'));
    await act(async () => result.current.submitInteraction('second'));
    expect(sendMessage).not.toHaveBeenCalled();

    const queued = result.current.interactionQueue.filter(
      (item) => item.source === 'local_pending',
    );
    expect(queued.map((item) => item.text)).toEqual(['first', 'second']);

    // idle → drain sends the first queued item
    rerender({ loading: false });
    await act(async () => {
      await Promise.resolve();
    });
    expect(sendMessage).toHaveBeenCalledTimes(1);
    expect(sendMessage).toHaveBeenCalledWith('first', undefined);
    expect(
      result.current.interactionQueue.filter(
        (item) => item.source === 'local_pending',
      ),
    ).toHaveLength(1);

    // second idle transition drains the remaining item
    rerender({ loading: true });
    rerender({ loading: false });
    await act(async () => {
      await Promise.resolve();
    });
    expect(sendMessage).toHaveBeenCalledTimes(2);
    expect(sendMessage).toHaveBeenLastCalledWith('second', undefined);
  });

  it('P1#6: deletes a local pending queue item without touching the sender', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(
      ({ loading }: { loading: boolean }) =>
        useQueueHarness('ws-1', { loading }),
      { initialProps: { loading: true } },
    );
    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('keep'));
    await act(async () => result.current.submitInteraction('drop'));
    const dropId = result.current.interactionQueue.find(
      (item) => item.source === 'local_pending' && item.text === 'drop',
    )?.id;
    expect(dropId).toBeTruthy();

    act(() => result.current.deleteQueuedInteraction(dropId as string));

    expect(
      result.current.interactionQueue.filter(
        (item) => item.source === 'local_pending',
      ),
    ).toHaveLength(1);
    expect(sendMessage).not.toHaveBeenCalled();
  });

  it('P1#6: steer on a local pending item yields to the next message (swap)', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('A'));
    await act(async () => result.current.submitInteraction('B'));
    await act(async () => result.current.submitInteraction('C'));

    // 让位给下一条：A 与 B 交换 → [B, A, C]
    const aId = result.current.interactionQueue.find(
      (item) => item.source === 'local_pending' && item.text === 'A',
    )?.id;
    act(() => {
      void result.current.steerQueuedInteraction(aId as string);
    });
    const order = result.current.interactionQueue
      .filter((item) => item.source === 'local_pending')
      .map((item) => item.text);
    expect(order).toEqual(['B', 'A', 'C']);
  });

  it('P1#6: reorders local pending items via drag drop mapping', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('A'));
    await act(async () => result.current.submitInteraction('B'));
    await act(async () => result.current.submitInteraction('C'));

    const aId = result.current.interactionQueue.find(
      (item) => item.source === 'local_pending' && item.text === 'A',
    )?.id;
    const cId = result.current.interactionQueue.find(
      (item) => item.source === 'local_pending' && item.text === 'C',
    )?.id;

    act(() =>
      result.current.reorderQueuedInteraction(aId as string, cId as string),
    );
    const order = result.current.interactionQueue
      .filter((item) => item.source === 'local_pending')
      .map((item) => item.text);
    expect(order).toEqual(['B', 'C', 'A']);
  });

  it('P1#6: stopQueue aborts in-flight request, clears local queue and pending steering', async () => {
    const sendMessage = jest.fn(async () => {});
    const cancelAll = jest.fn();
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));
    act(() => result.current.bindCancelAll(cancelAll));
    await act(async () => result.current.submitInteraction('pending-1'));
    await act(async () => result.current.submitInteraction('pending-2'));

    act(() => result.current.stopQueue());

    expect(cancelAll).toHaveBeenCalledTimes(1);
    expect(
      result.current.interactionQueue.filter(
        (item) => item.source === 'local_pending',
      ),
    ).toHaveLength(0);
    expect(sendMessage).not.toHaveBeenCalled();
  });

  it('P1#6: enqueueInteraction returns the local item id when busy', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));

    let queuedId: string | null = null;
    act(() => {
      queuedId = result.current.enqueueInteraction('queued-message');
    });
    expect(queuedId).toMatch(/^local-/);
    expect(sendMessage).not.toHaveBeenCalled();
  });
});
