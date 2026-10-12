using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

// Acceptance regressions injected only by Review.targets. Assertions describe required behavior.
public sealed class BrowserDeliveryReviewService
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
