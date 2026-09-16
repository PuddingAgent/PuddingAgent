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

const makeTodoFound = () => ({
  goalRunId: 'goal-1',
  found: true,
  listId: 'tdl-1',
  title: '看板梳理拆解',
  revision: 3,
  items: [
    {
      slug: 'audit',
      title: '审计现状',
      status: 'in_progress',
      note: '正在扫描',
      evidenceRef: null,
      blockedReason: null,
      orderIndex: 0,
      startedAtUtc: null,
      completedAtUtc: null,
    },
    {
      slug: 'fix',
      title: '修复根因',
      status: 'completed',
      note: null,
      evidenceRef: 'commit:deadbee',
      blockedReason: null,
      orderIndex: 1,
      startedAtUtc: '2026-09-16T00:02:00Z',
      completedAtUtc: '2026-09-16T00:08:00Z',
    },
    {
      slug: 'blocked-item',
      title: '等待上游接口',
      status: 'blocked',
      note: null,
      evidenceRef: null,
      blockedReason: '依赖外部修复',
      orderIndex: 2,
      startedAtUtc: null,
      completedAtUtc: null,
    },
  ],
  summary: {
    total: 3,
    pending: 0,
    inProgress: 1,
    completed: 1,
    blocked: 1,
    currentSlug: 'audit',
    blockedSlugs: ['blocked-item'],
  },
});

const makeTodoNotFound = () => ({
  goalRunId: 'goal-1',
  found: false,
  listId: null,
  title: null,
  revision: 0,
  items: [],
  summary: null,
});

describe('GoalStepsPanel', () => {
  beforeEach(() => {
    mockRequest.mockReset();
  });

  it('renders steps ordered by sequenceNo with current-step highlight and goal-level checks', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockResolvedValueOnce(makeTodoFound());
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
    mockRequest.mockResolvedValueOnce(makeTodoFound());
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
    mockRequest.mockResolvedValueOnce(makeTodoNotFound());
    render(<GoalStepsPanel goal={makeGoal()} />);

    expect(
      await screen.findByText(/该目标尚未生成执行计划（暂无步骤）。/),
    ).toBeTruthy();
  });

  it('renders a readable hint on 404 instead of crashing or blank screen', async () => {
    mockRequest.mockRejectedValueOnce({ response: { status: 404 } });
    mockRequest.mockResolvedValueOnce(makeTodoNotFound());
    render(<GoalStepsPanel goal={makeGoal()} />);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('404');
    expect(alert.textContent).toContain('/goals/{id}/steps');
  });

  it('renders a readable hint on network failure and supports manual retry', async () => {
    mockRequest.mockRejectedValueOnce(new Error('network down'));
    mockRequest.mockResolvedValueOnce(makeTodoFound());
    render(<GoalStepsPanel goal={makeGoal()} />);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('network down');

    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockResolvedValueOnce(makeTodoFound());
    fireEvent.click(screen.getByRole('button', { name: '刷新 Goal 步骤' }));
    await screen.findByText('运行修复');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('renders plan version, step timestamps and compact evidence refs', async () => {
    const snapshot = makeSnapshot();
    snapshot.steps = [
      ...snapshot.steps,
      {
        nodeId: 'node-4',
        sequenceNo: 4,
        kind: 'verify',
        title: '归档证据',
        status: 'completed',
        startedAtUtc: '2026-09-16T00:06:00Z',
        completedAtUtc: '2026-09-16T00:07:30Z',
        blockerCode: null,
        evidenceRefs: [
          'logs/run-42/very/long/path/evidence-0001-long-name.json',
        ],
      },
    ];
    mockRequest.mockResolvedValueOnce(snapshot);
    mockRequest.mockResolvedValueOnce(makeTodoFound());
    const { container } = render(<GoalStepsPanel goal={makeGoal()} />);

    expect(await screen.findByText('归档证据')).toBeTruthy();
    expect(screen.getByText(/计划版本 v2/)).toBeTruthy();

    const row = container.querySelector('[data-goal-step-id="node-4"]');
    expect(row?.getAttribute('data-step-started')).toBe(
      '2026-09-16T00:06:00Z',
    );
    expect(row?.getAttribute('data-step-completed')).toBe(
      '2026-09-16T00:07:30Z',
    );
    expect(row?.textContent).toContain('开始 ');
    expect(row?.textContent).toContain('完成 ');
    expect(row?.textContent).toContain('证据：');
    expect(row?.textContent).toContain('evidence-0001');
    expect(row?.textContent).not.toContain('.json');
  });

  it('renders todo breakdown with blocked items on top and self-reported progress label', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockResolvedValueOnce(makeTodoFound());
    const { container } = render(<GoalStepsPanel goal={makeGoal()} />);

    expect(await screen.findByText('等待上游接口')).toBeTruthy();

    // 两条进度分列且标注来源（设计 §6.3）：拆解=自述，验收=平台裁决，禁止合并。
    expect(
      screen.getByText(/自述进度 1\/3（不代表目标达成）/),
    ).toBeTruthy();
    expect(screen.getByText(/平台裁决/)).toBeTruthy();

    // 受阻项置顶；其余保持 orderIndex 升序。
    const slugs = Array.from(
      container.querySelectorAll('[data-todo-slug]'),
    ).map((node) => node.getAttribute('data-todo-slug'));
    expect(slugs).toEqual(['blocked-item', 'audit', 'fix']);

    // 受阻项展示结构化受阻说明；in_progress/currentSlug 项高亮。
    expect(screen.getByText(/受阻：依赖外部修复/)).toBeTruthy();
    const auditRow = container.querySelector('[data-todo-slug="audit"]');
    expect(auditRow?.getAttribute('data-todo-status')).toBe('in_progress');
    expect(auditRow?.textContent).toContain('正在扫描');
    expect(screen.getByText('审计现状')).toBeTruthy();
    expect(screen.getByText('修复根因')).toBeTruthy();
  });

  it('shows an approachable empty state when the agent has not written a breakdown', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockResolvedValueOnce(makeTodoNotFound());
    render(<GoalStepsPanel goal={makeGoal()} />);

    await screen.findByText('运行修复');
    const empty = screen.getByTestId('todo-empty');
    expect(empty.textContent).toContain('尚未写拆解');
  });

  it('falls back to a readable hint when todo fetch fails while steps still render', async () => {
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockRejectedValueOnce(new Error('todo down'));
    render(<GoalStepsPanel goal={makeGoal()} />);

    // 步骤区正常渲染（一个失败不影响另一区）。
    await screen.findByText('运行修复');
    const todoFailure = screen.getByTestId('todo-failure');
    expect(todoFailure.textContent).toContain('todo down');

    // 手动刷新恢复。
    mockRequest.mockResolvedValueOnce(makeSnapshot());
    mockRequest.mockResolvedValueOnce(makeTodoFound());
    fireEvent.click(screen.getByRole('button', { name: '刷新 Goal 步骤' }));
    await screen.findByText(/自述进度 1\/3/);
    expect(screen.queryByTestId('todo-failure')).toBeNull();
  });
});
