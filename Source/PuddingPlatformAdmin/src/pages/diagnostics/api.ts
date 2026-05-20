// ─── Diagnostics API 客户端 (ADR-023 Phase 2) ────────────────────
import { request } from '@umijs/max';
import type {
  PagedTimelineResult,
  RuntimeTimelineItem,
  ComponentHealthItem,
  PagedResult,
  SubAgentRunSummary,
  SubAgentRunDetail,
  EventStats,
} from './types';

/** 获取运行时时间线（支持多维度过滤） */
export async function getRuntimeTimeline(params: {
  sessionId?: string;
  traceId?: string;
  component?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}) {
  return request<PagedTimelineResult>('/api/diagnostics/runtime/timeline', { params });
}

/** 获取指定会话的时间线 */
export async function getSessionTimeline(sessionId: string) {
  return request<RuntimeTimelineItem[]>(`/api/diagnostics/sessions/${encodeURIComponent(sessionId)}/timeline`);
}

/** 获取组件健康状态列表 */
export async function getComponentHealth() {
  return request<ComponentHealthItem[]>('/api/diagnostics/runtime/components');
}

/** 获取子代理运行列表 */
export async function getSubAgentRuns(params: {
  parentSessionId?: string;
  status?: string;
  limit?: number;
  offset?: number;
}) {
  return request<PagedResult<SubAgentRunSummary>>('/api/sub-agents/runs', { params });
}

/** 获取子代理运行详情 */
export async function getSubAgentRunDetail(runId: string) {
  return request<SubAgentRunDetail>(`/api/sub-agents/runs/${encodeURIComponent(runId)}`);
}

/** 获取事件统计 */
export async function getEventStats() {
  return request<EventStats>('/api/diagnostics/events/stats');
}
