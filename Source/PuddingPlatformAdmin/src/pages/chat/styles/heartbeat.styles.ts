// ── 心跳显示样式 ─────────────────────────────────────────────────
import { createStyles } from 'antd-style';

export const useHeartbeatStyles = createStyles(() => ({
  pulseDot: {
    width: 8,
    height: 8,
    borderRadius: '50%',
    background: 'var(--accent-purple)',
  },
  pulseLabel: {
    fontSize: 13,
    color:
      'color-mix(in srgb, var(--accent-purple) 50%, var(--text-secondary))',
    fontStyle: 'italic',
  },
  heartbeatContainer: {
    maxWidth: '100%',
    width: '100%',
    background: 'var(--soft-white)',
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
    borderLeft: '3px solid',
    borderLeftColor:
      'color-mix(in srgb, var(--accent-purple) 22%, transparent)',
    borderRadius: 8,
    borderTopLeftRadius: 4,
    padding: '6px 12px',
  },
  heartbeatHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    cursor: 'pointer',
    listStyle: 'none',
    userSelect: 'none' as const,
    padding: '2px 0',
    '&::-webkit-details-marker': { display: 'none' },
    '&::marker': { content: 'none' },
  },
  heartbeatIcon: {
    fontSize: 13,
    lineHeight: 1,
    color: 'var(--accent-purple)',
    opacity: 0.7,
  },
  heartbeatLabel: {
    fontSize: 12,
    fontWeight: 500,
    color: 'var(--pudding-chat-text-muted)',
    lineHeight: '20px',
  },
  heartbeatBody: {
    marginTop: 8,
    paddingTop: 8,
    borderTop: '1px dashed',
    borderTopColor: 'color-mix(in srgb, var(--earth-brown) 8%, transparent)',
    fontSize: 13,
    lineHeight: 1.65,
    color: 'var(--pudding-chat-text-muted)',
    '& p': { marginBottom: 4 },
    '& p:last-child': { marginBottom: 0 },
  },
  heartbeatTime: {
    marginLeft: 'auto',
    fontSize: 11,
    color: 'var(--pudding-chat-text-muted)',
    opacity: 0.65,
    fontVariantNumeric: 'tabular-nums' as const,
  },
}));
