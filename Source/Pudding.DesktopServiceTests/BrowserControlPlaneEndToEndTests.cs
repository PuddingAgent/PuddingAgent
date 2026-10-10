using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 控制面**端到端**（方案 §4.1）：线缆载荷 → `DesktopService` 分支 → `BrowserTaskBindingService`
/// → 签发/撤销 → ack。判据是"ack 里有没有 grant"与"拒绝原因是否可区分"，不是"看日志"。
/// </summary>
public sealed class BrowserControlPlaneEndToEndTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-test");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-test");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(11);

    private static readonly BrowserCallerIdentity Agent =
        new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private sealed record Arrangement(
        DesktopService Service,
        BrowserPageLifecycle Lifecycle,
        DesktopTargetRegistry Targets,
        TestTimeProvider Clock);

    private static Arrangement Arrange(bool selectAgentTarget)
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);
        var targets = lifecycle.PageTargets;
        targets.RegisterContext("ctx-agent", DesktopContextTrust.AgentAuthorized);
        targets.RegisterPage(ServiceHarness.AgentPage, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        if (selectAgentTarget)
        {
            targets.SetAgentTarget(ServiceHarness.AgentPage.ContextId, ServiceHarness.AgentPage.PageId);
        }

        lifecycle.Authority.NotifyConnectionGeneration(Generation);
        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, clock);
        var bindings = new BrowserTaskBindingService(lifecycle, issuer);

        var service = new DesktopService(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            targets,
            new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserBindingApply | DesktopCapability.BrowserBindingRevoke,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => Generation,
            bindings);

        return new Arrangement(service, lifecycle, targets, clock);
    }

    private static DesktopCallContext Context(string operationId) => new(
        DesktopId,
        new OperationId(operationId),
        DateTimeOffset.UtcNow.AddSeconds(30),
        correlationId: null,
        browserCaller: Agent);

    private static BrowserTaskBinding Binding() => new(
        Agent,
        ServiceHarness.AgentPage,
        BrowserPageGrantScope.Read,
        TimeSpan.FromMinutes(5),
        "user-goal:g-1");

    private static DesktopCapabilityDescriptor Descriptor(string name)
    {
        Assert.True(DesktopCapabilities.TryGetByName(name, out var descriptor));
        return descriptor;
    }

    [Fact]
    public async Task ApplyingABindingForTheUserSelectedPageIsAcknowledgedWithAGrant()
    {
        var harness = Arrange(selectAgentTarget: true);

        var response = await harness.Service.ExecuteAsync(
            Descriptor("browser.binding.apply"),
            DesktopCapabilityRequest.ForBindingApply(Binding()),
            Context("op-apply"));

        Assert.NotNull(response.BindingAck);
        Assert.True(response.BindingAck!.Accepted, response.BindingAck.Reason);
        Assert.NotNull(response.BindingAck.GrantId);
        Assert.Single(harness.Lifecycle.Authority.ActiveGrants);
    }

    [Fact]
    public async Task ApplyingABindingForAPageTheUserDidNotSelectIsAcknowledgedAsRefusedWithAReason()
    {
        var harness = Arrange(selectAgentTarget: false);

        var response = await harness.Service.ExecuteAsync(
            Descriptor("browser.binding.apply"),
            DesktopCapabilityRequest.ForBindingApply(Binding()),
            Context("op-apply-denied"));

        Assert.NotNull(response.BindingAck);
        Assert.False(response.BindingAck!.Accepted);
        // 原因必须**可区分**：不是笼统的"拒绝"，而是"这一页不是用户当前选定给 Agent 的页"。
        Assert.Contains("not the page the user currently selected", response.BindingAck.Reason!, StringComparison.Ordinal);
        Assert.Null(response.BindingAck.GrantId);
        Assert.Empty(harness.Lifecycle.Authority.ActiveGrants);
    }

    [Fact]
    public async Task RevokingIsAcknowledgedAndIdempotent()
    {
        var harness = Arrange(selectAgentTarget: true);
        Assert.True((await harness.Service.ExecuteAsync(
            Descriptor("browser.binding.apply"),
            DesktopCapabilityRequest.ForBindingApply(Binding()),
            Context("op-apply"))).BindingAck!.Accepted);

        var revoke = DesktopCapabilityRequest.ForBindingRevoke(Agent, ServiceHarness.AgentPage);

        var first = await harness.Service.ExecuteAsync(
            Descriptor("browser.binding.revoke"), revoke, Context("op-revoke-1"));
        Assert.True(first.BindingAck!.Accepted, first.BindingAck.Reason);
        Assert.Empty(harness.Lifecycle.Authority.ActiveGrants);

        // 幂等：再撤一次不抛，且给可区分原因。
        var second = await harness.Service.ExecuteAsync(
            Descriptor("browser.binding.revoke"), revoke, Context("op-revoke-2"));
        Assert.False(second.BindingAck!.Accepted);
        Assert.Contains("no binding is active", second.BindingAck.Reason!, StringComparison.Ordinal);
    }
}
