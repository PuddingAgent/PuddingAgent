import { createStyles } from 'antd-style';

export const useReasoningStyles = createStyles(() => ({
  disclosure: {
    width: '100%',
    minWidth: 0,
  },
  row: {
    position: 'relative' as const,
    display: 'grid',
    gridTemplateColumns: 'auto auto auto minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: 7,
    minHeight: 24,
    width: '100%',
    padding: '1px 4px',
    border: 0,
    borderRadius: 5,
    background: 'transparent',
    color: 'var(--text-secondary)',
    font: 'inherit',
    textAlign: 'left' as const,
    cursor: 'pointer',
    overflow: 'hidden',
    transition: 'background 140ms ease, color 140ms ease',
    '&:hover': {
      color: 'var(--text-primary)',
      background: 'color-mix(in srgb, var(--accent-purple) 5%, transparent)',
    },
    '&:focus-visible': {
      outline:
        '2px solid color-mix(in srgb, var(--accent-purple) 55%, transparent)',
      outlineOffset: 1,
    },
  },
  rowRunning: {
    '&::after': {
      content: '""',
      position: 'absolute' as const,
      inset: 0,
      pointerEvents: 'none' as const,
      background:
        'linear-gradient(100deg, transparent 15%, color-mix(in srgb, var(--accent-purple) 10%, transparent) 50%, transparent 85%)',
      backgroundSize: '220% 100%',
      animation: 'reasoningEnergySweep 2.4s ease-in-out infinite',
    },
    '@media (prefers-reduced-motion: reduce)': {
      '&::after': { animation: 'none', opacity: 0 },
    },
  },
  title: {
    position: 'relative' as const,
    zIndex: 1,
    fontSize: 13,
    fontWeight: 650,
    color: 'var(--text-primary)',
    whiteSpace: 'nowrap' as const,
  },
  separator: {
    position: 'relative' as const,
    zIndex: 1,
    color: 'var(--text-tertiary)',
    letterSpacing: 1,
  },
  summary: {
    position: 'relative' as const,
    zIndex: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
    fontSize: 13,
    lineHeight: 1.45,
  },
  chevron: {
    position: 'relative' as const,
    zIndex: 1,
    width: 16,
    textAlign: 'center' as const,
    color: 'var(--text-tertiary)',
    fontSize: 12,
  },
  body: {
    maxHeight: 300,
    margin: '4px 4px 8px 18px',
    padding: '8px 12px',
    overflow: 'auto',
    borderLeft:
      '1px solid color-mix(in srgb, var(--accent-purple) 24%, var(--border-subtle))',
    borderRadius: '0 6px 6px 0',
    background: 'color-mix(in srgb, var(--accent-purple) 3%, transparent)',
    color: 'var(--text-secondary)',
    fontFamily: 'inherit',
    fontSize: 12.5,
    lineHeight: 1.65,
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
  },
  '@keyframes reasoningEnergySweep': {
    '0%, 100%': { opacity: 0.12, backgroundPosition: '110% 0' },
    '50%': { opacity: 0.55, backgroundPosition: '-10% 0' },
  },
}));
