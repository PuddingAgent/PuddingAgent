import {
  AUTO_CLEANUP_HINT,
  AUTO_CLEANUP_SCHEDULING_NOTE,
  AUTO_CLEANUP_TAG_COLOR,
  resolveAutoCleanupState,
  resolveEffectiveRetention,
} from './autoCleanupState';

// ── 自动清理状态推导：四个分支 + 失败关闭 + 保留生效值 ──
// 只测纯函数，不渲染页面（整页集成由浏览器 smoke 覆盖）。

describe('resolveAutoCleanupState', () => {
  it('受保护优先于其它条件，并拼接保护原因', () => {
    const state = resolveAutoCleanupState({
      protected: true,
      protectionReason: '会话事件为证据类数据',
      automaticCleanupAllowed: true,
      requiresRollupBeforeAutomatic: true,
    });

    expect(state).toEqual({
      kind: 'protected',
      label: '受保护 · 会话事件为证据类数据',
      tone: 'neutral',
    });
  });

  it('受保护但缺少保护原因时不留下悬空分隔符', () => {
    const state = resolveAutoCleanupState({ protected: true, protectionReason: '   ' });

    expect(state.kind).toBe('protected');
    expect(state.label).toBe('受保护');
  });

  it('目录未允许自动清理时显示「不自动清理」', () => {
    const state = resolveAutoCleanupState({
      protected: false,
      automaticCleanupAllowed: false,
      requiresRollupBeforeAutomatic: false,
    });

    expect(state).toEqual({ kind: 'not-allowed', label: '不自动清理', tone: 'neutral' });
  });

  it('允许自动清理但需先聚合时给出警告态', () => {
    const state = resolveAutoCleanupState({
      protected: false,
      automaticCleanupAllowed: true,
      requiresRollupBeforeAutomatic: true,
    });

    expect(state).toEqual({
      kind: 'needs-rollup',
      label: '暂不自动清理 · 需聚合先落地',
      tone: 'warning',
    });
  });

  it('允许且无需聚合时显示「可自动清理」（不是「已启用」）', () => {
    const state = resolveAutoCleanupState({
      protected: false,
      automaticCleanupAllowed: true,
      requiresRollupBeforeAutomatic: false,
    });

    expect(state).toEqual({ kind: 'available', label: '可自动清理', tone: 'positive' });
    expect(state.label).not.toContain('已启用');
  });

  it('目录缺项时失败关闭为「不自动清理」', () => {
    expect(resolveAutoCleanupState({}).kind).toBe('not-allowed');
  });

  it('每个分支都有配色与 tooltip 口径，页面级说明不写成「已启用」', () => {
    for (const kind of ['protected', 'not-allowed', 'needs-rollup', 'available'] as const) {
      expect(AUTO_CLEANUP_HINT[kind].length).toBeGreaterThan(0);
    }
    expect(AUTO_CLEANUP_TAG_COLOR.neutral).toBe('default');
    expect(AUTO_CLEANUP_TAG_COLOR.warning).toBe('warning');
    expect(AUTO_CLEANUP_TAG_COLOR.positive).toBe('success');
    expect(AUTO_CLEANUP_SCHEDULING_NOTE).toContain('需人工触发');
    expect(AUTO_CLEANUP_SCHEDULING_NOTE).not.toContain('已启用');
  });
});

describe('resolveEffectiveRetention', () => {
  it('策略值优先于目录默认值', () => {
    const retention = resolveEffectiveRetention({
      policyRetentionDays: 14,
      defaultRetentionDays: 30,
      minRetentionDays: 7,
      maxRetentionDays: 90,
    });

    expect(retention.source).toBe('policy');
    expect(retention.days).toBe(14);
    expect(retention.label).toBe('14 天');
    expect(retention.rangeLabel).toBe('可接受范围 7 ~ 90 天');
  });

  it('策略值缺失时回落目录默认值', () => {
    const retention = resolveEffectiveRetention({
      policyRetentionDays: null,
      defaultRetentionDays: 30,
      minRetentionDays: 7,
      maxRetentionDays: 90,
    });

    expect(retention.source).toBe('default');
    expect(retention.label).toBe('30 天');
  });

  it('两者都缺失时显示「未设置」', () => {
    const retention = resolveEffectiveRetention({ minRetentionDays: 7 });

    expect(retention.source).toBe('unset');
    expect(retention.days).toBeNull();
    expect(retention.label).toBe('未设置');
    expect(retention.rangeLabel).toBe('可接受下限 7 天');
  });

  it('无上下限时不产出范围说明', () => {
    expect(
      resolveEffectiveRetention({ policyRetentionDays: 7 }).rangeLabel,
    ).toBeNull();
  });
});
