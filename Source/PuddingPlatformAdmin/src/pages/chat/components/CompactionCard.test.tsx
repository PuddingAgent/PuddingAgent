import { act, render, screen } from '@testing-library/react';
import React from 'react';
import CompactionCard from './CompactionCard';
import type { CurrentRunActivity } from './processPreview';
const activity = (state: 'running' | 'completed' | 'failed' | 'unknown' | 'skipped' = 'running'): CurrentRunActivity => ({
  kind: 'system', variant: 'compaction', title: '上下文整理', status: 'running',
  compaction: { id: 'test', state, startedAt: Date.now() - 65_000, verifiedAt: Date.now() },
});
describe('compaction status strip', () => {
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
});
