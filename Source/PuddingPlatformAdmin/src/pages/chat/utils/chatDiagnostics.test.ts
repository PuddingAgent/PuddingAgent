import {
  buildChatDiagnosticCopyPayload,
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
        type: 'turn.completed',
        reply: '## 请求失败',
        isError: true,
      }),
    ).toBe(true);
    // TR-01/CU-02：canonical 终态事件名（turn.failed 直接判错）。
    expect(
      isChatStreamErrorEvent({
        type: 'turn.failed',
        message: 'boom',
      }),
    ).toBe(true);
    expect(
      looksLikePersistedErrorDiagnostic(
        'Session fuse triggered. Recovery: Send /resume to continue.',
      ),
    ).toBe(true);
  });

  // 可诊断基础设施 §12：界面第一眼要能认出错误类别，且复制出去的是完整现场。
  it('renders the friendly title, cause code and remediation from backend fields', () => {
    const markdown = formatChatErrorDiagnostic({
      type: 'error',
      message: 'LLM 调用失败: Error while copying content to a stream.',
      causeTitle: '模型服务连接中断',
      causeShortCause: '请求体上传阶段连接被对端重置（网络或代理不稳定）。',
      causeCode: 'transport.request_upload_reset',
      causePhase: 'request_upload',
      retryable: 'true',
      remediationHint: '先确认网络/代理路径，再重试。',
      errorId: 'llm-abc',
      traceId: 'trace-1',
    });

    expect(markdown).toContain('**模型服务连接中断**');
    expect(markdown).toContain('请求体上传阶段连接被对端重置');
    expect(markdown).toContain('因果码: `transport.request_upload_reset`');
    expect(markdown).toContain('失败阶段: `request_upload`');
    expect(markdown).toContain('可重试: `是`');
    expect(markdown).toContain('处置建议: 先确认网络/代理路径，再重试。');
    // 既有可定位字段不能丢（前端按子串识别持久化诊断）。
    expect(markdown).toContain('## 请求失败');
    expect(markdown).toContain('Error ID: `llm-abc`');
  });

  it('copies the backend report verbatim when the backend provides it', () => {
    const payload = buildChatDiagnosticCopyPayload({
      event: {
        reportText: '== Pudding 错误报告（pudding.diagnostic-error/1） ==\nerrorId: llm-x',
        reportJson: '{"Schema":"pudding.diagnostic-error/1"}',
      },
      sessionId: 'session-1',
      turnId: 'turn-1',
    });

    expect(payload.text).toBe(
      '== Pudding 错误报告（pudding.diagnostic-error/1） ==\nerrorId: llm-x',
    );
    expect(payload.json).toBe('{"Schema":"pudding.diagnostic-error/1"}');
  });

  it('falls back to a locatable report when the backend report is absent', () => {
    const payload = buildChatDiagnosticCopyPayload({
      event: {
        causeTitle: '模型服务连接中断',
        causeCode: 'transport.request_upload_reset',
        causePhase: 'request_upload',
        errorId: 'llm-abc',
        traceId: 'trace-1',
        errorEvidence: '{"socket_error_code":"10054"}',
      },
      sessionId: 'session-1',
      turnId: 'turn-1',
      errorMessage: '原始异常消息',
      userAgent: 'jest',
      url: 'http://localhost/chat',
    });

    // 复制出去的必须是可定位的完整信息，而不是一行英文异常。
    expect(payload.text).toContain('因果码: transport.request_upload_reset');
    expect(payload.text).toContain('失败阶段: request_upload');
    expect(payload.text).toContain('errorId: llm-abc');
    expect(payload.text).toContain('traceId: trace-1');
    expect(payload.text).toContain('socket_error_code');
    expect(payload.text).not.toContain('undefined');

    const parsed = JSON.parse(payload.json) as Record<string, unknown>;
    expect(parsed.causeCode).toBe('transport.request_upload_reset');
    expect(parsed.traceId).toBe('trace-1');
    expect(parsed.sessionId).toBe('session-1');
    expect(parsed.errorMessage).toBe('原始异常消息');
  });
});
