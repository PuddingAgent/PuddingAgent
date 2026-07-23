// ── agent styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useAgentStyles = createStyles(({ token }) => ({
  agentStatusTag: {
    flexShrink: 0,
    minWidth: 38,
    padding: '2px 6px',
    borderRadius: 999,
    fontSize: 11,
    lineHeight: '16px',
    textAlign: 'center' as const,
    border: '1px solid transparent',
  },
  agentStatusTag_working: {
    color: '#8a4b00',
    background: '#fff4d6',
    borderColor: '#f2cf7a',
  },
  agentStatusTag_idle: {
    color: '#216e48',
    background: '#dcfce7',
    borderColor: '#9be5b7',
  },
  agentStatusTag_disabled: {
    color: 'var(--pudding-chat-text-subtle)',
    background: 'var(--pudding-chat-surface-muted)',
    borderColor: 'var(--pudding-chat-border)',
  },
  agentContent: { alignItems: 'flex-start' },
  agentRow: { justifyContent: 'flex-start' },
  agentBubble: {
    maxWidth: 'min(76%, 820px)',
    background: 'transparent',
    color: 'var(--text-primary)',
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    borderRadius: 8,
    borderBottomLeftRadius: 4,
    '&:hover': {
      background: 'color-mix(in srgb, var(--soft-white) 50%, transparent)',
    },
  },
  assistantAnswer: {
    maxWidth: 'min(82%, 880px)',
    background: 'var(--soft-white)',
    color: 'var(--text-primary)',
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
    borderRadius: 8,
    borderBottomLeftRadius: 4,
    padding: '12px 16px',
    fontSize: 14,
  },
  assistantStatusMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    flexWrap: 'wrap' as const,
    fontSize: 12,
  },
  assistantStatusTag: {
    fontSize: 12,
    borderRadius: 999,
    padding: '2px 8px',
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    background: token.colorFillSecondary,
    color: token.colorTextSecondary,
  },
  agentThinking: {
    position: 'relative' as const,
  },
  agentSearching: {
    position: 'relative' as const,
  },
  agentRecall: {},

  /* ── Error ── */
  agentError: {
    animation: 'glitchShake 0.4s ease-in-out',
    borderLeft: '3px solid #ff4d4f',
  },
  agentSuccess: {
    animation: 'softDiffuse 1s ease-in-out 1',
  },
  agentAvatarWrapper: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    fontSize: 16,
    userSelect: 'none' as const,
    overflow: 'hidden',
    marginTop: 18,
    marginRight: 10,
  },
  agentAvatarImg: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    objectFit: 'cover' as const,
  },
  agentAvatarGrouped: {
    visibility: 'hidden' as const,
    marginRight: 10,
    width: 32,
    flexShrink: 0,
  },
  agentNameRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    marginBottom: 2,
    paddingLeft: 4,
    minHeight: 20,
  },
  agentNameText: {
    fontSize: 13,
    fontWeight: 600,
    color: 'var(--earth-brown)',
    lineHeight: '20px',
  },
  agentTimeText: {
    fontSize: 11,
    color: 'var(--earth-brown)',
    opacity: 0.5,
    lineHeight: '20px',
  },
  agentBubbleNew: {
    background: 'var(--soft-white)',
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
    borderRadius: 8,
    borderTopLeftRadius: 4,
    padding: '12px 16px',
    fontSize: 14,
    lineHeight: 1.7,
    color: 'var(--text-primary)',
    wordBreak: 'break-word' as const,
    width: '100%',
    contain: 'layout paint style',
    transition: 'background 200ms ease, border-color 200ms ease',
    '&:hover': {
      background: 'color-mix(in srgb, var(--soft-white) 95%, transparent)',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    },
  },
  agentBubbleGrouped: {
    borderTopLeftRadius: 8,
    borderTop: '1px solid',
    borderTopColor: 'color-mix(in srgb, var(--earth-brown) 4%, transparent)',
  },
  agentBubbleStreaming: {
    borderColor: 'color-mix(in srgb, var(--accent-purple) 20%, transparent)',
  },
    agentWaitingBubble: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    width: 'fit-content',
    minHeight: 44,
    background: 'color-mix(in srgb, var(--soft-white) 85%, transparent)',
    backdropFilter: 'blur(6px)',
  },
  waitingDots: {
    display: 'flex',
    alignItems: 'center',
    gap: 5,
    height: 20,
  },
  waitingDot: {
    width: 7,
    height: 7,
    borderRadius: '50%',
    background: 'var(--accent-purple)',
    opacity: 0.4,
    animation: 'waitingBounce 1.4s ease-in-out infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      opacity: 0.55,
    },
  },
  waitingDotSlow: {
    background: '#d97706',
    boxShadow: '0 0 6px rgba(217,119,6,.45)',
  },
  waitingLabel: {
    fontSize: 13,
    color: 'color-mix(in srgb, var(--accent-purple) 48%, var(--text-secondary))',
    fontStyle: 'italic',
    lineHeight: '20px',
  },
  waitingLabelWarning: {
    color: 'color-mix(in srgb, #d97706 65%, var(--text-secondary))',
  },
  '@keyframes waitingBounce': {
    '0%, 80%, 100%': {
      transform: 'translateY(0) scale(0.7)',
      opacity: 0.35,
    },
    '40%': {
      transform: 'translateY(-7px) scale(1)',
      opacity: 0.9,
    },
  },
  agentBubbleError: {
    borderColor: 'color-mix(in srgb, #ef4444 30%, transparent)',
    background: 'color-mix(in srgb, #ef4444 4%, var(--soft-white))',
  },
  // E2: 流式停滞警告（琥珀色慢脉冲边框）
  agentBubbleWarning: {
    borderColor: 'color-mix(in srgb, #d97706 35%, transparent)',
    animation: 'stallPulse 2s ease-in-out infinite',
  },
  pulseDotWarning: {
    background: '#d97706',
    boxShadow: '0 0 8px rgba(217,119,6,.5)',
  },
  agentQuotedMessage: {
    margin: '0 0 10px',
    padding: '8px 10px',
    borderLeft:
      '3px solid color-mix(in srgb, var(--pudding-chat-accent) 35%, var(--pudding-chat-border))',
    borderRadius: 6,
    background:
      'color-mix(in srgb, var(--pudding-chat-surface-muted) 72%, transparent)',
    color: 'var(--pudding-chat-text-muted)',
  },
  agentQuotedMessageHeader: {
    marginBottom: 4,
    fontSize: 12,
    fontWeight: 600,
    color: 'var(--earth-brown)',
    opacity: 0.78,
  },
  agentQuotedMessageBody: {
    display: '-webkit-box',
    WebkitBoxOrient: 'vertical' as const,
    WebkitLineClamp: 4,
    overflow: 'hidden',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    fontSize: 13,
    lineHeight: 1.55,
  },
  agentActiveOutputSurface: {
    borderColor:
      'color-mix(in srgb, var(--accent-purple) 26%, var(--earth-brown) 10%)',
    boxShadow:
      '0 0 0 1px color-mix(in srgb, var(--accent-purple) 8%, transparent), 0 0 18px color-mix(in srgb, var(--accent-purple) 10%, transparent)',
    animation: 'agentActiveOutputGlow 2.8s ease-in-out infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      boxShadow:
        '0 0 0 1px color-mix(in srgb, var(--accent-purple) 8%, transparent)',
    },
  },
  '@keyframes agentActiveOutputGlow': {
    '0%, 100%': {
      borderColor:
        'color-mix(in srgb, var(--accent-purple) 20%, var(--earth-brown) 8%)',
      boxShadow:
        '0 0 0 1px color-mix(in srgb, var(--accent-purple) 5%, transparent), 0 0 10px color-mix(in srgb, var(--accent-purple) 7%, transparent)',
    },
    '50%': {
      borderColor:
        'color-mix(in srgb, var(--accent-purple) 34%, var(--earth-brown) 12%)',
      boxShadow:
        '0 0 0 1px color-mix(in srgb, var(--accent-purple) 11%, transparent), 0 0 22px color-mix(in srgb, var(--accent-purple) 14%, transparent)',
    },
  },
}));
