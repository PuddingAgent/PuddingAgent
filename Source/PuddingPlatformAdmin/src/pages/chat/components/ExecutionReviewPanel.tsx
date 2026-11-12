import React from 'react';
import type {
  BudgetAmount,
  ExecutionReviewPanelProps,
  ExecutionReviewView,
} from './ExecutionReviewPanel.types';
import styles from './ExecutionReviewPanel.module.css';

const RUN_STATE_TEXT: Record<ExecutionReviewView['runState'], string> = {
  running: '执行中',
  'waiting-review': '等待验收',
  'queued-next-turn': '已排队下一轮',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
  unknown: '状态未知',
};

const REVIEW_STATE_TEXT: Record<ExecutionReviewView['reviewState'], string> = {
  'not-required': '无需验收',
  pending: '验收待处理',
  running: '验收进行中',
  satisfied: '验收满足',
  'needs-work': '验收未满足',
  'needs-user': '等待用户决定',
  unknown: '验收结果未知',
  stale: '验收结论已过期',
};

const OUTCOME_TEXT: Record<'not-supported' | 'unknown', string> = {
  'not-supported': '未满足',
  unknown: '未知',
};

const MAX_VISIBLE_UNMET_CRITERIA = 5;

/** 已知金额显示 `<amount> <unit>`；未知一律显示“未知”，绝不退化成 0/0.00/100%。 */
const formatBudgetAmount = (amount: BudgetAmount): string =>
  amount.known && amount.amount !== null
    ? `${amount.amount} ${amount.unit}`
    : '未知';

/** 上限缺失（null）表示无上限，不推导成 0。 */
const formatBudgetLimits = (
  maxOutputTokens: number | null,
  maxCost: number | null,
): string =>
  `输出 Token ${maxOutputTokens ?? '无上限'}，费用 ${maxCost ?? '无上限'}`;

const describeCancelState = (cancel: ExecutionReviewView['cancel']): string => {
  if (!cancel.requested) return '未请求取消';
  return cancel.draining
    ? `取消请求已受理，仍有未决调用（${cancel.pendingCalls}）`
    : '取消请求已受理';
};

const describeFreshness = (
  freshness: ExecutionReviewView['freshness'],
): string => {
  const parts: string[] = [];
  if (freshness.stale) {
    parts.push(`数据可能已过期（截至 ${freshness.dataAsOf}）`);
  }
  if (freshness.gapDetected) {
    parts.push('检测到事件缺口，已请求快照刷新');
  }
  return parts.join('；');
};

/**
 * 纯展示执行评审面板：只渲染后端 projection 的展示映射 `ExecutionReviewView`。
 * 不 fetch/SSE、不读 storage、不注册路由、不轮询、不改 stores/hooks；
 * 点击证据只回调冻结的 `EvidenceRef` 引用，不拼 URL、不推导控制结论。
 */
export const ExecutionReviewPanel: React.FC<ExecutionReviewPanelProps> = ({
  view,
  onOpenEvidence,
}) => {
  const runStateText = RUN_STATE_TEXT[view.runState];
  const reviewStateText = REVIEW_STATE_TEXT[view.reviewState];
  const visibleCriteria = view.unmetCriteria.slice(
    0,
    MAX_VISIBLE_UNMET_CRITERIA,
  );
  const hiddenCriteriaCount = view.unmetCriteria.length - visibleCriteria.length;
  const showFreshness = view.freshness.stale || view.freshness.gapDetected;

  return (
    <section
      className={styles.panel}
      role="region"
      aria-label="执行评审"
      data-testid="execution-review-panel"
    >
      <header className={styles.header}>
        <span
          className={styles.runState}
          data-testid="execution-run-state"
          aria-label={runStateText}
        >
          {runStateText}
        </span>
        <span
          className={styles.reviewState}
          data-testid="execution-review-state"
          aria-label={reviewStateText}
        >
          {reviewStateText}
        </span>
      </header>

      {view.runState === 'waiting-review' && view.reviewState === 'pending' ? (
        <p className={styles.hint} data-testid="review-hint">
          本轮已结束，等待验收
        </p>
      ) : null}

      {view.stopReason ? (
        <p
          className={styles.stopReason}
          data-testid="stop-reason"
          title={view.stopReason.reasonCode}
        >
          停止原因：{view.stopReason.summary}
        </p>
      ) : null}

      {view.continueReason ? (
        <p className={styles.continueReason} data-testid="continue-reason">
          续跑原因：{view.continueReason.summary}
        </p>
      ) : null}

      {view.modelSuggestion ? (
        <p className={styles.modelSuggestion} data-testid="model-suggestion">
          模型建议：{view.modelSuggestion.finding}（证据级别{' '}
          {view.modelSuggestion.evidenceLevel}）
        </p>
      ) : null}

      {view.failedChecks.length > 0 ? (
        <ul className={styles.failedChecks}>
          {view.failedChecks.map((check) => (
            <li
              key={check.checkId}
              className={styles.failedCheck}
              data-testid={`failed-check-${check.checkId}`}
              title={check.summary}
            >
              检查失败：{check.checkId}（退出码 {check.exitCode}）
            </li>
          ))}
        </ul>
      ) : null}

      {visibleCriteria.length > 0 ? (
        <div className={styles.unmetCriteria} data-testid="unmet-criteria">
          <div className={styles.sectionTitle}>未满足条件</div>
          <ul className={styles.unmetList}>
            {visibleCriteria.map((criterion) => (
              <li
                key={criterion.criterionId}
                className={styles.unmetItem}
                data-testid={`unmet-criterion-${criterion.criterionId}`}
              >
                <span className={styles.criterionText}>{criterion.text}</span>
                <span className={styles.outcome} data-outcome={criterion.outcome}>
                  {OUTCOME_TEXT[criterion.outcome]}
                </span>
                {criterion.evidenceRefs.length > 0 ? (
                  <span className={styles.evidenceRefs}>
                    {criterion.evidenceRefs.map((evidence) => (
                      <button
                        key={evidence.evidenceId}
                        type="button"
                        className={styles.evidenceButton}
                        data-testid={`open-evidence-${evidence.evidenceId}`}
                        aria-label={`查看证据 ${evidence.evidenceId}`}
                        onClick={() => onOpenEvidence(evidence)}
                      >
                        {evidence.label}
                      </button>
                    ))}
                  </span>
                ) : (
                  <span className={styles.noEvidence}>无证据引用</span>
                )}
              </li>
            ))}
          </ul>
          {hiddenCriteriaCount > 0 ? (
            <p className={styles.overflow} data-testid="unmet-overflow">
              还有 {hiddenCriteriaCount} 项未显示
            </p>
          ) : null}
        </div>
      ) : null}

      <dl className={styles.budget}>
        <div className={styles.budgetRow}>
          <dt>实际</dt>
          <dd data-testid="budget-actual">
            {formatBudgetAmount(view.budget.actual)}
          </dd>
        </div>
        <div className={styles.budgetRow}>
          <dt>预留</dt>
          <dd data-testid="budget-reserved">
            {formatBudgetAmount(view.budget.reserved)}
          </dd>
        </div>
        <div className={styles.budgetRow}>
          <dt>未知</dt>
          <dd data-testid="budget-unknown">
            {formatBudgetAmount(view.budget.unknown)}
          </dd>
        </div>
        <div className={styles.budgetRow}>
          <dt>上限</dt>
          <dd data-testid="budget-limits">
            {formatBudgetLimits(
              view.budget.maxOutputTokens,
              view.budget.maxCost,
            )}
          </dd>
        </div>
      </dl>

      <p className={styles.cancelState} data-testid="cancel-state">
        {describeCancelState(view.cancel)}
      </p>

      {showFreshness ? (
        <p className={styles.freshness} data-testid="freshness-warning">
          {describeFreshness(view.freshness)}
        </p>
      ) : null}
    </section>
  );
};

export default ExecutionReviewPanel;
