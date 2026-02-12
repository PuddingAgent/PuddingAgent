// ── Agent Status Store ─────────────────────────────────────────
// ADR-054 Step 7: 管理 agent working status、pending 状态。
// 从 index.tsx 的 polling 逻辑中迁出，对 UI 只暴露稳定的 ViewModel。

import type { AgentStatusInfo, RuntimeStore } from './types';

// ── Store 类型 ───────────────────────────────────────────

export interface AgentStatusState {
  statuses: Record<string, AgentStatusInfo>;
  updatedAt: number;
}

export interface AgentStatusStoreActions {
  updateStatus(agentId: string, isWorking: boolean, hasPending: boolean): void;
  setStatuses(statuses: Record<string, AgentStatusInfo>): void;
  clearStatus(agentId: string): void;
  clearAll(): void;
}

export type AgentStatusStore = RuntimeStore<AgentStatusState> &
  AgentStatusStoreActions;

// ── Store 创建 ───────────────────────────────────────────

export function createAgentStatusStore(
  initial?: Partial<AgentStatusState>,
): AgentStatusStore {
  let state: AgentStatusState = {
    updatedAt: 0,
    ...initial,
    statuses: initial?.statuses ?? {},
  };
  const listeners = new Set<() => void>();

  function notify(): void {
    for (const l of listeners) {
      l();
    }
  }

  function setState(partial: Partial<AgentStatusState>): void {
    state = { ...state, ...partial, updatedAt: Date.now() };
    notify();
  }

  function subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }

  function getSnapshot(): AgentStatusState {
    return state;
  }

  function updateStatus(
    agentId: string,
    isWorking: boolean,
    hasPending: boolean,
  ): void {
    const current = state.statuses[agentId];
    if (
      current &&
      current.isWorking === isWorking &&
      current.hasPending === hasPending
    ) {
      return; // 无变化，不通知
    }
    setState({
      statuses: {
        ...state.statuses,
        [agentId]: { agentId, isWorking, hasPending },
      },
    });
  }

  function setStatuses(statuses: Record<string, AgentStatusInfo>): void {
    setState({ statuses: { ...statuses } });
  }

  function clearStatus(agentId: string): void {
    if (!state.statuses[agentId]) return;
    const next = { ...state.statuses };
    delete next[agentId];
    setState({ statuses: next });
  }

  function clearAll(): void {
    setState({ statuses: {} });
  }

  return {
    subscribe,
    getSnapshot,
    updateStatus,
    setStatuses,
    clearStatus,
    clearAll,
  };
}
