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
  it('does not mount action buttons while hidden', () => {
    render(
      <MessageActions
        content="整理今天的会议记录。"
        visible={false}
        onCopy={jest.fn()}
        onRerun={jest.fn()}
        onPin={jest.fn()}
        onDelete={jest.fn()}
      />,
    );

    expect(screen.queryByRole('button', { name: '复制' })).toBeNull();
    expect(screen.queryByRole('button', { name: '重新生成' })).toBeNull();
    expect(screen.queryByRole('button', { name: '固定' })).toBeNull();
    expect(screen.queryByRole('button', { name: '删除' })).toBeNull();
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
