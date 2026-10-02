using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// SCROLL-001 / 设计规格 §14.4：宿主外观映射的纯逻辑回归。
/// 覆盖「保存的外观选择 → 生效配色 → WebView2 预绘制背景」以及下拉下标互转，
/// 保证启动、手动切换、系统主题变化三条路径共用同一份判断。
/// </summary>
public sealed class WorkbenchAppearanceTests
{
    [Theory]
    [InlineData("Light", WorkbenchThemePreference.Light)]
    [InlineData("Dark", WorkbenchThemePreference.Dark)]
    [InlineData("System", WorkbenchThemePreference.System)]
    [InlineData("", WorkbenchThemePreference.System)]
    [InlineData(null, WorkbenchThemePreference.System)]
    [InlineData("garbage", WorkbenchThemePreference.System)]
    public void ParsePreference_maps_saved_theme_with_system_fallback(
        string? saved,
        WorkbenchThemePreference expected) =>
        Assert.Equal(expected, WorkbenchAppearance.ParsePreference(saved));

    [Theory]
    [InlineData(WorkbenchThemePreference.System, 0)]
    [InlineData(WorkbenchThemePreference.Light, 1)]
    [InlineData(WorkbenchThemePreference.Dark, 2)]
    public void Combo_index_round_trips(WorkbenchThemePreference preference, int index)
    {
        Assert.Equal(index, WorkbenchAppearance.ComboIndex(preference));
        Assert.Equal(preference, WorkbenchAppearance.PreferenceFromComboIndex(index));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_combo_index_falls_back_to_system(int index) =>
        Assert.Equal(WorkbenchThemePreference.System, WorkbenchAppearance.PreferenceFromComboIndex(index));

    [Theory]
    // 显式选择优先于系统主题
    [InlineData(WorkbenchThemePreference.Light, true, WorkbenchColorScheme.Light)]
    [InlineData(WorkbenchThemePreference.Light, false, WorkbenchColorScheme.Light)]
    [InlineData(WorkbenchThemePreference.Dark, true, WorkbenchColorScheme.Dark)]
    [InlineData(WorkbenchThemePreference.Dark, false, WorkbenchColorScheme.Dark)]
    // 跟随系统时才采用系统实际主题（ActualTheme）
    [InlineData(WorkbenchThemePreference.System, true, WorkbenchColorScheme.Dark)]
    [InlineData(WorkbenchThemePreference.System, false, WorkbenchColorScheme.Light)]
    public void Resolve_prefers_explicit_choice_then_falls_back_to_system(
        WorkbenchThemePreference preference,
        bool systemPrefersDark,
        WorkbenchColorScheme expected) =>
        Assert.Equal(expected, WorkbenchAppearance.Resolve(preference, systemPrefersDark));

    [Fact]
    public void Background_uses_the_web_layout_truth_source_colors()
    {
        // 与 src/components/ThemeMode/index.tsx 的 colorBgLayout / global.style.ts 一致（§3 中性色）
        Assert.Equal(0xFFF7F8FAu, WorkbenchAppearance.BackgroundArgb(WorkbenchColorScheme.Light));
        Assert.Equal(0xFF11151Bu, WorkbenchAppearance.BackgroundArgb(WorkbenchColorScheme.Dark));
    }

    [Theory]
    [InlineData(WorkbenchColorScheme.Light)]
    [InlineData(WorkbenchColorScheme.Dark)]
    public void Argb_parts_are_fully_opaque_and_decompose(WorkbenchColorScheme scheme)
    {
        var argb = WorkbenchAppearance.BackgroundArgb(scheme);
        var (alpha, red, green, blue) = WorkbenchAppearance.ToArgbParts(argb);
        Assert.Equal(0xFF, alpha);
        Assert.Equal(argb, (uint)((alpha << 24) | (red << 16) | (green << 8) | blue));
    }

    [Theory]
    [InlineData(WorkbenchThemePreference.Light, WorkbenchPreferredColorScheme.Light)]
    [InlineData(WorkbenchThemePreference.Dark, WorkbenchPreferredColorScheme.Dark)]
    [InlineData(WorkbenchThemePreference.System, WorkbenchPreferredColorScheme.System)]
    public void Preferred_color_scheme_keeps_system_as_auto(
        WorkbenchThemePreference preference,
        WorkbenchPreferredColorScheme expected) =>
        Assert.Equal(expected, WorkbenchAppearance.PreferredColorSchemeFor(preference));

    [Fact]
    public void Every_webview2_surface_shares_one_appearance_decision()
    {
        // 工具区里的浏览器页面与主工作台是两个 WebView2：只要有一处漏掉外观，
        // 浅色应用里就会露出一块纯黑画布（about:blank 走 UA 深色 → Chromium #121212）。
        // 这条把"两处用同一份判断"钉住：取值来源只有 Resolve + BackgroundArgb +
        // PreferredColorSchemeFor 三个纯函数。
        foreach (var preference in Enum.GetValues<WorkbenchThemePreference>())
        {
            var scheme = WorkbenchAppearance.Resolve(preference, systemPrefersDark: true);
            var argb = WorkbenchAppearance.BackgroundArgb(scheme);
            var preferred = WorkbenchAppearance.PreferredColorSchemeFor(preference);

            Assert.Contains(argb, new[] { WorkbenchAppearance.LightBackgroundArgb, WorkbenchAppearance.DarkBackgroundArgb });
            // 显式浅色时绝不能把 UA 留在深色（反之亦然）
            if (preference == WorkbenchThemePreference.Light)
            {
                Assert.Equal(WorkbenchColorScheme.Light, scheme);
                Assert.Equal(WorkbenchPreferredColorScheme.Light, preferred);
            }
            if (preference == WorkbenchThemePreference.Dark)
            {
                Assert.Equal(WorkbenchColorScheme.Dark, scheme);
                Assert.Equal(WorkbenchPreferredColorScheme.Dark, preferred);
            }
        }
    }

    /// <summary>
    /// 用户实测：浅色模式下标题栏的最小化/最大化/关闭与白色高度相似、无法分辨。
    /// 根因是 ExtendsContentIntoTitleBar 之后按钮不再跟随应用主题。
    /// 这里把"两套主题的字形必须各自可辨"钉成不变量。
    /// </summary>
    [Fact]
    public void Caption_glyphs_contrast_with_their_own_scheme()
    {
        foreach (var scheme in Enum.GetValues<WorkbenchColorScheme>())
        {
            var glyph = WorkbenchAppearance.CaptionGlyphArgb(scheme);
            Assert.Equal(0xFFu, glyph >> 24); // 必须完全不透明

            var luminance = RelativeLuminance(glyph);
            var background = RelativeLuminance(WorkbenchAppearance.BackgroundArgb(scheme));

            // 浅色方案：深字形画在浅底上；深色方案：浅字形画在深底上
            if (scheme == WorkbenchColorScheme.Light)
            {
                Assert.True(luminance < 0.2, "浅色方案的标题栏字形必须足够深");
                Assert.True(background > 0.8, "浅色方案的标题栏底应接近白色");
            }
            else
            {
                Assert.True(luminance > 0.8, "深色方案的标题栏字形必须足够浅");
                Assert.True(background < 0.05, "深色方案的标题栏底应接近黑色");
            }

            // 对比度按 WCAG 相对亮度比，至少要远高于"看不清"的 1.5:1
            var ratio = (Math.Max(luminance, background) + 0.05) / (Math.Min(luminance, background) + 0.05);
            Assert.True(ratio >= 7, $"标题栏字形对比度不足：{ratio:F1}:1");
        }
    }

    [Fact]
    public void Caption_hover_and_pressed_backgrounds_stay_translucent()
    {
        // 背景必须半透明，让 Mica/窗口底色透出来；不透明会在浅色标题栏上贴一块灰
        foreach (var scheme in Enum.GetValues<WorkbenchColorScheme>())
        {
            Assert.True((WorkbenchAppearance.CaptionHoverBackgroundArgb(scheme) >> 24) < 0xFF);
            Assert.True((WorkbenchAppearance.CaptionPressedBackgroundArgb(scheme) >> 24) < 0xFF);
            // 非激活字形弱化但仍不完全透明
            var inactiveAlpha = WorkbenchAppearance.CaptionInactiveGlyphArgb(scheme) >> 24;
            Assert.InRange(inactiveAlpha, 0x40u, 0xFEu);
        }
    }

    private static double RelativeLuminance(uint argb)
    {
        var (_, red, green, blue) = WorkbenchAppearance.ToArgbParts(argb);
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(red) + 0.7152 * Channel(green) + 0.0722 * Channel(blue);
    }
}
