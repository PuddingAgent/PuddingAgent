// ── P2#9 RecentlyDeniedPanel 组件测试 ─────────────────
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { RecentlyDeniedItem } from '../classifier/autoReviewClassifier';
import { toRecentlyDeniedItem } from '../classifier/autoReviewClassifier';
import RecentlyDeniedPanel from './RecentlyDeniedPanel';

const makeItem = (id: string, overrides: Partial<RecentlyDeniedItem> = {}): RecentlyDeniedItem =>
  toRecentlyDeniedItem({
    id,
    toolName: 'shell',
    description: 'rm -rf /tmp/build',
    riskLevel: 'high',
    rule: 'destructive',
    blockedAt: Date.now() - 5_000,
    ...overrides,
  });

describe('RecentlyDeniedPanel', () => {
  it('renders empty state when no items', () => {
    render(<RecentlyDeniedPanel items={[]} />);
    expect(screen.getByTestId('recently-denied-panel')).toBeTruthy();
    expect(screen.getByTestId('recently-denied-empty')).toBeTruthy();
  });

  it('renders items with tool name, rule label and source', () => {
    render(
      <RecentlyDeniedPanel
        items={[makeItem('a'), makeItem('b', { source: 'user_deny' })]}
      />,
    );
    const items = screen.getAllByTestId('recently-denied-item');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('shell');
    expect(items[0].textContent).toContain('破坏性操作');
    expect(items[0].textContent).toContain('自动拦截');
    expect(items[1].textContent).toContain('手动拒绝');
  });

  it('calls onRetry with the item', () => {
    const onRetry = jest.fn();
    render(
      <RecentlyDeniedPanel
        items={[makeItem('a')]}
        onRetry={onRetry}
      />,
    );
    fireEvent.click(screen.getByTestId('recently-denied-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
    expect(onRetry.mock.calls[0][0].id).toBe('a');
  });

  it('calls onRemove with the item id', () => {
    const onRemove = jest.fn();
    render(
      <RecentlyDeniedPanel
        items={[makeItem('a')]}
        onRemove={onRemove}
      />,
    );
    fireEvent.click(screen.getByTestId('recently-denied-remove'));
    expect(onRemove).toHaveBeenCalledWith('a');
  });

  it('calls onClear when clear button clicked', () => {
    const onClear = jest.fn();
    render(
      <RecentlyDeniedPanel
        items={[makeItem('a')]}
        onClear={onClear}
      />,
    );
    fireEvent.click(screen.getByTestId('recently-denied-clear'));
    expect(onClear).toHaveBeenCalledTimes(1);
  });

  it('hides clear button when list empty', () => {
    render(<RecentlyDeniedPanel items={[]} onClear={jest.fn()} />);
    expect(screen.queryByTestId('recently-denied-clear')).toBeNull();
  });
});
