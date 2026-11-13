// 存储页矩形树图的布局算法：squarified treemap。
//
// 为什么不引图表库：本页的环形图与趋势图都是手写 SVG（见同目录 StorageOverviewCharts.tsx），
// 而 treemap 需要的只是「按权重把一块矩形切成若干块」这一件事，为它引入整包图表库不划算。
//
// 为什么单独成文件：布局是与 React 无关的纯计算。独立成纯函数才能在没有 DOM 的 jest 里
// 直接验证不变量（面积正比 / 不重叠 / 铺满 / 不出界），而不是靠截图肉眼比对。

export interface TreemapRectInput {
  /** 条目标识，只用于回传，不参与几何计算。 */
  key: string;
  /** 权重，面积正比于它；只有 > 0 的有限值才代表一块真实面积。 */
  weight: number;
}

export interface TreemapItem {
  key: string;
  weight: number;
  /** 占有效权重总和的比例，∈ (0,1]。 */
  fraction: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface TreemapBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface LayoutRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * 行内最差长宽比（1 = 正方形，越大越细长）。
 *
 * 一行沿剩余矩形的短边铺开：设短边长为 side、行面积和为 sum，则行厚 t = sum/side，
 * 行内条目长度 l = area/t。于是最差比 = max(l_max/t, t/l_min)
 * = max(maxArea·side²/sum², sum²/(side²·minArea))。
 *
 * 只看 sum / min / max 三个统计量就能判断「再加一个条目会让这一行更方还是更扁」，
 * 不必先把每个矩形算出来再比较——这是贪心行能一趟扫完的前提。
 */
function worstAspectRatio(sum: number, side: number, minArea: number, maxArea: number): number {
  const sumSquared = sum * sum;
  const sideSquared = side * side;
  const longest = (maxArea * sideSquared) / sumSquared;
  const squat = sumSquared / (sideSquared * minArea);
  return Math.max(longest, squat);
}

/**
 * 当前行沿「短边」方向要占多厚。
 *
 * 非末行按面积比精确取；末行直接吃掉剩余尺寸——行厚是「父矩形尺寸 − 已占尺寸」累减
 * 出来的，末行若仍按面积比算，父矩形远端会残留一条极窄的缝，「铺满」就只是近似
 * 而非构造。厚度算不出正数（面积下溢到 0）时同样让它吃掉整块，避免 0 除把
 * Infinity 写进输出。
 */
function rowThickness(
  rowSum: number,
  spannedSide: number,
  fullSide: number,
  isLastRow: boolean,
): number {
  if (isLastRow) return fullSide;
  const raw = rowSum / spannedSide;
  if (!Number.isFinite(raw) || raw <= 0) return fullSide;
  return Math.min(raw, fullSide);
}

/**
 * squarified treemap 布局：把一组带权重的条目切成面积正比于权重的子矩形。
 *
 * 返回顺序按权重（面积）降序；同权重时保持输入顺序，使输出确定可回归。
 *
 * 退化输入的行为：空数组、全部权重非正、bounds 宽或高非正 → 返回 []；
 * 权重非有限值（NaN/±Infinity）与 <= 0 的条目被丢弃，其余按有效权重归一。
 * 相同 key 不做去重（视为两个独立条目）——本函数只负责几何，key 的唯一性是调用方的
 * 数据聚合职责，静默合并会改变面积口径并掩盖上游缺陷。
 */
export function layoutTreemap(items: TreemapRectInput[], bounds: TreemapBounds): TreemapItem[] {
  const boundsWidth = bounds.width;
  const boundsHeight = bounds.height;
  if (!Number.isFinite(boundsWidth) || !Number.isFinite(boundsHeight)) return [];
  if (!(boundsWidth > 0) || !(boundsHeight > 0)) return [];

  // 只保留有限正权重：<=0 无法表示面积，NaN/Infinity 会让后续每一步都变成 NaN。
  const kept: { key: string; weight: number; order: number }[] = [];
  for (const item of items) {
    if (!Number.isFinite(item.weight) || item.weight <= 0) continue;
    kept.push({ key: item.key, weight: item.weight, order: kept.length });
  }
  if (kept.length === 0) return [];

  // 先除以最大权重再求和：两个 1e308 直接相加会溢出成 Infinity，之后每个面积都是 NaN。
  // 归一化后每个 unit ∈ (0,1]、unitSum ≤ 条目数，既不溢出又保持单调。
  let maxWeight = 0;
  for (const item of kept) maxWeight = Math.max(maxWeight, item.weight);
  const units = kept.map((item) => ({
    key: item.key,
    weight: item.weight,
    order: item.order,
    unit: item.weight / maxWeight,
  }));

  let unitSum = 0;
  for (const item of units) unitSum += item.unit;

  units.sort((a, b) => b.unit - a.unit || a.order - b.order);

  const totalArea = boundsWidth * boundsHeight;
  const areaPerUnit = totalArea / unitSum;
  const areas = units.map((item) => item.unit * areaPerUnit);

  const placed: LayoutRect[] = [];
  let rest: LayoutRect = { x: bounds.x, y: bounds.y, width: boundsWidth, height: boundsHeight };
  let cursor = 0;

  while (cursor < areas.length) {
    // 短边 = 行能铺开的最大长度；squarified 始终让行平行于短边，切出来的块才不至于过长。
    const side = Math.min(rest.width, rest.height);

    // 贪心选行：只要再加入下一个条目能让本行最差长宽比不变差，就继续加。
    let rowSum = 0;
    let rowMin = Number.POSITIVE_INFINITY;
    let rowMax = 0;
    let rowCount = 0;
    let worst = Number.POSITIVE_INFINITY;
    while (cursor + rowCount < areas.length) {
      const area = areas[cursor + rowCount];
      const nextSum = rowSum + area;
      const nextMin = Math.min(rowMin, area);
      const nextMax = Math.max(rowMax, area);
      const nextWorst = worstAspectRatio(nextSum, side, nextMin, nextMax);
      if (rowCount > 0 && nextWorst > worst) break;
      rowSum = nextSum;
      rowMin = nextMin;
      rowMax = nextMax;
      worst = nextWorst;
      rowCount += 1;
    }

    const rowEnd = cursor + rowCount;
    const isLastRow = rowEnd >= areas.length;

    if (rest.width >= rest.height) {
      // 剩余矩形「宽 > 高」：短边是高，行是一条竖带，条目自上而下堆叠。
      const strip = rowThickness(rowSum, rest.height, rest.width, isLastRow);
      let offset = rest.y;
      for (let i = cursor; i < rowEnd; i += 1) {
        // 每块高度**只由它自己的面积决定**。
        // 刻意不让行内末条「吃掉浮点余量」：那等于做 rest.y + rest.height - offset 这种
        // 两个近似相等的大数相减，小块的尺寸会被抵消成 0 或被放大几十倍——实测 1e9 与
        // 1e-9 混合时末条面积相对误差达 5.4e1，而按面积直接还原时是 0。
        const height = !(strip > 0) ? rest.y + rest.height - offset : areas[i] / strip;
        placed.push({ x: rest.x, y: offset, width: strip, height });
        offset += height;
      }
      rest = { x: rest.x + strip, y: rest.y, width: rest.width - strip, height: rest.height };
    } else {
      // 剩余矩形「高 > 宽」：短边是宽，行是一条横带，条目自左而右排开。
      const strip = rowThickness(rowSum, rest.width, rest.height, isLastRow);
      let offset = rest.x;
      for (let i = cursor; i < rowEnd; i += 1) {
        // 同上：宽度只由自己的面积决定，不做大数相减式的「收口」。
        const width = !(strip > 0) ? rest.x + rest.width - offset : areas[i] / strip;
        placed.push({ x: offset, y: rest.y, width, height: strip });
        offset += width;
      }
      rest = { x: rest.x, y: rest.y + strip, width: rest.width, height: rest.height - strip };
    }

    cursor = rowEnd;
  }

  return units.map((item, index) => ({
    key: item.key,
    weight: item.weight,
    fraction: item.unit / unitSum,
    x: placed[index].x,
    y: placed[index].y,
    width: placed[index].width,
    height: placed[index].height,
  }));
}
