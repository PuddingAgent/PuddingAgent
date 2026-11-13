import React, { useMemo } from 'react';
import { formatBytes } from './api';
import { classColor } from './StorageOverviewCharts';

// 每库构成的 100% 堆叠横条（受控展示组件）。
//
// 为什么分母取「段字节之和」而不是 row.totalBytes：
// totalBytes 是库文件的大小，里面会含不属于任何数据类的页（可复用空闲页、索引页、
// 尚未分类的页……）。这条横条回答的是「这个库由哪些数据类构成」，比例只对已分类段
// 有意义；拿 totalBytes 当分母会让每段都偏小、条上永远留一段解释不了的空白，
// 把「可回收的空闲页」误读成「某个分类占得少」。总量只用来在右侧显示文件大小。
//
// 为什么段宽写成百分比字符串而不是 flex-grow：
// flex-grow 的配比是隐式的（DOM 里看不到数值），jsdom 又完全不做布局，测试只能靠
// 几何测量才碰得到；写成 width="43.5%" 后断言直接读字符串数值，实现一改坏立刻取红。
//
// 为什么用 <svg> 而不是 <div style={{ width: '43.5%' }}>：
// <title> 是 HTML 规范里 head 专属的元素，React 19 会把渲染在任意位置的 <title>
// 提升（hoist）到 document.head —— 那样悬浮提示既丢失，测试也查不到它。SVG 命名
// 空间下的 <title> 是图形自身的无障碍/悬浮标题，不会被提升，正好承载段的明细文案。

export interface StackedBarSegment {
  /** 数据类 targetId，用于取色与判定。 */
  targetId: string;
  /** 展示名。 */
  label: string;
  /** 该段字节数。 */
  bytes: number;
}

export interface StackedBarRow {
  /** 库标识（如 'platform'）。 */
  key: string;
  /** 库展示名。 */
  label: string;
  /** 库总字节数，**只用于右侧文字**，不参与横条分段比例。 */
  totalBytes: number;
  segments: StackedBarSegment[];
}

export interface StorageStackedBarsProps {
  rows: StackedBarRow[];
  /** 横条高度（px），默认 10。 */
  barHeight?: number;
  /** 点击某段时回调（本任务只透传，不做导航）。 */
  onSelect?: (targetId: string) => void;
}

/** 默认横条高度：一屏内多库并排时要细，但要能点到。 */
const DEFAULT_BAR_HEIGHT = 10;

/** 横条底槽色：只用来承接小数进位留下的零点几个百分点的缝，不表达任何分类。 */
const TRACK_COLOR = '#f0f0f0';

/**
 * 该段是否参与构成。
 *
 * NaN / Infinity 不能当权重：1 个 NaN 会让分母变 NaN，整行宽度全成 'NaN%'，
 * 而 React 会丢掉非法样式 ⇒ 横条静默消失。负数同理（后端补页计数理论上不会为负，
 * 但这里宁可按 0 处理，也不让一行脏数据把整个面板画塌）。
 */
function isPositiveBytes(bytes: number): boolean {
  return Number.isFinite(bytes) && bytes > 0;
}

/**
 * 段宽的展示文本：保留一位小数。
 *
 * 整段占满时写成 '100%' 而不是 '100.0%' —— 它表达的是「全部」，不是估算出来的比例；
 * 一位小数只该出现在真的需要区分构成的地方。
 */
function percentText(fraction: number): string {
  if (fraction >= 1) return '100%';
  return `${(fraction * 100).toFixed(1)}%`;
}

interface PreparedSegment {
  segment: StackedBarSegment;
  /** 段宽文本（如 '43.5%'）。 */
  width: string;
  /** 段左边界相对横条的偏移文本，累计前面各段宽度。 */
  offset: string;
}

interface PreparedRow {
  row: StackedBarRow;
  segments: PreparedSegment[];
}

export const StorageStackedBars: React.FC<StorageStackedBarsProps> = ({
  rows,
  barHeight = DEFAULT_BAR_HEIGHT,
  onSelect,
}) => {
  // 归一化只算一次：分母、段宽、偏移三者必须来自同一套字节数，否则会出现
  // 「宽度加起来不等于 100%」的漂移。
  const prepared = useMemo<PreparedRow[]>(
    () =>
      rows.map((row) => {
        const positive = row.segments.filter((segment) => isPositiveBytes(segment.bytes));
        const denominator = positive.reduce((sum, segment) => sum + segment.bytes, 0);
        let cursor = 0;
        const segments = positive.map((segment) => {
          const fraction = denominator > 0 ? segment.bytes / denominator : 0;
          const item: PreparedSegment = {
            segment,
            width: percentText(fraction),
            offset: `${(cursor * 100).toFixed(3)}%`,
          };
          cursor += fraction;
          return item;
        });
        return { row, segments };
      }),
    [rows],
  );

  if (rows.length === 0) {
    // 空态不渲染任何行：一排没有横条的空行只会让人以为「数据没加载出来」。
    return (
      <div data-testid="stacked-bars-empty" style={{ padding: '48px 0', color: '#8c8c8c' }}>
        暂无库占用明细
      </div>
    );
  }

  return (
    <div style={{ display: 'grid', gap: 10 }}>
      {prepared.map(({ row, segments }) => (
        <div
          key={row.key}
          data-testid="stacked-row"
          data-database-key={row.key}
          style={{ display: 'flex', alignItems: 'center', gap: 12 }}
        >
          <span
            title={row.label}
            style={{
              flex: '0 0 120px',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {row.label}
          </span>
              {/* 只需一个条件：`segments` 已经是「过滤掉非正/非有限字节后」的段，
                  非空则各段 > 0 ⇒ 分母必 > 0。别处声称这里还要防 NaN/0 分母，
                  但上游 `isPositiveBytes` 已经把 NaN/Infinity/≤0 全剔了，那个条件不可达。 */}
              {segments.length === 0 ? (
            <span
              data-testid="stacked-row-empty"
              style={{ flex: 1, minWidth: 0, color: '#bfbfbf', fontSize: 12 }}
            >
              无占用明细
            </span>
          ) : (
            <svg
              data-testid="stacked-bar"
              width="100%"
              height={barHeight}
              role="img"
              aria-label={`${row.label} 数据类构成`}
              style={{ flex: 1, minWidth: 0, display: 'block' }}
            >
              <rect x="0" y="0" width="100%" height="100%" fill={TRACK_COLOR} />
              {segments.map(({ segment, width, offset }) => {
                const title = `${segment.label} · ${formatBytes(segment.bytes)} · ${width}`;
                return (
                  <rect
                    key={segment.targetId}
                    data-testid="stacked-segment"
                    data-target-id={segment.targetId}
                    x={offset}
                    y="0"
                    width={width}
                    height="100%"
                    fill={classColor(segment.targetId)}
                    role={onSelect ? 'button' : undefined}
                    tabIndex={onSelect ? 0 : undefined}
                    style={onSelect ? { cursor: 'pointer' } : undefined}
                    onClick={onSelect ? () => onSelect(segment.targetId) : undefined}
                  >
                    <title>{title}</title>
                  </rect>
                );
              })}
            </svg>
          )}
          <span
            data-testid="stacked-row-total"
            style={{
              flex: '0 0 auto',
              color: 'rgba(0, 0, 0, 0.65)',
              fontVariantNumeric: 'tabular-nums',
            }}
          >
            {formatBytes(row.totalBytes)}
          </span>
        </div>
      ))}
    </div>
  );
};

export default StorageStackedBars;
