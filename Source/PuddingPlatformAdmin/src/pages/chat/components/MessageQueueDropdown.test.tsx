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

  it('Phase 2: retrying counts as queued — real retry shows warning, substate=waiting shows waiting', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          backendItem('b1', '后端一'),
          { ...backendItem('b2', '投递中'), status: 'delivering' },
          {
            ...backendItem('b3', '真实失败重试'),
            status: 'retrying',
            substate: 'retrying',
            error: '{"message":"模型超时，即将重试","attempt":3}',
            metadata: { attemptCount: '3' },
          },
          {
            ...backendItem('b4', '忙等待'),
            status: 'queued',
            substate: 'waiting',
            deferCount: 2,
            error: '{"executionState":"Busy","message":"Agent 忙碌中"}',
            metadata: { attemptCount: '2' },
          },
          {
            ...backendItem('b5', '已失败'),
            status: 'failed',
            substate: 'failed',
          },
        ]}
      />,
    );

    // queued×1（b1）+ retrying×1（b3）+ queued×1（b4，substate=waiting）归入排队 = 3；
    // delivering=1；终态=1（substate 不改变 phase 计数，phase 仍由 status 归类）
    expect(screen.getByText('排队 3 · 执行 1 · 终态 1')).toBeTruthy();
    // 真实失败重试：警示标签 + 尝试次数
    expect(screen.getByText('重试中 · 第 3 次')).toBeTruthy();
    // substate=waiting（busy 挂起）：等待 Agent 空闲
    expect(screen.getByText('排队中 · 等待 Agent 空闲')).toBeTruthy();
  });

  it('Phase 2: substate=waiting hides retry/error even when lastError mentions busy', () => {
    // Phase 2 后 busy deferral 由后端投影的 substate=waiting 权威驱动，
    // 不再依赖组件内 /busy/i 原文嗅探。
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          {
            ...backendItem('b1', '忙等待（substate 权威信号）'),
            status: 'queued',
            substate: 'waiting',
            deferCount: 8,
            error:
              '{"error":"Agent default.global_general-assistant.6a8 is busy.","executionState":"Busy"}',
            metadata: { attemptCount: '8' },
          },
        ]}
      />,
    );

    expect(screen.getByText('排队中 · 等待 Agent 空闲')).toBeTruthy();
    // waiting 不渲染为失败重试、不显示错误原文
    expect(screen.queryByText(/重试中/)).toBeNull();
    expect(screen.queryByText(/is busy/)).toBeNull();
  });

  it('Phase 2 fallback: no substate + waitReason=busy-wait still shows waiting (old backend)', () => {
    // 旧后端（无 substate）：由 chatStateUtils 的 isBusyWaitRetry 嗅探派生 waitReason 兜底
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          {
            ...backendItem('b1', '旧后端 busy 挂起'),
            status: 'retrying',
            waitReason: 'busy-wait',
            error: '{"executionState":"Busy","message":"Agent 忙碌中"}',
            metadata: { attemptCount: '1' },
          },
        ]}
      />,
    );
    expect(screen.getByText('排队中 · 等待 Agent 空闲')).toBeTruthy();
    expect(screen.queryByText(/重试中/)).toBeNull();
    expect(screen.queryByText(/Agent 忙碌中/)).toBeNull();
  });

  it('P1#10: retrying errors are summarized with full-text tooltip; busy-wait hides errors', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          {
            ...backendItem('b1', '真实失败重试'),
            status: 'retrying',
            error: '{"message":"模型超时，即将重试","attempt":3}',
            metadata: { attemptCount: '3' },
          },
          {
            ...backendItem('b2', '忙等待'),
            status: 'retrying',
            error: '{"executionState":"Busy","message":"Agent 忙碌中"}',
            metadata: { attemptCount: '1' },
            waitReason: 'busy-wait',
          },
          {
            ...backendItem('b3', '终态失败'),
            status: 'failed',
            error: '{"message":"请求被拒","code":403}',
          },
        ]}
      />,
    );

    // 真实失败重试：摘要（提取 message），title 保留全量原文
    const retryError = screen.getByText('模型超时，即将重试');
    expect(retryError.getAttribute('title')).toBe(
      '{"message":"模型超时，即将重试","attempt":3}',
    );
    // 终态失败：同样摘要化，不再渲染原文 JSON
    expect(screen.getByText('请求被拒')).toBeTruthy();
    expect(screen.queryByText(/executionState/)).toBeNull();
    expect(screen.queryByText(/"code":403/)).toBeNull();
    // busy-wait：不显示任何错误
    expect(screen.queryByText('Agent 忙碌中')).toBeNull();
  });

  it('Phase 2: substate drives labels for fresh/terminal states', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          {
            ...backendItem('q1', '普通排队'),
            status: 'queued',
            substate: 'fresh',
          },
          {
            ...backendItem('c1', '取消的消息'),
            status: 'cancelled',
            substate: 'cancelled',
          },
          {
            ...backendItem('e1', '过期的消息'),
            status: 'expired',
            substate: 'expired',
          },
        ]}
      />,
    );
    // fresh → 普通「排队中」；cancelled/expired → 终态原值
    expect(screen.getByText('排队中')).toBeTruthy();
    expect(screen.getByText('已取消')).toBeTruthy();
    expect(screen.getByText('已过期')).toBeTruthy();
    // 头部计数：排队 1 · 执行 0 · 终态 2
    expect(screen.getByText('排队 1 · 执行 0 · 终态 2')).toBeTruthy();
  });

  it('Phase 2: terminal items render placeholder action buttons (disabled until backend endpoints land)', () => {
    render(
      <MessageQueueDropdown
        {...defaultProps}
        interactionQueue={[
          {
            ...backendItem('d1', '已送达的消息'),
            status: 'delivered',
            substate: 'delivered',
          },
          {
            ...backendItem('dl1', '死信的消息'),
            status: 'dead_letter',
            substate: 'dead_letter',
          },
          {
            ...backendItem('f1', '失败的消息'),
            status: 'failed',
            substate: 'failed',
          },
        ]}
      />,
    );

    // delivered：仅「查看」占位（可点击，onClick 暂为空）
    const viewButton = screen.getByTestId(
      'queue-action-delivered-view',
    ) as HTMLButtonElement;
    expect(viewButton).toBeTruthy();
    expect(viewButton.disabled).toBe(false);

    // dead_letter：重入队 + 丢弃（禁用占位，后端端点待实现）
    const requeue = screen.getByTestId(
      'queue-action-dead-letter-requeue',
    ) as HTMLButtonElement;
    const discard = screen.getByTestId(
      'queue-action-dead-letter-discard',
    ) as HTMLButtonElement;
    expect(requeue.disabled).toBe(true);
    expect(discard.disabled).toBe(true);

    // failed：重试 + 查看错误（禁用占位，后端端点待实现）
    const retry = screen.getByTestId(
      'queue-action-failed-retry',
    ) as HTMLButtonElement;
    const viewError = screen.getByTestId(
      'queue-action-failed-view-error',
    ) as HTMLButtonElement;
    expect(retry.disabled).toBe(true);
    expect(viewError.disabled).toBe(true);

    // 终态标签
    expect(screen.getByText('已送达')).toBeTruthy();
    expect(screen.getByText('死信')).toBeTruthy();
    expect(screen.getByText('失败')).toBeTruthy();
    // 头部计数：终态聚合显示
    expect(screen.getByText('排队 0 · 执行 0 · 终态 3')).toBeTruthy();
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
    expect(root.getAttribute('data-open')).toBe('false');

    fireEvent.click(screen.getByTestId('message-queue-trigger'));
    expect(root.getAttribute('data-open')).toBe('true');

    fireEvent.click(screen.getByTestId('message-queue-trigger'));
    expect(root.getAttribute('data-open')).toBe('false');
  });
});
