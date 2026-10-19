import React, { useMemo } from 'react';
import type { ChartSpec } from './chartSpec';
import { validateChartData } from './chartSpec';
import {
  buildAreaPath,
  buildLinePath,
  buildSeriesPoints,
  computeBarWidth,
  computeDomain,
  scaleValue,
} from './chartGeometry';

/**
 * 轻量 SVG 图表：line / bar / area。
 *
 * ## 为什么自己画
 *
 * 我们只需要只读展示，不需要缩放刷选联动。为此引入 recharts（~100KB）
 * 或 echarts（~300KB+）不划算，还要额外验 Desktop WebView2 兼容性。
 * 数据已由 chartSpec 校验并降采样到 ≤500 点，这里只做坐标映射。
 *
 * ## 没有坐标刻度是刻意的
 *
 * 在对话里，图表的作用是"看一眼趋势对不对"，精确读数应该点开制成品看表。
 * 加刻度会让 200px 高的图挤成一团。
 *
 * ## rows 由宿主传入
 *
 * `source.kind === 'artifact'` 时，宿主先按 artifactId 读出 rows；
 * 本组件不读任何存储。
 */
export interface MiniChartProps {
  spec: ChartSpec;
  /** 已解析出的数据行（artifact 来源也由宿主解析好）。 */
  rows: Array<Record<string, unknown>> | null | undefined;
  width?: number;
  height?: number;
}

const COLORS = ['#3370ff', '#00b42a', '#ff7d00', '#f53f3f', '#722ed1', '#13c2c2'];
const PADDING = 12;

export const MiniChart: React.FC<MiniChartProps> = ({
  spec,
  rows,
  width = 480,
  height = 200,
}) => {
  const validation = useMemo(() => validateChartData(spec, rows), [spec, rows]);

  if (!validation.ok) {
    return (
      <div
        data-testid="mini-chart-error"
        style={{
          padding: 12,
          border: '1px dashed #f53f3f',
          borderRadius: 8,
          fontSize: 12,
          color: '#f53f3f',
          background: '#fff1f0',
        }}
      >
        图表数据不可用：{validation.errors.join('；')}
      </div>
    );
  }

  const data = validation.rows;
  const series = buildSeriesPoints(data, spec.y, width, height, PADDING);
  const domain = computeDomain(data, spec.y);
  const baselineY = height - PADDING;
  const barWidth = spec.type === 'bar' ? computeBarWidth(data.length, width, PADDING) : 0;
  const innerWidth = Math.max(0, width - PADDING * 2);
  const slot = data.length > 0 ? innerWidth / data.length : 0;

  return (
    <figure
      data-testid="mini-chart"
      style={{ margin: '8px 0', border: '1px solid #e5e7eb', borderRadius: 10, padding: 10, background: '#fff' }}
    >
      {spec.title ? (
        <figcaption style={{ fontSize: 13, fontWeight: 600, marginBottom: 6 }}>
          {spec.title}
        </figcaption>
      ) : null}

      <svg
        data-testid="mini-chart-svg"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={spec.title ?? '图表'}
        style={{ width: '100%', height: 'auto', display: 'block' }}
      >
        {/* 基线 */}
        <line
          x1={PADDING}
          y1={baselineY}
          x2={width - PADDING}
          y2={baselineY}
          stroke="#e5e7eb"
          strokeWidth={1}
        />

        {spec.type === 'bar'
          ? series.map((item, seriesIndex) =>
              item.points.map((point, index) => {
                const barHeight = Math.max(0, baselineY - point.y);
                const groupWidth = barWidth / Math.max(1, series.length);
                return (
                  <rect
                    key={`${item.key}-${index}`}
                    data-testid={`chart-bar-${item.key}`}
                    x={PADDING + index * slot + (slot - barWidth) / 2 + seriesIndex * groupWidth}
                    y={point.y}
                    width={groupWidth}
                    height={barHeight}
                    fill={COLORS[seriesIndex % COLORS.length]}
                    opacity={0.85}
                  />
                );
              }),
            )
          : series.map((item, index) => {
              const color = COLORS[index % COLORS.length];
              if (spec.type === 'area') {
                return (
                  <path
                    key={item.key}
                    data-testid={`chart-area-${item.key}`}
                    d={buildAreaPath(item.points, baselineY)}
                    fill={color}
                    opacity={0.15}
                    stroke={color}
                    strokeWidth={1.5}
                  />
                );
              }
              if (spec.type === 'scatter') {
                return item.points.map((point, pointIndex) => (
                  <circle
                    key={`${item.key}-${pointIndex}`}
                    data-testid={`chart-point-${item.key}`}
                    cx={point.x}
                    cy={point.y}
                    r={2.5}
                    fill={color}
                  />
                ));
              }
              // line：单点时画圆，否则画路径（0 长度路径不可见）
              return item.points.length === 1 ? (
                <circle
                  key={item.key}
                  data-testid={`chart-point-${item.key}`}
                  cx={item.points[0].x}
                  cy={item.points[0].y}
                  r={3}
                  fill={color}
                />
              ) : (
                <path
                  key={item.key}
                  data-testid={`chart-line-${item.key}`}
                  d={buildLinePath(item.points)}
                  fill="none"
                  stroke={color}
                  strokeWidth={1.8}
                  strokeLinejoin="round"
                />
              );
            })}
      </svg>

      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', fontSize: 11, marginTop: 6 }}>
        {spec.y.map((key, index) => (
          <span key={key} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <i
              style={{
                width: 8,
                height: 8,
                borderRadius: 2,
                background: COLORS[index % COLORS.length],
                display: 'inline-block',
              }}
            />
            {key}
          </span>
        ))}
        {validation.droppedRows > 0 ? (
          <span data-testid="mini-chart-sampled" style={{ marginLeft: 'auto', opacity: 0.6 }}>
            已采样显示（省略 {validation.droppedRows} 点）
          </span>
        ) : null}
      </div>
    </figure>
  );
};

export default MiniChart;
