import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import PinnedMessageButton from './PinnedMessageButton';

describe('PinnedMessageButton', () => {
  beforeEach(() => {
    localStorage.clear();
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.runOnlyPendingTimers();
    jest.useRealTimers();
  });

  it('renders as a normal control button when a message is pinned', () => {
    localStorage.setItem(
      'pudding_pinned_message',
      JSON.stringify({
        turnId: 'turn-1633',
        preview: '前三行摘要',
        fullText: '完整消息',
        pinnedAt: 1_717_000_000_000,
      }),
    );

    render(<PinnedMessageButton onQuote={jest.fn()} />);

    const button = screen.getByRole('button', { name: /钉住的消息/ });
    expect(button.style.position).toBe('');
    expect(button.style.right).toBe('');
    expect(button.style.bottom).toBe('');
  });

  it('single-clicks the pinned button to append a structured quote', async () => {
    localStorage.setItem(
      'pudding_pinned_message',
      JSON.stringify({
        messageId: 1633,
        turnId: 'turn-1633',
        preview:
          '## 当前状态总览\n### 记忆系统结构化项目\n| Phase | 状态 | 内容 | Commit |',
        fullText: '完整消息',
        pinnedAt: 1_717_000_000_000,
      }),
    );
    const onQuote = jest.fn();

    render(<PinnedMessageButton onQuote={onQuote} />);

    fireEvent.click(screen.getByRole('button', { name: /钉住的消息/ }));
    jest.runOnlyPendingTimers();

    await waitFor(() => {
      expect(onQuote).toHaveBeenCalledWith(
          [
            '> 消息ID：1633',
            '> 请通过Query Session Log工具获取原始信息',
            '> 摘要：## 当前状态总览',
            '> ### 记忆系统结构化项目',
            '> | Phase | 状态 | 内容 | Commit |',
            '',
          ].join('\n'),
        );
    });
  });

  it('double-clicks the pinned button to clear without quoting', async () => {
    localStorage.setItem(
      'pudding_pinned_message',
      JSON.stringify({
        turnId: 'turn-1633',
        preview: '前三行摘要',
        fullText: '完整消息',
        pinnedAt: 1_717_000_000_000,
      }),
    );
    const onQuote = jest.fn();

    render(<PinnedMessageButton onQuote={onQuote} />);

    fireEvent.click(screen.getByRole('button', { name: /钉住的消息/ }));
    fireEvent.doubleClick(screen.getByRole('button', { name: /钉住的消息/ }));
    jest.runOnlyPendingTimers();

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: /钉住的消息/ })).toBeNull();
    });
    expect(onQuote).not.toHaveBeenCalled();
    expect(localStorage.getItem('pudding_pinned_message')).toBeNull();
  });
});
