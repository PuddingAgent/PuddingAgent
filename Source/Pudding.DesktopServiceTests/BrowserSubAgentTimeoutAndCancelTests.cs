using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// B6（§5）级联释放的另外两条路径：**超时**（授权到期 ⇒ 子代理失去许可）与**取消**（不产生驱动调用；在途取消也要释放租约）。
/// </summary>
public sealed class BrowserSubAgentTimeoutAndCancelTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-test");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-test");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(71);

    private static readonly BrowserCallerIdentity Parent = new(BrowserCallerKind.Agent, "agent-1", "run-parent");
    private static readonly BrowserCallerIdentity Child =
        new(BrowserCallerKind.SubAgent, "agent-1", "run-child", parentTaskId: "run-parent");

    private sealed record Arrangement(
        DesktopService Service, BrowserPageLifecycle Lifecycle, TestTimeProvider Clock,
        BrowserTaskBindingService Bindings, RecordingUiSurface Surface);

    private static Arrangement Arrange()
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);
        var targets = lifecycle.PageTargets;
        targets.RegisterContext("ctx-agent", DesktopContextTrust.AgentAuthorized);
        targets.RegisterPage(ServiceHarness.AgentPage, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        targets.SetAgentTarget(ServiceHarness.AgentPage.ContextId, ServiceHarness.AgentPage.PageId);
        lifecycle.Authority.NotifyConnectionGeneration(Generation);

        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, clock);
        var bindings = new BrowserTaskBindingService(lifecycle, issuer, clock);
        var surface = new RecordingUiSurface();

        var service = new DesktopService(
            new ManualUiDispatcher { HasThreadAccess = true },
            surface,
            targets,
            new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserBindingApply | DesktopCapability.WebViewNavigate,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => Generation,
            bindings);

        return new Arrangement(service, lifecycle, clock, bindings, surface);
    }

    private static void IssueParentGrant(Arrangement a, TimeSpan validity)
    {
        var issuedAt = a.Clock.GetUtcNow();
        a.Lifecycle.Authority.IssueGrant(new BrowserPageGrant(
            new BrowserPageGrantId("grant-parent"),
            new BrowserPageGrantBinding(DesktopId, ProcessId, Generation),
            ServiceHarness.AgentPage,
            Parent,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.Add(validity)));
    }

    private static DesktopCapabilityDescriptor Navigate() =>
        DesktopCapabilities.TryGetByName("webview.navigate", out var d) ? d : throw new InvalidOperationException();

    private static DesktopCapabilityRequest NavigateRequest() =>
        DesktopCapabilityRequest.ForNavigate(
            new NavigateRequest(ServiceHarness.AgentPage, new Uri("https://example.com/")));

    private static DesktopCallContext Context(Arrangement a, string operationId, BrowserCallerIdentity caller,
        CancellationToken token = default) => new(
        DesktopId, new OperationId(operationId), DateTimeOffset.UtcNow.AddMinutes(5), null, caller);

    [Fact]
    public async Task AnExpiredGrantStopsTheChildFromWriting()
    {
        var a = Arrange();
        IssueParentGrant(a, TimeSpan.FromMinutes(2));

        var accepted = a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(1), "user-goal:g-timeout"));
        Assert.True(accepted.Accepted, accepted.Reason);

        // 父与子授权都已到期：推进时钟越过两者的到期时刻。
        a.Clock.Advance(TimeSpan.FromMinutes(5));

        var response = await a.Service.ExecuteAsync(
            Navigate(), NavigateRequest(), Context(a, "op-after-expiry", Child));

        Assert.True(response.IsFailure, "授权到期后子代理不应还能写");
        Assert.Equal(0, a.Surface.NavigateCount);
    }

    [Fact]
    public async Task APreCancelledWriteNeverReachesTheDriver()
    {
        var a = Arrange();
        IssueParentGrant(a, TimeSpan.FromMinutes(10));
        Assert.True(a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5), "user-goal:g-cancel")).Accepted);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await a.Service.ExecuteAsync(
            Navigate(), NavigateRequest(), Context(a, "op-pre-cancelled", Child), cts.Token);

        Assert.True(response.IsFailure);
        Assert.Equal(0, a.Surface.NavigateCount);
    }

    [Fact]
    public async Task CancellingWhileInFlightStillReleasesTheLease()
    {
        var a = Arrange();
        IssueParentGrant(a, TimeSpan.FromMinutes(10));
        Assert.True(a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5), "user-goal:g-cancel-mid")).Accepted);

        using var cts = new CancellationTokenSource();
        a.Surface.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = a.Service.ExecuteAsync(
            Navigate(), NavigateRequest(), Context(a, "op-in-flight", Child), cts.Token);

        var waited = 0;
        while (a.Surface.NavigateCount < 1 && waited < 200)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Equal(1, a.Surface.NavigateCount);
        Assert.NotNull(a.Lifecycle.Authority.Capture().ActiveLease); // 在途 ⇒ 租约在册

        cts.Cancel(); // 在途取消：surface 的等待会因 token 抛出 ⇒ 操作结束 ⇒ finally 必须释放租约

        var response = await inFlight;
        Assert.True(response.IsFailure);
        Assert.Null(a.Lifecycle.Authority.Capture().ActiveLease);
    }
}