import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { getExecutionReviewByGoal } from '@/services/platform/api';
import ExecutionReviewSection from './ExecutionReviewSection';

jest.mock('@/services/platform/api', () => ({
  getExecutionReviewByGoal: jest.fn(),
}));

const mockedGet = getExecutionReviewByGoal as unknown as jest.Mock;

const snapshot = (overrides: Record<string, unknown> = {}) => ({
  workId: 'work-1',
  goalId: 'goal-1',
  turnId: 'turn-1',
  reviewId: 'review-1',
  runState: 'completed',
  reviewState: 'needs-work',
  stopReasonCode: null,
  stopReasonSummary: null,
  continueReasonCode: null,
  continueReasonSummary: null,
  unmetCriteria: [
    {
      criterionId: 'criterion-2',
      text: '必须包含乙文件。',
      outcome: 'not-supported',
      evidenceRefs: [
        {
          evidenceId: 'evidence-check-b',
          kind: 'check-report',
          label: '乙文件检查',
          reference: 'check://files/b',
          contentHash: 'sha256-check-b',
          availability: 'missing',
        },
      ],
    },
  ],
  budget: {
    maxOutputTokens: 20000,
    maxCost: 2,
    actual: { amount: 0.25, unit: 'usd', known: true },
    reserved: { amount: 0.05, unit: 'usd', known: true },
    unknown: { amount: 0, unit: 'usd', known: true },
  },
  modelPolicy: {
    modelId: 'model-a',
    modelRevision: 'rev-1',
    policyVersion: 'policy-v1',
    promptVersion: 'prompt-v3',
    inheritedFromPrimary: false,
  },
  cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
  freshness: {
    stale: false,
    dataAsOf: '2026-11-05T12:00:00+00:00',
    lastSequence: 41,
    snapshotRevision: 9,
    gapDetected: false,
  },
  modelSuggestionFinding: 'Satisfied',
  modelSuggestionLevel: 'ModelAssessed',
  failedChecks: [{ checkId: 'check-b', exitCode: 1, summary: '乙文件缺失' }],
  snapshotRevision: 9,
  lastSequence: 41,
  ...overrides,
});

describe('ExecutionReviewSection', () => {
  beforeEach(() => mockedGet.mockReset());

  it('renders the panel from the authorized projection', async () => {
    mockedGet.mockResolvedValue({ review: snapshot() });
    render(<ExecutionReviewSection goalId="goal-1" />);
    await waitFor(() => expect(screen.getByTestId('execution-review-panel')).toBeTruthy());
    expect(screen.getByTestId('execution-review-state').textContent).toBe('验收未满足');
    expect(screen.getByText('检查失败：check-b（退出码 1）')).toBeTruthy();
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('shows the evidence reference only after the user opens it', async () => {
    mockedGet.mockResolvedValue({ review: snapshot() });
    render(<ExecutionReviewSection goalId="goal-1" />);
    const button = await screen.findByTestId('open-evidence-evidence-check-b');
    expect(screen.queryByTestId('execution-review-evidence')).toBeNull();
    fireEvent.click(button);
    expect(screen.getByTestId('execution-review-evidence').textContent).toContain('check://files/b');
  });

  it('renders nothing when the projection is missing', async () => {
    mockedGet.mockResolvedValue({ review: null });
    const { container } = render(<ExecutionReviewSection goalId="goal-1" />);
    await waitFor(() => expect(mockedGet).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(container.querySelector('[data-testid="execution-review-section"]')).toBeNull();
    expect(screen.queryByTestId('execution-review-panel')).toBeNull();
  });
});
