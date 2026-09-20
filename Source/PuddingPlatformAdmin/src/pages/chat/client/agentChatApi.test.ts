import {
  getAgentConversation,
  getAgentMessageProcessItems,
  listAgentStatuses,
  loadAgentAccessLevel,
  saveAgentAccessLevel,
} from './agentChatApi';

const mockRequest = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => mockRequest(...args),
}));

describe('agentChatApi', () => {
  beforeEach(() => {
    mockRequest.mockReset();
  });

  it('uses Agent-first status and conversation endpoints', async () => {
    mockRequest.mockResolvedValueOnce([]);
    await listAgentStatuses('default');
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/agents/status',
      { method: 'GET' },
    );

    mockRequest.mockResolvedValueOnce({ messages: [] });
    await getAgentConversation('default', 'agent/a');
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/agents/agent%2Fa/conversation',
      {
        method: 'GET',
        skipErrorHandler: true,
      },
    );
  });

  it('maps unchanged conversation projections to null for cursor-based sync', async () => {
    mockRequest.mockRejectedValueOnce({ response: { status: 304 } });

    await expect(
      getAgentConversation('default', 'agent/a', 31053),
    ).resolves.toBeNull();

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/agents/agent%2Fa/conversation?knownCursor=31053',
      {
        method: 'GET',
        skipErrorHandler: true,
      },
    );
  });

  it('loads historical process items only for the selected message', async () => {
    mockRequest.mockResolvedValueOnce({ processItems: [] });

    await getAgentMessageProcessItems('workspace/a', 'agent/a', 'message/a');

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/workspace%2Fa/agents/agent%2Fa/conversation/messages/message%2Fa/process-items',
      // 生产已为历史过程项请求接入取消信号（切消息时中止在途请求）⇒ 断言里必须包含 signal，
      // 否则本用例会在生产正确时反而变红；用 objectContaining + expect.any(AbortSignal) 既不放松
      // 对 method 的检查，也不与信号的内部形态耦合。
      expect.objectContaining({ method: 'GET', signal: expect.any(AbortSignal) }),
    );
  });

  it('persists the Agent access level via REST', async () => {
    mockRequest.mockResolvedValueOnce(undefined);

    await expect(
      saveAgentAccessLevel('ws/default', 'agent/a', 'full'),
    ).resolves.toBe(true);

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/agents/agent%2Fa/access-level',
      {
        method: 'PUT',
        data: { level: 'full', durationSeconds: undefined },
        skipErrorHandler: true,
      },
    );
  });

  it('requests a temporary grant with the 5-minute duration', async () => {
    mockRequest.mockResolvedValueOnce(undefined);

    await saveAgentAccessLevel('ws/default', 'agent/a', 'fullTemporary');

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/agents/agent%2Fa/access-level',
      {
        method: 'PUT',
        data: { level: 'full', durationSeconds: 300 },
        skipErrorHandler: true,
      },
    );
  });

  it('returns false instead of throwing when the write fails', async () => {
    mockRequest.mockRejectedValueOnce(new Error('403'));

    await expect(
      saveAgentAccessLevel('ws/default', 'agent/a', 'full'),
    ).resolves.toBe(false);
  });

  it('reads auto / full / temporary access levels from the backend', async () => {
    mockRequest.mockResolvedValueOnce({
      level: 'auto',
      fullAccessActive: false,
    });
    await expect(
      loadAgentAccessLevel('ws/default', 'agent/a'),
    ).resolves.toEqual({ mode: 'auto', expiresAtUtc: null });

    mockRequest.mockResolvedValueOnce({
      level: 'full',
      fullAccessActive: true,
      temporary: false,
    });
    await expect(
      loadAgentAccessLevel('ws/default', 'agent/a'),
    ).resolves.toEqual({ mode: 'full', expiresAtUtc: null });

    mockRequest.mockResolvedValueOnce({
      level: 'full',
      fullAccessActive: true,
      temporary: true,
      expiresAtUtc: '2026-09-19T10:20:00.000Z',
    });
    await expect(
      loadAgentAccessLevel('ws/default', 'agent/a'),
    ).resolves.toEqual({
      mode: 'fullTemporary',
      expiresAtUtc: '2026-09-19T10:20:00.000Z',
    });

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/agents/agent%2Fa/access-level',
      { method: 'GET', skipErrorHandler: true },
    );
  });

  it('returns null for unknown payloads and network errors', async () => {
    mockRequest.mockResolvedValueOnce({ level: 'rogue' });
    await expect(
      loadAgentAccessLevel('ws/default', 'agent/a'),
    ).resolves.toBeNull();

    mockRequest.mockRejectedValueOnce(new Error('404'));
    await expect(
      loadAgentAccessLevel('ws/default', 'agent/a'),
    ).resolves.toBeNull();
  });
});
