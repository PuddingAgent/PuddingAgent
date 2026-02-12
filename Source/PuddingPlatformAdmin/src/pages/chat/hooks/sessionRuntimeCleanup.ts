// ── 会话运行时清理：SessionNotFoundError + 有作用域清理函数 ──────
// P0 v2: 删除/归档/404 后统一清理 session 运行时引用，防止幻影轮询和 SSE 重连。
//
// 修正（v2）：
// - 原 v1 disposeSessionRuntime 无条件清理所有 refs，会误伤非当前 session 的 SSE/active messages。
// - v2 拆为两个有作用域的函数：
//   1. disposeCurrentSessionRuntime — 仅当 sessionId 匹配当前流时清理 timer/SSE/active refs
//   2. clearDeletedSessionReferences — 只清理 projectionOwned / 间接引用，永远不碰 SSE

import type { MutableRefObject } from 'react';

/** 当 API 返回 404 或 session 被删除/归档时抛出，用于跨层传播 */
export class SessionNotFoundError extends Error {
  public readonly sessionId: string;
  constructor(sessionId: string, context?: string) {
    const msg = context
      ? `Session not found: ${sessionId} (${context})`
      : `Session not found: ${sessionId}`;
    super(msg);
    this.name = 'SessionNotFoundError';
    this.sessionId = sessionId;
  }
}

/** 判断 error 是否为 SessionNotFoundError */
export function isSessionNotFoundError(
  error: unknown,
): error is SessionNotFoundError {
  return error instanceof SessionNotFoundError;
}

/** runtime 中需要统一清理的 mutable refs */
export interface SessionRuntimeRefs {
  sessionIdRef: MutableRefObject<string | undefined>;
  sseSessionIdRef: MutableRefObject<string | null>;
  lastSequenceNumRef: MutableRefObject<number>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  projectionOwnedSessionIdsRef: MutableRefObject<Set<string>>;
  turnsRef: MutableRefObject<unknown[]>; // ChatTurn[]
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

/**
 * 有作用域清理：仅当 sessionId 匹配当前 SSE 流时才中止 SSE 和清除 active refs。
 * 如果只匹配 sessionIdRef，则只清 sessionIdRef/projection ownership，不碰 SSE/timer/active refs。
 */
export function disposeCurrentSessionRuntime(
  sessionId: string,
  refs: SessionRuntimeRefs,
  reason: string,
): boolean {
  const isCurrentStream = refs.sseSessionIdRef.current === sessionId;
  const isCurrentRef = refs.sessionIdRef.current === sessionId;

  // Only sessionIdRef matches (not the current SSE stream) → lightweight cleanup only
  if (!isCurrentStream) {
    if (isCurrentRef) refs.sessionIdRef.current = undefined;
    refs.projectionOwnedSessionIdsRef.current.delete(sessionId);
    console.debug(
      '[Pudding Chat] disposeCurrentSessionRuntime: SKIP SSE (sessionIdRef only)',
      {
        sessionId,
        reason,
        sseSessionId: refs.sseSessionIdRef.current,
        sessionIdRef: refs.sessionIdRef.current,
      },
    );
    return false;
  }

  console.debug('[Pudding Chat] disposeCurrentSessionRuntime', {
    sessionId,
    reason,
    isCurrentStream,
    isCurrentRef,
  });

  // 1. 清除所有 timer
  if (refs.sessionEventsPollTimerRef.current != null) {
    window.clearTimeout(refs.sessionEventsPollTimerRef.current);
    refs.sessionEventsPollTimerRef.current = null;
  }
  if (refs.sessionEventsReconnectTimerRef.current != null) {
    window.clearTimeout(refs.sessionEventsReconnectTimerRef.current);
    refs.sessionEventsReconnectTimerRef.current = null;
  }
  if (refs.deltaFlushTimerRef.current != null) {
    window.clearTimeout(refs.deltaFlushTimerRef.current);
    refs.deltaFlushTimerRef.current = null;
  }
  if (refs.thinkingFlushTimerRef.current != null) {
    window.clearTimeout(refs.thinkingFlushTimerRef.current);
    refs.thinkingFlushTimerRef.current = null;
  }

  // 2. Abort SSE
  if (refs.sessionEventsAbortRef.current) {
    refs.sessionEventsAbortRef.current.abort();
    refs.sessionEventsAbortRef.current = null;
  }

  // 3. 清除活跃消息状态
  refs.activeMessageIdsRef.current.clear();
  refs.pendingDeltaRef.current.clear();
  refs.pendingThinkingRef.current.clear();
  refs.duplicateDeltaReplayOffsetRef.current.clear();
  refs.eventCountsRef.current.clear();
  refs.streamStartAtRef.current.clear();

  // 4. 清除 turn/消息映射
  refs.messageIdToTurnIdRef.current.clear();
  refs.messageIdToAgentIdsRef.current.clear();
  refs.completedTurnsRef.current.clear();
  refs.latestTurnIdRef.current = null;

  // 5. 重置 sessionId 引用（仅匹配的）
  if (refs.sessionIdRef.current === sessionId) {
    refs.sessionIdRef.current = undefined;
  }
  if (refs.sseSessionIdRef.current === sessionId) {
    refs.sseSessionIdRef.current = null;
  }
  refs.lastSequenceNumRef.current = 0;
  refs.forceNewSessionRef.current = false;

  // 6. 清除 projection ownership
  refs.projectionOwnedSessionIdsRef.current.delete(sessionId);

  // 7. 清空 turns ref（避免残留引用）
  refs.turnsRef.current = [];

  return true;
}

/**
 * 无副作用引用清理：只清理 projectionOwned 等间接引用，永远不碰 SSE/active messages/timer。
 *
 * 用于删除/归档非当前 session 时的轻量清理。
 */
export function clearDeletedSessionReferences(
  sessionId: string,
  refs: SessionRuntimeRefs,
): void {
  refs.projectionOwnedSessionIdsRef.current.delete(sessionId);

  // 如果被删除的 session 恰好是 sessionIdRef（但不等于 sseSessionIdRef），只清 ref 本身
  if (refs.sessionIdRef.current === sessionId) {
    refs.sessionIdRef.current = undefined;
  }
}
