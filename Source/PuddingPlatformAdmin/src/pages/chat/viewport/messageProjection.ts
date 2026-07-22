import type { AgentConversationView } from '../client/types';
import type { ChatTurn } from '../types';
import { buildMessageBlocks } from '../types';
import type { VirtualMessageItem, VirtualMessageHeightHint } from './types';

export interface BuildVirtualMessageItemsInput {
  turns: ChatTurn[];
  conversationView?: AgentConversationView | null;
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
  const messageItemsById = new Map<string, VirtualMessageItem>();
  for (const block of blocks) {
    const prefix = block.role === 'user' ? 'message:user' : 'message:agent';
    const id = `${prefix}:${block.id}`;
    // A canonical Turn can legitimately contain more than one persisted
    // message. Virtual row identity therefore belongs to the message, not the
    // Turn. Replayed copies of the same message are idempotently replaced.
    messageItemsById.set(id, {
      kind: 'message',
      id,
      createdAt: block.createdAt,
      block,
      heightHint: getHeightHint(block.content, block.isStreaming),
    });
  }
  items.push(...messageItemsById.values());

  const kindOrder = { loader: 0, message: 1 };
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
