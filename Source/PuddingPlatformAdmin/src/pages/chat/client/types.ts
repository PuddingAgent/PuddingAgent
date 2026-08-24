import type { ToolPresentationDto } from '@/services/platform/api';

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
  /** 工具调用身份（后端 toolCallId；tool_call/tool_result 精确配对用） */
  toolCallId?: string | null;
  /** 父调用 ID（TR-01 冻结字段；服务端穿透后可用）。 */
  parentToolCallId?: string | null;
  /** 工具调用耗时（服务端事实）。 */
  durationMs?: number | null;
  /** Tool-owned presentation intent（实时/历史同构）。 */
  presentation?: ToolPresentationDto | null;
  kind: string;
  status: string;
  text: string;
  timestamp: string;
  name?: string | null;
  arguments?: string | null;
  output?: string | null;
  exitCode?: number | null;
  message?: string | null;
  /**
   * 委派节点跨事件分组键（subagent 子 run id 或 sub_agent_id）。
   * 服务端 2026-08-24 起对 kind=delegation 提供；旧后端缺省。
   */
  delegationRunId?: string | null;
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
  /** 工具审批卡片（P0#1）。由 approval.requested / approval.resolved 事件投影。 */
  approvalCard?: ApprovalCardData | null;
  /** 计划卡片（P1#5 Plan 模式）。由 plan.proposal / plan.finalized 事件投影。 */
  planCard?: PlanCardData | null;
}

/** 工具审批请求的 UI 投影（P0#1 审批卡片）。 */
export interface ApprovalCardData {
  approvalId: string;
  toolName: string;
  description: string;
  riskLevel: 'low' | 'medium' | 'high' | 'critical';
  arguments?: Record<string, unknown>;
  status: 'pending' | 'approved' | 'denied';
  decision?: 'allow_once' | 'always_allow' | 'deny';
  reason?: string;
  requestedAt: string;
  expiresAt?: string;
}

/** 计划步骤（P1#5）。每步可编辑/删除/拖拽排序。 */
export interface PlanStepData {
  id: string;
  title: string;
  description?: string;
}

/** Plan 模式用户决定（EditablePlanCard 三按钮契约）。 */
export type PlanDecision = 'approve_and_build' | 'manual' | 'keep_planning';

/** 计划卡片的 UI 投影（P1#5 Plan 模式）。 */
export interface PlanCardData {
  planId: string;
  summary?: string;
  steps: PlanStepData[];
  status: 'pending' | 'finalized';
  decision?: PlanDecision;
  decidedAt?: string;
  requestedAt: string;
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
