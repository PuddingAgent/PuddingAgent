import request from '@/utils/request'

export type ApprovalStatus = 'Pending' | 'Confirmed' | 'Rejected' | 'Expired'

export interface ApprovalRecord {
  approvalId: string
  sessionId: string
  workspaceId: string
  actionDescription: string
  status: ApprovalStatus
  confirmationCode?: string
  createdAt: string
  expiresAt?: string
  resolvedAt?: string
  resolvedBy?: string
}

export function getPendingApprovals() {
  return request({
    url: '/api/approval/pending',
    method: 'get',
  }) as Promise<ApprovalRecord[]>
}

export function getApproval(approvalId: string) {
  return request({
    url: `/api/approval/${encodeURIComponent(approvalId)}`,
    method: 'get',
  }) as Promise<ApprovalRecord>
}

export function confirmApproval(approvalId: string, confirmationCode: string, confirmedBy: string) {
  return request({
    url: `/api/approval/${encodeURIComponent(approvalId)}/confirm`,
    method: 'post',
    data: { confirmationCode, confirmedBy },
  })
}

export function rejectApproval(approvalId: string, rejectedBy: string) {
  return request({
    url: `/api/approval/${encodeURIComponent(approvalId)}/reject`,
    method: 'post',
    data: { rejectedBy },
  })
}
