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
    // 左右内距已下沉到内容层（2026-09-19 滚动条贴右修复）：本容器保持满宽，
    // 让 messageList（滚动容器）直达 chat 区右边界。垂直滚动条渲染在滚动
    // 容器（overflow-y:auto 元素）的 padding box 右缘——给滚动容器加右侧
    // padding 不会移动滚动条，外层限宽只会把滚动条一起拖离最右，因此限宽
    // 必须下沉到滚动容器内部的内容上（见 messageList 子选择器）。
    padding: '0',
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
    // 不再在此限宽（2026-09-19 滚动条贴右修复）：此前的 maxWidth:1040 +
    // margin:0 auto 会把内部 messageList 滚动容器一起收窄，导致垂直滚动条
    // 出现在内容列右缘而非 chat 区最右。限宽与留白已下沉：消息内容见
    // messageList 子选择器，输入框见 composerAligner，两者同用
    // 1280 上限 + clamp 留白，边界严格对齐。
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
    // 滚动条贴右 + 阅读宽 1280（2026-09-19）：滚动容器本身保持满宽（垂直
    // 滚动条渲染在其 padding box 右缘 = chat 区最右），限宽/留白下沉到内容：
    // 直接子元素（虚拟滚动容器/空态/加载态/错误条）限宽 1280 居中。用
    // :not() 排除 messageViewportControls（data-testid=
    // chat-bottom-scroll-controls）——它 absolute 锚定 messageListShell
    // 右下角的语义不得被限宽/留白影响。
    '& > *:not([data-testid="chat-bottom-scroll-controls"])': {
      width: '100%',
      maxWidth: 1280,
      margin: '0 auto',
    },
    // 左右留白放在第二层（turn 行包装器）而非本层：虚拟化模式下包装器是
    // position:absolute + width:100%，以父 padding box 为基准、不随父
    // padding 内缩；padding 落在包装器自身（border-box）上，虚拟/非虚拟
    // 两种渲染模式的内缩边界才能严格一致。
    '& > *:not([data-testid="chat-bottom-scroll-controls"]) > *': {
      boxSizing: 'border-box' as const,
      paddingLeft: 'clamp(20px, 3vw, 48px)',
      paddingRight: 'clamp(20px, 3vw, 48px)',
    },
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
