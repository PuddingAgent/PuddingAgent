// ── 聊天页样式（antd-style createStyles — 组合模式）─────────────────────
import { createStyles } from 'antd-style';
import { useLayoutStyles } from './styles/layout.styles';
import { useMessageStyles } from './styles/message.styles';
import { useAgentStyles } from './styles/agent.styles';
import { useUserStyles } from './styles/user.styles';
import { useProcessStyles } from './styles/process.styles';
import { useReasoningStyles } from './styles/reasoning.styles';
import { useMarkdownStyles } from './styles/markdown.styles';
import { useComposerStyles } from './styles/composer.styles';
import { usePanelStyles } from './styles/panel.styles';
import { useVoiceStyles } from './styles/voice.styles';
import { useHeartbeatStyles } from './styles/heartbeat.styles';
import { useStatusStyles } from './styles/status.styles';
import { useDevpanelStyles } from './styles/devpanel.styles';
import { useAnimationStyles } from './styles/animations.styles';
import { useSidebarStyles } from './styles/sidebar.styles';

export const SIDEBAR_WIDTH = 260;

// Residual styles kept inline (misc styles not yet split into modules;
// heartbeat/status/devpanel/animations/sidebar live in ./styles/)
const useResidualStyles = createStyles(({ token }) => ({
  // (workspace-studio styles removed — ~105 keys, ~1600 lines. Scheduled for rebuild.)
  historyLoading: {
    display: 'flex',
    justifyContent: 'center',
    padding: '8px 0',
  },
  bubble: {
    width: 'fit-content' as const,
    maxWidth: 'min(76%, 820px)',
    padding: '12px 16px',
    borderRadius: 8,
    lineHeight: 1.6,
    wordBreak: 'break-word' as const,
    whiteSpace: 'pre-wrap' as const,
    border: '1px solid transparent',
    background: 'transparent',
    transition: 'background 200ms ease-in-out, box-shadow 200ms ease-in-out',
    '&:hover': {
      background: 'color-mix(in srgb, var(--soft-white) 50%, transparent)',
      boxShadow: '0 1px 6px rgba(0,0,0,0.04)',
    },
  },
  stepCardList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 12,
    position: 'relative' as const,
    paddingLeft: 12,
  },
  stepCardLine: {
    position: 'absolute' as const,
    left: 5,
    top: 4,
    bottom: 4,
    width: 2,
    borderRadius: 2,
    background: token.colorBorderSecondary,
  },
  stepCard: {
    position: 'relative' as const,
    background: token.colorFillQuaternary,
    borderRadius: token.borderRadius,
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 15%, transparent)',
    borderLeftWidth: 4,
    padding: '8px 10px',
  },
  stepCardDot: {
    position: 'absolute' as const,
    left: -11,
    top: 14,
    width: 8,
    height: 8,
    borderRadius: '50%',
    background: token.colorBorder,
    border: `1px solid ${token.colorBgContainer}`,
  },
  stepCardExecuting: { borderLeftColor: 'var(--earth-brown)' },
  stepCardSuccess: {
    borderLeftColor: 'color-mix(in srgb, var(--earth-brown) 40%, transparent)',
  },
  stepCardError: { borderLeftColor: token.colorError },
  stepCardTitle: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
    marginBottom: 4,
    fontSize: 13,
  },
  stepCardStatus: {
    color: token.colorTextSecondary,
    fontSize: 12,
    fontWeight: 600,
    textTransform: 'uppercase' as const,
  },
  stepCardMessage: {
    color: token.colorText,
    fontSize: 13,
    lineHeight: 1.6,
    whiteSpace: 'pre-wrap' as const,
  },
  stepCardTime: { color: 'var(--earth-brown)', opacity: 0.7, fontSize: 12 },

  /* ── Tool output collapse (长输出折叠 + 滚动 + 模糊) ── */
  toolOutputCollapse: {
    marginTop: 4,
    borderRadius: 6,
    overflow: 'hidden',
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
  },
  toolOutputContent: {
    padding: '8px 10px',
    fontSize: 12,
    color: token.colorTextSecondary,
    lineHeight: 1.5,
    whiteSpace: 'pre-wrap' as const,
    overflow: 'hidden',
    position: 'relative' as const,
    transition: 'max-height 0.3s ease',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    background: token.colorFillQuaternary,
  },
  toolOutputFade: {
    position: 'absolute' as const,
    bottom: 0,
    left: 0,
    right: 0,
    height: 40,
    background: `linear-gradient(to bottom, transparent, ${token.colorFillQuaternary})`,
    pointerEvents: 'none' as const,
  },
  toolOutputToggle: {
    padding: '4px 10px',
    fontSize: 11,
    color: token.colorPrimary,
    cursor: 'pointer',
    borderTop: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    textAlign: 'center' as const,
    userSelect: 'none' as const,
    background: token.colorFillQuaternary,
  },
  errorBubble: { borderColor: token.colorError },
  inputPanel: {
    padding: '8px 0 0',
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    background: 'color-mix(in srgb, var(--soft-white) 80%, transparent)',
    backdropFilter: 'blur(16px)',
    borderTop: '1px solid',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    borderRadius: '8px 8px 0 0',
    marginTop: 4,
  },
  tokenIndicator: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    padding: '0 2px',
  },
  tokenProgress: { flex: 1 },
  // 工具栏（输入框上方一行）
  toolbar: { display: 'flex', alignItems: 'center', gap: 4, padding: '0 6px' },
  toolbarSpacer: { flex: 1 },
  // 状态栏（窄条 IDE 风格：紧贴底部，单行图标透传状态）──
  // 状态栏图标通用
  inputArea: { display: 'flex', gap: 8, alignItems: 'center' },
  input: { flex: 1 },
  emptyState: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: token.colorTextQuaternary,
    fontSize: 15,
  },
  emptyStateShell: {
    flex: 1,
    minHeight: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '48px 24px 96px',
  },
  emptyStateInner: {
    width: '100%',
    maxWidth: 420,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    textAlign: 'center' as const,
    gap: 12,
    animation: 'emptyFadeIn 220ms ease-out',
  },
  emptyLogoFrame: {
    width: 112,
    height: 112,
    display: 'grid',
    placeItems: 'center',
    borderRadius: 28,
    background: 'color-mix(in srgb, var(--accent-purple) 5%, transparent)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 8%, transparent)',
  },
  emptyLogo: {
    width: 64,
    height: 64,
    objectFit: 'contain' as const,
    opacity: 0.92,
  },
  emptyTitle: {
    margin: '8px 0 0 !important',
    fontSize: '22px !important',
    fontWeight: '600 !important',
    color: 'var(--text-primary) !important',
  },
  emptySubtitle: {
    maxWidth: 360,
    fontSize: 14,
    lineHeight: 1.7,
    color: 'var(--text-muted)',
  },
  emptySuggestionRow: {
    display: 'flex',
    gap: 10,
    flexWrap: 'wrap' as const,
    justifyContent: 'center',
    marginTop: 8,
  },
  emptySuggestionButton: {
    height: 34,
    padding: '0 14px',
    borderRadius: 17,
    border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    background: 'color-mix(in srgb, var(--soft-white) 86%, transparent)',
    color: 'var(--text-muted)',
    fontSize: 13,
    cursor: 'pointer' as const,
    transition:
      'background 160ms ease, border-color 160ms ease, color 160ms ease',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--accent-purple) 7%, var(--soft-white))',
      borderColor: 'color-mix(in srgb, var(--accent-purple) 18%, transparent)',
      color: 'var(--text-primary)',
    },
  },
    errorAlert: { margin: '8px 0' },
  sidebarEmpty: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: token.colorTextQuaternary,
    fontSize: 13,
    padding: '0 16px',
    textAlign: 'center' as const,
  },
  
  stepCardCompleteIcon: {
    transition: 'color 300ms ease-in-out',
    color: 'var(--desaturated-green)',
  },
  inputCursor: {
    display: 'inline-block',
    width: 2,
    height: 18,
    background: 'var(--accent-purple)',
    marginLeft: 1,
    animation: 'cursorBlink 1s steps(1) infinite',
    verticalAlign: 'middle' as const,
  },
  avatarGroup: {
    display: 'flex',
    gap: 12,
    width: '100%',
    alignItems: 'flex-start',
  },
  avatarCol: { width: 32, flexShrink: 0 },
  avatarImg: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  avatarPlaceholder: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    color: '#fff',
    fontSize: 14,
    fontWeight: 500,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    userSelect: 'none' as const,
  },
  avatarNameRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    marginBottom: 4,
    minHeight: 20,
  },
  avatarName: {
    fontSize: 14,
    color: 'var(--earth-brown)',
    fontWeight: 500,
    lineHeight: '20px',
  },
  avatarUserIcon: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    background: 'var(--sky-soft)',
    color: 'var(--earth-brown)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    '& .anticon': { fontSize: 16 },
  },
  subconsciousTimeline: {
    padding: '4px 0 8px',
    '& .ant-timeline-item': { paddingBottom: 12 },
    '& .ant-timeline-item-content': { fontSize: 13 },
  },
  
  tokenUsageLine: {
    fontSize: 10,
    color: 'var(--earth-brown)',
    opacity: 0.35,
    padding: '2px 4px 0',
  },
  cameraInputModalBody: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 12,
  },
  cameraInputStatusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 22,
    fontSize: 12,
    color: 'var(--pudding-text-muted, #756b5f)',
  },
  cameraInputStatusDot: {
    width: 7,
    height: 7,
    borderRadius: '50%',
    background: 'var(--pudding-accent, #8b5cf6)',
    flexShrink: 0,
  },
  cameraInputResolution: {
    marginLeft: 'auto',
    fontVariantNumeric: 'tabular-nums' as const,
    color: 'var(--pudding-text-muted, #756b5f)',
    opacity: 0.72,
  },
  cameraInputPreviewFrame: {
    position: 'relative' as const,
    width: '100%',
    aspectRatio: '16 / 9',
    overflow: 'hidden' as const,
    borderRadius: 8,
    border:
      '1px solid color-mix(in srgb, var(--earth-brown, #5c4a3a) 12%, transparent)',
    background:
      'color-mix(in srgb, var(--pudding-surface-soft, #f3eee7) 76%, #000 4%)',
  },
  cameraInputPreviewMedia: {
    display: 'block',
    width: '100%',
    height: '100%',
    objectFit: 'cover' as const,
  },
  cameraInputActionRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
  },
  activeRunOutput: {
    margin: '0 0 4px 0',
  },
  
  paperStreaming: {
    background: 'color-mix(in srgb, var(--soft-white) 92%, #fff7df)',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 8%, transparent)',
    boxShadow: '0 1px 8px rgba(92, 64, 42, 0.04)',
    transition:
      'background 420ms ease, border-color 420ms ease, box-shadow 520ms ease',
    contain: 'paint',
  },
  paperSettled: {
    background: 'var(--soft-white)',
    borderColor: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
    boxShadow: 'none',
    transition:
      'background 520ms ease, border-color 520ms ease, box-shadow 520ms ease',
  },
  actionButtonsSettled: {
    opacity: '1 !important' as unknown as number,
    transform: 'translateY(0) !important' as unknown as string,
    pointerEvents:
      'auto !important' as unknown as React.CSSProperties['pointerEvents'],
    transition: 'opacity 400ms ease-out, transform 400ms ease-out',
  },
  
}));

export const useChatStyles = () => {
  const { styles: layout, cx, theme } = useLayoutStyles();
  const { styles: message } = useMessageStyles();
  const { styles: agent } = useAgentStyles();
  const { styles: user } = useUserStyles();
  const { styles: process } = useProcessStyles();
  const { styles: reasoning } = useReasoningStyles();
  const { styles: markdown } = useMarkdownStyles();
  const { styles: composer } = useComposerStyles();
  const { styles: panel } = usePanelStyles();
    const { styles: voice } = useVoiceStyles();
    const { styles: heartbeat } = useHeartbeatStyles();
  const { styles: status } = useStatusStyles();
  const { styles: devpanel } = useDevpanelStyles();
  const { styles: animation } = useAnimationStyles();
  const { styles: sidebar } = useSidebarStyles();
  const { styles: residual } = useResidualStyles();

  return {
    styles: {
      ...residual,
      ...layout,
      ...message,
      ...agent,
      ...user,
      ...process,
      ...reasoning,
      ...markdown,
      ...composer,
      ...panel,
      ...voice,
      ...heartbeat,
      ...status,
      ...devpanel,
      ...animation,
      ...sidebar,
    },
    cx,
    theme,
  };
};
