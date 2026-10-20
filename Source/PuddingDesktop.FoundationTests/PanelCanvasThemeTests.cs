namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 面板画布主题契约（<see cref="PanelCanvasTheme"/>）的测试。
///
/// 为什么这层要测：主题变量是 Agent 内容的**唯一**取色来源。变量名拼错、某个槽位
/// 漏改、深浅两套写到同一个值 —— 这些都不会抛异常，只会让画布"看起来有点怪"，
/// 或者让某段文字在深色底上彻底消失。所以这里断言三件事：
/// ① 两套配色每个槽位都真的不同（防复制粘贴漏改）；
/// ② 注入块包含全部变量名（防拼错/漏项）；
/// ③ 正文类文字在自己那套底色上的对比度 ≥ 4.5:1（防"淡灰提示文字"）。
/// </summary>
public sealed class PanelCanvasThemeTests
{
    [Fact]
    public void Light_and_dark_differ_on_every_slot()
    {
        var light = PanelCanvasTheme.Light;
        var dark = PanelCanvasTheme.Dark;

        // 逐个槽位比较，不用 record 相等：相等可以因为"整体换了"而通过，
        // 这里要的是"每个槽位都改过"，才能挡住复制粘贴漏改一行。
        Assert.NotEqual(light.Page, dark.Page);
        Assert.NotEqual(light.Surface, dark.Surface);
        Assert.NotEqual(light.SurfaceStrong, dark.SurfaceStrong);
        Assert.NotEqual(light.Text, dark.Text);
        Assert.NotEqual(light.TextSecondary, dark.TextSecondary);
        Assert.NotEqual(light.TextTertiary, dark.TextTertiary);
        Assert.NotEqual(light.Border, dark.Border);
        Assert.NotEqual(light.Accent, dark.Accent);
    }

    [Fact]
    public void For_maps_each_scheme_to_its_own_palette()
    {
        Assert.Equal(PanelCanvasTheme.Light, PanelCanvasTheme.For(WorkbenchColorScheme.Light));
        Assert.Equal(PanelCanvasTheme.Dark, PanelCanvasTheme.For(WorkbenchColorScheme.Dark));
    }

    [Fact]
    public void PageArgb_matches_the_palette_page()
    {
        // 两处各写一份底色会漂移：首帧用 ARGB，文档内用十六进制字符串。
        // 这条把它们钉在一起，任一处单独改动都会红。
        Assert.Equal(PanelCanvasTheme.Light.Page, ToHex(PanelCanvasTheme.PageArgb(WorkbenchColorScheme.Light)));
        Assert.Equal(PanelCanvasTheme.Dark.Page, ToHex(PanelCanvasTheme.PageArgb(WorkbenchColorScheme.Dark)));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Declare_declares_color_scheme(string expected)
    {
        var scheme = expected == "dark" ? WorkbenchColorScheme.Dark : WorkbenchColorScheme.Light;

        Assert.Contains("color-scheme:" + expected, PanelCanvasTheme.Declare(scheme), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.Page), "--pudding-page")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.Surface), "--pudding-surface")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.SurfaceStrong), "--pudding-surface-strong")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.Text), "--pudding-text")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.TextSecondary), "--pudding-text-secondary")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.TextTertiary), "--pudding-text-tertiary")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.Border), "--pudding-border")]
    [InlineData(nameof(PanelCanvasTheme.PanelPalette.Accent), "--pudding-accent")]
    public void Declare_emits_every_documented_variable(string slot, string cssName)
    {
        // 用例名里的 slot 只用于读报告时定位，真正的断言是 cssName 必须出现。
        Assert.False(string.IsNullOrWhiteSpace(slot));
        Assert.Contains(cssName + ":", PanelCanvasTheme.Declare(WorkbenchColorScheme.Light), StringComparison.Ordinal);
        Assert.Contains(cssName + ":", PanelCanvasTheme.Declare(WorkbenchColorScheme.Dark), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkbenchColorScheme.Light)]
    [InlineData(WorkbenchColorScheme.Dark)]
    public void Declare_is_a_single_well_formed_root_block(WorkbenchColorScheme scheme)
    {
        var css = PanelCanvasTheme.Declare(scheme);

        Assert.StartsWith(":root{", css, StringComparison.Ordinal);
        Assert.EndsWith("}", css, StringComparison.Ordinal);
        Assert.Equal(1, css.Count(c => c == '{'));
        Assert.Equal(1, css.Count(c => c == '}'));
        Assert.DoesNotContain(";;", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkbenchColorScheme.Light)]
    [InlineData(WorkbenchColorScheme.Dark)]
    public void Body_text_meets_the_contrast_floor_on_its_own_page(WorkbenchColorScheme scheme)
    {
        // 规范要求正文与标签 ≥ 4.5:1。低对比度的"淡灰提示文字"在投影和高分屏上
        // 会直接消失，而在代码里它只是另一个十六进制值 —— 必须由测试守住。
        var palette = PanelCanvasTheme.For(scheme);
        var page = RelativeLuminance(palette.Page);

        Assert.True(Contrast(palette.Text, page) >= 4.5,
            $"{scheme} 正文对比度不足：{palette.Text} on {palette.Page}");
        Assert.True(Contrast(palette.TextSecondary, page) >= 4.5,
            $"{scheme} 次级文字对比度不足：{palette.TextSecondary} on {palette.Page}");
        Assert.True(Contrast(palette.TextTertiary, page) >= 4.5,
            $"{scheme} 注释文字对比度不足：{palette.TextTertiary} on {palette.Page}");
    }

    [Fact]
    public void Accent_stays_distinguishable_from_body_text()
    {
        // 高亮色与正文色太接近时，"选中"这个状态就没有视觉重点了。
        foreach (var palette in new[] { PanelCanvasTheme.Light, PanelCanvasTheme.Dark })
        {
            Assert.NotEqual(palette.Accent, palette.Text, StringComparer.OrdinalIgnoreCase);
            Assert.True(Contrast(palette.Accent, RelativeLuminance(palette.Page)) >= 3.0,
                $"高亮色在 {palette.Page} 上低于大字号可读的 3:1：{palette.Accent}");
        }
    }

    private static string ToHex(uint argb)
        => "#" + (argb & 0x00FFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    private static double Contrast(string foregroundHex, double backgroundColorRelativeLuminance)
    {
        var fg = RelativeLuminance(foregroundHex);
        var hi = Math.Max(fg, backgroundColorRelativeLuminance);
        var lo = Math.Min(fg, backgroundColorRelativeLuminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG 2.1 相对亮度。输入形如 <c>#RRGGBB</c>。</summary>
    private static double RelativeLuminance(string hex)
    {
        var value = hex.StartsWith('#') ? hex[1..] : hex;
        Assert.Equal(6, value.Length);

        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var channel = Convert.ToInt32(value.Substring(i * 2, 2), 16) / 255.0;
            channels[i] = channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }
}
