using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Automation;

namespace Pudding.DesktopSurface.BrowserTests;

/// <summary>
/// 目标注册表驱动的语义（脱离 UI 可测）。重点是两类"只读状态无法自证"的缺陷：
/// ① 同一个页面在两个注册表里说法不一致（一个有版本、一个没有）；
/// ② Agent 目标被撤销后注册表仍回答"是 Agent 目标"——等于替不该被驱动的页面继续背书。
/// </summary>
public sealed class BrowserWorkspaceTargetBridgeTests
{
    /// <summary>模拟已握手成功的连接世代（授权绑定要带它，否则签发被拒）。</summary>
    private static readonly Pudding.Contracts.ConnectionGeneration TestGeneration =
        Pudding.Contracts.ConnectionGeneration.Require(7);
    private const string ContextId = "ctx-1";
    private const string PageId = "page-1";

    [Fact]
    public void CreatedPage_IsKnownToBothRegistries()
    {
        var (bridge, _, browser, pages) = Create();

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
        var (bridge, _, browser, _) = Create();
        bridge.OnContextCreated(ContextId);

        // fail closed：没登记过的上下文绝不会被当成可信。
        Assert.Equal(DesktopContextTrust.Untrusted, browser.TrustFor("ctx-never-registered"));
    }

    [Fact]
    public void PageBeforeItsContext_FailsLoud()
    {
        var (bridge, _, _, _) = Create();

        // 顺序错误不能被静默补登记：那会把不可信上下文变成可信。
        Assert.Throws<InvalidOperationException>(() =>
            bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1)));
    }

    [Fact]
    public void ActivatedPage_IsReportedAsActiveInBothRegistries()
    {
        var (bridge, _, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1));

        bridge.OnPageActivated(ContextId, PageId, DesktopPageVersion.Require(2));

        Assert.Equal((ContextId, PageId), browser.ActivePage);
        Assert.Equal(2, pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.Version.Value);
    }

    [Fact]
    public void VersionAdvance_MovesForwardAndNeverBackwards()
    {
        var (bridge, _, _, pages) = Create();
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
        var (bridge, _, _, pages) = Create();
        bridge.OnContextCreated(ContextId);

        bridge.OnPageVersionAdvanced("ctx-1", "never-created", DesktopPageVersion.Require(9));

        Assert.Equal(0, pages.OpenPageCount);
    }

    [Fact]
    public void AgentTarget_CanBeGrantedAndRetracted()
    {
        var (bridge, _, browser, _) = Create();
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
        var (bridge, _, browser, _) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, "page-a", DesktopPageVersion.Require(1));
        bridge.OnPageCreated(ContextId, "page-b", DesktopPageVersion.Require(1));

        bridge.OnAgentTargetChanged(ContextId, "page-a");
        bridge.OnAgentTargetChanged(ContextId, "page-b");

        Assert.False(browser.IsAgentTarget(ContextId, "page-a"));
        Assert.True(browser.IsAgentTarget(ContextId, "page-b"));
    }

    [Fact]
    public void RepeatedPageCreated_DoesNotClearAPinnedAgentTarget()
    {
        // 回归：页面生命周期事件会重复触发（标签重建 / 重新播种 / 窗口重连）。
        // 早先 `OnPageCreated` 把"不表态"当成了 `isAgentTarget: false`，于是**重复的创建事件
        // 会静默关掉页级准入闸门**：UI 仍显示这是 Agent 目标页，准入却说不是
        // （两个注册表正是在这里分叉 —— browser 侧只增不减、admission 侧被清零）。
        var (bridge, _, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(4));
        bridge.OnAgentTargetChanged(ContextId, PageId);

        // 同一个页面的创建事件再次到达（版本未知）。
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Unknown);

        Assert.True(
            pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.IsAgentTarget,
            "重复登记不得清掉用户选定的 Agent 目标");
        Assert.True(browser.IsAgentTarget(ContextId, PageId));
        Assert.Equal(4, pages.Resolve(new DesktopPageTarget(ContextId, PageId))!.Version.Value);
    }

    [Fact]
    public void AgentTarget_IsMirroredIntoThePageRegistryThatAdmissionReads()
    {
        // 两个注册表都要更新：准入读的是**页面**注册表的 page 级标记。
        // 只更新一边就会出现「UI 说这是目标页、准入说不是」这种最难排查的漂移。
        var (bridge, _, _, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, "page-a", DesktopPageVersion.Require(1));
        bridge.OnPageCreated(ContextId, "page-b", DesktopPageVersion.Require(1));

        bridge.OnAgentTargetChanged(ContextId, "page-b");

        Assert.False(pages.Resolve(new DesktopPageTarget(ContextId, "page-a"))!.IsAgentTarget);
        Assert.True(pages.Resolve(new DesktopPageTarget(ContextId, "page-b"))!.IsAgentTarget);

        bridge.OnAgentTargetChanged(ContextId, "page-a");
        Assert.True(pages.Resolve(new DesktopPageTarget(ContextId, "page-a"))!.IsAgentTarget);
        Assert.False(pages.Resolve(new DesktopPageTarget(ContextId, "page-b"))!.IsAgentTarget);

        // 撤销同理（用户接管 / 会话结束）。
        bridge.OnAgentTargetChanged(null, null);
        Assert.False(pages.Resolve(new DesktopPageTarget(ContextId, "page-a"))!.IsAgentTarget);
    }

    [Fact]
    public void ClosedPage_TakesTheAgentTargetMarkWithIt()
    {
        var (bridge, _, browser, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(1));
        bridge.OnAgentTargetChanged(ContextId, PageId);

        bridge.OnPageClosed(ContextId, PageId);

        Assert.False(browser.IsAgentTarget(ContextId, PageId));
        Assert.Null(pages.Resolve(new DesktopPageTarget(ContextId, PageId)));
        // 不凭已关闭的 id 重新标记。
        Assert.False(pages.SetAgentTarget(ContextId, PageId));
    }

    [Fact]
    public void ClosedPage_DisappearsFromBothRegistries()
    {
        var (bridge, _, browser, pages) = Create();
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
        var (bridge, _, browser, pages) = Create();
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
        var (bridge, _, browser, pages) = Create();
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
        var (bridge, _, browser, _) = Create();

        bridge.OnContextCreated(ContextId, DesktopContextTrust.Workbench);

        Assert.Equal(DesktopContextTrust.Workbench, browser.TrustFor(ContextId));
    }

    [Fact]
    public void Constructor_RejectsMissingLifecyclePort()
    {
        Assert.Throws<ArgumentNullException>(() => new BrowserWorkspaceTargetBridge(null!));
    }

    [Fact]
    public void ClosingAPage_AlsoRevokesItsAuthorization()
    {
        // 桥接层只负责"关闭事件"，清理交给生命周期端口（设计方案 §3.3）：
        // 关闭后授权必须一起没 —— 页面都不存在了，"还能读"没有意义。
        var (bridge, authority, _, pages) = Create();
        bridge.OnContextCreated(ContextId);
        bridge.OnPageCreated(ContextId, PageId, DesktopPageVersion.Require(2));
        bridge.OnAgentTargetChanged(ContextId, PageId);

        var grant = new BrowserPageGrant(
            BrowserPageGrantId.NewId(),
            new BrowserPageGrantBinding(
                new Pudding.Contracts.DesktopInstanceId("desk-test"),
                new Pudding.Contracts.DesktopProcessInstanceId("proc-test"),
                TestGeneration),
            new DesktopPageTarget(ContextId, PageId),
            new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            BrowserPageGrantEpoch.Initial,
            DateTimeOffset.UtcNow);
        authority.IssueGrant(grant);
        Assert.Single(authority.ActiveGrants);

        bridge.OnPageClosed(ContextId, PageId);

        Assert.Null(pages.Resolve(new DesktopPageTarget(ContextId, PageId)));
        Assert.Empty(authority.ActiveGrants);
    }

    [Fact]
    public void BlankIdentifiers_AreRejectedInsteadOfSilentlyIgnored()
    {
        var (bridge, _, _, _) = Create();

        Assert.Throws<ArgumentException>(() => bridge.OnContextCreated(" "));
        Assert.Throws<ArgumentException>(() => bridge.OnPageCreated(ContextId, " ", DesktopPageVersion.Require(1)));
    }

    private static (BrowserWorkspaceTargetBridge Bridge, BrowserAutomationAuthority Authority,
        BrowserTargetRegistry Browser, DesktopTargetRegistry Pages) Create()
    {
        var browser = new BrowserTargetRegistry();
        var pages = new DesktopTargetRegistry();
        var authority = new BrowserAutomationAuthority(
            new Pudding.Contracts.DesktopInstanceId("desk-test"),
            new Pudding.Contracts.DesktopProcessInstanceId("proc-test"));
        // 授权绑定包含连接世代：未握手（None）时签发会被拒，先模拟握手成功。
        authority.NotifyConnectionGeneration(TestGeneration);
        var lifecycle = new BrowserPageLifecycle(browser, pages, authority);
        return (new BrowserWorkspaceTargetBridge(lifecycle), authority, browser, pages);
    }
}
