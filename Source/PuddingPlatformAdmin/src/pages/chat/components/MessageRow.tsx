// ── MessageRow：单条消息行（路由到 User/Agent 气泡）────────
import React from 'react';
import { useChatStyles } from '../styles';
import type { ChatMessageBlock } from '../types';
import UserMessageBubble from './UserMessageBubble';
import AgentMessageBubble from './AgentMessageBubble';

interface MessageRowProps {
  block: ChatMessageBlock;
  defaultAvatarUrl?: string;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  onDeleteTurn?: (turnId: string) => void;
}

const MessageRow: React.FC<MessageRowProps> = ({
  block,
  defaultAvatarUrl,
  formatTime,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onDeleteTurn,
}) => {
  const { styles, cx } = useChatStyles();

  if (block.role === 'user') {
    return (
      <div className={cx(styles.messageRow, styles.messageRowUser)}>
        <UserMessageBubble
          content={block.content}
          createdAt={block.createdAt}
          status={block.status}
          formatTime={formatTime}
          onContextMenu={(e) => onContextMenu?.(e, block.turnId, 'user')}
        />
      </div>
    );
  }

  if (block.role === 'agent') {
    return (
      <div
        className={cx(
          styles.messageRow,
          styles.messageRowAgent,
          block.groupedWithPrevious && styles.messageRowGrouped,
        )}
        data-agent={block.agentName}
        data-streaming={block.isStreaming ? 'true' : undefined}
      >
        <AgentMessageBubble
          id={block.id}
          content={block.content}
          status={block.status}
          createdAt={block.createdAt}
          agentName={block.agentName || 'Pudding'}
          agentAvatarEmoji={block.agentAvatarEmoji}
          agentAvatarColor={block.agentAvatarColor}
          agentAvatarUrl={block.agentAvatarUrl || defaultAvatarUrl}
          processItems={block.processItems}
          usage={block.usage}
          groupedWithPrevious={block.groupedWithPrevious}
          isStreaming={block.isStreaming}
          formatTime={formatTime}
          turnId={block.turnId}
          onContextMenu={onContextMenu}
          onRerun={onRerunTurn ? () => onRerunTurn(block.turnId) : undefined}
          onPin={onPinTurn ? () => onPinTurn(block.turnId) : undefined}
          onDelete={onDeleteTurn ? () => onDeleteTurn(block.turnId) : undefined}
        />
      </div>
    );
  }

  // system 消息暂不渲染
  return null;
};

export default React.memo(MessageRow);
