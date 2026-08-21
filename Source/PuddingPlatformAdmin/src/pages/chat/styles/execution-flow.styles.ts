// ── ExecutionFlow 行式 chrome 样式（CU-05，对齐消息 UI §5.1 + §6.1）────────
// 服务 ExecutionDisclosureRow（共享行式折叠 chrome）与 TurnStatus（单行运行态）。
// 行规格（§6.1）：
//  - 可点击区 ≥32px（行高基准 32px）；leading 16px 固定槽；状态点 10px
//  - 标题 14px/22px 600；摘要 13px/20px 单行 ellipsis；行间距 4px
//  - 展开体左缩进对齐行内容（8 + 16 + 8 = 32px），圆角 10px
// token 全部走 --pudding-* / --accent-* 变量，组件内零字面量主色；
// 不触碰 message.styles.ts / process.styles.ts / global.style.ts。
import { createStyles } from 'antd-style';

export const useExecutionFlowStyles = createStyles(() => ({
  /** 行容器：非可展开行无 hover/焦点反馈；chevron 占位保持对齐 */
  row: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 32,
    padding: '0 8px',
    boxSizing: 'border-box' as const,
    borderRadius: 6,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    transition: 'background 150ms ease',
  },
  /** 可展开行：整行可点（cursor + hover + :focus-visible 焦点环） */
  rowClickable: {
    cursor: 'pointer',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--pudding-chat-text-subtle) 10%, transparent)',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-status-running)',
      outlineOffset: -2,
    },
  },
  /** leading 16px 固定槽（状态点 10px / 图标 14–16px） */
  leading: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 32,
  },
  /** 行主体：弹性填充；单行截断由内容自身处理 */
  body: {
    flex: 1,
    minWidth: 0,
    display: 'inline-flex',
    alignItems: 'center',
    gap: 8,
  },
  /** chevron 16px 固定槽；不可展开时占位隐藏（行首对齐不跳动） */
  chevron: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 32,
    fontSize: 10,
    lineHeight: 1,
    color: 'var(--pudding-chat-text-subtle)',
    transition: 'transform 150ms ease',
  },
  chevronPlaceholder: {
    visibility: 'hidden',
  },
  /** 展开体：左缩进对齐行内容（8 + 16 + 8 = 32px）；圆角 10px；表面由消费方定义 */
  expanded: {
    padding: '2px 8px 6px 32px',
    boxSizing: 'border-box' as const,
    maxWidth: 'min(720px, 100%)',
  },

  // ── TurnStatus（CU-05 §5.1）──
  /** 单行运行态：与执行流行共享同一内容列与左边界（§6.1），不套气泡壳 */
  turnStatusRow: {
    maxWidth: 'min(720px, 100%)',
  },
  /** 「{agentName} 正在运行」/ 阶段文案：text-shimmer（对齐 D6 TurnStatus），reduced-motion 降级静态 */
  turnStatusLabel: {
    fontSize: 13,
    fontWeight: 600,
    lineHeight: '20px',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    background:
      'linear-gradient(90deg, var(--pudding-chat-text) 40%, var(--accent-purple) 52%, var(--pudding-chat-text) 64%)',
    backgroundSize: '200% 100%',
    WebkitBackgroundClip: 'text',
    backgroundClip: 'text',
    color: 'transparent',
    animation: 'executionFlowTextShimmer 2.6s linear infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      background: 'none',
      color: 'var(--pudding-chat-text)',
    },
  },
  /** 「· 已等待 Xs/Xm」（≥15s 才渲染；基于持久化 turn start，刷新不归零） */
  turnStatusElapsed: {
    color: 'var(--pudding-chat-text-muted)',
    fontSize: 11,
    lineHeight: '20px',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  '@keyframes executionFlowTextShimmer': {
    '0%': { backgroundPosition: '200% 0' },
    '100%': { backgroundPosition: '-200% 0' },
  },
}));
