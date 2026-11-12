/**
 * PA-08A 冻结的只读展示类型（主代理维护，DS-PA08-A 不得修改本文件）。
 *
 * 约束：
 * - `ExecutionReviewView` 是后端 projection 的展示映射；组件只渲染，不推导控制结论、不发起请求。
 * - 未知金额必须 `known === false && amount === null`，不得用 0 代替未知。
 * - 缺权限/缺失数据以 availability/missingReason 表达，不能用空列表冒充"不存在"。
 * - `onOpenEvidence` 只传递冻结的 EvidenceRef；组件不拼 URL、不 fetch、不写 storage。
 */
export type ExecutionRunState =
  | 'running'
  | 'waiting-review'
  | 'queued-next-turn'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'unknown';

export type ExecutionReviewState =
  | 'not-required'
  | 'pending'
  | 'running'
  | 'satisfied'
  | 'needs-work'
  | 'needs-user'
  | 'unknown'
  | 'stale';

export type EvidenceKind =
  | 'canonical-output'
  | 'check-report'
  | 'registered-artifact'
  | 'caller-supplied';

export interface EvidenceRef {
  evidenceId: string;
  kind: EvidenceKind;
  label: string;
  reference: string;
  contentHash: string;
  availability: 'available' | 'missing' | 'truncated';
}

export interface UnmetCriterion {
  criterionId: string;
  text: string;
  outcome: 'not-supported' | 'unknown';
  evidenceRefs: EvidenceRef[];
}

/** 金额未知时 amount=null 且 known=false；组件必须渲染"未知"，禁止渲染 0 或 100%。 */
export interface BudgetAmount {
  amount: number | null;
  unit: string;
  known: boolean;
}

export interface ExecutionBudgetView {
  maxOutputTokens: number | null;
  maxCost: number | null;
  actual: BudgetAmount;
  reserved: BudgetAmount;
  unknown: BudgetAmount;
}

export interface ModelPolicyView {
  modelId: string;
  modelRevision: string;
  policyVersion: string;
  promptVersion: string;
  inheritedFromPrimary: boolean;
}

export interface CancelStateView {
  requested: boolean;
  requestedAt: string | null;
  draining: boolean;
  pendingCalls: number;
  reasonCode: string | null;
}

export interface DataFreshnessView {
  stale: boolean;
  dataAsOf: string;
  lastSequence: number;
  snapshotRevision: number;
  gapDetected: boolean;
}

export interface ExecutionReviewView {
  workId: string;
  goalId: string;
  turnId: string;
  reviewId: string | null;
  runState: ExecutionRunState;
  reviewState: ExecutionReviewState;
  stopReason: { reasonCode: string; summary: string } | null;
  continueReason: { reasonCode: string; summary: string } | null;
  unmetCriteria: UnmetCriterion[];
  budget: ExecutionBudgetView;
  modelPolicy: ModelPolicyView;
  cancel: CancelStateView;
  freshness: DataFreshnessView;
  /** 模型建议与受控检查结论分开展示；模型建议不得被表述为"已通过全部测试"。 */
  modelSuggestion: { finding: string; evidenceLevel: string } | null;
  failedChecks: { checkId: string; exitCode: number; summary: string }[];
}

export interface ExecutionReviewPanelProps {
  view: ExecutionReviewView;
  onOpenEvidence: (evidence: EvidenceRef) => void;
}
