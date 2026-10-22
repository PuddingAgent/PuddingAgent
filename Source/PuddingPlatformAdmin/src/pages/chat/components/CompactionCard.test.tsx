import { act, render, screen } from '@testing-library/react';
import React from 'react';
import CompactionCard from './CompactionCard';
import type { CurrentRunActivity } from './processPreview';
const activity = (state: 'running' | 'completed' | 'failed' | 'unknown' | 'skipped' | 'checking' = 'running'): CurrentRunActivity => ({
  kind: 'system', variant: 'compaction', title: '上下文整理', status: 'running',
  compaction: { id: 'test', state, startedAt: Date.now() - 65_000, verifiedAt: Date.now() },
});
describe('compaction status strip', () => {
  it.each([
    ['preparing', '正在准备上下文摘要', true, '后台整理'],
    ['prepared', '摘要已准备好', true, '等待切换'],
    ['waiting', '正在整理上下文', false, '请求暂缓'],
    ['applying', '正在保存整理结果', false, '请求暂缓'],
  ] as const)('explains task impact during %s', (stage, label, background, badge) => {
    const value = activity();
    render(<CompactionCard activity={{ ...value, compaction: { ...value.compaction!, stage, background } }} />);
    expect(screen.getByText(label)).toBeTruthy();
    expect(screen.getAllByText(badge).length).toBeGreaterThan(0);
    expect(screen.getByText(/发送消息|当前任务继续/)).toBeTruthy();
    expect(screen.getByTestId('compaction-card').getAttribute('data-blocking')).toBe(String(!background));
  });
  it('animates only recently verified work, without a percentage', () => {
    const { container } = render(<CompactionCard activity={activity()} />);
    expect(screen.getByTestId('compaction-animation')).toBeTruthy();
    expect(screen.getByTestId('compaction-elapsed').textContent).toContain('1 分 5 秒');
    expect(container.textContent).not.toContain('%');
  });
  it('an old orphan start cannot animate or accumulate days of elapsed time', () => {
    render(<CompactionCard activity={{ ...activity(), compaction: undefined, startedAt: Date.now() - 9 * 86400000 }} />);
    expect(screen.getByTestId('compaction-card').getAttribute('data-state')).toBe('unknown');
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    expect(screen.queryByTestId('compaction-elapsed')).toBeNull();
  });
  it('losing server confirmation expires the animation without claiming failure', () => {
    jest.useFakeTimers();
    const { unmount } = render(<CompactionCard activity={activity()} />);
    act(() => jest.advanceTimersByTime(31_000));
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    expect(screen.getByTestId('compaction-card').getAttribute('data-state')).toBe('unknown');
    unmount(); jest.useRealTimers();
  });
  it.each(['completed', 'failed', 'skipped', 'unknown'] as const)('%s remains static', (state) => {
    render(<CompactionCard activity={activity(state)} />);
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    expect(screen.getByTestId('compaction-card').getAttribute('data-state')).toBe(state);
  });
  it('a ready candidate waits quietly instead of suggesting ongoing generation', () => {
    const value = activity();
    render(<CompactionCard activity={{ ...value, compaction: { ...value.compaction!, stage: 'prepared', background: true } }} />);
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    expect(screen.getByLabelText('上下文整理阶段').querySelector('[aria-current="step"]')?.textContent).toContain('等待切换');
  });
  it('blocking while generating does not claim the summary is already prepared', () => {
    const value = activity();
    render(<CompactionCard activity={{ ...value, compaction: { ...value.compaction!, stage: 'waiting' } }} />);
    const steps = screen.getByLabelText('上下文整理阶段');
    expect(steps.querySelector('[aria-current="step"]')?.textContent).toContain('准备摘要');
    expect(steps.querySelector('[data-step="done"]')).toBeNull();
  });
  it.each(['completed', 'skipped'] as const)('%s is collapsed but retains the complete record', (state) => {
    const full = '原始记录仍保留。完整诊断末尾';
    const { container } = render(<CompactionCard activity={{ ...activity(state), outputPreview: '截短预览', outputFull: full }} />);
    const details = container.querySelector('details');
    expect(details?.hasAttribute('open')).toBe(false);
    expect(details?.querySelector('summary')?.textContent).toContain('详情');
    expect(details?.textContent).toContain(full);
    expect(details?.textContent).not.toContain('截短预览');
  });
  it('failure keeps the explanation behind a disclosure, without asserting task continuation', () => {
    render(<CompactionCard activity={{ ...activity('failed'), outputPreview: 'summary_rejected' }} />);
    expect(screen.getByText('原始会话仍保留。展开详情查看原因。')).toBeTruthy();
    expect(screen.getByText('summary_rejected').closest('details')?.open).toBe(false);
    expect(screen.queryByText(/完成后继续/)).toBeNull();
  });
  it('only semantic status changes are announced, not every elapsed second', () => {
    jest.useFakeTimers();
    const { unmount } = render(<CompactionCard activity={activity()} />);
    const announcement = screen.getByRole('status').textContent;
    const before = screen.getByTestId('compaction-elapsed').textContent;
    act(() => jest.advanceTimersByTime(2000));
    expect(screen.getByRole('status').textContent).toBe(announcement);
    expect(screen.getByTestId('compaction-elapsed').textContent).not.toBe(before);
    expect(screen.getByRole('status').contains(screen.getByTestId('compaction-elapsed'))).toBe(false);
    unmount(); jest.useRealTimers();
  });
  it('a new confirmation revives animation after an expired lease', () => {
    jest.useFakeTimers();
    const value = activity();
    const { rerender, unmount } = render(<CompactionCard activity={value} />);
    act(() => jest.advanceTimersByTime(31_000));
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    rerender(<CompactionCard activity={{ ...value, compaction: { ...value.compaction!, verifiedAt: Date.now() } }} />);
    expect(screen.getByTestId('compaction-animation')).toBeTruthy();
    unmount(); jest.useRealTimers();
  });
  it('a submitted request has no animation or made-up phase', () => {
    render(<CompactionCard activity={activity('checking')} />);
    expect(screen.queryByTestId('compaction-animation')).toBeNull();
    expect(screen.queryByLabelText('上下文整理阶段')).toBeNull();
  });
  it('invalid timestamps never render NaN or Invalid Date', () => {
    const value = activity('completed');
    const { container } = render(<CompactionCard activity={{ ...value, compaction: { ...value.compaction!, startedAt: NaN, endedAt: Infinity } }} />);
    expect(container.textContent).not.toMatch(/NaN|Invalid Date/);
    expect(screen.queryByTestId('compaction-elapsed')).toBeNull();
  });
});
