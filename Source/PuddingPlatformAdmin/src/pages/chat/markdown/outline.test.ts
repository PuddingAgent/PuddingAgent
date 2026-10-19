import { extractOutline } from './outline';

/**
 * 大纲提取：跳过围栏代码块是最容易漏的一条——
 * 配置/脚本里的 `# 注释` 会被误当成 H1。
 */
describe('extractOutline', () => {
  it('提取 H1–H3 并保留顺序与层级', () => {
    const md = ['# 标题一', '正文', '## 小节', '### 细目', '#### 太深'].join('\n');
    const items = extractOutline(md);

    expect(items.map((item) => item.level)).toEqual([1, 2, 3]);
    expect(items[0].text).toBe('标题一');
    expect(items[2].text).toBe('细目');
  });

  it('跳过围栏代码块里的 # 注释', () => {
    const md = ['```bash', '# 这不是标题', 'echo hi', '```', '## 真标题'].join('\n');
    const items = extractOutline(md);

    expect(items).toHaveLength(1);
    expect(items[0].text).toBe('真标题');
  });

  it('去掉标题末尾的 # 收尾标记', () => {
    expect(extractOutline('## 小节 ##')[0].text).toBe('小节');
  });

  it('记录源行号，便于排障', () => {
    const items = extractOutline(['正文', '', '## 第二节'].join('\n'));
    expect(items[0].line).toBe(2);
  });

  it('maxLevel 可限制深度', () => {
    const md = ['# a', '## b', '### c'].join('\n');
    expect(extractOutline(md, 1)).toHaveLength(1);
    expect(extractOutline(md, 2)).toHaveLength(2);
  });

  it('空文档与纯文本返回空数组', () => {
    expect(extractOutline('')).toHaveLength(0);
    expect(extractOutline('没有标题的一段话')).toHaveLength(0);
  });
});
