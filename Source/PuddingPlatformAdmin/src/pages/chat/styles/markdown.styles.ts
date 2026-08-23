// ── markdown styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useMarkdownStyles = createStyles(() => ({
  markdownBody: {
    whiteSpace: 'normal' as const,
    '& p': { margin: '0 0 8px' },
    '& p:last-child': { marginBottom: 0 },
    // 列表节奏放半档（对齐 harness 16px 节奏）：列表块 8px、li 间 2px
    '& ul, & ol': { paddingLeft: 22, margin: '8px 0' },
    '& li': { margin: '2px 0' },
    // emoji 字号收敛：预处理包 data-md-emoji span，避免 emoji 比正文大一号的突兀感
    '& [data-md-emoji]': {
      fontSize: '0.95em',
      lineHeight: 1,
      verticalAlign: '-0.06em',
    },
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
    // 表格对齐 harness MarkdownText：仅横向分隔线（无竖线网格），
    // th 加粗下边线、td 弱下边线，末行无底线。
    '& table': {
      borderCollapse: 'collapse' as const,
      minWidth: 'min(100%, 420px)',
      margin: '2px 0',
    },
    '& th, & td': {
      border: 'none',
      padding: '9px 14px',
      textAlign: 'left' as const,
      verticalAlign: 'top',
    },
    '& th': {
      background: 'transparent',
      fontSize: 13,
      fontWeight: 600,
      whiteSpace: 'nowrap' as const,
      borderBottom:
        '1.5px solid color-mix(in srgb, var(--text-primary, #333) 25%, transparent)',
    },
    '& td': {
      fontSize: 13.5,
      lineHeight: '22px',
      borderBottom:
        '1px solid color-mix(in srgb, var(--text-primary, #333) 10%, transparent)',
    },
    '& tr:last-child td': { borderBottom: 'none' },
  },
  markdownTableScroll: {
    maxWidth: '100%',
    overflowX: 'auto' as const,
    margin: '8px 0',
  },
  inlineCode: {
    padding: '1px 5px',
    borderRadius: 6,
    background: 'color-mix(in srgb, var(--misty-blue) 30%, transparent)',
    fontSize: '0.92em',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
  },
    codeBlockWrap: {
    position: 'relative' as const,
    margin: '10px 0',
    borderRadius: 12,
    // P0-3: 深一档背景（双主题 token，浅 #1e2430 / 深 #0d1117）。
    // 注意：不能设 overflow:hidden —— 会破坏 sticky banner 相对滚动容器的吸附。
    background: 'var(--pudding-chat-code-bg)',
    '& pre': {
      margin: 0,
      padding: '14px 16px',
      overflowX: 'auto' as const,
      fontSize: 13,
      fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
      // P0-3: 深底上文字必须为浅色（#e6edf3，AA 对比）；Prism 无主题时不染色，token 继承此色
      color: '#e6edf3',
    },
    // P0-3: hover 代码块时显隐复制按钮（attribute 选择器，避免依赖 hashed class 名）
    '&:hover [data-code-copy]': {
      opacity: 1,
    },
  },
  // P0-3: sticky banner（语言标签 + 复制按钮行），与代码块同深底，代码滚动时吸附顶部
  codeBlockBanner: {
    position: 'sticky' as const,
    top: 0,
    zIndex: 6,
    display: 'flex',
    alignItems: 'center' as const,
    justifyContent: 'space-between' as const,
    gap: 8,
    padding: '6px 8px',
    background: 'var(--pudding-chat-code-bg)',
    borderBottom:
      '1px solid color-mix(in srgb, #e6edf3 14%, transparent)',
  },
  // P0-3: 语言标签（11px、浅色、等宽；随 banner 行内布局）
  codeLanguageLabel: {
    maxWidth: 'calc(100% - 96px)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
    fontSize: 11,
    lineHeight: '16px',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    color: '#e6edf3',
    opacity: 0.78,
    userSelect: 'none' as const,
    pointerEvents: 'none' as const,
  },
  codeCopyButton: {
    display: 'inline-flex' as const,
    alignItems: 'center' as const,
    gap: 4,
    fontSize: 12,
    lineHeight: '16px',
    // P0-3: banner 内常显；键盘聚焦（focus-visible）保持反馈
    opacity: 1,
    transition: 'opacity 150ms ease',
    '&:focus-visible': {
      opacity: 1,
    },
  },
  // ── P0-1：Agent 错误摘要行（批 B 范围内唯一可改的样式文件；类名并入聚合 styles，
  //    由 AgentMessageBubble 经 useChatMessageStyles 消费）──
  agentErrorSummaryRow: {
    display: 'flex',
    alignItems: 'center' as const,
    gap: 6,
    padding: '6px 4px 0',
    flexWrap: 'wrap' as const,
  },
  agentErrorSummaryTitle: {
    fontSize: 12,
    lineHeight: '16px',
    fontWeight: 600,
    color: 'var(--pudding-status-error)',
    whiteSpace: 'nowrap' as const,
    userSelect: 'none' as const,
  },
  // cancelled 态标题用警告色（--pudding-status-waiting 为已定义的双主题 token）
  agentErrorSummaryTitleWarning: {
    color: 'var(--pudding-status-waiting)',
  },
  agentErrorSummaryText: {
    fontSize: 12,
    lineHeight: '16px',
    color: 'var(--pudding-chat-text-muted)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
    minWidth: 0,
    flex: '1 1 auto',
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
