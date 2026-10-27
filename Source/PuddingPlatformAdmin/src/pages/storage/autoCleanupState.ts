import type { StorageDataClass } from './types';

// ── 自动清理状态推导（ADR-076 存储页，纯前端）─────────────────────
//
// 后端 `StorageMaintenanceCoordinator` 有 manual / automatic 双队列，
// 但 `_automaticQueue` 目前**没有任何周期性生产者**，自动清理自上线起从未触发。
// 因此本模块只回答「目录是否**允许**自动清理」，不允许表述为「已启用自动清理」；
// 页面必须同时展示 `AUTO_CLEANUP_SCHEDULING_NOTE`，说明当前清理均需人工触发。
//
// 推导全部基于既有 DTO 字段（`StorageDataClass`），不新增接口字段、不再造策略值。

/** 推导所需的最小字段集合；目录缺项时字段可缺失（按「不自动清理」失败关闭）。 */
export type AutoCleanupStateInput = Partial<
  Pick<
    StorageDataClass,
    'protected' | 'protectionReason' | 'automaticCleanupAllowed' | 'requiresRollupBeforeAutomatic'
  >
>;

/** 语义色：neutral=中性、warning=警告、positive=正常。 */
export type AutoCleanupStateTone = 'neutral' | 'warning' | 'positive';

/** 推导分支标识。供排序、统计与测试断言使用，不作为展示文案。 */
export type AutoCleanupStateKind = 'protected' | 'not-allowed' | 'needs-rollup' | 'available';

export interface AutoCleanupState {
  kind: AutoCleanupStateKind;
  /** 展示文案（简体中文，不带感叹号）。 */
  label: string;
  tone: AutoCleanupStateTone;
}

/** 语义色 → antd Tag 颜色。neutral 走默认灰，不额外强调。 */
export const AUTO_CLEANUP_TAG_COLOR: Record<AutoCleanupStateTone, string> = {
  neutral: 'default',
  warning: 'warning',
  positive: 'success',
};

/** 每个分支的口径说明（列内 tooltip）。 */
export const AUTO_CLEANUP_HINT: Record<AutoCleanupStateKind, string> = {
  protected: '证据类数据受独立保留策略保护，自动化流程不会清理。',
  'not-allowed': '目录未将该类型列入自动清理范围，只能人工触发清理。',
  'needs-rollup': '需先完成 rollup 聚合落地，之后才允许纳入自动清理。',
  available: '目录允许自动清理；但调度尚未实现，当前仍需人工触发。',
};

/** 页面级口径说明：必须与「自动清理状态」列同时呈现。 */
export const AUTO_CLEANUP_SCHEDULING_NOTE =
  '自动清理的调度尚未实现（自动队列无生产者），当前清理均需人工触发。';

/**
 * 按序推导一个数据类的自动清理状态。
 *
 * <p>分支顺序即优先级：受保护 → 不允许自动清理 → 需先聚合 → 可自动清理。
 * 目录缺项（`input` 为空对象）落到「不自动清理」，属于失败关闭而非默认放行。
 * </p>
 */
export function resolveAutoCleanupState(input: AutoCleanupStateInput): AutoCleanupState {
  const reason = input.protectionReason?.trim();

  if (input.protected === true) {
    return {
      kind: 'protected',
      label: reason ? `受保护 · ${reason}` : '受保护',
      tone: 'neutral',
    };
  }

  if (input.automaticCleanupAllowed !== true) {
    return { kind: 'not-allowed', label: '不自动清理', tone: 'neutral' };
  }

  if (input.requiresRollupBeforeAutomatic === true) {
    return { kind: 'needs-rollup', label: '暂不自动清理 · 需聚合先落地', tone: 'warning' };
  }

  return { kind: 'available', label: '可自动清理', tone: 'positive' };
}

/** 保留策略列「生效天数」的推导结果。 */
export interface EffectiveRetention {
  /** policy=取自策略值、default=回落目录默认值、unset=两者都缺失。 */
  source: 'policy' | 'default' | 'unset';
  /** 生效天数；null 表示未设置。 */
  days: number | null;
  /** 展示文案：`{n} 天` 或 `未设置`。 */
  label: string;
  /** 可接受范围说明（列内 tooltip）；上下限都缺失时为 null。 */
  rangeLabel: string | null;
}

export interface EffectiveRetentionInput {
  /** 策略为该类型设置的天数（`StorageRetentionPolicyTarget.retentionDays`）。 */
  policyRetentionDays?: number | null;
  /** 目录默认保留天数（`StorageDataClass.defaultRetentionDays`）。 */
  defaultRetentionDays?: number | null;
  minRetentionDays?: number | null;
  maxRetentionDays?: number | null;
}

function normalizeDays(value?: number | null): number | null {
  if (value === null || value === undefined) return null;
  if (!Number.isFinite(value) || value < 0) return null;
  return Math.trunc(value);
}

function formatRange(min?: number | null, max?: number | null): string | null {
  const lower = normalizeDays(min);
  const upper = normalizeDays(max);
  if (lower !== null && upper !== null) return `可接受范围 ${lower} ~ ${upper} 天`;
  if (lower !== null) return `可接受下限 ${lower} 天`;
  if (upper !== null) return `可接受上限 ${upper} 天`;
  return null;
}

/**
 * 推导保留策略列的生效天数：策略值优先，缺失时回落目录默认值，都没有则「未设置」。
 *
 * <p>只读 DTO，不构造策略值；可接受范围仅用于 tooltip 说明，不参与生效值计算。
 * </p>
 */
export function resolveEffectiveRetention(input: EffectiveRetentionInput): EffectiveRetention {
  const policyDays = normalizeDays(input.policyRetentionDays);
  const defaultDays = normalizeDays(input.defaultRetentionDays);
  const days = policyDays ?? defaultDays;

  return {
    source: policyDays !== null ? 'policy' : defaultDays !== null ? 'default' : 'unset',
    days,
    label: days === null ? '未设置' : `${days} 天`,
    rangeLabel: formatRange(input.minRetentionDays, input.maxRetentionDays),
  };
}
