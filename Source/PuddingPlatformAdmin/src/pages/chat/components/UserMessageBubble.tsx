// ── UserMessageBubble：用户消息气泡（右对齐，带头像）────────

import {
  CheckOutlined,
  CopyOutlined,
  PictureOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Avatar, Tooltip } from 'antd';
import dayjs from 'dayjs';
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';

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
  /** 消息元数据；失败态下取 metadata.error 作为 title 错误详情。 */
  metadata?: Record<string, string>;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

const MESSAGE_ENTRANCE_WINDOW_MS = 5_000;

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
  metadata,
  formatTime,
  onContextMenu,
}) => {
  const { styles, cx } = useChatMessageStyles();
  const [failedImageIds, setFailedImageIds] = React.useState<Set<string>>(
    () => new Set(),
  );
  // P1-4: hover 展示操作按钮（首次 hover 后保持挂载，同 AgentMessageBubble）
  const [showActions, setShowActions] = React.useState(false);
  const [actionsMounted, setActionsMounted] = React.useState(false);
  // P1-4: copy 成功 1s 反馈（ref 防重入 + 卸载保护，同 MessageActions 模式）
  const [copyPending, setCopyPending] = React.useState(false);
  const copyTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const mountedRef = React.useRef(true);
  React.useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  const isSending = status === 'sending';
  const isError = status === 'error';
  const messageAgeMs = Math.max(0, Date.now() - createdAt);
  const shouldAnimateEntrance =
    isSending || messageAgeMs <= MESSAGE_ENTRANCE_WINDOW_MS;
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

  // P1-4: 失败态 title 错误详情 —— 优先 metadata.error，缺省通用文案
  const errorDetail = React.useMemo(() => {
    const detail = metadata?.error;
    return detail && detail.trim()
      ? detail.trim()
      : '消息发送失败，请稍后重试';
  }, [metadata]);

  const revealActions = React.useCallback(() => {
    setActionsMounted(true);
    setShowActions(true);
  }, []);

  const hideActions = React.useCallback(() => {
    setShowActions(false);
  }, []);

  const handleCopy = React.useCallback(() => {
    navigator.clipboard.writeText(content).catch(() => {});
    if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    setCopyPending(true);
    copyTimerRef.current = setTimeout(() => {
      if (mountedRef.current) setCopyPending(false);
    }, 1000);
  }, [content]);

  return (
    <div
      className={styles.userMessageContainer}
      onMouseEnter={revealActions}
      onMouseLeave={hideActions}
    >
      <div className={styles.userMetaRow}>
        <span
          className={styles.userTimeText}
          title={dayjs(createdAt).format('YYYY-MM-DD HH:mm:ss')}
        >
          {formatTime(createdAt)}
        </span>
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
        <div
          className={cx(styles.userBubbleArea, styles.userMessageActionsHost)}
        >
          <div
            className={cx(
              styles.userBubbleNew,
              shouldAnimateEntrance && styles.userBubbleEntrance,
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
          {isError && (
            <span className={styles.userErrorText} title={errorDetail}>
              发送失败
            </span>
          )}
          {actionsMounted && content.trim() && (
            <div
              className={cx(
                styles.userMessageActions,
                showActions && styles.userMessageActionsVisible,
              )}
              onClick={(e) => e.stopPropagation()}
            >
              <Tooltip title="复制">
                <button
                  type="button"
                  className={styles.messageActionBtn}
                  onClick={handleCopy}
                  aria-label={copyPending ? '已复制' : '复制'}
                >
                  {copyPending ? <CheckOutlined /> : <CopyOutlined />}
                </button>
              </Tooltip>
            </div>
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
