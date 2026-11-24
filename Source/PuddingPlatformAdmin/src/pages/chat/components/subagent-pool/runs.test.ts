// ── 运行历史纯函数测试 ──────────────────────────────────────────
// 这些函数决定「一次执行被描述成什么」，写错的代价不对称：
// 把未知状态说成「已完成」会让人以为跑完了；把未结算耗时说成「0.0s」会让人以为瞬间完成。
import {
  describeRunDuration,
  describeRunStatus,
  describeRunSummary,
  describeSpend,
  describeSpendBrief,
  formatRunStartedAt,
  formatTokenCount,
  formatUsd,
} from './runs';

describe('describeRunStatus', () => {
  it('已知状态翻成中文', () => {
    expect(describeRunStatus('running')).toBe('运行中');
    expect(describeRunStatus('completed')).toBe('已完成');
    expect(describeRunStatus('budget_exhausted')).toBe('预算耗尽');
    expect(describeRunStatus('paused')).toBe('已暂停');
  });

  it('未知状态原样返回，不冒充「已完成」', () => {
    expect(describeRunStatus('waiting_for_human')).toBe('waiting_for_human');
  });
});

describe('describeRunDuration', () => {
  it('running 期间后端写 0，必须显示成 — 而不是 0.0s', () => {
    expect(describeRunDuration(0)).toBe('—');
    expect(describeRunDuration(null)).toBe('—');
    expect(describeRunDuration(undefined)).toBe('—');
    expect(describeRunDuration(Number.NaN)).toBe('—');
    expect(describeRunDuration(-1)).toBe('—');
  });

  it('按量级切换单位', () => {
    expect(describeRunDuration(820)).toBe('820ms');
    expect(describeRunDuration(41_200)).toBe('41.2s');
    expect(describeRunDuration(125_000)).toBe('2m05s');
  });
});

describe('formatRunStartedAt', () => {
  it('可解析时格式化为 MM-DD HH:mm', () => {
    expect(formatRunStartedAt('2026-11-24T06:33:22+08:00')).toBe('11-24 06:33');
  });

  it('不可解析或为空时退回原串 / 破折号，不显示空白', () => {
    expect(formatRunStartedAt('not-a-date')).toBe('not-a-date');
    expect(formatRunStartedAt(null)).toBe('—');
    expect(formatRunStartedAt('')).toBe('—');
  });
});

describe('describeRunSummary', () => {
  it('一行内含时刻、状态、轮次、工具数、耗时', () => {
    const summary = describeRunSummary({
      startedAt: '2026-11-24T06:33:22+08:00',
      status: 'completed',
      totalRounds: 8,
      totalToolCalls: 23,
      totalDurationMs: 41_200,
    });

    expect(summary).toBe('11-24 06:33 · 已完成 · 8 轮 · 23 次工具 · 41.2s');
  });

  it('运行中且耗时未结算时，耗时位显示破折号而不是 0.0s', () => {
    const summary = describeRunSummary({
      startedAt: '2026-11-24T06:33:22+08:00',
      status: 'running',
      totalRounds: 3,
      totalToolCalls: 11,
      totalDurationMs: 0,
    });

    expect(summary).toContain('运行中');
    expect(summary).toContain('—');
    expect(summary).not.toContain('0.0s');
  });
});

describe('formatUsd', () => {
  // 为什么不能统一 toFixed(2)：子代理一次调用实测低至 $0.0033，
  // 两位小数会把它显示成 $0.00 —— 把「花了一点」说成「没花」。
  it('小额保留足够有效位，不归零成 $0.00', () => {
    expect(formatUsd(0.0033)).toBe('$0.0033');
    expect(formatUsd(0.152166)).toBe('$0.152');
    expect(formatUsd(0.0033)).not.toBe('$0.00');
  });

  it('按量级切换精度', () => {
    expect(formatUsd(1.555588)).toBe('$1.56');
    expect(formatUsd(12.3)).toBe('$12.30');
    expect(formatUsd(0)).toBe('$0');
  });

  it('低于万分之一用不等式表达，而不是默默归零', () => {
    expect(formatUsd(0.00001)).toBe('<$0.0001');
  });

  it('非法输入返回破折号', () => {
    expect(formatUsd(Number.NaN)).toBe('—');
    expect(formatUsd(-1)).toBe('—');
  });
});

describe('formatTokenCount', () => {
  it('按量级缩写', () => {
    expect(formatTokenCount(17_128_349)).toBe('17.13M');
    expect(formatTokenCount(291_706)).toBe('291.7K');
    expect(formatTokenCount(999)).toBe('999');
    expect(formatTokenCount(2_500_000_000)).toBe('2.50B');
  });
});

describe('describeSpend', () => {
  it('无账本记录时返回 null（不渲染），而不是 $0', () => {
    expect(describeSpend(null)).toBeNull();
    expect(describeSpend(undefined)).toBeNull();
  });

  it('已计价 + 未计价：给出金额同时列出未计价次数', () => {
    const summary = describeSpend({
      totalTokens: 17_128_349,
      pricedCostUsd: 1.555588,
      pricedCalls: 125,
      unpricedCalls: 3,
    });

    expect(summary?.amount).toBe('$1.56');
    expect(summary?.detail).toBe('17.13M tokens · 128 次调用');
    expect(summary?.warning).toContain('3 次没有价格');
  });

  it('全部未计价：说「全部未计价」而不是 $0', () => {
    const summary = describeSpend({
      totalTokens: 49_000_000,
      pricedCostUsd: 0,
      pricedCalls: 0,
      unpricedCalls: 5,
    });

    expect(summary?.amount).toBe('全部未计价');
    expect(summary?.amount).not.toBe('$0');
    expect(summary?.warning).toBeNull();
  });

  it('有记录但无任何可计量用量：与「缺价」区分开', () => {
    const summary = describeSpend({
      totalTokens: 0,
      pricedCostUsd: 0,
      pricedCalls: 0,
      unpricedCalls: 0,
    });

    expect(summary?.amount).toBe('无可计量用量');
    expect(summary?.warning).toBeNull();
  });

  it('仅已计价时不给未计价告警', () => {
    const summary = describeSpend({
      totalTokens: 1_000,
      pricedCostUsd: 0.5,
      pricedCalls: 2,
      unpricedCalls: 0,
    });

    expect(summary?.amount).toBe('$0.500');
    expect(summary?.warning).toBeNull();
  });
});

describe('describeSpendBrief', () => {
  it('无账本时返回 null（不渲染），不给 $0', () => {
    expect(describeSpendBrief(null)).toBeNull();
    expect(describeSpendBrief(undefined)).toBeNull();
  });

  it('全部已计价：原样给金额，不加 +', () => {
    expect(
      describeSpendBrief({
        totalTokens: 17_128_349,
        pricedCostUsd: 1.555588,
        pricedCalls: 128,
        unpricedCalls: 0,
      }),
    ).toBe('$1.56');
  });

  // 有未计价调用时金额只是**下界**：`pricedCostUsd` 不包含那些行。
  // 不带 `+` 就是在断言「恰好花了这么多」，而那个断言是错的。
  it('有未计价调用：金额加 + 表示下界', () => {
    expect(
      describeSpendBrief({
        totalTokens: 17_128_349,
        pricedCostUsd: 1.555588,
        pricedCalls: 125,
        unpricedCalls: 3,
      }),
    ).toBe('$1.56+');
  });

  it('已经是下界的不等式不再叠加 +', () => {
    expect(
      describeSpendBrief({
        totalTokens: 900,
        pricedCostUsd: 0.00001,
        pricedCalls: 1,
        unpricedCalls: 2,
      }),
    ).toBe('<$0.0001');
  });

  it('全部未计价 / 无用量：保留短语，不拼成金额', () => {
    expect(
      describeSpendBrief({
        totalTokens: 49_000_000,
        pricedCostUsd: 0,
        pricedCalls: 0,
        unpricedCalls: 5,
      }),
    ).toBe('全部未计价');

    expect(
      describeSpendBrief({
        totalTokens: 0,
        pricedCostUsd: 0,
        pricedCalls: 0,
        unpricedCalls: 0,
      }),
    ).toBe('无可计量用量');
  });
});
