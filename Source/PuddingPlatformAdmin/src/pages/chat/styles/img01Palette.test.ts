import '../../../global.style';

/**
 * IMG01 回归守卫（设计规格 §3 / §13.2）。
 * 断言主题真源已收敛为「中性层级 + 单一蓝色强调」，且历史暖米/紫色不再出现在
 * 注入的全局样式里 —— 后者正是截图里「像几个独立产品」的直接来源。
 * 同时守住「被引用但未定义的短名 token」这一类缺陷：缺定义时 var() 的回退
 * 暖色字面量会真实渲染（本次据此修掉 26 处）。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('IMG01 §3 中性色板与单一蓝色强调', () => {
  it('浅色主题取 §3 的 bg/surface/text/border/accent', () => {
    const css = injectedCssText();
    expect(css).toContain('--pudding-chat-bg:#f7f8fa');
    expect(css).toMatch(/--pudding-chat-surface:#(fff|ffffff)\b/);
    expect(css).toContain('--pudding-chat-surface-muted:#eef1f5');
    expect(css).toContain('--pudding-chat-text:#182230');
    expect(css).toContain('--pudding-chat-text-muted:#526174');
    expect(css).toContain('--pudding-chat-border:#d8dee8');
    expect(css).toContain('--pudding-chat-accent:#2458d3');
    expect(css).toContain('--pudding-chat-accent-soft:#eaf0ff');
  });

  it('深色主题取 §3 的对应档位', () => {
    const css = injectedCssText();
    expect(css).toContain('--pudding-chat-bg:#11151b');
    expect(css).toContain('--pudding-chat-surface:#1a2029');
    expect(css).toContain('--pudding-chat-surface-muted:#242c37');
    expect(css).toContain('--pudding-chat-text:#e8edf4');
    expect(css).toContain('--pudding-chat-text-muted:#a8b5c7');
    expect(css).toContain('--pudding-chat-border:#445166');
    expect(css).toContain('--pudding-chat-accent:#91b3ff');
    expect(css).toContain('--pudding-chat-accent-soft:#243657');
  });

  it('状态色与文本四档取 §3 值（IMG04）', () => {
    const css = injectedCssText();
    // 浅色
    expect(css).toContain('--pudding-status-success:#157347');
    expect(css).toContain('--pudding-status-warning:#8a5700');
    expect(css).toContain('--pudding-status-error:#b42318');
    expect(css).toContain('--pudding-chat-text-caption:#6e7a88');
    // 深色
    expect(css).toContain('--pudding-status-success:#75d6a4');
    expect(css).toContain('--pudding-status-warning:#f0c36a');
    expect(css).toContain('--pudding-status-error:#ff9e99');
    expect(css).toContain('--pudding-chat-text-caption:#8b98a9');
  });

  it('暖米色与紫色强调不再出现在注入的全局样式里', () => {
    const css = injectedCssText();
    for (const legacy of [
      '#7c3aed', // 旧强调紫
      '#a78bfa', // 旧强调紫（深色）
      'rgba(124,58,237', // 旧紫色玻璃/边框
      'rgba(167,139,250', // 旧紫色光晕
      '#f5f0e8', // 旧暖米色（bg）
      '#fafaf7', // 旧暖白（surface）
      '#11100d', // 旧暖黑（深色 bg）
      '#5c4a3a', // 旧棕灰（text-muted）
      '#1a1a2e', // 旧正文色
    ]) {
      expect(css).not.toContain(legacy);
    }
  });

  it('历史短名 token 均有定义，暖色回退不再实际渲染', () => {
    const css = injectedCssText();
    expect(css).toContain('--pudding-text:var(--pudding-chat-text)');
    expect(css).toContain('--pudding-text-muted:var(--pudding-chat-text-muted)');
    expect(css).toContain('--pudding-accent:var(--pudding-chat-accent)');
    expect(css).toContain('--pudding-danger:var(--pudding-chat-danger)');
    expect(css).toContain(
      '--pudding-surface-soft:var(--pudding-chat-surface-muted)',
    );
    expect(css).toContain('--pudding-chat-panel-bg:var(--pudding-chat-surface)');
  });
});
