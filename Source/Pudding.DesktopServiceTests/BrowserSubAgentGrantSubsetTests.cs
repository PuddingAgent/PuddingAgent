using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// B6（§5 / I3）：**子代理授权必须是父授权的有效子集**，父授权必须**真实存在**，期限不得超过父授权剩余期限。
/// 判据取自方案：越权 0 调用；期限不超过父授权；无父授权 ⇒ 拒绝。
/// </summary>
public sealed class BrowserSubAgentGrantSubsetTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-test");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-test");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(41);

    private static readonly BrowserCallerIdentity Parent = new(BrowserCallerKind.Agent, "agent-1", "run-parent");
    private static readonly BrowserCallerIdentity Child =
        new(BrowserCallerKind.SubAgent, "agent-1", "run-child", parentTaskId: "run-parent");

    private sealed record Arrangement(
        DesktopService Service, BrowserPageLifecycle Lifecycle, TestTimeProvider Clock);

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
                AllowedCapabilities = DesktopCapability.BrowserBindingApply,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => Generation,
            bindings);

        return new Arrangement(service, lifecycle, clock);
    }

    private static DesktopCallContext Context(string operationId, BrowserCallerIdentity caller) => new(
        DesktopId, new OperationId(operationId), DateTimeOffset.UtcNow.AddSeconds(30), null, caller);

    private static DesktopCapabilityDescriptor Apply() =>
        DesktopCapabilities.TryGetByName("browser.binding.apply", out var d) ? d : throw new InvalidOperationException();

    private static BrowserTaskBinding BindingFor(
        BrowserCallerIdentity task, BrowserPageGrantScope scope, TimeSpan validity) =>
        new(task, ServiceHarness.AgentPage, scope, validity, "user-goal:g-sub");

    private async Task<BrowserBindingAck> ApplyAsync(Arrangement a, BrowserTaskBinding binding, string op) =>
        (await a.Service.ExecuteAsync(Apply(), DesktopCapabilityRequest.ForBindingApply(binding),
            Context(op, binding.Task))).BindingAck!;

    private static void IssueParentGrant(Arrangement a, BrowserPageGrantScope scope, TimeSpan validity)
    {
        var issuedAt = a.Clock.GetUtcNow();
        a.Lifecycle.Authority.IssueGrant(new BrowserPageGrant(
            new BrowserPageGrantId("grant-parent"),
            new BrowserPageGrantBinding(DesktopId, ProcessId, Generation),
            ServiceHarness.AgentPage,
            Parent,
            scope,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.Add(validity)));
    }

    [Fact]
    public async Task ASubAgentWithoutARealParentGrantIsRefused()
    {
        var a = Arrange();

        var ack = await ApplyAsync(a, BindingFor(Child, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5)), "op-sub-no-parent");

        Assert.False(ack.Accepted);
        Assert.Null(ack.GrantId);
        Assert.Contains("parent", ack.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASubAgentAskingForMoreThanTheParentGrantIsRefused()
    {
        var a = Arrange();
        IssueParentGrant(a, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(10)); // 父只有读

        var ack = await ApplyAsync(a, BindingFor(Child, BrowserPageGrantScope.Write, TimeSpan.FromMinutes(1)), "op-sub-overreach");

        Assert.False(ack.Accepted);
        Assert.Null(ack.GrantId);
    }

    [Fact]
    public async Task ASubAgentWithinTheParentGrantIsAccepted()
    {
        var a = Arrange();
        IssueParentGrant(a, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, TimeSpan.FromMinutes(10));

        var ack = await ApplyAsync(a, BindingFor(Child, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5)), "op-sub-ok");

        Assert.True(ack.Accepted, ack.Reason);
        Assert.NotNull(ack.GrantId);
    }

    [Fact]
    public async Task ASubAgentGrantIsClampedToTheParentsRemainingTime()
    {
        var a = Arrange();
        IssueParentGrant(a, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(2)); // 父只 2 分钟

        // 子要 30 分钟：§5 要求"期限不超过父授权" ⇒ 应被**夹取**到父的剩余期限（而非拒绝）。
        var ack = await ApplyAsync(a, BindingFor(Child, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(30)), "op-sub-clamp");

        Assert.True(ack.Accepted, ack.Reason);

        var child = a.Lifecycle.Authority.ActiveGrants.Single(g => g.Grantee.TaskId == "run-child");
        var parent = a.Lifecycle.Authority.ActiveGrants.Single(g => g.Grantee.TaskId == "run-parent");
        Assert.NotNull(child.ExpiresAtUtc);
        Assert.NotNull(parent.ExpiresAtUtc);
        Assert.True(child.ExpiresAtUtc! <= parent.ExpiresAtUtc!,
            $"子授权到期 {child.ExpiresAtUtc} 必须不晚于父授权 {parent.ExpiresAtUtc}");
    }
}
