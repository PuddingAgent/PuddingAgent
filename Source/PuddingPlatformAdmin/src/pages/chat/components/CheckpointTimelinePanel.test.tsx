// ── CheckpointTimelinePanel 组件测试 (P2#7) ───────────────────
import * as React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import CheckpointTimelinePanel from './CheckpointTimelinePanel';
import type { ChatCheckpoint } from '../client/checkpointStore';

const checkpoint = (
  id: string,
  label: string,
  createdAt: number,
  turnIndex: number,
): ChatCheckpoint => ({
  checkpointId: id,
  sessionId: 'session-1',
  workspaceId: 'ws-1',
  agentId: 'agent-1',
  createdAt,
  turnIndex,
  label,
  turns: [],
});

describe('CheckpointTimelinePanel', () => {
  const baseProps = {
    open: true,
    sessionId: 'session-1',
    checkpoints: [],
    restoredCheckpointId: null,
    formatTime: (ts: number) => `T${ts}`,
    onRestore: jest.fn(),
    onFork: jest.fn(),
    onDelete: jest.fn(),
    onClearAll: jest.fn(),
    onClose: jest.fn(),
    forkLoading: false,
  };

  it('renders nothing when closed', () => {
    render(<CheckpointTimelinePanel {...baseProps} open={false} />);
    expect(screen.queryByTestId('checkpoint-panel')).toBeNull();
  });

  it('shows empty state when no checkpoints', () => {
    render(<CheckpointTimelinePanel {...baseProps} />);
    expect(screen.getByTestId('checkpoint-panel')).toBeTruthy();
    expect(screen.getByTestId('checkpoint-empty')).toBeTruthy();
    expect(screen.queryByTestId('checkpoint-list')).toBeNull();
  });

  it('renders a sorted list newest-first with time/count/label', () => {
    const checkpoints = [
      checkpoint('cp-old', '较早快照', 1000, 1),
      checkpoint('cp-new', '最新快照', 3000, 3),
      checkpoint('cp-mid', '中间快照', 2000, 2),
    ];
    render(
      <CheckpointTimelinePanel {...baseProps} checkpoints={checkpoints} />,
    );

    const items = screen.getAllByTestId(/^checkpoint-item-cp-/);
    expect(items).toHaveLength(3);
    // 最新在前
    const labels = screen
      .getAllByTestId('checkpoint-item-label')
      .map((el) => el.textContent);
    expect(labels).toEqual(['最新快照', '中间快照', '较早快照']);
    const times = screen
      .getAllByTestId('checkpoint-item-time')
      .map((el) => el.textContent);
    expect(times).toEqual(['T3000', 'T2000', 'T1000']);
    const counts = screen
      .getAllByTestId('checkpoint-item-count')
      .map((el) => el.textContent);
    expect(counts).toEqual(['3 轮', '2 轮', '1 轮']);
  });

  it('marks the restored checkpoint and disables its Restore button', () => {
    const checkpoints = [
      checkpoint('cp-1', '快照1', 1000, 1),
      checkpoint('cp-2', '快照2', 2000, 2),
    ];
    render(
      <CheckpointTimelinePanel
        {...baseProps}
        checkpoints={checkpoints}
        restoredCheckpointId="cp-1"
      />,
    );

    const restoredItem = screen.getByTestId('checkpoint-item-cp-1');
    expect(restoredItem.getAttribute('data-restored')).toBe('true');
    expect(
      (screen.getByTestId('checkpoint-restore-cp-1') as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    expect(screen.getByTestId('checkpoint-restore-cp-1').textContent).toContain(
      '已还原',
    );
    expect(
      (screen.getByTestId('checkpoint-restore-cp-2') as HTMLButtonElement)
        .disabled,
    ).toBe(false);
  });

  it('calls onRestore / onFork / onDelete when actions clicked', () => {
    const onRestore = jest.fn();
    const onFork = jest.fn();
    const onDelete = jest.fn();
    const checkpoints = [checkpoint('cp-1', '快照1', 1000, 1)];

    render(
      <CheckpointTimelinePanel
        {...baseProps}
        checkpoints={checkpoints}
        onRestore={onRestore}
        onFork={onFork}
        onDelete={onDelete}
      />,
    );

    fireEvent.click(screen.getByTestId('checkpoint-restore-cp-1'));
    expect(onRestore).toHaveBeenCalledWith('cp-1');

    fireEvent.click(screen.getByTestId('checkpoint-fork-cp-1'));
    expect(onFork).toHaveBeenCalledWith('cp-1');

    fireEvent.click(screen.getByTestId('checkpoint-delete-cp-1'));
    expect(onDelete).toHaveBeenCalledWith('cp-1');
  });

  it('calls onClearAll / onClose from the header', () => {
    const onClearAll = jest.fn();
    const onClose = jest.fn();
    render(
      <CheckpointTimelinePanel
        {...baseProps}
        checkpoints={[checkpoint('cp-1', '快照1', 1000, 1)]}
        onClearAll={onClearAll}
        onClose={onClose}
      />,
    );

    fireEvent.click(screen.getByTestId('checkpoint-clear-all'));
    expect(onClearAll).toHaveBeenCalled();

    fireEvent.click(screen.getByTestId('checkpoint-close'));
    expect(onClose).toHaveBeenCalled();
  });

  it('prompts for session selection when sessionId is null', () => {
    render(<CheckpointTimelinePanel {...baseProps} sessionId={null} />);
    expect(screen.getByText('请先选择会话')).toBeTruthy();
  });
});
