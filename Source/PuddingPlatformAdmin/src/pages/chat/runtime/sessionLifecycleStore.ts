// ── Session Lifecycle State Machine ────────────────────────────
// ADR-054 Step 2: 纯函数 reduceSessionLifecycle，管理 session 生命周期状态机。
// 不依赖 React、DOM、API 调用。

import type {
  SessionLifecycleEvent,
  SessionLifecycleState,
  SessionRuntimeOwner,
} from './types';
import { INITIAL_SESSION_LIFECYCLE } from './types';

// ── Reducer ──────────────────────────────────────────────

/**
 * Session Lifecycle 纯函数 reducer。
 *
 * 规则：
 * - SSE 只能由 owner==='legacy-sse' && phase==='active' 派生启动。
 * - Agent projection 拥有消息加载时，不启动 legacy SSE。
 * - 404 和 410 都派发 SESSION_TERMINAL。
 * - delete/archive 不直接清全局 refs，必须走 SESSION_TERMINAL。
 */
export function reduceSessionLifecycle(
  state: SessionLifecycleState,
  event: SessionLifecycleEvent,
): SessionLifecycleState {
  switch (event.type) {
    case 'WORKSPACE_SELECTED':
      return {
        phase: 'idle',
        workspaceId: event.workspaceId,
      };

    case 'AGENT_SELECTED':
      return {
        ...state,
        phase: state.phase === 'idle' ? 'idle' : state.phase,
        agentId: event.agentId,
      };

    case 'MAIN_SESSION_RESOLVING':
      return {
        phase: 'resolving',
        workspaceId: event.workspaceId,
        agentId: event.agentId,
      };

    case 'SESSION_ACTIVE':
      return {
        phase: 'active',
        workspaceId: state.workspaceId,
        agentId: state.agentId,
        sessionId: event.sessionId,
        owner: event.owner,
        mainSessionId: event.mainSessionId ?? state.mainSessionId,
      };

    case 'SESSION_TERMINAL': {
      // 只终止匹配的 sessionId — 不匹配则 state 不变
      if (state.sessionId !== event.sessionId) return state;

      const next: SessionLifecycleState = {
        phase: 'terminal',
        workspaceId: state.workspaceId,
        agentId: state.agentId,
        sessionId: undefined,
        terminalReason: event.reason,
      };

      // mainSessionId 也指向该 session → 一并清除
      if (state.sessionId === state.mainSessionId) {
        next.mainSessionId = undefined;
      } else {
        next.mainSessionId = state.mainSessionId;
      }

      return next;
    }

    case 'SESSION_CLEANED':
      return {
        phase: 'idle',
        workspaceId: state.workspaceId,
        agentId: state.agentId,
      };

    default:
      return state;
  }
}

// ── Helper ───────────────────────────────────────────────

/**
 * 判断给定 phase 是否允许 SSE 连接。
 * 只有 legacy-sse owner + active phase 才可启动 SSE。
 */
export function canStartLegacySse(state: SessionLifecycleState): boolean {
  return state.phase === 'active' && state.owner === 'legacy-sse';
}

/**
 * 判断给定 phase 是否允许 Agent projection 加载消息。
 */
export function canLoadAgentProjection(state: SessionLifecycleState): boolean {
  return state.phase === 'active' && state.owner === 'agent-projection';
}

/**
 * 根据 reason 字符串映射到 SESSION_TERMINAL reason。
 */
export function mapReasonToTerminalReason(
  reason: string,
): 'deleted' | 'archived' | 'not-found' | 'gone' {
  if (reason === 'delete') return 'deleted';
  if (reason === 'archive') return 'archived';
  if (reason.startsWith('sse-404') || reason === 'replay-poll-404')
    return 'not-found';
  if (reason.startsWith('sse-410')) return 'gone';
  return 'not-found';
}

/**
 * 创建 SESSION_TERMINAL 事件的工厂函数。
 * 将删除/归档/404/410 统一映射为 SESSION_TERMINAL 事件。
 */
export function createSessionTerminalEvent(
  sessionId: string,
  reason: string,
): SessionLifecycleEvent {
  return {
    type: 'SESSION_TERMINAL',
    sessionId,
    reason: mapReasonToTerminalReason(reason),
  };
}
