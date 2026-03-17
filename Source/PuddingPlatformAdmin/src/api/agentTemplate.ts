import request from '@/utils/request'

export type AgentTemplateType = 'Service' | 'Task' | 'Audit'

export interface AgentTemplateDefinition {
  templateId: string
  name: string
  description?: string
  templateType: AgentTemplateType
  skillIds: string[]
  systemPrompt?: string
}

export function getAgentTemplateList() {
  return request({
    url: '/api/agenttemplate',
    method: 'get',
  }) as Promise<AgentTemplateDefinition[]>
}

export function getAgentTemplate(templateId: string) {
  return request({
    url: `/api/agenttemplate/${encodeURIComponent(templateId)}`,
    method: 'get',
  }) as Promise<AgentTemplateDefinition>
}
