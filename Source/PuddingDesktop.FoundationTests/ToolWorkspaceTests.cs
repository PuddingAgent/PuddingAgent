using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// Layout and instance policy for the right-hand multi-tab tool workspace. These are the
/// rules the WinUI Shell only projects into controls, so they are verified without a window.
/// </summary>
public sealed class ToolWorkspaceLayoutTests
{
    [Fact]
    public void DefaultPreferenceIsCollapsedAtFortyFivePercent()
    {
        var layout = new ToolWorkspaceLayout();

        Assert.Equal(0.45, layout.WidthRatio);
        Assert.False(layout.AutoExpandOnActivity);

        var collapsed = layout.Resolve(2000, expanded: false);
        Assert.False(collapsed.IsVisible);
        Assert.Equal(2000, collapsed.ChatWidth);
        Assert.Equal(0, collapsed.ToolRegionWidth);
    }

    [Fact]
    public void SplitKeepsChatAndToolWidthsAdditive()
    {
        var allocation = new ToolWorkspaceLayout().Resolve(2000, expanded: true);

        Assert.False(allocation.SpansContent);
        Assert.Equal(2000, allocation.ChatWidth + allocation.ToolRegionWidth);
        Assert.Equal(900, allocation.ToolRegionWidth, 3);
        // The divider lives inside the tool region, beside the panel itself.
        Assert.Equal(900 - ToolWorkspaceLayout.SplitterWidth, allocation.PanelWidth, 3);
    }

    [Fact]
    public void DraggedWidthBelowMinimumIsClamped()
    {
        var layout = new ToolWorkspaceLayout().WithToolWidth(2000, 10);
        var allocation = layout.Resolve(2000, expanded: true);

        Assert.True(allocation.ToolRegionWidth >= ToolWorkspaceLayout.MinimumToolWidth);
    }

    [Fact]
    public void DraggedRatioIsReclampedAgainstTheCurrentChatMinimum()
    {
        // A ratio captured on a wide window must not squeeze the chat on a smaller one.
        var layout = new ToolWorkspaceLayout().WithToolWidth(2000, 1200);
        var allocation = layout.Resolve(1000, expanded: true);

        Assert.False(allocation.SpansContent);
        Assert.True(allocation.ChatWidth >= ToolWorkspaceLayout.MinimumChatWidth);
        Assert.Equal(1000, allocation.ChatWidth + allocation.ToolRegionWidth);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(200)]
    public void NarrowWindowOverlaysTheChatInsteadOfSqueezingIt(double width)
    {
        var allocation = new ToolWorkspaceLayout().Resolve(width, expanded: true);

        Assert.True(allocation.SpansContent);
        // The chat keeps its full width because the panel floats above it.
        Assert.Equal(width, allocation.ChatWidth);
        Assert.True(allocation.ToolRegionWidth <= width);
    }

    [Fact]
    public void ZoomedWorkspaceTakesEverything()
    {
        var allocation = new ToolWorkspaceLayout().Resolve(2000, expanded: true, zoomed: true);

        Assert.True(allocation.SpansContent);
        Assert.Equal(0, allocation.ChatWidth);
        Assert.Equal(2000, allocation.ToolRegionWidth);
    }

    [Fact]
    public void DoubleClickResetRestoresTheDefaultRatio()
    {
        var dragged = new ToolWorkspaceLayout().WithToolWidth(2000, 1500);

        Assert.Equal(0.75, dragged.WidthRatio, 3);
        Assert.Equal(ToolWorkspaceLayout.DefaultWidthRatio, dragged.ResetWidth().WidthRatio);
    }

    [Theory]
    [InlineData(double.NaN, 0.45)]
    [InlineData(double.PositiveInfinity, 0.45)]
    [InlineData(4.0, 0.9)]
    [InlineData(-1.0, 0.1)]
    public void InvalidStoredRatioFallsBackToTheDefault(double stored, double expected)
    {
        Assert.Equal(expected, new ToolWorkspaceLayout { WidthRatio = stored }.Normalize().WidthRatio);
    }

    [Fact]
    public void InvalidWidthsNeverProduceANegativeSplit()
    {
        var layout = new ToolWorkspaceLayout();

        var unknown = layout.Resolve(double.NaN, expanded: true);
        var empty = layout.Resolve(0, expanded: true);

        Assert.False(unknown.IsVisible);
        Assert.False(empty.IsVisible);
        Assert.True(layout.WithToolWidth(double.NaN, 400).WidthRatio > 0);
    }
}

public sealed class ToolWorkspaceTabsTests
{
    private static ToolTabDescriptor Browser(string pageId, string title = "页面") => new()
    {
        Id = ToolTabIdentity.Browser(pageId),
        Kind = ToolTabKind.Browser,
        Title = title,
        Subtitle = "Agent 浏览器",
        ResourceKey = pageId,
    };

    [Fact]
    public void OpeningTheSameInstanceTwiceReusesOneTab()
    {
        var tabs = new ToolWorkspaceTabs();

        var first = tabs.Open(Browser("p1"));
        var second = tabs.Open(Browser("p1", "改名后"));

        Assert.Single(tabs.Tabs);
        Assert.Same(first, second);
        Assert.Equal("改名后", first.Title);
        Assert.Equal(first.Id, tabs.ActiveTabId);
    }

    [Fact]
    public void ResourceIdentityUnifiesCaseAndSeparators()
    {
        Assert.Equal(ToolTabIdentity.Output(@"C:\Temp\Report.CSV"), ToolTabIdentity.Output(@"c:/temp/report.csv"));
        // Opaque ids stay case-sensitive.
        Assert.NotEqual(ToolTabIdentity.Artifact("Report-A"), ToolTabIdentity.Artifact("report-a"));
    }

    [Fact]
    public void TerminalsAreAlwaysNewInstances()
    {
        var tabs = new ToolWorkspaceTabs();

        tabs.Open(new ToolTabDescriptor { Id = ToolTabIdentity.Terminal("s1"), Kind = ToolTabKind.Terminal, Title = "终端 1" });
        tabs.Open(new ToolTabDescriptor { Id = ToolTabIdentity.Terminal("s2"), Kind = ToolTabKind.Terminal, Title = "终端 2" });

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Equal(ToolTabIdentity.Terminal("s2"), tabs.ActiveTabId);
    }

    [Fact]
    public void BackgroundOutputNeverStealsFocus()
    {
        var tabs = new ToolWorkspaceTabs();
        var focused = tabs.Open(Browser("p1"));

        var background = tabs.Open(Browser("p2", "后台页面"), focus: false);

        Assert.Equal(focused.Id, tabs.ActiveTabId);
        Assert.True(background.HasUnread);
        Assert.False(focused.HasUnread);
        Assert.Equal(1, tabs.UnreadCount);
    }

    [Fact]
    public void FirstInstanceIsSelectedEvenWhenItAskedNotToStealFocus()
    {
        var tabs = new ToolWorkspaceTabs();

        var tab = tabs.Open(Browser("p1"), focus: false);

        Assert.Equal(tab.Id, tabs.ActiveTabId);
        Assert.False(tab.HasUnread);
    }

    [Fact]
    public void ActivatingATabClearsItsUnreadMarker()
    {
        var tabs = new ToolWorkspaceTabs();
        tabs.Open(Browser("p1"));
        tabs.Open(Browser("p2"), focus: false);

        Assert.True(tabs.Activate(ToolTabIdentity.Browser("p2")));

        Assert.Equal(0, tabs.UnreadCount);
        Assert.Equal(ToolTabIdentity.Browser("p2"), tabs.ActiveTabId);
    }

    [Fact]
    public void ClosingTheActiveTabSelectsTheNeighbour()
    {
        var tabs = new ToolWorkspaceTabs();
        tabs.Open(Browser("p1"));
        tabs.Open(Browser("p2"));
        tabs.Open(Browser("p3"));
        tabs.Activate(ToolTabIdentity.Browser("p2"));

        Assert.True(tabs.Close(ToolTabIdentity.Browser("p2")));

        Assert.Equal(ToolTabIdentity.Browser("p3"), tabs.ActiveTabId);
        Assert.Equal(2, tabs.Tabs.Count);
    }

    [Fact]
    public void HomeTabIsSingletonFirstAndNotClosable()
    {
        var tabs = new ToolWorkspaceTabs();
        tabs.Open(Browser("p1"));
        var home = tabs.EnsureHome();

        Assert.Same(home, tabs.EnsureHome());
        Assert.Same(home, tabs.Tabs[0]);
        Assert.False(home.CanClose);
        Assert.False(tabs.Close(ToolTabIdentity.Home));
        Assert.Equal(2, tabs.Tabs.Count);
    }

    [Fact]
    public void RunningAndUnreadMarkersAreCountedForTheEntryBadge()
    {
        var tabs = new ToolWorkspaceTabs();
        tabs.EnsureHome();
        Assert.Equal(string.Empty, tabs.DescribeActivity());

        tabs.Open(Browser("p1"));
        tabs.SetRunning(ToolTabIdentity.Browser("p1"), true);
        Assert.Equal("1 项运行中", tabs.DescribeActivity());

        tabs.Open(Browser("p2"), focus: false);
        Assert.Equal("1 项运行中 · 1 项未读", tabs.DescribeActivity());

        tabs.SetRunning(ToolTabIdentity.Browser("p1"), false);
        Assert.Equal("1 项未读", tabs.DescribeActivity());
    }

    [Fact]
    public void DeferredCapabilitiesStayHonest()
    {
        var tabs = new ToolWorkspaceTabs();
        var terminal = tabs.Open(new ToolTabDescriptor
        {
            Id = ToolTabIdentity.Terminal("s1"),
            Kind = ToolTabKind.Terminal,
            Title = "终端 · 新会话",
            Availability = ToolTabAvailability.Deferred,
        });

        Assert.Equal(ToolTabAvailability.Deferred, terminal.Availability);
        Assert.False(terminal.IsRunning);
        Assert.False(tabs.SetRunning(terminal.Id, false));
        Assert.True(tabs.SetStatus(terminal.Id, "会话组件待接入"));
        Assert.Equal("会话组件待接入", terminal.StatusText);
    }

    [Fact]
    public void TooltipCarriesTheOwningAgentOrSession()
    {
        var tabs = new ToolWorkspaceTabs();
        var tab = tabs.Open(new ToolTabDescriptor
        {
            Id = ToolTabIdentity.Browser("p1"),
            Kind = ToolTabKind.Browser,
            Title = "销售报告",
            Subtitle = "会话 A · Agent 1",
        });

        Assert.Equal("销售报告 · 会话 A · Agent 1", tab.Tooltip);
    }

    [Fact]
    public void OpeningWithoutAnIdIsRejected()
    {
        var tabs = new ToolWorkspaceTabs();

        Assert.Throws<ArgumentException>(() => tabs.Open(new ToolTabDescriptor
        {
            Id = "  ",
            Kind = ToolTabKind.Output,
            Title = "文件",
        }));
    }
}

public sealed class ToolWorkspaceActivityPolicyTests
{
    [Fact]
    public void AutoExpandIsOffUntilTheUserOptsIn()
    {
        var policy = new ToolWorkspaceActivityPolicy();

        Assert.False(policy.ShouldAutoExpand());

        policy.AutoExpandOnActivity = true;
        Assert.True(policy.ShouldAutoExpand());
    }

    [Fact]
    public void ManualCollapseSuppressesAutoExpandForTheRestOfTheRound()
    {
        var policy = new ToolWorkspaceActivityPolicy { AutoExpandOnActivity = true };

        policy.NotifyUserCollapsed();

        Assert.True(policy.IsSuppressedByUser);
        Assert.False(policy.ShouldAutoExpand());
        // Activity still in flight: the suppression holds.
        Assert.False(policy.Observe(activityInFlight: true));
        Assert.False(policy.ShouldAutoExpand());
    }

    [Fact]
    public void SuppressionEndsWhenTheRoundGoesIdle()
    {
        var policy = new ToolWorkspaceActivityPolicy { AutoExpandOnActivity = true };
        policy.NotifyUserCollapsed();

        Assert.True(policy.Observe(activityInFlight: false));
        Assert.False(policy.IsSuppressedByUser);
        Assert.True(policy.ShouldAutoExpand());
    }

    [Fact]
    public void OpeningTheWorkspaceByHandClearsSuppression()
    {
        var policy = new ToolWorkspaceActivityPolicy { AutoExpandOnActivity = true };
        policy.NotifyUserCollapsed();

        policy.NotifyUserExpanded();

        Assert.True(policy.ShouldAutoExpand());
    }
}
