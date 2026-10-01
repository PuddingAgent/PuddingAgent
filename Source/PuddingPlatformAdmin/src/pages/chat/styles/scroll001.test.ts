import '../../../global.style';
import { thinScrollbarStyle } from './scrollTokens';

/**
 * SCROLL-001 回归守卫（设计规格 §14.2/§14.3）。
 * 断言的是不变量而非像素值：主题真源必须声明 color-scheme；滚动条皮肤必须用
 * 透明轨道（渲染结果 = 所属容器背景）+ 主题 token + 系统高对比回退；
 * 全局不得出现非 auto 的 scrollbar-*，否则会反过来停用 Chromium 伪元素
 * （两种策略互相覆盖，正是 §14.3 第 4 步禁止的做法）。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('SCROLL-001 主题滚动条', () => {
  it('主题真源分别声明浅色与深色 color-scheme', () => {
    const css = injectedCssText();
    expect(css).toContain('color-scheme:light');
    expect(css).toContain('color-scheme:dark');
    // 深色分支必须挂在 ThemeMode 真实写入的选择器上
    expect(css).toContain("[data-pudding-theme='dark']");
  });

  it('浅色与深色各自提供 thumb idle/hover/active 三态 token', () => {
    const css = injectedCssText();
    for (const declaration of [
      '--pudding-scroll-thumb:#738197',
      '--pudding-scroll-thumb-hover:#526174',
      '--pudding-scroll-thumb-active:#2458d3',
      '--pudding-scroll-thumb:#65758c',
      '--pudding-scroll-thumb-hover:#8b9db5',
      '--pudding-scroll-thumb-active:#91b3ff',
    ]) {
      expect(css).toContain(declaration);
    }
  });

  it('轨道透明、thumb 圆角细条、不绘制箭头、不隐藏滚动能力', () => {
    const css = injectedCssText();
    expect(css).toContain('::-webkit-scrollbar-thumb');
    expect(css).toContain('background-clip:content-box');
    expect(css).toContain('border-radius:999px');
    expect(css).toContain('::-webkit-scrollbar-button');
    // 轨道走 transparent，深色下才不会出现高亮白条（IMG02）
    expect(css).toMatch(/::-webkit-scrollbar-track[^{]*\{[^}]*background:transparent/);
  });

  it('hover/active 只换色，不改变滚动条宽度（gutter 不跳动）', () => {
    const css = injectedCssText();
    const hoverRule = css.match(
      /::-webkit-scrollbar-thumb:hover\s*\{([^}]*)\}/,
    )?.[1];
    expect(hoverRule).toBeTruthy();
    expect(hoverRule).toContain('background-color');
    expect(hoverRule).not.toContain('width');
  });

  it('系统高对比下放弃自定义皮肤，恢复系统绘制', () => {
    const css = injectedCssText();
    expect(css).toContain('forced-colors: active');
    expect(css).toContain('revert');
  });

  it('非 Chromium 回退放在 @supports 内，不与伪元素皮肤互相覆盖', () => {
    const css = injectedCssText();
    expect(css).toContain('@supports not selector(::-webkit-scrollbar)');
  });
});

describe('thinScrollbarStyle（块级容器细条）', () => {
  it('保留 thin 与伪元素形状，颜色改走 SCROLL-001 token', () => {
    expect(thinScrollbarStyle.scrollbarWidth).toBe('thin');
    expect(thinScrollbarStyle.scrollbarColor).toBe(
      'var(--pudding-scroll-thumb) transparent',
    );
    expect(thinScrollbarStyle['&::-webkit-scrollbar']).toEqual({
      width: 6,
      height: 6,
    });
    expect(thinScrollbarStyle['&::-webkit-scrollbar-thumb'].background).toBe(
      'var(--pudding-scroll-thumb)',
    );
    expect(
      thinScrollbarStyle['&::-webkit-scrollbar-thumb:hover'].background,
    ).toBe('var(--pudding-scroll-thumb-hover)');
  });
});
