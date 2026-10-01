import {
  EMPTY_TEXT,
  UNKNOWN_TEXT,
  formatJobsReason,
  formatTriStateBoolean,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateDurationMs,
  formatTriStateText,
  formatTriStateTimestamp,
} from './api';

// ── Slice B 定向测试：三态显示纪律（D3 / R4）─────────────────────────
// 核心命题：后端用 `null` 表达「未知」，与 `false` / `0` 是**不同事实**。
// 这些断言就是「未知 ≠ 否 ≠ 0」的**可失败**证明：
// 任何把 null 折叠成否/零/「—」的改动都会让下面的用例变红。
// 页面渲染由浏览器 smoke 覆盖（jsdom 下 antd Table 测量循环不稳定，不作页面级断言）。

describe('formatTriStateBoolean（null ⇒ 未知 / false ⇒ 否 / true ⇒ 是）', () => {
  it('null 与 undefined 都渲染为「未知」，绝不渲染成「否」', () => {
    expect(formatTriStateBoolean(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateBoolean(undefined)).toBe(UNKNOWN_TEXT);
  });

  it('false 才是「否」，true 是「是」', () => {
    expect(formatTriStateBoolean(false)).toBe('否');
    expect(formatTriStateBoolean(true)).toBe('是');
  });

  it('三态两两不同：未知 ≠ 否（防折叠回归）', () => {
    expect(formatTriStateBoolean(null)).not.toBe(formatTriStateBoolean(false));
    expect(formatTriStateBoolean(null)).not.toBe(formatTriStateBoolean(true));
    expect(formatTriStateBoolean(false)).not.toBe(formatTriStateBoolean(true));
  });
});

describe('formatTriStateCount（null ⇒ 未知 / 0 ⇒ "0"）', () => {
  it('null 渲染「未知」，而不是 0', () => {
    expect(formatTriStateCount(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateCount(undefined)).toBe(UNKNOWN_TEXT);
  });

  it('真实的 0 必须原样显示为 "0"（不得折叠成未知/「—」）', () => {
    expect(formatTriStateCount(0)).toBe('0');
    expect(formatTriStateCount(12)).toBe('12');
    expect(formatTriStateCount(0)).not.toBe(formatTriStateCount(null));
  });
});

describe('formatTriStateBytes（null ⇒ 未知 / 0 ⇒ "0 B"）', () => {
  it('null 渲染「未知」（不是 0 B）', () => {
    expect(formatTriStateBytes(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateBytes(undefined)).toBe(UNKNOWN_TEXT);
  });

  it('真实的 0 显示为 "0 B"', () => {
    expect(formatTriStateBytes(0)).toBe('0 B');
    expect(formatTriStateBytes(0)).not.toBe(formatTriStateBytes(null));
  });

  it('按 1024 进制给人类可读值', () => {
    expect(formatTriStateBytes(512)).toBe('512 B');
    expect(formatTriStateBytes(1024)).toBe('1.0 KB');
    expect(formatTriStateBytes(800_000_000)).toBe('763 MB');
    expect(formatTriStateBytes(2_100_000_000)).toBe('2.0 GB');
  });
});

describe('formatTriStateTimestamp', () => {
  it('null / 空串 / 不可解析字符串一律「未知」，不伪造时刻', () => {
    expect(formatTriStateTimestamp(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateTimestamp(undefined)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateTimestamp('')).toBe(UNKNOWN_TEXT);
    expect(formatTriStateTimestamp('not-a-date')).toBe(UNKNOWN_TEXT);
  });

  it('可解析的 UTC ISO 串给出本地时间文本', () => {
    expect(formatTriStateTimestamp('2026-10-01T03:41:06Z')).toContain('2026');
  });
});

describe('formatTriStateDurationMs', () => {
  it('null（非终态）⇒ 未知；0 ⇒ "0 ms"', () => {
    expect(formatTriStateDurationMs(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateDurationMs(undefined)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateDurationMs(0)).toBe('0 ms');
  });

  it('毫秒/秒/分钟分档', () => {
    expect(formatTriStateDurationMs(420)).toBe('420 ms');
    expect(formatTriStateDurationMs(12_500)).toBe('12.5 s');
    expect(formatTriStateDurationMs(71_000)).toBe('1.2 min');
  });
});

describe('formatTriStateText', () => {
  it('null ⇒ 未知；空串 ⇒ 「（空）」而不是未知（有值但为空 ≠ 没有值）', () => {
    expect(formatTriStateText(null)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateText(undefined)).toBe(UNKNOWN_TEXT);
    expect(formatTriStateText('')).toBe(EMPTY_TEXT);
    expect(formatTriStateText('')).not.toBe(UNKNOWN_TEXT);
    expect(formatTriStateText('ok')).toBe('ok');
  });
});

describe('formatJobsReason（台账为空时必须给中文解释）', () => {
  it('三个已登记原因码都有中文解释', () => {
    expect(formatJobsReason('composition-not-created')).toContain('从未被构造');
    expect(formatJobsReason('no-jobs-recorded')).toContain('台账');
    expect(formatJobsReason('ledger-read-failed')).toContain('不可知');
  });

  it('null 不静默：明确报「未提供原因码」', () => {
    expect(formatJobsReason(null)).toContain('未提供台账原因码');
    expect(formatJobsReason(undefined)).toContain('未提供台账原因码');
  });

  it('未登记的原因码原样回显并标注未登记（不吞不猜）', () => {
    const text = formatJobsReason('brand-new-reason');
    expect(text).toContain('brand-new-reason');
    expect(text).toContain('未登记');
  });
});
