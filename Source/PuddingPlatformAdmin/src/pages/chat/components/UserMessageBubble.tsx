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
  /** 同一消息包含的全部图片；为空时回退到 visionArtifactId。 */
  visionArtifactIds?: string[];
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
  visionArtifactIds,
  workspaceId,
  userName,
  userAvatarUrl,
  formatTime,
  onContextMenu,
}) => {
  const { styles, cx } = useChatStyles();
  const [failedImageIds, setFailedImageIds] = React.useState<Set<string>>(
    () => new Set(),
  );
  const isSending = status === 'sending';
  const displayName = userName || '我';

  const isVisionModality = modality === 'image' || modality === 'camera';
  const artifactIds = React.useMemo(() => {
    const ids = visionArtifactIds?.length
      ? visionArtifactIds
      : visionArtifactId
        ? [visionArtifactId]
        : [];
    return Array.from(new Set(ids.filter(Boolean)));
  }, [visionArtifactId, visionArtifactIds]);

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
                <div className={styles.userVisionGallery}>
                  {artifactIds.length > 0 ? (
                    artifactIds.map((artifactId, index) => {
                      const visionSrc = workspaceId
                        ? `/api/workspaces/${encodeURIComponent(workspaceId)}/vision-artifacts/${encodeURIComponent(artifactId)}`
                        : undefined;
                      return visionSrc && !failedImageIds.has(artifactId) ? (
                        <img
                          key={artifactId}
                          src={visionSrc}
                          alt={`${content || '用户上传图片'} ${index + 1}/${artifactIds.length}`}
                          className={styles.userVisionImage}
                          onError={() =>
                            setFailedImageIds((current) => {
                              const next = new Set(current);
                              next.add(artifactId);
                              return next;
                            })
                          }
                        />
                      ) : (
                        <span
                          key={artifactId}
                          className={styles.userVisionImageFallback}
                        >
                          <PictureOutlined />
                          {visionSrc ? '图片加载失败' : '图片'}
                        </span>
                      );
                    })
                  ) : (
                    <span className={styles.userVisionImageFallback}>
                      <PictureOutlined />
                      图片
                    </span>
                  )}
                </div>
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
