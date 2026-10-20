import { splitFrontMatter } from './frontMatter';

describe('splitFrontMatter', () => {
  it('剥掉开头的 front matter，正文从标题开始', () => {
    const raw = ['---', 'title: 部署操作手册', 'status: draft', '---', '# 部署操作手册', '', '正文'].join(
      '\n',
    );

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBe('title: 部署操作手册\nstatus: draft');
    expect(result.body).toBe('# 部署操作手册\n\n正文');
  });

  it('没有 front matter 时正文逐字等于输入（含开头空行）', () => {
    const raw = '\n# 只有正文\n\n段落';

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBeUndefined();
    expect(result.body).toBe(raw);
  });

  it('只有开头没有闭合时不剥离（宁可显示异常，也不要吞掉整篇）', () => {
    // 少一个 --- 的文档很常见（手写漏了）。若按「找到 --- 就切」实现，
    // 这里会把整篇正文当成 front matter 丢掉，读者看到一张空白页。
    const raw = ['---', 'title: 忘了闭合', '# 正文标题', '', '正文内容'].join('\n');

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBeUndefined();
    expect(result.body).toBe(raw);
  });

  it('正文中间的分隔线不会被当成 front matter', () => {
    // 开头不是 ---，所以整篇都是正文；中间那条 --- 是 Markdown 水平线。
    const raw = ['# 标题', '', '上半段', '', '---', '', '下半段'].join('\n');

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBeUndefined();
    expect(result.body).toBe(raw);
  });

  it('空的 ---/--- 块不算 front matter', () => {
    const raw = ['---', '---', '# 标题'].join('\n');

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBeUndefined();
    expect(result.body).toBe(raw);
  });

  it('CRLF 换行也能识别', () => {
    const raw = ['---', 'title: a', '---', '# 标题'].join('\r\n');

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBe('title: a');
    expect(result.body).toBe('# 标题');
  });

  it('开头有 BOM 时仍能识别（否则第一行判断会失效）', () => {
    const raw = `\ufeff${['---', 'title: a', '---', '# 标题'].join('\n')}`;

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBe('title: a');
    expect(result.body).toBe('# 标题');
  });

  it('以 ... 结尾的 front matter 也接受（YAML 另一种终止符）', () => {
    const raw = ['---', 'title: a', '...', '# 标题'].join('\n');

    const result = splitFrontMatter(raw);

    expect(result.frontMatter).toBe('title: a');
    expect(result.body).toBe('# 标题');
  });
});
