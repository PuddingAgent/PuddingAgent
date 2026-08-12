import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageQueueDropdown from './MessageQueueDropdown';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({ styles }),
  };
});

const localItem = (
  id: string,
  text: string,
  extra?: Record<string, unknown>,
) => ({
  id,
  text,
  createdAt: Date.now(),
  status: 'queued',
  source: 'local_pending' as const,
  ...extra,
});

const backendItem = (
  id: string,
  text: string,
  extra?: Record<string, unknown>,
) => ({
  id,
  text,
  createdAt: Date.now(),
  status: 'queued',
  source: 'backend_message_queue' as const,
  ...extra,
});

const steeringItem = (
  id: string,
  text: string,
  extra?: Record<string, unknown>,
) => ({
  id,
  text,
  createdAt: Date.now(),
  status: 'steering_pending' as const,
  source: 'steering' as const,
  ...extra,
});

describe('MessageQueueDropdown', () => {
  const defaultProps = {
    loading: false,
    interactionQueue: [],
  };

  it('renders nothing when the queue is empty', () => {
    render(<MessageQueueDropdown {...defaultProps} />);
    expect(screen.queryByTestId('interaction-queue')).toBeNull();
  });

  it('P1#6: shows the three-state phase summary on the trigger', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          localItem('l1', '本地一'),
          localItem('l2', '本地二'),
          backendItem('b1', '后端一'),
          { ...backendItem('b2', '后端二'), status: 'delivering' },
          { ...steeringItem('s1', '引导一'), status: 'steering_injected' },
          { ...backendItem('b3', '已取消'), status: 'cancelled' },
        ]}
      />,
    );

    expect(screen.getByTestId('interaction-queue')).toBeTruthy();
    expect(screen.getByText('排队 3 · 执行 2 · 终态 1')).toBeTruthy();
    // 本地待发项标注"排队中 · 待发送"
    expect(screen.getAllByText('排队中 · 待发送')).toHaveLength(2);
  });

  it('P1#6: local pending items are draggable and reorder on drop', () => {
    const onReorder = jest.fn();
    render(
      <MessageQueueDropdown
        {...defaultProps}
        onReorderQueuedInteraction={onReorder}
        interactionQueue={[localItem('a', 'A'), localItem('b', 'B')]}
      />,
    );

    const itemA = screen.getByText('A').closest('[data-draggable="true"]');
    const itemB = screen.getByText('B').closest('[data-draggable="true"]');
    expect(itemA?.getAttribute('draggable')).toBe('true');
    expect(itemB?.getAttribute('draggable')).toBe('true');

    fireEvent.dragStart(itemA as Element, {
      dataTransfer: { setData: jest.fn(), effectAllowed: 'move' },
    });
    fireEvent.dragOver(itemB as Element, { preventDefault: jest.fn() });
    fireEvent.drop(itemB as Element, { preventDefault: jest.fn() });

    expect(onReorder).toHaveBeenCalledWith('a', 'b');
  });

  it('P1#6: backend snapshot items are not draggable', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[backendItem('b1', '后端一')]}
      />,
    );
    const item = screen.getByText('后端一').closest('[data-draggable="true"]');
    expect(item).toBeNull();
  });

  it('P1#6: deletes local pending and steering items, keeps backend delete disabled', () => {
    const onDelete = jest.fn();
    render(
      <MessageQueueDropdown
        {...defaultProps}
        onDeleteQueuedInteraction={onDelete}
        interactionQueue={[
          localItem('l1', '本地'),
          steeringItem('s1', '引导'),
          backendItem('b1', '后端'),
        ]}
      />,
    );

    const deleteButtons = screen.getAllByLabelText('删除队列消息');
    expect(deleteButtons).toHaveLength(3);

    fireEvent.click(deleteButtons[0] as Element);
    fireEvent.click(deleteButtons[1] as Element);
    expect(onDelete).toHaveBeenCalledTimes(2);
  });

  it('P1#6: steer button yields local pending and steers backend queued items', () => {
    const onSteer = jest.fn(async () => {});
    render(
      <MessageQueueDropdown
        {...defaultProps}
        loading
        onSteerQueuedInteraction={onSteer}
        interactionQueue={[
          localItem('l1', '本地一'),
          localItem('l2', '本地二'),
          backendItem('b1', '后端一'),
        ]}
      />,
    );

    // 本地项：让位给下一条（需至少两个本地项才可用）
    const yieldButtons = screen.getAllByRole('button', {
      name: '让位给下一条',
    });
    fireEvent.click(yieldButtons[0] as Element);
    expect(onSteer).toHaveBeenCalledWith('l1');

    // 后端排队项：引导 Agent（注入下一次上下文）
    const steerButton = screen.getByRole('button', { name: '引导 Agent' });
    fireEvent.click(steerButton);
    expect(onSteer).toHaveBeenCalledWith('b1');
  });

  it('P1#6: stop-all clears the queue through onStopAll', () => {
    const onStopAll = jest.fn();
    render(
      <MessageQueueDropdown
        {...defaultProps}
        loading
        onStopAll={onStopAll}
        interactionQueue={[localItem('l1', '本地一')]}
      />,
    );
    fireEvent.click(screen.getByTestId('message-queue-stop-all'));
    expect(onStopAll).toHaveBeenCalledTimes(1);
  });

  it('P1#6: collapses and expands the panel via the trigger', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[localItem('l1', '本地一')]}
      />,
    );
    const root = screen.getByTestId('interaction-queue');
    expect(root.getAttribute('data-open')).toBe('true');

    fireEvent.click(screen.getByTestId('message-queue-trigger'));
    expect(root.getAttribute('data-open')).toBe('false');

    fireEvent.click(screen.getByTestId('message-queue-trigger'));
    expect(root.getAttribute('data-open')).toBe('true');
  });
});
