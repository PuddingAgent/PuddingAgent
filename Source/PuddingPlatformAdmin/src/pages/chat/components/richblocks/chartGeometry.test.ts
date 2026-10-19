import {
  buildAreaPath,
  buildLinePath,
  buildSeriesPoints,
  computeBarWidth,
  computeDomain,
  scaleValue,
} from './chartGeometry';

/**
 * 图表几何：容易错在"全等值除零"和"单行除零"，
 * 两者产生的 NaN 会让整条线在 SVG 里凭空消失，不报错、只看不到。
 */
const rows = [
  { t: '0', a: 10, b: 20 },
  { t: '1', a: 12, b: 18 },
  { t: '2', a: 8, b: 22 },
];

describe('computeDomain', () => {
  it('取所有序列的全局 min/max（共享值域才能比较高低）', () => {
    expect(computeDomain(rows, ['a', 'b'])).toEqual({ min: 8, max: 22 });
  });

  it('全等值时扩 ±1，避免除零', () => {
    const flat = [{ t: '0', a: 5 }, { t: '1', a: 5 }];
    expect(computeDomain(flat, ['a'])).toEqual({ min: 4, max: 6 });
  });

  it('没有数值时返回安全默认值', () => {
    expect(computeDomain([{ t: '0', a: 'n/a' }], ['a'])).toEqual({ min: 0, max: 1 });
  });
});

describe('scaleValue', () => {
  it('最大值映射到顶部（y 小），最小值到底部（y 大）', () => {
    const domain = { min: 0, max: 10 };
    expect(scaleValue(10, domain, 100, 10)).toBeCloseTo(10, 5);
    expect(scaleValue(0, domain, 100, 10)).toBeCloseTo(90, 5);
  });

  it('中间值落在中间', () => {
    expect(scaleValue(5, { min: 0, max: 10 }, 100, 10)).toBeCloseTo(50, 5);
  });
});

describe('buildSeriesPoints', () => {
  it('每条序列点数 = 行数', () => {
    const series = buildSeriesPoints(rows, ['a', 'b'], 300, 100, 10);
    expect(series).toHaveLength(2);
    expect(series[0].points).toHaveLength(3);
    expect(series[1].points).toHaveLength(3);
  });

  it('单行数据放在水平正中间，不除零', () => {
    const series = buildSeriesPoints([{ t: '0', a: 5 }], ['a'], 300, 100, 10);
    const point = series[0].points[0];
    expect(Number.isNaN(point.x)).toBe(false);
    expect(point.x).toBeCloseTo(150, 5);
  });

  it('缺值不补 0（补 0 会造出假谷）', () => {
    const withGap = [{ a: 10 }, { a: null }, { a: 20 }];
    const series = buildSeriesPoints(withGap, ['a'], 300, 100, 10);
    expect(series[0].points).toHaveLength(2);
  });

  it('所有坐标都是有限数（全等值也不出 NaN）', () => {
    const flat = [{ a: 5 }, { a: 5 }, { a: 5 }];
    const series = buildSeriesPoints(flat, ['a'], 300, 100, 10);
    for (const point of series[0].points) {
      expect(Number.isFinite(point.x)).toBe(true);
      expect(Number.isFinite(point.y)).toBe(true);
    }
  });
});

describe('buildLinePath / buildAreaPath', () => {
  it('少于两点返回空串', () => {
    expect(buildLinePath([])).toBe('');
    expect(buildLinePath([{ x: 1, y: 2 }])).toBe('');
  });

  it('首点用 M，后续用 L', () => {
    const path = buildLinePath([
      { x: 0, y: 10 },
      { x: 10, y: 20 },
    ]);
    expect(path.startsWith('M0.00,10.00')).toBe(true);
    expect(path).toContain('L10.00,20.00');
  });

  it('面积路径回到底边闭合', () => {
    const path = buildAreaPath(
      [
        { x: 0, y: 10 },
        { x: 10, y: 20 },
      ],
      90,
    );
    expect(path.endsWith('Z')).toBe(true);
    expect(path).toContain('L10.00,90.00');
  });
});

describe('computeBarWidth', () => {
  it('柱宽为正且随数量变小', () => {
    const few = computeBarWidth(3, 300, 10);
    const many = computeBarWidth(30, 300, 10);
    expect(few).toBeGreaterThan(many);
    expect(many).toBeGreaterThan(0);
  });

  it('数量为 0 时返回 0', () => {
    expect(computeBarWidth(0, 300, 10)).toBe(0);
  });
});
