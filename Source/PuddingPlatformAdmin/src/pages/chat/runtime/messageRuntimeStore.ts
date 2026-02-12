// ── Message Runtime Store [EXPERIMENTAL] ────────────────────────
// ADR-054 Step 1 scaffold. 尚未接入生产主链路，仅被测试引用。
// ────────────────────────────────────────────────────────────────
// 管理 turns、delta buffer、thinking buffer、active message ids、subAgentCards。
// 流式 delta 更新只改变目标 turn，其他 turn 引用保持稳定。

import type { RuntimeStore } from './types';

// ── 类型定义 ────────────────────────────────────────────

export interface SubAgentCard {
  subAgentId: string;
  parentMessageId: string;
  name: string;
  status: 'running' | 'completed' | 'failed';
  summary?: string;
  createdAt: number;
}

export interface ChatTurn {
  turnId: string;
  userMessage: {
    id: string;
    text: string;
    timestamp: number;
    status: 'sending' | 'success' | 'error';
  };
  assistant: {
    id: string;
    status:
      | 'thinking'
      | 'executing'
      | 'streaming'
      | 'success'
      | 'error'
      | 'cancelled';
    answerMarkdown: string;
    isStreaming: boolean;
    thinkingText?: string;
  };
}

export interface MessageRuntimeSnapshot {
  turns: ChatTurn[];
  subAgentCards: Record<string, SubAgentCard>;
  activeMessageIds: Set<string>;
  lastSequenceNum: number;
  updatedAt: number;
}

// ── 初始状态 ─────────────────────────────────────────────

export const INITIAL_MESSAGE_RUNTIME: MessageRuntimeSnapshot = {
  turns: [],
  subAgentCards: {},
  activeMessageIds: new Set(),
  lastSequenceNum: 0,
  updatedAt: 0,
};

// ── Store 接口 ───────────────────────────────────────────

export interface MessageRuntimeStore
  extends RuntimeStore<MessageRuntimeSnapshot> {
  /** 追加 delta 到指定 message */
  enqueueDelta(messageId: string, turnId: string, delta: string): void;
  /** flush 所有 pending delta */
  flushPendingDeltas(): Map<string, string>;
  /** 追加 thinking 文本 */
  enqueueThinking(messageId: string, turnId: string, text: string): void;
  /** flush 所有 pending thinking */
  flushPendingThinking(): Map<string, string>;
  /** 追加新 turn（如 optimistic user turn） */
  appendTurn(turn: ChatTurn): void;
  /** 更新 assistant turn 的状态/内容 */
  updateAssistantTurn(
    turnId: string,
    update: Partial<ChatTurn['assistant']>,
  ): void;
  /** 添加/更新 subAgent card */
  upsertSubAgentCard(card: SubAgentCard): void;
  /** 设置历史 turns（prepend） */
  setTurns(turns: ChatTurn[]): void;
  /** 追加历史 turns 到开头 */
  prependTurns(turns: ChatTurn[]): void;
  /** 清理所有状态 */
  clear(): void;
}

// ── Store 创建 ───────────────────────────────────────────

export function createMessageRuntimeStore(
  initial?: Partial<MessageRuntimeSnapshot>,
): MessageRuntimeStore {
  let state: MessageRuntimeSnapshot = {
    ...INITIAL_MESSAGE_RUNTIME,
    ...initial,
    activeMessageIds: initial?.activeMessageIds ?? new Set(),
    subAgentCards: initial?.subAgentCards ?? {},
  };
  const listeners = new Set<() => void>();

  // Internal: pending delta/tinking buffers
  const pendingDelta = new Map<string, string>();
  const pendingThinking = new Map<string, string>();
  let deltaFlushTimer: ReturnType<typeof setTimeout> | null = null;
  let thinkingFlushTimer: ReturnType<typeof setTimeout> | null = null;

  const FLUSH_DELAY_MS = 50;

  function notify(): void {
    for (const l of listeners) {
      l();
    }
  }

  function setState(partial: Partial<MessageRuntimeSnapshot>): void {
    state = {
      ...state,
      ...partial,
      updatedAt: Date.now(),
    };
    notify();
  }

  function subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }

  function getSnapshot(): MessageRuntimeSnapshot {
    return state;
  }

  // ── Mutation 方法 ──────────────────────────────────────

  function enqueueDelta(
    messageId: string,
    turnId: string,
    _delta: string,
  ): void {
    const existing = pendingDelta.get(messageId) ?? '';
    pendingDelta.set(messageId, existing + _delta);

    // 标记 active
    const nextActive = new Set(state.activeMessageIds);
    nextActive.add(messageId);
    setState({ activeMessageIds: nextActive });

    // schedule flush
    if (deltaFlushTimer == null) {
      deltaFlushTimer = setTimeout(() => {
        deltaFlushTimer = null;
        _flushPendingDeltas();
      }, FLUSH_DELAY_MS);
    }
  }

  function _flushPendingDeltas(): void {
    if (pendingDelta.size === 0) return;

    const nextTurns = state.turns.map((turn) => {
      const delta = pendingDelta.get(turn.assistant.id);
      if (!delta) return turn;
      return {
        ...turn,
        assistant: {
          ...turn.assistant,
          answerMarkdown: turn.assistant.answerMarkdown + delta,
        },
      };
    });

    pendingDelta.clear();
    setState({ turns: nextTurns });
  }

  function flushPendingDeltas(): Map<string, string> {
    _flushPendingDeltas();
    return new Map(); // 返回空，deltas 已应用
  }

  function enqueueThinking(
    messageId: string,
    _turnId: string,
    text: string,
  ): void {
    const existing = pendingThinking.get(messageId) ?? '';
    pendingThinking.set(messageId, existing + text);

    if (thinkingFlushTimer == null) {
      thinkingFlushTimer = setTimeout(() => {
        thinkingFlushTimer = null;
        _flushPendingThinking();
      }, FLUSH_DELAY_MS);
    }
  }

  function _flushPendingThinking(): void {
    if (pendingThinking.size === 0) return;

    const nextTurns = state.turns.map((turn) => {
      const thinking = pendingThinking.get(turn.assistant.id);
      if (!thinking) return turn;
      return {
        ...turn,
        assistant: {
          ...turn.assistant,
          thinkingText: (turn.assistant.thinkingText ?? '') + thinking,
        },
      };
    });

    pendingThinking.clear();
    setState({ turns: nextTurns });
  }

  function flushPendingThinking(): Map<string, string> {
    _flushPendingThinking();
    return new Map();
  }

  function appendTurn(turn: ChatTurn): void {
    setState({ turns: [...state.turns, turn] });
  }

  function updateAssistantTurn(
    turnId: string,
    update: Partial<ChatTurn['assistant']>,
  ): void {
    const nextTurns = state.turns.map((turn) => {
      if (turn.turnId !== turnId) return turn;
      return {
        ...turn,
        assistant: { ...turn.assistant, ...update },
      };
    });
    setState({ turns: nextTurns });
  }

  function upsertSubAgentCard(card: SubAgentCard): void {
    setState({
      subAgentCards: {
        ...state.subAgentCards,
        [card.subAgentId]: card,
      },
    });
  }

  function setTurns(turns: ChatTurn[]): void {
    setState({ turns: [...turns] });
  }

  function prependTurns(turns: ChatTurn[]): void {
    if (turns.length === 0) return;
    setState({
      turns: [...turns, ...state.turns],
    });
  }

  function clear(): void {
    pendingDelta.clear();
    pendingThinking.clear();
    if (deltaFlushTimer != null) {
      clearTimeout(deltaFlushTimer);
      deltaFlushTimer = null;
    }
    if (thinkingFlushTimer != null) {
      clearTimeout(thinkingFlushTimer);
      thinkingFlushTimer = null;
    }
    setState({
      turns: [],
      subAgentCards: {},
      activeMessageIds: new Set(),
      lastSequenceNum: 0,
    });
  }

  return {
    subscribe,
    getSnapshot,
    enqueueDelta,
    flushPendingDeltas,
    enqueueThinking,
    flushPendingThinking,
    appendTurn,
    updateAssistantTurn,
    upsertSubAgentCard,
    setTurns,
    prependTurns,
    clear,
  };
}
