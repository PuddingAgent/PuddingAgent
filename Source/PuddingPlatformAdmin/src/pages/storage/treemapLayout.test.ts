import type { TreemapBounds, TreemapItem, TreemapRectInput } from './treemapLayout';
import { layoutTreemap } from './treemapLayout';

// ── 布局不变量的定向测试 ──────────────────────────────────────────
// 这里钉的是**几何契约**而不是外观：
//   ① 面积必须正比于权重（否则「占比」说的是假话）；
//   ② 子矩形之间不许重叠、合起来必须铺满父矩形（否则图上会出现视觉空洞或重叠误导）；
//   ③ 任何退化输入都不许吐 NaN / Infinity（SVG 里一个 NaN 就会让整块图消失）；
//   ④ squarified 的收益要能被量化——和「按权重简单横切」比最差长宽比，实测数字必须更小。

const EPS = 1e-9;

function area(bounds: TreemapBounds): number {
  return bounds.width * bounds.height;
}

/** 相对误差；期望值为 0 时退化成绝对误差。 */
function relError(actual: number, expected: number): number {
  if (expected === 0) return Math.abs(actual);
  return Math.abs(actual - expected) / Math.abs(expected);
}

function rectArea(rect: { width: number; height: number }): number {
  return rect.width * rect.height;
}

/** 每个输出字段都必须是有限数，宽高必须非负——这是所有用例的公共底线。 */
function expectFiniteRects(rects: TreemapItem[]): void {
  for (const rect of rects) {
    expect(Number.isNaN(rect.weight)).toBe(false);
    expect(Number.isNaN(rect.fraction)).toBe(false);
    expect(Number.isFinite(rect.weight)).toBe(true);
    expect(Number.isFinite(rect.fraction)).toBe(true);
    expect(Number.isFinite(rect.x)).toBe(true);
    expect(Number.isFinite(rect.y)).toBe(true);
    expect(Number.isFinite(rect.width)).toBe(true);
    expect(Number.isFinite(rect.height)).toBe(true);
    expect(rect.width).toBeGreaterThanOrEqual(0);
    expect(rect.height).toBeGreaterThanOrEqual(0);
  }
}

/** 两个矩形的交集面积（边重合算 0）。 */
function intersectionArea(a: TreemapItem, b: TreemapItem): number {
  const left = Math.max(a.x, b.x);
  const right = Math.min(a.x + a.width, b.x + b.width);
  const top = Math.max(a.y, b.y);
  const bottom = Math.min(a.y + a.height, b.y + b.height);
  if (right <= left || bottom <= top) return 0;
  return (right - left) * (bottom - top);
}

/** 断言一整套不变量：有限数 / 面积正比 / 不重叠 / 铺满 / 不出界 / fraction 归一。 */
function expectLayoutInvariants(rects: TreemapItem[], bounds: TreemapBounds): void {
  expectFiniteRects(rects);

  const parentArea = area(bounds);
  const toleratedSlack = EPS * Math.max(bounds.width, bounds.height);

  let fractionSum = 0;
  let placedArea = 0;
  for (const rect of rects) {
    fractionSum += rect.fraction;
    placedArea += rectArea(rect);

    // 面积正比：单个矩形面积 / 父矩形面积 必须等于它的权重占比。
    expect(relError(rectArea(rect) / parentArea, rect.fraction)).toBeLessThanOrEqual(EPS);

    // 不出界。
    expect(rect.x).toBeGreaterThanOrEqual(bounds.x - toleratedSlack);
    expect(rect.y).toBeGreaterThanOrEqual(bounds.y - toleratedSlack);
    expect(rect.x + rect.width).toBeLessThanOrEqual(bounds.x + bounds.width + toleratedSlack);
    expect(rect.y + rect.height).toBeLessThanOrEqual(bounds.y + bounds.height + toleratedSlack);
  }

  // fraction 归一。
  expect(relError(fractionSum, 1)).toBeLessThanOrEqual(EPS);

  // 铺满。
  expect(relError(placedArea, parentArea)).toBeLessThanOrEqual(EPS);

  // 不重叠。
  for (let i = 0; i < rects.length; i += 1) {
    for (let j = i + 1; j < rects.length; j += 1) {
      expect(intersectionArea(rects[i], rects[j])).toBeLessThanOrEqual(EPS);
    }
  }
}

function worstAspect(rects: { width: number; height: number }[]): number {
  let worst = 1;
  for (const rect of rects) {
    const long = Math.max(rect.width, rect.height);
    const short = Math.min(rect.width, rect.height);
    if (!(short > 0)) continue;
    worst = Math.max(worst, long / short);
  }
  return worst;
}

/**
 * 对照组：朴素切法。按权重一次性沿父矩形的长边切成通高的竖条（或通宽的横条）。
 * 这是不实现 squarify 时最直观的写法，也是本任务要比较的基线。
 */
function naiveSliceRects(
  items: TreemapRectInput[],
  bounds: TreemapBounds,
): { width: number; height: number }[] {
  const total = items.reduce((sum, item) => sum + item.weight, 0);
  const rects: { width: number; height: number }[] = [];
  if (bounds.width >= bounds.height) {
    for (const item of items) {
      rects.push({ width: (bounds.width * item.weight) / total, height: bounds.height });
    }
  } else {
    for (const item of items) {
      rects.push({ width: bounds.width, height: (bounds.height * item.weight) / total });
    }
  }
  return rects;
}

function makeItems(weights: number[]): TreemapRectInput[] {
  return weights.map((weight, index) => ({ key: `k${index}`, weight }));
}

describe('layoutTreemap 退化输入', () => {
  const bounds: TreemapBounds = { x: 0, y: 0, width: 600, height: 400 };

  it('空输入返回空数组', () => {
    expect(layoutTreemap([], bounds)).toEqual([]);
  });

  it('全部权重非正返回空数组', () => {
    expect(layoutTreemap(makeItems([0, -1, -0.5]), bounds)).toEqual([]);
  });

  it('bounds 宽或高非正返回空数组', () => {
    const items = makeItems([1, 2, 3]);
    expect(layoutTreemap(items, { x: 0, y: 0, width: 0, height: 400 })).toEqual([]);
    expect(layoutTreemap(items, { x: 0, y: 0, width: 600, height: 0 })).toEqual([]);
    expect(layoutTreemap(items, { x: 0, y: 0, width: -600, height: 400 })).toEqual([]);
    expect(layoutTreemap(items, { x: 0, y: 0, width: Number.NaN, height: 400 })).toEqual([]);
  });

  it('只有一个正值条目时矩形恰好等于 bounds，fraction 为 1', () => {
    const [only] = layoutTreemap(makeItems([7]), bounds);
    expect(only).toEqual({
      key: 'k0',
      weight: 7,
      fraction: 1,
      x: 0,
      y: 0,
      width: 600,
      height: 400,
    });
  });

  it('丢弃非正权重条目，其余按有效权重归一', () => {
    const rects = layoutTreemap(
      [
        { key: 'a', weight: 3 },
        { key: 'zero', weight: 0 },
        { key: 'negative', weight: -2 },
        { key: 'nan', weight: Number.NaN },
        { key: 'b', weight: 1 },
      ],
      bounds,
    );
    expect(rects.map((rect) => rect.key)).toEqual(['a', 'b']);
    expect(rects.map((rect) => rect.fraction)).toEqual([3 / 4, 1 / 4]);
    expectLayoutInvariants(rects, bounds);
  });

  it('相同 key 重复出现视为两个独立条目（不去重）', () => {
    const rects = layoutTreemap(
      [
        { key: 'dup', weight: 2 },
        { key: 'dup', weight: 1 },
      ],
      bounds,
    );
    // 理由：本函数只管几何。key 的唯一性是调用方数据聚合的职责；
    // 静默合并会改变面积口径，并把上游的重复行缺陷藏起来。
    expect(rects).toHaveLength(2);
    expect(rects.every((rect) => rect.key === 'dup')).toBe(true);
    expectLayoutInvariants(rects, bounds);
  });
});

describe('layoutTreemap 几何不变量', () => {
  const cases: { name: string; weights: number[]; bounds: TreemapBounds }[] = [
    { name: '经典 squarified 用例（宽矩形）', weights: [6, 6, 4, 3, 2, 2, 1], bounds: { x: 0, y: 0, width: 600, height: 400 } },
    { name: '经典 squarified 用例（高矩形）', weights: [6, 6, 4, 3, 2, 2, 1], bounds: { x: 0, y: 0, width: 200, height: 1200 } },
    { name: '等权 10 项', weights: [1, 1, 1, 1, 1, 1, 1, 1, 1, 1], bounds: { x: 0, y: 0, width: 1000, height: 300 } },
    { name: '斐波那契梯度', weights: [37, 21, 13, 8, 5, 3, 2, 1], bounds: { x: 0, y: 0, width: 800, height: 800 } },
    { name: '带偏移原点的 bounds', weights: [5, 3, 2], bounds: { x: 37.5, y: -12.25, width: 450, height: 320 } },
  ];

  for (const testCase of cases) {
    it(`面积正比 / 不重叠 / 铺满 / 不出界：${testCase.name}`, () => {
      const rects = layoutTreemap(makeItems(testCase.weights), testCase.bounds);
      expect(rects).toHaveLength(testCase.weights.length);
      expectLayoutInvariants(rects, testCase.bounds);
    });
  }

  it('输出按权重降序，同权重保持输入顺序', () => {
    const rects = layoutTreemap(
      [
        { key: 'small', weight: 1 },
        { key: 'big', weight: 9 },
        { key: 'mid-a', weight: 4 },
        { key: 'mid-b', weight: 4 },
      ],
      { x: 0, y: 0, width: 600, height: 400 },
    );
    expect(rects.map((rect) => rect.key)).toEqual(['big', 'mid-a', 'mid-b', 'small']);
    expect(rects.map((rect) => rect.fraction)).toEqual([9 / 18, 4 / 18, 4 / 18, 1 / 18]);
  });

  it('不修改入参（items 与 bounds 均被冻结，且有快照对照）', () => {
    const items: TreemapRectInput[] = [
      { key: 'a', weight: 3 },
      { key: 'b', weight: 1 },
      { key: 'c', weight: 0 },
    ];
    const bounds: TreemapBounds = { x: 0, y: 0, width: 600, height: 400 };
    const itemsSnapshot = JSON.stringify(items);
    const boundsSnapshot = JSON.stringify(bounds);

    items.forEach((item) => Object.freeze(item));
    Object.freeze(items);
    Object.freeze(bounds);

    expect(() => layoutTreemap(items, bounds)).not.toThrow();
    expect(JSON.stringify(items)).toBe(itemsSnapshot);
    expect(JSON.stringify(bounds)).toBe(boundsSnapshot);
  });
});

describe('layoutTreemap 极端量级', () => {
  const bounds: TreemapBounds = { x: 0, y: 0, width: 1000, height: 600 };

  it('1e9 与 1e-9 混合：两项面积都严格正比（≤1e-9），小块不被抹成零面积', () => {
    const rects = layoutTreemap(
      [
        { key: 'huge', weight: 1e9 },
        { key: 'tiny', weight: 1e-9 },
      ],
      bounds,
    );
    expectLayoutInvariants(rects, bounds);

    // 权重比 1e18 看似超出 double 分辨率，但尺寸是「面积 / 行厚」除出来的，不需要
    // 大数相减，因此 1e-18 占比仍能得到正宽矩形（实测 width=6e-16、面积相对误差 0）。
    // 注：这个用例在旧写法下也能过——两条目时小块落在「按宽度切」的分支，恰好没走到
    // 出问题的那条。真正能抓住该回归的是下面「跨 18 个量级的多条目混合」，实测旧写法 5.4e1。
    const tiny = rects[1];
    expect(tiny.key).toBe('tiny');
    expect(tiny.width).toBeGreaterThan(0);
    expect(tiny.height).toBeGreaterThan(0);
  });

  it('跨 18 个量级的多条目混合：每一项面积都严格正比（≤1e-9）', () => {
    // 这是「行内末条吃掉浮点余量」那个写法的真实失败形态：1e-18 占比的条目
    // 高度被从 5.94e-9 放大到 3.28e-7，面积相对误差 5.4e1。
    const bounds1000x600: TreemapBounds = { x: 0, y: 0, width: 1000, height: 600 };
    const rects = layoutTreemap(
      [
        { key: 'huge', weight: 1e9 },
        { key: 'tiny', weight: 1e-9 },
        { key: 'one', weight: 1 },
        { key: 'mid', weight: 100 },
      ],
      bounds1000x600,
    );
    expect(rects).toHaveLength(4);
    expectLayoutInvariants(rects, bounds1000x600);
  });

  it('量级差 1e6、最小占比 1e-6 时逐项面积仍严格正比（≤1e-9）', () => {
    const rects = layoutTreemap(
      [
        { key: 'big', weight: 1e4 },
        { key: 'mid', weight: 1 },
        { key: 'small', weight: 1e-2 },
      ],
      bounds,
    );
    expect(rects).toHaveLength(3);
    expectLayoutInvariants(rects, bounds);
  });

  it('最小占比 1e-9 时逐项面积仍严格正比（≤1e-9）', () => {
    const rects = layoutTreemap(
      [
        { key: 'big', weight: 1e6 },
        { key: 'mid', weight: 1 },
        { key: 'small', weight: 1e-3 },
      ],
      bounds,
    );
    expect(rects).toHaveLength(3);
    expectLayoutInvariants(rects, bounds);
  });

  it('两个极大权重不会因求和溢出而变成 NaN', () => {
    const rects = layoutTreemap(
      [
        { key: 'a', weight: 1e308 },
        { key: 'b', weight: 1e308 },
      ],
      bounds,
    );
    expectFiniteRects(rects);
    expect(rects.map((rect) => rect.fraction)).toEqual([0.5, 0.5]);
    expect(rects[0].width * rects[0].height).toBeCloseTo(area(bounds) / 2, 6);
  });
});

describe('layoutTreemap 长宽比：squarified 对朴素切片的收益', () => {
  it('经典用例 [6,6,4,3,2,2,1] 的最差长宽比明显更小', () => {
    const weights = [6, 6, 4, 3, 2, 2, 1];
    const bounds: TreemapBounds = { x: 0, y: 0, width: 600, height: 400 };
    const items = makeItems(weights);

    const squarified = layoutTreemap(items, bounds);
    const squarifiedWorst = worstAspect(squarified);
    const naiveWorst = worstAspect(naiveSliceRects(items, bounds));

    // 实测数字随 jest 输出一起留痕，报告里引用的就是这两个值。
    console.log(
      `[aspect 6,6,4,3,2,2,1 @600x400] squarified worst=${squarifiedWorst.toFixed(6)} naive slice worst=${naiveWorst.toFixed(6)}`,
    );

    expect(squarifiedWorst).toBeLessThan(naiveWorst);
    // 绝对上限防止「对手更烂」式通过：squarified 的收益应当是可感知的，而不是勉强赢一点。
    expect(squarifiedWorst).toBeLessThan(3);
  });

  it('等权 8 项在正方形里同样优于朴素切片', () => {
    const weights = [1, 1, 1, 1, 1, 1, 1, 1];
    const bounds: TreemapBounds = { x: 0, y: 0, width: 300, height: 300 };
    const items = makeItems(weights);

    const squarifiedWorst = worstAspect(layoutTreemap(items, bounds));
    const naiveWorst = worstAspect(naiveSliceRects(items, bounds));

    console.log(
      `[aspect 8x1 @300x300] squarified worst=${squarifiedWorst.toFixed(6)} naive slice worst=${naiveWorst.toFixed(6)}`,
    );

    expect(squarifiedWorst).toBeLessThan(naiveWorst);
    // 等权 8 项在正方形里：朴素切片是 8 条 37.5x300（比 8.0），
    // squarified 收成 3+2+2+1 的参差块，最差比 2.88——收益要能被量化，
    // 所以除「比基线小」外还要卡一个绝对上限，防止「对手更烂」式通过。
    expect(squarifiedWorst).toBeLessThan(3);
  });
});
