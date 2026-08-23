import { act, renderHook } from '@testing-library/react';
import {
  chunkVisibleText,
  findStableMarkdownBoundary,
  useTypewriterStreaming,
} from './useTypewriterStreaming';

describe('findStableMarkdownBoundary 表格感知', () => {
  it('完整表格（表头+分隔行+数据行）在表格结束后提交', () => {
    const text =
      '前文\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\n后续段落';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe(
      '前文\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n',
    );
  });

  it('表格行不带尾管道时同样识别（LLM 常见输出）', () => {
    const text = '| a | b\n| --- | ---\n| 1 | 2\n\n结论';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe(
      '| a | b\n| --- | ---\n| 1 | 2\n',
    );
  });

  it('半截表头（分隔行未到达）不提交，避免表头降级为段落原文', () => {
    const text = '前文\n\n| a | b |\n| 1';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe('前文\n');
  });

  it('半截表头后跟空行也不提交', () => {
    const text = '前文\n\n| a | b |\n\n后续段落';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe('前文\n');
  });

  it('表格流式增长过程中（run 未结束）不提交中间行', () => {
    const text =
      '前文\n\n| a | b |\n| --- | --- |\n| 1';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe('前文\n');
  });

  it('未闭合代码块仍回退到围栏之前的安全边界', () => {
    const text = '段落一\n\n```js\nconst x = 1;';
    const boundary = findStableMarkdownBoundary(text);
    expect(text.slice(0, boundary)).toBe('段落一\n');
  });
});

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
