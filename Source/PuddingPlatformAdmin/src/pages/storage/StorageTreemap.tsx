import React, { useMemo } from 'react';
import { formatBytes } from './api';
import { classColor } from './StorageOverviewCharts';
import { layoutTreemap } from './treemapLayout';

// 存储分类占用矩形树图（受控展示组件）。
//
// 为什么几何全交给 treemapLayout.ts：面积怎么切是纯数学，独立成纯函数才能在无 DOM 的
// 测试里验证不变量（面积正比 / 铺满 / 不重叠）。组件这里只做「把算好的矩形画出来」，
// 一旦这里再出现第二套面积换算，两处口径必然漂移——所以 fraction 直接取布局返回值，
// 前端不做任何二次归一化。
//
// 为什么不引图表库：同页环形图与趋势图都是手写 SVG（StorageOverviewCharts.tsx），
// 悬浮提示用 SVG 原生 <title> 就够，为一张 treemap 引 d3 / antd Tooltip 不划算。

export interface StorageTreemapDatum {
  /** 数据类 targetId（如 'diagnostics.telemetry-raw'），用于取色与判定降级。 */
  targetId: string;
  /** 界面展示名（如「遥测原始数据」）。 */
  label: string;
  /** 字节数，作为 treemap 权重。 */
  bytes: number;
}

export interface StorageTreemapProps {
  data: StorageTreemapDatum[];
  /** SVG 逻辑宽高（viewBox 尺寸）。默认 640 × 320。 */
  width?: number;
  height?: number;
  /** 点击某个色块（S3 接入下钻时用）。本任务只透传，不做任何导航。 */
  onSelect?: (targetId: string) => void;
}

/** 默认 viewBox 尺寸：一屏内看得清占比，又不把页面撑高。 */
const DEFAULT_WIDTH = 640;
const DEFAULT_HEIGHT = 320;

/**
 * 文字标签的最小可见尺寸。
 *
 * SVG 不裁剪文本：块太小时字会溢出到相邻色块上，读起来像「标签属于右边那块」。
 * 所以宁可留空，也不在小碎块里塞字——碎块的信息交给 <title> 悬浮与读屏。
 */
const MIN_LABEL_WIDTH = 56;
const MIN_LABEL_HEIGHT = 24;

/** 单字约占的横向像素（fontSize 12 的经验值），用于把过长的分类名截断。 */
const CHAR_WIDTH = 12;

function isPositiveBytes(datum: StorageTreemapDatum): boolean {
  // NaN / Infinity 不能代表面积：布局会丢弃它们换算出的 NaN，导致整张图塌掉。
  return Number.isFinite(datum.bytes) && datum.bytes > 0;
}

/** 只做「把布局给的 fraction 写成百分比」，不做任何除法，避免第二套口径。 */
function percentText(fraction: number): string {
  return `${(fraction * 100).toFixed(1)}%`;
}

/** 分类名过长时按可用宽度截断，避免文字压到相邻块上。 */
function fitLabel(label: string, rectWidth: number): string {
  const maxChars = Math.floor((rectWidth - 8) / CHAR_WIDTH);
  if (maxChars <= 1) return '';
  return label.length <= maxChars ? label : `${label.slice(0, maxChars - 1)}…`;
}

export const StorageTreemap: React.FC<StorageTreemapProps> = ({
  data,
  width = DEFAULT_WIDTH,
  height = DEFAULT_HEIGHT,
  onSelect,
}) => {
  // 0 字节的分类不占面积，但仍要在数据里存在（页面会传全量数据类），先剔掉再算布局。
  const visible = useMemo(() => data.filter(isPositiveBytes), [data]);

  const items = useMemo(() => {
    // 只有一块时布局毫无意义（结果就是一个 100% 的单色矩形），直接走降级分支。
    if (visible.length < 2) return [];
    return layoutTreemap(
      visible.map((datum) => ({ key: datum.targetId, weight: datum.bytes })),
      { x: 0, y: 0, width, height },
    );
  }, [visible, width, height]);

  const datumByTarget = useMemo(
    () => new Map(visible.map((datum) => [datum.targetId, datum])),
    [visible],
  );

  if (visible.length === 0) {
    // 空态不画空 svg：一个没有矩形的坐标系只会让人以为「图挂了」。
    return (
      <div data-testid="treemap-empty" style={{ padding: '48px 0', color: '#8c8c8c' }}>
        暂无分类占用数据
      </div>
    );
  }

  if (visible.length === 1) {
    // 单块降级为一行文字：100% 的单色矩形不含任何信息量，是本轮改版明确要去掉的观感。
    const only = visible[0];
    return (
      <div data-testid="treemap-single" style={{ padding: '16px 0' }}>
        仅一个分类：
        {only.label}
        {' · '}
        {formatBytes(only.bytes)}
      </div>
    );
  }

  if (items.length === 0) {
    // 宽高非正等退化输入：布局算不出面积，同样退回空态文案而不是空 svg。
    return (
      <div data-testid="treemap-empty" style={{ padding: '48px 0', color: '#8c8c8c' }}>
        暂无分类占用数据
      </div>
    );
  }

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={`存储分类占用矩形树图，共 ${visible.length} 个分类`}
    >
      {items.map((item, index) => {
        const datum = datumByTarget.get(item.key);
        // key 由 targetId 派生，页面保证唯一；查不到说明数据在两次渲染间变了，跳过而不是画黑块。
        if (!datum) return null;

        const showLabel = item.width >= MIN_LABEL_WIDTH && item.height >= MIN_LABEL_HEIGHT;
        const labelText = showLabel ? fitLabel(datum.label, item.width) : '';
        const title = `${datum.label} · ${formatBytes(datum.bytes)} · ${percentText(item.fraction)}`;

        return (
          // 用 <g> 把矩形与它的文本绑在一起：文本是兄弟元素而非子元素（SVG <rect> 不能含子节点），
          // 分组后测试才能判定「这段文字属于这一块」。
          <g key={`${item.key}#${index}`}>
            <rect
              data-testid="treemap-cell"
              data-target-id={datum.targetId}
              x={item.x}
              y={item.y}
              width={item.width}
              height={item.height}
              fill={classColor(datum.targetId)}
              stroke="#ffffff"
              strokeWidth={1}
              role={onSelect ? 'button' : undefined}
              tabIndex={onSelect ? 0 : undefined}
              style={onSelect ? { cursor: 'pointer' } : undefined}
              onClick={onSelect ? () => onSelect(datum.targetId) : undefined}
            >
              <title>{title}</title>
            </rect>
            {labelText ? (
              <text
                data-testid="treemap-label"
                data-target-id={datum.targetId}
                x={item.x + item.width / 2}
                y={item.y + item.height / 2}
                textAnchor="middle"
                dominantBaseline="middle"
                fontSize={12}
                fill="#ffffff"
                stroke="rgba(0, 0, 0, 0.45)"
                strokeWidth={0.6}
                paintOrder="stroke"
                // 文本不接收指针事件：否则点标签等于点不到下面的矩形。
                pointerEvents="none"
              >
                {labelText}
              </text>
            ) : null}
          </g>
        );
      })}
    </svg>
  );
};

export default StorageTreemap;
