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
    // P0-3: hover 代码块时显隐复制按钮（attribute 选择器，避免依赖 hashed class 名）
    '&:hover [data-code-copy]': {
      opacity: 1,
    },
  },
  // P0-3: 左上角语言标签（11px、半透明、等宽）
  codeLanguageLabel: {
    position: 'absolute' as const,
    top: 8,
    left: 10,
    zIndex: 1,
    maxWidth: 'calc(100% - 96px)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
    fontSize: 11,
    lineHeight: '16px',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    color: 'var(--earth-brown)',
    opacity: 0.62,
    userSelect: 'none' as const,
    pointerEvents: 'none' as const,
  },
  codeCopyButton: {
    position: 'absolute' as const,
    top: 8,
    right: 8,
    zIndex: 1,
    // P0-3: 默认隐藏，hover 代码块或键盘聚焦（focus-visible）时显示
    opacity: 0,
    transition: 'opacity 150ms ease',
    '&:focus-visible': {
      opacity: 1,
    },
  },
  artifactImageWrap: {
    display: 'block',
    maxWidth: '100%',
    margin: '10px 0',
    lineHeight: 0,
  },
  artifactImage: {
    display: 'block',
    maxWidth: '100%',
    maxHeight: '70vh',
    width: 'auto',
    height: 'auto',
    borderRadius: 10,
    objectFit: 'contain' as const,
    boxShadow: '0 4px 18px color-mix(in srgb, var(--earth-brown) 14%, transparent)',
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
