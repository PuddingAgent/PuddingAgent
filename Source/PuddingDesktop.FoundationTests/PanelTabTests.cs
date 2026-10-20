namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 「交互面板」标签页（ToolTabKind.Panel）的纯逻辑契约。
///
/// 用户 2026-10-20 的定位：Panel 是**可交互的媒体画布**（Markdown / 设计图纸等），
/// 不是表单提交通道。Shell 侧的 WebView2 载体与 Agent 推送通道（shell.panel_render）
/// 都已落地，所以它现在是「可用」。
///
/// UI 那半边无法在无窗口环境验证，但「可用性标签」与「画布单例」两条判据在 Foundation 里，
/// 必须在这里钉住：少了第二条，表现就是「点两次交互面板出现两个画布」，
/// 而且没有任何断言会失败。
/// </summary>
public sealed class PanelTabTests
{
    [Fact]
    public void Panel_is_ready_because_agent_can_push_content()
    {
        // 取红历史（这正是这条断言的价值）：panel.render 落地前它是 Deferred，
        // 本测试当时断言 Deferred 并注明「落地后这条会红」。它确实红了，于是把标签改成可用。
        // 若将来通道被移除，这里必须再改回去 —— 不允许「实现没了但标签还写着可用」。
        Assert.Equal(ToolTabAvailability.Ready, ToolAvailabilityCatalog.For(ToolTabKind.Panel));
        Assert.Equal("可用", ToolAvailabilityCatalog.CardLabel(ToolTabKind.Panel));
    }

    [Fact]
    public void Panel_is_one_canvas_and_reopening_activates_it()
    {
        var tabs = new ToolWorkspaceTabs();

        var first = tabs.Open(Descriptor());
        var second = tabs.Open(Descriptor());

        Assert.Equal(1, tabs.Tabs.Count);
        Assert.Equal(first.Id, second.Id);
        Assert.Same(second, tabs.ActiveTab);
    }

    [Fact]
    public void Panel_can_close_but_home_cannot()
    {
        var tabs = new ToolWorkspaceTabs();
        var panel = tabs.Open(Descriptor());

        Assert.True(panel.CanClose);
        Assert.False(tabs.EnsureHome().CanClose);
    }

    private static ToolTabDescriptor Descriptor() => new()
    {
        Id = ToolTabIdentity.Panel("main"),
        Kind = ToolTabKind.Panel,
        Title = "交互面板",
        Subtitle = "内置示例画布",
    };
}
