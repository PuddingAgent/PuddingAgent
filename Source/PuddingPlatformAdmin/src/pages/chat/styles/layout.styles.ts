// ── layout styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useLayoutStyles = createStyles(({ token }) => ({
  layout: {
    display: 'flex',
    height: '100vh',
    overflow: 'hidden',
    background: 'var(--pudding-chat-bg)',
  },
  mainArea: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    overflow: 'hidden',
  },
  workbenchShell: {
    flexDirection: 'row' as const,
    alignItems: 'stretch',
    background: 'var(--pudding-chat-bg)',
  },
  workbenchCenter: {
    display: 'flex',
    flexDirection: 'column' as const,
    flex: 1,
    minWidth: 0,
    minHeight: 0,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: 10,
    padding: '12px 16px',
    borderBottom: '1px solid',
    borderColor: 'var(--pudding-chat-border)',
    background: 'var(--pudding-chat-header-bg)',
    backdropFilter: 'blur(12px)',
    minHeight: 44,
    flexShrink: 0,
  },
  headerSelect: { minWidth: 110, maxWidth: 180, fontSize: 12 },
  headerSwitchSelect: {
    '@media (max-width: 920px)': { display: 'none' },
  },
  headerSelectPopup: {
    background: 'var(--pudding-chat-surface) !important',
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 8,
    boxShadow: 'var(--pudding-chat-shadow)',
    padding: 4,
    '& .ant-select-item': {
      borderRadius: 6,
      padding: '5px 12px',
      fontSize: 13,
      color: 'var(--pudding-chat-text)',
      transition: 'background 0.15s, color 0.15s',
    },
    '& .ant-select-item-option-active': {
      background: 'var(--pudding-chat-surface-muted) !important',
    },
    '& .ant-select-item-option-selected': {
      background: 'var(--pudding-chat-accent-soft) !important',
      color: 'var(--pudding-chat-accent) !important',
      fontWeight: 500,
    },
    '& .ant-select-item-option-disabled': {
      color: 'var(--pudding-chat-text-subtle) !important',
      opacity: 0.5,
    },
    '& .ant-select-item-empty': {
      color: 'var(--pudding-chat-text-subtle)',
    },
    '& .ant-divider': {
      margin: '4px 0',
      borderColor: 'var(--pudding-chat-border)',
    },
    '& .ant-btn-link': {
      color: 'var(--pudding-chat-accent)',
      fontSize: 12,
      height: 32,
      width: '100%',
      borderRadius: 6,
      transition: 'background 0.15s',
      '&:hover': {
        background: 'var(--pudding-chat-accent-soft) !important',
      },
    },
  },
  taskBoardButton: {
    color: 'var(--pudding-chat-accent)',
    background: 'var(--pudding-chat-accent-soft)',
    borderRadius: 999,
    paddingInline: 10,
    fontWeight: 600,
    flexShrink: 0,
    '&:hover': {
      color: 'var(--pudding-chat-accent) !important',
      background:
        'color-mix(in srgb, var(--pudding-chat-accent) 16%, transparent) !important',
    },
  },
  devModeActive: { color: token.colorPrimary },
  /** 语义按钮复位：让原本用 span onClick 的可点击内容键盘可达（Enter/Space 原生生效）。 */
  headerTextButton: {
    display: 'inline-flex',
    alignItems: 'center',
    padding: 0,
    border: 'none',
    background: 'none',
    cursor: 'pointer',
    color: 'inherit',
    font: 'inherit',
    '&:focus-visible': {
      outline:
        '2px solid color-mix(in srgb, var(--pudding-chat-accent) 45%, transparent)',
      outlineOffset: 2,
      borderRadius: 6,
    },
  },
  /** 移动端隐藏元素 */
  hideOnMobile: {
    '@media (max-width: 767px)': { display: 'none' },
  },
  hideOnTablet: {
    '@media (min-width: 768px) and (max-width: 1023px)': { display: 'none' },
  },
  chatBody: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minHeight: 0,
    overflow: 'hidden',
    // 布局留白（2026-09-19 用户诉求：消息区过宽、两侧需呼吸感）：
    // 消息列表与 IntentConsole 共用本容器，改此一处即可整体收窄并保持左右对齐。
    // 用 clamp 而非固定 px 阅读上限：窄屏保持 20px 基础内距不被挤压，
    // 宽屏按视口渐进放宽（4vw）至上限 64px，避免大屏内容贴边。
    padding: '0 clamp(20px, 4vw, 64px)',
    background: 'var(--pudding-chat-bg)',
  },
  chatBodyWithDev: {
    display: 'flex',
    flexDirection: 'row' as const,
    gap: 12,
    minHeight: 0,
    height: '100%',
  },
  chatBodyMain: {
    display: 'flex',
    flexDirection: 'column' as const,
    flex: 1,
    minWidth: 0,
    minHeight: 0,
  },
  chatInteractionShell: {
    position: 'relative' as const,
    display: 'flex',
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    height: '100%',
    gap: 12,
    alignItems: 'stretch',
    overflow: 'hidden',
  },
  chatConversationColumn: {
    display: 'flex',
    flexDirection: 'column' as const,
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    // 阅读宽度上限（2026-09-19 用户诉求第 1/2 项：消息区继续收窄 + 输入框同宽对齐）：
    // 消息列表（timelineRegion）与 IntentConsole 是本列的两个兄弟节点，
    // 故此处设上限即可让两者自动同宽、左右边界严格对齐，无需分别调 padding。
    // 用 maxWidth 而非继续加大 chatBody 的 padding：padding 方案在超宽屏仍会让
    // 正文无限拉宽（一行过长不利阅读），maxWidth 才能给出稳定的阅读宽度。
    maxWidth: 1040,
    margin: '0 auto',
  },
  messageList: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto' as const,
    overscrollBehavior: 'contain',
    scrollbarGutter: 'stable',
    overflowAnchor: 'none',
    padding: '16px 0 8px',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 12,
  },
  /** P2#8：消息列表外壳 — 顶部承载 Focus view 工具栏，滚动区域占满剩余高度；
   * relative 作为底部滚动控制簇（回到底部/贴底跟随）的定位锚：控制簇锚定
   * 消息区右下角，天然始终位于 composer 上方（对齐 Hermes measured-composer
   * 锚定语义，无需写死视口偏移/测量桥）。 */
  messageListShell: {
    display: 'flex',
    flexDirection: 'column' as const,
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    overflow: 'hidden',
    position: 'relative' as const,
  },
}));
