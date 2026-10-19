// ── 标注几何 ───────────────────────────────────────────────────
// 把"鼠标拖拽的像素段"换算成"归一化矩形"。
//
// 为什么要单独抽成纯函数：
// 1. 拖拽逻辑在 jsdom 里几乎测不了（没有真实布局与指针序列），但换算规则
//    恰恰是错得最隐蔽的地方——反向拖拽、超出边界、误触产生零尺寸框；
// 2. 同一套换算要同时服务 Web（mouse/pointer）与 Desktop（WebView2 指针），
//    抽出来只测一遍。
// ─────────────────────────────────────────────────────────────

export interface Point {
  x: number;
  y: number;
}

export interface NormalizedRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** 小于这个像素尺寸的拖拽视为误触，不产生标注。 */
export const MIN_DRAG_PX = 6;

export interface ContainerSize {
  width: number;
  height: number;
}

const clamp01 = (value: number): number => (value < 0 ? 0 : value > 1 ? 1 : value);

/**
 * 把拖拽起止点（容器内像素坐标）换算成归一化矩形。
 *
 * - 支持任意方向拖拽（end 可以小于 start）；
 * - 超出容器的部分被裁剪到边界；
 * - 宽或高小于 `MIN_DRAG_PX` 返回 null（防误触产生一条看不见的标注）；
 * - 容器尺寸为 0（未布局/隐藏）返回 null，避免除零得到 Infinity。
 */
export function normalizeRect(
  container: ContainerSize,
  start: Point,
  end: Point,
): NormalizedRect | null {
  if (container.width <= 0 || container.height <= 0) return null;

  const leftPx = Math.min(start.x, end.x);
  const topPx = Math.min(start.y, end.y);
  const widthPx = Math.abs(end.x - start.x);
  const heightPx = Math.abs(end.y - start.y);

  if (widthPx < MIN_DRAG_PX || heightPx < MIN_DRAG_PX) return null;

  const x = clamp01(leftPx / container.width);
  const y = clamp01(topPx / container.height);
  const right = clamp01((leftPx + widthPx) / container.width);
  const bottom = clamp01((topPx + heightPx) / container.height);

  const w = right - x;
  const h = bottom - y;
  // 完全落在容器外时 w/h 会变成 0。
  if (w <= 0 || h <= 0) return null;

  return { x, y, w, h };
}

/** 把容器内坐标点归一化（用于 note 的落点）。越界返回 null。 */
export function normalizePoint(
  container: ContainerSize,
  point: Point,
): Point | null {
  if (container.width <= 0 || container.height <= 0) return null;
  const x = point.x / container.width;
  const y = point.y / container.height;
  if (x < 0 || x > 1 || y < 0 || y > 1) return null;
  return { x, y };
}
