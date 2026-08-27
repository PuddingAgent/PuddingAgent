import { act, renderHook, waitFor } from '@testing-library/react';
import { useEffect, useRef } from 'react';
import {
  createChatSteeringMessage,
  getAgentMessageQueue,
} from '@/services/platform/api';
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
  turns?: any[];
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
            turnId: 'active-turn-1',
            assistant: { isStreaming: true, status: 'streaming' },
          },
        ]
      : [],
  );
  const projectedTurns = options.turns ?? turnsRef.current;
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
      turns: projectedTurns,
      turnsRef: turnsRef as never,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      handleCompactCommand,
    },
    messageApi: messageApi as never,
  });
  // Mirror useChatState's ordering: the queue hook registers its effects before
  // the outer owner synchronizes the canonical turns ref.
  useEffect(() => {
    turnsRef.current = projectedTurns;
  }, [projectedTurns]);
  return { ...queue, handleCompactCommand, turnsRef };
}

describe('useMessageInteractionQueue', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (getAgentMessageQueue as jest.Mock).mockResolvedValue({ items: [] });
    (createChatSteeringMessage as jest.Mock).mockResolvedValue({
      steeringId: 'steering-1',
    });
  });

  it('forwards trimmed interaction commands through the bound sender', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() => useQueueHarness());

    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('  hello  '));

    expect(sendMessage).toHaveBeenCalledWith('hello', undefined);
  });

  it('projects only active backend deliveries into the composer queue', async () => {
    let resolveSnapshot: (snapshot: unknown) => void = () => {};
    (getAgentMessageQueue as jest.Mock).mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveSnapshot = resolve;
        }),
    );

    const { result } = renderHook(() => useQueueHarness('ws-1'));

    await waitFor(() =>
      expect(getAgentMessageQueue).toHaveBeenCalledWith('ws-1', 'agent-1', {
        limit: 20,
        includeTerminal: false,
      }),
    );
    await act(async () => {
      resolveSnapshot({
        items: [
          {
            deliveryId: 'delivered-1',
            queueKind: 'message_delivery',
            content: '已完成历史',
            createdAt: 1,
            status: 'delivered',
          },
          {
            deliveryId: 'claimed-1',
            queueKind: 'message_delivery',
            content: '已被消费者认领',
            createdAt: 2,
            status: 'delivering',
          },
          {
            deliveryId: 'queued-1',
            queueKind: 'chat_turn',
            messageId: 'user-1',
            workspaceId: 'ws-1',
            content: '等待处理',
            createdAt: 3,
            status: 'queued',
            priority: 0,
            attemptCount: 0,
          },
        ],
      });
    });
    expect(result.current.interactionQueue.map((item) => item.id)).toEqual([
      'queued-1',
    ]);
    expect(result.current.interactionQueue[0].metadata?.queueKind).toBe(
      'chat_turn',
    );
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

  it('submits every normal message to the durable Turn API even while busy', async () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true, busyTurns: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));
    await act(async () => result.current.submitInteraction('first'));
    await act(async () => result.current.submitInteraction('second'));

    expect(sendMessage).toHaveBeenNthCalledWith(1, 'first', undefined);
    expect(sendMessage).toHaveBeenNthCalledWith(2, 'second', undefined);
    expect(result.current.interactionQueue).toHaveLength(0);
  });

  it('enqueueInteraction returns no browser-owned id and forwards while busy', () => {
    const sendMessage = jest.fn(async () => {});
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true, busyTurns: true }),
    );
    act(() => result.current.bindSendMessage(sendMessage));

    let queueItemId: string | null = 'unexpected';
    act(() => {
      queueItemId = result.current.enqueueInteraction('queued-message');
    });

    expect(queueItemId).toBeNull();
    expect(sendMessage).toHaveBeenCalledWith('queued-message', undefined);
  });

  it('allows only one steering admission in flight for the same source item', async () => {
    let resolveAdmission!: (value: { steeringId: string }) => void;
    (createChatSteeringMessage as jest.Mock).mockImplementationOnce(
      () =>
        new Promise<{ steeringId: string }>((resolve) => {
          resolveAdmission = resolve;
        }),
    );
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true, busyTurns: true }),
    );

    let firstAdmission!: Promise<boolean>;
    let secondAccepted = true;
    await act(async () => {
      firstAdmission = result.current.submitSteeringInteraction(
        '只注入一次',
        'source-1',
      );
      secondAccepted = await result.current.submitSteeringInteraction(
        '不应重复注入',
        'source-1',
      );
    });
    expect(createChatSteeringMessage).toHaveBeenCalledTimes(1);
    expect(secondAccepted).toBe(false);

    await act(async () => {
      resolveAdmission({ steeringId: 'steering-once' });
      expect(await firstAdmission).toBe(true);
    });
  });

  it('uses Ctrl+Enter as insertion mode while the active turn is running', async () => {
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true, busyTurns: true }),
    );
    act(() => result.current.setInputValue('立即改查日志'));
    const preventDefault = jest.fn();

    await act(async () => {
      result.current.handleKeyDown({
        key: 'Enter',
        ctrlKey: true,
        metaKey: false,
        shiftKey: false,
        preventDefault,
      } as never);
      await Promise.resolve();
    });

    expect(preventDefault).toHaveBeenCalled();
    expect(createChatSteeringMessage).toHaveBeenCalledWith(
      'ws-1',
      'session-1',
      'active-turn-1',
      expect.objectContaining({ messageText: '立即改查日志' }),
    );
    expect(
      result.current.interactionQueue.filter(
        (item) => item.source === 'local_pending',
      ),
    ).toHaveLength(0);
  });

  it('restores direct insertion text when the active turn rejects steering', async () => {
    (createChatSteeringMessage as jest.Mock).mockRejectedValueOnce(
      new Error('Steering rejected: turn is succeeded.'),
    );
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true, busyTurns: true }),
    );
    act(() => result.current.setInputValue('不要丢失这条插嘴'));

    await act(async () => {
      result.current.handleKeyDown({
        key: 'Enter',
        ctrlKey: true,
        metaKey: false,
        shiftKey: false,
        preventDefault: jest.fn(),
      } as never);
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(result.current.inputValue).toBe('不要丢失这条插嘴');
    });
  });

  it('stopQueue aborts the current request without deleting server-owned queue items', async () => {
    (getAgentMessageQueue as jest.Mock).mockResolvedValue({
      items: [
        {
          deliveryId: 'turn:cmd-1',
          queueKind: 'chat_turn',
          messageId: 'user-1',
          workspaceId: 'ws-1',
          content: '服务端已受理',
          status: 'queued',
          priority: 0,
          attemptCount: 0,
          createdAt: 1,
        },
      ],
    });
    const cancelAll = jest.fn();
    const { result } = renderHook(() =>
      useQueueHarness('ws-1', { loading: true }),
    );
    act(() => result.current.bindCancelAll(cancelAll));
    await waitFor(() => expect(result.current.interactionQueue).toHaveLength(1));

    act(() => result.current.stopQueue());

    expect(cancelAll).toHaveBeenCalledTimes(1);
    expect(result.current.interactionQueue).toEqual([
      expect.objectContaining({ id: 'turn:cmd-1', text: '服务端已受理' }),
    ]);
  });
});
