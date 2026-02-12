import { render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageItem from './MessageItem';

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
    }),
  };
});

jest.mock('prismjs', () => ({
  highlightElement: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  isPerfDiagnosticsEnabled: jest.fn(() => false),
  recordPerfEvent: jest.fn(),
}));

describe('MessageItem markdown code rendering', () => {
  it('does not expose markdown link titles as hover tooltips', () => {
    const markdownWithFetchedTitle =
      '[CodeWhale](https://github.com/Hmbown/CodeWhale ' +
      '"HTTP 200 OK Hmbown / **CodeWhale** Public - Notifications You must be signed in")';

    render(<MessageItem markdownText={markdownWithFetchedTitle} />);

    const link = screen.getByRole('link', { name: 'CodeWhale' });
    expect(link.getAttribute('href')).toBe(
      'https://github.com/Hmbown/CodeWhale',
    );
    expect(link.getAttribute('title')).toBeNull();
  });

  it('keeps inline code inline instead of nesting a block pre inside a paragraph', () => {
    const { container } = render(
      <MessageItem markdownText="查看 `/etc/os-release` 文件" />,
    );

    expect(container.querySelector('pre')).toBeNull();
    expect(screen.getByText('/etc/os-release').tagName.toLowerCase()).toBe(
      'code',
    );
  });

  it('normalizes standalone double-backtick fences so following markdown still renders', () => {
    const text = [
      '# 如何进行评估：完整方法论指南',
      '',
      '一、评估方法论框架',
      '',
      '``',
      '┌───────────────┐',
      '│ 评估循环 │',
      '└───────────────┘',
      '``',
      '',
      '## 二、评估的具体步骤详解',
      '',
      '### **阶段一：评估规划**',
    ].join('\n');

    const { container } = render(<MessageItem markdownText={text} />);

    expect(container.querySelector('pre')).toBeTruthy();
    expect(
      screen.getByRole('heading', { level: 2, name: '二、评估的具体步骤详解' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('heading', { level: 3, name: '阶段一：评估规划' }),
    ).toBeTruthy();
    expect(screen.queryByText('## 二、评估的具体步骤详解')).toBeNull();
  });

  it('does not read layout metrics when perf diagnostics are disabled', () => {
    const readScrollHeight = jest.fn(() => 120);
    const originalDescriptor = Object.getOwnPropertyDescriptor(
      HTMLDivElement.prototype,
      'scrollHeight',
    );
    Object.defineProperty(HTMLDivElement.prototype, 'scrollHeight', {
      configurable: true,
      get: readScrollHeight,
    });

    try {
      render(<MessageItem markdownText="streaming text" isStreaming />);

      expect(readScrollHeight).not.toHaveBeenCalled();
    } finally {
      if (originalDescriptor) {
        Object.defineProperty(
          HTMLDivElement.prototype,
          'scrollHeight',
          originalDescriptor,
        );
      } else {
        delete (HTMLDivElement.prototype as any).scrollHeight;
      }
    }
  });

  it('renders streaming live text as a single text node instead of animated chunks', () => {
    const { container } = render(
      <MessageItem
        markdownText="稳定内容正在输出"
        isStreaming
        stableMarkdown="稳定内容"
        liveText="正在输出"
        visibleLiveText="正在输出"
      />,
    );

    expect(container.textContent).toContain('稳定内容正在输出');
    expect(container.querySelectorAll('.inkChunk')).toHaveLength(0);
  });

  it('keeps the full received live tail visible when the typewriter lags behind', () => {
    const { container } = render(
      <MessageItem
        markdownText="稳定内容尾段完整文本"
        isStreaming
        stableMarkdown="稳定内容"
        liveText="尾段完整文本"
        visibleLiveText="尾段"
      />,
    );

    expect(container.textContent).toContain('稳定内容尾段完整文本');
  });

  it('renders streaming markdown with the markdown renderer while the message is still growing', () => {
    const { container } = render(
      <MessageItem
        markdownText="**第七段：会话切换**\n\n正文继续输出"
        isStreaming
        stableMarkdown="**第七段：会话切换**"
        liveText="\n\n正文继续输出"
        visibleLiveText="\n\n正文"
      />,
    );

    expect(container.textContent).toContain('第七段：会话切换');
    expect(container.querySelector('strong')).toBeTruthy();
  });
});
