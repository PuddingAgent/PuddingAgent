import {
  describeThemeToggle,
  THEME_SCOPE_HINT,
} from './index';

/**
 * 设计规格 §13.x：保留 Web 独立外观开关，就必须标明它的**作用范围**。
 * 实测依据（2026-10-02 像素复核）：浅色下 Web 为 `#f7f8fa`/`#ffffff`，而右侧工具区
 * 在深浅两张截图里都是 `#27272b`/`#121212` —— 即该开关不改 Shell 工具区。
 * 这组断言把"范围提示"钉住，避免以后被当成啰嗦文案删掉。
 */
describe('Web 主题开关的作用范围披露', () => {
  it('系统模式：说明跟随系统与当前明暗，并写明范围', () => {
    const copy = describeThemeToggle('system', true);

    expect(copy.tooltipText).toContain('跟随系统');
    expect(copy.tooltipText).toContain('暗色');
    expect(copy.tooltipText).toContain(THEME_SCOPE_HINT);
    expect(copy.tooltipText).toContain('右侧工具区');
  });

  it('手动模式：说明会切到哪一侧，并同样写明范围', () => {
    const toLight = describeThemeToggle('dark', true);
    expect(toLight.tooltipText).toContain('点击切换到亮色');
    expect(toLight.tooltipText).toContain(THEME_SCOPE_HINT);

    const toDark = describeThemeToggle('light', false);
    expect(toDark.tooltipText).toContain('点击切换到暗色');
    expect(toDark.tooltipText).toContain(THEME_SCOPE_HINT);
  });

  it('可访问名称只声称 Web 工作台主题，不暗示会改整个应用', () => {
    const copy = describeThemeToggle('dark', true);

    expect(copy.ariaLabel).toBe('切换 Web 工作台主题');
    // 不能只说"切换主题"，那会让读屏用户以为它是全局开关
    expect(copy.ariaLabel).not.toBe('切换主题');
  });

  it('两种模式都不能省略范围提示', () => {
    for (const mode of ['system', 'light', 'dark'] as const) {
      for (const isDark of [true, false]) {
        expect(describeThemeToggle(mode, isDark).tooltipText).toContain(
          THEME_SCOPE_HINT,
        );
      }
    }
  });
});
