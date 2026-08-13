// ── approval.styles：ApprovalCard 样式（P0-5 并入 createStyles 体系并消费 token）──
import { createStyles } from 'antd-style';

// ── 风险四档色 = 严重度语义（非 --pudding-status-* 状态色阶）──
// 设计文档 §4.1 P0-4 约束：可新增 --pudding-risk-* token 或保留字面量但集中于此。
// 本批选择集中常量方案：沿用 antd 语义色（低绿/中金/高橙/严重红），
// 浅/深色模式暂不区分，保持既有行为与观感不变（后续如需主题化再升级为 token）。
export const RISK_COLORS: Record<
  'low' | 'medium' | 'high' | 'critical',
  { color: string; background: string }
> = {
  low: { color: '#389e0d', background: '#f6ffed' },
  medium: { color: '#d48806', background: '#fffbe6' },
  high: { color: '#d46b08', background: '#fff7e6' },
  critical: { color: '#cf1322', background: '#fff1f0' },
};

export const useApprovalStyles = createStyles(() => ({
  cardContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: 8,
    width: '100%',
    maxWidth: 'min(560px, 100%)',
    padding: '12px 14px',
    marginTop: 8,
    borderRadius: 'var(--pudding-chat-radius-md)',
    border:
      '1px solid color-mix(in srgb, var(--pudding-chat-border) 80%, transparent)',
    background: 'var(--pudding-chat-surface-muted)',
    boxShadow: 'var(--pudding-chat-shadow-md)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    flexWrap: 'wrap',
  },
  title: {
    fontSize: 13,
    fontWeight: 600,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text)',
    wordBreak: 'break-all',
  },
  description: {
    margin: 0,
    fontSize: 12,
    lineHeight: 1.6,
    color: 'var(--pudding-chat-text-muted)',
    wordBreak: 'break-word',
    whiteSpace: 'pre-wrap',
  },
  argumentsPre: {
    margin: 0,
    maxHeight: 160,
    overflowY: 'auto',
    padding: '8px 10px',
    borderRadius: 'var(--pudding-chat-radius-sm)',
    background:
      'color-mix(in srgb, var(--pudding-chat-border) 18%, transparent)',
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    fontSize: 11,
    lineHeight: 1.55,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
  },
  reasonRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
  },
  /** 统一消除 antd Tag 默认 marginInlineEnd（风险/状态/决策标签共用） */
  tag: { marginInlineEnd: 0 },
  /** 小号次要文本（过期提示 / 允许清单说明） */
  metaText: { fontSize: 12 },
  /** 决策理由行 */
  decisionReason: {
    marginTop: 6,
    fontSize: 12,
    color: 'var(--pudding-chat-text-muted)',
  },
  /** 按钮行容器（原内联 marginTop） */
  actions: { marginTop: 8 },
}));
