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
  /** 行容器：非可展开行无 hover/焦点反馈；chevron 占位保持对齐（harness 24px 行，规范下限 28px） */
  row: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    minHeight: 28,
    padding: '0 4px',
    boxSizing: 'border-box' as const,
    borderRadius: 6,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    transition: 'background 150ms ease',
  },
  /** 可展开行：整行可点（cursor + hover + :focus-visible 焦点环）；可点击区最小 32px（§6 规范） */
  rowClickable: {
    cursor: 'pointer',
    minHeight: 32,
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
    height: 28,
  },
  /** 行主体：弹性填充；单行截断由内容自身处理 */
  body: {
    flex: 1,
    minWidth: 0,
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
  },
  /** 标题与摘要之间的 2×2px 分隔点（harness 行式 chrome） */
  titleDot: {
    flexShrink: 0,
    width: 2,
    height: 2,
    borderRadius: 1,
    background: 'var(--pudding-chat-text-caption)',
    opacity: 0.85,
  },
  /** 折叠行尾部计量槽（耗时 / exit code）：caption 灰 + tabular-nums，不挤压摘要 */
  duration: {
    flexShrink: 0,
    fontSize: 11,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text-caption)',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  /** exit code 非零时的错误色计量 */
  durationError: {
    color: 'var(--pudding-status-error)',
  },
  /** running 行扫光（与工具行 toolCallSweep 同参数家族；reasoning/委派行复用） */
  rowSweep: {
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
      animation: 'executionFlowRowSweep 1.7s ease-in-out infinite',
      pointerEvents: 'none' as const,
    },
    overflow: 'hidden',
    '@media (prefers-reduced-motion: reduce)': {
      '&::after': {
        animation: 'none',
        transform: 'none',
        opacity: 0.16,
      },
    },
  },
  '@keyframes executionFlowRowSweep': {
    '0%': { transform: 'translateX(-130%)' },
    '55%, 100%': { transform: 'translateX(360%)' },
  },
  /** chevron 16px 固定槽；不可展开时占位隐藏（行首对齐不跳动） */
  chevron: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 28,
    fontSize: 10,
    lineHeight: 1,
    color: 'var(--pudding-chat-text-caption)',
    transition: 'transform 150ms ease',
  },
  chevronPlaceholder: {
    visibility: 'hidden',
  },
  /** 展开体：左缩进对齐行内容（leading 16 + gap 6 = 22px）；圆角 10px；表面由消费方定义 */
  expanded: {
    padding: '2px 8px 6px 22px',
    boxSizing: 'border-box' as const,
    maxWidth: 'min(720px, 100%)',
  },

  // ── TurnStatus（CU-05 §5.1）──
  /** 单行运行态：与执行流行共享同一内容列与左边界（§6.1），不套气泡壳 */
  turnStatusRow: {
    maxWidth: 'min(720px, 100%)',
  },
  /** 墨球宿主槽：20px inline 档，-2px 光学居中于 16px leading 槽（两侧各溢 2px 吃进行 padding） */
  orbHost: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 20,
    height: 20,
    marginLeft: -2,
  },
  /** 「{agentName} 正在运行」/ 阶段文案：text-shimmer（对齐 harness TurnStatus shimmer），reduced-motion 降级静态 */
  turnStatusLabel: {
    fontSize: 14,
    fontWeight: 500,
    lineHeight: '22px',
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
    color: 'var(--pudding-chat-text-caption)',
    fontSize: 11,
    lineHeight: '20px',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  '@keyframes executionFlowTextShimmer': {
    '0%': { backgroundPosition: '200% 0' },
    '100%': { backgroundPosition: '-200% 0' },
  },

  // ── ReasoningDisclosureRow（CU-06 §5.1 + §6.1 + 行为链 §3.3 计量 chip）──
  /** reasoning 标题「思考」：14px/22px 500（过程行标题 = secondary 档） */
  reasoningTitle: {
    fontSize: 14,
    fontWeight: 500,
    lineHeight: '22px',
    color: 'var(--pudding-chat-text-secondary)',
    whiteSpace: 'nowrap' as const,
  },
  /** 摘要单行 ellipsis：13px/20px，过程正文 = tertiary 档 */
  reasoningSummary: {
    fontSize: 13,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text-tertiary)',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    minWidth: 0,
  },
  /** 完成态计量 chip：「思考 · 12s」（caption 底 pill，对齐 harness "Thought for Ns"） */
  reasoningChip: {
    flexShrink: 0,
    fontSize: 11,
    lineHeight: '18px',
    padding: '0 8px',
    borderRadius: 999,
    color: 'var(--pudding-chat-text-caption)',
    background:
      'color-mix(in srgb, var(--pudding-chat-text-caption) 10%, transparent)',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  /** 展开内容容器：标题/复制/正文纵向排布 */
  reasoningWrap: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 6,
  },
  /** 复制按钮：小号次级文案按钮 */
  reasoningCopy: {
    alignSelf: 'flex-start' as const,
    fontSize: 11,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text-caption)',
    background: 'transparent',
    border: 'none' as const,
    padding: 0,
    cursor: 'pointer',
    textDecoration: 'underline',
  },
  /** 展开体：完整可审计文本，最大高度 320px 内部滚动（测试断言 max-height:320px / overflow:auto） */
  reasoningBody: {
    maxHeight: 320,
    overflow: 'auto',
    borderRadius: 8,
    background: 'var(--pudding-chat-code-bg)',
    padding: '8px 10px',
    boxSizing: 'border-box' as const,
  },
  /** pre 保留换行（可审计原文），等宽字体 */
  reasoningText: {
    margin: 0,
    fontSize: 12.5,
    lineHeight: 1.6,
    color: 'var(--pudding-chat-text)',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
  },

  // ── ReasoningDisclosureRow inline-full（I-10 §7.8：行为组内完整推理自然换行，无二级披露）──
  /** 行容器：同执行流行左边界（16px leading 槽），纵向承载 meta + 全文 */
  reasoningFullRow: {
    display: 'flex',
    alignItems: 'flex-start' as const,
    gap: 6,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    padding: '2px 4px',
    boxSizing: 'border-box' as const,
  },
  /** 内容列：弹性填充 + minWidth:0，允许内部文本任意断行 */
  reasoningFullContent: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
  },
  /** 「思考 · 12s」meta：caption 档小字（无 nowrap，随内容列纵排） */
  reasoningFullMeta: {
    fontSize: 11,
    lineHeight: '18px',
    color: 'var(--pudding-chat-text-caption)',
    fontVariantNumeric: 'tabular-nums' as const,
  },
  /** 完整推理正文：自然换行（禁止复用 reasoningSummary 的 nowrap/ellipsis 类名） */
  reasoningFullText: {
    margin: 0,
    fontSize: 12.5,
    lineHeight: 1.6,
    color: 'var(--pudding-chat-text)',
    whiteSpace: 'pre-wrap' as const,
    overflowWrap: 'anywhere' as const,
    wordBreak: 'break-word' as const,
    minWidth: 0,
  },

  // ── TurnContentStream（AgentTurnCard 重构：正文段 ⇄ 行为组内容块流）──
  /** 内容块流容器：与时间线同规格（gap 4 / 720px 上限） */
  turnContentStream: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    boxSizing: 'border-box' as const,
  },
  /** 行为组容器：仅承载组头折叠行 + 展开体，不叠加容器级 margin */
  activityGroup: {
    width: '100%',
    maxWidth: 'min(720px, 100%)',
  },
  /** 组展开体：成员行纵向排布 */
  activityGroupBody: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
  },
  /** 超长 Turn/行为组的渐进揭示入口；默认只挂载最新窗口，旧内容按需恢复。 */
  trajectoryWindowButton: {
    alignSelf: 'flex-start',
    border: 0,
    padding: '2px 0',
    background: 'transparent',
    color: 'var(--pudding-chat-text-tertiary)',
    fontSize: 12,
    lineHeight: '20px',
    cursor: 'pointer',
    '&:hover': {
      color: 'var(--pudding-chat-primary)',
    },
  },
  /** 交错文本段：与正文同款排版（15/1.75 全宽），区别于过程行的 tertiary 灰阶 */
  timelineMessageSegment: {
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    fontSize: 15,
    lineHeight: 1.75,
    color: 'var(--pudding-chat-text)',
    wordBreak: 'break-word' as const,
    marginTop: 2,
    marginBottom: 2,
  },

  // ── TurnStatsLine（行为链 §3.3：turn 终态计量行）──  /** 统计行：正文下方一行 caption 灰计量（harness StatsLine 同语义） */
  statsLine: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    marginTop: 8,
    fontSize: 12,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text-caption)',
    fontVariantNumeric: 'tabular-nums' as const,
    maxWidth: 'min(720px, 100%)',
  },
  /** 统计项之间的分隔点 */
  statsDot: {
    flexShrink: 0,
    width: 2,
    height: 2,
    borderRadius: 1,
    background: 'var(--pudding-chat-text-caption)',
    opacity: 0.7,
  },
}));
