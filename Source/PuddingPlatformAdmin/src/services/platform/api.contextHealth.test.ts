// ── getContextHealth 的请求选项单测 ───────────────────────────────
// 背景（2026-10-17 诊断）：打开**早期会话**（未绑定 Agent 实例）时，context-health 返回
// 409 `context_window_unresolved`；这是已知的暂时不可用，不是服务故障。但 api 层若不带
// skipErrorHandler，umi 全局错误处理会照常弹红色 toast（「GET … — HTTP 409 — …」）。
// 本文件锁定：该请求必须 opt-out 全局 toast，错误交给调用方按服务态纪律降级。

import { request } from '@umijs/max';
import { getContextHealth } from './api';

jest.mock('@umijs/max', () => ({
  request: jest.fn(),
}));

const mockRequest = request as jest.Mock;

describe('getContextHealth request options', () => {
  beforeEach(() => {
    mockRequest.mockReset();
    mockRequest.mockResolvedValue({ sessionId: 'legacy-session' });
  });

  it('opts out of the global error toast (409 context_window_unresolved is expected)', async () => {
    await getContextHealth('legacy-session');

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/sessions/legacy-session/context-health',
      expect.objectContaining({
        method: 'GET',
        skipErrorHandler: true,
      }),
    );
  });

  it('encodes the session id in the path', async () => {
    await getContextHealth('session/with space');

    expect(mockRequest).toHaveBeenCalledWith(
      '/api/sessions/session%2Fwith%20space/context-health',
      expect.objectContaining({ skipErrorHandler: true }),
    );
  });
});
