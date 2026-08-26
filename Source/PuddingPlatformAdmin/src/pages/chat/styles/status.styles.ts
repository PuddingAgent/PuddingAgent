// ── 状态栏、Token 统计、运行时状态指示器样式 ─────────────────
import { createStyles } from 'antd-style';

export const useStatusStyles = createStyles(() => ({
  statusBar: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    padding: '1px 8px 2px',
    fontSize: 10,
    color: 'var(--earth-brown)',
    opacity: 0.65,
    flexWrap: 'wrap' as const,
    minHeight: 20,
    background: 'color-mix(in srgb, var(--earth-brown) 4%, transparent)',
    borderTop: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
  },
  statusText: { whiteSpace: 'nowrap' as const },
  statusDivider: { userSelect: 'none' as const, opacity: 0.3 },
  // 状态栏图标通用
  statusIcon: {
    display: 'inline-flex',
    alignItems: 'center',
    width: 16,
    height: 16,
    justifyContent: 'center',
    flexShrink: 0,
  },
  statusIconGroup: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 3,
    cursor: 'default',
    flexShrink: 0,
  },
  statusIconLabel: {
    fontSize: 10,
    whiteSpace: 'nowrap' as const,
    lineHeight: '14px',
  },
  statusIconThunder: { transition: 'color 0.3s ease, opacity 0.3s ease' },
  statusPulse: { animation: 'statusPulseAnim 2s ease-in-out infinite' },
  '@keyframes statusPulseAnim': {
    '0%, 100%': { opacity: 0.35 },
    '50%': { opacity: 0.85 },
  },
  subconsciousGlow: {
    animation: 'subconsciousGlowAnim 3s ease-in-out infinite',
    filter: 'drop-shadow(0 0 3px rgba(167,139,250,0.5))',
  },
  '@keyframes subconsciousGlowAnim': {
    '0%, 100%': { filter: 'drop-shadow(0 0 3px rgba(167,139,250,0.5))' },
    '50%': { filter: 'drop-shadow(0 0 8px rgba(167,139,250,0.9))' },
  },
  tokenStatsPopup: {
    position: 'absolute' as const,
    bottom: 28,
    right: 0,
    width: 640,
    maxHeight: 420,
    overflowY: 'auto' as const,
    background: 'color-mix(in srgb, var(--soft-white) 98%, transparent)',
    backdropFilter: 'blur(20px)',
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 15%, transparent)',
    borderRadius: 10,
    padding: 12,
    boxShadow: '0 8px 32px rgba(0,0,0,0.12)',
    zIndex: 1050,
    fontSize: 11,
  },
  tokenStatsTitle: {
    fontWeight: 600,
    fontSize: 12,
    marginBottom: 6,
    color: 'var(--earth-brown)',
  },
  tokenStatsTable: {
    width: '100%',
    borderCollapse: 'collapse' as const,
    '& th': {
      padding: '3px 6px',
      textAlign: 'left' as const,
      fontSize: 10,
      color: 'var(--earth-brown)',
      opacity: 0.6,
      fontWeight: 500,
      borderBottom: '1px solid',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    },
    '& td': {
      padding: '3px 6px',
      fontSize: 10,
      fontVariantNumeric: 'tabular-nums' as const,
    },
  },
  runtimeStateThinking: {
    borderLeft: '2px solid var(--memory-glow, #A78BFA)',
    background: 'color-mix(in srgb, var(--memory-glow) 2%, transparent)',
  },
  runtimeStateMemory: {
    borderLeft: '2px solid var(--memory-glow, #A78BFA)',
    animation: 'softDiffuse 2s ease-in-out infinite',
  },
  runtimeStateTool: {
    borderLeft: '3px solid var(--tool-signal, #22D3EE)',
  },
  runtimeStateToolRunning: {
    borderLeft: '3px solid var(--tool-signal, #22D3EE)',
    '&::before': {
      content: '""',
      position: 'absolute' as const,
      left: -3,
      top: 0,
      bottom: 0,
      width: 3,
      background:
        'linear-gradient(180deg, transparent, var(--tool-signal, #22D3EE), transparent)',
      backgroundSize: '100% 200%',
      animation: 'signalFlow 1.5s linear infinite',
    },
  },
  runtimeStateStreaming: {
    borderLeft: '3px solid var(--accent-purple, #7c3aed)',
  },
  runtimeStateSuccess: {
    borderLeftColor: 'var(--success-signal, #22C55E)',
    transition: 'border-left-color 400ms ease',
  },
  runtimeStateError: {
    borderLeftColor: 'var(--error-signal, #EF4444)',
  },
  statusTextThinking: { color: 'var(--earth-brown)' },
  statusTextMemory: { color: 'var(--earth-brown)' },
  statusTextTool: { color: 'var(--tool-signal, #22D3EE)' },
  statusTextStreaming: { color: 'var(--accent-purple, #7c3aed)' },
  statusTextSuccess: { color: 'var(--success-signal, #22C55E)' },

  statusTextError: { color: 'var(--error-signal, #EF4444)' },
  // ── 余额徽标（ProviderBalanceIndicator）：在通用图标组之上略增强，不影响其他指示器 ──
  balanceBadgeGroup: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 4,
    cursor: 'pointer',
    flexShrink: 0,
    padding: '1px 6px',
    borderRadius: 9,
    transition: 'background-color 0.2s ease, transform 0.2s ease',
    '&:hover': {
      backgroundColor:
        'color-mix(in srgb, var(--earth-brown) 8%, transparent)',
      transform: 'scale(1.04)',
    },
  },
  balanceBadgeLabel: {
    fontSize: 11,
    fontWeight: 600 as const,
    lineHeight: '16px',
    whiteSpace: 'nowrap' as const,
    fontVariantNumeric: 'tabular-nums' as const,
    transition: 'color 0.2s ease',
  },
  balanceBadgeLabelError: {
    color: 'var(--error-signal, #EF4444)',
  },
  // 刷新请求在途：品牌图标轻微旋转反馈
  balanceBadgeSpin: {
    display: 'inline-flex',
    animation: 'balanceBadgeSpinAnim 1s linear infinite',
  },
  '@keyframes balanceBadgeSpinAnim': {
    from: { transform: 'rotate(0deg)' },
    to: { transform: 'rotate(360deg)' },
  },
  // ── 余额悬浮富卡片（HoverCard）：参考 DeepSeek 余额卡片布局，
  //    配色全部走 --pudding-chat-* 主题变量，浅色暖米系/深色自动适配 ──
  balanceCard: {
    width: 252,
    padding: 12,
    boxSizing: 'border-box' as const,
    background: 'var(--pudding-chat-surface)',
    border: '1px solid',
    borderColor: 'var(--pudding-chat-border-strong)',
    borderRadius: 'var(--pudding-chat-radius-md)',
    boxShadow: 'var(--pudding-chat-shadow-md)',
    fontSize: 11,
    color: 'var(--pudding-chat-text-muted)',
  },
  // 标题行：服务商图标 + “XX 余额：¥xx”
  balanceCardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    marginBottom: 8,
    fontSize: 11,
    fontWeight: 500 as const,
    color: 'var(--pudding-chat-text-subtle)',
    whiteSpace: 'nowrap' as const,
  },
  // 大号余额：卡片视觉焦点
  balanceCardAmount: {
    fontSize: 26,
    fontWeight: 700 as const,
    lineHeight: 1.2,
    letterSpacing: '-0.5px',
    color: 'var(--pudding-chat-text)',
    fontVariantNumeric: 'tabular-nums' as const,
  },
  balanceCardAmountError: {
    color: 'var(--pudding-chat-danger)',
  },
  balanceCardDivider: {
    height: 1,
    margin: '10px 0',
    background: 'var(--pudding-chat-border)',
  },
  // 明细区：赠送 / 充值 / 查询时间，label 左、value 右对齐
  balanceCardMeta: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 6,
  },
  balanceCardMetaRow: {
    display: 'flex',
    justifyContent: 'space-between' as const,
    gap: 16,
    fontSize: 11,
    lineHeight: '16px',
  },
  balanceCardMetaLabel: {
    color: 'var(--pudding-chat-text-subtle)',
    whiteSpace: 'nowrap' as const,
  },
  balanceCardMetaValue: {
    color: 'var(--pudding-chat-text-muted)',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  // 底部行：查询失败原因 / “点击刷新”提示
  balanceCardFooter: {
    marginTop: 10,
    paddingTop: 10,
    borderTop: '1px solid',
    borderColor: 'var(--pudding-chat-border)',
    fontSize: 11,
    color: 'var(--pudding-chat-text-caption)',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden' as const,
    textOverflow: 'ellipsis' as const,
  },
}));
