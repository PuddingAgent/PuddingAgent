import { act, renderHook } from '@testing-library/react';
import { chunkVisibleText, useTypewriterStreaming } from './useTypewriterStreaming';

describe('chunkVisibleText', () => {
  it('splits visible text deterministically across renders', () => {
    const randomSpy = jest.spyOn(Math, 'random');
    randomSpy.mockReturnValue(0);
    const first = chunkVisibleText('商用密码应用安全性评估').map((chunk) => chunk.text);

    randomSpy.mockReturnValue(0.95);
    const second = chunkVisibleText('商用密码应用安全性评估').map((chunk) => chunk.text);

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

  it('settles to full stable markdown after streaming stops when a stable boundary already exists', () => {
    const { result, rerender } = renderHook(
      ({ text, isStreaming }) => useTypewriterStreaming({
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

    expect(result.current.stableMarkdown.length).toBeGreaterThan(0);

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
});
