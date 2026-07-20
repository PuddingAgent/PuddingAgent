// ── markdown styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useMarkdownStyles = createStyles(({ token }) => ({
  markdownBody: {
    whiteSpace: 'normal' as const,
    '& p': { margin: '0 0 8px' },
    '& p:last-child': { marginBottom: 0 },
    '& ul, & ol': { paddingLeft: 22, margin: '6px 0' },
    '& blockquote': {
      margin: '8px 0',
      paddingLeft: 12,
      borderLeft: '2px solid var(--pale-yellow-sunlight)',
      color: 'var(--earth-brown)',
      opacity: 0.8,
    },
    '& a': {
      color: 'var(--sky-soft)',
      textDecoration: 'none',
      '&:hover': { textDecoration: 'underline' },
    },
    '& table': { borderCollapse: 'collapse' as const },
    '& th, & td': {
      border: `1px solid ${token.colorBorderSecondary}`,
      padding: '6px 10px',
      textAlign: 'left' as const,
    },
    '& th': { background: token.colorFillQuaternary },
  },
  markdownTableScroll: {
    maxWidth: '100%',
    overflowX: 'auto' as const,
    margin: '8px 0',
  },
  inlineCode: {
    padding: '1px 5px',
    borderRadius: 4,
    background: 'color-mix(in srgb, var(--misty-blue) 30%, transparent)',
    fontSize: '0.92em',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
  },
  codeBlockWrap: {
    position: 'relative' as const,
    margin: '10px 0',
    borderRadius: 8,
    overflow: 'hidden',
    background: 'color-mix(in srgb, var(--misty-blue) 30%, transparent)',
    '& pre': {
      margin: 0,
      padding: '14px 16px',
      overflowX: 'auto' as const,
      fontSize: 13,
      fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    },
  },
  codeCopyButton: {
    position: 'absolute' as const,
    top: 8,
    right: 8,
    zIndex: 1,
  },
  inkChunk: {
    display: 'inline' as const,
  },
  '@keyframes inkBloom': {
    '0%': { opacity: 0.35 },
    '100%': { opacity: 1 },
  },
  inkCursor: {
    display: 'inline-block' as const,
    width: 2,
    height: '1em',
    marginLeft: 2,
    verticalAlign: '-0.12em',
    background: 'color-mix(in srgb, var(--earth-brown) 55%, transparent)',
    animation: 'inkCursorBreath 1.4s ease-in-out infinite',
  },
  '@keyframes inkCursorBreath': {
    '0%, 100%': { opacity: 0.28 },
    '50%': { opacity: 0.75 },
  },
}));
