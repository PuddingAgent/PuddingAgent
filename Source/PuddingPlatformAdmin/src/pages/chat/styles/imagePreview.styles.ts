// ── image preview styles ─────────────────────────
import { createStyles } from 'antd-style';

/**
 * 全屏图片预览遮罩样式（点击消息内的图片后弹出）。
 *
 * 独立 style 域：消息叶子节点只订阅 messageStyleContext 的聚合样式，
 * 本文件由预览组件按需调用，不并入聚合域，避免每行消息订阅无关样式。
 *
 * zIndex 1050：在聊天内容之上、antd Modal/Message（1000±）之下。
 */
export const useImagePreviewStyles = createStyles(() => ({
  overlay: {
    position: 'fixed' as const,
    inset: 0,
    zIndex: 1050,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
    background: 'rgba(0, 0, 0, 0.72)',
    // 遮罩即关闭区：整屏 zoom-out 光标，点空白处关闭。
    cursor: 'zoom-out' as const,
  },
  image: {
    display: 'block',
    maxWidth: '92vw',
    maxHeight: '86vh',
    width: 'auto',
    height: 'auto',
    objectFit: 'contain' as const,
    borderRadius: 8,
    boxShadow: '0 12px 40px rgba(0, 0, 0, 0.45)',
    // 点图片本身不关闭。
    cursor: 'default' as const,
  },
  closeBtn: {
    position: 'absolute' as const,
    top: 16,
    right: 16,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 36,
    height: 36,
    padding: 0,
    border: 'none',
    borderRadius: '50%',
    background: 'rgba(255, 255, 255, 0.14)',
    color: 'rgba(255, 255, 255, 0.92)',
    fontSize: 16,
    cursor: 'pointer' as const,
    '&:hover': { background: 'rgba(255, 255, 255, 0.24)' },
    '&:focus-visible': {
      outline: '2px solid rgba(255, 255, 255, 0.7)',
      outlineOffset: 2,
    },
  },
  navBtn: {
    position: 'absolute' as const,
    top: '50%',
    transform: 'translateY(-50%)',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 40,
    height: 40,
    padding: 0,
    border: 'none',
    borderRadius: '50%',
    background: 'rgba(255, 255, 255, 0.14)',
    color: 'rgba(255, 255, 255, 0.92)',
    fontSize: 16,
    cursor: 'pointer' as const,
    '&:hover': { background: 'rgba(255, 255, 255, 0.24)' },
    '&:focus-visible': {
      outline: '2px solid rgba(255, 255, 255, 0.7)',
      outlineOffset: 2,
    },
  },
  navBtnPrev: { left: 16 },
  navBtnNext: { right: 16 },
  counter: {
    position: 'absolute' as const,
    bottom: 18,
    left: '50%',
    transform: 'translateX(-50%)',
    padding: '3px 10px',
    borderRadius: 10,
    background: 'rgba(0, 0, 0, 0.45)',
    color: 'rgba(255, 255, 255, 0.88)',
    fontSize: 12,
    lineHeight: '18px',
    letterSpacing: '0.04em',
  },
  /**
   * 可点击放大的行内图片包裹层（Markdown 制品图等）。
   * 只负责光标与焦点环，不设 display：包裹层可能同时带调用方的
   * artifactImageWrap（display: block），两个单类选择器的优先级相同，
   * 谁生效取决于样式域注入顺序 —— 这里不制造这种不确定冲突。
   */
  zoomable: {
    cursor: 'zoom-in' as const,
    '&:focus-visible': {
      outline: '2px solid var(--accent-purple)',
      outlineOffset: 2,
      borderRadius: 8,
    },
  },
}));
