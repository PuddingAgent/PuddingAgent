// ── CompactionCard 契约测试 ──
// 三态语义（用户 2026-09-19 重设计后）：
//   运行中只表达「还在跑」——一行流光文本 + 已运行时长，不含假百分比；
//   完成/未完成只留一行居中标记，未完成必须把原因原样露出。
import { render, screen } from '@testing-library/react';
import React from 'react';
import CompactionCard from './CompactionCard';
import type { CurrentRunActivity } from './processPreview';

const makeActivity = (
  overrides: Partial<CurrentRunActivity> = {},
): CurrentRunActivity => ({
  kind: 'system',
  title: '正在压缩上下文',
  status: 'running',
  startedAt: Date.now() - 65_000,
  updatedAt: Date.now(),
  variant: 'compaction',
  ...overrides,
});

describe('CompactionCard', () => {
  it('运行中：标记 running，给出流光文本与已运行时长，不出现百分比', () => {
    const { container } = render(
      <CompactionCard activity={makeActivity()} />,
    );
    const card = screen.getByTestId('compaction-card');
    expect(card.getAttribute('data-state')).toBe('running');
    expect(screen.getByTestId('compaction-shimmer').textContent).toBe(
      '正在压缩上下文…',
    );
    expect(screen.getByTestId('compaction-elapsed').textContent).toContain(
      '已运行 1m 5s',
    );
    expect(screen.queryByTestId('compaction-marker')).toBeNull();
    expect(container.textContent).not.toContain('%');
  });

  it('完成：标记 completed，只留一行居中标记且不残留流光', () => {
    render(
      <CompactionCard
        activity={makeActivity({
          status: 'completed',
          title: '上下文压缩完成',
          startedAt: 1_000,
          updatedAt: 9_000,
        })}
      />,
    );
    const card = screen.getByTestId('compaction-card');
    expect(card.getAttribute('data-state')).toBe('completed');
    expect(screen.getByTestId('compaction-marker').textContent).toBe(
      '—— 已完成压缩 ✓ ——',
    );
    expect(screen.queryByTestId('compaction-shimmer')).toBeNull();
    expect(screen.getByTestId('compaction-elapsed').textContent).toContain(
      '耗时 8s',
    );
  });

  it('未完成：标记 interrupted，标记为未完成并原样展示原因', () => {
    render(
      <CompactionCard
        activity={makeActivity({
          status: 'failed',
          title: '运行事件失败',
          outputPreview: '压缩中断：进程重启前未写入终态记录',
        })}
      />,
    );
    const card = screen.getByTestId('compaction-card');
    expect(card.getAttribute('data-state')).toBe('interrupted');
    expect(screen.getByTestId('compaction-marker').textContent).toBe(
      '—— 压缩未完成 ——',
    );
    expect(
      screen.getByText('压缩中断：进程重启前未写入终态记录'),
    ).toBeTruthy();
    expect(screen.queryByTestId('compaction-shimmer')).toBeNull();
  });

  it('缺少起点时间时不编造时长', () => {
    render(
      <CompactionCard
        activity={makeActivity({ startedAt: undefined, updatedAt: undefined })}
      />,
    );
    expect(screen.queryByTestId('compaction-elapsed')).toBeNull();
  });
});
