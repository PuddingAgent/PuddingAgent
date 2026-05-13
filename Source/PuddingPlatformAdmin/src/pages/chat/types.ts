// ── 聊天页共享类型 ─────────────────────────────────────────────
import type { TokenUsageDto, WorkspaceAgentDto, WorkspaceWithPermDto } from '@/services/platform/api';

export type MessageStatus = 'sending' | 'success' | 'error';
export type AssistantStatus = 'thinking' | 'executing' | 'streaming' | 'success' | 'error' | 'cancelled';

/** 统一时间线条目：思考 / 工具调用 / 工具结果 / 潜意识步骤 */
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

export interface SessionGroup {
  label: string;
  items: { sessionId: string; title: string; timestamp: number }[];
}

export interface SessionItem {
  sessionId: string;
  title: string;
  timestamp: number;
}

export const assistantStatusLabel: Record<AssistantStatus, string> = {
  thinking: '思考中',
  executing: '执行中',
  streaming: '生成中',
  success: '完成',
  error: '错误',
  cancelled: '已取消',
};
