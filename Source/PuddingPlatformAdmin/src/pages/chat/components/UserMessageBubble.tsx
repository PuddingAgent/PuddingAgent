// ── UserMessageBubble：用户消息气泡（右对齐）────────────────
import React from 'react';
import { useChatStyles } from '../styles';

interface UserMessageBubbleProps {
  content: string;
  createdAt: number;
  status: string;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

const UserMessageBubble: React.FC<UserMessageBubbleProps> = ({
  content,
  createdAt,
  status,
  formatTime,
  onContextMenu,
}) => {
  const { styles, cx } = useChatStyles();
  const isSending = status === 'sending';

  return (
    <div className={styles.userMessageContainer}>
      <div className={styles.userNameRow}>
        <span className={styles.userTimeText}>{formatTime(createdAt)}</span>
        <span className={styles.userNameText}>我</span>
      </div>
      <div
        className={cx(styles.userBubbleNew, isSending && styles.userBubbleSending)}
        onContextMenu={onContextMenu}
      >
        {content}
      </div>
      {isSending && (
        <span className={styles.userSendingIndicator}>发送中...</span>
      )}
    </div>
  );
};

export default React.memo(UserMessageBubble);
