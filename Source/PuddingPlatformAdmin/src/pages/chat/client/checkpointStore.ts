// ── Checkpoint Timeline (P2#7) ─────────────────────────────────
// 纯模块：会话快照的创建、持久化、还原与分支（Fork）。
// 快照只保留可序列化子集（用户文本 / 助手回复 / 状态 / 时间戳），
// 不携带 timelineItems 大对象，保证 localStorage 体积与 JSON 往返稳定。
import type {
  AssistantStatus,
  ChatTurn,
  MessageStatus,
} from '../types';
import type { ConversationProcessSummary } from './types';

export const CHECKPOINT_STORAGE_KEY = 'pudding-chat-checkpoints-v1';
export const MAX_CHECKPOINTS_PER_SESSION = 30;

/** 单个 turn 的可序列化快照（timelineItems 等大对象不进入快照）。 */
export interface CheckpointTurnSnapshot {
  turnId: string;
  user: {
    id: string;
    text: string;
    timestamp: number;
    status: MessageStatus;
    metadata?: Record<string, string>;
    dbMessageId?: number;
  };
  assistant: {
    id: string;
    status: AssistantStatus;
    answerMarkdown: string;
    isStreaming: boolean;
    renderMode: string;
    processSummary?: ConversationProcessSummary | null;
  };
}

/** 一次 turn 前保存的会话快照。 */
export interface ChatCheckpoint {
  checkpointId: string;
  sessionId: string;
  workspaceId?: string;
  agentId?: string;
  /** 快照时的本地时间（毫秒）。 */
  createdAt: number;
  /** 快照时的 turn 数（即下一次 turn 的序号）。 */
  turnIndex: number;
  /** 触发该快照的用户消息预览（≤ 60 字符）。 */
  label: string;
  turns: CheckpointTurnSnapshot[];
}

export type CheckpointStoreMap = Record<string, ChatCheckpoint[]>;

export const createCheckpointId = (): string =>
  `cp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 9)}`;

export function buildCheckpointLabel(text: string, max = 60): string {
  const trimmed = (text || '').replace(/\s+/g, ' ').trim();
  return trimmed.length > max ? `${trimmed.slice(0, max)}…` : trimmed;
}

/** ChatTurn[] → 可序列化快照数组。 */
export function turnsToCheckpointSnapshot(
  turns: ChatTurn[],
): CheckpointTurnSnapshot[] {
  return turns.map((turn) => ({
    turnId: turn.turnId,
    user: {
      id: turn.userMessage.id,
      text: turn.userMessage.text,
      timestamp: turn.userMessage.timestamp,
      status: turn.userMessage.status,
      metadata: turn.userMessage.metadata,
      dbMessageId: turn.userMessage.dbMessageId,
    },
    assistant: {
      id: turn.assistant.id,
      status: turn.assistant.status,
      answerMarkdown: turn.assistant.answerMarkdown,
      isStreaming: turn.assistant.isStreaming,
      renderMode: turn.assistant.renderMode,
      processSummary: turn.assistant.processSummary ?? null,
    },
  }));
}

/** 快照数组 → ChatTurn[]（还原视图；不重建 timelineItems）。 */
export function checkpointSnapshotToTurns(
  snapshot: CheckpointTurnSnapshot[],
): ChatTurn[] {
  return snapshot.map((item) => ({
    turnId: item.turnId,
    userMessage: {
      id: item.user.id,
      text: item.user.text,
      timestamp: item.user.timestamp,
      status: item.user.status,
      metadata: item.user.metadata,
      dbMessageId: item.user.dbMessageId,
    },
    assistant: {
      id: item.assistant.id,
      status: item.assistant.status,
      timelineItems: [],
      answerMarkdown: item.assistant.answerMarkdown,
      isStreaming: false,
      renderMode: item.assistant.renderMode as ChatTurn['assistant']['renderMode'],
      processSummary: item.assistant.processSummary ?? undefined,
    },
  }));
}

/**
 * 保存一个新快照到现有列表（不可变），并按规则去重/截断：
 * - 与最近一条同 turnIndex 且同 label 的重复快照会被跳过（幂等重试防抖）
 * - 每会话最多保留 MAX_CHECKPOINTS_PER_SESSION 条（保留最新）
 */
export function captureCheckpoint(
  input: {
    sessionId: string;
    workspaceId?: string;
    agentId?: string;
    createdAt: number;
    label: string;
    turns: ChatTurn[];
  },
  existing: ChatCheckpoint[] = [],
): ChatCheckpoint[] {
  const latest = existing[0];
  const turnIndex = input.turns.length;
  if (
    latest &&
    latest.turnIndex === turnIndex &&
    latest.label === input.label
  ) {
    return existing;
  }
  const checkpoint: ChatCheckpoint = {
    checkpointId: createCheckpointId(),
    sessionId: input.sessionId,
    workspaceId: input.workspaceId,
    agentId: input.agentId,
    createdAt: input.createdAt,
    turnIndex,
    label: buildCheckpointLabel(input.label),
    turns: turnsToCheckpointSnapshot(input.turns),
  };
  return [checkpoint, ...existing].slice(0, MAX_CHECKPOINTS_PER_SESSION);
}

export function removeCheckpoint(
  checkpointId: string,
  existing: ChatCheckpoint[] = [],
): ChatCheckpoint[] {
  return existing.filter((item) => item.checkpointId !== checkpointId);
}

export function clearCheckpoints(
  existing: ChatCheckpoint[] = [],
): ChatCheckpoint[] {
  return [];
}

// ── localStorage 持久化（整表 map：sessionId → 快照列表）───────

export function loadCheckpointStore(): CheckpointStoreMap {
  if (typeof window === 'undefined') return {};
  try {
    const raw = window.localStorage.getItem(CHECKPOINT_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as CheckpointStoreMap;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

export function persistCheckpointStore(map: CheckpointStoreMap): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(CHECKPOINT_STORAGE_KEY, JSON.stringify(map));
  } catch {
    // 隐私模式/配额满时静默降级：快照仅保留在内存，不打断聊天。
  }
}

export function loadSessionCheckpoints(sessionId: string): ChatCheckpoint[] {
  if (!sessionId) return [];
  const map = loadCheckpointStore();
  return map[sessionId] ?? [];
}
