/**
 * PA-08A 主代理冻结的设计状态样本（DS-PA08-A 不得修改本文件）。
 * 每个样本覆盖任务书 08 §3 UI 表的一行；测试必须直接消费这些样本，不得改写期望。
 */
import type { ExecutionReviewView } from './ExecutionReviewPanel.types';

const baseBudget = {
  maxOutputTokens: 20_000,
  maxCost: 2,
  actual: { amount: 0.25, unit: 'usd', known: true },
  reserved: { amount: 0.05, unit: 'usd', known: true },
  unknown: { amount: null, unit: 'usd', known: false },
} as const;

const basePolicy = {
  modelId: 'deepseek-chat',
  modelRevision: 'rev-7',
  policyVersion: 'policy-v1',
  promptVersion: 'prompt-v3',
  inheritedFromPrimary: false,
} as const;

const availableEvidence = {
  evidenceId: 'evidence-build',
  kind: 'check-report',
  label: '受控构建检查',
  reference: 'check://build/exit-0',
  contentHash: 'sha256-abc',
  availability: 'available',
} as const;

const freshData = {
  stale: false,
  dataAsOf: '2026-11-05T12:00:00+00:00',
  lastSequence: 41,
  snapshotRevision: 9,
  gapDetected: false,
} as const;

export const executionReviewFixtures: Record<string, ExecutionReviewView> = {
  /** Turn 已完成，验收 Pending：保留工作状态，不显示整个 Goal 完成。 */
  pendingReview: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-1',
    reviewId: 'review-pending-1',
    runState: 'waiting-review',
    reviewState: 'pending',
    stopReason: null,
    continueReason: null,
    unmetCriteria: [],
    budget: baseBudget,
    modelPolicy: basePolicy,
    cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
    freshness: freshData,
    modelSuggestion: null,
    failedChecks: [],
  },

  /** 模型认为满足、受控检查失败：两种结论分开展示。 */
  modelSatisfiedButCheckFailed: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-2',
    reviewId: 'review-failed-1',
    runState: 'completed',
    reviewState: 'needs-work',
    stopReason: null,
    continueReason: null,
    unmetCriteria: [
      {
        criterionId: 'criterion-2',
        text: '必须包含乙文件并列出测试结果。',
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
    budget: baseBudget,
    modelPolicy: basePolicy,
    cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
    freshness: freshData,
    modelSuggestion: { finding: '满足', evidenceLevel: 'ModelAssessed' },
    failedChecks: [{ checkId: 'check-b', exitCode: 1, summary: '乙文件缺失' }],
  },

  /** 下一轮已排队但未开始：只显示排队状态，不宣称正在执行工具。 */
  queuedNextTurn: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-2',
    reviewId: 'review-queued-1',
    runState: 'queued-next-turn',
    reviewState: 'needs-work',
    stopReason: null,
    continueReason: { reasonCode: 'goal.continue_repair', summary: '缺少乙文件，最多自动补做一次' },
    unmetCriteria: [
      {
        criterionId: 'criterion-2',
        text: '必须包含乙文件。',
        outcome: 'not-supported',
        evidenceRefs: [availableEvidence],
      },
    ],
    budget: baseBudget,
    modelPolicy: basePolicy,
    cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
    freshness: freshData,
    modelSuggestion: { finding: '需要补充', evidenceLevel: 'Observed' },
    failedChecks: [],
  },

  /** 取消已请求、provider 仍在排空：保留未决调用与费用占位。 */
  cancelDraining: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-3',
    reviewId: 'review-cancel-1',
    runState: 'running',
    reviewState: 'needs-user',
    stopReason: { reasonCode: 'goal.cancelled', summary: '用户取消' },
    continueReason: null,
    unmetCriteria: [],
    budget: baseBudget,
    modelPolicy: basePolicy,
    cancel: {
      requested: true,
      requestedAt: '2026-11-05T12:01:00+00:00',
      draining: true,
      pendingCalls: 2,
      reasonCode: 'user.cancelled',
    },
    freshness: freshData,
    modelSuggestion: null,
    failedChecks: [],
  },

  /** Unknown 金额/未知验收/证据缺失：明确未知，禁止 0 元或成功替代。 */
  unknownAndMissingEvidence: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-4',
    reviewId: 'review-unknown-1',
    runState: 'unknown',
    reviewState: 'unknown',
    stopReason: { reasonCode: 'budget.usage_unknown', summary: '上游用量未知' },
    continueReason: null,
    unmetCriteria: [
      {
        criterionId: 'criterion-1',
        text: '必须给出可核对的构建结果（很长的中文原因：容器网络抖动导致上传中断，检查报告未生成）。',
        outcome: 'unknown',
        evidenceRefs: [],
      },
    ],
    budget: {
      maxOutputTokens: null,
      maxCost: null,
      actual: { amount: null, unit: 'usd', known: false },
      reserved: { amount: null, unit: 'usd', known: false },
      unknown: { amount: null, unit: 'usd', known: false },
    },
    modelPolicy: basePolicy,
    cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
    freshness: freshData,
    modelSuggestion: null,
    failedChecks: [],
  },

  /** SSE/stale/cursor 缺口：显示数据时间与刷新提示，不沿用旧数据宣称实时成功。 */
  staleGap: {
    workId: 'work-1',
    goalId: 'goal-1',
    turnId: 'turn-5',
    reviewId: 'review-stale-1',
    runState: 'completed',
    reviewState: 'stale',
    stopReason: null,
    continueReason: null,
    unmetCriteria: [],
    budget: baseBudget,
    modelPolicy: basePolicy,
    cancel: { requested: false, requestedAt: null, draining: false, pendingCalls: 0, reasonCode: null },
    freshness: {
      stale: true,
      dataAsOf: '2026-11-05T11:55:00+00:00',
      lastSequence: 40,
      snapshotRevision: 8,
      gapDetected: true,
    },
    modelSuggestion: null,
    failedChecks: [],
  },
};
