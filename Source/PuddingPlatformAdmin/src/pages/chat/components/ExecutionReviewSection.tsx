// ── ExecutionReviewSection：PA-08 A 只读执行评审小节（Goal 详情内）────
// 数据来自受权只读端点；无评审/错误时不渲染伪造视图。证据点击只展示已拿到的引用，
// 不在此处 fetch/执行工具。
import * as React from 'react';
import { Alert } from 'antd';
import ExecutionReviewPanel from './ExecutionReviewPanel';
import type { EvidenceRef } from './ExecutionReviewPanel.types';
import { useGoalExecutionReview } from '../hooks/useGoalExecutionReview';
import { toExecutionReviewView } from '../utils/executionReviewView';

export default function ExecutionReviewSection({ goalId }: { goalId?: string }) {
  const { snapshot, loading, error } = useGoalExecutionReview(goalId);
  const [evidence, setEvidence] = React.useState<EvidenceRef | null>(null);
  if (!goalId) return null;
  if (loading) {
    return (
      <div data-testid="execution-review-loading" style={{ marginTop: 10, fontSize: 12 }}>
        正在加载执行评审…
      </div>
    );
  }
  if (error || !snapshot) return null;
  const view = toExecutionReviewView(snapshot);
  return (
    <section
      data-testid="execution-review-section"
      aria-label="执行评审"
      style={{ marginTop: 12, paddingTop: 10, borderTop: '1px solid var(--pudding-chat-border, #eee)' }}
    >
      <ExecutionReviewPanel view={view} onOpenEvidence={setEvidence} />
      {evidence && (
        <Alert
          data-testid="execution-review-evidence"
          style={{ marginTop: 8 }}
          type="info"
          showIcon
          message={`证据：${evidence.label}`}
          description={`${evidence.reference} · ${evidence.contentHash} · ${evidence.availability}`}
          onClose={() => setEvidence(null)}
          closable
        />
      )}
    </section>
  );
}
