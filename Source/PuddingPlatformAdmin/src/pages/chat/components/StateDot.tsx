// ── StateDot：状态语义点（P0-2，对齐 deepseek-harness D1）──────────────────
// 4 态：done | warning | ongoing | error
// - done/warning/error：10px 同色 halo（::before，10% opacity）+ 6px 实心 core（::after）
// - ongoing：3×3 矩阵外圈 8 cell 顺时针「像素追逐」
//   （负 animation-delay 相位、steps(1) flat keyframe 无补间、1s 循环）
// 颜色全部走 --pudding-status-* token，组件内零字面量色；根元素 aria-hidden。
// @media (prefers-reduced-motion: reduce) 下 ongoing 降级为静态单点。
import { createStyles } from 'antd-style';
import React from 'react';

export type StateDotState = 'done' | 'warning' | 'ongoing' | 'error';

export interface StateDotProps {
  /** 状态语义：done=成功 / warning=警告 / ongoing=进行中 / error=错误 */
  state: StateDotState;
  /** 点直径（px），默认 10（halo 直径；core = 0.6×） */
  size?: number;
}

/** ongoing：3×3 矩阵外圈 8 cell，顺时针（gridRow/gridColumn 从 1 起，中心留空） */
const PIXEL_CELLS: ReadonlyArray<{ row: number; col: number }> = [
  { row: 1, col: 1 },
  { row: 1, col: 2 },
  { row: 1, col: 3 },
  { row: 2, col: 3 },
  { row: 3, col: 3 },
  { row: 3, col: 2 },
  { row: 3, col: 1 },
  { row: 2, col: 1 },
];

/** 状态 → --pudding-status-* token（组件内唯一色来源，零字面量色） */
export const STATE_DOT_COLOR: Record<StateDotState, string> = {
  done: 'var(--pudding-status-success)',
  warning: 'var(--pudding-status-warning)',
  ongoing: 'var(--pudding-status-running)',
  error: 'var(--pudding-status-error)',
};

export const useStateDotStyles = createStyles(() => ({
  root: {
    position: 'relative',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 'var(--pudding-state-dot-size)',
    height: 'var(--pudding-state-dot-size)',
    flexShrink: 0,
    lineHeight: 0,
  },
  /** done/warning/error：10px 同色 halo（::before，10% opacity）+ 6px 实心 core（::after） */
  haloCore: {
    '&::before': {
      content: '""',
      position: 'absolute',
      inset: 0,
      borderRadius: '50%',
      background: 'currentColor',
      opacity: 0.1,
    },
    '&::after': {
      content: '""',
      width: 'calc(var(--pudding-state-dot-size) * 0.6)',
      height: 'calc(var(--pudding-state-dot-size) * 0.6)',
      borderRadius: '50%',
      background: 'currentColor',
    },
  },
  dotDone: { color: 'var(--pudding-status-success)' },
  dotWarning: { color: 'var(--pudding-status-warning)' },
  dotOngoing: { color: 'var(--pudding-status-running)' },
  dotError: { color: 'var(--pudding-status-error)' },
  /** ongoing：3×3 外圈像素网格（中心空；降级 = 静态单点） */
  pixelGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, calc(var(--pudding-state-dot-size) * 0.3))',
    gridTemplateRows: 'repeat(3, calc(var(--pudding-state-dot-size) * 0.3))',
    gap: 'calc(var(--pudding-state-dot-size) * 0.05)',
    color: 'currentColor',
    '@media (prefers-reduced-motion: reduce)': {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      '&::after': {
        content: '""',
        width: 'calc(var(--pudding-state-dot-size) * 0.3)',
        height: 'calc(var(--pudding-state-dot-size) * 0.3)',
        borderRadius: 1,
        background: 'currentColor',
      },
    },
  },
  pixelCell: {
    width: '100%',
    height: '100%',
    borderRadius: 1,
    background: 'currentColor',
    opacity: 0.18,
    animation: 'puddingPixelChase 1s steps(1, end) infinite',
    '@media (prefers-reduced-motion: reduce)': {
      display: 'none',
    },
  },
  '@keyframes puddingPixelChase': {
    '0%': { opacity: 1 },
    '12.5%': { opacity: 0.18 },
    '100%': { opacity: 0.18 },
  },
}));

const StateDot: React.FC<StateDotProps> = ({ state, size = 10 }) => {
  const { styles, cx } = useStateDotStyles();
  const stateClass = {
    done: styles.dotDone,
    warning: styles.dotWarning,
    ongoing: styles.dotOngoing,
    error: styles.dotError,
  }[state];

  return (
    <span
      className={cx(
        styles.root,
        stateClass,
        state !== 'ongoing' && styles.haloCore,
      )}
      style={
        { '--pudding-state-dot-size': `${size}px` } as React.CSSProperties
      }
      aria-hidden="true"
      data-state={state}
      data-testid="state-dot"
    >
      {state === 'ongoing' && (
        <span className={styles.pixelGrid}>
          {PIXEL_CELLS.map((cell, index) => (
            <span
              key={index}
              className={styles.pixelCell}
              style={{
                gridRow: cell.row,
                gridColumn: cell.col,
                animationDelay: `${-index * 0.125}s`,
              }}
              data-state-cell
            />
          ))}
        </span>
      )}
    </span>
  );
};

export default React.memo(StateDot);
