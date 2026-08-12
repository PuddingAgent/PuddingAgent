import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import ApprovalCard from './ApprovalCard';

describe('ApprovalCard', () => {
  const baseProps = {
    approvalId: 'approval-1',
    toolName: 'shell',
    description: '执行命令 rm -rf /tmp/build',
    riskLevel: 'high' as const,
    status: 'pending' as const,
    requestedAt: '2026-08-12T00:00:00.000Z',
  };

  it('renders pending card with tool, description, risk tag and three actions', () => {
    render(<ApprovalCard {...baseProps} />);

    expect(screen.getByTestId('approval-card')).toBeTruthy();
    expect(screen.getByTestId('approval-card-tool').textContent).toContain(
      'shell',
    );
    expect(
      screen.getByTestId('approval-card-description').textContent,
    ).toContain('rm -rf');
    expect(screen.getByTestId('approval-card-risk').textContent).toContain(
      '高风险',
    );
    expect(screen.getByTestId('approval-card-allow-once')).toBeTruthy();
    expect(screen.getByTestId('approval-card-always-allow')).toBeTruthy();
    expect(screen.getByTestId('approval-card-deny')).toBeTruthy();
  });

  it('calls onDecide with allow_once when 允许一次 is clicked', async () => {
    const onDecide = jest.fn().mockResolvedValue(undefined);
    render(<ApprovalCard {...baseProps} onDecide={onDecide} />);

    fireEvent.click(screen.getByTestId('approval-card-allow-once'));
    await waitFor(() =>
      expect(onDecide).toHaveBeenCalledWith('allow_once', undefined),
    );
  });

  it('calls onDecide with deny and the entered reason', async () => {
    const onDecide = jest.fn().mockResolvedValue(undefined);
    render(<ApprovalCard {...baseProps} onDecide={onDecide} />);

    fireEvent.change(screen.getByTestId('approval-card-reason-input'), {
      target: { value: '暂不批准该命令' },
    });
    fireEvent.click(screen.getByTestId('approval-card-deny'));
    await waitFor(() =>
      expect(onDecide).toHaveBeenCalledWith('deny', '暂不批准该命令'),
    );
  });

  it('shows read-only decision for approved cards and hides actions', () => {
    render(
      <ApprovalCard
        {...baseProps}
        status="approved"
        decision="always_allow"
        reason="该工具安全"
      />,
    );

    expect(screen.getByTestId('approval-card-status').textContent).toContain(
      '已批准',
    );
    expect(screen.getByTestId('approval-card-decision-tag').textContent).toContain(
      '始终允许',
    );
    expect(
      screen.getByTestId('approval-card-decision-reason').textContent,
    ).toContain('该工具安全');
    expect(screen.queryByTestId('approval-card-actions')).toBeNull();
  });

  it('shows 审批已过期 for an expired pending card and disables actions', () => {
    render(
      <ApprovalCard
        {...baseProps}
        expiresAt="2000-01-01T00:00:00.000Z"
      />,
    );

    expect(screen.getByTestId('approval-card-expired').textContent).toContain(
      '审批已过期',
    );
    expect(screen.queryByTestId('approval-card-actions')).toBeNull();
  });
});
