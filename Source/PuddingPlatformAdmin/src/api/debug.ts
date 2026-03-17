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

export function getDebugSummary() {
  return request({
    url: '/api/debug/summary',
    method: 'get',
  }) as Promise<DebugSummary>
}
