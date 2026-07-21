import {
  formatChatErrorDiagnostic,
  isChatStreamErrorEvent,
  looksLikePersistedErrorDiagnostic,
  toChatDiagValue,
} from './chatDiagnostics';

describe('chatDiagnostics', () => {
  it('bounds long strings and nested values', () => {
    const value = toChatDiagValue({
      message: 'x'.repeat(320),
      nested: { child: { hidden: true } },
    }) as Record<string, unknown>;

    expect(value.message).toBe(`${'x'.repeat(300)}...`);
    expect(value.nested).toEqual({ child: '[object]' });
  });

  it('serializes Error values without stack data', () => {
    expect(toChatDiagValue(new Error('boom'))).toEqual({
      name: 'Error',
      message: 'boom',
    });
  });

  it('formats stream errors with stable log lookup fields', () => {
    const markdown = formatChatErrorDiagnostic(
      {
        type: 'error',
        message: 'LLM 调用失败: Too many requests',
        sessionId: 'session-1',
        messageId: '1667',
        traceId: 'trace-1',
        errorId: 'llm-abc',
        location: 'agent.stream.llm_provider',
        errorCode: 'HTTP_429',
        round: 1,
        maxRounds: 200,
        modelId: 'deepseek-v4-flash',
        endpointHost: 'api.deepseek.com',
      },
      { turnId: 'local-turn' },
    );

    expect(markdown).toContain('Session ID: `session-1`');
    expect(markdown).toContain('Message ID / Turn ID: `1667`');
    expect(markdown).toContain('Error ID: `llm-abc`');
    expect(markdown).toContain('Round: `1/200`');
  });

  it('recognizes error terminal events and persisted diagnostics', () => {
    expect(
      isChatStreamErrorEvent({
        type: 'done',
        reply: '## 请求失败',
        isError: true,
      }),
    ).toBe(true);
    expect(
      looksLikePersistedErrorDiagnostic(
        'Session fuse triggered. Recovery: Send /resume to continue.',
      ),
    ).toBe(true);
  });
});
