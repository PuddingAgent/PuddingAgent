import {
  isRichBlockLanguage,
  resolveRichBlock,
  RICH_BLOCK_LANGUAGES,
} from './RichBlockRegistry';

/**
 * 富媒体围栏注册表：只测纯函数部分。
 *
 * 不在这一层测"渲染成功"——`lazy(() => import(...))` 在 jest 的 CommonJS
 * 运行时里无法执行动态 import，会一直 pending 或抛错。渲染行为由
 * MermaidBlock.test.tsx 用注入的 loader 覆盖。
 */
describe('RichBlockRegistry', () => {
  it('mermaid 已注册', () => {
    expect(RICH_BLOCK_LANGUAGES).toContain('mermaid');
    expect(isRichBlockLanguage('mermaid')).toBe(true);
    expect(resolveRichBlock('mermaid')).toBeDefined();
  });

  it('语言名大小写不敏感、允许首尾空白', () => {
    expect(isRichBlockLanguage('Mermaid')).toBe(true);
    expect(isRichBlockLanguage('  MERMAID  ')).toBe(true);
    expect(resolveRichBlock('MERMAID')).toBe(resolveRichBlock('mermaid'));
  });

  /**
   * 每个已注册围栏都要在这里占一行。
   *
   * 这条断言的真正作用是**锁住接线**：渲染器自己的单测（如 PanelBlock.test）
   * 是直接 import 组件的，即使注册表里漏了 `panel` 也照样全绿，
   * 但生产里围栏会静默退化成普通代码块 —— 只有这里能抓到。
   */
  it('chart 与 panel 已注册（防注册表漏项）', () => {
    expect(RICH_BLOCK_LANGUAGES).toContain('chart');
    expect(RICH_BLOCK_LANGUAGES).toContain('panel');
    expect(resolveRichBlock('chart')).toBeDefined();
    expect(resolveRichBlock('panel')).toBeDefined();
    // 模型常把语言名写成首字母大写
    expect(isRichBlockLanguage('Panel')).toBe(true);
  });

  it('未注册的语言返回 undefined（调用方据此回退为普通代码块）', () => {
    expect(isRichBlockLanguage('ts')).toBe(false);
    expect(resolveRichBlock('python')).toBeUndefined();
    expect(resolveRichBlock('code')).toBeUndefined();
  });

  it('空值不抛异常', () => {
    expect(resolveRichBlock(undefined)).toBeUndefined();
    expect(resolveRichBlock(null)).toBeUndefined();
    expect(resolveRichBlock('')).toBeUndefined();
    expect(resolveRichBlock('   ')).toBeUndefined();
  });
});
