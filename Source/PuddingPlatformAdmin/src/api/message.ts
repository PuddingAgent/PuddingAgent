import request from '@/utils/request'

export interface MessageIngressRequest {
  channelId: string
  userExternalId: string
  messageText: string
  workspaceId?: string
  sessionId?: string
}

export interface MessageIngressResponse {
  messageId: string
  sessionId: string
  routeDecisionId?: string
  reply?: string
  isSuccess: boolean
  errorMessage?: string
}

export function sendMessage(data: MessageIngressRequest) {
  return request({
    url: '/api/messageingress',
    method: 'post',
    data,
  }) as Promise<MessageIngressResponse>
}
