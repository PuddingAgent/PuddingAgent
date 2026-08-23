// ── user styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useUserStyles = createStyles(() => ({
  userContent: { alignItems: 'flex-end' },
  userRow: { justifyContent: 'flex-end' },
  userBubble: {
    maxWidth: 'min(68%, 680px)',
    background:
      'color-mix(in srgb, var(--accent-purple) 10%, var(--soft-white))',
    color: 'var(--text-primary)',
    borderBottomRightRadius: 4,
    borderBottomLeftRadius: 8,
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 18%, transparent)',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--accent-purple) 14%, var(--soft-white))',
    },
  },
  sendingText: { color: 'var(--earth-brown)', opacity: 0.7, fontSize: 12 },
  userAvatarGroup: {
    display: 'flex',
    gap: 12,
    width: '100%',
    alignItems: 'flex-start',
    flexDirection: 'row-reverse' as const,
  },
  userMetaRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    marginBottom: 2,
    paddingRight: 40, // 为右侧头像留位，保持名称/时间与气泡对齐
    minHeight: 20,
  },
  userNameText: {
    fontSize: 13,
    color: 'var(--earth-brown)',
    opacity: 0.7,
    lineHeight: '20px',
  },
  userTimeText: {
    fontSize: 11,
    color: 'var(--earth-brown)',
    opacity: 0.5,
    lineHeight: '20px',
  },
  userBubbleRow: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: 8,
    maxWidth: '100%',
  },
  userBubbleArea: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-end',
    minWidth: 0,
    flex: '0 1 auto',
  },
  userBubbleNew: {
    position: 'relative' as const,
    // 对齐 harness：用户消息为右侧无边框大圆角气泡（底色取 Pudding accent 淡染）
    background:
      'color-mix(in srgb, var(--accent-purple) 9%, var(--soft-white))',
    border: 'none',
    // 行为链升级 §3.2：圆角对齐 harness 数值（22px），padding 10/16 已一致
    borderRadius: 22,
    padding: '10px 16px',
    fontSize: 14.5,
    lineHeight: 1.65,
    color: 'var(--text-primary)',
    wordBreak: 'break-word' as const,
    whiteSpace: 'pre-wrap' as const,
    contain: 'layout style',
    boxShadow: 'none',
    transition: 'background 200ms ease',
  },
  userBubbleEntrance: {
    transformOrigin: 'bottom right',
    animation:
      'messageGlowIn 680ms ease-out, messageBounceIn 460ms cubic-bezier(0.22, 1, 0.36, 1)',
    animationFillMode: 'backwards',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
    },
  },
  userBubbleSending: {
    opacity: 0.7,
  },
  /** 视觉输入（图片/摄像头）气泡内嵌图片 */
  userVisionImageWrap: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 6,
  },
  userVisionGallery: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 6,
    '&:has(> :only-child)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  userVisionImage: {
    display: 'block',
    maxWidth: 280,
    maxHeight: 200,
    width: '100%',
    borderRadius: 8,
    objectFit: 'cover' as const,
    cursor: 'zoom-in' as const,
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 22%, transparent)',
    transition: 'transform 200ms ease, box-shadow 200ms ease',
    '&:hover': {
      transform: 'scale(1.02)',
      boxShadow: '0 4px 14px rgba(0,0,0,0.12)',
    },
  },
  userVisionImageFallback: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    padding: '8px 12px',
    fontSize: 12,
    color: 'var(--earth-brown)',
    opacity: 0.7,
  },
  userSendingIndicator: {
    fontSize: 11,
    color: 'var(--earth-brown)',
    opacity: 0.5,
    marginTop: 2,
    paddingRight: 4,
  },
  userAvatarShell: {
    flex: '0 0 auto',
  },
  userAvatarImg: {
    border: '2px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 20%, transparent)',
  },
  userAvatarFallback: {
    background:
      'color-mix(in srgb, var(--accent-purple) 15%, var(--soft-white))',
    color: 'var(--accent-purple)',
    border: '2px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 20%, transparent)',
  },
  typewriterLiveText: {
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    overflowWrap: 'anywhere' as const,
  },
  liveTextSpan: {
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
  },
}));
