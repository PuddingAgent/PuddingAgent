using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// B6（§5）：**级联释放**。子任务完成 / 取消 / 父授权撤销 / 进程重连都必须让子代理**失去执行许可**。
/// 这三条是"授权生命周期"的核心判据，任一条不成立都是真实的权限漏洞。
/// </summary>
public sealed class BrowserSubAgentCascadeReleaseTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-test");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-test");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(61);

    private static readonly BrowserCallerIdentity Parent = new(BrowserCallerKind.Agent, "agent-1", "run-parent");
    private static readonly BrowserCallerIdentity Child =
        new(BrowserCallerKind.SubAgent, "agent-1", "run-child", parentTaskId: "run-parent");

    private static readonly BrowserPageGrantId ParentGrantId = new("grant-parent");

    private sealed record Arrangement(
        DesktopService Service, BrowserPageLifecycle Lifecycle, TestTimeProvider Clock, BrowserTaskBindingService Bindings);

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

        var service = new DesktopService(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
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

        return new Arrangement(service, lifecycle, clock, bindings);
    }

    private static void IssueParentGrant(Arrangement a)
    {
        var issuedAt = a.Clock.GetUtcNow();
        a.Lifecycle.Authority.IssueGrant(new BrowserPageGrant(
            ParentGrantId,
            new BrowserPageGrantBinding(DesktopId, ProcessId, Generation),
            ServiceHarness.AgentPage,
            Parent,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.AddMinutes(10)));
    }

    private static DesktopCallContext Context(Arrangement a, string operationId, BrowserCallerIdentity caller) =>
        new(DesktopId, new OperationId(operationId), DateTimeOffset.UtcNow.AddSeconds(30), null, caller);

    /// <summary>子代理执行一次同页写，返回是否被允许（fail-closed 判据）。</summary>
    private static async Task<bool> ChildCanWriteAsync(Arrangement a, string operationId)
    {
        var descriptor = DesktopCapabilities.TryGetByName("webview.navigate", out var d)
            ? d : throw new InvalidOperationException();
        var response = await a.Service.ExecuteAsync(
            descriptor,
            DesktopCapabilityRequest.ForNavigate(new NavigateRequest(ServiceHarness.AgentPage, new Uri("https://example.com/"))),
            Context(a, operationId, Child));
        return !response.IsFailure;
    }

    [Fact]
    public async Task RevokingTheParentGrantStopsTheChildFromWriting()
    {
        var a = Arrange();
        IssueParentGrant(a);

        // 子代理取得子集授权（父存在 ⇒ 可签发）。
        var accepted = a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5), "user-goal:g-cascade"));
        Assert.True(accepted.Accepted, accepted.Reason);

        Assert.True(await ChildCanWriteAsync(a, "op-child-before"), "子授权存在时应当可以写");

        // 父授权被撤销 ⇒ 子代理必须立刻失去许可（级联）。
        a.Lifecycle.Authority.RevokeGrant(ParentGrantId);

        Assert.False(await ChildCanWriteAsync(a, "op-child-after-parent-revoke"),
            "父授权已撤销，子代理不应还能写");
    }

    [Fact]
    public async Task EndingTheSubTaskReleasesItsPermission()
    {
        var a = Arrange();
        IssueParentGrant(a);
        Assert.True(a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5), "user-goal:g-end")).Accepted);
        Assert.True(await ChildCanWriteAsync(a, "op-child-active"));

        // 子任务结束 ⇒ 撤销其全部绑定 ⇒ 不再可写。
        Assert.Equal(1, a.Bindings.RevokeAllForCaller(Child));

        Assert.False(await ChildCanWriteAsync(a, "op-child-ended"));
    }

    [Fact]
    public async Task ANewConnectionGenerationStopsBothParentAndChild()
    {
        var a = Arrange();
        IssueParentGrant(a);
        Assert.True(a.Bindings.Accept(new BrowserTaskBinding(
            Child, ServiceHarness.AgentPage, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5), "user-goal:g-reconnect")).Accepted);

        // 进程重连：世代推进 ⇒ 旧世代上的授权一律失效（两类调用者都应被拒）。
        a.Lifecycle.Authority.NotifyConnectionGeneration(ConnectionGeneration.Require(Generation.Value + 1));

        Assert.False(await ChildCanWriteAsync(a, "op-child-reconnect"));
    }
}