import { act, renderHook } from '@testing-library/react';
import {
  chunkVisibleText,
  useTypewriterStreaming,
} from './useTypewriterStreaming';

describe('chunkVisibleText', () => {
  it('splits visible text deterministically across renders', () => {
    const randomSpy = jest.spyOn(Math, 'random');
    randomSpy.mockReturnValue(0);
    const first = chunkVisibleText('商用密码应用安全性评估').map(
      (chunk) => chunk.text,
    );

    randomSpy.mockReturnValue(0.95);
    const second = chunkVisibleText('商用密码应用安全性评估').map(
      (chunk) => chunk.text,
    );

    expect(second).toEqual(first);
    randomSpy.mockRestore();
  });
});

describe('useTypewriterStreaming', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('does not commit stable markdown ahead of the visible typewriter position', () => {
    const { result, rerender } = renderHook(
      ({ text }) =>
        useTypewriterStreaming({
          text,
          isStreaming: true,
          tickMs: 10,
          maxLagChars: 1000,
        }),
      {
        initialProps: {
          text: '第一段\n\n尾段正在生成',
        },
      },
    );

    expect(result.current.stableMarkdown).toBe('');

    rerender({
      text: '第一段\n\n尾段正在生成更多内容',
    });

    expect(result.current.stableMarkdown).toBe('');

    act(() => {
      jest.advanceTimersByTime(80);
    });

    expect(result.current.stableMarkdown).toBe('第一段\n');
  });

  it('settles to full stable markdown after streaming stops when a stable boundary already exists', () => {
    const { result, rerender } = renderHook(
      ({ text, isStreaming }) =>
        useTypewriterStreaming({
          text,
          isStreaming,
          tickMs: 10,
        }),
      {
        initialProps: {
          text: '第一段\n\n尾段正在生成',
          isStreaming: true,
        },
      },
    );

    rerender({
      text: '第一段\n\n尾段正在生成',
      isStreaming: false,
    });

    act(() => {
      jest.advanceTimersByTime(1000);
    });

    expect(result.current.stableMarkdown).toBe('第一段\n\n尾段正在生成');
    expect(result.current.liveText).toBe('');
    expect(result.current.visibleLiveText).toBe('');
    expect(result.current.isSettling).toBe(false);
  });

  it('renders already completed history as full stable markdown immediately', () => {
    const { result } = renderHook(() =>
      useTypewriterStreaming({
        text: '已经完成的历史回答',
        isStreaming: false,
        tickMs: 10,
      }),
    );

    expect(result.current.stableMarkdown).toBe('已经完成的历史回答');
    expect(result.current.liveText).toBe('');
    expect(result.current.visibleLiveText).toBe('');
    expect(result.current.isSettling).toBe(false);
  });
});
