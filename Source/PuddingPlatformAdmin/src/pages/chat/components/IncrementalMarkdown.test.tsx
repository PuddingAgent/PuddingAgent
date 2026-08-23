// ── IncrementalMarkdown 测试：冻结块缓存 + 尾部增量重解析 ────────────────
// 锁定契约（对齐 harness IncrementalMarkdownParser 架构）：
//  - splitMarkdownBlocks：围栏外空行切分、fence 内空行保护、空白区跳过、偏移稳定；
//  - 增量渲染：追加文本时已冻结块不重渲染（memo 生效），只有新块/尾块重解析。
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  IncrementalMarkdown,
  splitMarkdownBlocks,
} from './IncrementalMarkdown';
import MarkdownBlock from './MarkdownBlock';

// 计数每个块文本的渲染次数（验证冻结块 memo）；渲染为轻量 stub（只展示文本，
// 不跑真实 markdown 管线）。计数器挂在 mock 模块上（jest.mock 工厂不允许引用
// 外部变量）。
jest.mock('./MarkdownBlock', () => {
  const React = jest.requireActual('react');
  const renderCounts = new Map<string, number>();
  const mockFn = jest.fn((props: { markdownText: string }) =>
    React.createElement('span', null, props.markdownText),
  );
  const tracked = jest.fn((props: { markdownText: string }) => {
    const count = (renderCounts.get(props.markdownText) ?? 0) + 1;
    renderCounts.set(props.markdownText, count);
    return mockFn(props);
  });
  return {
    __esModule: true,
    default: tracked,
    __renderCounts: renderCounts,
  };
});

const renderCounts: Map<string, number> = jest.requireMock(
  './MarkdownBlock',
).__renderCounts;

const styles = {} as Record<string, string>;

describe('splitMarkdownBlocks', () => {
  it('按空行切分段落；fence 内空行不切', () => {
    const text = [
      '第一段。',
      '',
      '```ts',
      'const a = 1;',
      '',
      'const b = 2;',
      '```',
      '',
      '第三段。',
    ].join('\n');
    const slices = splitMarkdownBlocks(text);
    expect(slices.map((s) => s.text)).toEqual([
      '第一段。',
      '```ts\nconst a = 1;\n\nconst b = 2;\n```',
      '第三段。',
    ]);
  });

  it('偏移指向块首字符；全文可由切片重建', () => {
    const text = '甲。\n\n乙段落\n第二行。\n\n\n丙。';
    const slices = splitMarkdownBlocks(text);
    for (const slice of slices) {
      expect(text.slice(slice.offset, slice.offset + slice.text.length)).toBe(
        slice.text,
      );
    }
    expect(slices.map((s) => s.text)).toEqual(['甲。', '乙段落\n第二行。', '丙。']);
  });

  it('空文本/纯空白 → 空切片', () => {
    expect(splitMarkdownBlocks('')).toEqual([]);
    expect(splitMarkdownBlocks('\n\n  \n')).toEqual([]);
  });

  it('未闭合 fence 的尾部追加保持整块（流式半截代码块不碎裂）', () => {
    const first = '说明：\n\n```python\nprint(1)';
    const grown = '说明：\n\n```python\nprint(1)\nprint(2)';
    const a = splitMarkdownBlocks(first);
    const b = splitMarkdownBlocks(grown);
    expect(a).toHaveLength(2);
    expect(b).toHaveLength(2);
    // 首块（说明：）冻结不变；fence 块整体为尾块
    expect(b[0]).toEqual(a[0]);
    expect(b[1].text).toBe('```python\nprint(1)\nprint(2)');
  });
});

describe('IncrementalMarkdown（增量渲染）', () => {
  beforeEach(() => {
    renderCounts.clear();
    (MarkdownBlock as unknown as jest.Mock).mockClear();
  });

  it('追加新块时：冻结块不重渲染，只渲染新增块', () => {
    // 注意：JSX 字符串属性会折叠换行，传真实换行必须用模板字面量
    const one = '第一段。';
    const two = `${one}\n\n第二段。`;
    const { rerender } = render(
      <IncrementalMarkdown text={one} styles={styles} />,
    );
    expect(screen.getByText('第一段。')).toBeTruthy();
    expect(renderCounts.get('第一段。')).toBe(1);

    rerender(<IncrementalMarkdown text={two} styles={styles} />);
    expect(screen.getByText('第二段。')).toBeTruthy();
    // 冻结块仍只渲染过 1 次（memo 生效）
    expect(renderCounts.get('第一段。')).toBe(1);
    expect(renderCounts.get('第二段。')).toBe(1);
  });

  it('尾块增长：仅尾块重渲染，前序块冻结', () => {
    const first = '段落一。';
    const { rerender } = render(
      <IncrementalMarkdown text={`${first}\n\n段落二开头`} styles={styles} />,
    );
    expect(renderCounts.get(first)).toBe(1);

    rerender(
      <IncrementalMarkdown
        text={`${first}\n\n段落二开头继续增长`}
        styles={styles}
      />,
    );
    expect(renderCounts.get(first)).toBe(1);
    expect(renderCounts.get('段落二开头继续增长')?.valueOf).toBeTruthy();
  });
});
