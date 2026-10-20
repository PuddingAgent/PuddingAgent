namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 「交互面板」标签页（ToolTabKind.Panel）的纯逻辑契约。
///
/// 用户 2026-10-20 的定位：Panel 是**可交互的媒体画布**（Markdown / 设计图纸等），
/// 不是表单提交通道。Shell 侧的 WebView2 载体已经在 MainWindow 里实现，
/// 但 Agent 推送内容的 panel.render 通道还没建立 —— 所以它现在必须仍是「待接入」。
///
/// UI 那半边无法在无窗口环境验证，但「可用性标签」与「画布单例」两条判据在 Foundation 里，
/// 必须在这里钉住：少了第二条，表现就是「点两次交互面板出现两个画布」，
/// 而且没有任何断言会失败。
/// </summary>
public sealed class PanelTabTests
{
    [Fact]
    public void Panel_stays_deferred_until_agent_can_push_content()
    {
        // 这条断言是边界的锚：等 panel.render 落地、Agent 真能推内容时，
        // 它会失败，提醒把标签改成「可用」——而不是靠人记得去改。
        Assert.Equal(ToolTabAvailability.Deferred, ToolAvailabilityCatalog.For(ToolTabKind.Panel));
        Assert.Equal("待接入", ToolAvailabilityCatalog.CardLabel(ToolTabKind.Panel));
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
