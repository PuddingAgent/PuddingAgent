import { act, renderHook } from '@testing-library/react';
import { useRef } from 'react';
import { getAgentMessageQueue } from '@/services/platform/api';
import { useMessageInteractionQueue } from './useMessageInteractionQueue';

jest.mock('@/services/platform/api', () => ({
  createChatSteeringMessage: jest.fn(),
  getAgentMessageQueue: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  recordPerfEvent: jest.fn(),
}));

const messageApi = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
};

function useQueueHarness(workspaceId?: string) {
  const sessionIdRef = useRef<string | undefined>('session-1');
  const turnsRef = useRef([]);
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
      loading: false,
      turns: [],
      turnsRef: turnsRef as never,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      handleCompactCommand,
    },
    messageApi: messageApi as never,
  });
  return { ...queue, handleCompactCommand };
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
});
