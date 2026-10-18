import {
  getConversationHistory,
  getConversationOpen,
  getConversationProcess,
} from './conversationOpenApi';

const mockRequest = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => mockRequest(...args),
}));

describe('conversationOpenApi', () => {
  beforeEach(() => {
    mockRequest.mockReset();
  });

  it('opens a conversation with bounded window and conditional ETag headers', async () => {
    mockRequest.mockResolvedValueOnce({ conversationId: 'conv-1', history: { items: [] } });

    await getConversationOpen('ws/default', 'agent/a', {
      messageLimit: 30,
      etag: '"abc123"',
    });

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/agents/agent%2Fa/conversation-open?messageLimit=30',
      {
        method: 'GET',
        skipErrorHandler: true,
        signal: undefined,
        headers: { 'If-None-Match': '"abc123"' },
      },
    );
  });

  it('maps 304 to null so callers keep the cached snapshot', async () => {
    mockRequest.mockRejectedValueOnce({ response: { status: 304 } });

    await expect(
      getConversationOpen('ws', 'agent-1', { etag: '"v1"' }),
    ).resolves.toBeNull();
  });

  it('propagates non-304 open failures instead of inventing an empty conversation', async () => {
    mockRequest.mockRejectedValueOnce(new Error('500'));

    await expect(getConversationOpen('ws', 'agent-1')).rejects.toThrow('500');
  });

  it('pages history with a stable keyset cursor and omits empty filters', async () => {
    mockRequest.mockResolvedValueOnce({ items: [], hasMore: false });

    await getConversationHistory('conv/1', {
      beforeCreatedAt: 1729200000000,
      beforeMessageId: 'msg-1',
      limit: 20,
    });

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/conversations/conv%2F1/history?beforeCreatedAt=1729200000000&beforeMessageId=msg-1&limit=20',
      {
        method: 'GET',
        skipErrorHandler: true,
        signal: undefined,
      },
    );

    await getConversationHistory('conv-1', { limit: 20 });
    expect(mockRequest).toHaveBeenLastCalledWith(
      '/api/conversations/conv-1/history?limit=20',
      {
        method: 'GET',
        skipErrorHandler: true,
        signal: undefined,
      },
    );
  });

  it('pages process details by canonical sequence bounds', async () => {
    mockRequest.mockResolvedValueOnce({ items: [], hasMoreBefore: false });
    const controller = new AbortController();

    await getConversationProcess('conv-1', {
      turnId: 'turn/1',
      beforeSequence: 900,
      throughSequence: 1000,
      limit: 100,
      signal: controller.signal,
    });

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/conversations/conv-1/process?turnId=turn%2F1&beforeSequence=900&throughSequence=1000&limit=100',
      {
        method: 'GET',
        skipErrorHandler: true,
        signal: controller.signal,
      },
    );
  });

  it('propagates process failures (invalid cursor must stay visible)', async () => {
    mockRequest.mockRejectedValueOnce({ response: { status: 400 } });

    await expect(getConversationProcess('conv-1')).rejects.toBeTruthy();
  });
});
