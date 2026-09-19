// ── P1: Connection Manager ────────────────────────────────────
// 管理 SSE 连接生命周期：连接、重连、退避、generation token。
// 一次只有一个活跃连接。旧连接延迟到达的事件会被丢弃。
// ───────────────────────────────────────────────────────────────

import { createSseClient, type SseClientHandle } from '../transport/sseClient';
import type { AdminChatStreamEvent } from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/perfEventRuntime';

export interface ConnectionState {
  connected: boolean;
  generation: number;
  sessionId: string | null;
  cursor: number;
  reconnectCount: number;
  lastError: string | null;
}

export interface ConnectionCallbacks {
  onEvent: (event: AdminChatStreamEvent, sequenceNum?: number) => void;
  onStateChange: (state: ConnectionState) => void;
  /**
   * 服务端要求重取快照（410 `snapshot_required`）：游标之后的事件已不可读。
   * 与 frozen / not-found 不同，这是**可恢复**的：实现方应重取 `/bootstrap`、
   * 把游标推进到快照位置后重新 `connect()`。
   * 未提供时不做自动重试（同游标重连必然再次 410，会变成无界重试循环），
   * 但会留下 `chat.connection.snapshotRequired` 性能事件以便定位。
   */
  onSnapshotRequired?: (sessionId: string) => void;
}

const MAX_RECONNECT_DELAY_MS = 30_000;
const BASE_RECONNECT_DELAY_MS = 1_000;

export function createConnectionManager(
  callbacks: ConnectionCallbacks,
): {
  connect: (sessionId: string, afterSequence: number) => void;
  disconnect: () => void;
  reconnect: (afterSequence?: number) => void;
  getState: () => ConnectionState;
} {
  let state: ConnectionState = {
    connected: false,
    generation: 0,
    sessionId: null,
    cursor: 0,
    reconnectCount: 0,
    lastError: null,
  };

  let currentClient: SseClientHandle | null = null;
  let reconnectTimer: number | null = null;

  function updateState(partial: Partial<ConnectionState>) {
    state = { ...state, ...partial };
    callbacks.onStateChange(state);
  }

  function clearReconnectTimer() {
    if (reconnectTimer != null) {
      window.clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
  }

  function disconnect() {
    clearReconnectTimer();
    currentClient?.abort();
    currentClient = null;
    updateState({ connected: false });
    recordPerfEvent('chat.connection.disconnected', { sessionId: state.sessionId, generation: state.generation });
  }

  function scheduleReconnect(delayMs?: number) {
    clearReconnectTimer();
    const delay = delayMs ?? Math.min(
      BASE_RECONNECT_DELAY_MS * Math.pow(2, state.reconnectCount),
      MAX_RECONNECT_DELAY_MS,
    );
    reconnectTimer = window.setTimeout(() => {
      reconnectTimer = null;
      if (state.sessionId) {
        doConnect(state.sessionId, state.cursor);
      }
    }, delay);
  }

  function connect(sessionId: string, afterSequence: number) {
    disconnect();
    state.sessionId = sessionId;
    state.cursor = afterSequence;
    state.reconnectCount = 0;
    state.generation = 0;
    doConnect(sessionId, afterSequence);
  }

  function reconnect(afterSequence?: number) {
    if (!state.sessionId) return;
    const cursor = afterSequence ?? state.cursor;
    if (cursor > state.cursor) {
      state.cursor = cursor;
    }
    doConnect(state.sessionId, state.cursor);
  }

  function doConnect(sessionId: string, afterSequence: number) {
    clearReconnectTimer();
    currentClient?.abort();
    currentClient = null;

    const gen = state.generation + 1;
    updateState({ generation: gen, connected: true, lastError: null });

    recordPerfEvent('chat.connection.connect', {
      sessionId,
      generation: gen,
      afterSequence,
      reconnectCount: state.reconnectCount,
    });

    currentClient = createSseClient({
      sessionId,
      afterSequence,
      generation: gen,
      onEvent: (event, sequenceNum) => {
        // P0: Only process events from current generation
        if (state.generation !== gen) {
          recordPerfEvent('chat.connection.staleEvent', {
            sessionId,
            currentGen: state.generation,
            eventGen: gen,
            sequenceNum,
          });
          return;
        }

        // P0: cursor advancement must happen AFTER reducer successfully commits.
        // Caller (useConversation) will update cursor via applyEvent → store → connection cursor.
        callbacks.onEvent(event, sequenceNum);
      },
      onError: (error, httpStatus, code) => {
        if (state.generation !== gen) return;

        updateState({
          connected: false,
          lastError: error.message,
          reconnectCount: state.reconnectCount + 1,
        });

        recordPerfEvent('chat.connection.error', {
          sessionId,
          generation: gen,
          error: error.message,
          httpStatus,
          code,
          reconnectCount: state.reconnectCount,
        });

        // snapshot_required 也是 410，但语义是「重取快照即可继续」，
        // 不是「会话消失」。必须优先判定，否则可恢复情况会被静默判死。
        if (code === 'snapshot_required') {
          recordPerfEvent('chat.connection.snapshotRequired', {
            sessionId,
            generation: gen,
          });
          callbacks.onSnapshotRequired?.(sessionId);
          return;
        }

        // P0: Don't reconnect on 404 (session deleted) or 410 (frozen)
        if (httpStatus === 404 || httpStatus === 410) return;

        scheduleReconnect();
      },
    });
  }

  return { connect, disconnect, reconnect, getState: () => ({ ...state }) };
}
