import { render } from '@testing-library/react';
import React from 'react';
import MarkdownBlock from './MarkdownBlock';

/**
 * P0 安全回归：chat 正文开启了 `rehypeRaw`（为了渲染 emoji span），
 * 如果不在其后消毒，模型输出里的原始 HTML 就是可执行 HTML。
 *
 * 这两组断言要一起看：
 * - 危险向量必须**被中和**（不是"没报错"）；
 * - 合法能力必须**不被误伤**（否则修安全会顺手修坏功能）。
 *
 * 对应外部案例：LightRAG GHSA-xpjq-3w4w-w5wr、CopilotKit #5629。
 */
const styles = new Proxy<Record<string, string>>(
  {},
  {
    get: (_target, property) => String(property),
  },
);

const renderMarkdown = (markdownText: string) =>
  render(
    <MarkdownBlock markdownText={markdownText} styles={styles} workspaceId="default" />,
  );

describe('MarkdownBlock 消毒：危险向量被中和', () => {
  it('移除 <script>', () => {
    renderMarkdown('<script>alert(1)</script>\n\n正文');

    expect(document.querySelector('script')).toBeNull();
    expect(document.body.textContent).toContain('正文');
  });

  it('移除 <iframe srcdoc>', () => {
    renderMarkdown('<iframe srcdoc="<script>alert(1)</script>"></iframe>');

    expect(document.querySelector('iframe')).toBeNull();
  });

  it('移除 <style>', () => {
    renderMarkdown('<style>body{display:none}</style>');

    expect(document.querySelector('style')).toBeNull();
  });

  it('移除 <svg> 内嵌 <script>', () => {
    renderMarkdown('<svg><script>alert(1)</script></svg>');

    expect(document.querySelector('svg script')).toBeNull();
  });

  it('移除 on* 事件属性', () => {
    renderMarkdown('<img src="x" onerror="alert(1)">');

    const img = document.querySelector('img');
    // 图片本身可以留下（Markdown 也允许），但事件处理器必须没了。
    expect(img?.getAttribute('onerror')).toBeNull();
  });

  it('移除 style 属性（防覆盖层钓鱼）', () => {
    renderMarkdown('<div style="position:fixed;inset:0">覆盖</div>');

    const div = Array.from(document.querySelectorAll('div')).find(
      (node) => node.textContent === '覆盖',
    );
    expect(div?.getAttribute('style')).toBeNull();
  });

  it('拦掉 javascript: 链接', () => {
    renderMarkdown('[点我](javascript:alert(1))');

    const anchor = document.querySelector('a');
    const href = anchor?.getAttribute('href') ?? '';
    expect(href.toLowerCase().startsWith('javascript:')).toBe(false);
  });
});

describe('MarkdownBlock 消毒：合法能力不被误伤', () => {
  it('保留 emoji 收敛标记 data-md-emoji', () => {
    renderMarkdown('早上好 😀');

    expect(document.querySelector('[data-md-emoji]')).not.toBeNull();
  });

  it('保留围栏代码块的 language-* class（富媒体分派的入口）', () => {
    renderMarkdown(['```mermaid', 'graph TD; A-->B', '```'].join('\n'));

    const code = document.querySelector('code');
    expect(code?.className ?? '').toContain('language-mermaid');
  });

  it('保留 mark / u 行内标签', () => {
    renderMarkdown('<mark>重点</mark> 与 <u>下划线</u>');

    expect(document.querySelector('mark')?.textContent).toBe('重点');
    expect(document.querySelector('u')?.textContent).toBe('下划线');
  });

  it('保留 GFM 表格与其对齐属性', () => {
    renderMarkdown(
      ['| 项 | 结果 |', '| :--- | ---: |', '| 心跳 | 1800 秒 |'].join('\n'),
    );

    expect(document.querySelector('table')).not.toBeNull();
    expect(document.querySelectorAll('td').length).toBe(2);
  });

  it('保留 KaTeX 公式渲染', () => {
    renderMarkdown('$E = mc^2$');

    // KaTeX 会输出 .katex 容器；若消毒把它剥掉，说明顺序放错了。
    expect(document.querySelector('.katex')).not.toBeNull();
  });
});
