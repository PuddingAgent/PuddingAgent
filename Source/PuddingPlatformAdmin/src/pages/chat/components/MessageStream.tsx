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

export default React.memo(MessageStream, (prev, next) => {
  if (prev.sessionId !== next.sessionId) return false;
  if (prev.turns.length !== next.turns.length) return false;
  // 比较最后一轮：turnId / answerMarkdown 值 / status 任一变化则重渲染
  const prevLast = prev.turns[prev.turns.length - 1];
  const nextLast = next.turns[next.turns.length - 1];
  if (!prevLast || !nextLast) return prevLast === nextLast;
  return (
    prevLast.turnId === nextLast.turnId &&
    prevLast.assistant.answerMarkdown ===
      nextLast.assistant.answerMarkdown &&
    prevLast.assistant.status === nextLast.assistant.status
  );
});
