// ── P2#9 useAutoReviewClassifier：Auto-review classifier 状态 hook ──
// 职责：
// - 持有 classifier 状态（连续/累计 blocked 计数、回退手动审批标记），localStorage 持久化；
// - 持有 Recently denied 面板条目列表，localStorage 持久化；
// - 暴露记录拦截、用户拒绝、重试、移除、恢复 auto 等操作。
// 纯逻辑位于 ../classifier/autoReviewClassifier.ts，本 hook 只做状态与副作用编排。
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ApprovalCardData } from '../client/types';
import {
  addRecentlyDeniedItem,
  applyAutoReviewBlock,
  AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
  AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD,
  createInitialAutoReviewState,
  disableAutoReview,
  removeRecentlyDeniedItem,
  resetAutoReviewState,
  toRecentlyDeniedItem,
  type AutoReviewBlockRule,
  type AutoReviewClassifierState,
  type RecentlyDeniedItem,
} from '../classifier/autoReviewClassifier';

const CLASSIFIER_STORAGE_KEY = 'pudding-chat-auto-review-classifier';
const RECENTLY_DENIED_STORAGE_KEY = 'pudding-chat-recently-denied';

export interface UseAutoReviewClassifierOptions {
  /** 默认是否启用（auto 模式下为 true） */
  enabled?: boolean;
  /** 触发回退手动审批时的回调（父组件通常借此把权限模式切回 manual） */
  onFallbackToManual?: (
    reason: 'consecutive' | 'total',
    state: AutoReviewClassifierState,
  ) => void;
}

export interface UseAutoReviewClassifierReturn {
  state: AutoReviewClassifierState;
  recentlyDenied: RecentlyDeniedItem[];
  /** 记录一次 classifier 拦截（auto 模式 blocked）；达到阈值触发回退回调。 */
  recordClassifierBlock: (
    input: Omit<RecentlyDeniedItem, 'id' | 'blockedAt' | 'source'>,
  ) => void;
  /** 记录一次用户手动拒绝（进入 Recently denied，同时计入 blocked 计数）。 */
  recordUserDeny: (
    input: Omit<RecentlyDeniedItem, 'id' | 'blockedAt' | 'source'>,
  ) => void;
  /** 重试 Recently denied 条目；返回被重试的条目并移除出列表。 */
  retryDenied: (id: string) => RecentlyDeniedItem | undefined;
  /** 移除 Recently denied 条目 */
  removeDenied: (id: string) => void;
  /** 清空 Recently denied 列表 */
  clearDenied: () => void;
  /** 用户恢复 auto 模式：清空 blocked 计数并解除回退。 */
  resetToAuto: () => void;
  /** 用户切出 auto 模式：禁用 classifier 但保留计数现场。 */
  setEnabled: (enabled: boolean) => void;
  /** 从审批卡片决策投影出 Recently denied 条目（ApprovalCardData → 条目） */
  denyFromApproval: (card: ApprovalCardData) => void;
}

const safeParse = <T,>(raw: string | null, fallback: T): T => {
  if (!raw) return fallback;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
};

export const useAutoReviewClassifier = (
  options: UseAutoReviewClassifierOptions = {},
): UseAutoReviewClassifierReturn => {
  const { enabled = true, onFallbackToManual } = options;
  const onFallbackRef = useRef(onFallbackToManual);
  onFallbackRef.current = onFallbackToManual;

  const [state, setState] = useState<AutoReviewClassifierState>(() => {
    const initial = createInitialAutoReviewState();
    const persisted = safeParse<AutoReviewClassifierState | null>(
      typeof window !== 'undefined'
        ? window.localStorage.getItem(CLASSIFIER_STORAGE_KEY)
        : null,
      null,
    );
    return persisted ? { ...initial, ...persisted, enabled } : { ...initial, enabled };
  });

  const [recentlyDenied, setRecentlyDenied] = useState<RecentlyDeniedItem[]>(
    () =>
      safeParse<RecentlyDeniedItem[] | null>(
        typeof window !== 'undefined'
          ? window.localStorage.getItem(RECENTLY_DENIED_STORAGE_KEY)
          : null,
        null,
      ) ?? [],
  );
  // 镜像 ref：retryDenied 需要在副作用外同步读取当前列表
  const recentlyDeniedRef = useRef(recentlyDenied);
  recentlyDeniedRef.current = recentlyDenied;

  // 持久化
  useEffect(() => {
    try {
      window.localStorage.setItem(CLASSIFIER_STORAGE_KEY, JSON.stringify(state));
    } catch {
      // 忽略存储异常（隐私模式/配额）
    }
  }, [state]);

  useEffect(() => {
    try {
      window.localStorage.setItem(
        RECENTLY_DENIED_STORAGE_KEY,
        JSON.stringify(recentlyDenied),
      );
    } catch {
      // 忽略存储异常
    }
  }, [recentlyDenied]);

  // 回退触发回调
  const fallbackFiredRef = useRef(false);
  useEffect(() => {
    if (state.fallbackTriggered && !fallbackFiredRef.current) {
      fallbackFiredRef.current = true;
      onFallbackRef.current?.(
        state.fallbackReason ?? 'consecutive',
        state,
      );
    }
  }, [state]);

  const recordBlock = useCallback(
    (
      input: Omit<RecentlyDeniedItem, 'id' | 'blockedAt' | 'source'>,
      source: RecentlyDeniedItem['source'],
    ) => {
      const item = toRecentlyDeniedItem({ ...input, source });
      setRecentlyDenied((list) => addRecentlyDeniedItem(list, item));
      setState((prev) => {
        if (!prev.enabled) return prev;
        return applyAutoReviewBlock(prev, { ...item, source });
      });
    },
    [],
  );

  const recordClassifierBlock = useCallback(
    (input: Omit<RecentlyDeniedItem, 'id' | 'blockedAt' | 'source'>) => {
      recordBlock(input, 'classifier');
    },
    [recordBlock],
  );

  const recordUserDeny = useCallback(
    (input: Omit<RecentlyDeniedItem, 'id' | 'blockedAt' | 'source'>) => {
      recordBlock(input, 'user_deny');
    },
    [recordBlock],
  );

  const retryDenied = useCallback((id: string) => {
    const current = recentlyDeniedRef.current;
    const item = current.find((entry) => entry.id === id);
    if (item) {
      setRecentlyDenied(removeRecentlyDeniedItem(current, id));
    }
    return item;
  }, []);

  const removeDenied = useCallback((id: string) => {
    setRecentlyDenied((list) => removeRecentlyDeniedItem(list, id));
  }, []);

  const clearDenied = useCallback(() => {
    setRecentlyDenied([]);
  }, []);

  const resetToAuto = useCallback(() => {
    fallbackFiredRef.current = false;
    setState((prev) => ({ ...resetAutoReviewState(prev), enabled: true }));
  }, []);

  const setEnabled = useCallback((nextEnabled: boolean) => {
    setState((prev) =>
      nextEnabled
        ? { ...prev, enabled: true }
        : disableAutoReview(prev),
    );
  }, []);

  const denyFromApproval = useCallback(
    (card: ApprovalCardData) => {
      const rule: AutoReviewBlockRule =
        card.riskLevel === 'critical' || card.riskLevel === 'high'
          ? 'destructive'
          : 'unknown';
      recordUserDeny({
        toolName: card.toolName,
        description: card.description,
        riskLevel: card.riskLevel,
        rule,
      });
    },
    [recordUserDeny],
  );

  return {
    state,
    recentlyDenied,
    recordClassifierBlock,
    recordUserDeny,
    retryDenied,
    removeDenied,
    clearDenied,
    resetToAuto,
    setEnabled,
    denyFromApproval,
  };
};

/** 供 indicator 展示的阈值常量导出（避免组件直接 import classifier 内部常量） */
export const AUTO_REVIEW_THRESHOLDS = {
  consecutive: AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
  total: AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD,
} as const;
