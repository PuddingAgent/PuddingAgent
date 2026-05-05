// ── 聊天页共享类型 ─────────────────────────────────────────────
import type { TokenUsageDto, WorkspaceAgentDto, WorkspaceWithPermDto } from '@/services/platform/api';

export type MessageStatus = 'sending' | 'success' | 'error';
export type AssistantStatus = 'thinking' | 'executing' | 'streaming' | 'success' | 'error' | 'cancelled';

export interface ReasoningBlock {
  id: string;
  text: string;
  collapsed: boolean;
}

export interface StepCard {
  id: string;
  status: string;
  message: string;
  timestamp: number;
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
    reasoningBlocks: ReasoningBlock[];
    stepCards: StepCard[];
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
