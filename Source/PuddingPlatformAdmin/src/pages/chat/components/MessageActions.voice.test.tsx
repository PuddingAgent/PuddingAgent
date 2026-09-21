import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import MessageActions from './MessageActions';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
  };
});

describe('MessageActions voice output', () => {
  it('keeps action buttons mounted while hidden so hovering never changes card height', () => {
    // 2026-09-21 修「hover 抖动」：隐藏态**不得卸载按钮行**。
    // 卸载会让卡片高度在「28px 按钮行 + 6px 上边距」之间反复跳变：
    // 鼠标进入 ⇒ 卡片变高 ⇒ 鼠标相对位置改变 ⇒ 触发 leave ⇒ 变矮 ⇒ 又 enter …（鬼畜抖动）。
    // 契约：隐藏只切透明度类（messageActionsNew → messageActionsVisible），
    // 两种状态下 DOM 结构与按钮数量完全一致。
    const sharedProps = {
      content: '整理今天的会议记录。',
      onCopy: jest.fn(),
      onRerun: jest.fn(),
      onPin: jest.fn(),
      onDelete: jest.fn(),
    };

    const hidden = render(<MessageActions {...sharedProps} visible={false} />);
    const hiddenRow = hidden.container.querySelector(
      '[data-testid="message-actions"]',
    );
    expect(hiddenRow).not.toBeNull();
    expect(hiddenRow!.querySelectorAll('button').length).toBe(4);

    const shown = render(<MessageActions {...sharedProps} visible />);
    const shownRow = shown.container.querySelector(
      '[data-testid="message-actions"]',
    );
    expect(shownRow).not.toBeNull();
    // 核心断言：按钮数量不随显隐变化 ⇒ 不会触发 reflow
    expect(shownRow!.querySelectorAll('button').length).toBe(
      hiddenRow!.querySelectorAll('button').length,
    );
    // 仅类名不同（可见态追加 messageActionsVisible，即仅 opacity 1 与 pointerEvents auto）
    expect(shownRow!.className).not.toBe(hiddenRow!.className);
  });

  it('speaks assistant text and lets the user stop playback', async () => {
    const handle = { stop: jest.fn() };
    let handlers: any;
    const voiceOutputAdapter = {
      isSupported: () => true,
      speak: jest.fn((_text: string, nextHandlers: any) => {
        handlers = nextHandlers;
        handlers.onStart?.();
        return handle;
      }),
    };

    render(
      <MessageActions
        content="整理今天的会议记录。"
        visible
        voiceOutputAdapter={voiceOutputAdapter}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '朗读回复' }));

    expect(voiceOutputAdapter.speak).toHaveBeenCalledWith(
      '整理今天的会议记录。',
      expect.objectContaining({ lang: 'zh-CN' }),
    );
    expect(
      await screen.findByRole('button', { name: '停止朗读' }),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '停止朗读' }));

    expect(handle.stop).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: '朗读回复' })).toBeTruthy();
    });
  });

  it('keeps voice output unavailable when browser speech synthesis is missing', () => {
    render(
      <MessageActions
        content="整理今天的会议记录。"
        visible
        voiceOutputAdapter={{
          isSupported: () => false,
          speak: jest.fn(),
        }}
      />,
    );

        const button = screen.getByRole('button', { name: '浏览器不支持语音朗读' });

    expect((button as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('MessageActions copy feedback (P0-4)', () => {
  beforeEach(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: jest.fn().mockResolvedValue(undefined) },
    });
  });

  it('switches the copy icon to a check for 1s then restores', () => {
    jest.useFakeTimers();
    try {
      const onCopy = jest.fn();
      render(
        <MessageActions content="整理今天的会议记录。" visible onCopy={onCopy} />,
      );

      const copyButton = screen.getByRole('button', { name: '复制' });
      fireEvent.click(copyButton);

      expect(onCopy).toHaveBeenCalledTimes(1);
      expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
        '整理今天的会议记录。',
      );
      expect(screen.getByRole('button', { name: '已复制' })).toBeTruthy();

      React.act(() => {
        jest.advanceTimersByTime(1_000);
      });
      expect(screen.getByRole('button', { name: '复制' })).toBeTruthy();
      expect(screen.queryByRole('button', { name: '已复制' })).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('resets the pending state when clicked again before the timer fires', () => {
    jest.useFakeTimers();
    try {
      render(<MessageActions content="文本" visible />);

      fireEvent.click(screen.getByRole('button', { name: '复制' }));
      fireEvent.click(screen.getByRole('button', { name: '已复制' }));

      // 防重入：第二次点击重置定时器，仍处于已复制态
      expect(screen.getByRole('button', { name: '已复制' })).toBeTruthy();

      React.act(() => {
        jest.advanceTimersByTime(1_000);
      });
      expect(screen.getByRole('button', { name: '复制' })).toBeTruthy();
    } finally {
      jest.useRealTimers();
    }
  });

  it('does not set state after unmount while the feedback timer is pending', () => {
    jest.useFakeTimers();
    try {
      const { unmount } = render(<MessageActions content="文本" visible />);
      fireEvent.click(screen.getByRole('button', { name: '复制' }));
      expect(screen.getByRole('button', { name: '已复制' })).toBeTruthy();

      unmount();
      React.act(() => {
        jest.advanceTimersByTime(1_000);
      });
      // 卸载保护生效：无 act 警告/错误即通过
    } finally {
      jest.useRealTimers();
    }
  });
});
