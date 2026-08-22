// ── ToolCallRow 工具调用行样式（P1-1，对齐 deepseek-harness D5 ToolRow）──
// 单行 24px 摘要（StateDot + 工具名 + 2×2 分隔点 + summary FILL）+ 整行展开 IN/OUT 卡。
// token 全部走 --pudding-* / --accent-* 变量，组件内零字面量主色；
// 不触碰 message.styles.ts / process.styles.ts / global.style.ts。
import { createStyles } from 'antd-style';

export const useToolCallStyles = createStyles(() => ({
  /** 列表容器：仅在有 tool_call 行时渲染 */
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    marginTop: 6,
    boxSizing: 'border-box' as const,
  },
  /** 单行：24px 高；button role 整行可点；hover/focus-visible 反馈 */
  row: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 24,
    padding: '0 8px',
    boxSizing: 'border-box' as const,
    borderRadius: 6,
    cursor: 'pointer',
    transition: 'background 150ms ease',
    overflow: 'hidden',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--pudding-chat-text-subtle) 10%, transparent)',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-status-running)',
      outlineOffset: -2,
    },
  },
  /** running 行：CSS sweep 扫光（prefers-reduced-motion 降级为静态弱光） */
  rowRunning: {
    '&::after': {
      content: '""',
      position: 'absolute',
      top: 0,
      left: 0,
      height: '100%',
      width: '38%',
      background:
        'linear-gradient(100deg, transparent, color-mix(in srgb, var(--pudding-status-running) 12%, transparent), transparent)',
      transform: 'translateX(-130%)',
      animation: 'toolCallSweep 1.7s ease-in-out infinite',
      pointerEvents: 'none' as const,
    },
    '@media (prefers-reduced-motion: reduce)': {
      '&::after': {
        animation: 'none',
        transform: 'none',
        opacity: 0.16,
      },
    },
  },
  '@keyframes toolCallSweep': {
    '0%': { transform: 'translateX(-130%)' },
    '55%, 100%': { transform: 'translateX(360%)' },
  },
  /** leading：16px 状态点（StateDot） */
  leading: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 24,
  },
  /** 工具名 14px */
  title: {
    flexShrink: 0,
    fontSize: 14,
    fontWeight: 600,
    lineHeight: '24px',
    whiteSpace: 'nowrap' as const,
    color: 'var(--pudding-chat-text)',
  },
  /** 2×2 分隔点 */
  dotGrid: {
    flexShrink: 0,
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 2px)',
    gridTemplateRows: 'repeat(2, 2px)',
    gap: 2,
  },
  dot: {
    width: 2,
    height: 2,
    borderRadius: '50%',
    background: 'var(--pudding-chat-text-subtle)',
    opacity: 0.55,
  },
  /** summary FILL 单行截断 */
  summary: {
    flex: 1,
    minWidth: 0,
    fontSize: 12,
    lineHeight: '24px',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    color: 'var(--pudding-chat-text-muted)',
  },
  /** error 摘要：红色（错误首行） */
  summaryError: {
    color: 'var(--pudding-status-error)',
  },
  /** chevron：展开旋转 180° */
  chevron: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 24,
    fontSize: 10,
    lineHeight: 1,
    color: 'var(--pudding-chat-text-subtle)',
    transition: 'transform 150ms ease',
  },
  chevronOpen: {
    transform: 'rotate(180deg)',
  },
  /** 展开体：IN/OUT 卡列 */
  expanded: {
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    padding: '0 8px 6px 32px',
    boxSizing: 'border-box' as const,
  },
  /** IN/OUT 卡：深底 + 等宽 + 260px 内滚 */
  card: {
    position: 'relative',
    maxHeight: 260,
    overflow: 'auto',
    borderRadius: 6,
    background: 'var(--pudding-chat-code-bg)',
    border: '1px solid color-mix(in srgb, #e6edf3 12%, transparent)',
  },
  /** sticky 标签：随卡滚动吸附顶部 */
  cardLabel: {
    position: 'sticky',
    top: 0,
    zIndex: 2,
    padding: '4px 10px',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: '0.08em',
    lineHeight: '16px',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    background: 'var(--pudding-chat-code-bg)',
    color: '#e6edf3',
    opacity: 0.9,
    borderBottom: '1px solid color-mix(in srgb, #e6edf3 14%, transparent)',
    userSelect: 'none' as const,
    pointerEvents: 'none' as const,
  },
  /** error 时 OUT 标签红 */
  cardLabelError: {
    color: 'var(--pudding-status-error)',
    opacity: 1,
  },
  /** 卡内容：等宽字体 + 浅字（深底） */
  cardPre: {
    margin: 0,
    padding: '8px 10px 10px',
    fontSize: 12,
    lineHeight: 1.55,
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    color: '#e6edf3',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
  },
    /** error 时 OUT 首行红 */
  errorText: {
    color: 'var(--pudding-status-error)',
  },

  // ── ToolCallTree（CU-07 递归调用树）──
  /** 单分支容器：子列表缩进在其下方 */
  treeBranch: {
    display: 'flex',
    flexDirection: 'column',
    gap: 0,
  },
  /** 子列表：左缩进 24px（相对父行）+ 左侧竖线（调用树层级视觉） */
  treeChildren: {
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    marginLeft: 24,
    paddingLeft: 8,
    borderLeft: '1px solid color-mix(in srgb, var(--pudding-chat-text-subtle) 24%, transparent)',
    boxSizing: 'border-box' as const,
  },
}));
