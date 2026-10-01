// ── markdown styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useMarkdownStyles = createStyles(() => ({
  markdownBody: {
    whiteSpace: 'normal' as const,
    // IMG07 / §5：正文段落节奏 12（原 8 偏密）
    '& p': { margin: '0 0 12px' },
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
    // §5：块引用左线 3px + surface-muted 底 + 内边距 12
    // （原为 2px + 暖黄 borderLeft + 棕字 + opacity，属于「叠了几套底」的来源之一）
    '& blockquote': {
      margin: '8px 0',
      padding: '8px 12px',
      borderLeft: '3px solid var(--pudding-chat-border)',
      background: 'var(--pudding-chat-surface-muted)',
      borderRadius: '0 6px 6px 0',
      color: 'var(--pudding-chat-text-muted)',
    },
    // §5：链接需要可辨认的强调色。原 --sky-soft 在浅色主题下几乎是白色，
    // 正文里的链接实际读不出来（IM04 类可读性问题）。
    '& a': {
      color: 'var(--pudding-chat-accent)',
      textDecoration: 'none',
      '&:hover': { textDecoration: 'underline' },
    },
    // IMG07（§13.5）：单元格 10/12、分隔线走主题 border；首列给 7em 最小宽，
    // 避免「可部署产物」这类短标签被挤成一字一行。长内容列仍可换行，
    // 整表超宽时由 markdownTableScroll 横滚（不把整个消息设 overflow:hidden）。
    '& table': {
      borderCollapse: 'collapse' as const,
      width: '100%',
      minWidth: 'min(100%, 420px)',
      margin: '2px 0',
    },
    '& th, & td': {
      border: 'none',
      padding: '10px 12px',
      textAlign: 'left' as const,
      verticalAlign: 'top',
    },
    '& th:first-child, & td:first-child': {
      minWidth: '7em',
    },
    '& th': {
      background: 'transparent',
      fontSize: 13,
      fontWeight: 600,
      whiteSpace: 'nowrap' as const,
      borderBottom: '1.5px solid var(--pudding-chat-border)',
    },
    '& td': {
      fontSize: 13.5,
      lineHeight: '22px',
      borderBottom: '1px solid var(--pudding-chat-border)',
    },
    '& tr:last-child td': { borderBottom: 'none' },
  },
  markdownTableScroll: {
    maxWidth: '100%',
    overflowX: 'auto' as const,
    margin: '8px 0',
  },
  inlineCode: {
    // IMG06 / §13.5：行内代码降低对比、常规字重、收紧内边距与圆角。
    // 原来是 6px 圆角 + misty-blue 混色底，让正文里每个路径都像可点 chip，
    // 和标题、粗体、代码块一起抢眼。表格内的行内代码也走这里（不再叠亮灰硬边）。
    padding: '2px 4px',
    borderRadius: 4,
    background: 'var(--pudding-chat-surface-muted)',
    color: 'var(--pudding-chat-text)',
    fontWeight: 400,
    fontSize: '0.9em',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    overflowWrap: 'anywhere' as const,
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
