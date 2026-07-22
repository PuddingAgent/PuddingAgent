// ── UserMessageBubble：用户消息气泡（右对齐，带头像）────────

import { PictureOutlined, UserOutlined } from '@ant-design/icons';
import { Avatar } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface UserMessageBubbleProps {
  content: string;
  createdAt: number;
  status: string;
  modality?: 'text' | 'voice' | 'camera' | 'image';
  /** 视觉制品 ID，用于从后端加载图片（image/camera modality） */
  visionArtifactId?: string;
  /** 当前工作空间 ID，用于拼接视觉制品 GET 地址 */
  workspaceId?: string;
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
  visionArtifactId,
  workspaceId,
  userName,
  userAvatarUrl,
  formatTime,
  onContextMenu,
}) => {
  const { styles, cx } = useChatStyles();
  const [imageFailed, setImageFailed] = React.useState(false);
  const isSending = status === 'sending';
  const displayName = userName || '我';

  const isVisionModality = modality === 'image' || modality === 'camera';
  const visionSrc =
    isVisionModality && visionArtifactId && workspaceId
      ? `/api/workspaces/${encodeURIComponent(workspaceId)}/vision-artifacts/${encodeURIComponent(visionArtifactId)}`
      : undefined;

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
        {modality === 'image' ? (
          <span className={styles.messageModalityBadge}>Image</span>
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
            {isVisionModality ? (
              <div className={styles.userVisionImageWrap}>
                {visionSrc && !imageFailed ? (
                  <img
                    src={visionSrc}
                    alt={content || '用户上传图片'}
                    className={styles.userVisionImage}
                    onError={() => setImageFailed(true)}
                  />
                ) : (
                  <span className={styles.userVisionImageFallback}>
                    <PictureOutlined />
                    {visionSrc ? '图片加载失败' : '图片'}
                  </span>
                )}
                {content ? <span>{content}</span> : null}
              </div>
            ) : (
              content
            )}
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
