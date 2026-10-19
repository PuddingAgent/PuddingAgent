// ── 右侧产物区尺寸 ──────────────────────────────────────────
//
// 起因（两轮实测）：
//   1. 第一版没有宽度上限，右侧面板一宽就把聊天列压到约 430px；
//   2. 加了 40% 上限后，用户仍无法**调**宽度 —— 面板要么太窄读不下文档，
//      要么写死一个值谁都不满意。用户原话："我还无法拖动这个文档区域的尺寸"。
//
// 结论：**宽度由用户拖**，代码只负责三条约束：
//   - 最小 320px（再窄只剩标题栏，没有阅读价值）；
//   - 最大 900px（再宽就变成"文档应用"，聊天沦为附注）；
//   - 整行 55% 的硬顶（CSS max-width，窗口很窄时防止面板吃掉聊天）。
//
// 为什么上限用像素而不是纯百分比：拖拽是**绝对宽度**操作。百分比会让
// 拖同样距离在不同窗口宽度下产生不同结果，手感不一致。
// 百分比只作为窗口过窄时的最后一道闸（maxWidth），不参与拖拽计算。
// ─────────────────────────────────────────────────────────

import type React from 'react';

/** 拖拽允许的最小宽度（px）。 */
export const SIDE_PANEL_MIN_WIDTH_PX = 320;

/** 拖拽允许的最大宽度（px）。 */
export const SIDE_PANEL_MAX_WIDTH_PX = 900;

/** 首次打开（localStorage 无记录）时的宽度。 */
export const SIDE_PANEL_DEFAULT_WIDTH_PX = 420;

/** 窗口很窄时的硬顶：面板最多占整行的这个百分比。 */
export const SIDE_PANEL_MAX_PERCENT = 55;

/** 宽度持久化键。 */
export const SIDE_PANEL_WIDTH_STORAGE_KEY = 'pudding.chat.sidePanelWidth';

/** 把任意宽度夹到允许区间。非有限数（NaN/Infinity）回落到默认值。 */
export function clampWidth(width: number): number {
  if (!Number.isFinite(width)) return SIDE_PANEL_DEFAULT_WIDTH_PX;
  return Math.min(SIDE_PANEL_MAX_WIDTH_PX, Math.max(SIDE_PANEL_MIN_WIDTH_PX, Math.round(width)));
}

/**
 * 拖拽后的新宽度。
 *
 * 面板在右侧，分隔条向左拖（`deltaX < 0`）意味着面板变宽，
 * 所以是 `start - delta`，不是 `start + delta`。
 */
export function widthAfterDrag(startWidth: number, deltaX: number): number {
  return clampWidth(startWidth - deltaX);
}

/** 从 localStorage 读回宽度；读不到 / 坏值 / 无 storage 一律回落默认。 */
export function loadSavedWidth(storage?: Pick<Storage, 'getItem'>): number {
  const store = storage ?? safeStorage();
  if (!store) return SIDE_PANEL_DEFAULT_WIDTH_PX;
  try {
    const raw = store.getItem(SIDE_PANEL_WIDTH_STORAGE_KEY);
    if (!raw) return SIDE_PANEL_DEFAULT_WIDTH_PX;
    const parsed = Number.parseInt(raw, 10);
    return clampWidth(parsed);
  } catch {
    return SIDE_PANEL_DEFAULT_WIDTH_PX;
  }
}

/** 写入宽度；失败（隐私模式、配额）静默忽略 —— 记住宽度是锦上添花，不该影响布局。 */
export function saveWidth(width: number, storage?: Pick<Storage, 'setItem'>): void {
  const store = storage ?? safeStorage();
  if (!store) return;
  try {
    store.setItem(SIDE_PANEL_WIDTH_STORAGE_KEY, String(clampWidth(width)));
  } catch {
    /* 忽略 */
  }
}

function safeStorage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

/**
 * 右侧产物区宿主的行内样式。
 *
 * `width` 来自用户拖拽；`maxWidth` 是窗口过窄时的硬顶；
 * `flexShrink: 0` 保证聊天列变窄时不会反过来压扁面板
 * （面板宽度是用户的选择，不该被内容挤掉）。
 */
export function sidePanelHostStyle(width: number): React.CSSProperties {
  return {
    width: `${clampWidth(width)}px`,
    maxWidth: `${SIDE_PANEL_MAX_PERCENT}%`,
    flex: '0 0 auto',
  };
}
