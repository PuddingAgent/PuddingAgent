// ── 思维链预览样式 ─────────────────────────────────────────────
import { createStyles } from 'antd-style';

export const useReasoningStyles = createStyles(() => ({
  reasoningContainer: {
    position: 'relative',
    isolation: 'isolate' as const,
    width: '100%',
    minWidth: 'min(560px, calc(100vw - 128px))',
    maxWidth: 'min(720px, 100%)',
    boxSizing: 'border-box' as const,
    padding: '12px 16px',
    borderRadius: 10,
    borderTopLeftRadius: 5,
    background:
      'linear-gradient(135deg, color-mix(in srgb, var(--accent-purple) 2.5%, var(--soft-white)), var(--soft-white) 58%)',
    border:
      '1px solid color-mix(in srgb, var(--accent-purple) 18%, var(--earth-brown) 5%)',
    borderLeft:
      '2px solid color-mix(in srgb, var(--accent-purple) 38%, var(--earth-brown) 6%)',
    boxShadow:
      '0 5px 18px rgba(63, 38, 95, 0.055), 0 0 18px 2px rgba(139, 63, 232, 0.065)',
    overflow: 'hidden',
    transition:
      'background 240ms ease, border-color 240ms ease, box-shadow 240ms ease',
    animation:
      'messageGlowIn 680ms ease-out, messageBounceIn 460ms cubic-bezier(0.22, 1, 0.36, 1)',
    animationFillMode: 'backwards',
    transformOrigin: 'bottom left',
    '&::after': {
      content: '""',
      position: 'absolute' as const,
      inset: 0,
      borderRadius: 'inherit',
      background:
        'linear-gradient(112deg, transparent 8%, rgba(196, 181, 253, 0.015) 34%, rgba(139, 92, 246, 0.055) 50%, rgba(196, 181, 253, 0.015) 66%, transparent 92%)',
      backgroundSize: '220% 100%',
      pointerEvents: 'none' as const,
      zIndex: 0,
      animation: 'reasoningEnergySweep 2.8s ease-in-out infinite',
    },
    '& > *': {
      position: 'relative' as const,
      zIndex: 1,
    },
    '@media (max-width: 720px)': {
      minWidth: 0,
    },
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      '&::after': {
        animation: 'none',
        opacity: 0.14,
      },
    },
  },
  reasoningHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    marginBottom: 8,
  },
  reasoningIcon: {
    display: 'grid',
    placeItems: 'center',
    width: 22,
    height: 22,
    borderRadius: '50%',
    background: 'color-mix(in srgb, var(--accent-purple) 12%, transparent)',
    boxShadow: '0 0 12px rgba(139, 92, 246, 0.16)',
    fontSize: 13,
    lineHeight: 1,
  },
  reasoningTitle: {
    fontSize: 13,
    fontWeight: 650,
    color: 'color-mix(in srgb, var(--accent-purple) 82%, var(--text-primary))',
    letterSpacing: '0.02em',
  },
  reasoningLines: {
    display: 'flex',
    flexDirection: 'column',
    gap: 3,
    marginBottom: 8,
  },
  reasoningLine: {
    fontSize: 13,
    lineHeight: 1.6,
    color:
      'color-mix(in srgb, var(--accent-purple) 52%, var(--text-secondary))',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    animation: 'reasoningLineFadeIn 0.35s ease both',
  },
  reasoningFooter: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
  },
  reasoningDot: {
    width: 7,
    height: 7,
    borderRadius: '50%',
    background: 'rgba(139, 92, 246, 0.82)',
    animation: 'reasoningGlowPulse 2s ease-in-out infinite',
  },
  reasoningLabel: {
    fontSize: 12,
    color:
      'color-mix(in srgb, var(--accent-purple) 54%, var(--text-secondary))',
    fontStyle: 'italic',
  },
  '@keyframes reasoningEnergySweep': {
    '0%, 100%': { opacity: 0.14, backgroundPosition: '105% 0' },
    '50%': { opacity: 0.42, backgroundPosition: '-15% 0' },
  },
}));
