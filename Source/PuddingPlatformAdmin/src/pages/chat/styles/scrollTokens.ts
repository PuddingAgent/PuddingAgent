// ── Chat 内容块限高 / 细滚动条 token（看板卡 73c87ea8）────────────────────────
// 语义：「块级」滚动容器（推理展开体 / 工具 IN-OUT 卡 / presentation 内容窗口）
// 的统一高度上限与滚动条皮肤 —— 对齐 WorkBuddy 参考实现：每个内容块各自限高内滚，
// 出现多段彼此独立的滚动条。**不是卡级滚动**：严禁把 AgentMessageBubble /
// TurnContentStream 整体变成滚动容器（硬约束，勿回退）。
// 数值单位 px：antd-style createStyles 会把 number 序列化为 px；
// 既有测试断言 max-height:320px / max-height:260px 字面量，改值需同步测试断言。

/** 推理内容块限高（reasoningBody 展开体 / reasoningFullText 行为组内联正文，同源） */
export const CHAT_BLOCK_MAX_HEIGHT = 320;

/** 工具调用卡限高（toolcall IN/OUT 卡 docCard + presentation 卡 card，同源） */
export const CHAT_TOOLCARD_MAX_HEIGHT = 260;

/** presentation renderer 内容窗口限高（terminal/diff/read/search/web 共用 body） */
export const CHAT_RENDERER_BODY_MAX_HEIGHT = 224;

/**
 * 细滚动条皮肤：scrollbar-width: thin（Firefox）+ ::-webkit-scrollbar（Chromium）。
 * 颜色只走既有 caption token 低饱和 color-mix，不引入新颜色
 * （对齐用户批注 3「整体的配色和字体很好」）。
 */
export const thinScrollbarStyle = {
  scrollbarWidth: 'thin' as const,
  scrollbarColor:
    'color-mix(in srgb, var(--pudding-chat-text-caption) 45%, transparent) transparent',
  '&::-webkit-scrollbar': { width: 6, height: 6 },
  '&::-webkit-scrollbar-track': { background: 'transparent' },
  '&::-webkit-scrollbar-thumb': {
    background:
      'color-mix(in srgb, var(--pudding-chat-text-caption) 45%, transparent)',
    borderRadius: 3,
  },
  '&::-webkit-scrollbar-thumb:hover': {
    background:
      'color-mix(in srgb, var(--pudding-chat-text-caption) 70%, transparent)',
  },
};

/**
 * 键盘可滚动性配套焦点环：滚动容器必须可键盘聚焦（tabIndex={0}）并有可见焦点反馈，
 * 与行式 chrome 同款 running 色 outline（offset -2 贴边不外溢）。
 */
export const scrollFocusRingStyle = {
  '&:focus-visible': {
    outline: '2px solid var(--pudding-status-running)',
    outlineOffset: -2,
  },
};
