// ── MessageItem 流式 markdown 定向单测：BUG1（原文与渲染并存）──────────────
//  1. 完整 markdown 表格必须渲染为 <table>，不允许降级为 <p> 原始管道文本
//     （回归 preprocessMarkdown 管道行收集 hack 与半截表头提交缺陷）。
//  2. 流式尾段携带块级语法（表格）时走 markdown 渲染，不再以纯文本 span
//     显示原始管道字符；纯文本尾段保留打字机 span。
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageItem from './MessageItem';

jest.mock('../styles/messageStyleContext', () => ({
  useChatMessageStyles: () => ({
    styles: new Proxy(
      {},
      {
        get: (_target: unknown, prop: string | symbol) => String(prop),
      },
    ),
    cx: (...names: Array<string | false | undefined>) =>
      names.filter(Boolean).join(' '),
  }),
}));

const TABLE_MARKDOWN =
  '| ID | 名称 | 状态 |\n| --- | --- | --- |\n| CU-01 | 审计 | Done |\n| CU-11 | 视觉密度 | Backlog |';

describe('MessageItem 流式 markdown 渲染', () => {
  it('完整表格渲染为 table 元素，不出现原始管道段落', () => {
    const { container } = render(
      <MessageItem markdownText={TABLE_MARKDOWN} isStreaming={false} />,
    );

    expect(container.querySelector('table')).toBeTruthy();
    expect(container.querySelectorAll('th').length).toBe(3);
    expect(container.querySelectorAll('tbody tr').length).toBe(2);
    // 原文不得作为段落文本出现
    const paragraphText = Array.from(
      container.querySelectorAll('p'),
    )
      .map((node) => node.textContent)
      .join('');
    expect(paragraphText).not.toContain('| ID |');
  });

  it('流式尾段含表格行时经 markdown 渲染，不显示原始管道字符', () => {
    const { container } = render(
      <MessageItem
        markdownText={`已完成段落\n\n${TABLE_MARKDOWN}`}
        isStreaming
        stableMarkdown="已完成段落\n\n"
        liveText={TABLE_MARKDOWN}
        visibleLiveText={TABLE_MARKDOWN}
      />,
    );

    expect(container.querySelectorAll('table').length).toBe(1);
    const paragraphText = Array.from(
      container.querySelectorAll('p'),
    )
      .map((node) => node.textContent)
      .join('');
    expect(paragraphText).not.toContain('| ID |');
  });

  it('纯文本尾段保留打字机文本（零解析路径）', () => {
    render(
      <MessageItem
        markdownText="稳定段落\n\n正在逐字输出的中文尾段"
        isStreaming
        stableMarkdown="稳定段落\n\n"
        liveText="正在逐字输出的中文尾段"
        visibleLiveText="正在逐字输出的中文尾段"
      />,
    );

    expect(screen.getByText('正在逐字输出的中文尾段')).toBeTruthy();
  });
});
