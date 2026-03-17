import request from '@/utils/request'

export interface DebugSummary {
  utcNow: string
  recentAuditCount: number
  recentAuditEvents: Array<{
    eventId: string
    eventType: string
    messageId?: string
    sessionId?: string
    timestamp: string
  }>
}

export interface DebugMetrics {
  utcNow: string
  session: { total: number; active: number; closed: number }
  approval: { pending: number }
  runtime: { totalNodes: number; onlineNodes: number; totalActiveSessionLoad: number }
  workspace: { total: number; enabled: number; frozen: number }
  audit: { recentCount: number; byType: Record<string, number> }
}

export interface SessionDebugSnapshot {
  session: any
  auditCount: number
  recentAudit: Array<{
    eventId: string
    eventType: string
    messageId?: string
    detail?: string
    timestamp: string
  }>
}

export interface MessageDebugSnapshot {
  messageId: string
  routeDecision?: any
  session?: any
  auditCount: number
  auditTimeline: Array<{
    eventId: string
    eventType: string
    detail?: string
    timestamp: string
  }>
  diagnosis: {
    routeSuccess?: boolean
    routeFailureReason?: string
    hasPermissionDenied: boolean
    hasRuntimeFailure: boolean
  }
}

export interface WorkspaceDebugSnapshot {
  workspace: {
    workspaceId: string
    name: string
    isEnabled: boolean
    isFrozen: boolean
    channelCount: number
    agentTemplateCount: number
    auditAgentCount: number
  }
  session: {
    total: number
    active: number
    frozen: number
    failed: number
  }
  approval: {
    pending: number
  }
  routing: {
    recentTotal: number
    recentFailures: number
    topFailureReasons: Array<{ reason: string; count: number }>
  }
  audit: {
    recentCount: number
    permissionDeniedCount: number
    runtimeFailedCount: number
  }
  workflow: {
    boundWorkflows: number
    potentialBlockerHint?: string
  }
}

export function getDebugSummary() {
  return request({
    url: '/api/debug/summary',
    method: 'get',
  }) as Promise<DebugSummary>
}

export function getDebugMetrics() {
  return request({
    url: '/api/debug/metrics',
    method: 'get',
  }) as Promise<DebugMetrics>
}

export function getSessionDebug(sessionId: string) {
  return request({
    url: `/api/debug/session/${encodeURIComponent(sessionId)}`,
    method: 'get',
  }) as Promise<SessionDebugSnapshot>
}

export function getMessageDebug(messageId: string) {
  return request({
    url: `/api/debug/message/${encodeURIComponent(messageId)}`,
    method: 'get',
  }) as Promise<MessageDebugSnapshot>
}

export function getWorkspaceDebug(workspaceId: string) {
  return request({
    url: `/api/debug/workspace/${encodeURIComponent(workspaceId)}`,
    method: 'get',
  }) as Promise<WorkspaceDebugSnapshot>
}
