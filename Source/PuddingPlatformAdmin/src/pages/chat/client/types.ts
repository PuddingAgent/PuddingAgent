export type AgentRunStatus =
  | 'queued'
  | 'running'
  | 'waiting'
  | 'succeeded'
  | 'failed'
  | 'cancelled';

export interface AgentStatusProjection {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  status: 'idle' | 'running' | 'waiting' | 'failed' | 'offline';
  activeRunId?: string | null;
  summary: string;
  unreadCount: number;
  eventCursor: number;
  updatedAt: string;
}

export interface ProcessSummaryItem {
  id: string;
  kind: string;
  status: string;
  text: string;
  timestamp: string;
  name?: string | null;
  arguments?: string | null;
  output?: string | null;
  exitCode?: number | null;
  message?: string | null;
}

export interface ConversationProcessSummary {
  totalItems: number;
  thinkingRounds: number;
  thinkingSteps: number;
  toolCalls: number;
  toolResults: number;
  failedTools: number;
  durationMs: number;
  hasDetails: boolean;
}

export interface MessageProcessDetailsView {
  messageId: string;
  runId?: string | null;
  processItems: ProcessSummaryItem[];
}

export interface AgentOutputSnapshot {
  markdown: string;
  processItems: ProcessSummaryItem[];
  processSummary?: ConversationProcessSummary | null;
}

export interface AgentRunView {
  runId: string;
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  commandClientId?: string | null;
  status: AgentRunStatus;
  statusText: string;
  summary: string;
  eventCursor: number;
  outputSnapshot: AgentOutputSnapshot;
  startedAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface ConversationMessageView {
  messageId: string;
  turnId?: string | null;
  runId?: string | null;
  role: 'user' | 'agent' | 'system';
  sourceKind?: 'user' | 'agent' | 'system';
  sourceId: string;
  sourceName: string;
  messageType?:
    | 'user_message'
    | 'agent_message'
    | 'agent_reply'
    | 'agent_output'
    | 'system_event'
    | string;
  llmRole?: 'system' | 'user' | 'assistant' | 'tool' | string;
  createdAt: string;
  content: string;
  metadata?: Record<string, string>;
  status:
    | 'sending'
    | 'sent'
    | 'streaming'
    | 'succeeded'
    | 'failed'
    | 'cancelled';
  processItems: ProcessSummaryItem[];
  processSummary?: ConversationProcessSummary | null;
}

export interface AgentConversationView {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
  messages: ConversationMessageView[];
  activeRun?: AgentRunView | null;
  eventCursor: number;
  updatedAt: string;
}
