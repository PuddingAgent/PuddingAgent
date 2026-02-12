import { getAgentConversation, listAgentStatuses } from './agentChatApi';

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
});
