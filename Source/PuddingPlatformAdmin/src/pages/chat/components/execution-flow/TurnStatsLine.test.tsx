// ── 行为链 §3.3: TurnStatsLine / formatDuration 测试 ─────────────────────
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  buildTurnStatsParts,
  TurnStatsLine,
} from './TurnStatsLine';
import {
  formatDurationMs,
  formatTokenCount,
} from '../../utils/formatDuration';

describe('formatDurationMs', () => {
  it.each([
    [undefined, null],
    [null, null],
    [-1, null],
    [Number.NaN, null],
    [0, '0ms'],
    [123, '123ms'],
    [999, '999ms'],
    [1200, '1.2s'],
    [1000, '1s'],
    [59_400, '59.4s'],
    [60_000, '1m00s'],
    [63_000, '1m03s'],
    [181_500, '3m01s'],
  ])('formatDurationMs(%p) → %p', (input, expected) => {
    expect(formatDurationMs(input as number | undefined | null)).toBe(expected);
  });
});

describe('formatTokenCount', () => {
  it.each([
    [undefined, null],
    [0, null],
    [42, '42 tokens'],
    [999, '999 tokens'],
    [4_200, '4.2k tokens'],
    [128_000, '128k tokens'],
  ])('formatTokenCount(%p) → %p', (input, expected) => {
    expect(formatTokenCount(input as number | undefined | null)).toBe(expected);
  });
});

describe('TurnStatsLine', () => {
  it('组装计量项（顺序固定：思考段 → 工具 → 时长 → token），缺失项省略', () => {
    expect(
      buildTurnStatsParts({
        reasoningSegments: 3,
        toolCount: 12,
        totalDurationMs: 181_000,
        totalTokens: 4_200,
      }),
    ).toEqual(['3 段思考', '12 工具', '3m01s', '4.2k tokens']);

    expect(
      buildTurnStatsParts({ reasoningSegments: 0, toolCount: 5 }),
    ).toEqual(['5 工具']);
  });

  it('渲染统计行；全缺失 → 不渲染', () => {
    const { rerender, container } = render(
      <TurnStatsLine reasoningSegments={2} toolCount={6} totalDurationMs={90_000} />,
    );
    const line = screen.getByTestId('turn-stats-line');
    expect(line.textContent).toContain('2 段思考');
    expect(line.textContent).toContain('6 工具');
    expect(line.textContent).toContain('1m30s');

    rerender(<TurnStatsLine />);
    expect(container.firstChild).toBeNull();
  });
});
