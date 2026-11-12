import { renderHook, waitFor } from '@testing-library/react';
import { getExecutionReview } from '@/services/platform/api';
import { useExecutionReview } from './useExecutionReview';

jest.mock('@/services/platform/api', () => ({
  getExecutionReview: jest.fn(),
}));

const mockedGet = getExecutionReview as unknown as jest.Mock;

const snapshot = () => ({
  workId: 'work-1',
  goalId: 'goal-1',
  turnId: 'turn-1',
  reviewId: 'review-1',
  runState: 'completed',
  reviewState: 'satisfied',
  stopReasonCode: null,
  stopReasonSummary: null,
  continueReasonCode: null,
  continueReasonSummary: null,
  unmetCriteria: [],
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
  modelSuggestionLevel: 'CheckVerified',
  failedChecks: [],
  snapshotRevision: 9,
  lastSequence: 41,
});

describe('useExecutionReview', () => {
  beforeEach(() => mockedGet.mockReset());

  it('loads the snapshot once and never polls', async () => {
    mockedGet.mockResolvedValue({ review: snapshot() });
    const { result } = renderHook(() => useExecutionReview('review-1'));
    await waitFor(() => expect(result.current.snapshot?.reviewId).toBe('review-1'));
    expect(result.current.error).toBeNull();
    expect(mockedGet).toHaveBeenCalledTimes(1);
    expect(mockedGet).toHaveBeenCalledWith('review-1', expect.objectContaining({ stale: false }));
  });

  it('reports a missing review instead of faking a snapshot', async () => {
    mockedGet.mockResolvedValue({ review: null });
    const { result } = renderHook(() => useExecutionReview('review-missing'));
    await waitFor(() => expect(result.current.error).toBe('review_not_found'));
    expect(result.current.snapshot).toBeNull();
  });

  it('does not fetch when disabled or without a review id', async () => {
    renderHook(() => useExecutionReview(undefined));
    renderHook(() => useExecutionReview('review-1', { enabled: false }));
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(mockedGet).not.toHaveBeenCalled();
  });
});
