// ── GoalStepsPanel：步骤渲染 / 未知状态降级 / 端点缺失兜底 ───────────
// 注意：本文件含 jest.mock 工厂；当前 umi jest 转换链不允许「jest.mock +
// 导入类型用于类型注解」组合，故这里使用本地结构类型而非 import type。
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import GoalStepsPanel from './GoalStepsPanel';

const mockRequest = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => mockRequest(...args),
}));

const makeGoal = () => ({
  goalRunId: 'goal-1',
  conversationId: 'conv-1',
  agentInstanceId: 'agent-1',
  objective: '完成 Goal 步骤面板',
  objectiveVersion: 1,
  phase: 'active' as const,
  blockedCode: null,
  statusReason: null,
  maxIterations: 32,
  iterationsStarted: 2,
  iterationsSettled: 1,
  activationEpoch: 1,
  aggregateVersion: 7,
  lastNextAction: null,
  createdAtUtc: '2026-09-16T00:00:00Z',
  updatedAtUtc: '2026-09-16T00:10:00Z',
  terminalAtUtc: null,
});

const makeSnapshot = () => ({
  goalRunId: 'goal-1',
  phase: 'active',
  planVersion: 2,
  hasPlan: true,
  progress: {
    stepsTotal: 3,
    stepsPassed: 1,
    stepsFailed: 0,
    stepsInProgress: 1,
    currentStepId: 'node-2',
  },
  steps: [
    {
      nodeId: 'node-2',
      sequenceNo: 2,
      kind: 'execute',
      title: '运行修复',
      status: 'in_progress',
      startedAtUtc: '2026-09-16T00:05:00Z',
      completedAtUtc: null,
      blockerCode: null,
      evidenceRefs: [] as string[],
    },
    {
      nodeId: 'node-1',
      sequenceNo: 1,
      kind: 'plan',
      title: '制定计划',
      status: 'passed',
      startedAtUtc: '2026-09-16T00:01:00Z',
      completedAtUtc: '2026-09-16T00:04:00Z',
      blockerCode: null,
      evidenceRefs: [] as string[],
    },
    {
      nodeId: 'node-3',
      sequenceNo: 3,
      kind: 'verify',
      title: '验收',
      status: 'weird_future_status',
      startedAtUtc: null,
      completedAtUtc: null,
      blockerCode: null,
      evidenceRefs: [] as string[],
    },
  ],
  checks: [
    {
      checkId: 'check-1',
      criterionId: '全部测试通过',
      status: 'passed',
      exitCode: 0,
      summary: 'jest 123/123',
      evidenceRefs: [] as string[],
    },
  ],
});

describe('GoalStepsPanel', () => {
  beforeEach(() => {
    mockRequest.mockReset();
  });

  it('renders steps ordered by sequenceNo with current-step highlight and goal-level checks', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    const { container } = render(<GoalStepsPanel goal={makeGoal()} />);

    expect(mockRequest).toHaveBeenCalledWith('/api/v1/goals/goal-1/steps', {
      method: 'GET',
      skipErrorHandler: true,
    });

    await screen.findByText('运行修复');

    const orderedIds = Array.from(
      container.querySelectorAll('[data-goal-step-id]'),
    ).map((node) => node.getAttribute('data-goal-step-id'));
    expect(orderedIds).toEqual(['node-1', 'node-2', 'node-3']);

    expect(screen.getAllByText('当前步骤').length).toBeGreaterThan(0);
    expect(screen.getByText(/共 3 步 · 通过 1 · 失败 0 · 进行中 1/)).toBeTruthy();
    expect(screen.getByText('目标级校验（非逐步）')).toBeTruthy();
    expect(
      screen.getByText(/以下校验针对整个目标，不属于任何单个步骤/),
    ).toBeTruthy();
    expect(screen.getByText(/jest 123\/123/)).toBeTruthy();
  });

  it('degrades unknown step status to a neutral pill with the raw value', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    render(<GoalStepsPanel goal={makeGoal()} />);

    const unknownPill = await screen.findByText(/状态：weird_future_status/);
    expect(unknownPill.getAttribute('data-step-status')).toBe(
      'weird_future_status',
    );
  });

  it('shows an empty state when the goal has no plan', async () => {
    mockRequest.mockResolvedValueOnce({
      ...makeSnapshot(),
      hasPlan: false,
      steps: [],
      checks: [],
      progress: {
        stepsTotal: 0,
        stepsPassed: 0,
        stepsFailed: 0,
        stepsInProgress: 0,
        currentStepId: null,
      },
    });
    render(<GoalStepsPanel goal={makeGoal()} />);

    expect(
      await screen.findByText(/该目标尚未生成执行计划（暂无步骤）。/),
    ).toBeTruthy();
  });

  it('renders a readable hint on 404 instead of crashing or blank screen', async () => {
    mockRequest.mockRejectedValueOnce({ response: { status: 404 } });
    render(<GoalStepsPanel goal={makeGoal()} />);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('404');
    expect(alert.textContent).toContain('/goals/{id}/steps');
  });

  it('renders a readable hint on network failure and supports manual retry', async () => {
    mockRequest.mockRejectedValueOnce(new Error('network down'));
    render(<GoalStepsPanel goal={makeGoal()} />);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('network down');

    mockRequest.mockResolvedValueOnce(makeSnapshot());
    fireEvent.click(screen.getByRole('button', { name: '刷新 Goal 步骤' }));
    await screen.findByText('运行修复');
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
