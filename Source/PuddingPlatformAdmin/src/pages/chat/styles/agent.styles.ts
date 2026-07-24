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
    position: 'relative' as const,
    isolation: 'isolate' as const,
    background: 'var(--soft-white)',
    border: '1px solid',
    borderColor:
      'color-mix(in srgb, var(--accent-purple) 9%, var(--earth-brown) 5%)',
    borderLeft:
      '2px solid color-mix(in srgb, var(--accent-purple) 22%, var(--earth-brown) 8%)',
    borderRadius: 10,
    borderTopLeftRadius: 5,
    padding: '12px 16px',
    fontSize: 14,
    lineHeight: 1.7,
    color: 'var(--text-primary)',
    wordBreak: 'break-word' as const,
    width: '100%',
    contain: 'layout style',
    boxShadow: '0 3px 12px rgba(63, 38, 95, 0.04), 0 1px 3px rgba(0,0,0,0.035)',
    transition:
      'background 200ms ease, border-color 200ms ease, box-shadow 200ms ease, transform 250ms cubic-bezier(0.34, 1.56, 0.64, 1)',
    '@media (hover: hover) and (pointer: fine)': {
      '&:hover': {
        background:
          'color-mix(in srgb, var(--accent-purple) 2%, var(--soft-white))',
        borderColor:
          'color-mix(in srgb, var(--accent-purple) 22%, var(--earth-brown) 6%)',
        boxShadow:
          '0 6px 18px rgba(63, 38, 95, 0.065), 0 0 0 1px rgba(139, 63, 232, 0.025)',
        transform: 'translateY(-1px) scale(1.006)',
      },
    },
    '@media (prefers-reduced-motion: reduce)': {
      '&:hover': { transform: 'none' },
    },
  },
  agentBubbleEntrance: {
    transformOrigin: 'bottom left',
    animation:
      'messageGlowIn 680ms ease-out, messageBounceIn 460ms cubic-bezier(0.22, 1, 0.36, 1)',
    animationFillMode: 'backwards',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
    },
  },
  agentBubbleGrouped: {
    borderTopLeftRadius: 8,
    borderTop: '1px solid',
    borderTopColor: 'color-mix(in srgb, var(--earth-brown) 4%, transparent)',
  },
  agentBubbleStreaming: {
    borderColor:
      'color-mix(in srgb, var(--accent-purple) 22%, var(--earth-brown) 5%)',
    boxShadow:
      '0 5px 18px rgba(63, 38, 95, 0.055), 0 0 18px 2px rgba(139, 63, 232, 0.07)',
  },
  agentWaitingBubble: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    width: 'fit-content',
    minHeight: 44,
    minWidth: 176,
    padding: '11px 16px',
    contain: 'layout style',
  },
  waitingDots: {
    display: 'flex',
    alignItems: 'center',
    gap: 5,
    height: 20,
    position: 'relative' as const,
    zIndex: 2,
  },
  waitingDot: {
    width: 7,
    height: 7,
    borderRadius: '50%',
    background: 'var(--accent-purple)',
    opacity: 0.5,
    animation: 'waitingBounce 1.4s ease-in-out infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      opacity: 0.6,
    },
  },
  waitingDotSlow: {
    background: '#d97706',
    boxShadow: '0 0 6px rgba(217,119,6,.45)',
  },
  waitingLabel: {
    fontSize: 13,
    color:
      'color-mix(in srgb, var(--accent-purple) 68%, var(--text-secondary))',
    fontStyle: 'italic',
    lineHeight: '20px',
    position: 'relative' as const,
    zIndex: 2,
  },
  waitingLabelWarning: {
    color: 'color-mix(in srgb, #d97706 65%, var(--text-secondary))',
  },
  /* ── P2: 等待粒子 (Waiting Particles) ── */
  particleContainer: {
    position: 'absolute' as const,
    inset: '-18px -14px -4px',
    pointerEvents: 'none' as const,
    zIndex: 1,
  },
  particleDot: {
    position: 'absolute' as const,
    borderRadius: '50%',
    background:
      'radial-gradient(circle at 35% 30%, #ffffff 0 10%, #c4b5fd 34%, #9f67dd 74%)',
    boxShadow:
      '0 0 5px 1px rgba(139, 63, 232, 0.3), 0 0 9px rgba(167, 139, 250, 0.14)',
    animationName: 'particleFloatUp',
    animationDuration: '2.4s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'cubic-bezier(0.22, 1, 0.36, 1)',
    opacity: 0,
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },

  /* ── P3: 完成粒子 (Completion Particles) ── */
  answerParticlesContainer: {
    position: 'absolute' as const,
    bottom: 10,
    right: 14,
    pointerEvents: 'none' as const,
    zIndex: 3,
  },
  answerParticle: {
    position: 'absolute' as const,
    borderRadius: '50%',
    background:
      'radial-gradient(circle at 35% 30%, #ffffff 0 10%, #c4b5fd 32%, #9f67dd 74%)',
    boxShadow:
      '0 0 4px 1px rgba(139, 63, 232, 0.32), 0 0 8px rgba(167, 139, 250, 0.16)',
    animation: 'particleBurst 640ms cubic-bezier(0.16, 1, 0.3, 1) forwards',
    opacity: 0,
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
    },
  },

  agentBubbleError: {
    borderColor: 'color-mix(in srgb, #ef4444 30%, transparent)',
    background: 'color-mix(in srgb, #ef4444 4%, var(--soft-white))',
  },
  // E2: 流式停滞警告（琥珀色慢脉冲边框）
  agentBubbleWarning: {
    borderColor: 'color-mix(in srgb, #d97706 23%, transparent)',
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
    position: 'relative' as const,
    isolation: 'isolate' as const,
    borderColor:
      'color-mix(in srgb, var(--accent-purple) 24%, var(--earth-brown) 6%)',
    boxShadow:
      '0 5px 18px rgba(63, 38, 95, 0.055), 0 0 18px 2px rgba(139, 63, 232, 0.07)',
    '&::after': {
      content: '""',
      position: 'absolute' as const,
      inset: 0,
      borderRadius: 'inherit',
      background:
        'linear-gradient(112deg, transparent 8%, rgba(167, 139, 250, 0.015) 35%, rgba(139, 63, 232, 0.055) 50%, rgba(167, 139, 250, 0.015) 64%, transparent 90%)',
      backgroundSize: '220% 100%',
      pointerEvents: 'none' as const,
      zIndex: 0,
      animation: 'agentActiveOutputGlow 2.8s ease-in-out infinite',
    },
    '& > *': {
      position: 'relative' as const,
      zIndex: 1,
    },
    '@media (prefers-reduced-motion: reduce)': {
      boxShadow:
        '0 3px 12px rgba(63, 38, 95, 0.04), 0 1px 3px rgba(0,0,0,0.035)',
      '&::after': {
        animation: 'none',
        opacity: 0.12,
      },
    },
  },
  '@keyframes waitingBounce': {
    '0%, 80%, 100%': { transform: 'translateY(0) scale(0.6)', opacity: 0.35 },
    '40%': { transform: 'translateY(-5px) scale(1)', opacity: 1 },
  },
  '@keyframes agentActiveOutputGlow': {
    '0%, 100%': {
      opacity: 0.16,
      backgroundPosition: '105% 0',
    },
    '50%': {
      opacity: 0.42,
      backgroundPosition: '-15% 0',
    },
  },
}));
