// ── MessageStream：IM 风格消息流 ────────────────────────────
// 替代 MessageGroup 的 Runtime Timeline 容器。
// 将 ChatTurn[] 转换为 ChatMessageBlock[] 后渲染为 IM 风格的消息列表。
import React, { useMemo } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn } from '../types';
import { buildMessageBlocks } from '../types';
import MessageRow from './MessageRow';

interface MessageStreamProps {
  turns: ChatTurn[];
  sessionId?: string | null;
  /** 当前工作空间 ID，用于用户视觉消息的图片加载 */
  workspaceId?: string;
  agentName?: string;
  defaultAvatarUrl?: string;
  /** 当前登录用户信息，用于用户消息头像和名称 */
  currentUser?: { name?: string; avatar?: string };
  formatTime: (ts: number) => string;
  onContextMenu?: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  onDeleteTurn?: (turnId: string) => void;
}

const MessageStream: React.FC<MessageStreamProps> = ({
  turns,
  sessionId,
  workspaceId,
  agentName,
  defaultAvatarUrl,
  currentUser,
  formatTime,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onDeleteTurn,
}) => {
  const { styles } = useChatStyles();

  const blocks = useMemo(
    () => buildMessageBlocks(turns, agentName, currentUser),
    [turns, agentName, currentUser],
  );

  return (
    <div className={styles.messageStream}>
      {blocks.map((block) => (
                <MessageRow
          key={block.id}
          block={block}
          sessionId={sessionId}
          workspaceId={workspaceId}
          defaultAvatarUrl={defaultAvatarUrl}
          formatTime={formatTime}
          onContextMenu={onContextMenu}
          onRerunTurn={onRerunTurn}
          onPinTurn={onPinTurn}
          onDeleteTurn={onDeleteTurn}
        />
      ))}
    </div>
  );
};

const timelineItemEquals = (
  previous: ChatTurn['assistant']['timelineItems'][number],
  next: ChatTurn['assistant']['timelineItems'][number],
) =>
  previous.id === next.id &&
  previous.type === next.type &&
  previous.status === next.status &&
  previous.timestamp === next.timestamp &&
  previous.text === next.text &&
  previous.name === next.name &&
  previous.arguments === next.arguments &&
  previous.output === next.output &&
  previous.exitCode === next.exitCode &&
  previous.message === next.message;

const timelineEquals = (
  previous: ChatTurn['assistant']['timelineItems'],
  next: ChatTurn['assistant']['timelineItems'],
) =>
  previous === next ||
  (previous.length === next.length &&
    previous.every((item, index) => timelineItemEquals(item, next[index])));

const processSummaryEquals = (
  previous: ChatTurn['assistant']['processSummary'],
  next: ChatTurn['assistant']['processSummary'],
) =>
  previous === next ||
  (previous?.totalItems === next?.totalItems &&
    previous?.thinkingRounds === next?.thinkingRounds &&
    previous?.thinkingSteps === next?.thinkingSteps &&
    previous?.toolCalls === next?.toolCalls &&
    previous?.toolResults === next?.toolResults &&
    previous?.failedTools === next?.failedTools &&
    previous?.durationMs === next?.durationMs &&
    previous?.hasDetails === next?.hasDetails);

export default React.memo(MessageStream, (prev, next) => {
  if (prev.sessionId !== next.sessionId) return false;
  if (prev.turns.length !== next.turns.length) return false;
  // 正文尚未产生时，thinking/tool 事件是唯一的实时可见进度。
  // 这些字段不能被 memo 吞掉，否则 UI 会长期停留在首 Token 等待态。
  const prevLast = prev.turns[prev.turns.length - 1];
  const nextLast = next.turns[next.turns.length - 1];
  if (!prevLast || !nextLast) return prevLast === nextLast;
  return (
    prevLast.turnId === nextLast.turnId &&
    prevLast.assistant.answerMarkdown ===
      nextLast.assistant.answerMarkdown &&
    prevLast.assistant.status === nextLast.assistant.status &&
    prevLast.assistant.isStreaming === nextLast.assistant.isStreaming &&
    prevLast.assistant.processMessageId ===
      nextLast.assistant.processMessageId &&
    timelineEquals(
      prevLast.assistant.timelineItems,
      nextLast.assistant.timelineItems,
    ) &&
    processSummaryEquals(
      prevLast.assistant.processSummary,
      nextLast.assistant.processSummary,
    )
  );
});
