// ── CompactionCard 契约测试 ──
// 覆盖三态语义：运行中只表达「还在跑」（不定量扫描条 + 已运行时长，不含假百分比），
// 完成/未完成不残留运行态痕迹，未完成必须把原因原样露出。
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
  it('运行中：标记 running、给出已运行时长与扫描条，不出现百分比', () => {
    const { container } = render(
      <CompactionCard activity={makeActivity()} />,
    );
    const card = screen.getByTestId('compaction-card');
    expect(card.getAttribute('data-state')).toBe('running');
    expect(screen.getByText('正在压缩上下文')).toBeTruthy();
    expect(screen.getByTestId('compaction-elapsed').textContent).toContain(
      '已运行 1m 5s',
    );
    expect(screen.getByTestId('compaction-sweep')).toBeTruthy();
    expect(container.textContent).not.toContain('%');
    expect(container.textContent).toContain('压缩期间新消息进入队列');
  });

  it('完成：标记 completed，用完成文案收口且不残留扫描条', () => {
    const { container } = render(
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
    expect(screen.getByText('上下文压缩完成')).toBeTruthy();
    expect(screen.queryByTestId('compaction-sweep')).toBeNull();
    expect(screen.getByTestId('compaction-elapsed').textContent).toContain(
      '耗时 8s',
    );
    expect(container.textContent).toContain('摘要已写回上下文');
  });

  it('未完成：标记 interrupted，标题改为未完成并原样展示原因', () => {
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
    expect(screen.getByText('上下文压缩未完成')).toBeTruthy();
    expect(
      screen.getByText('压缩中断：进程重启前未写入终态记录'),
    ).toBeTruthy();
    expect(screen.queryByTestId('compaction-sweep')).toBeNull();
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
