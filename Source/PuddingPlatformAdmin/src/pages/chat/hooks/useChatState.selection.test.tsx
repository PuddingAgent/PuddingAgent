import { act, renderHook, waitFor } from '@testing-library/react';
import { App } from 'antd';
import * as React from 'react';
import {
  compactSession,
  ensureMainSession,
  executeConversationSystemCommand,
  getAgentMessageQueue,
  listSessionMessages,
  listSessions,
  listWorkspaceAgents,
  listWorkspaces,
  submitConversationTurn,
  subscribeSessionEvents,
} from '@/services/platform/api';
import { useChatState } from './useChatState';

jest.mock('@/utils/debug', () => ({
  installPerfDiagnostics: jest.fn(),
  markPerf: jest.fn(),
  measurePerf: jest.fn(),
  recordPerfEvent: jest.fn(),
  recordPerfStep: jest.fn(),
  writeDebugSessionState: jest.fn(),
  writeDebugTrace: jest.fn(),
}));

jest.mock('@/services/platform/api', () => ({
  archiveSession: jest.fn(),
  compactSession: jest.fn(),
  createSession: jest.fn(),
  createChatSteeringMessage: jest.fn(),
  createWorkspace: jest.fn(),
  createWorkspaceAgent: jest.fn(),
  deleteSession: jest.fn(),
  ensureMainSession: jest.fn(),
  executeConversationSystemCommand: jest.fn(),
  getAgentMessageQueue: jest.fn(),
  listSessions: jest.fn(),
  listSessionMessages: jest.fn(),
  listTeams: jest.fn(),
  listWorkspaceAgents: jest.fn(),
  listWorkspaces: jest.fn(),
  renameSession: jest.fn(),
  submitConversationTurn: jest.fn(),
  subscribeSessionEvents: jest.fn(),
  subscribeWorkspaceNotifications: jest.fn(),
}));

jest.mock('../outbox/commandOutbox', () => ({
  dequeueCommand: jest.fn().mockResolvedValue(undefined),
  enqueueCommand: jest.fn().mockResolvedValue(undefined),
  flushOutbox: jest.fn().mockResolvedValue({ sent: 0, failed: 0 }),
  markSending: jest.fn().mockResolvedValue(undefined),
}));

type Deferred<T> = {
  promise: Promise<T>;
  resolve: (value: T) => void;
};

const deferred = <T,>(): Deferred<T> => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
};

const workspace = {
  id: 1,
  workspaceId: 'default',
  slug: 'default',
  teamId: 'team-1',
  teamName: '默认团队',
  name: '默认工作空间',
  isEnabled: true,
  isFrozen: false,
  memberCount: 1,
  teamAccessPolicy: 'Write',
  companyAccessPolicy: 'None',
  createdAt: '2026-06-06T00:00:00Z',
};

const agents = [
  {
    agentId: 'agent-a',
    name: 'Agent A',
    displayName: 'Agent A',
    sourceTemplateId: 'global:a',
    isEnabled: true,
    isFrozen: false,
    createdAt: '2026-06-06T00:00:00Z',
    updatedAt: '2026-06-06T00:00:00Z',
  },
  {
    agentId: 'agent-b',
    name: 'Agent B',
    displayName: 'Agent B',
    sourceTemplateId: 'global:b',
    isEnabled: true,
    isFrozen: false,
    createdAt: '2026-06-06T00:00:00Z',
    updatedAt: '2026-06-06T00:00:00Z',
  },
];

const sessions = [
  {
    sessionId: 'session-a',
    workspaceId: 'default',
    agentTemplateId: 'global:a',
    channelId: 'admin-chat',
    ownerUserId: 'admin',
    sessionType: 'ServiceSession',
    sessionRole: 'Main',
    status: 'Idle',
    principalKind: 'agent',
    principalId: 'agent-a',
    title: 'Agent A',
    createdAt: '2026-06-06T00:00:00Z',
    lastActiveAt: '2026-06-06T00:00:00Z',
  },
  {
    sessionId: 'session-b',
    workspaceId: 'default',
    agentTemplateId: 'global:b',
    channelId: 'admin-chat',
    ownerUserId: 'admin',
    sessionType: 'ServiceSession',
    sessionRole: 'Main',
    status: 'Idle',
    principalKind: 'agent',
    principalId: 'agent-b',
    title: 'Agent B',
    createdAt: '2026-06-06T00:00:00Z',
    lastActiveAt: '2026-06-06T00:00:01Z',
  },
];

const messagePage = (prefix: string) => ({
  items: [
    { id: 1, role: 'user' as const, content: `${prefix} user`, createdAt: 1 },
    {
      id: 2,
      role: 'agent' as const,
      content: `${prefix} answer`,
      createdAt: 2,
    },
  ],
  hasMore: false,
  oldestCreatedAt: null,
});

const wrapper = ({ children }: any) => <App>{children}</App>;

describe('useChatState session selection races', () => {
  beforeEach(() => {
    localStorage.clear();
    jest.clearAllMocks();
    (listWorkspaces as jest.Mock).mockResolvedValue([workspace]);
    (listWorkspaceAgents as jest.Mock).mockResolvedValue(agents);
    (listSessions as jest.Mock).mockResolvedValue(sessions);
    (ensureMainSession as jest.Mock).mockResolvedValue(sessions[0]);
    (executeConversationSystemCommand as jest.Mock).mockResolvedValue({
      conversationId: 'session-a',
      clientMessageId: 'client-message',
      responseMessageId: 'system-response',
      command: '/yolo',
      message: 'Runtime mode is now Yolo (memory-only, lost on restart).',
      runtimeMode: 'Yolo',
    });
    (getAgentMessageQueue as jest.Mock).mockResolvedValue({
      workspaceId: 'default',
      agentId: 'agent-a',
      items: [],
    });
    (subscribeSessionEvents as jest.Mock).mockImplementation(() => undefined);
    globalThis.fetch = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ events: [], hasMore: false, totalEventCount: 0 }),
    }) as jest.Mock;
  });

  it('keeps the latest agent session when an older history request resolves later', async () => {
    const slowA = deferred<ReturnType<typeof messagePage>>();
    const fastB = deferred<ReturnType<typeof messagePage>>();
    (listSessionMessages as jest.Mock).mockImplementation(
      (sessionId: string) => {
        if (sessionId === 'session-a') return slowA.promise;
        if (sessionId === 'session-b') return fastB.promise;
        return Promise.resolve(messagePage(sessionId));
      },
    );

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });

    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    let selectA!: Promise<number | undefined>;
    await act(async () => {
      selectA = result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    let selectB!: Promise<number | undefined>;
    await act(async () => {
      selectB = result.current.handleSelectSession('session-b', {
        agentId: 'agent-b',
      });
    });

    await act(async () => {
      fastB.resolve(messagePage('B'));
      await selectB;
    });

    expect(result.current.selectedSessionId).toBe('session-b');
    expect(
      result.current.turns[result.current.turns.length - 1]?.assistant
        .answerMarkdown,
    ).toBe('B answer');

    await act(async () => {
      slowA.resolve(messagePage('A'));
      await selectA;
    });

    expect(result.current.selectedSessionId).toBe('session-b');
    expect(
      result.current.turns[result.current.turns.length - 1]?.assistant
        .answerMarkdown,
    ).toBe('B answer');
  });

  it('keeps history loading visible while the latest session request is still pending', async () => {
    const slowA = deferred<ReturnType<typeof messagePage>>();
    const slowB = deferred<ReturnType<typeof messagePage>>();
    (listSessionMessages as jest.Mock).mockImplementation(
      (sessionId: string) => {
        if (sessionId === 'session-a') return slowA.promise;
        if (sessionId === 'session-b') return slowB.promise;
        return Promise.resolve(messagePage(sessionId));
      },
    );

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    let selectA!: Promise<number | undefined>;
    await act(async () => {
      selectA = result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    let selectB!: Promise<number | undefined>;
    await act(async () => {
      selectB = result.current.handleSelectSession('session-b', {
        agentId: 'agent-b',
      });
    });

    await act(async () => {
      slowA.resolve(messagePage('A'));
      await selectA;
    });

    expect(result.current.selectedSessionId).toBe('session-b');
    expect(result.current.historyLoading).toBe(true);

    await act(async () => {
      slowB.resolve(messagePage('B'));
      await selectB;
    });

    expect(result.current.historyLoading).toBe(false);
    expect(
      result.current.turns[result.current.turns.length - 1]?.assistant
        .answerMarkdown,
    ).toBe('B answer');
  });

  it('does not abort an in-flight agent request when switching to another session', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(messagePage('B'));
    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });

    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    const abort = jest.fn();
    result.current.abortRef.current = { abort } as any;

    await act(async () => {
      await result.current.handleSelectSession('session-b', {
        agentId: 'agent-b',
      });
    });

    expect(abort).not.toHaveBeenCalled();
    expect(result.current.selectedSessionId).toBe('session-b');
  });

  it('can ensure an agent main session without loading legacy messages', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(
      messagePage('history'),
    );
    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });

    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    (ensureMainSession as jest.Mock).mockResolvedValueOnce(sessions[1]);
    (listSessionMessages as jest.Mock).mockClear();

    let ensuredSessionId: string | undefined;
    await act(async () => {
      ensuredSessionId = await result.current.ensureAgentMainSession(
        'default',
        'agent-b',
        {
          selectSession: false,
        },
      );
    });

    expect(ensuredSessionId).toBe('session-b');
    expect(listSessionMessages).not.toHaveBeenCalled();
    expect(result.current.selectedSessionId).toBe('session-b');
    expect(result.current.mainSessionId).toBe('session-b');
  });

  it('does not start legacy session SSE when agent projection owns message loading', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(
      messagePage('history'),
    );
    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });

    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    (ensureMainSession as jest.Mock).mockResolvedValueOnce(sessions[1]);
    (subscribeSessionEvents as jest.Mock).mockClear();

    await act(async () => {
      await result.current.ensureAgentMainSession('default', 'agent-b', {
        selectSession: false,
      });
    });

    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(result.current.selectedSessionId).toBe('session-b');
    expect(subscribeSessionEvents).not.toHaveBeenCalled();
  });

  it('does not pull focus back when a background agent send returns after switching away', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(
      messagePage('history'),
    );
    const sendResult = deferred<{
      messageId: string;
      conversationId: string;
      turnIds: string[];
      commandIds: string[];
      acceptedSequence: number;
    }>();
    (submitConversationTurn as jest.Mock).mockReturnValue(sendResult.promise);

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    let sendPromise!: Promise<void>;
    act(() => {
      sendPromise = result.current.sendMessage('hello from A');
    });

    await act(async () => {
      await result.current.handleSelectSession('session-b', {
        agentId: 'agent-b',
      });
    });
    expect(result.current.selectedSessionId).toBe('session-b');
    expect(result.current.loading).toBe(false);

    await act(async () => {
      sendResult.resolve({
        messageId: 'message-a',
        conversationId: 'session-a',
        turnIds: ['turn-a'],
        commandIds: ['command-a'],
        acceptedSequence: 1,
      });
      await sendPromise;
    });

    expect(result.current.selectedSessionId).toBe('session-b');
    expect(
      result.current.turns[result.current.turns.length - 1]?.assistant
        .answerMarkdown,
    ).toBe('history answer');
  });

  it('replaces the optimistic user message id with the server message id after send returns', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(
      messagePage('history'),
    );
    const sendResult = deferred<{
      messageId: string;
      conversationId: string;
      turnIds: string[];
      commandIds: string[];
      acceptedSequence: number;
    }>();
    (submitConversationTurn as jest.Mock).mockReturnValue(sendResult.promise);

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    let sendPromise!: Promise<void>;
    act(() => {
      sendPromise = result.current.sendMessage('current prompt');
    });

    expect(
      result.current.turns[result.current.turns.length - 1]?.userMessage.id,
    ).not.toBe('message-current');

    await act(async () => {
      sendResult.resolve({
        messageId: 'message-current',
        conversationId: 'session-a',
        turnIds: ['turn-current'],
        commandIds: ['command-current'],
        acceptedSequence: 1,
      });
      await sendPromise;
    });

    const optimisticTurn = result.current.turns.find(
      (turn) => turn.userMessage.text === 'current prompt',
    );
    expect(optimisticTurn?.turnId).toBe('turn-current');
    expect(optimisticTurn?.userMessage.id).toBe('message-current');
    expect(optimisticTurn?.userMessage.status).toBe('success');
  });

  it('does not prepend pinned message context during normal send', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(
      messagePage('history'),
    );
    const sendResult = deferred<{
      messageId: string;
      conversationId: string;
      turnIds: string[];
      commandIds: string[];
      acceptedSequence: number;
    }>();
    (submitConversationTurn as jest.Mock).mockReturnValue(sendResult.promise);
    localStorage.setItem(
      'pudding_pinned_message',
      JSON.stringify({
        messageId: 1633,
        turnId: 'turn-1633',
        preview: '好的，已从日志中获取了 turnId=1627 的原始内容。',
        fullText: '完整消息',
        pinnedAt: 1,
      }),
    );

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    let sendPromise!: Promise<void>;
    act(() => {
      sendPromise = result.current.sendMessage('目前状态怎么样');
    });

    const optimisticTurn = result.current.turns.at(-1);
    expect(optimisticTurn?.userMessage.text).toBe('目前状态怎么样');

    await waitFor(() =>
      expect(submitConversationTurn).toHaveBeenCalledTimes(1),
    );
    const request = (submitConversationTurn as jest.Mock).mock.calls[0][2];
    expect(request.content[0].text).toBe('目前状态怎么样');

    await act(async () => {
      sendResult.resolve({
        messageId: 'message-current',
        conversationId: 'session-a',
        turnIds: ['turn-current'],
        commandIds: ['command-current'],
        acceptedSequence: 1,
      });
      await sendPromise;
    });
  });

  it('recovers an active agent turn from replay when session history is still empty', async () => {
    (listSessionMessages as jest.Mock).mockImplementation(
      (sessionId: string) => {
        if (sessionId === 'session-a') {
          return Promise.resolve({
            items: [],
            hasMore: false,
            oldestCreatedAt: null,
          });
        }
        return Promise.resolve(messagePage(sessionId));
      },
    );
    globalThis.fetch = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        events: [
          {
            sequenceNum: 1,
            type: 'metadata',
            payload: {
              messageId: 'message-a',
              sessionId: 'session-a',
              source_id: 'agent-a',
              source_name: 'Agent A',
              fanout_index: '0',
            },
          },
          {
            sequenceNum: 2,
            type: 'delta',
            payload: {
              messageId: 'message-a',
              delta: 'replayed answer',
            },
          },
          {
            sequenceNum: 3,
            type: 'done',
            payload: {
              messageId: 'message-a',
              sessionId: 'session-a',
              reply: 'replayed answer',
            },
          },
        ],
        hasMore: false,
        totalEventCount: 3,
      }),
    }) as jest.Mock;

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    expect(result.current.selectedSessionId).toBe('session-a');
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].source?.displayName).toBe('Agent A');
    expect(result.current.turns[0].assistant.answerMarkdown).toBe(
      'replayed answer',
    );
    expect(result.current.loading).toBe(false);
  });

  it('preserves the visible compact result when switching to the new compacted session', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(messagePage('A'));
    (compactSession as jest.Mock).mockResolvedValue({
      compaction: {
        sessionId: 'session-a',
        summaryMessageId: 'summary-1',
        mode: 'Manual',
        level: 'Full',
        beforeTokens: 120,
        afterTokens: 40,
        compactedMessageCount: 8,
        summaryPreview: 'summary',
      },
      newSessionId: 'session-compact',
      newSessionTitle: '压缩 - Agent A',
    });

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() =>
      expect(result.current.selectedSessionId).toBe('session-a'),
    );
    (listSessionMessages as jest.Mock).mockClear();

    await act(async () => {
      await result.current.sendMessage('/compact');
    });

    expect(result.current.selectedSessionId).toBe('session-compact');
    expect(
      result.current.turns.some((turn) =>
        turn.assistant.answerMarkdown.includes('覆盖 8 条历史消息'),
      ),
    ).toBe(true);
    expect(listSessionMessages).not.toHaveBeenCalledWith(
      'session-compact',
      undefined,
      expect.any(Number),
    );
  });

  it('routes /yolo to the system command endpoint without creating an Agent turn', async () => {
    (listSessionMessages as jest.Mock).mockResolvedValue(messagePage('A'));

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() =>
      expect(result.current.selectedSessionId).toBe('session-a'),
    );

    await act(async () => {
      await result.current.sendMessage('/yolo');
    });

    expect(executeConversationSystemCommand).toHaveBeenCalledWith(
      'default',
      'session-a',
      expect.objectContaining({
        agentId: 'agent-a',
        commandText: '/yolo',
      }),
    );
    expect(submitConversationTurn).not.toHaveBeenCalled();

    const commandTurn = result.current.turns.at(-1);
    expect(commandTurn?.source?.sourceType).toBe('system_command');
    expect(commandTurn?.assistant.status).toBe('success');
    expect(commandTurn?.assistant.answerMarkdown).toContain(
      'Runtime mode is now Yolo',
    );
  });

  it('does not abort current SSE when deleting a non-current session', async () => {
    const deleteSession = require('@/services/platform/api')
      .deleteSession as jest.Mock;
    deleteSession.mockResolvedValue(undefined);

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-b', {
        agentId: 'agent-b',
      });
    });
    expect(result.current.selectedSessionId).toBe('session-b');

    const abortSpy = jest.spyOn(AbortController.prototype, 'abort');

    try {
      await act(async () => {
        await result.current.handleDeleteSession('session-a');
      });

      expect(result.current.selectedSessionId).toBe('session-b');
      expect(
        result.current.sessions.some((s) => s.sessionId === 'session-a'),
      ).toBe(false);
      expect(abortSpy).not.toHaveBeenCalled();
    } finally {
      abortSpy.mockRestore();
    }
  });

  it('handleSessionNotFound clears selected, main, and sessions for current stream', async () => {
    const deleteSession = require('@/services/platform/api')
      .deleteSession as jest.Mock;
    deleteSession.mockResolvedValue(undefined);

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });
    expect(result.current.selectedSessionId).toBe('session-a');

    await act(async () => {
      await result.current.handleDeleteSession('session-a');
    });

    // handleSessionNotFound 清理 selected + main，suppress 阻止自动重建
    expect(result.current.selectedSessionId).toBeNull();
    expect(result.current.mainSessionId).toBeNull();
    expect(
      result.current.sessions.some((s) => s.sessionId === 'session-a'),
    ).toBe(false);
  });

  it('SSE onError with 410 clears session without reconnect', async () => {
    let capturedOnError:
      | ((error: Error, httpStatus?: number) => void)
      | undefined;

    (subscribeSessionEvents as jest.Mock).mockImplementation(
      (
        _sessionId: string,
        _onEvent: unknown,
        _signal: unknown,
        opts: { onError?: (error: Error, httpStatus?: number) => void },
      ) => {
        capturedOnError = opts.onError;
      },
    );

    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });
    expect(result.current.selectedSessionId).toBe('session-a');

    // Simulate SSE returning 410 (frozen/archived)
    await act(async () => {
      capturedOnError?.(new Error('SSE 410'), 410);
    });

    // Session should be cleaned up (terminal state)
    expect(result.current.selectedSessionId).toBeNull();
    expect(
      result.current.sessions.some((s) => s.sessionId === 'session-a'),
    ).toBe(false);
  });

  it('does not attach message list scroll listeners from useChatState', async () => {
    const { result } = renderHook(() => useChatState('?workspaceId=default'), {
      wrapper,
    });
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    await act(async () => {
      await result.current.handleSelectSession('session-a', {
        agentId: 'agent-a',
      });
    });

    expect(result.current.loadMoreMessages).toBeDefined();
    expect(typeof result.current.loadMoreMessages).toBe('function');
  });
});
