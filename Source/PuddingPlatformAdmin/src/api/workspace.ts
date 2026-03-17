import request from '@/utils/request'

export interface ChannelBinding {
  channelId: string
  channelType: string
  defaultAgentTemplateId?: string
  allowedAgentTemplateIds: string[]
}

export interface WorkspaceDefinition {
  workspaceId: string
  name: string
  description?: string
  isEnabled: boolean
  isFrozen: boolean
  channelBindings: ChannelBinding[]
  agentTemplateIds: string[]
  auditAgentTemplateIds: string[]
}

export function getWorkspaceList() {
  return request({
    url: '/api/workspace',
    method: 'get',
  }) as Promise<WorkspaceDefinition[]>
}

export function getWorkspace(workspaceId: string) {
  return request({
    url: `/api/workspace/${encodeURIComponent(workspaceId)}`,
    method: 'get',
  }) as Promise<WorkspaceDefinition>
}

export function upsertWorkspace(workspaceId: string, workspace: Partial<WorkspaceDefinition>) {
  return request({
    url: `/api/workspace/${encodeURIComponent(workspaceId)}`,
    method: 'put',
    data: workspace,
  })
}

export function deleteWorkspace(workspaceId: string) {
  return request({
    url: `/api/workspace/${encodeURIComponent(workspaceId)}`,
    method: 'delete',
  })
}

export function reloadWorkspaces() {
  return request({
    url: '/api/workspace/reload',
    method: 'post',
  })
}
