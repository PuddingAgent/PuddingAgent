import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import ExecutionReviewPanel from './ExecutionReviewPanel';
import { executionReviewFixtures } from './ExecutionReviewPanel.fixtures';
import type {
  EvidenceRef,
  ExecutionReviewState,
  ExecutionReviewView,
  ExecutionRunState,
} from './ExecutionReviewPanel.types';

const noop = (_evidence: EvidenceRef) => {};

const renderPanel = (
  view: ExecutionReviewView,
  onOpenEvidence: (evidence: EvidenceRef) => void = noop,
) => render(<ExecutionReviewPanel view={view} onOpenEvidence={onOpenEvidence} />);

const textOf = (testId: string) =>
  screen.getByTestId(testId).textContent ?? '';

describe('ExecutionReviewPanel', () => {
  it('renders pendingReview as waiting for review without claiming completion', () => {
    const { container } = renderPanel(executionReviewFixtures.pendingReview);

    expect(textOf('execution-run-state')).toBe('等待验收');
    expect(
      screen.getByTestId('execution-run-state').getAttribute('aria-label'),
    ).toBe('等待验收');
    expect(textOf('execution-review-state')).toBe('验收待处理');
    expect(
      screen.getByTestId('execution-review-state').getAttribute('aria-label'),
    ).toBe('验收待处理');
    expect(textOf('review-hint')).toBe('本轮已结束，等待验收');
    expect(container.textContent).not.toContain('已完成');
  });

  it('shows the model suggestion and the failed controlled check separately', () => {
    const { container } = renderPanel(
      executionReviewFixtures.modelSatisfiedButCheckFailed,
    );

    expect(textOf('model-suggestion')).toBe(
      '模型建议：满足（证据级别 ModelAssessed）',
    );
    const failedCheck = screen.getByTestId('failed-check-check-b');
    expect(failedCheck.textContent).toBe('检查失败：check-b（退出码 1）');
    expect(failedCheck.getAttribute('title')).toBe('乙文件缺失');
    expect(textOf('unmet-criterion-criterion-2')).toContain(
      '必须包含乙文件并列出测试结果。',
    );
    expect(textOf('unmet-criterion-criterion-2')).toContain('未满足');
    expect(container.textContent).not.toContain('模型已验证所有测试');
  });

  it('renders queuedNextTurn with the continue reason and no running claim', () => {
    const { container } = renderPanel(executionReviewFixtures.queuedNextTurn);

    expect(textOf('execution-run-state')).toBe('已排队下一轮');
    expect(textOf('continue-reason')).toBe(
      '续跑原因：缺少乙文件，最多自动补做一次',
    );
    expect(textOf('execution-review-state')).toBe('验收未满足');
    expect(container.textContent).not.toContain('执行中');
  });

  it('renders cancel draining with pending calls and keeps budget occupancy', () => {
    renderPanel(executionReviewFixtures.cancelDraining);

    expect(textOf('cancel-state')).toBe(
      '取消请求已受理，仍有未决调用（2）',
    );
    expect(textOf('budget-actual')).toBe('0.25 usd');
    expect(textOf('budget-reserved')).toBe('0.05 usd');
  });

  it('renders unknown amounts as 未知 and missing evidence without buttons', () => {
    const { container } = renderPanel(
      executionReviewFixtures.unknownAndMissingEvidence,
    );

    expect(textOf('budget-actual')).toBe('未知');
    expect(textOf('budget-reserved')).toBe('未知');
    expect(textOf('budget-unknown')).toBe('未知');
    expect(textOf('budget-limits')).toContain('无上限');
    expect(container.textContent).not.toContain('0 usd');
    expect(container.textContent).not.toContain('0.00');
    expect(container.textContent).not.toContain('100%');
    const criterion = textOf('unmet-criterion-criterion-1');
    expect(criterion).toContain('容器网络抖动导致上传中断');
    expect(criterion).toContain('未知');
    expect(screen.queryByTestId('open-evidence-evidence-build')).toBeNull();
    expect(container.textContent).toContain('无证据引用');
  });

  it('renders the staleGap freshness warning instead of a real-time success claim', () => {
    const { container } = renderPanel(executionReviewFixtures.staleGap);

    expect(textOf('freshness-warning')).toBe(
      '数据可能已过期（截至 2026-11-05T11:55:00+00:00）；检测到事件缺口，已请求快照刷新',
    );
    expect(textOf('execution-review-state')).toBe('验收结论已过期');
    expect(container.textContent).toContain('数据可能已过期');
  });

  it('opens evidence exactly once with the same EvidenceRef reference', () => {
    const view = executionReviewFixtures.queuedNextTurn;
    const onOpenEvidence = jest.fn();
    renderPanel(view, onOpenEvidence);

    const button = screen.getByTestId('open-evidence-evidence-build');
    expect(button.getAttribute('type')).toBe('button');
    expect(button.getAttribute('aria-label')).toBe(
      '查看证据 evidence-build',
    );
    expect(onOpenEvidence).not.toHaveBeenCalled();

    fireEvent.click(button);

    expect(onOpenEvidence).toHaveBeenCalledTimes(1);
    expect(onOpenEvidence.mock.calls[0][0]).toBe(
      view.unmetCriteria[0].evidenceRefs[0],
    );
  });

  it('keeps run/review aria-labels equal to their visible text for every fixture', () => {
    for (const view of Object.values(executionReviewFixtures)) {
      const { unmount } = renderPanel(view);
      const run = screen.getByTestId('execution-run-state');
      const review = screen.getByTestId('execution-review-state');
      expect(run.getAttribute('aria-label')).toBe(run.textContent);
      expect(review.getAttribute('aria-label')).toBe(review.textContent);
      unmount();
    }
  });

  it('truncates more than five unmet criteria and reports the hidden count', () => {
    const base = executionReviewFixtures.pendingReview;
    const unmetCriteria = Array.from({ length: 7 }, (_, index) => ({
      criterionId: `criterion-extra-${index + 1}`,
      text: `补充条件 ${index + 1}`,
      outcome: 'not-supported' as const,
      evidenceRefs: [],
    }));
    const view: ExecutionReviewView = { ...base, unmetCriteria };
    renderPanel(view);

    expect(screen.getAllByTestId(/^unmet-criterion-/)).toHaveLength(5);
    expect(
      screen.queryByTestId('unmet-criterion-criterion-extra-6'),
    ).toBeNull();
    expect(textOf('unmet-overflow')).toBe('还有 2 项未显示');
  });

  it('does not render the unmet-criteria container when there are no unmet criteria', () => {
    renderPanel(executionReviewFixtures.pendingReview);

    expect(screen.queryByTestId('unmet-criteria')).toBeNull();
    expect(screen.queryByTestId('unmet-overflow')).toBeNull();
  });

  it('renders known budget amounts with their unit and known limits', () => {
    renderPanel(executionReviewFixtures.pendingReview);

    expect(textOf('budget-actual')).toBe('0.25 usd');
    expect(textOf('budget-reserved')).toBe('0.05 usd');
    expect(textOf('budget-unknown')).toBe('未知');
    expect(textOf('budget-limits')).toContain('20000');
    expect(textOf('budget-limits')).toContain('费用 2');
  });

  it('renders the stop reason with its reasonCode only in the title attribute', () => {
    renderPanel(executionReviewFixtures.cancelDraining);

    const stopReason = screen.getByTestId('stop-reason');
    expect(stopReason.textContent).toBe('停止原因：用户取消');
    expect(stopReason.getAttribute('title')).toBe('goal.cancelled');
    expect(stopReason.textContent).not.toContain('goal.cancelled');
  });

  it('exposes the panel region and its accessible label', () => {
    renderPanel(executionReviewFixtures.pendingReview);

    const root = screen.getByTestId('execution-review-panel');
    expect(root.getAttribute('role')).toBe('region');
    expect(root.getAttribute('aria-label')).toBe('执行评审');
  });

  it('renders every run state with matching text and aria-label', () => {
    const labels: Record<ExecutionRunState, string> = {
      running: '执行中',
      'waiting-review': '等待验收',
      'queued-next-turn': '已排队下一轮',
      completed: '已完成',
      failed: '失败',
      cancelled: '已取消',
      unknown: '状态未知',
    };
    for (const [runState, label] of Object.entries(labels) as [
      ExecutionRunState,
      string,
    ][]) {
      const { unmount } = renderPanel({
        ...executionReviewFixtures.pendingReview,
        runState,
      });
      expect(textOf('execution-run-state')).toBe(label);
      expect(
        screen.getByTestId('execution-run-state').getAttribute('aria-label'),
      ).toBe(label);
      unmount();
    }
  });

  it('renders every review state with matching text and aria-label', () => {
    const labels: Record<ExecutionReviewState, string> = {
      'not-required': '无需验收',
      pending: '验收待处理',
      running: '验收进行中',
      satisfied: '验收满足',
      'needs-work': '验收未满足',
      'needs-user': '等待用户决定',
      unknown: '验收结果未知',
      stale: '验收结论已过期',
    };
    for (const [reviewState, label] of Object.entries(labels) as [
      ExecutionReviewState,
      string,
    ][]) {
      const { unmount } = renderPanel({
        ...executionReviewFixtures.pendingReview,
        reviewState,
      });
      expect(textOf('execution-review-state')).toBe(label);
      expect(
        screen.getByTestId('execution-review-state').getAttribute('aria-label'),
      ).toBe(label);
      unmount();
    }
  });

  it('renders the not-requested and requested-without-draining cancel states', () => {
    const { unmount } = renderPanel(executionReviewFixtures.pendingReview);
    expect(textOf('cancel-state')).toBe('未请求取消');
    unmount();

    renderPanel({
      ...executionReviewFixtures.cancelDraining,
      cancel: {
        ...executionReviewFixtures.cancelDraining.cancel,
        draining: false,
        pendingCalls: 0,
      },
    });
    expect(textOf('cancel-state')).toBe('取消请求已受理');
    expect(textOf('cancel-state')).not.toContain('仍有未决调用');
  });

  it('omits the review hint and freshness warning when they do not apply', () => {
    renderPanel(executionReviewFixtures.cancelDraining);
    expect(screen.queryByTestId('review-hint')).toBeNull();
    expect(screen.queryByTestId('freshness-warning')).toBeNull();

    renderPanel(executionReviewFixtures.staleGap);
    expect(textOf('freshness-warning')).toContain('数据可能已过期');
    expect(textOf('freshness-warning')).toContain('检测到事件缺口');
  });

  it('renders multiple failed checks and multiple evidence refs with distinct buttons', () => {
    const evidenceRefs: EvidenceRef[] = [
      {
        evidenceId: 'ev-a',
        kind: 'check-report',
        label: '检查 A',
        reference: 'check://a',
        contentHash: 'sha-a',
        availability: 'available',
      },
      {
        evidenceId: 'ev-b',
        kind: 'canonical-output',
        label: '输出 B',
        reference: 'turn://b',
        contentHash: 'sha-b',
        availability: 'available',
      },
    ];
    const onOpenEvidence = jest.fn();
    const view: ExecutionReviewView = {
      ...executionReviewFixtures.modelSatisfiedButCheckFailed,
      unmetCriteria: [
        {
          criterionId: 'criterion-9',
          text: '多条证据条件',
          outcome: 'not-supported',
          evidenceRefs,
        },
      ],
      failedChecks: [
        { checkId: 'check-a', exitCode: 1, summary: 'A 失败' },
        { checkId: 'check-b', exitCode: 2, summary: 'B 失败' },
      ],
    };
    renderPanel(view, onOpenEvidence);

    expect(textOf('failed-check-check-a')).toBe('检查失败：check-a（退出码 1）');
    expect(textOf('failed-check-check-b')).toBe('检查失败：check-b（退出码 2）');
    expect(
      screen.getAllByTestId(/^open-evidence-/).map((el) => el.textContent),
    ).toHaveLength(2);
    expect(
      screen.getByTestId('open-evidence-ev-a').getAttribute('aria-label'),
    ).toBe('查看证据 ev-a');

    fireEvent.click(screen.getByTestId('open-evidence-ev-a'));
    expect(onOpenEvidence).toHaveBeenCalledTimes(1);
    expect(onOpenEvidence.mock.calls[0][0]).toBe(evidenceRefs[0]);
  });
});
