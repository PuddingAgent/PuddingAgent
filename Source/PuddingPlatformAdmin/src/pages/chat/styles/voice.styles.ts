// ── voice styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useVoiceStyles = createStyles(({ token }) => ({
  voiceArea: { display: 'flex', alignItems: 'center', gap: 4, flexShrink: 0 },
  voiceBtn: {
    fontSize: 14,
    opacity: 0.5,
    transition: 'opacity 0.2s, color 0.2s',
    '&:hover': { opacity: 0.8 },
  },
  voiceBtnRecording: {
    fontSize: 14,
    color: '#ef4444',
    animation: 'voicePulse 1.2s ease-in-out infinite',
  },
  voiceTranscriptPreview: {
    maxWidth: 116,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
    color: 'var(--pudding-chat-text-subtle)',
    fontSize: 11,
    lineHeight: '18px',
  },
  '@keyframes voicePulse': {
    '0%, 100%': { opacity: 1 },
    '50%': { opacity: 0.5 },
  },
  voiceWaveform: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: 3,
    height: 18,
    paddingRight: 4,
  },
  voiceBarRecording: {
    width: 3,
    borderRadius: 2,
    background: '#7c3aed',
    animation: 'voiceBarAnim 0.6s ease-in-out infinite alternate',
  },
  voiceBarPlaying: {
    width: 3,
    borderRadius: 2,
    background: '#22c55e',
    animation: 'voiceBarAnim 0.5s ease-in-out infinite alternate',
  },
  '@keyframes voiceBarAnim': { '0%': { height: 4 }, '100%': { height: 18 } },
  '@keyframes puddingComposerRecording': {
    '0%, 100%': {
      boxShadow:
        '0 0 12px rgba(124,58,237,0.2), 0 0 30px rgba(124,58,237,0.05)',
    },
    '50%': {
      boxShadow:
        '0 0 22px rgba(124,58,237,0.4), 0 0 50px rgba(124,58,237,0.15)',
    },
  },
  voicePanel: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 10,
    padding: '2px 0 0',
  },
  voicePanelHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: 12,
    minHeight: 38,
  },
  voicePanelTitle: {
    fontSize: 14,
    fontWeight: 600,
    color: 'var(--pudding-chat-text)',
    lineHeight: 1.35,
  },
  voicePanelSubtitle: {
    marginTop: 2,
    fontSize: 12,
    lineHeight: 1.45,
    color: 'var(--pudding-chat-text-muted)',
  },
  voicePanelState: {
    minWidth: 86,
    height: 24,
    padding: '0 9px',
    borderRadius: 6,
    border: '1px solid var(--pudding-chat-border)',
    background:
      'color-mix(in srgb, var(--pudding-chat-surface-muted) 65%, transparent)',
    color: 'var(--pudding-chat-text-muted)',
    fontSize: 12,
    lineHeight: '22px',
    textAlign: 'center' as const,
    whiteSpace: 'nowrap' as const,
  },
  voicePanelBody: {
    display: 'grid',
    gridTemplateColumns: '58px minmax(0, 1fr)',
    gap: 10,
    alignItems: 'stretch',
    minHeight: 98,
    '@media (max-width: 640px)': {
      gridTemplateColumns: '1fr',
    },
  },
  voicePrimaryControl: {
    width: 58,
    minWidth: 58,
    height: 58,
    minHeight: 58,
    alignSelf: 'center',
    borderRadius: '50%',
    border:
      '1px solid color-mix(in srgb, var(--pudding-chat-accent) 25%, var(--pudding-chat-border))',
    background: 'var(--pudding-chat-surface)',
    color: 'var(--pudding-chat-accent)',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: 22,
    cursor: 'pointer' as const,
    transition:
      'background 160ms ease, border-color 160ms ease, color 160ms ease',
    '&:hover:not(:disabled)': {
      borderColor:
        'color-mix(in srgb, var(--pudding-chat-accent) 42%, var(--pudding-chat-border))',
      background:
        'color-mix(in srgb, var(--pudding-chat-accent) 6%, var(--pudding-chat-surface))',
    },
    '&[data-active="true"]': {
      color: '#8a4f32',
      borderColor:
        'color-mix(in srgb, #8a4f32 36%, var(--pudding-chat-border))',
      background: 'color-mix(in srgb, #8a4f32 7%, var(--pudding-chat-surface))',
    },
    '&:disabled': {
      cursor: 'not-allowed' as const,
      opacity: 0.45,
    },
    '@media (max-width: 640px)': {
      width: '100%',
      borderRadius: 7,
    },
  },
  voiceTranscriptBox: {
    minHeight: 98,
    display: 'flex',
    flexDirection: 'column' as const,
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 7,
    background:
      'color-mix(in srgb, var(--pudding-chat-surface-muted) 38%, transparent)',
    overflow: 'hidden' as const,
  },
  voiceTranscriptTextarea: {
    width: '100%',
    minHeight: 86,
    resize: 'vertical' as const,
    border: 'none',
    outline: 'none',
    background: 'transparent',
    color: 'var(--pudding-chat-text)',
    fontSize: 14,
    lineHeight: 1.55,
    padding: '10px 12px',
    '&::placeholder': {
      color: 'var(--pudding-chat-text-subtle)',
    },
    '&:disabled': {
      cursor: 'default' as const,
      color: 'var(--pudding-chat-text)',
    },
  },
  voiceErrorText: {
    borderTop: '1px solid var(--pudding-chat-border)',
    padding: '6px 10px',
    color: '#9a4c2d',
    fontSize: 12,
    lineHeight: 1.4,
    background: 'color-mix(in srgb, #c4944c 8%, transparent)',
  },
  voicePanelActions: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: 8,
    flexWrap: 'wrap' as const,
    minHeight: 32,
  },
  voiceComposer: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
    minHeight: 58,
  },
  voiceComposerTranscriptBox: {
    minHeight: 30,
    border: 'none',
    borderRadius: 0,
    background: 'transparent',
    overflow: 'visible' as const,
  },
  voiceComposerTextarea: {
    minHeight: 30,
    maxHeight: 132,
    resize: 'none' as const,
  },
  voiceComposerStatus: {
    display: 'inline-flex',
    alignItems: 'center',
    minHeight: 22,
    padding: '0 7px',
    borderRadius: 11,
    border: '1px solid color-mix(in srgb, var(--earth-brown) 8%, transparent)',
    color: 'var(--earth-brown)',
    opacity: 0.48,
    fontSize: 11,
    lineHeight: '20px',
    whiteSpace: 'nowrap' as const,
  },
}));
