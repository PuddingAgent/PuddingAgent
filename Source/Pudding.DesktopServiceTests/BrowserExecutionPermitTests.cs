using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// **执行许可的不变量套件**（2026-10-12 由外部复核反例迁入常规测试）。
///
/// 四条断言的都是"准入一次、复检原许可、全路径释放、选中页不被无关任务共享"：
/// ① 排队期间"接管→恢复"不得让旧操作复活；② 入队前取消不得泄漏已签发的租约；
/// ③ 版本拒绝不得泄漏租约；④ 选中页不得被无关任务共享（共享需独立显式许可）。
///
/// 原反例文件仍保留在 <c>TestScripts/BrowserDeliveryReview/ServiceReview.cs</c>（外部复核证据与重演路径）；
/// 本文件是它在**默认构建**中的常驻版本。
/// </summary>
public sealed class BrowserExecutionPermitTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(33);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-a", "run-a");

    private static ServiceHarness Create(bool immediate = true)
    {
        var h = ServiceHarness.Create(allowed: DesktopCapability.WebViewNavigate,
            hasThreadAccess: immediate, connectionGeneration: () => Generation);
        h.Service.Authority.NotifyConnectionGeneration(Generation);
        var now = h.Clock.GetUtcNow();
        h.Service.Authority.IssueGrant(new BrowserPageGrant(new BrowserPageGrantId("review-grant"),
            new BrowserPageGrantBinding(new DesktopInstanceId("desk-test"),
                new DesktopProcessInstanceId("proc-test"), Generation),
            ServiceHarness.AgentPage, Agent, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            default, now, expiresAtUtc: now.AddMinutes(5)));
        return h;
    }

    [Fact]
    public async Task TakeoverThenResumeWhileQueuedMustNotReviveTheOldOperation()
    {
        var h = Create(immediate: false);
        var pending = h.ExecuteAsync(DesktopCapability.WebViewNavigate,
            h.NavigateRequest(ServiceHarness.AgentPage), h.Context("review-resume") with { BrowserCaller = Agent });
        Assert.False(pending.IsCompleted);
        Assert.NotNull(h.Service.Authority.Capture().ActiveLease);
        h.Service.Authority.SetUserTakeover(true, "review takeover");
        h.Service.Authority.SetUserTakeover(false, "review resume");
        h.Dispatcher.Pump();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.IsFailure, $"old request resumed; driver calls={h.Surface.NavigateCount}");
        Assert.Equal(0, h.Surface.NavigateCount);
        Assert.Null(h.Service.Authority.Capture().ActiveLease);
    }

    [Fact]
    public async Task CancellationBeforeQueueMustNotLeakTheAdmittedLease()
    {
        var h = Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var response = await h.ExecuteAsync(DesktopCapability.WebViewNavigate,
            h.NavigateRequest(ServiceHarness.AgentPage), h.Context("review-cancel") with { BrowserCaller = Agent },
            cancelled.Token);
        Assert.True(response.IsFailure);
        Assert.Equal(0, h.Surface.NavigateCount);
        Assert.Null(h.Service.Authority.Capture().ActiveLease);
    }

    [Fact]
    public async Task VersionRejectionMustNotLeakTheAdmittedLease()
    {
        var h = Create();
        var response = await h.ExecuteAsync(DesktopCapability.WebViewNavigate,
            h.NavigateRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(999)),
            h.Context("review-version") with { BrowserCaller = Agent });
        Assert.True(response.IsFailure);
        Assert.Equal(0, h.Surface.NavigateCount);
        Assert.Null(h.Service.Authority.Capture().ActiveLease);
    }

    [Fact]
    public void SelectingAPageMustNotAuthorizeEveryUnrelatedTask()
    {
        var desk = new DesktopInstanceId("review-desk");
        var process = new DesktopProcessInstanceId("review-process");
        var lifecycle = BrowserPageLifecycle.Create(desk, process);
        lifecycle.Authority.NotifyConnectionGeneration(Generation);
        var page = new DesktopPageTarget("review-context", "review-page");
        lifecycle.PageTargets.RegisterContext(page.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.PageTargets.RegisterPage(page, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        lifecycle.PageTargets.SetAgentTarget(page.ContextId, page.PageId);
        var service = new BrowserTaskBindingService(lifecycle,
            new BrowserGrantIssuer(lifecycle, desk, process, () => Generation));
        var first = service.Accept(new BrowserTaskBinding(Agent, page,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, TimeSpan.FromMinutes(5), "review"));
        Assert.True(first.Accepted);
        var stranger = new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-b", "run-b");
        var second = service.Accept(new BrowserTaskBinding(stranger, page,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, TimeSpan.FromMinutes(5), "review"));
        Assert.False(second.Accepted, "unrelated task acquired the same selected page without a user task binding");
    }
}
