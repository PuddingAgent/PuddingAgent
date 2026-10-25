using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 控制面**端到端**（方案 §4.1）：线缆载荷 → `DesktopService` 分支 → `BrowserTaskBindingService`
/// → 签发/撤销 → ack。判据是"ack 里有没有 grant"与"拒绝原因是否可区分"，不是"看日志"。
///
/// 目标判据自 2026-10-25 起是三条分支（用户裁定）：已显式选定 / AgentAuthorized 上下文默认接管 /
/// 其余拒绝。因此"未选页"不再等价于"拒绝"——拒绝只发生在**非 AgentAuthorized 上下文**
/// 或**默认过又被显式撤销**之后。这两条各有一个用例，避免默认化悄悄外溢。
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

    private static Arrangement Arrange(
        bool selectAgentTarget,
        DesktopContextTrust contextTrust = DesktopContextTrust.AgentAuthorized)
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);
        var targets = lifecycle.PageTargets;
        // 必须走 lifecycle 的组合登记：它同时写入页面注册表与浏览器目标注册表。
        // 只写页面注册表时 TrustFor 看不到上下文，默认化分支永远不触发（本轮就是被这条测出来的）。
        lifecycle.RegisterContext("ctx-agent", contextTrust);
        lifecycle.RegisterPage(ServiceHarness.AgentPage, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
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

    private static Task<DesktopCapabilityResponse> ApplyAsync(Arrangement harness, string operationId) =>
        harness.Service.ExecuteAsync(
            Descriptor("browser.binding.apply"),
            DesktopCapabilityRequest.ForBindingApply(Binding()),
            Context(operationId));

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
    public async Task ApplyingABindingWithoutAnExplicitSelectionInAnAgentAuthorizedContextDefaultsToThePage()
    {
        // 面板里有页面、但没人点过「设为 Agent 目标」——旧行为是这里失败，
        // 工具侧只看到 "no page grant covers ..."，无法分辨是没授权还是页面不存在。
        var harness = Arrange(selectAgentTarget: false);

        var response = await ApplyAsync(harness, "op-apply-default");

        Assert.True(response.BindingAck!.Accepted, response.BindingAck.Reason);
        Assert.NotNull(response.BindingAck.GrantId);
        Assert.Single(harness.Lifecycle.Authority.ActiveGrants);

        // 注册表必须被对齐：否则 UI 说"没有目标页"、准入说"这就是目标页"，是最难查的漂移。
        Assert.Equal(ServiceHarness.AgentPage, harness.Targets.AgentTarget);
    }

    [Fact]
    public async Task ApplyingABindingInANonAgentAuthorizedContextIsRefusedEvenWithoutSelection()
    {
        // 默认化**不得外溢**：用户自己的工作台/第三方页面，没显式选给 Agent 就是不给。
        var harness = Arrange(selectAgentTarget: false, contextTrust: DesktopContextTrust.Untrusted);

        var response = await ApplyAsync(harness, "op-apply-denied");

        Assert.NotNull(response.BindingAck);
        Assert.False(response.BindingAck!.Accepted);
        // 原因必须**可区分**：不是笼统的"拒绝"，而是"这一页不是用户当前选定给 Agent 的页"。
        Assert.Contains("not the page the user currently selected", response.BindingAck.Reason!, StringComparison.Ordinal);
        Assert.Null(response.BindingAck.GrantId);
        Assert.Empty(harness.Lifecycle.Authority.ActiveGrants);
    }

    [Fact]
    public async Task ReleasingADefaultedTargetDoesNotBlockALaterBinding()
    {
        // 语义变更（用户裁定 2026-10-25）：移除「设为 Agent 目标」开关后，默认化按需重入。
        //
        // 原用例断言的是「默认只做一次、撤销后不再恢复」。那条规则只在开关存在时才有意义
        // （防止开关骗人）；开关移除后它反而制造新的假故障：短期授权到期后无法再次申请，
        // 页面会在使用中突然不可驱动。因此本用例改断「释放不会永久堵死后续绑定」，
        // 并且仍然断「判据是上下文可信级，不是任意页面」。
        var harness = Arrange(selectAgentTarget: false);
        Assert.True((await ApplyAsync(harness, "op-default")).BindingAck!.Accepted);

        harness.Lifecycle.SetAgentTarget(null, null);

        var second = await ApplyAsync(harness, "op-after-release");

        Assert.True(second.BindingAck!.Accepted, second.BindingAck.Reason);
        Assert.NotNull(second.BindingAck.GrantId);
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
