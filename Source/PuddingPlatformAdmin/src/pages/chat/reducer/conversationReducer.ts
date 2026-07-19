// ── P0: Conversation Event Reducer ──────────────────────────────
// 单一纯函数 reduceConversationEvent，替代 useChatState 中分散的 event 处理逻辑。
//
// 规则：
//   sequence <= cursor     → 幂等忽略
//   sequence == cursor + 1 → 正常提交
//   sequence > cursor + 1  → 停止提交，进入缺口恢复
//
// 事件职责：
//   turn.accepted     → 创建 Turn + user/assistant 消息占位
//   message.delta     → 追加到指定 messageId
//   message.completed → 完成指定消息
//   turn.completed    → 完成指定 Turn
//   turn.failed       → 失败指定 Turn
//   turn.cancelled    → 取消指定 Turn
// ─────────────────────────────────────────────────────────────────
import {
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from './subAgentReducer';

export interface ConversationState {
  conversationId: string;
  cursor: number; // last applied sequence
  turns: ConversationTurn[];
  messages: Map<string, ConversationMessage>;
  turnOrder: string[]; // ordered turn IDs
  subAgentRuns: SubAgentRunMap;
  gapDetected: boolean;
}

export interface ConversationTurn {
  turnId: string;
  status: 'active' | 'completed' | 'failed' | 'cancelled';
  userMessageId: string;
  assistantMessageId: string;
  createdAt: number;
}

export interface ConversationMessage {
  messageId: string;
  role: 'user' | 'assistant';
  content: string;
  thinkingDelta: string;
  status: 'placeholder' | 'streaming' | 'completed' | 'failed' | 'cancelled';
  turnId: string;
  createdAt: number;
  errorCode?: string;
  errorMessage?: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
  };
}

export interface ConversationEvent {
  sequence: number;
  type: string;
  turnId?: string;
  messageId?: string;
  conversationId?: string;
  commandId?: string;
  runId?: string;
  clientRequestId?: string;
  userMessageId?: string;
  assistantMessageId?: string;
  delta?: string;
  reply?: string;
  message?: string;
  errorCode?: string;
  errorMessage?: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
  };
  [key: string]: unknown;
}

const createId = () =>
  `ev-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

export function createInitialState(conversationId?: string): ConversationState {
  return {
    conversationId: conversationId ?? '',
    cursor: 0,
    turns: [],
    messages: new Map(),
    turnOrder: [],
    subAgentRuns: {},
    gapDetected: false,
  };
}

export function setSnapshotCursor(
  state: ConversationState,
  cursor: number,
  messages?: Array<{ id: number; role: string; content: string; createdAt: number }>,
  turns?: ConversationTurn[],
): ConversationState {
  const newMessages = new Map(state.messages);
  const newTurns: ConversationTurn[] = [...(turns ?? [])];

  if (messages) {
    for (const m of messages) {
      const msgId = `hist-${m.id}`;
      if (!newMessages.has(msgId)) {
        newMessages.set(msgId, {
          messageId: msgId,
          role: m.role as 'user' | 'assistant',
          content: m.content,
          thinkingDelta: '',
          status: 'completed' as const,
          turnId: '',
          createdAt: m.createdAt,
        });
      }
    }
  }

  return {
    ...state,
    cursor: Math.max(state.cursor, cursor),
    messages: newMessages,
    turns: newTurns.length > 0 ? newTurns : state.turns,
    turnOrder: newTurns.length > 0 ? newTurns.map((t) => t.turnId) : state.turnOrder,
    gapDetected: false,
  };
}

/** 纯函数：将单个事件应用到对话状态，返回新状态。 */
export function reduceConversationEvent(
  state: ConversationState,
  event: ConversationEvent,
): { state: ConversationState; applied: boolean } {
  const seq = event.sequence;
  const type = normalizeEventType(event.type);

  // 幂等忽略
  if (seq <= state.cursor) {
    return { state, applied: false };
  }

  // 缺口检测
  if (seq > state.cursor + 1) {
    return {
      state: { ...state, gapDetected: true },
      applied: false,
    };
  }

  // 正常提交
  const nextState = applyEvent(state, event, type, seq);
  return {
    state: { ...nextState, cursor: seq, gapDetected: false },
    applied: true,
  };
}

function normalizeEventType(rawType: string): string {
  const map: Record<string, string> = {
    // ADR-057 backend event types (ConversationEventTypes)
    'turn.accepted': 'turn.accepted',
    'turn.started': 'turn.started',
    'turn.waiting_for_tool': 'turn.waiting_for_tool',
    'turn.completed': 'turn.completed',
    'turn.failed': 'turn.failed',
    'turn.cancelled': 'turn.cancelled',
    'message.content.appended': 'message.delta',
    'message.thinking_summary.appended': 'message.thinking',
    'message.completed': 'message.completed',
    'message.failed': 'message.failed',
    'usage.recorded': 'message.usage',
    'tool.call.requested': 'tool.call',
    'tool.call.completed': 'tool.result',
    'tool.call.failed': 'tool.failed',
    // Legacy SSE event types (backward compat)
    'assistant.content.delta': 'message.delta',
    'assistant.thinking.delta': 'message.thinking',
    done: 'turn.completed',
    error: 'turn.failed',
    cancelled: 'turn.cancelled',
    usage: 'message.usage',
    delta: 'message.delta',
    thinking: 'message.thinking',
  };
  return map[rawType] ?? rawType;
}

function applyEvent(
  state: ConversationState,
  event: ConversationEvent,
  type: string,
  seq: number,
): ConversationState {
  const nextSubAgentRuns = reduceSubAgentRunEvent(
    state.subAgentRuns,
    event,
  );
  if (nextSubAgentRuns !== state.subAgentRuns) {
    return { ...state, subAgentRuns: nextSubAgentRuns };
  }

  switch (type) {
    case 'turn.accepted': {
      const turnId = event.turnId ?? `turn-${seq}`;
      const userMsgId = event.userMessageId ?? `umsg-${seq}`;
      const asstMsgId = event.assistantMessageId ?? `amsg-${seq}`;

      const turn: ConversationTurn = {
        turnId,
        status: 'active',
        userMessageId: userMsgId,
        assistantMessageId: asstMsgId,
        createdAt: Date.now(),
      };

      const newTurns = [...state.turns, turn];
      const newMessages = new Map(state.messages);

      newMessages.set(userMsgId, {
        messageId: userMsgId,
        role: 'user',
        content: '',
        thinkingDelta: '',
        status: 'placeholder',
        turnId,
        createdAt: Date.now(),
      });

      newMessages.set(asstMsgId, {
        messageId: asstMsgId,
        role: 'assistant',
        content: '',
        thinkingDelta: '',
        status: 'placeholder',
        turnId,
        createdAt: Date.now(),
      });

      return {
        ...state,
        turns: newTurns,
        messages: newMessages,
        turnOrder: [...state.turnOrder, turnId],
      };
    }

    case 'message.delta': {
      const targetMsgId = event.messageId ?? findActiveAssistantMessage(state);
      if (!targetMsgId) return state;

      const msg = state.messages.get(targetMsgId);
      if (!msg) return state;

      const delta = event.delta ?? '';
      const newMessages = new Map(state.messages);
      newMessages.set(targetMsgId, {
        ...msg,
        content: msg.content + delta,
        status: 'streaming',
      });

      return { ...state, messages: newMessages };
    }

    case 'message.thinking': {
      const targetMsgId = event.messageId ?? findActiveAssistantMessage(state);
      if (!targetMsgId) return state;

      const msg = state.messages.get(targetMsgId);
      if (!msg) return state;

      const delta = event.delta ?? '';
      const newMessages = new Map(state.messages);
      newMessages.set(targetMsgId, {
        ...msg,
        thinkingDelta: msg.thinkingDelta + delta,
      });

      return { ...state, messages: newMessages };
    }

    case 'message.completed': {
      const targetMsgId = event.messageId ?? findActiveAssistantMessage(state);
      if (!targetMsgId) return state;

      const msg = state.messages.get(targetMsgId);
      if (!msg) return state;

      const newMessages = new Map(state.messages);
      newMessages.set(targetMsgId, {
        ...msg,
        status: 'completed',
        content: event.reply ?? msg.content,
      });

      return { ...state, messages: newMessages };
    }

    case 'turn.completed': {
      const turnId = event.turnId ?? findActiveTurn(state);
      if (!turnId) return state;

      const newTurns = state.turns.map((t) =>
        t.turnId === turnId ? { ...t, status: 'completed' as const } : t,
      );

      // Complete the assistant message if still placeholder
      const turn = state.turns.find((t) => t.turnId === turnId);
      const newMessages = new Map(state.messages);
      if (turn) {
        const asstMsg = newMessages.get(turn.assistantMessageId);
        if (asstMsg && asstMsg.status !== 'completed') {
          newMessages.set(turn.assistantMessageId, {
            ...asstMsg,
            status: 'completed',
            content: event.reply ?? asstMsg.content,
          });
        }
      }

      return { ...state, turns: newTurns, messages: newMessages };
    }

    case 'turn.failed': {
      const turnId = event.turnId ?? findActiveTurn(state);
      if (!turnId) return state;

      const newTurns = state.turns.map((t) =>
        t.turnId === turnId ? { ...t, status: 'failed' as const } : t,
      );
      const turn = state.turns.find((t) => t.turnId === turnId);
      const newMessages = new Map(state.messages);
      if (turn) {
        const assistant = newMessages.get(turn.assistantMessageId);
        if (assistant) {
          newMessages.set(turn.assistantMessageId, {
            ...assistant,
            status: 'failed',
            errorCode: event.errorCode,
            errorMessage: event.errorMessage ?? event.message ?? '请求失败',
          });
        }
      }

      return { ...state, turns: newTurns, messages: newMessages };
    }

    case 'turn.cancelled': {
      const turnId = event.turnId ?? findActiveTurn(state);
      if (!turnId) return state;

      const newTurns = state.turns.map((t) =>
        t.turnId === turnId ? { ...t, status: 'cancelled' as const } : t,
      );
      const turn = state.turns.find((t) => t.turnId === turnId);
      const newMessages = new Map(state.messages);
      if (turn) {
        const assistant = newMessages.get(turn.assistantMessageId);
        if (assistant) {
          newMessages.set(turn.assistantMessageId, {
            ...assistant,
            status: 'cancelled',
            errorMessage: event.errorMessage ?? event.message,
          });
        }
      }

      return { ...state, turns: newTurns, messages: newMessages };
    }

    case 'message.usage': {
      if (!event.usage) return state;
      const targetMsgId = event.messageId ?? findActiveAssistantMessage(state);
      if (!targetMsgId) return state;

      const msg = state.messages.get(targetMsgId);
      if (!msg) return state;

      const newMessages = new Map(state.messages);
      newMessages.set(targetMsgId, { ...msg, usage: event.usage });

      return { ...state, messages: newMessages };
    }

    default:
      return state;
  }
}

function findActiveTurn(state: ConversationState): string | null {
  for (let i = state.turnOrder.length - 1; i >= 0; i--) {
    const turnId = state.turnOrder[i];
    const turn = state.turns.find((t) => t.turnId === turnId);
    if (turn && turn.status === 'active') return turn.turnId;
  }
  return state.turns[state.turns.length - 1]?.turnId ?? null;
}

function findActiveAssistantMessage(state: ConversationState): string | null {
  const turnId = findActiveTurn(state);
  if (!turnId) return null;
  const turn = state.turns.find((t) => t.turnId === turnId);
  return turn?.assistantMessageId ?? null;
}
