import type { AgentConversationView } from '../client/types';
import type { ChatTurn } from '../types';
import { buildMessageBlocks } from '../types';
import type { VirtualMessageHeightHint, VirtualMessageItem } from './types';

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

const getHeightHint = (
  content: string,
  streaming?: boolean,
): VirtualMessageHeightHint => {
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

  const blocks = buildMessageBlocks(
    input.turns,
    input.agentName,
    input.currentUser,
  );
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

  // `turns` is already the authoritative display sequence assembled by
  // MessageList (older history, canonical projection, then an unmatched live
  // run). Re-sorting the resulting blocks by their original timestamps moves
  // a long-running active status back above newer messages and also invalidates
  // the grouping metadata that buildMessageBlocks computed for this sequence.
  // Keep the supplied order so live run state remains at the current edge of
  // the conversation without changing its original timestamp/wait duration.

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
