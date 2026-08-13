import dayjs from 'dayjs';
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
  /** P2#8：Focus view 开启时每 turn 折叠为一行，高度估计统一按紧凑行处理 */
  focusView?: boolean;
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
  focusView?: boolean,
): VirtualMessageHeightHint => {
  if (focusView) return 'compact';
  if (streaming) return 'streaming';
  if (content.length > 1800 || content.includes('```')) return 'rich';
  if (content.length < 120) return 'compact';
  return 'normal';
};

/** 消息本地日历日（YYYY-MM-DD），跨天判定依据 */
const getLocalDayKey = (timestamp: number): string =>
  dayjs(timestamp).format('YYYY-MM-DD');

/** 日期分隔线标签：今天 / 昨天 / MM-DD */
const getDividerLabel = (timestamp: number): string => {
  const day = dayjs(timestamp).startOf('day');
  const diffDays = dayjs().startOf('day').diff(day, 'day');
  if (diffDays === 0) return '今天';
  if (diffDays === 1) return '昨天';
  return dayjs(timestamp).format('MM-DD');
};

const pushDayDivider = (
  items: VirtualMessageItem[],
  dayKey: string,
  dayDividerCounts: Map<string, number>,
  createdAt: number,
) => {
  const count = (dayDividerCounts.get(dayKey) ?? 0) + 1;
  dayDividerCounts.set(dayKey, count);
  items.push({
    kind: 'divider',
    // 稳定 key：同一日期唯一（divider:YYYY-MM-DD）；历史回填/活跃态重排导致
    // 同一日期在序列中再次出现时追加序号，避免 React 重复 key。
    id: count === 1 ? `divider:${dayKey}` : `divider:${dayKey}:${count}`,
    createdAt,
    label: getDividerLabel(createdAt),
    heightHint: 'compact',
  });
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
      heightHint: getHeightHint(
        block.content,
        block.isStreaming,
        input.focusView,
      ),
    });
  }
  // 跨天分组：在每条消息前按本地日期插入 divider（首条消息前亦有）。
  // 仅按消息项判定，loader 项不参与；日期保持输入序列顺序（与虚拟滚动
  // 现有“保持 supplied order”约定一致），同一日期出现多次时各自插分隔线。
  let lastDayKey: string | null = null;
  const dayDividerCounts = new Map<string, number>();
  for (const messageItem of messageItemsById.values()) {
    const dayKey = getLocalDayKey(messageItem.createdAt);
    if (dayKey !== lastDayKey) {
      pushDayDivider(items, dayKey, dayDividerCounts, messageItem.createdAt);
      lastDayKey = dayKey;
    }
    items.push(messageItem);
  }

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
