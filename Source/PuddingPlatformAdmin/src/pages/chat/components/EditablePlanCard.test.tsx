import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import EditablePlanCard from './EditablePlanCard';

describe('EditablePlanCard', () => {
  const baseSteps = [
    { id: 's1', title: '调研现有 SSE 事件流', description: '阅读 SessionEventsController' },
    { id: 's2', title: '实现 plan.proposal 投影', description: '前端 useSessionEventProjection' },
    { id: 's3', title: '渲染 EditablePlanCard', description: '' },
  ];

  const baseProps = {
    planId: 'plan-1',
    summary: '为 Chat UI 实现 Plan 模式',
    steps: baseSteps,
    status: 'pending' as const,
    requestedAt: '2026-08-12T00:00:00.000Z',
  };

  it('renders pending card with summary, steps and three actions', () => {
    render(<EditablePlanCard {...baseProps} />);

    expect(screen.getByTestId('editable-plan-card')).toBeTruthy();
    expect(screen.getByTestId('plan-card-title').textContent).toContain(
      '执行计划',
    );
    expect(screen.getByTestId('plan-card-mode').textContent).toContain(
      'Plan 模式',
    );
    expect(screen.getByTestId('plan-card-summary').textContent).toContain(
      '为 Chat UI 实现 Plan 模式',
    );
    expect(screen.getAllByTestId(/^plan-step-row-/)).toHaveLength(3);
    expect(screen.getByTestId('plan-card-approve-build')).toBeTruthy();
    expect(screen.getByTestId('plan-card-manual')).toBeTruthy();
    expect(screen.getByTestId('plan-card-keep-planning')).toBeTruthy();
  });

  it('allows editing a step title inline', () => {
    render(<EditablePlanCard {...baseProps} />);

    const titleInput = screen.getByTestId('plan-step-title-0');
    fireEvent.change(titleInput, {
      target: { value: '调研 SSE 事件流（已更新）' },
    });
    expect((titleInput as HTMLInputElement).value).toBe(
      '调研 SSE 事件流（已更新）',
    );
  });

  it('allows deleting a step', () => {
    render(<EditablePlanCard {...baseProps} />);

    fireEvent.click(screen.getByTestId('plan-step-delete-1'));
    expect(screen.getAllByTestId(/^plan-step-row-/)).toHaveLength(2);
    expect(screen.queryByTestId('plan-step-row-2')).toBeNull();
  });

  it('reorders steps via drag-and-drop (move-to-index)', () => {
    render(<EditablePlanCard {...baseProps} />);

    const firstTitle = (screen.getByTestId('plan-step-title-0') as HTMLInputElement)
      .value;
    const secondTitle = (screen.getByTestId('plan-step-title-1') as HTMLInputElement)
      .value;

    fireEvent.dragStart(screen.getByTestId('plan-step-row-0'));
    fireEvent.dragOver(screen.getByTestId('plan-step-row-2'));
    fireEvent.drop(screen.getByTestId('plan-step-row-2'));
    fireEvent.dragEnd(screen.getByTestId('plan-step-row-0'));

    // 移动语义：第 1 步插入到目标下标 2，最终顺序为 [s2, s3, s1]
    expect((screen.getByTestId('plan-step-title-0') as HTMLInputElement).value)
      .toBe(secondTitle);
    expect((screen.getByTestId('plan-step-title-2') as HTMLInputElement).value)
      .toBe(firstTitle);
  });

  it('calls onDecide with approve_and_build and the current steps', async () => {
    const onDecide = jest.fn().mockResolvedValue(undefined);
    render(<EditablePlanCard {...baseProps} onDecide={onDecide} />);

    // 编辑后点击“批准并构建”，应带上编辑后的步骤
    fireEvent.change(screen.getByTestId('plan-step-title-0'), {
      target: { value: '编辑后的第一步' },
    });
    fireEvent.click(screen.getByTestId('plan-card-approve-build'));
    await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));
    const [decision, steps] = onDecide.mock.calls[0];
    expect(decision).toBe('approve_and_build');
    expect(steps[0].title).toBe('编辑后的第一步');
    expect(steps).toHaveLength(3);
  });

  it('calls onDecide with manual and keep_planning', async () => {
    const onDecide = jest.fn().mockResolvedValue(undefined);
    render(<EditablePlanCard {...baseProps} onDecide={onDecide} />);

    fireEvent.click(screen.getByTestId('plan-card-manual'));
    await waitFor(() =>
      expect(onDecide).toHaveBeenCalledWith('manual', expect.any(Array)),
    );

    fireEvent.click(screen.getByTestId('plan-card-keep-planning'));
    await waitFor(() =>
      expect(onDecide).toHaveBeenCalledWith(
        'keep_planning',
        expect.any(Array),
      ),
    );
  });

  it('renders read-only finalized state and hides actions', () => {
    render(
      <EditablePlanCard
        {...baseProps}
        status="finalized"
        decision="approve_and_build"
        decidedAt="2026-08-12T01:00:00.000Z"
      />,
    );

    expect(screen.getByTestId('plan-card-status').textContent).toContain(
      '已批准并构建',
    );
    expect(screen.getByTestId('plan-card-decision-tag').textContent).toContain(
      '已批准并构建',
    );
    expect(screen.queryByTestId('plan-card-actions')).toBeNull();
    // 终态下输入框禁用
    expect(
      (screen.getByTestId('plan-step-title-0') as HTMLInputElement).disabled,
    ).toBe(true);
  });
});
