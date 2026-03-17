import request from '@/utils/request'

export type AuditEventType =
  | 'MessageReceived'
  | 'SessionCreated'
  | 'SessionReused'
  | 'RouteDecision'
  | 'RouteFailure'
  | 'ApprovalRequested'
  | 'ApprovalConfirmed'
  | 'ApprovalRejected'
  | 'RuntimeDispatched'
  | 'RuntimeReplyReceived'
  | 'PermissionDenied'
  | 'WorkspaceFrozen'
  | 'WorkspaceResumed'
  | 'AgentInstanceCreated'
  | 'AgentExecutionCompleted'
  | 'AgentExecutionFailed'

export interface AuditEventRecord {
  eventId: string
  eventType: AuditEventType
  sessionId?: string
  messageId?: string
  workspaceId?: string
  agentTemplateId?: string
  approvalId?: string
  detail?: string
  timestamp: string
}

export interface AuditQueryParams {
  sessionId?: string
  workspaceId?: string
  messageId?: string
  limit?: number
}

export function queryAuditEvents(params?: AuditQueryParams) {
  return request({
    url: '/api/audit',
    method: 'get',
    params,
  }) as Promise<AuditEventRecord[]>
}

export function getAuditEvent(eventId: string) {
  return request({
    url: `/api/audit/${encodeURIComponent(eventId)}`,
    method: 'get',
  }) as Promise<AuditEventRecord>
}
