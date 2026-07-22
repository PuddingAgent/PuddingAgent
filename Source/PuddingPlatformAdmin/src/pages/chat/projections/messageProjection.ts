// ── Message Projection [EXPERIMENTAL] ────────────────────────────
// ADR-054 Step 1 scaffold. 尚未接入生产主链路，仅被测试引用。
// ────────────────────────────────────────────────────────────────
// 把 turns + conversationView + activeRun + subAgentCards 转成 VirtualMessageItem[]。
// 纯函数，不访问 DOM、localStorage、API。

import type {
  ChatMessageBlock,
  ChatTurn as SystemChatTurn,
  TimelineItem,
} from '../types';

// ── 类型定义 ────────────────────────────────────────────

export type VirtualMessageItem =
  | { kind: 'message'; key: string; block: ChatMessageBlock }
  | { kind: 'subagent'; key: string; card: SubAgentCardVm }
  | { kind: 'system'; key: string; text: string; createdAt: number };

export interface SubAgentCardVm {
  subAgentId: string;
  parentMessageId: string;
  name: string;
  status: 'running' | 'completed' | 'failed';
  summary?: string;
  createdAt: number;
}

export interface AgentConversationView {
  projectedTurns: SystemChatTurn[];
  activeRunId?: string;
  activeRunMarkdown?: string;
}

export interface SubAgentCardMap {
  [key: string]: SubAgentCardVm;
}

export interface MessageProjectionInput {
  turns: SystemChatTurn[];
  conversationView?: AgentConversationView | null;
  activeRunMarkdown?: string;
  subAgentCards?: SubAgentCardMap;
  agentName: string;
  currentUser?: { name?: string; avatar?: string };
}

export interface MessageProjectionOutput {
  items: VirtualMessageItem[];
  lastMessageKey?: string;
  activeMessageKey?: string;
}

// ── 助理状态映射 ────────────────────────────────────────

type ChatMessageStatus =
  | 'sending'
  | 'thinking'
  | 'streaming'
  | 'success'
  | 'error'
  | 'cancelled';

function toChatMessageStatus(status: string): ChatMessageStatus {
  const map: Record<string, ChatMessageStatus> = {
    thinking: 'thinking',
    executing: 'thinking',
    streaming: 'streaming',
    success: 'success',
    error: 'error',
    cancelled: 'cancelled',
  };
  return map[status] ?? 'success';
}

// ── Projection 核心 ─────────────────────────────────────

/**
 * 从 ChatTurn[] 构建聊天消息块列表。
 * 拆分规则：
 * 1. 每次用户输入至少生成一个 Agent 消息
 * 2. 同一 Agent 连续消息可分组
 * 3. 不同 Agent 分开显示
 * 4. 工具过程不作为消息气泡
 */
export function buildMessageBlocks(
  turns: SystemChatTurn[],
  agentName?: string,
  currentUser?: { name?: string; avatar?: string },
): ChatMessageBlock[] {
  const blocks: ChatMessageBlock[] = [];

  for (let i = 0; i < turns.length; i++) {
    const turn = turns[i];

    // 用户消息
    if (turn.userMessage.text.trim()) {
      blocks.push({
        id: `${turn.userMessage.id}:user`,
        turnId: turn.turnId,
        role: 'user',
        content: turn.userMessage.text,
        status: turn.userMessage.status === 'sending' ? 'sending' : 'success',
        createdAt: turn.userMessage.timestamp,
        metadata: turn.userMessage.metadata,
        modality:
          turn.userMessage.metadata?.inputMode === 'voice'
            ? 'voice'
            : turn.userMessage.metadata?.inputMode === 'camera'
              ? 'camera'
              : 'text',
        userName: currentUser?.name || '我',
        userAvatarUrl: currentUser?.avatar,
      });
    }

    // 心跳消息
    if (turn.assistant.renderMode === 'heartbeat') {
      blocks.push({
        id: `${turn.assistant.id}:heartbeat`,
        turnId: turn.turnId,
        role: 'heartbeat',
        content: turn.assistant.answerMarkdown,
        status: 'success',
        createdAt: turn.userMessage.timestamp,
      });
      continue;
    }

    // Agent 消息
    const hasInboundQuoted = Boolean(turn.assistant.quotedMessage);
    const hasContent =
      hasInboundQuoted ||
      Boolean(turn.assistant.answerMarkdown) ||
      turn.assistant.isStreaming ||
      turn.assistant.status === 'thinking' ||
      turn.assistant.status === 'executing' ||
      turn.assistant.status === 'streaming' ||
      turn.assistant.status === 'error' ||
      turn.assistant.status === 'cancelled';

    if (hasContent) {
      const blockAgentName =
        turn.source?.displayName || agentName || 'Pudding';
      const block: ChatMessageBlock = {
        id: `${turn.assistant.id}:assistant:0`,
        turnId: turn.turnId,
        role: 'agent',
        content: turn.assistant.answerMarkdown,
        status: toChatMessageStatus(turn.assistant.status),
        createdAt: turn.userMessage.timestamp,
        agentName: blockAgentName,
        agentAvatarUrl: turn.source?.avatarUrl,
        agentAvatarColor: turn.source?.avatarColor || '#7c3aed',
        agentAvatarEmoji: turn.source?.avatarEmoji || '🤖',
        processItems: turn.assistant.timelineItems?.length
          ? turn.assistant.timelineItems
          : undefined,
        usage: turn.assistant.usage,
        isStreaming:
          turn.assistant.isStreaming || turn.assistant.status === 'streaming',
        quotedMessage: turn.assistant.quotedMessage,
      };

      // Agent 消息分组不得跨用户消息
      const previousVisibleBlock = blocks[blocks.length - 1];
      if (
        previousVisibleBlock?.role === 'agent' &&
        previousVisibleBlock.agentName === blockAgentName
      ) {
        block.groupedWithPrevious = true;
      }

      blocks.push(block);
    }
  }

  return blocks;
}

// ── VirtualMessageItem 构建 ─────────────────────────────

/**
 * 构建 VirtualMessageItem[] 列表。
 * 合并 conversationView projected turns、本地 turns、active run 和 subAgentCards。
 */
export function buildVirtualMessageItems(
  input: MessageProjectionInput,
): MessageProjectionOutput {
  const {
    turns,
    conversationView,
    activeRunMarkdown,
    subAgentCards,
    agentName,
    currentUser,
  } = input;

  const items: VirtualMessageItem[] = [];

  // 1. 优先使用 conversationView 的投影 turns，但如果 conversationView 存在但 projectedTurns 为空，回退到本地 turns
  const hasServerProjection =
    conversationView && conversationView.projectedTurns.length > 0;
  const sourceTurns = hasServerProjection
    ? conversationView!.projectedTurns
    : turns;

  // 2. 构建基础消息块
  const blocks = buildMessageBlocks(sourceTurns, agentName, currentUser);

  for (const block of blocks) {
    items.push({
      kind: 'message',
      key: block.id,
      block,
    });
  }

  // 3. 如果有 active run 且未合并到现有 turn，追加为 streaming 消息
  if (activeRunMarkdown && conversationView?.activeRunId) {
    const alreadyMerged = blocks.some((b) =>
      b.id.includes(conversationView.activeRunId!),
    );
    if (!alreadyMerged) {
      items.push({
        kind: 'message',
        key: `active-run:${conversationView.activeRunId}`,
        block: {
          id: `active-run:${conversationView.activeRunId}`,
          turnId: conversationView.activeRunId,
          role: 'agent',
          content: activeRunMarkdown,
          status: 'streaming',
          createdAt: Date.now(),
          agentName: agentName || 'Pudding',
          isStreaming: true,
        },
      });
    }
  }

  // 4. 按时间戳插入 subAgentCards
  if (subAgentCards) {
    for (const card of Object.values(subAgentCards)) {
      items.push({
        kind: 'subagent',
        key: `subagent:${card.subAgentId}`,
        card,
      });
    }
  }

  // 5. 按 createdAt 排序（保持同时间戳的原始顺序）
  items.sort((a, b) => {
    const aTime =
      a.kind === 'message'
        ? a.block.createdAt
        : a.kind === 'subagent'
          ? a.card.createdAt
          : a.createdAt;
    const bTime =
      b.kind === 'message'
        ? b.block.createdAt
        : b.kind === 'subagent'
          ? b.card.createdAt
          : b.createdAt;
    return aTime - bTime;
  });

  // 6. 确定 lastMessageKey 和 activeMessageKey
  let lastMessageKey: string | undefined;
  let activeMessageKey: string | undefined;

  for (const item of items) {
    if (item.kind === 'message') {
      lastMessageKey = item.key;
      if (item.block.isStreaming) {
        activeMessageKey = item.key;
      }
    }
  }

  return {
    items,
    lastMessageKey,
    activeMessageKey,
  };
}
