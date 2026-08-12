import {
  getAgentConversation,
  getAgentMessageProcessItems,
  listAgentStatuses,
  loadPermissionMode,
  savePermissionMode,
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
      { method: 'GET' },
    );
  });

  it('persists the permission mode via REST (P1#4)', async () => {
    mockRequest.mockResolvedValueOnce(undefined);

    await savePermissionMode('ws/default', 'acceptEdits');

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/user-preferences/permission-mode',
      {
        method: 'PUT',
        data: { mode: 'acceptEdits' },
        skipErrorHandler: true,
      },
    );
  });

  it('swallows REST persistence failures so chat flow is never interrupted', async () => {
    mockRequest.mockRejectedValueOnce(new Error('network down'));

    await expect(
      savePermissionMode('ws/default', 'plan'),
    ).resolves.toBeUndefined();
  });

  it('restores a valid permission mode from the backend', async () => {
    mockRequest.mockResolvedValueOnce({ mode: 'manual' });

    await expect(loadPermissionMode('ws/default')).resolves.toBe('manual');
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/user-preferences/permission-mode',
      { method: 'GET', skipErrorHandler: true },
    );
  });

  it('rejects unknown permission modes and backend errors on restore', async () => {
    mockRequest.mockResolvedValueOnce({ mode: 'rogue-mode' });
    await expect(loadPermissionMode('ws/default')).resolves.toBeNull();

    mockRequest.mockRejectedValueOnce(new Error('404'));
    await expect(loadPermissionMode('ws/default')).resolves.toBeNull();
  });
});
