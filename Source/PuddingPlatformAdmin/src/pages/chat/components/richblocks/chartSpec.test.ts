import type { ChartSpec } from './chartSpec';
import { DEFAULT_MAX_POINTS, downsampleRows, validateChartData } from './chartSpec';

/**
 * 图表数据校验：重点是"字段选错要早报错"，以及"大表不能直接丢进渲染"。
 */
const baseSpec: ChartSpec = {
  type: 'line',
  source: { kind: 'artifact', artifactId: 'vision-data-1' },
  x: 'time',
  y: ['kitchen', 'living'],
};

const rows = [
  { time: '00:00', kitchen: 24.1, living: 25.2 },
  { time: '01:00', kitchen: 23.8, living: 25.0 },
  { time: '02:00', kitchen: 23.6, living: 24.9 },
];

describe('validateChartData', () => {
  it('合法数据通过并原样返回', () => {
    const result = validateChartData(baseSpec, rows);
    expect(result.ok).toBe(true);
    expect(result.rows).toHaveLength(3);
    expect(result.droppedRows).toBe(0);
  });

  it('空数据报错（宿主没解析出 rows 时不能画空图）', () => {
    expect(validateChartData(baseSpec, []).ok).toBe(false);
    expect(validateChartData(baseSpec, null).ok).toBe(false);
    expect(validateChartData(baseSpec, undefined).errors[0]).toBe('没有数据行');
  });

  it('分类字段不存在时报错并指出字段名', () => {
    const result = validateChartData({ ...baseSpec, x: 'timestamp' }, rows);
    expect(result.ok).toBe(false);
    expect(result.errors).toContain('缺少分类字段：timestamp');
  });

  it('数值字段不存在时报错', () => {
    const result = validateChartData({ ...baseSpec, y: ['living', 'bedroom'] }, rows);
    expect(result.ok).toBe(false);
    expect(result.errors).toContain('缺少数值字段：bedroom');
  });

  it('字段全是非数值时报错', () => {
    const badRows = [{ time: '00:00', kitchen: 'n/a', living: '25.0' }];
    const result = validateChartData({ ...baseSpec, y: ['kitchen'] }, badRows);
    expect(result.ok).toBe(false);
    expect(result.errors).toContain('字段不是数值：kitchen');
  });

  it('数字字符串算数值（CSV 来的数据常见）', () => {
    const csvRows = [{ time: '00:00', kitchen: '24.1', living: '25.2' }];
    expect(validateChartData(baseSpec, csvRows).ok).toBe(true);
  });

  it('y 为空时报错', () => {
    expect(validateChartData({ ...baseSpec, y: [] }, rows).errors).toContain(
      '至少需要一个数值字段',
    );
  });

  it('超过 maxPoints 时降采样，并报告丢弃行数', () => {
    const many = Array.from({ length: 1000 }, (_, i) => ({
      time: `t${i}`,
      kitchen: i,
      living: i * 2,
    }));
    const result = validateChartData({ ...baseSpec, maxPoints: 100 }, many);

    expect(result.ok).toBe(true);
    expect(result.rows).toHaveLength(100);
    expect(result.droppedRows).toBe(900);
    // 首尾必须保留
    expect(result.rows[0]).toEqual(many[0]);
    expect(result.rows[result.rows.length - 1]).toEqual(many[many.length - 1]);
  });

  it('默认上限是 500', () => {
    expect(DEFAULT_MAX_POINTS).toBe(500);
  });
});

describe('downsampleRows', () => {
  it('不超上限时原样返回', () => {
    expect(downsampleRows(rows, 10)).toBe(rows);
  });

  it('maxPoints=1 时只保留首行', () => {
    expect(downsampleRows(rows, 1)).toEqual([rows[0]]);
  });

  it('采样结果不重复索引', () => {
    const many = Array.from({ length: 10 }, (_, i) => ({ i }));
    const sampled = downsampleRows(many, 3);
    const indexes = sampled.map((row) => row.i as number);
    expect(new Set(indexes).size).toBe(indexes.length);
  });
});
