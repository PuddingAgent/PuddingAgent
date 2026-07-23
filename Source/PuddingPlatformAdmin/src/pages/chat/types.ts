// ── 聊天页共享类型 ─────────────────────────────────────────────
import type {
  TokenUsageDto,
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';

export type MessageStatus = 'sending' | 'success' | 'error';
export type AssistantStatus =
  | 'thinking'
  | 'executing'
  | 'streaming'
  | 'success'
  | 'error'
  | 'cancelled';
export type ChatMessageStatus =
  | 'sending'
  | 'thinking'
  | 'streaming'
  | 'success'
  | 'error'
  | 'cancelled';

/** 统一时间线条目：思考 / 工具调用 / 工具结果 / 潜意识步骤 / 子代理 */
export interface TimelineItem {
  id: string;
  type:
    | 'thinking'
    | 'tool_call'
    | 'tool_result'
    | 'subconscious_step'
    | 'subagent_spawned'
    | 'subagent_progress'
    | 'subagent_completed';
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
  /** 消息来源（agent / system command / websocket / webhook / email / mqtt） */
  source?: ChatSource;
  userMessage: {
    id: string;
    text: string;
    timestamp: number;
    status: MessageStatus;
    metadata?: Record<string, string>;
    /** ChatMessages.Id — 用于引用回复的结构化消息ID */
    dbMessageId?: number;
  };
  assistant: {
    id: string;
    status: AssistantStatus;
    /** 统一时间线：按 Agent 实际执行顺序排列 */
    timelineItems: TimelineItem[];
    answerMarkdown: string;
    isStreaming: boolean;
    usage?: TokenUsageDto;
    renderMode: 'legacy' | 'structured' | 'inbound' | 'heartbeat';
    quotedMessage?: ChatQuotedMessage;
    /** Agent 主动 TTS 标记：{ enabled: true, tts_text?: string } */
    voice?: { enabled?: boolean; tts_text?: string };
  };
}

export interface ChatQuotedMessage {
  sourceId: string;
  sourceName: string;
  sourceKind: 'user' | 'agent' | 'system';
  messageType?: string;
  content: string;
  createdAt: number;
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
  role: 'user' | 'agent' | 'system' | 'heartbeat';
  content: string;
  status: ChatMessageStatus;
  createdAt: number;
  metadata?: Record<string, string>;
  modality?: 'text' | 'voice' | 'camera' | 'image';
  /** 视觉制品 ID（image/camera modality），用于从后端加载图片 */
  visionArtifactId?: string;
  /** 同一条消息的全部视觉制品 ID。 */
  visionArtifactIds?: string[];

  /** 用户信息（仅 role='user' 时有效） */
  userName?: string;
  userAvatarUrl?: string;

  /** Agent 信息（仅 role='agent' 时有效） */
  agentId?: string;
  sourceType?: ChatSource['sourceType'];
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
  /** Agent 回复引用的入站消息，例如另一个 Agent 发来的任务/问题。 */
  quotedMessage?: ChatQuotedMessage;

  /** 流式渲染标记 */
  isStreaming?: boolean;
}

function isAssistantInProgress(status: AssistantStatus): boolean {
  return (
    status === 'thinking' || status === 'executing' || status === 'streaming'
  );
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

    // ── 用户消息 ──
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
              : turn.userMessage.metadata?.inputMode === 'image'
                ? 'image'
                : 'text',
        visionArtifactId: turn.userMessage.metadata?.visionArtifactId,
        visionArtifactIds: (
          turn.userMessage.metadata?.visionArtifactIds ??
          turn.userMessage.metadata?.visionArtifactId ??
          ''
        )
          .split(',')
          .map((id) => id.trim())
          .filter(Boolean),
        userName: currentUser?.name || '我',
        userAvatarUrl: currentUser?.avatar,
      });
    }

    // ── 心跳消息（系统主动检视）──
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

    // ── Agent 消息 ──
    const hasInboundQuoted = Boolean(turn.assistant.quotedMessage);
    const hasContent =
      hasInboundQuoted ||
      Boolean(turn.assistant.answerMarkdown) ||
      turn.assistant.isStreaming ||
      isAssistantInProgress(turn.assistant.status) ||
      turn.assistant.status === 'error' ||
      turn.assistant.status === 'cancelled';

    if (hasContent) {
      const blockAgentName = turn.source?.displayName || agentName || 'Pudding';
      const block: ChatMessageBlock = {
        id: `${turn.assistant.id}:assistant:0`,
        turnId: turn.turnId,
        role: 'agent',
        content: turn.assistant.answerMarkdown,
        status: toChatMessageStatus(turn.assistant.status),
        createdAt: turn.userMessage.timestamp,
        agentId: turn.source?.sourceId,
        sourceType: turn.source?.sourceType,
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

      // ADR: Agent 消息分组不得跨用户消息
      // 分组判断必须以 blocks 中最后一条 block（用户消息插入后）为准
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

function toChatMessageStatus(s: AssistantStatus): ChatMessageStatus {
  switch (s) {
    case 'thinking':
      return 'thinking';
    case 'executing':
      return 'thinking';
    case 'streaming':
      return 'streaming';
    case 'success':
      return 'success';
    case 'error':
      return 'error';
    case 'cancelled':
      return 'cancelled';
  }
}

export interface SessionListItem {
  sessionId: string;
  title: string;
  timestamp: number;
  unreadCount?: number;
  agentTemplateId?: string;
  channelId?: string;
  sessionRole?: string;
  principalKind?: string;
  principalId?: string;
}

export interface SessionGroup {
  label: string;
  items: SessionListItem[];
}

export interface SessionItem {
  sessionId: string;
  title: string;
  timestamp: number;
}

/** 消息来源描述（头像 + 名称 + 渠道） */
export interface ChatSource {
  sourceId: string;
  sourceType:
    | 'agent'
    | 'system_command'
    | 'websocket'
    | 'webhook'
    | 'email'
    | 'mqtt';
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
export type SubAgentCardStatus =
  | 'spawning'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'timed_out'
  | 'interrupted';

export interface SubAgentActivityDetail {
  kind: 'model_message' | 'reasoning_notice' | 'tool_input' | 'tool_output';
  label: string;
  content: string;
  truncated?: boolean;
}

/** canonical 子代理事件的安全、有界 UI 投影；不包含隐藏原始思维链。 */
export interface SubAgentActivity {
  eventId?: string;
  type: string;
  label: string;
  occurredAt: number;
  round?: number;
  toolName?: string;
  durationMs?: number;
  totalTokens?: number;
  error?: string;
  toolCallId?: string;
  details?: SubAgentActivityDetail[];
}

export interface SubAgentCard {
  /** 该子代理对应的 turnId */
  turnId: string;
  runId?: string;
  invocationId?: string;
  batchId?: string;
  subSessionId: string;
  /** 父会话 ID，用于会话切换时隔离子代理卡片 */
  parentSessionId?: string;
  parentTurnId?: string;
  parentRunId?: string;
  parentToolCallId?: string;
  templateId?: string;
  modelId?: string;
  providerId?: string;
  profileId?: string;
  originToolId?: string;
  role?: string;
  taskSummary: string;
  status: SubAgentCardStatus;
  phase?: string;
  currentRound?: number;
  maxRounds?: number;
  timeoutSeconds?: number;
  lastActivityAt?: number;
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
  llmDurationMs?: number;
  toolDurationMs?: number;
  toolCount?: number;
  failedToolCount?: number;
  activeToolName?: string;
  lastToolName?: string;
  error?: string;
  spawnedAt: number;
  completedAt?: number;
  /** 子代理完成后的输出摘要 */
  output?: string;
  /** 是否成功 */
  success?: boolean;
  /** 最近的运行活动，供运行坞和详情检查器消费。 */
  activities?: SubAgentActivity[];
}

/** 子代理卡片注册表：turnId → SubAgentCard */
export type SubAgentCardMap = Record<string, SubAgentCard>;
