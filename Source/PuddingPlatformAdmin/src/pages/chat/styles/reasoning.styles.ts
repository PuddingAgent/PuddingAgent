// ── 思维链预览样式 ─────────────────────────────────────────────
import { createStyles } from 'antd-style';

export const useReasoningStyles = createStyles(({ token }) => ({
  reasoningContainer: {
    position: 'relative',
    marginTop: 8,
    padding: '12px 16px',
    borderRadius: token.borderRadiusLG,
    background: 'rgba(139, 92, 246, 0.03)',
    border: '1px solid rgba(139, 92, 246, 0.1)',
    boxShadow: '0 0 32px rgba(139, 92, 246, 0.04)',
    maxWidth: '100%',
    overflow: 'hidden',
    transition: 'all 0.3s ease',
    backdropFilter: 'blur(8px)',
  },
  reasoningHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    marginBottom: 8,
  },
  reasoningIcon: {
    fontSize: 14,
    lineHeight: 1,
  },
  reasoningTitle: {
    fontSize: 12,
    fontWeight: 600,
    color: 'rgba(139, 92, 246, 0.75)',
    letterSpacing: '0.02em',
  },
  reasoningLines: {
    display: 'flex',
    flexDirection: 'column',
    gap: 3,
    marginBottom: 8,
  },
  reasoningLine: {
    fontSize: 12,
    lineHeight: 1.55,
    color: 'rgba(139, 92, 246, 0.45)',
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
    width: 6,
    height: 6,
    borderRadius: '50%',
    background: 'rgba(139, 92, 246, 0.5)',
    animation: 'reasoningGlowPulse 2s ease-in-out infinite',
  },
  reasoningLabel: {
    fontSize: 11,
    color: 'rgba(139, 92, 246, 0.35)',
    fontStyle: 'italic',
  },
}));
