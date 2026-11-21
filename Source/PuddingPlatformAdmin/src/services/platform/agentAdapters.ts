import { request } from '@umijs/max';

export interface AgentAdapterRow {
  tokenId: string;
  agentId: string;
  workspaceId: string;
  ak: string;
  name: string;
  version: number;
  expiresAtUtc: string;
  revokedAtUtc?: string | null;
  registeredAtUtc?: string | null;
  leaseExpiresAtUtc?: string | null;
  active: boolean;
  online: boolean;
}

export interface AgentAdapterCreateInput {
  name: string;
  workspaceId: string;
}

export interface AgentAdapterCredentials {
  agentId: string;
  workspaceId: string;
  ak: string;
  sk: string;
}

export function listAgentAdapters(page = 1, pageSize = 20) {
  return request<{ items: AgentAdapterRow[]; total: number }>('/api/admin/agent-adapters', {
    params: { page, pageSize },
  });
}

export function createAgentAdapter(data: AgentAdapterCreateInput) {
  return request<AgentAdapterCredentials>('/api/admin/agent-adapters', { method: 'POST', data });
}
