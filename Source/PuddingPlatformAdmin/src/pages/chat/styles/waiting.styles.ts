// ── WaitingBubble 单行运行态样式（P1-3，对齐 deepseek-harness D6 TurnStatus 单行范式）──
// 仅服务 WaitingBubble.tsx；token 复用 global.style.ts 既有 --pudding-* / --accent-*，
// 不触碰 message.styles.ts / global.style.ts。
import { createStyles } from 'antd-style';

export const useWaitingStyles = createStyles(() => ({
  /** 单行容器：StateDot(ongoing) + 「{agentName} 正在运行」 + （≥15s）计时段 */
  row: {
    display: 'flex',
    alignItems: 'center',
    width: '100%',
    maxWidth: 'min(720px, 100%)',
  },
  /** Tooltip 触发器行（hover 展示阶段文案） */
  line: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 8,
    minWidth: 0,
    maxWidth: '100%',
  },
  /** 「{agentName} 正在运行」：text-shimmer（对齐 TurnStatus），reduced-motion 降级静态 */
  title: {
    fontSize: 13,
    fontWeight: 600,
    lineHeight: '20px',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    background:
      'linear-gradient(90deg, var(--pudding-chat-text) 40%, var(--accent-purple) 52%, var(--pudding-chat-text) 64%)',
    backgroundSize: '200% 100%',
    WebkitBackgroundClip: 'text',
    backgroundClip: 'text',
    color: 'transparent',
    animation: 'waitingTextShimmer 2.6s linear infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      background: 'none',
      color: 'var(--pudding-chat-text)',
    },
  },
  /** 「· 已等待 Xs/Xm」（≥15s 才渲染） */
  elapsed: {
    color: 'var(--pudding-chat-text-muted)',
    fontSize: 11,
    lineHeight: '20px',
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  '@keyframes waitingTextShimmer': {
    '0%': { backgroundPosition: '200% 0' },
    '100%': { backgroundPosition: '-200% 0' },
  },
}));
