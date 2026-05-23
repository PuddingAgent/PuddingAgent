// ── 聊天页共享类型 ─────────────────────────────────────────────
import type { TokenUsageDto, WorkspaceAgentDto, WorkspaceWithPermDto } from '@/services/platform/api';

export type MessageStatus = 'sending' | 'success' | 'error';
export type AssistantStatus = 'thinking' | 'executing' | 'streaming' | 'success' | 'error' | 'cancelled';
export type ChatMessageStatus = 'sending' | 'thinking' | 'streaming' | 'success' | 'error' | 'cancelled';

/** 统一时间线条目：思考 / 工具调用 / 工具结果 / 潜意识步骤 / 子代理 */
export interface TimelineItem {
  id: string;
  type: 'thinking' | 'tool_call' | 'tool_result' | 'subconscious_step';
  text?: string;
  status?: string;
  name?: string;
  arguments?: string;
  output?: string;
  exitCode?: number;
  message?: string;
  timestamp: number;
  collapsed: boolean;
}

export interface ChatTurn {
  turnId: string;
  /** 消息来源（agent / websocket / webhook / email / mqtt） */
  source?: ChatSource;
  userMessage: {
    id: string;
    text: string;
    timestamp: number;
    status: MessageStatus;
  };
  assistant: {
    id: string;
    status: AssistantStatus;
    /** 统一时间线：按 Agent 实际执行顺序排列 */
    timelineItems: TimelineItem[];
    answerMarkdown: string;
    isStreaming: boolean;
    usage?: TokenUsageDto;
    renderMode: 'legacy' | 'structured';
  };
}

// ── IM-style MessageStream ViewModel ─────────────────────────

/**
 * ADR: Chat Message Interaction Redesign
 * IM-style 消息块，由 buildMessageBlocks 从 ChatTurn[] 转换而来。
 * 每个块对应一条独立的消息气泡。
 */
export interface ChatMessageBlock {
  id: string;
  turnId: string;
  role: 'user' | 'agent' | 'system';
  content: string;
  status: ChatMessageStatus;
  createdAt: number;

  /** 用户信息（仅 role='user' 时有效） */
  userName?: string;
  userAvatarUrl?: string;

  /** Agent 信息（仅 role='agent' 时有效） */
  agentId?: string;
  agentName?: string;
  agentAvatarUrl?: string;
  agentAvatarColor?: string;
  agentAvatarEmoji?: string;

  /** 执行过程（默认折叠） */
  processItems?: TimelineItem[];
  /** Token 用量 */
  usage?: TokenUsageDto;
  /** 是否与上一条消息同 Agent（视觉分组） */
  groupedWithPrevious?: boolean;

  /** 流式渲染标记 */
  isStreaming?: boolean;
}

/**
 * 从 ChatTurn[] 转换为 IM-style ChatMessageBlock[]
 * 拆分规则：
 * 1. 每次用户输入至少生成一个 AgentMessage
 * 2. 同一个 Agent 连续消息可分组
 * 3. 不同 Agent 必须分开显示
 * 4. 工具过程不作为消息气泡
 */
export function buildMessageBlocks(
  turns: ChatTurn[],
  agentName?: string,
  currentUser?: { name?: string; avatar?: string },
): ChatMessageBlock[] {
  const blocks: ChatMessageBlock[] = [];

  for (let i = 0; i < turns.length; i++) {
    const turn = turns[i];
    const prevBlock = blocks[blocks.length - 1];

    // ── 用户消息 ──
    if (turn.userMessage.text.trim()) {
      blocks.push({
        id: `${turn.turnId}:user`,
        turnId: turn.turnId,
        role: 'user',
        content: turn.userMessage.text,
        status: turn.userMessage.status === 'sending' ? 'sending' : 'success',
        createdAt: turn.userMessage.timestamp,
        userName: currentUser?.name || '我',
        userAvatarUrl: currentUser?.avatar,
      });
    }

    // ── Agent 消息 ──
    const hasContent = Boolean(turn.assistant.answerMarkdown) ||
      turn.assistant.isStreaming ||
      turn.assistant.status === 'error' ||
      turn.assistant.status === 'cancelled';

    if (hasContent) {
      const blockAgentName = turn.source?.displayName || agentName || 'Pudding';
      const block: ChatMessageBlock = {
        id: `${turn.turnId}:assistant:0`,
        turnId: turn.turnId,
        role: 'agent',
        content: turn.assistant.answerMarkdown,
        status: toChatMessageStatus(turn.assistant.status),
        createdAt: turn.userMessage.timestamp,
        agentName: blockAgentName,
        agentAvatarUrl: turn.source?.avatarUrl,
        agentAvatarColor: turn.source?.avatarColor || '#7c3aed',
        agentAvatarEmoji: turn.source?.avatarEmoji || '🤖',
        processItems: turn.assistant.timelineItems?.length ? turn.assistant.timelineItems : undefined,
        usage: turn.assistant.usage,
        isStreaming: turn.assistant.isStreaming,
      };

      // 连续同 Agent 消息：视觉分组
      if (prevBlock && prevBlock.role === 'agent' && prevBlock.agentName === blockAgentName) {
        block.groupedWithPrevious = true;
      }

      blocks.push(block);
    }
  }

  return blocks;
}

function toChatMessageStatus(s: AssistantStatus): ChatMessageStatus {
  switch (s) {
    case 'thinking': return 'thinking';
    case 'executing': return 'thinking';
    case 'streaming': return 'streaming';
    case 'success': return 'success';
    case 'error': return 'error';
    case 'cancelled': return 'cancelled';
  }
}

export interface SessionGroup {
  label: string;
  items: { sessionId: string; title: string; timestamp: number; unreadCount?: number }[];
}

export interface SessionItem {
  sessionId: string;
  title: string;
  timestamp: number;
}

/** 消息来源描述（头像 + 名称 + 渠道） */
export interface ChatSource {
  sourceId: string;
  sourceType: 'agent' | 'websocket' | 'webhook' | 'email' | 'mqtt';
  displayName: string;
  avatarEmoji: string;
  avatarColor: string;
  avatarUrl?: string;
}

export const assistantStatusLabel: Record<AssistantStatus, string> = {
  thinking: '思考中',
  executing: '执行中',
  streaming: '生成中',
  success: '完成',
  error: '错误',
  cancelled: '已取消',
};

/** 子代理状态卡片 */
export type SubAgentCardStatus = 'spawning' | 'running' | 'completed' | 'failed';

export interface SubAgentCard {
  /** 该子代理对应的 turnId */
  turnId: string;
  subSessionId: string;
  templateId?: string;
  modelId?: string;
  taskSummary: string;
  status: SubAgentCardStatus;
  spawnedAt: number;
  completedAt?: number;
  /** 子代理完成后的输出摘要 */
  output?: string;
  /** 是否成功 */
  success?: boolean;
}

/** 子代理卡片注册表：turnId → SubAgentCard */
export type SubAgentCardMap = Record<string, SubAgentCard>;
