/**
 * 阶段 D2：服务端统一打开/历史/过程分页契约（Plan §5.1–§5.3）。
 *
 * 注意：ASP.NET Core 当前未全局启用 JsonStringEnumConverter，故 DTO 中的枚举按**数字**
 * 序列化；这里显式给出冻结取值，禁止在组件里按字符串猜。
 */

export const ReadModelInitializationStates = {
  Uninitialized: 0,
  BaselineBuilding: 1,
  Partial: 2,
  Ready: 3,
} as const;

export const ConversationSyncStates = {
  Ready: 0,
  Syncing: 1,
} as const;

export const TurnTerminalStates = {
  Unknown: 0,
  Completed: 1,
  Failed: 2,
  Cancelled: 3,
} as const;

export const SummaryCompletenessStates = {
  Unavailable: 0,
  Partial: 1,
  Complete: 2,
} as const;

export const SubAgentRunLifecycleStates = {
  Created: 0,
  Running: 1,
  Completed: 2,
  Failed: 3,
  Cancelled: 4,
  TimedOut: 5,
  Interrupted: 6,
  BudgetExhausted: 7,
} as const;

/** 组件 ProcessSummary 的只读投影（camelCase JSON）。 */
export interface ConversationReadModelProcessSummary {
  contentAppendCount: number;
  thinkingAppendCount: number;
  toolCallRequestedCount: number;
  toolCallCompletedCount: number;
  toolCallFailedCount: number;
  subAgentRunCount: number;
  completeness: number;
  incompleteReason?: string | null;
  revision: number;
}

export interface ConversationTurnSummaryView {
  turnId: string;
  rootRunId?: string | null;
  userMessageId?: string | null;
  assistantMessageId?: string | null;
  terminalState: number;
  hasAssistantBody: boolean;
  process: ConversationReadModelProcessSummary;
  activePreview: string;
  activePreviewTruncated: boolean;
  firstSequence: number;
  lastSequence: number;
  revision: number;
}

export interface ConversationSubAgentSummaryView {
  runId: string;
  subAgentId?: string | null;
  parentRunId?: string | null;
  turnId?: string | null;
  state: number;
  firstSequence: number;
  lastSequence: number;
}

export interface ConversationRuntimeView {
  compactionRunning: boolean;
  compactionRunId?: string | null;
  compactionLastAttemptFailed: boolean;
  subAgentRuns: ConversationSubAgentSummaryView[];
  subAgentRunsOmitted: number;
  initialization: number;
  readModelVersion: number;
  revision: number;
}

export interface ConversationHistoryItem {
  messageId: string;
  role: string;
  turnId?: string | null;
  /** Unix 毫秒。 */
  createdAt: number;
  /** 服务端有界预览（短消息即全文；长消息需显式展开全文）。 */
  preview: string;
  hasMoreContent: boolean;
  contentLength: number;
  hasContentParts: boolean;
}

export interface ConversationHistoryPage {
  items: ConversationHistoryItem[];
  nextBeforeCreatedAt?: number | null;
  nextBeforeMessageId?: string | null;
  hasMore: boolean;
}

export interface ConversationOpenSnapshot {
  conversationId: string;
  /** 已投影位置 C；`eventHead >= snapshotCursor`。 */
  snapshotCursor: number;
  eventHead: number;
  initialization: number;
  syncState: number;
  retryAfterMs?: number | null;
  /** 表示层版本，用于 If-None-Match / 304。 */
  representationTag: string;
  history: ConversationHistoryPage;
  activeTurn?: ConversationTurnSummaryView | null;
  visibleTurns: ConversationTurnSummaryView[];
  runtime: ConversationRuntimeView;
  completeness: number;
  completenessReason?: string | null;
}

export interface ConversationProcessItem {
  sequence: number;
  eventId: string;
  type: string;
  preview: string;
  previewTruncated: boolean;
  originalPayloadBytes: number;
}

export interface ConversationProcessPage {
  items: ConversationProcessItem[];
  nextBeforeSequence?: number | null;
  hasMoreBefore: boolean;
  scannedBackThroughSequence: number;
  omittedNonDisplayable: number;
  /** none / item_limit / byte_budget / scan_budget（服务端 Truncation 枚举名）。 */
  truncation: string;
}

export interface ConversationOpenRequestOptions {
  messageLimit?: number;
  /** 上次响应 ETag；用于条件请求，304 时调用方返回 null。 */
  etag?: string | null;
  signal?: AbortSignal;
}

export interface ConversationHistoryRequestOptions {
  beforeCreatedAt?: number | null;
  beforeMessageId?: string | null;
  limit?: number;
  signal?: AbortSignal;
}

export interface ConversationProcessRequestOptions {
  turnId?: string | null;
  beforeSequence?: number | null;
  throughSequence?: number | null;
  limit?: number;
  signal?: AbortSignal;
}
