import React, { useMemo } from 'react';
import { Empty, Tooltip, Typography } from 'antd';
import type { StorageInventoryClass, StorageInventoryTrendPoint } from './types';
import { formatBytes } from './api';

// ── 分类固定配色（图标点 + 颜色 + 文字标签同时呈现，不只靠颜色表达）──
export const STORAGE_CLASS_COLORS: Record<string, string> = {
  'diagnostics.debug-payload': '#f5222d',
  'diagnostics.telemetry-raw': '#fa8c16',
  'diagnostics.context-layer-raw': '#faad14',
  'diagnostics.runtime-activity': '#52c41a',
  'diagnostics.logs.verbose': '#13c2c2',
  'diagnostics.logs.error': '#eb2f96',
  'diagnostics.rollups': '#722ed1',
  'code-index.obsolete-scopes': '#2f54eb',
  'storage.redundant-indexes': '#a0d911',
  'evidence.conversation-events': '#8c8c8c',
};

const FALLBACK_COLOR = '#bfbfbf';

export function classColor(targetId: string): string {
  return STORAGE_CLASS_COLORS[targetId] ?? FALLBACK_COLOR;
}

function buildDonutSegments(classes: StorageInventoryClass[]) {
  const known = classes.filter(
    (item) => (item.estimatedBytes ?? 0) > 0,
  );
  const total = known.reduce((sum, item) => sum + (item.estimatedBytes ?? 0), 0);
  if (total <= 0) return { segments: [] as Array<{ targetId: string; fraction: number }>, total };

  const sorted = [...known].sort((a, b) => (b.estimatedBytes ?? 0) - (a.estimatedBytes ?? 0));
  let topFraction = 0;
  const top = sorted.slice(0, 6).map((item) => {
    const fraction = (item.estimatedBytes ?? 0) / total;
    topFraction += fraction;
    return { targetId: item.targetId, fraction };
  });
  if (topFraction < 0.999 && sorted.length > 6) {
    top.push({ targetId: '__other__', fraction: 1 - topFraction });
  }
  return { segments: top, total };
}

/** 分类占比圆环（SVG stroke 分段，不引入图表库依赖）。 */
export const StorageClassDonut: React.FC<{ classes: StorageInventoryClass[] }> = ({ classes }) => {
  const { segments, total } = useMemo(() => buildDonutSegments(classes), [classes]);
  const radius = 72;
  const circumference = 2 * Math.PI * radius;
  let offset = 0;

  return (
    <div style={{ display: 'flex', gap: 24, alignItems: 'center', flexWrap: 'wrap' }}>
      {total <= 0 ? (
        <Empty description="估算中，等待后台采样完成" image={Empty.PRESENTED_IMAGE_SIMPLE} />
      ) : (
        <>
          <svg width={180} height={180} viewBox="0 0 180 180" role="img" aria-label="存储分类占比圆环图">
            <circle cx="90" cy="90" r={radius} fill="none" stroke="#f0f0f0" strokeWidth="26" />
            {segments.map((segment) => {
              const dash = segment.fraction * circumference;
              const circle = (
                <circle
                  key={segment.targetId}
                  cx="90"
                  cy="90"
                  r={radius}
                  fill="none"
                  stroke={segment.targetId === '__other__' ? FALLBACK_COLOR : classColor(segment.targetId)}
                  strokeWidth="26"
                  strokeDasharray={`${dash} ${circumference - dash}`}
                  strokeDashoffset={-offset}
                  transform="rotate(-90 90 90)"
                />
              );
              offset += dash;
              return circle;
            })}
            <text x="90" y="86" textAnchor="middle" style={{ fontSize: 15, fill: 'rgba(0,0,0,0.88)' }}>
              约 {formatBytes(total)}
            </text>
            <text x="90" y="106" textAnchor="middle" style={{ fontSize: 11, fill: 'rgba(0,0,0,0.45)' }}>
              可清理分类估算合计
            </text>
          </svg>
          <div style={{ display: 'grid', gap: 6, minWidth: 220, flex: 1 }}>
            {segments.map((segment) => {
              const inventory = classes.find((item) => item.targetId === segment.targetId);
              return (
                <Tooltip
                  key={segment.targetId}
                  title={`${inventory?.displayName ?? '其他'}：约 ${formatBytes(inventory?.estimatedBytes ?? (total * segment.fraction))}`}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <span
                      aria-hidden
                      style={{
                        width: 10,
                        height: 10,
                        borderRadius: 2,
                        background:
                          segment.targetId === '__other__' ? FALLBACK_COLOR : classColor(segment.targetId),
                        display: 'inline-block',
                      }}
                    />
                    <Typography.Text style={{ flex: 1 }}>
                      {segment.targetId === '__other__' ? '其他' : inventory?.displayName ?? segment.targetId}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      ≈{formatBytes(inventory?.estimatedBytes ?? total * segment.fraction)}（
                      {(segment.fraction * 100).toFixed(1)}%）
                    </Typography.Text>
                  </div>
                </Tooltip>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
};

/** 趋势堆叠面积图（按天聚合历史快照点，纯 SVG）。 */
export const StorageTrendChart: React.FC<{
  points: StorageInventoryTrendPoint[];
  days: number;
  classNames: Record<string, string>;
}> = ({ points, days, classNames }) => {
  const chart = useMemo(() => {
    // 同天取最后一个点，最多保留 days 天。
    const byDay = new Map<string, StorageInventoryTrendPoint>();
    const cutoff = Date.now() - days * 24 * 3600 * 1000;
    const filtered = points.filter(
      (point) => new Date(point.capturedAtUtc).getTime() >= cutoff,
    );
    for (const point of filtered) {
      byDay.set(point.capturedAtUtc.slice(0, 10), point);
    }
    const daily = [...byDay.values()].sort((a, b) =>
      a.capturedAtUtc.localeCompare(b.capturedAtUtc),
    );
    if (daily.length < 2) return null;

    const targetIds = [...new Set(daily.flatMap((point) => Object.keys(point.classBytes)))].filter(
      (id) => id !== '__other__',
    );
    const width = 640;
    const height = 180;
    const padding = { left: 56, right: 12, top: 10, bottom: 24 };
    const plotWidth = width - padding.left - padding.right;
    const plotHeight = height - padding.top - padding.bottom;

    const totals = daily.map((point) =>
      targetIds.reduce((sum, id) => sum + (point.classBytes[id] ?? 0), 0),
    );
    const maxTotal = Math.max(...totals, 1);

    const x = (index: number) =>
      padding.left + (index / (daily.length - 1)) * plotWidth;
    const y = (value: number) => padding.top + plotHeight - (value / maxTotal) * plotHeight;

    // 堆叠：自底向上累积每类。
    const layers = targetIds.map((targetId) => {
      let cumulative = 0;
      const lower: Array<[number, number]> = [];
      const upper: Array<[number, number]> = [];
      daily.forEach((point, index) => {
        cumulative += point.classBytes[targetId] ?? 0;
        lower.push([x(index), y(cumulative - (point.classBytes[targetId] ?? 0))]);
        upper.push([x(index), y(cumulative)]);
      });
      const path =
        `M ${lower.map(([px, py]) => `${px.toFixed(1)} ${py.toFixed(1)}`).join(' L ')}` +
        ` L ${upper
          .reverse()
          .map(([px, py]) => `${px.toFixed(1)} ${py.toFixed(1)}`)
          .join(' L ')} Z`;
      return { targetId, path };
    });

    return {
      width,
      height,
      padding,
      plotHeight,
      layers,
      daily,
      maxTotal,
      targetIds,
    };
  }, [points, days]);

  if (!chart) {
    return (
      <Empty
        description="历史快照不足（每小时记录一个点），稍后可查看趋势"
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    );
  }

  return (
    <div>
      <svg
        width="100%"
        viewBox={`0 0 ${chart.width} ${chart.height}`}
        role="img"
        aria-label={`${days} 天存储趋势堆叠面积图`}
        preserveAspectRatio="none"
      >
        {chart.layers.map((layer) => (
          <path key={layer.targetId} d={layer.path} fill={classColor(layer.targetId)} opacity={0.85}>
            <title>{`${classNames[layer.targetId] ?? layer.targetId}`}</title>
          </path>
        ))}
        <line
          x1={chart.padding.left}
          y1={chart.height - chart.padding.bottom}
          x2={chart.width - chart.padding.right}
          y2={chart.height - chart.padding.bottom}
          stroke="rgba(0,0,0,0.25)"
        />
        <text
          x={chart.padding.left}
          y={chart.padding.top + 4}
          style={{ fontSize: 10, fill: 'rgba(0,0,0,0.45)' }}
        >
          ≈{formatBytes(chart.maxTotal)}
        </text>
        <text
          x={chart.padding.left}
          y={chart.height - 6}
          style={{ fontSize: 10, fill: 'rgba(0,0,0,0.45)' }}
        >
          {chart.daily[0].capturedAtUtc.slice(5, 10)}
        </text>
        <text
          x={chart.width - chart.padding.right}
          y={chart.height - 6}
          textAnchor="end"
          style={{ fontSize: 10, fill: 'rgba(0,0,0,0.45)' }}
        >
          {chart.daily[chart.daily.length - 1].capturedAtUtc.slice(5, 10)}
        </text>
      </svg>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12, marginTop: 8 }}>
        {chart.targetIds.map((targetId) => (
          <span key={targetId} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
            <span
              aria-hidden
              style={{
                width: 9,
                height: 9,
                borderRadius: 2,
                background: classColor(targetId),
                display: 'inline-block',
              }}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {classNames[targetId] ?? targetId}
            </Typography.Text>
          </span>
        ))}
      </div>
    </div>
  );
};
