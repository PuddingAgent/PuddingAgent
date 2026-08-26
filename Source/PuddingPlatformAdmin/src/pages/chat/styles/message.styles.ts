// ── message styles ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useMessageStyles = createStyles(({ token }) => ({
  timelineRegion: {
    display: 'flex',
    flexDirection: 'column' as const,
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    overflow: 'hidden',
  },
  turnContainer: {
    position: 'relative' as const,
    display: 'flex',
    gap: 12,
    width: '100%',
  },
  turnTimeline: {
    width: 2,
    borderRadius: 2,
    background: token.colorBorderSecondary,
    marginTop: 4,
    marginBottom: 4,
    flexShrink: 0,
  },
  turnBody: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
    width: '100%',
  },
  messageContent: {
    maxWidth: '74%',
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    position: 'relative' as const,
    fontSize: 14,
    color: 'var(--text-primary)',
    '&:hover .message-actions': {
      opacity: 1,
      transform: 'translateY(0)',
      pointerEvents: 'auto' as const,
    },
  },
  messageActions: {
    opacity: 0,
    transform: 'translateY(3px)',
    pointerEvents: 'none' as const,
    transition: 'opacity 200ms ease-in-out, transform 200ms ease-in-out',
    '& .ant-btn-text': { color: 'var(--earth-brown)' },
    '& .ant-btn-text:hover': { color: 'var(--accent-purple)' },
    '& .ant-btn-dangerous': { color: 'var(--earth-brown)' },
    '& .ant-btn-dangerous:hover': { color: token.colorError },
  },
  messageMeta: { display: 'flex', alignItems: 'center', gap: 8, minHeight: 20 },
  timeText: { color: 'var(--earth-brown)', opacity: 0.7, fontSize: 12 },
  timeDivider: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    margin: '2px 0',
    color: 'var(--earth-brown)',
    opacity: 0.7,
    fontSize: 12,
    '&::before, &::after': {
      content: '""',
      flex: 1,
      height: 1,
      background: 'color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    },
  },
  groupContent: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
  },
  userGroupContent: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-end' as const,
  },
  systemRow: { justifyContent: 'center' },

  // ── 开发者模式面板 ───────────────────────────────────────────
  messageRow: {
    display: 'flex',
    width: '100%',
    marginBottom: 2,
    padding: '8px 0',
    contain: 'layout paint style',
    // 不使用 content-visibility/contain-intrinsic-size：行为组在流式与折叠切换后
    // 会留下陈旧的 remembered size，滚回长卡片时 scrollHeight 可瞬增数千像素。
    // 离屏成本由真实近视口水合 + viewport 虚拟化承担，正常流必须保持实高。
  },
  messageRowUser: {
    justifyContent: 'flex-end',
  },
  messageRowAgent: {
    justifyContent: 'flex-start',
  },
  messageRowGrouped: {
    marginTop: -4,
  },
  messageRowHeartbeat: {
    justifyContent: 'center',
    marginTop: 8,
    marginBottom: 8,
  },
  agentMessageContainer: {
    position: 'relative' as const,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-start',
    // 内容列统一以 720px 为阅读上限；卡片额外预留 28px 横向 padding + 2px
    // border，避免外壳扩到 82% 而内部只占 720px，形成大块卡内空白。
    width: '100%',
    maxWidth: 'min(750px, 82%)',
    minWidth: 0,
    // P0-2: 为气泡外绝对定位的操作按钮预留落点，长气泡/贴底场景防裁切
    paddingBottom: 8,
  },
  /**
   * AgentTurnCard 大卡片外壳（2026-08-24 验收 5）：一个 Agent 回合 = 一张卡。
   * 包住状态行、交错行为链、正文、错误行、操作与统计；内部节点保持行式
   * 紧凑布局，不再各套卡片。外壳结构稳定——终态只改变状态，不重挂载、
   * 不重播入场动画（动画只存在于正文气泡的 entrance 类）。
   * 表面：微弱暖色（主题 surface 混 3% 品牌紫）、1px 边界、14px 圆角。
   */
  agentTurnStateChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 4,
    fontSize: 11,
    lineHeight: '20px',
    opacity: 0.65,
    marginLeft: 6,
  },
  agentTurnCard: {
    position: 'relative' as const,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'stretch',
    background:
      'color-mix(in srgb, var(--pudding-chat-accent) 3%, var(--pudding-admin-surface))',
    border: '1px solid var(--pudding-admin-border)',
    borderRadius: 'var(--pudding-chat-radius-lg)',
    padding: '10px 14px 6px',
    width: '100%',
  },
  userMessageContainer: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-end',
    maxWidth: '70%',
    minWidth: 0,
  },
  messageModalityBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    height: 18,
    padding: '0 6px',
    borderRadius: 5,
    border: '1px solid var(--pudding-chat-border)',
    color: 'var(--pudding-chat-text-muted)',
    background:
      'color-mix(in srgb, var(--pudding-chat-accent) 8%, transparent)',
    fontSize: 11,
    lineHeight: '16px',
  },
  messageActionsNew: {
    // 行为链升级：去「白色药丸卡片 + 绝对定位悬浮」——与扁平消息流风格冲突，
    // 且 bottom:-26 悬浮会与 TurnStatsLine 重叠。改为 harness IconActions 模式：
    // 正文下方常驻透明图标行（hover 透明度切换、不 reflow），margin-left -6px
    // 做 28px 热区的光学对齐。
    display: 'flex',
    alignItems: 'center',
    gap: 2,
    margin: '6px 0 0 -6px',
    opacity: 0,
    pointerEvents: 'none' as const,
    transition: 'opacity 180ms ease',
  },
  messageActionsVisible: {
    // P0-2: hover 中操作按钮全亮（原 0.6 半透明）
    opacity: 1,
    pointerEvents: 'auto' as const,
    // P0-2: 键盘 Tab 聚焦到任一按钮时保持整组可见（focus-within）
    '&:focus-within': {
      opacity: 1,
      pointerEvents: 'auto' as const,
    },
  },
  messageActionBtn: {
    width: 28,
    height: 28,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    border: 'none',
    background: 'transparent',
    borderRadius: 6,
    cursor: 'pointer' as const,
    color: 'var(--pudding-chat-text-tertiary)',
    opacity: 1,
    fontSize: 13,
    transition: 'opacity 120ms ease, background 120ms ease, color 120ms ease',
    '&:hover': {
      opacity: 1,
      color: 'var(--pudding-chat-text-secondary)',
      background:
        'color-mix(in srgb, var(--pudding-chat-text-tertiary) 10%, transparent)',
    },
    // P0-2: 键盘焦点显影（对齐 focusViewRowHeader 的 focus-visible 写法）
    '&:focus-visible': {
      outline:
        '2px solid color-mix(in srgb, var(--accent-purple) 45%, transparent)',
      outlineOffset: 1,
    },
  },
  messageActionBtnDanger: {
    '&:hover': {
      color: '#ef4444',
      opacity: 1,
    },
  },
  // ── P1-4 用户消息操作按钮 + 失败态 ───────────────────────────
  userMessageActionsHost: {
    position: 'relative' as const,
  },
  userMessageActions: {
    // 与 agent 侧 messageActionsNew 同款 inline 透明图标行（去白卡/去绝对定位
    // 悬浮）：随气泡流排列、右对齐由 userBubbleArea（align-items:flex-end）提供，
    // hover 透明度切换、不 reflow；marginRight -6px 为 28px 热区光学对齐。
    display: 'flex',
    alignItems: 'center',
    gap: 2,
    marginTop: 4,
    marginRight: -6,
    opacity: 0,
    pointerEvents: 'none' as const,
    transition: 'opacity 180ms ease',
    '&:focus-within': {
      opacity: 1,
      pointerEvents: 'auto' as const,
    },
  },
  userMessageActionsVisible: {
    opacity: 1,
    pointerEvents: 'auto' as const,
    transform: 'translateY(0)',
  },
  userErrorText: {
    fontSize: 11,
    lineHeight: '16px',
    color: 'var(--pudding-status-error)',
    marginTop: 2,
    paddingRight: 4,
    opacity: 0.9,
  },
  // ── P1-5 用户附件图片：单图 240px 长边 / 多图 64px tile / 失败重试 ──
  // 对齐 deepseek-harness D9 MessageImage：单图长边 240px（宽高比 clamp
  // [0.25,4]，object-fit cover 锚点 top left，不放大超自然尺寸）；多图
  // 64px 方块 tile 网格；加载 shimmer 占位（reduced-motion 降级静态）；
  // 失败保留 tile/单图尺寸并支持点击重试（cache-bust）。
  '@keyframes userVisionShimmer': {
    '0%': { backgroundPosition: '-200% 0' },
    '100%': { backgroundPosition: '200% 0' },
  },
  /** 单图展示盒：默认 240×240 占位；JS 依据自然尺寸注入 clamp 后的宽高 */
  userVisionImageSingle: {
    position: 'relative' as const,
    display: 'grid',
    placeItems: 'center',
    flex: '0 0 auto',
    width: 240,
    height: 240,
    maxWidth: 240,
    maxHeight: 240,
    borderRadius: 8,
    overflow: 'hidden' as const,
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 22%, transparent)',
    background:
      'color-mix(in srgb, var(--accent-purple) 4%, var(--soft-white))',
    contain: 'layout style',
  },
  /** 单图 img：填满 clamp 后的展示盒，cover 裁切锚定左上 */
  userVisionImageSingleImg: {
    display: 'block',
    width: '100%',
    height: '100%',
    maxWidth: 240,
    maxHeight: 240,
    objectFit: 'cover' as const,
    objectPosition: 'top left' as const,
    borderRadius: 8,
  },
  /** 多图（≥2）：64px 方块 tile 网格，gap 10 */
  userVisionTileGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, 64px)',
    gap: 10,
    justifyContent: 'flex-start',
    maxWidth: '100%',
  },
  userVisionTile: {
    position: 'relative' as const,
    display: 'grid',
    placeItems: 'center',
    width: 64,
    height: 64,
    minWidth: 64,
    minHeight: 64,
    borderRadius: 8,
    overflow: 'hidden' as const,
    border: '1px solid',
    borderColor: 'color-mix(in srgb, var(--accent-purple) 22%, transparent)',
    background:
      'color-mix(in srgb, var(--accent-purple) 4%, var(--soft-white))',
    contain: 'layout style',
  },
  userVisionTileImg: {
    display: 'block',
    width: '100%',
    height: '100%',
    objectFit: 'cover' as const,
    borderRadius: 8,
  },
  /** 加载占位：浅色 shimmer；reduced-motion 降级为静态浅块 */
  userVisionImageLoading: {
    position: 'absolute' as const,
    inset: 0,
    background:
      'linear-gradient(100deg, color-mix(in srgb, var(--accent-purple) 4%, transparent) 40%, color-mix(in srgb, var(--accent-purple) 14%, transparent) 50%, color-mix(in srgb, var(--accent-purple) 4%, transparent) 60%)',
    backgroundSize: '200% 100%',
    animation: 'userVisionShimmer 1.4s linear infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animation: 'none',
      background: 'color-mix(in srgb, var(--accent-purple) 8%, transparent)',
    },
  },
  /** 失败占位内的重试按钮：占满 tile/单图块，不撑大布局 */
  userVisionRetryBtn: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
    width: '100%',
    height: '100%',
    padding: 0,
    border: 'none',
    background: 'transparent',
    cursor: 'pointer' as const,
    fontSize: 13,
    color: 'var(--earth-brown)',
    opacity: 0.8,
    borderRadius: 8,
    '&:hover': {
      opacity: 1,
      background: 'color-mix(in srgb, var(--earth-brown) 6%, transparent)',
      color: 'var(--accent-purple)',
    },
    '&:focus-visible': {
      outline:
        '2px solid color-mix(in srgb, var(--accent-purple) 45%, transparent)',
      outlineOffset: 1,
    },
  },
  timeline: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
    position: 'relative' as const,
    paddingLeft: 16,
    animation: 'fadeIn 200ms ease-out',
    '&::before': {
      content: '""',
      position: 'absolute' as const,
      left: 4,
      top: 0,
      bottom: 0,
      width: 2,
      background: 'var(--earth-brown, rgba(120,100,80,0.10))',
      borderRadius: 1,
    },
  },
  timelineUserMsg: {
    fontSize: 14,
    color: 'var(--text-primary)',
    padding: '4px 0 8px',
    position: 'relative' as const,
    '&::before': {
      content: '""',
      position: 'absolute' as const,
      left: -14,
      top: 10,
      width: 6,
      height: 6,
      borderRadius: '50%',
      background: 'var(--accent-purple, #7c3aed)',
      opacity: 0.5,
      zIndex: 1,
    },
  },
  timelineUserLabel: {
    display: 'inline-block',
    fontSize: 11,
    color: 'var(--text-muted)',
    opacity: 0.6,
    marginBottom: 2,
    marginRight: 8,
  },
  timelineNode: {
    position: 'relative' as const,
    padding: '6px 0 6px 12px',
    '&::before': {
      content: '""',
      position: 'absolute' as const,
      left: -14,
      top: 10,
      width: 8,
      height: 8,
      borderRadius: '50%',
      border: '2px solid var(--neural-line, rgba(124,58,237,0.18))',
      background: 'var(--warm-beige)',
      zIndex: 1,
    },
  },
  timelineNodeThinking: {
    opacity: 0.65,
    fontStyle: 'italic',
    fontSize: 12,
    borderLeft: '2px solid var(--memory-glow, #A78BFA)',
  },
  timelineNodeTool: {
    borderLeft: '2px solid var(--tool-signal, #22D3EE)',
  },
  timelineNodeToolRunning: {
    borderLeft: '2px solid var(--earth-brown)',
    '&::before': {
      animation: 'signalFlow 2s linear infinite',
      background: 'var(--earth-brown)',
      boxShadow:
        '0 0 6px color-mix(in srgb, var(--misty-blue) 40%, transparent)',
      backgroundSize: '200% 100%',
    },
  },
  timelineNodeToolDone: {
    borderLeftColor: 'var(--success-signal, #22C55E)',
  },
  timelineNodeToolFailed: {
    borderLeftColor: 'var(--error-signal, #EF4444)',
  },
  timelineNodeAnswer: {
    borderLeft: 'none',
    padding: '8px 0 8px 0',
    marginLeft: -12,
  },
  timelineAnswerBlock: {
    background: 'var(--soft-white)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 6%, transparent)',
    borderRadius: 12,
    padding: '14px 18px',
    fontSize: 15,
    lineHeight: 1.7,
    color: 'var(--text-primary)',
  },
  timelineNodeAppear: {
    animation: 'nodeAppear 300ms ease-out',
  },
  messageViewportControls: {
    // 锚定 messageListShell（relative）右下角：不随滚动移动、始终位于 composer
    // 上方（原 position:fixed + bottom:112 写死视口偏移，composer 变高时错位）。
    position: 'absolute' as const,
    right: 16,
    bottom: 16,
    display: 'flex',
    justifyContent: 'flex-end',
    gap: 8,
    pointerEvents: 'none' as const,
    zIndex: 30,
  },
  messageViewportControlButton: {
    pointerEvents: 'auto' as const,
    width: 40,
    height: 40,
    borderRadius: 8,
  },
  messageViewportScrollArea: {
    scrollbarGutter: 'stable' as const,
    overscrollBehavior: 'contain' as const,
    scrollbarWidth: 'thin' as const,
  },
}));

// ── P2#8 Focus view（单行折叠模式）──────────────────────────
// Focus view 把每个 turn 折叠成一行（隐藏工具调用 / thinking），点击展开查看完整内容。
// 对齐 Claude VS Code Focus view：运行中显示当前工具/活动。
export const useFocusViewStyles = createStyles(() => ({
  focusViewToolbar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    flexShrink: 0,
    padding: '2px 16px 4px',
  },
  focusViewToggle: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    minHeight: 22,
    padding: '2px 8px',
    borderRadius: 999,
    border:
      '1px solid color-mix(in srgb, var(--pudding-chat-border) 70%, transparent)',
    background:
      'color-mix(in srgb, var(--pudding-chat-surface-muted) 22%, transparent)',
  },
  focusViewToggleLabel: {
    fontSize: 11,
    lineHeight: '18px',
    color: 'var(--pudding-chat-text-muted)',
    opacity: 0.8,
    whiteSpace: 'nowrap' as const,
    userSelect: 'none' as const,
  },
  focusViewRow: {
    width: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
  },
  focusViewRowHeader: {
    appearance: 'none' as const,
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    width: '100%',
    minHeight: 28,
    padding: '2px 8px',
    border: '1px solid transparent',
    borderRadius: 8,
    background: 'transparent',
    cursor: 'pointer' as const,
    textAlign: 'left' as const,
    fontFamily: 'inherit',
    transition: 'background 150ms ease, border-color 150ms ease',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--pudding-chat-surface-muted) 42%, transparent)',
      borderColor:
        'color-mix(in srgb, var(--pudding-chat-accent) 12%, transparent)',
    },
    '&:focus-visible': {
      outline:
        '2px solid color-mix(in srgb, var(--accent-purple) 40%, transparent)',
      outlineOffset: 1,
    },
  },
  focusViewRowHeaderRunning: {
    background: 'color-mix(in srgb, var(--accent-purple) 5%, transparent)',
  },
  focusViewRowHeaderError: {
    background: 'color-mix(in srgb, #ef4444 5%, transparent)',
  },
  focusViewRowUserAvatar: {
    width: 22,
    height: 22,
    borderRadius: '50%',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    background: 'var(--sky-soft)',
    color: 'var(--earth-brown)',
    fontSize: 10,
    fontWeight: 500,
  },
  focusViewRowName: {
    fontSize: 12,
    color: 'var(--pudding-chat-text)',
    fontWeight: 500,
    whiteSpace: 'nowrap' as const,
    flexShrink: 0,
  },
  focusViewRowSummary: {
    flex: 1,
    minWidth: 0,
    fontSize: 12,
    lineHeight: '18px',
    color: 'var(--pudding-chat-text-muted)',
    opacity: 0.78,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  focusViewRowSummaryRunning: {
    color: 'var(--accent-purple)',
    opacity: 0.92,
  },
  focusViewRowSummaryError: {
    color: '#ef4444',
    opacity: 0.92,
  },
  focusViewRowTime: {
    fontSize: 11,
    lineHeight: '18px',
    color: 'var(--pudding-chat-text-muted)',
    opacity: 0.55,
    whiteSpace: 'nowrap' as const,
    flexShrink: 0,
  },
  focusViewRowCaret: {
    display: 'inline-flex',
    alignItems: 'center',
    fontSize: 10,
    color: 'var(--pudding-chat-text-muted)',
    opacity: 0.5,
    flexShrink: 0,
  },
  focusViewRowContent: {
    width: '100%',
    padding: '2px 0 4px 8px',
  },
}));
