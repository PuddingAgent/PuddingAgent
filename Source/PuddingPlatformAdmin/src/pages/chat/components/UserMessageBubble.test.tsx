import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import UserMessageBubble, { singleImageFit } from './UserMessageBubble';

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

describe('UserMessageBubble vision images (P1-5)', () => {
  it('renders a single image inside a 240px long-edge frame with cover top-left', () => {
    const { container } = renderBubble({
      content: '看图',
      modality: 'image',
      visionArtifactId: 'vision-single',
      workspaceId: 'default',
    });

    // 加载完成前展示 shimmer 占位
    expect(screen.getByTestId('user-vision-loading-0')).toBeTruthy();

    const img = screen.getByAltText('看图 1/1') as HTMLImageElement;
    Object.defineProperty(img, 'naturalWidth', {
      configurable: true,
      value: 800,
    });
    Object.defineProperty(img, 'naturalHeight', {
      configurable: true,
      value: 600,
    });
    fireEvent.load(img);

    // 800×600 → 长边 240，等比 240×180
    const frame = container.querySelector(
      '.userVisionImageSingle',
    ) as HTMLElement;
    expect(frame).toBeTruthy();
    expect(frame.style.width).toBe('240px');
    expect(frame.style.height).toBe('180px');
    expect(img.classList.contains('userVisionImageSingleImg')).toBe(true);
    // 加载完成后 shimmer 移除
    expect(screen.queryByTestId('user-vision-loading-0')).toBeNull();
  });

  it('clamps single-image aspect ratio to [0.25, 4] and never upscales', () => {
    // 超宽：2000×200 → 4:1 裁切盒 240×60
    expect(singleImageFit(2000, 200)).toEqual({ width: 240, height: 60 });
    // 超高：200×2000 → 1:4 裁切盒 60×240
    expect(singleImageFit(200, 2000)).toEqual({ width: 60, height: 240 });
    // 常规比例按长边 240 等比缩放
    expect(singleImageFit(1600, 1200)).toEqual({ width: 240, height: 180 });
    // 小图不放大：100×50 保持自然尺寸
    expect(singleImageFit(100, 50)).toEqual({ width: 100, height: 50 });
  });

  it('renders multi-image attachments as 64px tiles', () => {
    renderBubble({
      content: '比较图片',
      modality: 'image',
      visionArtifactIds: ['vision-a', 'vision-b', 'vision-c'],
      workspaceId: 'default',
    });

    expect(screen.getByTestId('user-vision-tile-0')).toBeTruthy();
    expect(screen.getByTestId('user-vision-tile-1')).toBeTruthy();
    expect(screen.getByTestId('user-vision-tile-2')).toBeTruthy();
    expect(screen.getAllByAltText(/比较图片 \d\/3/)).toHaveLength(3);
  });

  it('shows a retry control on load failure and reloads with cache-bust on click', () => {
    renderBubble({
      content: '看图',
      modality: 'image',
      visionArtifactId: 'vision-fail',
      workspaceId: 'default',
    });

    const img = screen.getByAltText('看图 1/1') as HTMLImageElement;
    fireEvent.error(img);
    expect(screen.getByTestId('user-vision-retry-0')).toBeTruthy();

    fireEvent.click(screen.getByTestId('user-vision-retry-0'));
    const reloaded = screen.getByAltText('看图 1/1') as HTMLImageElement;
    expect(reloaded.getAttribute('src')).toContain('retry=1');
    expect(reloaded.getAttribute('src')).toContain('vision-fail');
    // 重试后回到加载态（shimmer 占位重新出现）
    expect(screen.getByTestId('user-vision-loading-0')).toBeTruthy();
  });

  it('keeps a failed tile placeholder the same 64px box (does not expand layout)', () => {
    const { container } = renderBubble({
      content: '多图失败',
      modality: 'image',
      visionArtifactIds: ['vision-a', 'vision-b'],
      workspaceId: 'default',
    });

    const img0 = screen.getByAltText('多图失败 1/2') as HTMLImageElement;
    const img1 = screen.getByAltText('多图失败 2/2') as HTMLImageElement;
    fireEvent.error(img0);
    fireEvent.error(img1);

    // 失败后仍是 tile 容器（64px 方块），未退化为小图标/文字行
    const tile = container.querySelector('.userVisionTile') as HTMLElement;
    expect(tile).toBeTruthy();
    expect(screen.getByTestId('user-vision-retry-0')).toBeTruthy();
    expect(screen.getByTestId('user-vision-retry-1')).toBeTruthy();
  });

  it('keeps a failed single placeholder at the 240px box size', () => {
    const { container } = renderBubble({
      content: '单图失败',
      modality: 'image',
      visionArtifactId: 'vision-single-fail',
      workspaceId: 'default',
    });

    const img = screen.getByAltText('单图失败 1/1') as HTMLImageElement;
    fireEvent.error(img);

    const frame = container.querySelector(
      '.userVisionImageSingle',
    ) as HTMLElement;
    expect(frame).toBeTruthy();
    // 尺寸类断言：保持单图 240px 占位容器（CSS 默认 240×240，未注入内联尺寸）
    expect(frame.style.width).toBe('');
    expect(screen.getByTestId('user-vision-retry-0')).toBeTruthy();
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
