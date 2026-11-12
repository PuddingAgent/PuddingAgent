// PA-08 A：后端 ExecutionReviewSnapshot → 面板 ExecutionReviewView 的纯映射。
// 只做字段映射与受控枚举收敛；不推导控制结论、不把未知变成 0、不把 stale 变成实时成功。
import type { ExecutionReviewSnapshotDto } from '@/services/platform/api';
import type {
  EvidenceKind,
  ExecutionReviewState,
  ExecutionReviewView,
  ExecutionRunState,
} from '@/pages/chat/components/ExecutionReviewPanel.types';

const RUN_STATES: ExecutionRunState[] = [
  'running',
  'waiting-review',
  'queued-next-turn',
  'completed',
  'failed',
  'cancelled',
  'unknown',
];
const REVIEW_STATES: ExecutionReviewState[] = [
  'not-required',
  'pending',
  'running',
  'satisfied',
  'needs-work',
  'needs-user',
  'unknown',
  'stale',
];

const runStateOf = (value: string): ExecutionRunState =>
  (RUN_STATES as string[]).includes(value) ? (value as ExecutionRunState) : 'unknown';

const reviewStateOf = (value: string): ExecutionReviewState =>
  (REVIEW_STATES as string[]).includes(value) ? (value as ExecutionReviewState) : 'unknown';

export function toExecutionReviewView(snapshot: ExecutionReviewSnapshotDto): ExecutionReviewView {
  return {
    workId: snapshot.workId,
    goalId: snapshot.goalId,
    turnId: snapshot.turnId,
    reviewId: snapshot.reviewId,
    runState: runStateOf(snapshot.runState),
    reviewState: reviewStateOf(snapshot.reviewState),
    stopReason: snapshot.stopReasonCode
      ? { reasonCode: snapshot.stopReasonCode, summary: snapshot.stopReasonSummary ?? '' }
      : null,
    continueReason: snapshot.continueReasonCode
      ? { reasonCode: snapshot.continueReasonCode, summary: snapshot.continueReasonSummary ?? '' }
      : null,
    unmetCriteria: (snapshot.unmetCriteria ?? []).map((criterion) => ({
      criterionId: criterion.criterionId,
      text: criterion.text,
      outcome: criterion.outcome === 'not-supported' ? 'not-supported' : 'unknown',
      evidenceRefs: (criterion.evidenceRefs ?? []).map((ref) => ({
        evidenceId: ref.evidenceId,
        kind: ref.kind as EvidenceKind,
        label: ref.label,
        reference: ref.reference,
        contentHash: ref.contentHash,
        availability: ref.availability as 'available' | 'missing' | 'truncated',
      })),
    })),
    budget: {
      maxOutputTokens: snapshot.budget.maxOutputTokens,
      maxCost: snapshot.budget.maxCost,
      actual: {
        amount: snapshot.budget.actual.amount,
        unit: snapshot.budget.actual.unit,
        known: snapshot.budget.actual.known,
      },
      reserved: {
        amount: snapshot.budget.reserved.amount,
        unit: snapshot.budget.reserved.unit,
        known: snapshot.budget.reserved.known,
      },
      unknown: {
        amount: snapshot.budget.unknown.known === true ? snapshot.budget.unknown.amount : null,
        unit: snapshot.budget.unknown.unit,
        known: snapshot.budget.unknown.known === true,
      },
    },
    modelPolicy: {
      modelId: snapshot.modelPolicy.modelId,
      modelRevision: snapshot.modelPolicy.modelRevision,
      policyVersion: snapshot.modelPolicy.policyVersion,
      promptVersion: snapshot.modelPolicy.promptVersion,
      inheritedFromPrimary: snapshot.modelPolicy.inheritedFromPrimary,
    },
    cancel: {
      requested: snapshot.cancel.requested,
      requestedAt: snapshot.cancel.requestedAt,
      draining: snapshot.cancel.draining,
      pendingCalls: snapshot.cancel.pendingCalls,
      reasonCode: snapshot.cancel.reasonCode,
    },
    freshness: {
      stale: snapshot.freshness.stale,
      dataAsOf: snapshot.freshness.dataAsOf,
      lastSequence: snapshot.freshness.lastSequence,
      snapshotRevision: snapshot.freshness.snapshotRevision,
      gapDetected: snapshot.freshness.gapDetected,
    },
    modelSuggestion: snapshot.modelSuggestionFinding
      ? { finding: snapshot.modelSuggestionFinding, evidenceLevel: snapshot.modelSuggestionLevel ?? 'unknown' }
      : null,
    failedChecks: (snapshot.failedChecks ?? []).map((check) => ({
      checkId: check.checkId,
      exitCode: check.exitCode,
      summary: check.summary,
    })),
  };
}
