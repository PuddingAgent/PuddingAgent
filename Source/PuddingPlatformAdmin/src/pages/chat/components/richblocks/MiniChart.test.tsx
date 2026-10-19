import { render, screen } from '@testing-library/react';
import * as React from 'react';
import { MiniChart } from './MiniChart';
import type { ChartSpec } from './chartSpec';

/**
 * MiniChart：数据不可用要显性报错（不能静默画空白图），
 * 合法数据要真的产出 SVG 图元。
 */
const rows = [
  { t: '00:00', kitchen: 24.1, living: 25.2 },
  { t: '01:00', kitchen: 23.8, living: 25.0 },
  { t: '02:00', kitchen: 23.6, living: 24.9 },
];

const spec = (over: Partial<ChartSpec> = {}): ChartSpec => ({
  type: 'line',
  source: { kind: 'artifact', artifactId: 'a1' },
  x: 't',
  y: ['kitchen', 'living'],
  title: '温度趋势',
  ...over,
});

describe('MiniChart', () => {
  it('合法数据渲染两条折线', () => {
    render(<MiniChart spec={spec()} rows={rows} />);

    expect(screen.getByTestId('mini-chart-svg')).toBeTruthy();
    expect(screen.getByTestId('chart-line-kitchen')).toBeTruthy();
    expect(screen.getByTestId('chart-line-living')).toBeTruthy();
  });

  it('bar 类型渲染柱', () => {
    render(<MiniChart spec={spec({ type: 'bar', y: ['kitchen'] })} rows={rows} />);
    expect(screen.getAllByTestId('chart-bar-kitchen')).toHaveLength(3);
  });

  it('area 类型渲染面积路径', () => {
    render(<MiniChart spec={spec({ type: 'area', y: ['kitchen'] })} rows={rows} />);
    expect(screen.getByTestId('chart-area-kitchen')).toBeTruthy();
  });

  it('单点用 circle 而不是 0 长度路径', () => {
    render(<MiniChart spec={spec({ y: ['kitchen'] })} rows={[rows[0]]} />);
    expect(screen.getByTestId('chart-point-kitchen')).toBeTruthy();
    expect(screen.queryByTestId('chart-line-kitchen')).toBeNull();
  });

  it('数据不可用时显性报错，而不是画空白图', () => {
    render(<MiniChart spec={spec()} rows={[]} />);

    expect(screen.getByTestId('mini-chart-error')).toBeTruthy();
    expect(screen.queryByTestId('mini-chart-svg')).toBeNull();
    expect(screen.getByTestId('mini-chart-error').textContent).toContain('没有数据行');
  });

  it('字段选错时错误信息带字段名', () => {
    render(<MiniChart spec={spec({ y: ['bedroom'] })} rows={rows} />);
    expect(screen.getByTestId('mini-chart-error').textContent).toContain('bedroom');
  });

  it('超上限时提示已采样', () => {
    const many = Array.from({ length: 300 }, (_, i) => ({ t: `t${i}`, kitchen: i }));
    render(<MiniChart spec={spec({ y: ['kitchen'], maxPoints: 50 })} rows={many} />);

    expect(screen.getByTestId('mini-chart-sampled').textContent).toContain('250');
  });

  it('不超上限时不显示采样提示', () => {
    render(<MiniChart spec={spec()} rows={rows} />);
    expect(screen.queryByTestId('mini-chart-sampled')).toBeNull();
  });

  it('图例显示所有序列名', () => {
    render(<MiniChart spec={spec()} rows={rows} />);
    expect(screen.getByText('kitchen')).toBeTruthy();
    expect(screen.getByText('living')).toBeTruthy();
  });
});
