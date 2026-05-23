// ── UserMessageBubble：用户消息气泡（右对齐，带头像）────────
import React from 'react';
import { Avatar } from 'antd';
import { UserOutlined } from '@ant-design/icons';
import { useChatStyles } from '../styles';

interface UserMessageBubbleProps {
  content: string;
  createdAt: number;
  status: string;
  userName?: string;
  userAvatarUrl?: string;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

const UserMessageBubble: React.FC<UserMessageBubbleProps> = ({
  content,
  createdAt,
  status,
  userName,
  userAvatarUrl,
  formatTime,
  onContextMenu,
}) => {
  const { styles, cx } = useChatStyles();
  const isSending = status === 'sending';
  const displayName = userName || '我';
  const firstLetter = displayName.charAt(0).toUpperCase();

  return (
    <div className={styles.userMessageContainer}>
      <div className={styles.userMetaRow}>
        <span className={styles.userTimeText}>{formatTime(createdAt)}</span>
        <span className={styles.userNameText}>{displayName}</span>
      </div>
      <div className={styles.userBubbleRow}>
        <div className={styles.userBubbleArea}>
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
        <div className={styles.userAvatarShell}>
          {userAvatarUrl ? (
            <Avatar size={32} src={userAvatarUrl} className={styles.userAvatarImg} />
          ) : (
            <Avatar size={32} icon={<UserOutlined />} className={styles.userAvatarFallback} />
          )}
        </div>
      </div>
    </div>
  );
};

export default React.memo(UserMessageBubble);
