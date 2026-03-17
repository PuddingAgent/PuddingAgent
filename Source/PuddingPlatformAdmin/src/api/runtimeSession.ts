import request from '@/utils/request'

export interface SessionRuntimeRecord {
  sessionId: string
  agentInstanceId: string
  workspaceId: string
  agentTemplateId: string
  createdAt: string
  lastActiveAt: string
  turnCount: number
  isActive: boolean
  terminationReason?: string
}

export interface RuntimeSummary {
  utcNow: string
  totalSessions: number
  activeSessions: number
  expiredSessions: number
  activeAgents: number
}

export function getRuntimeSessionList() {
  return request({
    url: '/runtime-api/api/runtimesession',
    method: 'get',
  }) as Promise<SessionRuntimeRecord[]>
}

export function getRuntimeSession(sessionId: string) {
  return request({
    url: `/runtime-api/api/runtimesession/${encodeURIComponent(sessionId)}`,
    method: 'get',
  }) as Promise<SessionRuntimeRecord>
}

export function terminateRuntimeSession(sessionId: string) {
  return request({
    url: `/runtime-api/api/runtimesession/${encodeURIComponent(sessionId)}`,
    method: 'delete',
  })
}

export function getRuntimeSummary() {
  return request({
    url: '/runtime-api/api/runtimesession/summary',
    method: 'get',
  }) as Promise<RuntimeSummary>
}
