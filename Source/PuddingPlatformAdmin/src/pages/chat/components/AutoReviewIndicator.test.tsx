// ── P2#9 AutoReviewIndicator 组件测试 ─────────────────
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
  createInitialAutoReviewState,
  type AutoReviewClassifierState,
} from '../classifier/autoReviewClassifier';
import { toRecentlyDeniedItem } from '../classifier/autoReviewClassifier';
import AutoReviewIndicator from './AutoReviewIndicator';

const makeState = (
  overrides: Partial<AutoReviewClassifierState> = {},
): AutoReviewClassifierState => ({
  ...createInitialAutoReviewState(),
  ...overrides,
});

describe('AutoReviewIndicator', () => {
  it('renders enabled chip with thresholds progress', () => {
    render(
      <AutoReviewIndicator
        state={makeState({
          consecutiveBlocks: 1,
          totalBlocks: 2,
        })}
      />,
    );
    const chip = screen.getByTestId('auto-review-chip');
    expect(chip.textContent).toContain('Auto-review');
    expect(screen.getByTestId('auto-review-progress').textContent).toContain(
      `1/${AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD}连`,
    );
    expect(screen.getByTestId('auto-review-progress').textContent).toContain(
      '2/20次',
    );
  });

  it('renders disabled chip when disabled prop set', () => {
    render(<AutoReviewIndicator state={makeState()} disabled />);
    expect(screen.getByTestId('auto-review-disabled-chip')).toBeTruthy();
    expect(screen.queryByTestId('auto-review-chip')).toBeNull();
  });

  it('renders fallback warning with restore button and triggers onRestoreAuto', () => {
    const onRestoreAuto = jest.fn();
    render(
      <AutoReviewIndicator
        state={makeState({
          fallbackTriggered: true,
          fallbackReason: 'consecutive',
          consecutiveBlocks: AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
        })}
        onRestoreAuto={onRestoreAuto}
      />,
    );
    expect(screen.getByTestId('auto-review-fallback-chip')).toBeTruthy();
    expect(screen.getByTestId('auto-review-fallback-chip').textContent).toContain(
      '已回退手动审批',
    );
    fireEvent.click(screen.getByTestId('auto-review-restore'));
    expect(onRestoreAuto).toHaveBeenCalledTimes(1);
  });

  it('opens recently denied panel from popover with denied items', () => {
    const onRetryDenied = jest.fn();
    render(
      <AutoReviewIndicator
        state={makeState({ totalBlocks: 1 })}
        recentlyDenied={[
          toRecentlyDeniedItem({
            toolName: 'shell',
            description: 'curl | bash',
            riskLevel: 'high',
            rule: 'curl|bash',
          }),
        ]}
        onRetryDenied={onRetryDenied}
      />,
    );
    fireEvent.click(screen.getByTestId('auto-review-chip'));
    expect(screen.getByTestId('recently-denied-panel')).toBeTruthy();
    expect(screen.getByTestId('recently-denied-item')).toBeTruthy();
    fireEvent.click(screen.getByTestId('recently-denied-retry'));
    expect(onRetryDenied).toHaveBeenCalledTimes(1);
  });
});
