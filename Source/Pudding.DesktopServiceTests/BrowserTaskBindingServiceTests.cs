using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>任务绑定受理（§4.1）：**绑定不能替代用户选择**；受理后授权存在，撤销后消失。</summary>
public sealed class BrowserTaskBindingServiceTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-1");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(3);
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (BrowserTaskBindingService Service, BrowserPageLifecycle Lifecycle) Arrange(bool selectForAgent)
    {
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, new FixedClock(Now));
        lifecycle.Authority.NotifyConnectionGeneration(Generation);
        var page = new DesktopPageTarget("ctx-1", "page-1");
        lifecycle.PageTargets.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        lifecycle.PageTargets.RegisterPage(page, new DesktopPageVersion(1), DesktopPageReadiness.Interactive);
        if (selectForAgent)
        {
            lifecycle.PageTargets.SetAgentTarget("ctx-1", "page-1");
        }

        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, new FixedClock(Now));
        return (new BrowserTaskBindingService(lifecycle, issuer), lifecycle);
    }

    private static BrowserTaskBinding Binding() => new(
        new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"),
        new DesktopPageTarget("ctx-1", "page-1"),
        BrowserPageGrantScope.Read,
        TimeSpan.FromMinutes(5),
        "user-goal:g-1");

    [Fact]
    public void ABindingForAPageTheUserDidNotSelectIsRefused()
    {
        var (service, lifecycle) = Arrange(selectForAgent: false);

        var result = service.Accept(Binding());

        Assert.False(result.Accepted);
        Assert.Contains("not the page the user currently selected", result.Reason!, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Authority.ActiveGrants);
        Assert.Equal(0, service.ActiveCount);
    }

    [Fact]
    public void ABindingForTheSelectedTargetIsIssuedAndCanBeRevoked()
    {
        var (service, lifecycle) = Arrange(selectForAgent: true);

        var accepted = service.Accept(Binding());

        Assert.True(accepted.Accepted, accepted.Reason);
        Assert.Single(lifecycle.Authority.ActiveGrants);
        Assert.Equal(1, service.ActiveCount);

        Assert.True(service.Revoke(Binding()));
        Assert.Empty(lifecycle.Authority.ActiveGrants);
        Assert.False(service.Revoke(Binding())); // 幂等：再撤返回 false，不抛
    }

    [Fact]
    public void ReacceptingTheSameBindingDoesNotLeaveTwoGrants()
    {
        var (service, lifecycle) = Arrange(selectForAgent: true);

        Assert.True(service.Accept(Binding()).Accepted);
        Assert.True(service.Accept(Binding()).Accepted);

        Assert.Single(lifecycle.Authority.ActiveGrants);
        Assert.Equal(1, service.ActiveCount);
    }
    [Fact]
    public void SwitchingAwayFromTheTargetRevokesItsBindings()
    {
        var (service, lifecycle) = Arrange(selectForAgent: true);
        Assert.True(service.Accept(Binding()).Accepted);

        Assert.Equal(1, service.RevokeForTarget(new DesktopPageTarget("ctx-1", "page-1")));

        Assert.Empty(lifecycle.Authority.ActiveGrants);
        Assert.Equal(0, service.ActiveCount);
        Assert.Equal(0, service.RevokeForTarget(new DesktopPageTarget("ctx-1", "page-1"))); // 幂等
    }

    [Fact]
    public void EndingTheTaskRevokesItsBindingsAndLeavesOthersAlone()
    {
        var (service, lifecycle) = Arrange(selectForAgent: true);
        Assert.True(service.Accept(Binding()).Accepted);

        Assert.Equal(0, service.RevokeAllForCaller("agent-other", "run-9")); // 不相干的任务不动
        Assert.Single(lifecycle.Authority.ActiveGrants);

        Assert.Equal(1, service.RevokeAllForCaller("agent-1", "run-1"));

        Assert.Empty(lifecycle.Authority.ActiveGrants);
        Assert.Equal(0, service.ActiveCount);
    }
}