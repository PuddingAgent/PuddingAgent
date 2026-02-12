// ── UserMessageBubble：用户消息气泡（右对齐，带头像）────────

import { UserOutlined } from '@ant-design/icons';
import { Avatar } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface UserMessageBubbleProps {
  content: string;
  createdAt: number;
  status: string;
  modality?: 'text' | 'voice' | 'camera';
  userName?: string;
  userAvatarUrl?: string;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

const UserMessageBubble: React.FC<UserMessageBubbleProps> = ({
  content,
  createdAt,
  status,
  modality,
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
        {modality === 'voice' ? (
          <span className={styles.messageModalityBadge}>Voice</span>
        ) : null}
        {modality === 'camera' ? (
          <span className={styles.messageModalityBadge}>Vision</span>
        ) : null}
        <span className={styles.userNameText}>{displayName}</span>
      </div>
      <div className={styles.userBubbleRow}>
        <div className={styles.userBubbleArea}>
          <div
            className={cx(
              styles.userBubbleNew,
              isSending && styles.userBubbleSending,
            )}
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
            <Avatar
              size={32}
              src={userAvatarUrl}
              className={styles.userAvatarImg}
            />
          ) : (
            <Avatar
              size={32}
              icon={<UserOutlined />}
              className={styles.userAvatarFallback}
            />
          )}
        </div>
      </div>
    </div>
  );
};

export default React.memo(UserMessageBubble);
