namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// IMG05（设计规格 §13.3）：Shell 工具区分栏的默认比例，以及
/// 「用户从未配置」与「保存了默认值」之间的边界。
/// <para>
/// 这一批规则全部是纯判断，所以在无 UI 边界钉住：WinUI 只把结果投影成列宽。
/// 关键不变量：初始化默认**只能**影响未配置状态；已保存比例一律优先，
/// 哪怕它正好等于通用默认值，也哪怕当前显示的是工具首页。
/// </para>
/// </summary>
public sealed class ToolWorkspaceInitialRatioTests
{
    [Fact]
    public void Launcher_only_ratio_sits_inside_the_designed_range()
    {
        Assert.Equal(0.32, ToolWorkspaceLayout.LauncherOnlyWidthRatio, 3);
        Assert.InRange(ToolWorkspaceLayout.LauncherOnlyWidthRatio, 0.30, 0.35);
        // 首次使用不改变通用默认（已保存/非启动器场景仍按 0.45）
        Assert.Equal(0.45, ToolWorkspaceLayout.DefaultWidthRatio, 3);
    }

    [Theory]
    [InlineData(true, 0.32)]
    [InlineData(false, 0.45)]
    public void Initial_ratio_depends_on_whether_only_the_launcher_is_shown(
        bool launcherOnly,
        double expected) =>
        Assert.Equal(expected, ToolWorkspaceLayout.InitialRatioWithoutPreference(launcherOnly), 3);

    [Theory]
    [InlineData(true, 0.32)]
    [InlineData(false, 0.45)]
    public void Context_reset_target_follows_the_same_rule(bool launcherOnly, double expected) =>
        Assert.Equal(expected, ToolWorkspaceLayout.DefaultRatioFor(launcherOnly), 3);

    [Theory]
    [InlineData(0.62)]
    [InlineData(0.45)] // 用户拖到正好等于默认值：仍算「已配置」，不能被 0.32 覆盖
    [InlineData(0.30)]
    [InlineData(0.90)]
    public void Saved_preference_always_wins_over_the_initial_default(double saved)
    {
        // 与当前显示什么无关：切标签页不会让比例跳变（这正是 §13.3 禁止的行为）
        Assert.Equal(saved, ToolWorkspaceLayout.ResolveLoadedRatio(saved, launcherOnly: true), 3);
        Assert.Equal(saved, ToolWorkspaceLayout.ResolveLoadedRatio(saved, launcherOnly: false), 3);
    }

    [Fact]
    public void Unconfigured_falls_back_to_the_initial_default()
    {
        Assert.Equal(0.32, ToolWorkspaceLayout.ResolveLoadedRatio(null, launcherOnly: true), 3);
        Assert.Equal(0.45, ToolWorkspaceLayout.ResolveLoadedRatio(null, launcherOnly: false), 3);
    }

    [Fact]
    public void Non_finite_saved_value_is_treated_as_unconfigured()
    {
        // 脏数据不该把分栏算崩，也不该悄悄变成 0.45
        Assert.Equal(0.32, ToolWorkspaceLayout.ResolveLoadedRatio(double.NaN, launcherOnly: true), 3);
    }

    [Fact]
    public void Out_of_range_saved_value_is_clamped_not_replaced()
    {
        Assert.Equal(0.9, ToolWorkspaceLayout.ResolveLoadedRatio(5, launcherOnly: false), 3);
        Assert.Equal(0.1, ToolWorkspaceLayout.ResolveLoadedRatio(0.0001, launcherOnly: false), 3);
    }

    [Fact]
    public void Context_reset_keeps_the_other_settings()
    {
        var layout = new ToolWorkspaceLayout { WidthRatio = 0.7, AutoExpandOnActivity = true };

        var reset = layout.ResetWidth(ToolWorkspaceLayout.DefaultRatioFor(launcherOnly: true));

        Assert.Equal(0.32, reset.WidthRatio, 3);
        Assert.True(reset.AutoExpandOnActivity);
    }

    [Fact]
    public void Launcher_ratio_still_respects_the_chat_minimum()
    {
        // 新默认仍要走既有的 Resolve 保护：不得把聊天区压到最小宽以下，
        // 且 chat + tool 仍等于可用宽度。
        var layout = new ToolWorkspaceLayout
        {
            WidthRatio = ToolWorkspaceLayout.LauncherOnlyWidthRatio,
        };

        var allocation = layout.Resolve(2000, expanded: true);

        Assert.True(allocation.ChatWidth >= ToolWorkspaceLayout.MinimumChatWidth);
        Assert.Equal(2000, allocation.ChatWidth + allocation.ToolRegionWidth, 3);
    }
}
