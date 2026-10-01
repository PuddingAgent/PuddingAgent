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
}
