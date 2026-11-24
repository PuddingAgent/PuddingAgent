// ── 运行历史纯函数测试 ──────────────────────────────────────────
// 这些函数决定「一次执行被描述成什么」，写错的代价不对称：
// 把未知状态说成「已完成」会让人以为跑完了；把未结算耗时说成「0.0s」会让人以为瞬间完成。
import {
  describeRunDuration,
  describeRunStatus,
  describeRunSummary,
  formatRunStartedAt,
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
