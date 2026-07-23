// ── MessageRow：单条消息行（路由到 User/Agent/Heartbeat 气泡）──
import { HeartOutlined } from '@ant-design/icons';
import React from 'react';
import { useChatStyles } from '../styles';
import type { ChatMessageBlock } from '../types';
import AgentMessageBubble from './AgentMessageBubble';
import MessageItem from './MessageItem';
import UserMessageBubble from './UserMessageBubble';

interface MessageRowProps {
  block: ChatMessageBlock;
  sessionId?: string | null;
  /** 当前工作空间 ID，用于用户视觉消息的图片加载 */
  workspaceId?: string;
  defaultAvatarUrl?: string;
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

const MessageRow: React.FC<MessageRowProps> = ({
  block,
  sessionId,
  workspaceId,
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
          modality={block.modality}
          visionArtifactId={block.visionArtifactId}
          visionArtifactIds={block.visionArtifactIds}
          workspaceId={workspaceId}
          userName={block.userName}
          userAvatarUrl={block.userAvatarUrl}
          formatTime={formatTime}
          onContextMenu={(e) =>
            onContextMenu?.(e, block.turnId, 'user', block.content)
          }
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
          quotedMessage={block.quotedMessage}
          groupedWithPrevious={block.groupedWithPrevious}
          isStreaming={block.isStreaming}
          formatTime={formatTime}
          turnId={block.turnId}
          sessionId={sessionId}
          onContextMenu={onContextMenu}
          onRerun={onRerunTurn ? () => onRerunTurn(block.turnId) : undefined}
          onPin={onPinTurn ? () => onPinTurn(block.turnId) : undefined}
          onDelete={onDeleteTurn ? () => onDeleteTurn(block.turnId) : undefined}
        />
      </div>
    );
  }

  if (block.role === 'heartbeat') {
    return (
      <div className={cx(styles.messageRow, styles.messageRowHeartbeat)}>
        <div className={styles.heartbeatContainer}>
          <div className={styles.heartbeatHeader}>
            <HeartOutlined className={styles.heartbeatIcon} />
            <span className={styles.heartbeatLabel}>系统心跳</span>
          </div>
          <div className={styles.heartbeatBody}>
            <MessageItem markdownText={block.content} />
          </div>
        </div>
      </div>
    );
  }

  // system 消息暂不渲染
  return null;
};

export default React.memo(MessageRow);
