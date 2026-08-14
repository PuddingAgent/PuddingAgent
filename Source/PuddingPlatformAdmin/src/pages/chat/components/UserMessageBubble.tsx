// ── UserMessageBubble：用户消息气泡（右对齐，带头像）────────

import {
  CheckOutlined,
  CopyOutlined,
  PictureOutlined,
  ReloadOutlined,
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

/**
 * P1-5 单图展示盒（对齐 deepseek-harness D9 MessageImage.singleFit）：
 * 长边 240px、宽高比 clamp [0.25, 4]（溢出由 object-fit: cover 裁切）、
 * 不放大超自然尺寸（scale ≤ 1）。宽高由图片自然尺寸决定，故用 JS 计算
 * 而非纯 CSS（aspect-ratio: clamp() 无法引用尚未加载的自然宽高比）。
 */
export function singleImageFit(
  naturalWidth: number,
  naturalHeight: number,
): { width: number; height: number } {
  const naturalRatio = naturalWidth / naturalHeight;
  const ratio = Math.min(4, Math.max(0.25, naturalRatio));
  const box =
    ratio >= 1
      ? { width: 240, height: 240 / ratio }
      : { width: 240 * ratio, height: 240 };
  const scale = Math.min(
    1,
    naturalWidth / box.width,
    naturalHeight / box.height,
  );
  return {
    width: Math.max(1, Math.round(box.width * scale)),
    height: Math.max(1, Math.round(box.height * scale)),
  };
}

interface VisionImageItemProps {
  artifactId: string;
  src?: string;
  alt: string;
  /** single：单图 240px 长边；tile：64px 方块 */
  variant: 'single' | 'tile';
  index: number;
}

/**
 * P1-5 单张用户图片：加载 shimmer 占位 → 加载后按规则显示；
 * 失败渲染与 tile/单图同尺寸的占位块 + 重试按钮，重试带 cache-bust。
 */
const VisionImageItem: React.FC<VisionImageItemProps> = ({
  artifactId,
  src,
  alt,
  variant,
  index,
}) => {
  const { styles } = useChatMessageStyles();
  const [dims, setDims] = React.useState<{
    width: number;
    height: number;
  } | null>(null);
  const [failed, setFailed] = React.useState(false);
  const [attempt, setAttempt] = React.useState(0);

  // 单图：加载完成后按自然尺寸注入 clamp 后的展示盒；失败/加载中保持
  // CSS 默认 240×240 占位，布局不跳动、不撑大。
  const frameStyle = React.useMemo<React.CSSProperties | undefined>(() => {
    if (variant !== 'single' || !dims) return undefined;
    const fit = singleImageFit(dims.width, dims.height);
    return { width: fit.width, height: fit.height };
  }, [variant, dims]);

  // 重试时追加 cache-bust query，避免浏览器复用失败的缓存响应。
  const effectiveSrc = React.useMemo(() => {
    if (!src) return undefined;
    if (attempt <= 0) return src;
    const separator = src.includes('?') ? '&' : '?';
    return `${src}${separator}retry=${attempt}`;
  }, [src, attempt]);

  const handleLoad = React.useCallback(
    (event: React.SyntheticEvent<HTMLImageElement>) => {
      const img = event.currentTarget;
      const width = img.naturalWidth;
      const height = img.naturalHeight;
      setDims(
        width > 0 && height > 0
          ? { width, height }
          : { width: 240, height: 240 },
      );
      setFailed(false);
    },
    [],
  );

  const handleError = React.useCallback(() => {
    setFailed(true);
  }, []);

  const handleRetry = React.useCallback(() => {
    setFailed(false);
    setDims(null);
    setAttempt((value) => value + 1);
  }, []);

  // 无工作空间时拿不到加载地址：直接渲染轻量占位，不进入加载/失败态。
  if (!effectiveSrc) {
    return (
      <span className={styles.userVisionImageFallback}>
        <PictureOutlined />
        图片
      </span>
    );
  }

  const isLoading = !failed && dims === null;

  return (
    <div
      className={
        variant === 'single' ? styles.userVisionImageSingle : styles.userVisionTile
      }
      style={frameStyle}
      data-testid={
        variant === 'single' ? 'user-vision-single' : `user-vision-tile-${index}`
      }
    >
      {isLoading ? (
        <span
          className={styles.userVisionImageLoading}
          data-testid={`user-vision-loading-${index}`}
        />
      ) : null}
      {failed ? (
        <button
          type="button"
          className={styles.userVisionRetryBtn}
          onClick={handleRetry}
          aria-label={`重新加载图片 ${index + 1}`}
          data-testid={`user-vision-retry-${index}`}
        >
          <ReloadOutlined />
          {variant === 'single' ? <span>重新加载</span> : null}
        </button>
      ) : (
        <img
          key={`${artifactId}:${attempt}`}
          src={effectiveSrc}
          alt={alt}
          className={
            variant === 'single'
              ? styles.userVisionImageSingleImg
              : styles.userVisionTileImg
          }
          onLoad={handleLoad}
          onError={handleError}
          data-testid={`user-vision-image-${index}`}
        />
      )}
    </div>
  );
};

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

  const visionSrcFor = React.useCallback(
    (artifactId: string) =>
      workspaceId
        ? `/api/workspaces/${encodeURIComponent(workspaceId)}/vision-artifacts/${encodeURIComponent(artifactId)}`
        : undefined,
    [workspaceId],
  );

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
                {artifactIds.length === 1 ? (
                  // P1-5: 单图 → 240px 长边展示盒
                  <VisionImageItem
                    artifactId={artifactIds[0]}
                    src={visionSrcFor(artifactIds[0])}
                    alt={`${content || '用户上传图片'} 1/1`}
                    variant="single"
                    index={0}
                  />
                ) : artifactIds.length > 1 ? (
                  // P1-5: 多图（≥2）→ 64px 方块 tile 网格
                  <div className={styles.userVisionTileGrid}>
                    {artifactIds.map((artifactId, index) => (
                      <VisionImageItem
                        key={artifactId}
                        artifactId={artifactId}
                        src={visionSrcFor(artifactId)}
                        alt={`${content || '用户上传图片'} ${index + 1}/${artifactIds.length}`}
                        variant="tile"
                        index={index}
                      />
                    ))}
                  </div>
                ) : (
                  <span className={styles.userVisionImageFallback}>
                    <PictureOutlined />
                    图片
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
