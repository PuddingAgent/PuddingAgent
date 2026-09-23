// ── 命令 outbox 的重放策略 ──────────────────────────────────────
// 背景：flushOutbox 原先 catch { failed++ } 就把失败记录留在 IndexedDB，
// 没有错误分类、没有重试上限 ⇒ 4xx（服务端明确拒绝，如「请求体无效」）会被
// 每次开页重放一次、永远失败。用户侧表现为 console 里不停刷同一个 400。
import { isNonRetryableFailure } from './commandOutbox';

describe('isNonRetryableFailure', () => {
  it('4xx 一律不可重试', () => {
    expect(isNonRetryableFailure({ response: { status: 400 } })).toBe(true);
    expect(isNonRetryableFailure({ response: { status: 404 } })).toBe(true);
    expect(isNonRetryableFailure({ response: { status: 422 } })).toBe(true);
  });

  it('408 / 429 是例外：短时可恢复', () => {
    expect(isNonRetryableFailure({ response: { status: 408 } })).toBe(false);
    expect(isNonRetryableFailure({ response: { status: 429 } })).toBe(false);
  });

  it('5xx 可重试', () => {
    expect(isNonRetryableFailure({ response: { status: 500 } })).toBe(false);
    expect(isNonRetryableFailure({ response: { status: 503 } })).toBe(false);
  });

  it('网络类错误（无 response）可重试', () => {
    expect(isNonRetryableFailure(new Error('Network Error'))).toBe(false);
    expect(isNonRetryableFailure(null)).toBe(false);
    expect(isNonRetryableFailure(undefined)).toBe(false);
  });

  it('response 存在但 status 非数字时不误判为不可重试', () => {
    expect(isNonRetryableFailure({ response: {} })).toBe(false);
    expect(isNonRetryableFailure({ response: { status: '400' } })).toBe(false);
  });
});
