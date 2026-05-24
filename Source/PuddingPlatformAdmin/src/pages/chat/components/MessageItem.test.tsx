import { render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageItem from './MessageItem';

jest.mock('../styles', () => {
  const styles = new Proxy({}, {
    get: (_target, prop) => String(prop),
  });
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

jest.mock('prismjs', () => ({
  highlightElement: jest.fn(),
}));

describe('MessageItem markdown code rendering', () => {
  it('keeps inline code inline instead of nesting a block pre inside a paragraph', () => {
    const { container } = render(<MessageItem markdownText="查看 `/etc/os-release` 文件" />);

    expect(container.querySelector('pre')).toBeNull();
    expect(screen.getByText('/etc/os-release').tagName.toLowerCase()).toBe('code');
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
    expect(screen.getByRole('heading', { level: 2, name: '二、评估的具体步骤详解' })).toBeTruthy();
    expect(screen.getByRole('heading', { level: 3, name: '阶段一：评估规划' })).toBeTruthy();
    expect(screen.queryByText('## 二、评估的具体步骤详解')).toBeNull();
  });
});
