// ── ADR-057 Phase 5: Domain Contracts ─────────────────────────
// 与后端 ConversationEvent Envelope 一一对应。
// ─────────────────────────────────────────────────────────────

/** 后端 ConversationEvent Envelope */
export interface ConversationEventEnvelope {
  eventId: string;
  conversationId: string;
  sequence: number;
  workspaceId: string;
  turnId: string;
  commandId?: string;
  runId?: string;
  messageId?: string;
  type: string;
  schemaVersion: number;
  occurredAt: string;
  committedAt: string;
  correlationId?: string;
  causationId?: string;
  producerEventId?: string;
  payload: Record<string, unknown>;
}

/** Bootstrap 响应 */
export interface ConversationBootstrap {
  conversationId: string;
  snapshotCursor: number;
  messages: Array<{
    id: number;
    role: string;
    content: string;
    createdAt: number;
  }>;
  activeTurns: Array<{
    turnId: string;
    status: string;
    userMessageId: string;
    assistantMessageId: string;
    createdAt: number;
  }>;
  hasMoreBefore: boolean;
  oldestMessageCursor: string | null;
}

/** 202 命令受理响应 */
export interface AcceptTurnResponse {
  conversationId: string;
  commandId: string;
  turnId: string;
  userMessageId: string;
  assistantMessageId: string;
  acceptedSequence: number;
}

/** SSE 事件（从 SSE 解析的传输格式） */
export interface SseTransportEvent {
  sequence: number;
  type: string;
  turnId?: string;
  messageId?: string;
  commandId?: string;
  userMessageId?: string;
  assistantMessageId?: string;
  delta?: string;
  reply?: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
  };
  [key: string]: unknown;
}

/** snapshot_required 错误 */
export interface SnapshotRequiredError {
  code: 'snapshot_required';
  minimumAvailableSequence: number;
  snapshotUrl: string;
}
