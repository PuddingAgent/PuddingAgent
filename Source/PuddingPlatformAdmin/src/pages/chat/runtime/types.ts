// ── Chat Runtime 共享类型（不依赖 React 组件） ──────────────────
// ADR-054 Step 1: Runtime Store Skeleton 基础类型定义

import type { MutableRefObject } from 'react';

// ── Runtime Store 接口 ───────────────────────────────────

/** 外部 Store 接口：subscribe + getSnapshot，兼容 useSyncExternalStore */
export interface RuntimeStore<TSnapshot> {
  subscribe(listener: () => void): () => void;
  getSnapshot(): TSnapshot;
}

// ── Session Lifecycle 类型（从 ADR-054 §7.1 移植） ──────

export type SessionRuntimeOwner = 'legacy-sse' | 'agent-projection';

export type SessionLifecyclePhase =
  | 'idle'
  | 'resolving'
  | 'active'
  | 'stale'
  | 'terminal';

export interface SessionLifecycleState {
  phase: SessionLifecyclePhase;
  workspaceId?: string;
  agentId?: string;
  sessionId?: string;
  mainSessionId?: string;
  owner?: SessionRuntimeOwner;
  terminalReason?: 'deleted' | 'archived' | 'not-found' | 'gone';
}

export type SessionLifecycleEvent =
  | { type: 'WORKSPACE_SELECTED'; workspaceId?: string }
  | { type: 'AGENT_SELECTED'; agentId?: string }
  | { type: 'MAIN_SESSION_RESOLVING'; workspaceId: string; agentId: string }
  | {
      type: 'SESSION_ACTIVE';
      sessionId: string;
      owner: SessionRuntimeOwner;
      mainSessionId?: string;
    }
  | {
      type: 'SESSION_TERMINAL';
      sessionId: string;
      reason: 'deleted' | 'archived' | 'not-found' | 'gone';
    }
  | { type: 'SESSION_CLEANED'; sessionId: string };

// ── Composer 类型（从 ADR-054 §7.3 移植） ────────────────

export interface ComposerState {
  inputValue: string;
  disabled: boolean;
  submitting: boolean;
  draftMetadata?: Record<string, string>;
}

// ── Agent Status 类型 ────────────────────────────────────

export interface AgentStatusInfo {
  agentId: string;
  isWorking: boolean;
  hasPending: boolean;
}

// ── Chat Runtime Snapshot ────────────────────────────────

/** 整个 Chat Runtime 的不可变快照 */
export interface ChatRuntimeSnapshot {
  workspaceId?: string;
  agentId?: string;
  session: SessionLifecycleState;
  composer: ComposerState;
  agentStatuses: Record<string, AgentStatusInfo>;
  updatedAt: number;
}

// ── Chat Runtime Action ──────────────────────────────────

export type ChatRuntimeAction =
  | { type: 'WORKSPACE_SET'; workspaceId?: string }
  | { type: 'AGENT_SET'; agentId?: string }
  | { type: 'COMPOSER_INPUT_SET'; value: string }
  | { type: 'COMPOSER_SUBMIT_START' }
  | { type: 'COMPOSER_SUBMIT_END' }
  | {
      type: 'AGENT_STATUS_UPDATE';
      agentId: string;
      isWorking: boolean;
      hasPending: boolean;
    }
  | { type: 'AGENT_STATUSES_CLEAR' }
  | { type: 'SESSION_EVENT'; event: SessionLifecycleEvent };

// ── 初始状态 ─────────────────────────────────────────────

export const INITIAL_SESSION_LIFECYCLE: SessionLifecycleState = {
  phase: 'idle',
};

export const INITIAL_COMPOSER: ComposerState = {
  inputValue: '',
  disabled: false,
  submitting: false,
};

export const INITIAL_CHAT_RUNTIME_SNAPSHOT: ChatRuntimeSnapshot = {
  session: INITIAL_SESSION_LIFECYCLE,
  composer: INITIAL_COMPOSER,
  agentStatuses: {},
  updatedAt: 0,
};

// ── Session Runtime Refs（从 sessionRuntimeCleanup.ts 的类型保持兼容） ──

export interface SessionRuntimeRefs {
  sessionIdRef: MutableRefObject<string | undefined>;
  sseSessionIdRef: MutableRefObject<string | null>;
  lastSequenceNumRef: MutableRefObject<number>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  projectionOwnedSessionIdsRef: MutableRefObject<Set<string>>;
  turnsRef: MutableRefObject<unknown[]>;
  completedTurnsRef: MutableRefObject<Set<string>>;
  latestTurnIdRef: MutableRefObject<string | null>;
  forceNewSessionRef: MutableRefObject<boolean>;
  sessionEventsAbortRef: MutableRefObject<AbortController | null>;
  sessionEventsPollTimerRef: MutableRefObject<number | null>;
  sessionEventsReconnectTimerRef: MutableRefObject<number | null>;
  deltaFlushTimerRef: MutableRefObject<number | null>;
  thinkingFlushTimerRef: MutableRefObject<number | null>;
  pendingDeltaRef: MutableRefObject<Map<string, string>>;
  pendingThinkingRef: MutableRefObject<Map<string, string>>;
  streamStartAtRef: MutableRefObject<Map<string, number>>;
  messageIdToAgentIdsRef: MutableRefObject<Map<string, string[]>>;
  duplicateDeltaReplayOffsetRef: MutableRefObject<Map<string, number>>;
  eventCountsRef: MutableRefObject<Map<string, number>>;
}
