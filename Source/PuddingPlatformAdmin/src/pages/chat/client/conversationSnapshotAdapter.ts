import { getConversationMessageContent, getConversationOpen } from './conversationOpenApi';
import type {
  ConversationHistoryItem,
  ConversationOpenSnapshot,
  ConversationReadModelProcessSummary,
  ConversationTurnSummaryView,
} from './conversationOpenTypes';
import type {
  AgentConversationView,
  ConversationMessageView,
  ConversationProcessSummary,
} from './types';

/**
 * 阶段 D2c：统一打开快照 → 现有 AgentConversationView 渲染契约的适配层。
 *
 * 边界：
 * - 首屏只消费快照（最近 20 条、读模型 Turn 摘要）；不重放事件；
 * - 短消息直接使用快照内全文；长消息按 UTF-8 字节分段读取，单条/整页有上限，
 *   超出上限保留预览（后续展开全文入口）；
 * - 活动 Turn 尚未终态时回退旧投影一次（活动正文/工具轨迹仍由旧实时投影负责），
 *   终态后全部走快照；
 * - 旧端点不可用（旧 Core 未部署新端点）时回退旧投影，保证过渡期可用。
 */

const MAX_EXPAND_CONTENT_BYTES = 512 * 1024;
const CONTENT_SEGMENT_BYTES = 32 * 1024;
/** API pages are newest first, including their tie order. UI is oldest first. */
export const chronologicalHistory = (items: ConversationHistoryItem[]): ConversationHistoryItem[] =>
  [...items].reverse();

export interface SnapshotConversationDeps {
  getSnapshot?: typeof getConversationOpen;
  getContent?: typeof getConversationMessageContent;
  getLegacyConversation?: (
    workspaceId: string,
    agentId: string,
    signal?: AbortSignal,
  ) => Promise<AgentConversationView | null>;
}

const toIso = (createdAt: number): string =>
  new Date(createdAt).toISOString();

const roleOf = (item: ConversationHistoryItem): ConversationMessageView['role'] =>
  item.role === 'user' ? 'user' : item.role === 'system' ? 'system' : 'agent';

const processSummaryOf = (
  turn: ConversationTurnSummaryView | undefined,
): ConversationProcessSummary | null => {
  if (!turn) return null;
  const process: ConversationReadModelProcessSummary = turn.process;
  const totalItems =
    process.contentAppendCount +
    process.thinkingAppendCount +
    process.toolCallRequestedCount +
    process.toolCallCompletedCount +
    process.toolCallFailedCount;
  return {
    totalItems,
    thinkingRounds: 0,
    thinkingSteps: process.thinkingAppendCount,
    toolCalls: process.toolCallRequestedCount,
    toolResults: process.toolCallCompletedCount,
    failedTools: process.toolCallFailedCount,
    durationMs: 0,
    hasDetails: totalItems > 0,
  };
};

const outcomeOf = (
  turn: ConversationTurnSummaryView | undefined,
): ConversationMessageView['turnOutcome'] => {
  if (!turn) return null;
  if (turn.terminalState === 1) return { status: 'succeeded' };
  if (turn.terminalState === 2) return { status: 'failed' };
  if (turn.terminalState === 3) return { status: 'cancelled' };
  return null;
};

const isActiveTurn = (snapshot: ConversationOpenSnapshot): boolean =>
  Boolean(snapshot.activeTurn) && snapshot.activeTurn?.terminalState === 0;

/** 单条历史快照项 → 渲染消息；历史翻页复用同一映射（无 Turn 摘要时只保留基础字段）。 */
export function mapSnapshotHistoryItem(
  item: ConversationHistoryItem,
  turn: ConversationTurnSummaryView | undefined,
  content?: string,
): ConversationMessageView {
  const role = roleOf(item);
  const needsExpand = item.hasMoreContent && content === undefined;
  return {
    messageId: item.messageId,
    turnId: item.turnId ?? null,
    runId: turn?.rootRunId ?? null,
    role,
    sourceKind: item.sourceKind ?? (role === 'user' ? 'user' : role === 'system' ? 'system' : 'agent'),
    sourceId: item.sourceId ?? (role === 'user' ? 'user' : role === 'system' ? 'system' : 'agent'),
    sourceName: item.sourceName ?? (role === 'user' ? '用户' : role === 'system' ? '系统' : 'Agent'),
    messageType: item.messageType ?? (role === 'user' ? 'user_message' : 'agent_output'),
    llmRole: item.llmRole ?? (role === 'user' ? 'user' : role === 'system' ? 'system' : 'assistant'),
    contentParts: item.contentParts,
    createdAt: toIso(item.createdAt),
    content: content ?? item.preview,
    status: 'succeeded',
    processItems: [],
    processSummary: processSummaryOf(turn),
    turnOutcome: outcomeOf(turn),
    hasMoreContent: needsExpand,
    contentLength: item.contentLength,
  };
}

/**
 * 显式展开时按 UTF-8 字节分段读取正文；最多读 512KiB（超出保留预览并继续标记可展开）。
 * 返回 null 表示读取失败/无可显示内容。
 */
export async function readFullMessageContent(
  conversationId: string,
  messageId: string,
  totalBytes: number,
  getContent: typeof getConversationMessageContent,
  signal?: AbortSignal,
): Promise<string | null> {
  if (totalBytes <= 0) return null;

  const budget = Math.min(totalBytes, MAX_EXPAND_CONTENT_BYTES);
  let offset = 0;
  let content = '';
  for (let segment = 0; segment < 64 && offset < budget; segment++) {
    const page = await getContent(conversationId, messageId, {
      offsetBytes: offset,
      maxBytes: CONTENT_SEGMENT_BYTES,
      signal,
    });
    content += page.content;
    if (!page.hasMore) return content;
    if (page.nextOffsetBytes === null || page.nextOffsetBytes === undefined) {
      return null;
    }
    offset = page.nextOffsetBytes;
  }

  return content.length > 0 ? content : null;
}

export async function getAgentConversationFromSnapshot(
  workspaceId: string,
  agentId: string,
  ownerUserId: string,
  signal?: AbortSignal,
  deps: SnapshotConversationDeps = {},
): Promise<AgentConversationView | null> {
  const getSnapshot = deps.getSnapshot ?? getConversationOpen;
  const legacy = deps.getLegacyConversation;

  let snapshot: ConversationOpenSnapshot | null;
  try {
    snapshot = await getSnapshot(workspaceId, agentId, { signal });
  } catch (error) {
    // 过渡期：旧 Core 没有新端点时保持可用；新端点可用后不再走这里。
    if (legacy) return legacy(workspaceId, agentId, signal);
    throw error;
  }

  if (!snapshot) {
    return legacy ? legacy(workspaceId, agentId, signal) : null;
  }

  if (isActiveTurn(snapshot) && legacy) {
    // 活动 Turn 的正文/工具轨迹仍由旧实时投影负责；终态后走快照。
    const active = await legacy(workspaceId, agentId, signal);
    if (active) return active;
  }

  const turns = new Map<string, ConversationTurnSummaryView>();
  for (const turn of snapshot.visibleTurns) turns.set(turn.turnId, turn);
  if (snapshot.activeTurn) turns.set(snapshot.activeTurn.turnId, snapshot.activeTurn);

  const messages: ConversationMessageView[] = chronologicalHistory(snapshot.history.items).map(
    (item) => {
      const turn = item.turnId ? turns.get(item.turnId) : undefined;
      return mapSnapshotHistoryItem(item, turn);
    },
  );

  const lastCreatedAt = snapshot.history.items.length > 0
    ? snapshot.history.items[0].createdAt
    : Date.now();

  return {
    workspaceId,
    ownerUserId,
    agentId,
    mainSessionId: snapshot.conversationId,
    messages,
    activeRun: null,
    // 快照游标 C 是 SSE 的权威恢复起点；事件 head 由 SSE 追平（Plan §4.2/§7.2）。
    eventCursor: snapshot.snapshotCursor,
    updatedAt: toIso(lastCreatedAt),
    hasMoreHistory: snapshot.history.hasMore,
    historyNextBeforeCreatedAt: snapshot.history.nextBeforeCreatedAt ?? null,
    historyNextBeforeMessageId: snapshot.history.nextBeforeMessageId ?? null,
  };
}
