import type { AgentConversationView } from '../client/types';
import type { ChatTurn, SubAgentCardMap } from '../types';
import { buildMessageBlocks } from '../types';
import type { VirtualMessageItem, VirtualMessageHeightHint } from './types';

export interface BuildVirtualMessageItemsInput {
  turns: ChatTurn[];
  conversationView?: AgentConversationView | null;
  subAgentCards?: SubAgentCardMap;
  agentName: string;
  sessionId?: string | null;
  hasMoreBefore?: boolean;
  currentUser?: { name?: string; avatar?: string };
}

export interface BuildVirtualMessageItemsOutput {
  items: VirtualMessageItem[];
  firstMessageItemId?: string;
  lastMessageItemId?: string;
  activeItemId?: string;
}

const getSubAgentCreatedAt = (card: SubAgentCardMap[string]): number =>
  card.spawnedAt ?? card.completedAt ?? 0;

const getHeightHint = (content: string, streaming?: boolean): VirtualMessageHeightHint => {
  if (streaming) return 'streaming';
  if (content.length > 1800 || content.includes('```')) return 'rich';
  if (content.length < 120) return 'compact';
  return 'normal';
};

export function buildVirtualMessageItems(
  input: BuildVirtualMessageItemsInput,
): BuildVirtualMessageItemsOutput {
  const items: VirtualMessageItem[] = [];

  if (input.hasMoreBefore) {
    items.push({
      kind: 'loader',
      id: `loader:before:${input.sessionId ?? '__no_session__'}`,
      createdAt: Number.NEGATIVE_INFINITY,
      direction: 'before',
      heightHint: 'compact',
    });
  }

  const blocks = buildMessageBlocks(input.turns, input.agentName, input.currentUser);
  for (const block of blocks) {
    const prefix = block.role === 'user' ? 'message:user' : 'message:agent';
    items.push({
      kind: 'message',
      id: `${prefix}:${block.id}`,
      createdAt: block.createdAt,
      block,
      heightHint: getHeightHint(block.content, block.isStreaming),
    });
  }

  const subAgentGroups = new Map<
    string,
    { createdAt: number; cards: SubAgentCardMap[string][] }
  >();
  for (const card of Object.values(input.subAgentCards ?? {})) {
    const groupId =
      card.batchId ??
      card.invocationId ??
      card.parentToolCallId ??
      card.runId ??
      card.turnId;
    const createdAt = getSubAgentCreatedAt(card);
    const group = subAgentGroups.get(groupId);
    if (group) {
      group.cards.push(card);
      group.createdAt = Math.min(group.createdAt, createdAt);
    } else {
      subAgentGroups.set(groupId, { createdAt, cards: [card] });
    }
  }

  for (const [groupId, group] of subAgentGroups) {
    items.push({
      kind: 'subagent-anchor',
      id: `subagent-anchor:${groupId}`,
      createdAt: group.createdAt,
      cards: group.cards.sort((a, b) => a.spawnedAt - b.spawnedAt),
      heightHint: 'compact',
    });
  }

  const kindOrder = { loader: 0, message: 1, 'subagent-anchor': 2 };
  const roleOrder = { user: 0, agent: 1, system: 2, heartbeat: 3 };

  items.sort((a, b) => {
    const byTime = a.createdAt - b.createdAt;
    if (byTime !== 0) return byTime;

    const byKind = (kindOrder[a.kind] ?? 99) - (kindOrder[b.kind] ?? 99);
    if (byKind !== 0) return byKind;

    if (a.kind === 'message' && b.kind === 'message') {
      const byRole = (roleOrder[a.block.role] ?? 99) - (roleOrder[b.block.role] ?? 99);
      if (byRole !== 0) return byRole;
    }

    return a.id.localeCompare(b.id);
  });

  const messageItems = items.filter((item) => item.kind === 'message');
  const active = messageItems.find(
    (item) => item.kind === 'message' && item.block.isStreaming,
  );

  return {
    items,
    firstMessageItemId: messageItems[0]?.id,
    lastMessageItemId: messageItems[messageItems.length - 1]?.id,
    activeItemId: active?.id,
  };
}
