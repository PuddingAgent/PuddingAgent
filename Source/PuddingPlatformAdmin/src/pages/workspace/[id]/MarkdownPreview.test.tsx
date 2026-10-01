import { render, screen } from '@testing-library/react';
import * as React from 'react';
import MarkdownPreview from './MarkdownPreview';

/**
 * Agent 配置文档的预览安全约定（设计文档第 10 节）：
 * 不启用 rehype-raw，文档里的 HTML 按普通文本处理，不执行脚本。
 */
describe('MarkdownPreview 安全与空值', () => {
  it('渲染 Markdown 结构与 GFM 表格', () => {
    render(
      <MarkdownPreview markdown={'# 角色\n\n- 审计\n\n| 列 | 值 |\n| --- | --- |\n| a | b |'} />,
    );

    expect(screen.getByRole('heading', { name: '角色' })).toBeTruthy();
    expect(screen.getByText('审计')).toBeTruthy();
    expect(screen.getByRole('table')).toBeTruthy();
  });

  it('不把文档里的 script / img onerror 当作可执行 HTML 渲染', () => {
    const { container } = render(
      <MarkdownPreview
        markdown={'正文\n\n<script>window.__pwned = true;</script>\n\n<img src=x onerror="alert(1)">'}
      />,
    );

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    // 原文仍可读，只是当普通文本展示
    expect(container.textContent).toContain('window.__pwned');
    expect(container.textContent).toContain('<img src=x');
  });

  it('链接使用新窗口并带 noopener', () => {
    render(<MarkdownPreview markdown={'[文档](https://example.com/doc)'} />);
    const link = screen.getByRole('link', { name: '文档' }) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://example.com/doc');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
  });

  it('空内容显示传入的空值文案', () => {
    render(<MarkdownPreview markdown={'   '} emptyText="未填写。" />);
    expect(screen.getByText('未填写。')).toBeTruthy();
    expect(screen.queryByTestId('prompt-document-preview')).toBeNull();
  });
});
