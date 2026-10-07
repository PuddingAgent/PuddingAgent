import type { MutableRefObject } from 'react';

/**
 * 会话事件游标的**带身份状态**（B1，2026-10-02 高磁盘读取诊断）。
 *
 * 修复前「订阅起点」只由一个数字 `lastSequenceNumRef` 表达，于是「还没同步过」与
 * 「权威游标就是 0」不可区分：已有长会话在投影路径未初始化游标时以 `afterSequence=0` 开流，
 * 触发全历史回放（诊断中 164 万事件会话 ≈ 1 GB 读取）。
 *
 * 现在游标携带**会话身份**与**阶段**：
 * - `unknown`：没有权威游标 —— 不得开流，必须先 bootstrap；
 * - `hydrating`：正在同步 —— 不得开流，也不得把数字当作权威值；
 * - `ready`：服务端快照已应用，`sequence` 是该会话的权威位置（可以是合法的 0）。
 *
 * 阶段性写入一律通过本模块的函数，保证「只有成功应用过快照的会话才会 ready」。
 */
export type SessionEventCursorPhase = 'unknown' | 'hydrating' | 'ready';

export interface SessionEventCursorState {
  sessionId: string | null;
  phase: SessionEventCursorPhase;
  sequence: number;
}

export const createSessionEventCursorState = (): SessionEventCursorState => ({
  sessionId: null,
  phase: 'unknown',
  sequence: 0,
});

/** 该会话是否已有可用的权威游标。 */
export function isCursorReadyForSession(
  state: SessionEventCursorState,
  sessionId: string,
): boolean {
  return state.sessionId === sessionId && state.phase === 'ready';
}

/**
 * 清空游标（会话切换、会话消失、整体重置）。
 * `sessionId` 给定时只清空属于该会话的状态（迟到回调不得清掉当前会话的游标）。
 */
export function invalidateSessionEventCursor(
  state: SessionEventCursorState,
  sessionId?: string | null,
): boolean {
  if (sessionId != null && state.sessionId !== sessionId) return false;
  if (
    state.sessionId === null &&
    state.phase === 'unknown' &&
    state.sequence === 0
  ) {
    return false;
  }
  state.sessionId = null;
  state.phase = 'unknown';
  state.sequence = 0;
  return true;
}

/** 开始为该会话同步权威游标：同步期间不得断言任何数字。 */
export function beginSessionEventCursorHydration(
  state: SessionEventCursorState,
  sessionId: string,
): void {
  state.sessionId = sessionId;
  state.phase = 'hydrating';
  state.sequence = 0;
}

/** 服务端快照已成功应用：此刻起该 sequence 才是权威值（0 也是合法值）。 */
export function markSessionEventCursorReady(
  state: SessionEventCursorState,
  sessionId: string,
  sequence: number,
): void {
  state.sessionId = sessionId;
  state.phase = 'ready';
  state.sequence = Number.isFinite(sequence) ? Math.max(0, sequence) : 0;
}

/**
 * 游标准备失败：回到 `unknown`（保留会话身份用于诊断），绝不假装 ready。
 * 失败不等于准备成功，调用方必须据此拒绝以 0 兜底开流。
 */
export function markSessionEventCursorFailed(
  state: SessionEventCursorState,
  sessionId: string,
): boolean {
  if (state.sessionId !== sessionId) return false;
  state.phase = 'unknown';
  state.sequence = 0;
  return true;
}

/** 实时事件推进游标：只推进同一会话，且只前进不后退。 */
export function advanceSessionEventCursor(
  state: SessionEventCursorState,
  sessionId: string,
  sequence: number,
): boolean {
  if (state.sessionId !== sessionId) return false;
  if (!Number.isFinite(sequence) || sequence <= state.sequence) return false;
  state.sequence = sequence;
  return true;
}

/** `lastSequenceNumRef` 是给历史同步/重放用的数值镜像；状态才是开流决策的权威。 */
export function syncLastSequenceMirror(
  lastSequenceNumRef: MutableRefObject<number>,
  state: SessionEventCursorState,
): void {
  lastSequenceNumRef.current = state.sequence;
}

export type SessionEventStreamStartDecision =
  | {
      kind: 'open';
      afterSequence: number;
      cursorSource: 'explicit' | 'authoritative';
    }
  | { kind: 'needs-cursor-preparation' }
  | { kind: 'refused'; reason: 'cursor-unknown-for-session' };

/**
 * 唯一的开流起点决策（纯函数）。
 *
 * - 显式游标（含 0）＝调用方声明了权威位置：只有服务端刚创建、日志为空的会话才允许这样声明；
 * - 否则必须该会话已 `ready`，使用其权威 sequence；
 * - 未就绪且正在同步 ⇒ `needs-cursor-preparation`（调用方走 `ensureSessionEventStream`）；
 * - 其余 ⇒ `refused`：**不得**用 0 兜底。
 */
export function decideSessionEventStreamStart(input: {
  state: SessionEventCursorState;
  sessionId: string;
  explicitCursor?: number;
}): SessionEventStreamStartDecision {
  const { state, sessionId, explicitCursor } = input;

  if (typeof explicitCursor === 'number') {
    return {
      kind: 'open',
      afterSequence: Math.max(0, explicitCursor),
      cursorSource: 'explicit',
    };
  }

  if (isCursorReadyForSession(state, sessionId)) {
    return {
      kind: 'open',
      afterSequence: Math.max(0, state.sequence),
      cursorSource: 'authoritative',
    };
  }

  if (state.sessionId === sessionId && state.phase === 'hydrating') {
    return { kind: 'needs-cursor-preparation' };
  }

  return { kind: 'refused', reason: 'cursor-unknown-for-session' };
}

/**
 * 游标准备结果：`ok` 为判别位，绝不把「失败」与「成功但没有 turns」混为一谈。
 */
export type SessionEventCursorPreparation =
  | { ok: true; cursor: number; turns: unknown[] }
  | {
      ok: false;
      reason: 'aborted' | 'failed' | 'stale-session' | 'invalid-cursor';
    };

/**
 * 统一开流入口的结果。
 * - `ok=false`：游标准备失败/被拒 —— 调用方不得继续以 0 兜底；
 * - `ok=true, opened=false`：无需开流（连接已健康，或游标在准备与开流之间被清掉）。
 *
 * 注意：**不存在**「因为是投影会话所以不开流」这一分支（2026-10-06 诊断修正）——
 * 投影会话的主消息由 Agent 投影负责，但子代理事实只能走 canonical 事件通道。
 */
export interface SessionEventStreamEnsureResult {
  ok: boolean;
  opened: boolean;
  reason?:
    | 'already-connected'
    | 'cursor-preparation-failed'
    | 'stale-request'
    | 'cursor-not-ready';
}
