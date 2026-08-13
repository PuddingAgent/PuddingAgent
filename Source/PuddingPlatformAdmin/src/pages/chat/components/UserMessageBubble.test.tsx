import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import UserMessageBubble from './UserMessageBubble';

jest.mock('../styles', () => {
  const styles = new Proxy({}, { get: (_target, prop) => String(prop) });
  return {
    useChatStyles: () => ({
      styles,
      cx: (...names: Array<string | false | undefined>) =>
        names.filter(Boolean).join(' '),
    }),
  };
});

type RenderBubbleProps = Partial<{
  content: string;
  createdAt: number;
  status: string;
  modality: 'text' | 'voice' | 'camera' | 'image';
  visionArtifactId: string;
  visionArtifactIds: string[];
  workspaceId: string;
  userName: string;
  userAvatarUrl: string;
  metadata: Record<string, string>;
  formatTime: (ts: number) => string;
}>;

const renderBubble = (props: RenderBubbleProps = {}) =>
  render(
    <UserMessageBubble
      content="你好"
      createdAt={1000}
      status="success"
      userName="我"
      formatTime={() => '10:24'}
      {...props}
    />,
  );

describe('UserMessageBubble voice metadata', () => {
  it('marks user messages sent from voice input', () => {
    renderBubble({ modality: 'voice', content: '请总结今天的工作' });

    expect(screen.getByText('Voice')).toBeTruthy();
    expect(screen.getByText('请总结今天的工作')).toBeTruthy();
  });

  it('renders every image attached to one user message', () => {
    renderBubble({
      content: '比较图片',
      modality: 'image',
      visionArtifactIds: ['vision-a', 'vision-b'],
      workspaceId: 'default',
    });

    expect(screen.getByAltText('比较图片 1/2')).toBeTruthy();
    expect(screen.getByAltText('比较图片 2/2')).toBeTruthy();
  });

  it('does not replay the entrance animation for historical messages', () => {
    const { container } = renderBubble({
      content: '历史消息',
      createdAt: Date.now() - 60_000,
    });

    const bubble = container.querySelector('.userBubbleNew');
    expect(bubble).toBeTruthy();
    expect(bubble?.classList.contains('userBubbleEntrance')).toBe(false);
  });

  it('animates a message while it is being sent', () => {
    const { container } = renderBubble({
      content: '正在发送',
      createdAt: Date.now() - 60_000,
      status: 'sending',
    });

    expect(
      container.querySelector(
        '.userBubbleNew.userBubbleEntrance.userBubbleSending',
      ),
    ).toBeTruthy();
  });
});

describe('UserMessageBubble copy action (P1-4)', () => {
  beforeEach(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: jest.fn().mockResolvedValue(undefined) },
    });
  });

  it('shows the copy button only after the row is hovered', () => {
    const { container } = renderBubble({ content: '复制我' });

    expect(screen.queryByRole('button', { name: '复制' })).toBeNull();

    fireEvent.mouseEnter(container.firstElementChild as HTMLElement);
    expect(screen.getByRole('button', { name: '复制' })).toBeTruthy();
  });

  it('copies the message text and shows 1s check feedback then restores', () => {
    jest.useFakeTimers();
    try {
      const { container } = renderBubble({ content: '复制我' });
      fireEvent.mouseEnter(container.firstElementChild as HTMLElement);

      const copyButton = screen.getByRole('button', { name: '复制' });
      fireEvent.click(copyButton);

      expect(navigator.clipboard.writeText).toHaveBeenCalledWith('复制我');
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

  it('does not mount a copy button for empty content', () => {
    const { container } = renderBubble({ content: '' });
    fireEvent.mouseEnter(container.firstElementChild as HTMLElement);

    expect(screen.queryByRole('button', { name: '复制' })).toBeNull();
  });
});

describe('UserMessageBubble error state (P1-4)', () => {
  it('renders red failure text with metadata.error as title detail', () => {
    renderBubble({
      status: 'error',
      metadata: { error: '连接超时，请重试' },
    });

    const errorText = screen.getByText('发送失败');
    expect(errorText).toBeTruthy();
    expect(errorText.getAttribute('title')).toBe('连接超时，请重试');
  });

  it('falls back to a generic title when metadata has no error field', () => {
    renderBubble({ status: 'error' });

    const errorText = screen.getByText('发送失败');
    expect(errorText.getAttribute('title')).toBe('消息发送失败，请稍后重试');
  });

  it('does not render failure text while sending or after success', () => {
    const { rerender } = renderBubble({ status: 'sending' });
    expect(screen.queryByText('发送失败')).toBeNull();

    rerender(
      <UserMessageBubble
        content="你好"
        createdAt={1000}
        status="success"
        userName="我"
        formatTime={() => '10:24'}
      />,
    );
    expect(screen.queryByText('发送失败')).toBeNull();
  });
});
