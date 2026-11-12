import type { ExecutionReviewSnapshotDto } from '@/services/platform/api';
import { toExecutionReviewView } from './executionReviewView';

const snapshot = (overrides: Partial<ExecutionReviewSnapshotDto> = {}): ExecutionReviewSnapshotDto => ({
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
  ...overrides,
});

describe('toExecutionReviewView', () => {
  it('maps a verified completed review to the panel view', () => {
    const view = toExecutionReviewView(snapshot());
    expect(view.reviewState).toBe('satisfied');
    expect(view.runState).toBe('completed');
    expect(view.budget.actual).toEqual({ amount: 0.25, unit: 'usd', known: true });
    expect(view.modelSuggestion).toEqual({ finding: 'Satisfied', evidenceLevel: 'CheckVerified' });
  });

  it('keeps an unknown cost bucket unknown instead of zero', () => {
    const view = toExecutionReviewView(
      snapshot({
        budget: {
          ...snapshot().budget,
          unknown: { amount: 1.5, unit: 'usd', known: false },
        },
      }),
    );
    expect(view.budget.unknown.known).toBe(false);
    expect(view.budget.unknown.amount).toBeNull();
  });

  it('marks stale data and never presents it as live success', () => {
    const view = toExecutionReviewView(
      snapshot({
        reviewState: 'stale',
        freshness: { ...snapshot().freshness, stale: true, gapDetected: true },
      }),
    );
    expect(view.reviewState).toBe('stale');
    expect(view.freshness.stale).toBe(true);
    expect(view.freshness.gapDetected).toBe(true);
  });

  it('falls back to unknown for unrecognised states', () => {
    const view = toExecutionReviewView(snapshot({ runState: 'something-new', reviewState: 'mystery' }));
    expect(view.runState).toBe('unknown');
    expect(view.reviewState).toBe('unknown');
  });

  it('maps cancellation and reason fields', () => {
    const view = toExecutionReviewView(
      snapshot({
        runState: 'cancelled',
        reviewState: 'needs-user',
        cancel: {
          requested: true,
          requestedAt: '2026-11-05T12:01:00+00:00',
          draining: true,
          pendingCalls: 2,
          reasonCode: 'user.cancelled',
        },
      }),
    );
    expect(view.runState).toBe('cancelled');
    expect(view.cancel.requested).toBe(true);
    expect(view.cancel.draining).toBe(true);
    expect(view.cancel.pendingCalls).toBe(2);
  });
});
