using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Automation;

namespace DesktopServiceTests;

/// <summary>
/// 页面/上下文生命周期端口（设计方案 §3.3）：关闭后**同时**清掉两个注册表、该页全部授权与写租约。
///
/// 为什么这些断言值钱：关闭一处、漏另一处，是本项目已经踩过的坑
/// （审计 2026-10-08 结论 #1/#2 都是"两处状态不一致"）。
/// </summary>
public sealed class BrowserLifecycleTests
{
    private const string ContextId = "ctx-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(7);

    private static readonly DesKtopIds Ids = new();

    private sealed class DesKtopIds
    {
        public DesktopInstanceId Desktop { get; } = new("desk-test");

        public DesktopProcessInstanceId Process { get; } = new("proc-test");
    }

    private static (BrowserPageLifecycle Lifecycle, BrowserAutomationAuthority Authority,
        DesktopTargetRegistry Pages, BrowserTargetRegistry Browser, DesktopPageTarget Page) Create()
    {
        var pages = new DesktopTargetRegistry();
        var browser = new BrowserTargetRegistry();
        var authority = TestAuthorities.Create(Ids.Desktop, Ids.Process);
        authority.NotifyConnectionGeneration(Generation);
        var lifecycle = new BrowserPageLifecycle(browser, pages, authority);

        pages.RegisterContext(ContextId, DesktopContextTrust.AgentAuthorized);
        browser.RegisterContext(ContextId, DesktopContextTrust.AgentAuthorized);

        var page = new DesktopPageTarget(ContextId, "page-1");
        pages.RegisterPage(page, DesktopPageVersion.Require(3), DesktopPageReadiness.Interactive, isAgentTarget: true);
        browser.RegisterPage(ContextId, page.PageId, isAgentTarget: true);

        return (lifecycle, authority, pages, browser, page);
    }

    private static BrowserCallerIdentity Agent() => new(BrowserCallerKind.Agent, "agent-1", "task-1");

    private static BrowserPageGrant Grant(
        BrowserAutomationAuthority authority,
        DesktopPageTarget page,
        BrowserPageGrantScope scope = BrowserPageGrantScope.Read | BrowserPageGrantScope.Write)
    {
        var grant = new BrowserPageGrant(
            BrowserPageGrantId.NewId(),
            new BrowserPageGrantBinding(Ids.Desktop, Ids.Process, Generation),
            page,
            Agent(),
            scope,
            BrowserPageGrantEpoch.Initial,
            Now);

        authority.IssueGrant(grant);
        return grant;
    }

    private static BrowserAutomationRequest WriteRequest(DesktopPageTarget page) =>
        new(BrowserAutomationOperation.Interact, Agent(), OperationId.NewId(), Generation, page);

    [Fact]
    public void ClosePage_ClearsBothRegistries_AndRevokesTheGrant()
    {
        var (lifecycle, authority, pages, browser, page) = Create();
        Grant(authority, page);

        var outcome = lifecycle.ClosePage(page);

        Assert.True(outcome.WasRegistered);
        Assert.Equal(1, outcome.PagesCleared);
        Assert.Equal(1, outcome.GrantsRevoked);
        Assert.True(outcome.ChangedAnything);

        // ① 准入读的注册表：页面没了（Resolve 返回 null）。
        Assert.Null(pages.Resolve(page));
        // ② 浏览器目标注册表：目标标记随之消失（不留"替已关闭页面背书"的痕迹）。
        Assert.False(browser.IsAgentTarget(ContextId, page.PageId));
        // ③ 授权全部撤销 —— **连只读也不留**：页面都不存在了，"还能读"没有意义。
        Assert.Empty(authority.ActiveGrants);
    }

    [Fact]
    public void ClosingAPageRevokesMoreThanSwitchingTheAgentTarget()
    {
        // 两条路径的区别必须显式钉住（§3.2 vs §3.3）：
        // 切换目标只撤执行权（用户还想看），关闭页面连只读一起撤。
        var (_, authority, _, _, page) = Create();
        Grant(authority, page, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write);

        authority.RevokeExecutionScope(page);

        var remaining = Assert.Single(authority.ActiveGrants);
        Assert.Equal(BrowserPageGrantScope.Read, remaining.Scope);

        // 关闭路径（新端口）：什么都不留。
        var (lifecycle2, authority2, _, _, page2) = Create();
        Grant(authority2, page2, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write);
        lifecycle2.ClosePage(page2);

        Assert.Empty(authority2.ActiveGrants);
    }

    [Fact]
    public void ClosePage_ReleasesTheWriteLeaseHeldForIt()
    {
        // 页面关闭后租约必须释放：否则"页面已经没了"仍占着写租约，后续操作会莫名阻塞。
        var (lifecycle, authority, _, _, page) = Create();
        Grant(authority, page);

        var admitted = authority.Admit(WriteRequest(page));
        Assert.True(admitted.IsAllowed);
        Assert.NotNull(admitted.Lease);
        Assert.NotNull(authority.Capture().ActiveLease);

        var outcome = lifecycle.ClosePage(page);

        Assert.True(outcome.LeaseReleased);
        Assert.Null(authority.Capture().ActiveLease);
    }

    [Fact]
    public void ClosePage_IsIdempotent_AndStillRevokesStaleAuthorization()
    {
        // 失败安全：即使注册表里已经没有这个页面（重复关闭、事件顺序错乱），
        // 也**仍然**撤销授权 —— 宁可多撤一次，也不能让已关闭页面的授权继续活着。
        var (lifecycle, authority, _, _, page) = Create();
        var first = lifecycle.ClosePage(page);
        Assert.True(first.WasRegistered);

        Grant(authority, page);
        var second = lifecycle.ClosePage(page);

        Assert.False(second.WasRegistered);
        Assert.Equal(0, second.PagesCleared);
        Assert.Equal(1, second.GrantsRevoked);
        Assert.Empty(authority.ActiveGrants);
    }

    [Fact]
    public void CloseContext_ClearsEveryPageAndItsAuthorization()
    {
        var (lifecycle, authority, pages, browser, page) = Create();
        var second = new DesktopPageTarget(ContextId, "page-2");
        pages.RegisterPage(second, DesktopPageVersion.Require(1));
        browser.RegisterPage(ContextId, second.PageId);
        Grant(authority, page);
        Grant(authority, second);
        Grant(authority, second, BrowserPageGrantScope.Read);

        var outcome = lifecycle.CloseContext(ContextId);

        Assert.Equal(2, outcome.PagesCleared);
        Assert.Equal(3, outcome.GrantsRevoked);
        Assert.Null(pages.Resolve(page));
        Assert.Null(pages.Resolve(second));
        Assert.False(browser.IsAgentTarget(ContextId, page.PageId));
        Assert.Empty(authority.ActiveGrants);
        Assert.Equal(0, pages.ContextCount);
    }

    [Fact]
    public void CloseContext_DoesNotTouchAnotherContext()
    {
        var (lifecycle, authority, pages, _, page) = Create();
        var otherPage = new DesktopPageTarget("ctx-2", "page-9");
        pages.RegisterContext("ctx-2", DesktopContextTrust.AgentAuthorized);
        pages.RegisterPage(otherPage, DesktopPageVersion.Require(1), isAgentTarget: true);
        Grant(authority, page);
        var otherGrant = Grant(authority, otherPage);

        lifecycle.CloseContext(ContextId);

        Assert.Null(pages.Resolve(page));
        Assert.NotNull(pages.Resolve(otherPage));
        Assert.NotNull(pages.Resolve(otherPage));
        var survivor = Assert.Single(authority.ActiveGrants);
        Assert.Equal(otherGrant.GrantId.Value, survivor.GrantId.Value);
    }
}
