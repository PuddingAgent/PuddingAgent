// ── Chat Runtime Selectors ──────────────────────────────────────
// ADR-054 Step 1: 为 UI 暴露稳定、局部的 ViewModel。

import type {
  ChatRuntimeSnapshot,
  ComposerState,
  RuntimeStore,
  SessionLifecycleState,
} from './types';

// ── 基础 Selectors ───────────────────────────────────────

/** 选择 workspace + agent 组合 */
export function selectWorkspaceAgent(snapshot: ChatRuntimeSnapshot): {
  workspaceId?: string;
  agentId?: string;
} {
  return {
    workspaceId: snapshot.workspaceId,
    agentId: snapshot.agentId,
  };
}

/** 选择 session lifecycle 状态 */
export function selectSessionLifecycle(
  snapshot: ChatRuntimeSnapshot,
): SessionLifecycleState {
  return snapshot.session;
}

/** 选择 composer 状态 */
export function selectComposer(snapshot: ChatRuntimeSnapshot): ComposerState {
  return snapshot.composer;
}

/** 选择 agent statuses */
export function selectAgentStatuses(
  snapshot: ChatRuntimeSnapshot,
): Record<
  string,
  { agentId: string; isWorking: boolean; hasPending: boolean }
> {
  return snapshot.agentStatuses;
}

/** 选择特定 agent 的 working 状态 */
export function selectAgentWorking(
  snapshot: ChatRuntimeSnapshot,
  agentId: string,
): boolean {
  return snapshot.agentStatuses[agentId]?.isWorking ?? false;
}

// ── Selector Hook 辅助 ───────────────────────────────────

/**
 * 通用 selector hook 工厂。
 * 第一阶段直接内联到各 hook，不暴露通用 hook。
 * 此函数用于测试 selector 稳定性。
 */
export function applySelector<TSnapshot, TResult>(
  store: RuntimeStore<TSnapshot>,
  selector: (snapshot: TSnapshot) => TResult,
  isEqual?: (a: TResult, b: TResult) => boolean,
): {
  getSnapshot: () => TResult;
  subscribe: (listener: () => void) => () => void;
} {
  let prev: TResult | undefined;
  const defaultEqual = (a: TResult, b: TResult) => a === b;
  const eq = isEqual ?? defaultEqual;

  return {
    subscribe: store.subscribe,
    getSnapshot: () => {
      const next = selector(store.getSnapshot());
      if (prev !== undefined && eq(prev, next)) {
        return prev;
      }
      prev = next;
      return next;
    },
  };
}
