// ── 右侧产物区宽度约束 ──────────────────────────────────────
//
// 起因（实测）：右侧文档面板进 `ChatLayout` 的 flex 行之后，没有宽度上限，
// 内容一宽就把聊天列压到约 430px —— 消息正文几乎每行只剩两三个词。
// 用户判定："这一大片可以交互的区域"要能一边看一边聊，不是把聊天挤死。
//
// 所以定两条约束：
//   1. 右侧面板**最多占 40%** 宽度（相对整行，含侧栏）；
//   2. 低于 `SIDE_PANEL_MIN_WIDTH_PX` 时不再压缩（再窄就只剩标题栏，
//      不如维持可读的最小宽度，让聊天列先让位）。
//
// 为什么不做"聊天列绝对保底 480px"：在 1024 宽的窗口里
// （侧栏 230 + 聊天 480 + 面板 320 = 1030）会溢出，只能靠裁切，
// 反而把右侧内容截掉一半。用**比例上限 + 面板最小宽度**两头夹，
// 任何窗口宽度下都是可预测的。
// ─────────────────────────────────────────────────────────

import type React from 'react';

/** 右侧产物区最多占整行宽度的百分比。 */
export const SIDE_PANEL_MAX_PERCENT = 40;

/** 右侧产物区最小可读宽度（px）。 */
export const SIDE_PANEL_MIN_WIDTH_PX = 320;

/**
 * 右侧产物区宿主的行内样式。
 *
 * 用行内样式而不是 CSS 类，是为了让这条约束能被单测直接断言
 * （CSS-in-JS 在 jsdom 里不会生成可读的类名）。
 */
export function sidePanelHostStyle(): React.CSSProperties {
  return {
    flex: '0 1 auto',
    maxWidth: `${SIDE_PANEL_MAX_PERCENT}%`,
    minWidth: SIDE_PANEL_MIN_WIDTH_PX,
  };
}
