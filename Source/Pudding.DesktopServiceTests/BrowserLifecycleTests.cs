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

    // ── 创建侧（§3.3：创建与关闭必须同一个所有者，且登记失败不得留半登记状态）──────

    [Fact]
    public void CreateThroughThePort_RegistersBothRegistries()
    {
        var browser = new BrowserTargetRegistry();
        var pages = new DesktopTargetRegistry();
        var lifecycle = new BrowserPageLifecycle(browser, pages, TestAuthorities.Create());
        var page = new DesktopPageTarget("ctx-1", "page-1");

        lifecycle.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(page, DesktopPageVersion.Require(2), isActive: true);

        // 准入侧（版本/目标标记）与视图侧（可信级别/活动页）都在册。
        Assert.NotNull(pages.Resolve(page));
        Assert.Equal(2, pages.Resolve(page)!.Version.Value);
        Assert.True(browser.IsRegistered("ctx-1", "page-1"));
        Assert.Equal(DesktopContextTrust.AgentAuthorized, browser.TrustFor("ctx-1"));
        Assert.Equal(("ctx-1", "page-1"), browser.ActivePage);

        // 目标切换也走同一个端口。
        var outcome = lifecycle.SetAgentTarget("ctx-1", "page-1");
        Assert.True(outcome.WasRegistered);
        Assert.True(pages.Resolve(page)!.IsAgentTarget);
        Assert.True(browser.IsAgentTarget("ctx-1", "page-1"));
    }

    [Fact]
    public void RegisterPage_WithUnknownContext_LeavesNoHalfRegisteredState()
    {
        // §3.3「登记失败需回收新资源…不返回可操作页面」：页面登记失败后，
        // **视图侧也不能留下它**（否则 UI 会列出一个准入永远看不见的页面）。
        var browser = new BrowserTargetRegistry();
        var pages = new DesktopTargetRegistry();
        var lifecycle = new BrowserPageLifecycle(browser, pages, TestAuthorities.Create());

        // 只登记视图侧，人为造出"视图有、准入没有"的上下文。
        browser.RegisterContext("ctx-orphan", DesktopContextTrust.AgentAuthorized);

        Assert.Throws<InvalidOperationException>(
            () => lifecycle.RegisterPage(new DesktopPageTarget("ctx-orphan", "page-1")));

        Assert.False(browser.IsRegistered("ctx-orphan", "page-1"));
        Assert.Null(pages.Resolve(new DesktopPageTarget("ctx-orphan", "page-1")));
    }

    [Fact]
    public void RegisterContext_Failure_DoesNotLeaveTheViewSideRegistered()
    {
        var browser = new BrowserTargetRegistry();
        var pages = new DesktopTargetRegistry();
        var lifecycle = new BrowserPageLifecycle(browser, pages, TestAuthorities.Create());

        lifecycle.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);

        // 换可信级别必须显式先关闭（否则是静默提权路径）：失败后两侧都保持原样。
        Assert.Throws<InvalidOperationException>(
            () => lifecycle.RegisterContext("ctx-1", DesktopContextTrust.Untrusted));

        Assert.Equal(DesktopContextTrust.AgentAuthorized, browser.TrustFor("ctx-1"));

        // 上下文仍然有效：仍能登记页面（说明失败的回滚没有把它清掉）。
        lifecycle.RegisterPage(new DesktopPageTarget("ctx-1", "page-1"));
        Assert.NotNull(pages.Resolve(new DesktopPageTarget("ctx-1", "page-1")));
    }

    [Fact]
    public void SetAgentTarget_SwitchingRevokesTheOldTargetsExecutionScope_ButKeepsRead()
    {
        // §3.2：切换「Agent 目标」撤销旧页的执行权并推进 grant epoch，但**保留只读授权**
        // （用户切走目标不等于把旧页面从"可读"里删掉）。
        var (lifecycle, authority, pages, _, page) = Create();
        var second = new DesktopPageTarget(ContextId, "page-2");
        pages.RegisterPage(second, DesktopPageVersion.Require(1));
        Grant(authority, page, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write);
        Grant(authority, second, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write);

        var before = authority.Capture().GrantEpoch;
        var outcome = lifecycle.SetAgentTarget(ContextId, second.PageId);

        Assert.True(outcome.ExecutionScopeRevoked);
        Assert.True(authority.Capture().GrantEpoch.Value > before.Value, "切换目标必须推进授权世代");

        var oldTargetGrant = authority.ActiveGrants.Single(grant => grant.TargetsSamePage(page));
        Assert.Equal(BrowserPageGrantScope.Read, oldTargetGrant.Scope);

        var newTargetGrant = authority.ActiveGrants.Single(grant => grant.TargetsSamePage(second));
        Assert.Equal(BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, newTargetGrant.Scope);
    }

    [Fact]
    public void SetAgentTarget_ClearingAlsoRevokesTheExecutionScope()
    {
        var (lifecycle, authority, _, _, page) = Create();
        Grant(authority, page, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write);

        var outcome = lifecycle.SetAgentTarget(null, null);

        Assert.True(outcome.ExecutionScopeRevoked);
        Assert.Equal(BrowserPageGrantScope.Read, Assert.Single(authority.ActiveGrants).Scope);
    }

    [Fact]
    public void ReadingBaselines_AreScopedPerPageAndClearedByTheLifecycle()
    {
        // §145：内容基线必须**随页面/上下文清除**——"页面没了、内容还留着"与"授权没撤"同类。
        var (lifecycle, _, _, _, page) = Create();
        var other = new DesktopPageTarget(ContextId, "page-2");
        var elsewhere = new DesktopPageTarget("ctx-2", "page-9");

        Assert.True(lifecycle.Readings.Remember(page, "rev-1", ["a", "b"]));
        Assert.True(lifecycle.Readings.Remember(other, "rev-1", ["c"]));
        Assert.True(lifecycle.Readings.Remember(elsewhere, "rev-1", ["d"]));
        Assert.Equal(3, lifecycle.Readings.Count);

        // 按页隔离：别的页面的同一个 revision 不会串味。
        Assert.True(lifecycle.Readings.TryGet(page, "rev-1", out var lines));
        Assert.Equal(["a", "b"], lines);

        // 关闭一个页面 ⇒ 只清它自己的基线。
        lifecycle.ClosePage(page);
        Assert.False(lifecycle.Readings.TryGet(page, "rev-1", out _));
        Assert.True(lifecycle.Readings.TryGet(other, "rev-1", out _));
        Assert.True(lifecycle.Readings.TryGet(elsewhere, "rev-1", out _));

        // 关闭上下文 ⇒ 清掉该上下文下所有页面（别的上下文不受影响）。
        lifecycle.CloseContext(ContextId);
        Assert.False(lifecycle.Readings.TryGet(other, "rev-1", out _));
        Assert.True(lifecycle.Readings.TryGet(elsewhere, "rev-1", out _));
    }

    [Fact]
    public void ReadingBaselines_AreBoundedAndRefuseOversizedOnes()
    {
        // 有界：每页只留最近若干份（旧的淘汰 ⇒ 调用方得到 refresh_required 而不是错位 diff）。
        var store = new BrowserReadingBaselineStore(capacityPerPage: 2);
        var page = new DesktopPageTarget("ctx-1", "page-1");

        Assert.True(store.Remember(page, "r1", ["1"]));
        Assert.True(store.Remember(page, "r2", ["2"]));
        Assert.True(store.Remember(page, "r3", ["3"]));

        Assert.False(store.TryGet(page, "r1", out _));
        Assert.True(store.TryGet(page, "r3", out _));
        Assert.Equal(2, store.Count);

        // 过大的基线**不保存**（宁缺勿错：保存一份被截断的基线会算出错误差异）。
        var huge = Enumerable.Range(0, BrowserReadingBaselineStore.MaxLinesPerBaseline + 1)
            .Select(index => index.ToString())
            .ToArray();
        Assert.False(store.Remember(page, "r4", huge));
        Assert.False(store.TryGet(page, "r4", out _));
    }

    [Fact]
    public void CloseContext_DoesNotTouchAnotherContext()    {
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
