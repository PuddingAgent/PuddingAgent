import { CloseOutlined, LeftOutlined, RightOutlined } from '@ant-design/icons';
import React from 'react';
import { createPortal } from 'react-dom';
import { useImagePreviewStyles } from '../styles/imagePreview.styles';

export interface PreviewImageItem {
  src: string;
  alt: string;
}

interface ImagePreviewOverlayProps {
  /** 预览图集；多图时支持 ←/→ 与上一张/下一张按钮切换。 */
  items: PreviewImageItem[];
  /** 当前下标，越界时按首张处理。 */
  index: number;
  onIndexChange?: (index: number) => void;
  onClose: () => void;
}

/**
 * 全屏图片预览遮罩。
 *
 * 关闭方式：点遮罩空白处 / ESC / 右上角关闭按钮（三选一即可）。
 * 多图时：←/→ 键、左右按钮切换，底部显示 `当前/总数`。
 *
 * 用 portal 挂到 document.body：消息内的图片框架带 `contain: layout style`
 * （见 message.styles.ts），该属性会让元素成为 fixed 后代的包含块 —— 若就地
 * 渲染，遮罩会被裁剪到那个 240px/200px 的框内。
 */
export const ImagePreviewOverlay: React.FC<ImagePreviewOverlayProps> = ({
  items,
  index,
  onIndexChange,
  onClose,
}) => {
  const { styles, cx } = useImagePreviewStyles();
  const closeButtonRef = React.useRef<HTMLButtonElement | null>(null);
  const safeIndex = index >= 0 && index < items.length ? index : 0;
  const multi = items.length > 1;

  const go = React.useCallback(
    (delta: number) => {
      if (!multi || !onIndexChange) return;
      const next = (safeIndex + delta + items.length) % items.length;
      onIndexChange(next);
    },
    [items.length, multi, onIndexChange, safeIndex],
  );

  // 打开时把焦点移到关闭按钮，关闭时还原（不劫持调用方原有的焦点位置）。
  React.useEffect(() => {
    const previous =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    closeButtonRef.current?.focus();
    return () => {
      previous?.focus?.();
    };
  }, []);

  React.useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key === 'ArrowLeft') {
        event.preventDefault();
        go(-1);
        return;
      }
      if (event.key === 'ArrowRight') {
        event.preventDefault();
        go(1);
      }
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [go, onClose]);

  // 打开期间锁滚动，避免遮罩下方消息列表跟着滚动。
  React.useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, []);

  const current = items[safeIndex];
  if (!current) return null;
  if (typeof document === 'undefined') return null;

  return createPortal(
    // biome-ignore lint/a11y/noStaticElementInteractions: 遮罩空白区点击关闭是预期交互，键盘等价物为 ESC。
    <div
      className={styles.overlay}
      role="dialog"
      aria-modal="true"
      aria-label="图片预览"
      data-testid="image-preview-overlay"
      onClick={onClose}
    >
      <button
        ref={closeButtonRef}
        type="button"
        className={styles.closeBtn}
        aria-label="关闭图片预览"
        data-testid="image-preview-close"
        onClick={(event) => {
          event.stopPropagation();
          onClose();
        }}
      >
        <CloseOutlined />
      </button>
      {multi ? (
        <>
          <button
            type="button"
            className={cx(styles.navBtn, styles.navBtnPrev)}
            aria-label="上一张"
            data-testid="image-preview-prev"
            onClick={(event) => {
              event.stopPropagation();
              go(-1);
            }}
          >
            <LeftOutlined />
          </button>
          <button
            type="button"
            className={cx(styles.navBtn, styles.navBtnNext)}
            aria-label="下一张"
            data-testid="image-preview-next"
            onClick={(event) => {
              event.stopPropagation();
              go(1);
            }}
          >
            <RightOutlined />
          </button>
        </>
      ) : null}
      <img
        key={current.src}
        className={styles.image}
        src={current.src}
        alt={current.alt}
        data-testid="image-preview-image"
        onClick={(event) => event.stopPropagation()}
      />
      {multi ? (
        <span className={styles.counter} data-testid="image-preview-counter">
          {safeIndex + 1} / {items.length}
        </span>
      ) : null}
    </div>,
    document.body,
  );
};

interface ZoomableImageProps {
  src: string;
  alt: string;
  /** 图片本体 class（沿用调用方既有样式，不改变既有视觉）。 */
  imgClassName?: string;
  /** 外层可点击包裹层 class。 */
  className?: string;
  /** 未提供时用预览专用 zoom-in 包裹层样式。 */
  wrapperClassName?: string;
  loading?: 'lazy' | 'eager';
}

/**
 * 单图点击放大：自带开关状态，调用方无需持有 state。
 * 多图的图集切换交给 UserMessageBubble 统一管理（见那里的一处 overlay）。
 */
export const ZoomableImage: React.FC<ZoomableImageProps> = ({
  src,
  alt,
  imgClassName,
  className,
  wrapperClassName,
  loading,
}) => {
  const { styles, cx } = useImagePreviewStyles();
  const [open, setOpen] = React.useState(false);
  const items = React.useMemo<PreviewImageItem[]>(() => [{ src, alt }], [src, alt]);

  return (
    <>
      <span
        className={cx(styles.zoomable, wrapperClassName, className)}
        role="button"
        tabIndex={0}
        aria-label={`放大查看：${alt}`}
        data-testid="zoomable-image"
        onClick={() => setOpen(true)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            setOpen(true);
          }
        }}
      >
        <img className={imgClassName} src={src} alt={alt} loading={loading} />
      </span>
      {open ? (
        <ImagePreviewOverlay
          items={items}
          index={0}
          onClose={() => setOpen(false)}
        />
      ) : null}
    </>
  );
};
