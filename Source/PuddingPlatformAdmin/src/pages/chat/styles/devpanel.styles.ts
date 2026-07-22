// ── 开发者面板样式 ──────────────────────────────────────────
import { createStyles } from 'antd-style';

export const useDevpanelStyles = createStyles(({ token }) => ({
  devPanel: {
    width: 400,
    minWidth: 360,
    maxWidth: 440,
    display: 'flex',
    flexDirection: 'column' as const,
    borderLeft: '1px solid',
    borderColor: token.colorBorderSecondary,
    background: 'color-mix(in srgb, var(--soft-white) 78%, transparent)',
    backdropFilter: 'blur(12px)',
    borderRadius: 8,
    overflow: 'hidden',
    marginTop: 12,
    marginBottom: 12,
  },
  devPanelHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '10px 12px',
    borderBottom: '1px solid',
    borderColor: token.colorBorderSecondary,
    fontSize: 13,
    fontWeight: 600,
  },
  devPanelTabs: {
    flex: 1,
    minHeight: 0,
    padding: '8px 10px 10px',
    '& .ant-tabs-content-holder': {
      overflow: 'auto',
    },
  },
  devPanelSection: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
  },
  devPanelLoading: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: '16px 0',
  },
  devLine: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: 12,
    fontSize: 12,
  },
  devLayerTitle: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    width: '100%',
  },
  devPreview: {
    margin: 0,
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    background: token.colorFillQuaternary,
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    borderRadius: 6,
    padding: '8px 10px',
    fontSize: 12,
    lineHeight: 1.5,
  },
  devList: { display: 'flex', flexDirection: 'column' as const, gap: 6 },
  devListItem: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: 8,
    background: token.colorFillQuaternary,
    borderRadius: 6,
    padding: '6px 8px',
  },
  devErrorText: { margin: 0, color: token.colorError },
  devPanelHint: { margin: 0, fontSize: 12, color: token.colorTextSecondary },
  devPerfToolbar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
  },
  devPerfToolbarActions: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    flexShrink: 0,
  },
  devPerfDiagnosisList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 6,
  },
  devPerfDiagnosisItem: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    borderRadius: 6,
    padding: '7px 8px',
    background: token.colorFillQuaternary,
    fontSize: 12,
    '&[data-severity="warn"]': {
      borderColor:
        'color-mix(in srgb, var(--warning-signal, #F97316) 34%, transparent)',
      background:
        'color-mix(in srgb, var(--warning-signal, #F97316) 8%, transparent)',
    },
    '&[data-severity="critical"]': {
      borderColor:
        'color-mix(in srgb, var(--error-signal, #EF4444) 34%, transparent)',
      background:
        'color-mix(in srgb, var(--error-signal, #EF4444) 8%, transparent)',
    },
  },
  devPerfGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 8,
  },
  devPerfMetric: {
    minWidth: 0,
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    borderRadius: 6,
    padding: '7px 8px',
    background: token.colorFillQuaternary,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
    '&[data-tone="ok"]': {
      borderColor:
        'color-mix(in srgb, var(--desaturated-green) 34%, transparent)',
      background:
        'color-mix(in srgb, var(--desaturated-green) 8%, transparent)',
    },
    '&[data-tone="warn"]': {
      borderColor:
        'color-mix(in srgb, var(--warning-signal, #F97316) 34%, transparent)',
      background:
        'color-mix(in srgb, var(--warning-signal, #F97316) 8%, transparent)',
    },
  },
  devPerfMetricLabel: {
    fontSize: 11,
    lineHeight: '14px',
  },
  devPerfMetricValue: {
    fontSize: 15,
    lineHeight: '20px',
    fontWeight: 650,
    fontVariantNumeric: 'tabular-nums' as const,
  },
  devPerfCounts: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: 6,
  },
  devPerfEventItem: {
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    background: token.colorFillQuaternary,
    borderRadius: 6,
    padding: '6px 8px',
  },
  devPerfEventHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
    marginBottom: 4,
  },
  devEventList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
    maxHeight: '56vh',
    overflowY: 'auto' as const,
  },
  devEventItem: {
    background: token.colorFillQuaternary,
    border: '1px solid',
    borderColor: token.colorBorderSecondary,
    borderRadius: 6,
    padding: '6px 8px',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
  },
  devEventTime: { fontSize: 11, color: token.colorTextSecondary },
  devEventPayload: {
    margin: 0,
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    fontSize: 12,
    lineHeight: 1.5,
  },
}));
