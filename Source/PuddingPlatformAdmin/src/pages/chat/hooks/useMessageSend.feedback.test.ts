// P0-A 第一批：提交回执与失败草稿恢复。
// sendMessage 的失败路径必须把原文交还输入框（restoreDraft），
// 忙碌期间的提交在服务端受理后必须给出"已加入队列"回执。
import { act, renderHook } from '@testing-library/react';
import { useRef } from 'react';
import { submitConversationTurn } from '@/services/platform/api';
import { useMessageSend } from './useMessageSend';

jest.mock('@/services/platform/api', () => ({
  createSession: jest.fn(),
  ensureMainSession: jest.fn(),
  executeConversationSystemCommand: jest.fn(),
  submitConversationTurn: jest.fn(),
}));

jest.mock('../outbox/commandOutbox', () => ({
  enqueueCommand: jest.fn(async () => {}),
  dequeueCommand: jest.fn(async () => {}),
  markSending: jest.fn(async () => {}),
}));

jest.mock('@/utils/perfEventRuntime', () => ({
  markPerf: jest.fn(),
  measurePerf: jest.fn(),
  recordPerfEvent: jest.fn(),
  writeDebugSessionState: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

const messageApi = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
};

interface HarnessOverrides {
  loading?: boolean;
}

function useSendHarness(overrides: HarnessOverrides = {}) {
  const loadingRef = useRef(overrides.loading ?? false);
  const turnsRef = useRef<any[]>([]);
  const activeMessageIdsRef = useRef(new Set<string>());
  const messageIdToTurnIdRef = useRef(new Map<string, string>());
  const latestTurnIdRef = useRef<string | null>(null);
  const completedTurnsRef = useRef(new Set<string>());
  const pendingDeltaRef = useRef(new Map());
  const pendingThinkingRef = useRef(new Map());
  const duplicateDeltaReplayOffsetRef = useRef(new Map());
  const streamStartAtRef = useRef(new Map());
  const messageIdToAgentIdsRef = useRef(new Map());
  const sessionIdToAgentIdsRef = useRef(new Map());
  const sessionIdRef = useRef<string | undefined>('session-1');
  const selectedSessionIdRef = useRef<string | null>('session-1');
  const mainSessionIdRef = useRef<string | null>('main-1');
  const forceNewSessionRef = useRef(false);

  const restoreDraft = jest.fn();
  const setError = jest.fn();
  const setTurns = jest.fn((updater: any) => {
    if (typeof updater === 'function') {
      turnsRef.current = updater(turnsRef.current);
    } else {
      turnsRef.current = updater;
    }
  });

  const harness = useMessageSend({
    identity: {
      workspaceId: 'ws-1',
      agentId: 'agent-1',
      agents: [
        { agentId: 'agent-1', name: '测试代理', isEnabled: true },
      ] as never,
      mainSessionId: 'main-1',
      sessionIdRef,
      selectedSessionIdRef,
      mainSessionIdRef,
      forceNewSessionRef,
      resetMainSessionEnsureSuppression: () => undefined,
    },
    turns: {
      loading: loadingRef.current,
      setLoading: () => undefined,
      loadingRef,
      turnsRef,
      setTurns,
      activeMessageIdsRef,
      messageIdToTurnIdRef,
      latestTurnIdRef,
      completedTurnsRef,
      setViewportScrollIntent: () => undefined,
    },
    sessions: {
      setMainSessionId: () => undefined,
      setSelectedSessionId: () => undefined,
      setSessions: () => undefined,
      refreshSessions: async () => undefined,
    },
    stream: {
      startSessionEventStream: () => undefined,
      resetStreamCursorForSessionChange: () => undefined,
      replayMissedSessionEvents: async () => undefined,
      replayMissedSessionEventsIfNeeded: async () => undefined,
      reconcileCompletedSessionMessages: async () => undefined,
      pendingDeltaRef,
      pendingThinkingRef,
      duplicateDeltaReplayOffsetRef,
      streamStartAtRef,
      prepareForNewMessage: () => undefined,
    },
    activity: {
      setAgentIdsWorking: () => undefined,
      messageIdToAgentIdsRef,
      sessionIdToAgentIdsRef,
    },
    feedback: {
      setError,
      messageApi: messageApi as never,
      handleCompactCommand: async () => undefined,
      restoreDraft,
    },
  });

  return { ...harness, restoreDraft, setError, setTurns, turnsRef, loadingRef };
}

describe('useMessageSend feedback semantics', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('restores the draft when the server never accepts the submission', async () => {
    (submitConversationTurn as jest.Mock).mockRejectedValueOnce(
      new Error('network unavailable'),
    );
    const { result } = renderHook(() => useSendHarness());

    await act(async () => {
      await result.current.sendMessage('失败也不能丢的草稿');
    });

    expect(result.current.restoreDraft).toHaveBeenCalledWith('失败也不能丢的草稿');
    expect(result.current.setError).toHaveBeenCalledWith('network unavailable');
    // 失败轮保留在时间线作为记录，乐观 Turn 终态为 error
    const lastTurn = result.current.turnsRef.current.at(-1);
    expect(lastTurn?.assistant?.status).toBe('error');
    expect(lastTurn?.userMessage?.text).toBe('失败也不能丢的草稿');
  });

  it('acknowledges accepted submissions with a queue receipt while busy', async () => {
    (submitConversationTurn as jest.Mock).mockResolvedValueOnce({
      success: true,
      status: 'accepted',
      messageId: 'message-1',
      conversationId: 'session-1',
      turnIds: ['server-turn-1'],
      eventCursor: 0,
    });
    const { result } = renderHook(() => useSendHarness({ loading: true }));

    await act(async () => {
      await result.current.sendMessage('排队第二条消息');
    });

    expect(messageApi.success).toHaveBeenCalledWith(
      '已加入队列，将在当前回复完成后自动投递',
    );
    expect(result.current.restoreDraft).not.toHaveBeenCalled();
  });

  it('does not broadcast a queue receipt for idle submissions', async () => {
    (submitConversationTurn as jest.Mock).mockResolvedValueOnce({
      success: true,
      status: 'accepted',
      messageId: 'message-2',
      conversationId: 'session-1',
      turnIds: ['server-turn-2'],
      eventCursor: 0,
    });
    const { result } = renderHook(() => useSendHarness({ loading: false }));

    await act(async () => {
      await result.current.sendMessage('普通发送');
    });

    expect(messageApi.success).not.toHaveBeenCalled();
    expect(result.current.restoreDraft).not.toHaveBeenCalled();
  });
});
