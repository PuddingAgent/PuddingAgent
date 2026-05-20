// ─── Diagnostics 类型定义 (ADR-023 Phase 2) ─────────────────────

/** 运行时时间线条目 */
export type RuntimeTimelineItem = {
  id: string;
  kind: 'activity' | 'event' | 'session_frame' | 'subagent_run';
  component: string;
  operation: string;
  status: string;
  sessionId?: string;
  traceId?: string;
  runId?: string;
  eventId?: string;
  startedAtUtc: string;
  completedAtUtc?: string;
  durationMs?: number;
  summary?: string;
  error?: string;
  metadata?: Record<string, string>;
};

/** 分页时间线结果 */
export type PagedTimelineResult = {
  items: RuntimeTimelineItem[];
  page: number;
  pageSize: number;
  total: number;
};

/** 组件健康状态 */
export type ComponentHealthItem = {
  component: string;
  status: 'healthy' | 'degraded' | 'failing' | 'unknown';
  startedCount: number;
  succeededCount: number;
  failedCount: number;
  retriedCount: number;
  cancelledCount: number;
  lastSeenAtUtc?: string;
  lastError?: string;
};

/** 分页通用包装 */
export type PagedResult<T> = {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
};

/** 子代理运行摘要 */
export type SubAgentRunSummary = {
  runId: string;
  parentSessionId: string;
  subSessionId: string;
  workspaceId: string;
  agentInstanceId: string;
  templateId: string;
  status: string;
  startedAt: string;
  completedAt?: string;
  totalDurationMs: number;
  totalRounds: number;
  totalToolCalls: number;
  errorMessage?: string;
};

/** 子代理运行详情 */
export type SubAgentRunDetail = {
  summary: SubAgentRunSummary;
  task?: string;
  output?: string;
  llmProfiles?: string;
  trace?: string;
  eventCount: number;
  toolCallCount: number;
};

/** 事件统计 */
export type EventStats = {
  byStatus: { status: string; count: number }[];
  byComponent: { component: string; count: number }[];
};
