import { errorConfig } from './requestErrorConfig';

const mockMessageError = jest.fn();

jest.mock('antd', () => ({
  message: {
    error: (...args: unknown[]) => mockMessageError(...args),
    warning: jest.fn(),
  },
  notification: {
    open: jest.fn(),
  },
}));

const handleError = (error: unknown, opts?: Record<string, unknown>) => {
  const handler = errorConfig.errorConfig?.errorHandler as (
    error: unknown,
    opts?: unknown,
  ) => void;
  handler(error, opts);
};

describe('request error handling', () => {
  beforeEach(() => {
    mockMessageError.mockReset();
  });

  it('does not show toast noise for expected aborted requests', () => {
    handleError({
      name: 'AbortError',
      message: 'signal is aborted without reason',
      response: {
        status: 0,
        config: { method: 'get', url: '/api/sessions/session-a/events/stream' },
      },
    });

    expect(mockMessageError).not.toHaveBeenCalled();
  });

  it('does not render unknown request metadata as question marks for status zero errors', () => {
    handleError({
      response: {
        status: 0,
        data: undefined,
      },
    });

    expect(mockMessageError).toHaveBeenCalledWith(
      '网络请求失败，请检查连接或刷新页面',
    );
  });

  it('keeps real backend HTTP errors actionable', () => {
    handleError({
      response: {
        status: 500,
        config: { method: 'post', url: '/api/chat' },
        data: { title: 'LLM 调用失败' },
      },
    });

    expect(mockMessageError).toHaveBeenCalledWith(
      'POST /api/chat — HTTP 500 — LLM 调用失败',
    );
  });

  it('does not toast when the call site opts out via skipErrorHandler', () => {
    // 已知的暂时不可用（如 context-health 的 409 context_window_unresolved）由调用方
    // 自己降级成 unknown；全局 handler 不得再弹一次错误 toast。
    expect(() =>
      handleError(
        {
          response: {
            status: 409,
            config: { method: 'get', url: '/api/sessions/legacy-session/context-health' },
            data: {
              code: 'context_window_unresolved',
              message: '无法解析当前 Session 的上下文窗口。',
            },
          },
        },
        { skipErrorHandler: true },
      ),
    ).toThrow();
    expect(mockMessageError).not.toHaveBeenCalled();
  });
});
