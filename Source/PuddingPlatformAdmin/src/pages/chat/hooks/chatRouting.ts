import type { WorkspaceAgentDto } from '@/services/platform/api';

export type ChatAudience = 'agent' | 'all';

export interface ResolvedChatRoute {
  originalText: string;
  messageText: string;
  audience: ChatAudience;
  targetAgentIds: string[];
  primaryAgentId?: string;
}

function getAgentLabel(agent: WorkspaceAgentDto): string {
  return agent.displayName || agent.name || agent.agentId;
}

function isAvailableAgent(agent: WorkspaceAgentDto): boolean {
  return agent.isEnabled !== false && agent.isFrozen !== true;
}

function normalizeMention(value: string): string {
  return value.trim().toLowerCase();
}

function findMentionedAgent(
  agents: WorkspaceAgentDto[],
  mention: string,
): WorkspaceAgentDto | undefined {
  const normalized = normalizeMention(mention);
  if (!normalized) return undefined;
  return agents.find((agent) => {
    const labels = [agent.agentId, agent.name, agent.displayName]
      .filter(Boolean)
      .map((value) => normalizeMention(String(value)));
    return labels.includes(normalized);
  });
}

export function resolveChatRoute(
  text: string,
  agents: WorkspaceAgentDto[],
  selectedAgentId?: string,
): ResolvedChatRoute {
  const originalText = text;
  const trimmedStart = text.trimStart();
  const leadingWhitespaceLength = text.length - trimmedStart.length;
  const mentionMatch = /^@([^\s@]+)(?:\s+|$)/.exec(trimmedStart);
  const availableAgents = agents.filter(isAvailableAgent);
  const fallbackAgentId =
    (selectedAgentId &&
    availableAgents.some((agent) => agent.agentId === selectedAgentId)
      ? selectedAgentId
      : undefined) ??
    availableAgents[0]?.agentId ??
    selectedAgentId;

  if (!mentionMatch) {
    return {
      originalText,
      messageText: text,
      audience: 'agent',
      targetAgentIds: fallbackAgentId ? [fallbackAgentId] : [],
      primaryAgentId: fallbackAgentId,
    };
  }

  const mention = mentionMatch[1];
  const mentionTokenLength = mentionMatch[0].length;
  const messageText = text
    .slice(leadingWhitespaceLength + mentionTokenLength)
    .trimStart();

  if (normalizeMention(mention) === 'all') {
    const targetAgentIds = availableAgents.map((agent) => agent.agentId);
    return {
      originalText,
      messageText,
      audience: 'all',
      targetAgentIds,
      primaryAgentId: targetAgentIds[0],
    };
  }

  const targetAgent = findMentionedAgent(availableAgents, mention);
  if (!targetAgent) {
    return {
      originalText,
      messageText: text,
      audience: 'agent',
      targetAgentIds: fallbackAgentId ? [fallbackAgentId] : [],
      primaryAgentId: fallbackAgentId,
    };
  }

  return {
    originalText,
    messageText,
    audience: 'agent',
    targetAgentIds: [targetAgent.agentId],
    primaryAgentId: targetAgent.agentId,
  };
}

export function getChatRouteLabel(
  route: ResolvedChatRoute,
  agents: WorkspaceAgentDto[],
): string {
  if (route.audience === 'all') return 'all';
  const agent = agents.find((item) => item.agentId === route.primaryAgentId);
  return agent ? getAgentLabel(agent) : route.primaryAgentId || 'Agent';
}
