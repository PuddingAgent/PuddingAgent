using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;

namespace Pudding.DesktopSurface.BrowserTests;

/// <summary>
/// 切换 Agent 目标页时必须**真的撤销**旧目标上的任务绑定（§4.1）。
/// 否则"用户已经不选定的页"仍持有可执行授权 —— 这是开闸门之前必须先成立的属性。
/// </summary>
public sealed class BrowserTargetSwitchRevokesBindingsTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-1");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(5);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void SwitchingAwayFromTheSelectedPageRevokesItsBindings()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);
        var targets = lifecycle.PageTargets;
        var first = new DesktopPageTarget("ctx-1", "page-1");
        var second = new DesktopPageTarget("ctx-1", "page-2");
        targets.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        targets.RegisterPage(first, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        targets.RegisterPage(second, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        targets.SetAgentTarget(first.ContextId, first.PageId);

        lifecycle.Authority.NotifyConnectionGeneration(Generation);
        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, clock);
        var bindings = new BrowserTaskBindingService(lifecycle, issuer);
        var bridge = new BrowserWorkspaceTargetBridge(lifecycle, bindings);

        var binding = new BrowserTaskBinding(
            Agent, first, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "user-goal:g-1");
        Assert.True(bindings.Accept(binding).Accepted);
        Assert.Single(lifecycle.Authority.ActiveGrants);

        bridge.OnAgentTargetChanged(second.ContextId, second.PageId);

        Assert.Equal(second, targets.AgentTarget);
        Assert.Empty(lifecycle.Authority.ActiveGrants);
        Assert.Equal(0, bindings.ActiveCount);
    }
}