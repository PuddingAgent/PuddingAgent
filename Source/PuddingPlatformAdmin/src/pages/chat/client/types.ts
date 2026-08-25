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
   */
  delegationRunId?: string | null;
  /**
   * canonical 事件 sequence（服务端 2026-08-25 硬切为必填）。前端不得
   * 用数组下标或本地计数器补造，否则正文与行为节点无法跨来源精确交错。
   */
  sequence: number;
  /** canonical turnId / runId 透传（跨源合流别名归并用）。 */
  turnId?: string | null;
  runId?: string | null;
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
  /** 事件窗口边界（全量明细 hasMoreBefore=false；服务端 2026-08-25 起）。 */
  window?: TurnEventWindow | null;
}

/** Turn 事件窗口边界：快照截断（hasMoreBefore）与 sequence 游标对齐。 */
export interface TurnEventWindow {
  turnId: string;
  throughSequence: number;
  minSequence: number;
  maxSequence: number;
  hasMoreBefore: boolean;
}

export interface AgentOutputSnapshot {
  markdown: string;
  processItems: ProcessSummaryItem[];
  processSummary?: ConversationProcessSummary | null;
  /** 活动快照窗口：64 条只是最近活动，更早轨迹由完成态明细水合恢复。 */
  window?: TurnEventWindow | null;
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
