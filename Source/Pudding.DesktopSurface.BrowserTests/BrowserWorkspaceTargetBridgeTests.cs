using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;

namespace Pudding.DesktopSurface.BrowserTests;

/// <summary>
/// 目标注册表驱动的语义（脱离 UI 可测）。重点是两类"只读状态无法自证"的缺陷：
/// ① 同一个页面在两个注册表里说法不一致（一个有版本、一个没有）；
/// ② Agent 目标被撤销后注册表仍回答"是 Agent 目标"——等于替不该被驱动的页面继续背书。
/// </summary>
public sealed class BrowserWorkspaceTargetBridgeTests
{
    private const string ContextId = "ctx-1";
    private const string PageId = "page-1";

    [Fact]
    public void CreatedPage_IsKnownToBothRegistries()
    {
        var (bridge, browser, pages) = Create();

        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(3));

        Assert.Equal(DesktopContextTrust.AgentAuthorized, browser.TrustFor(ContextId));
        Assert.True(browser.IsAgentTarget(ContextId, PageId) is false);
        Assert.Equal(1, pages.OpenPageCount);
        Assert.Equal(3, pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.Version.Value);
    }

    [Fact]
    public void UnregisteredContext_IsUntrustedByDefault()
    {
        var (bridge, browser, _) = Create();
        bridge.OnContextCreated(ContextId);

        // fail closed：没登记过的上下文绝不会被当成可信。
        Assert.Equal(DesktopContextTrust.Untrusted, browser.TrustFor("ctx-never-registered"));
    }

    [Fact]
    public void PageBeforeItsContext_FailsLoud()
    {
        var (bridge, _, _) = Create();

        // 顺序错误不能被静默补登记：那会把不可信上下文变成可信。
        Assert.Throws<InvalidOperationException>(() =>
            bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1)));
    }

    [Fact]
    public void ActivatedPage_IsReportedAsActiveInBothRegistries()
    {
        var (bridge, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1));

        bridge.OnPageActivated(ContextId, PageId, DesktopPageVersion.Require(2));

        Assert.Equal((ContextId, PageId), browser.ActivePage);
        Assert.Equal(2, pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.Version.Value);
    }

    [Fact]
    public void VersionAdvance_MovesForwardAndNeverBackwards()
    {
        var (bridge, _, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(5));

        bridge.OnPageVersionAdvanced(ContextId, PageId, DesktopPageVersion.Require(6));
        // 迟到的旧版本事件（例如并发的两个导航回调乱序）不得把版本拉回去：
        // 否则已经作废的 Ref 会重新"有效"。
        bridge.OnPageVersionAdvanced(ContextId, PageId, DesktopPageVersion.Require(4));

        Assert.Equal(6, pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.Version.Value);
    }

    [Fact]
    public void VersionAdvance_ForUnknownPage_DoesNotInventATarget()
    {
        var (bridge, _, pages) = Create();
        bridge.OnContextCreated(ContextId);

        bridge.OnPageVersionAdvanced("ctx-1", "never-created", DesktopPageVersion.Require(9));

        Assert.Equal(0, pages.OpenPageCount);
    }

    [Fact]
    public void AgentTarget_CanBeGrantedAndRetracted()
    {
        var (bridge, browser, _) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1));

        bridge.OnAgentTargetChanged(ContextId, PageId);
        Assert.True(browser.IsAgentTarget(ContextId, PageId));

        // 用户接管 / 会话结束：撤销必须是**真的撤销**。
        // 注册表只增不减时，这里会继续回答 true —— 一个不该再被驱动的页面仍被背书。
        bridge.OnAgentTargetChanged(null, null);
        Assert.False(browser.IsAgentTarget(ContextId, PageId));
    }

    [Fact]
    public void AgentTarget_MovesWithoutLeavingTheOldPageAuthorized()
    {
        var (bridge, browser, _) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, "page-a", DesktopPageVersion.Require(1));
        bridge.OnPageCreated(ContextId, "page-b", DesktopPageVersion.Require(1));

        bridge.OnAgentTargetChanged(ContextId, "page-a");
        bridge.OnAgentTargetChanged(ContextId, "page-b");

        Assert.False(browser.IsAgentTarget(ContextId, "page-a"));
        Assert.True(browser.IsAgentTarget(ContextId, "page-b"));
    }

    [Fact]
    public void ClosedPage_DisappearsFromBothRegistries()
    {
        var (bridge, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1), isActive: true);

        bridge.OnPageClosed(ContextId, PageId);

        Assert.Null(browser.ActivePage);
        Assert.Null(pages.Resolve(new DesktopPageTarget(ContextId, PageId)));
        Assert.Equal(0, pages.OpenPageCount);
    }

    [Fact]
    public void ClosedContext_RemovesItsPages()
    {
        var (bridge, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1), isActive: true);

        bridge.OnContextClosed(ContextId);

        Assert.Equal(DesktopContextTrust.Untrusted, browser.TrustFor(ContextId));
        Assert.Null(browser.ActivePage);
        Assert.Equal(0, pages.OpenPageCount);
        Assert.Equal(0, pages.ContextCount);
    }

    [Fact]
    public void Clear_EmptiesBothRegistries()
    {
        var (bridge, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1), isActive: true);
        bridge.OnAgentTargetChanged(ContextId, PageId);

        bridge.Clear();

        Assert.Equal(DesktopContextTrust.Untrusted, browser.TrustFor(ContextId));
        Assert.Null(browser.ActivePage);
        Assert.False(browser.IsAgentTarget(ContextId, PageId));
        Assert.Equal(0, pages.OpenPageCount);
        Assert.Equal(0, pages.ContextCount);
    }

    [Fact]
    public void ExplicitTrust_IsNotOverriddenByTheDefault()
    {
        var (bridge, browser, _) = Create();

        bridge.OnContextCreated(ContextId, DesktopContextTrust.Workbench);

        Assert.Equal(DesktopContextTrust.Workbench, browser.TrustFor(ContextId));
    }

    [Fact]
    public void Constructor_RejectsMissingRegistries()
    {
        Assert.Throws<ArgumentNullException>(() => new BrowserWorkspaceTargetBridge(null!, new DesktopTargetRegistry()));
        Assert.Throws<ArgumentNullException>(() => new BrowserWorkspaceTargetBridge(new BrowserTargetRegistry(), null!));
    }

    [Fact]
    public void BlankIdentifiers_AreRejectedInsteadOfSilentlyIgnored()
    {
        var (bridge, _, _) = Create();

        Assert.Throws<ArgumentException>(() => bridge.OnContextCreated(" "));
        Assert.Throws<ArgumentException>(() => bridge.OnPageCreated(ContextId, " ", DesktopPageVersion.Require(1)));
    }

    private static (BrowserWorkspaceTargetBridge Bridge, BrowserTargetRegistry Browser, DesktopTargetRegistry Pages) Create()
    {
        var browser = new BrowserTargetRegistry();
        var pages = new DesktopTargetRegistry();
        return (new BrowserWorkspaceTargetBridge(browser, pages), browser, pages);
    }
}
