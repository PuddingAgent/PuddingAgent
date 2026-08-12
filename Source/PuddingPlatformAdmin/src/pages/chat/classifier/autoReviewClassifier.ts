// ── P2#9 Auto-review classifier（前端状态机）────────────────────
// 对齐 Claude Code auto mode / Cursor Auto-review / Codex auto_review：
// - auto 模式下，高风险动作由 classifier 预审；blocked 计数按规则累计。
// - 连续 blocked 3 次（AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD）或
//   累计 blocked 20 次（AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD）后，UI 自动回退到手动审批。
// - Recently denied 面板维护最近被拒工具调用列表，支持重试（retry）。
// 本模块为纯函数 + 常量 + 类型，不依赖 React，可独立单测。
import type { ApprovalCardData } from '../client/types';

/** 连续 blocked 阈值：达到后回退手动审批 */
export const AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD = 3;
/** 累计 blocked 阈值：达到后回退手动审批 */
export const AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD = 20;
/** Recently denied 面板最大保留条目 */
export const RECENTLY_DENIED_MAX_ITEMS = 20;

/** classifier 拦截规则分类（对齐业界 blocked 规则清单子集） */
export type AutoReviewBlockRule =
  | 'curl|bash'
  | 'sensitive_exfiltration'
  | 'production_deploy'
  | 'force_push'
  | 'terraform_destroy'
  | 'credential_probing'
  | 'destructive'
  | 'data_exfiltration'
  | 'persistent_security_weakening'
  | 'unknown';

export const AUTO_REVIEW_BLOCK_RULE_LABELS: Record<
  AutoReviewBlockRule,
  string
> = {
  'curl|bash': '高危命令',
  sensitive_exfiltration: '敏感外发',
  production_deploy: '生产部署',
  force_push: '强制推送',
  terraform_destroy: '破坏性 IaC',
  credential_probing: '凭据探测',
  destructive: '破坏性操作',
  data_exfiltration: '数据外发',
  persistent_security_weakening: '削弱安全',
  unknown: '未知风险',
};

/** 一次 classifier 拦截记录（用于累计 blocked 计数） */
export interface AutoReviewBlockRecord {
  id: string;
  toolName: string;
  description: string;
  riskLevel: ApprovalCardData['riskLevel'];
  rule: AutoReviewBlockRule;
  blockedAt: number;
  /** 前端记录来源：classifier（自动拦截）或 user_deny（用户手动拒绝） */
  source: 'classifier' | 'user_deny';
}

/** Recently denied 面板条目 = 拦截记录（含重试状态） */
export type RecentlyDeniedItem = AutoReviewBlockRecord;

export type AutoReviewFallbackReason = 'consecutive' | 'total';

/** classifier 状态机快照 */
export interface AutoReviewClassifierState {
  /** 是否处于 auto-review 生效的 auto 模式 */
  enabled: boolean;
  /** 当前连续 blocked 次数 */
  consecutiveBlocks: number;
  /** 累计 blocked 次数 */
  totalBlocks: number;
  /** 是否已触发回退手动审批 */
  fallbackTriggered: boolean;
  /** 回退触发原因（consecutive / total） */
  fallbackReason: AutoReviewFallbackReason | null;
  /** 最近一次 blocked 时间戳 */
  lastBlockedAt: number | null;
  /** 最近一次拦截规则 */
  lastBlockRule: AutoReviewBlockRule | null;
}

/** 初始 classifier 状态 */
export const createInitialAutoReviewState = (): AutoReviewClassifierState => ({
  enabled: true,
  consecutiveBlocks: 0,
  totalBlocks: 0,
  fallbackTriggered: false,
  fallbackReason: null,
  lastBlockedAt: null,
  lastBlockRule: null,
});

/** 判断当前 blocked 计数是否已达到回退阈值（不修改状态） */
export const shouldFallbackToManual = (
  state: Pick<AutoReviewClassifierState, 'consecutiveBlocks' | 'totalBlocks'>,
): AutoReviewFallbackReason | null => {
  if (state.consecutiveBlocks >= AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD) {
    return 'consecutive';
  }
  if (state.totalBlocks >= AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD) {
    return 'total';
  }
  return null;
};

/** 应用一次 classifier 拦截；达到阈值时置 fallbackTriggered。 */
export const applyAutoReviewBlock = (
  state: AutoReviewClassifierState,
  record: Pick<
    AutoReviewBlockRecord,
    'toolName' | 'description' | 'riskLevel' | 'rule' | 'source'
  > & { blockedAt?: number },
): AutoReviewClassifierState => {
  const next: AutoReviewClassifierState = {
    ...state,
    consecutiveBlocks: state.consecutiveBlocks + 1,
    totalBlocks: state.totalBlocks + 1,
    lastBlockedAt: record.blockedAt ?? Date.now(),
    lastBlockRule: record.rule,
  };
  const reason = shouldFallbackToManual(next);
  if (reason) {
    next.fallbackTriggered = true;
    next.fallbackReason = reason;
  }
  return next;
};

/**
 * 回退后用户重新恢复 auto 模式时调用：
 * 清空连续 blocked 计数并解除回退（Recently denied 列表保留，供用户重试）。
 * 累计 totalBlocks 保留——"累计 20 次" 是会话级累计，恢复 auto 不清零。
 */
export const resetAutoReviewState = (
  state: AutoReviewClassifierState,
): AutoReviewClassifierState => ({
  ...state,
  enabled: true,
  consecutiveBlocks: 0,
  fallbackTriggered: false,
  fallbackReason: null,
  lastBlockedAt: null,
  lastBlockRule: null,
});

/** 用户切出 auto 模式时调用：禁用 classifier 但保留计数现场。 */
export const disableAutoReview = (
  state: AutoReviewClassifierState,
): AutoReviewClassifierState => ({
  ...state,
  enabled: false,
});

/** 添加 Recently denied 条目（头部插入，按 id 去重，保留最近 N 条） */
export const addRecentlyDeniedItem = (
  list: RecentlyDeniedItem[],
  item: RecentlyDeniedItem,
  maxItems: number = RECENTLY_DENIED_MAX_ITEMS,
): RecentlyDeniedItem[] => {
  const deduped = list.filter((entry) => entry.id !== item.id);
  return [item, ...deduped].slice(0, Math.max(1, maxItems));
};

/** 移除指定 Recently denied 条目 */
export const removeRecentlyDeniedItem = (
  list: RecentlyDeniedItem[],
  id: string,
): RecentlyDeniedItem[] => list.filter((entry) => entry.id !== id);

/** 重试：从列表中移除并返回被重试的条目（由调用方决定如何重新发起） */
export const retryRecentlyDeniedItem = (
  list: RecentlyDeniedItem[],
  id: string,
): { item: RecentlyDeniedItem | undefined; list: RecentlyDeniedItem[] } => {
  const item = list.find((entry) => entry.id === id);
  return { item, list: removeRecentlyDeniedItem(list, id) };
};

/** 生成稳定 id（测试环境优先自增，避免依赖 crypto） */
let _seq = 0;
export const createBlockRecordId = (): string => {
  _seq += 1;
  const rand =
    typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID().slice(0, 8)
      : `${Date.now().toString(36)}${_seq}`;
  return `ar-${rand}-${_seq}`;
};

/** 便捷构造 RecentlyDeniedItem */
export const toRecentlyDeniedItem = (
  input: Omit<AutoReviewBlockRecord, 'id' | 'blockedAt' | 'source'> & {
    id?: string;
    blockedAt?: number;
    source?: AutoReviewBlockRecord['source'];
  },
): RecentlyDeniedItem => ({
  id: input.id ?? createBlockRecordId(),
  toolName: input.toolName,
  description: input.description,
  riskLevel: input.riskLevel,
  rule: input.rule,
  blockedAt: input.blockedAt ?? Date.now(),
  source: input.source ?? 'classifier',
});
