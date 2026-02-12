import { request } from '@umijs/max';
import type { AgentConversationView, AgentStatusProjection } from './types';

export async function listAgentStatuses(
  workspaceId: string,
): Promise<AgentStatusProjection[]> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/status`,
    { method: 'GET' },
  );
}

const isNotModifiedResponse = (error: unknown): boolean => {
  const responseStatus = (error as { response?: { status?: unknown } })
    ?.response?.status;
  const status = responseStatus ?? (error as { status?: unknown })?.status;
  return Number(status) === 304;
};

export async function getAgentConversation(
  workspaceId: string,
  agentId: string,
  knownCursor?: number,
): Promise<AgentConversationView | null> {
  const qs =
    knownCursor && knownCursor > 0 ? `?knownCursor=${knownCursor}` : '';
  const url = `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation${qs}`;
  try {
    return await request(url, { method: 'GET', skipErrorHandler: true });
  } catch (error) {
    if (isNotModifiedResponse(error)) return null;
    throw error;
  }
}
