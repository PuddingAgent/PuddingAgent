import { render } from '@testing-library/react';
import React from 'react';
import MarkdownBlock from './MarkdownBlock';

/**
 * IMG07（设计规格 §13.5）：`chatLabelTable`（首列最小宽 + 不换行）**只能**套在
 * 「首列是短标签」的规格/状态表上，普通数据表必须保持默认（允许换行 + 容器横滚）。
 *
 * 起因：上一批给所有表格首列加了全局 `min-width: 7em`——既违反 §13.5 的限定，
 * 实测在 Chromium 自动表格布局下也没生效（真实会话里该列约 4em）。本批改为
 * 按表头词判定后套类，这里就是把"判定范围"钉住。
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

const tableClass = () =>
  document.querySelector('table')?.getAttribute('class') ?? '';

describe('IMG07 标签表判定', () => {
  it('表头为短标签（项）的规格表套 chatLabelTable', () => {
    renderMarkdown(
      ['| 项 | 结果 |', '| --- | --- |', '| 心跳间隔 | min = max = 86400 秒 |'].join(
        '\n',
      ),
    );

    expect(tableClass()).toBe('chatLabelTable');
  });

  it('数据表（首列是数据，不是标签）不套类，保持默认可换行', () => {
    renderMarkdown(
      ['| 提供商 | 输入 Tokens |', '| --- | --- |', '| deepseek | 1,234 |'].join(
        '\n',
      ),
    );

    expect(tableClass()).toBe('');
  });

  it('表头是普通业务词时不套类', () => {
    renderMarkdown(
      ['| 文件名 | 大小 |', '| --- | --- |', '| a.ts | 1KB |'].join('\n'),
    );

    expect(tableClass()).toBe('');
  });

  it('没有表格时不渲染 table', () => {
    renderMarkdown('普通段落');

    expect(document.querySelector('table')).toBeNull();
  });
});
