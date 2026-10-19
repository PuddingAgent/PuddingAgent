// ── 图表几何 ───────────────────────────────────────────────────
// 不引入图表库的理由：
//
// 我们要的是"26 个房间 24 小时的温度曲线"这类只读图表，不需要缩放、刷选、
// 联动。为此拉进 recharts（~100KB）或 echarts（~300KB+）不划算，而且
// Desktop 的 WebView2 兼容性还要额外验一遍。
//
// 数据已经过 chartSpec 校验与降采样（≤500 点），这里只做"值 → 坐标"的映射：
// 纯函数、可测、零依赖。
//
// 坐标约定：SVG 左上角为原点，y 向下。所以"值大"要映射成"y 小"。
// ─────────────────────────────────────────────────────────────

export interface Point {
  x: number;
  y: number;
}

export interface Domain {
  min: number;
  max: number;
}

export interface SeriesPoints {
  key: string;
  points: Point[];
}

const toNumber = (value: unknown): number | null => {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
};

/**
 * 计算所有序列共享的值域。
 *
 * 共享而非各自归一化：多序列图的意义就是比较高低，各归各的会让
 * 两条恒定的线看起来一样高。
 *
 * `min === max`（全等值）时向两侧各扩 1，否则后面的除零会得到 NaN，
 * SVG 里表现为整条线消失——排查起来很费劲。
 */
export function computeDomain(rows: Array<Record<string, unknown>>, yKeys: string[]): Domain {
  let min = Number.POSITIVE_INFINITY;
  let max = Number.NEGATIVE_INFINITY;

  for (const row of rows) {
    for (const key of yKeys) {
      const value = toNumber(row[key]);
      if (value === null) continue;
      if (value < min) min = value;
      if (value > max) max = value;
    }
  }

  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: 0, max: 1 };
  if (min === max) return { min: min - 1, max: max + 1 };
  return { min, max };
}

/** 把数值映射到绘图区内的高度（padding 以内），值大 → y 小。 */
export function scaleValue(
  value: number,
  domain: Domain,
  height: number,
  padding: number,
): number {
  const inner = Math.max(0, height - padding * 2);
  const span = domain.max - domain.min || 1;
  const ratio = (value - domain.min) / span;
  return padding + (1 - ratio) * inner;
}

/**
 * 计算每条序列的点。
 *
 * x 按索引均匀分布（`i / (n-1)`）；只有一行时放在水平正中间，
 * 否则 `n-1 === 0` 会除零。
 */
export function buildSeriesPoints(
  rows: Array<Record<string, unknown>>,
  yKeys: string[],
  width: number,
  height: number,
  padding: number,
): SeriesPoints[] {
  if (rows.length === 0) return yKeys.map((key) => ({ key, points: [] }));

  const domain = computeDomain(rows, yKeys);
  const innerWidth = Math.max(0, width - padding * 2);
  const denominator = rows.length > 1 ? rows.length - 1 : 0;

  return yKeys.map((key) => {
    const points: Point[] = [];
    rows.forEach((row, index) => {
      const value = toNumber(row[key]);
      if (value === null) return; // 缺点就是缺点，不补 0（补 0 会造出假谷）
      const x =
        denominator === 0 ? padding + innerWidth / 2 : padding + (index / denominator) * innerWidth;
      points.push({ x, y: scaleValue(value, domain, height, padding) });
    });
    return { key, points };
  });
}

/** 折线 path。少于 2 个点时返回空（单点用 circle 画，不画 0 长度线）。 */
export function buildLinePath(points: Point[]): string {
  if (points.length < 2) return '';
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${point.x.toFixed(2)},${point.y.toFixed(2)}`)
    .join(' ');
}

/** 面积 path：折线 + 回到底边闭合。 */
export function buildAreaPath(points: Point[], baselineY: number): string {
  if (points.length < 2) return '';
  const line = buildLinePath(points);
  const first = points[0];
  const last = points[points.length - 1];
  return `${line} L${last.x.toFixed(2)},${baselineY.toFixed(2)} L${first.x.toFixed(2)},${baselineY.toFixed(2)} Z`;
}

/** 柱宽：在绘图区宽度内均分，留 30% 间隙。 */
export function computeBarWidth(
  count: number,
  width: number,
  padding: number,
): number {
  if (count <= 0) return 0;
  const inner = Math.max(0, width - padding * 2);
  const slot = inner / count;
  return Math.max(1, slot * 0.7);
}
