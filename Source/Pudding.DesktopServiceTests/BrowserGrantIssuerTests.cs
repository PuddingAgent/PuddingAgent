using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 授权签发的**生产路径**（方案 §4.1）：这是 <c>IssueGrant</c> 的第一个生产调用方。
/// 每条拒绝都必须给出具体原因，且**绝不**降级成"签发一个差不多的授权"。
/// </summary>
public sealed class BrowserGrantIssuerTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-1");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static BrowserCallerIdentity Agent() => new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (BrowserGrantIssuer Issuer, BrowserPageLifecycle Lifecycle, DesktopPageTarget Target) Arrange(
        ConnectionGeneration? generation = null,
        BrowserGrantIssuanceLimits? limits = null)
    {
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, new FixedClock(Now));
        var target = new DesktopPageTarget("ctx-1", "page-1");
        lifecycle.PageTargets.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        lifecycle.PageTargets.RegisterPage(target, new DesktopPageVersion(1), DesktopPageReadiness.Interactive);

        // authority 自身也要求"有活的连接世代"才肯绑定授权（纵深防御）⇒ 先把手柄告知它。
        lifecycle.Authority.NotifyConnectionGeneration(generation ?? ConnectionGeneration.Require(1));

        var issuer = new BrowserGrantIssuer(
            lifecycle,
            DesktopId,
            ProcessId,
            () => generation ?? ConnectionGeneration.Require(1),
            new FixedClock(Now),
            limits);

        return (issuer, lifecycle, target);
    }

    private static BrowserGrantIssuanceRequest Request(
        DesktopPageTarget target,
        BrowserCallerIdentity? caller = null,
        BrowserPageGrantScope scope = BrowserPageGrantScope.Read,
        TimeSpan? validity = null,
        BrowserPageGrantScope? parentScope = null)
        => new(
            caller ?? Agent(),
            target,
            scope,
            validity ?? TimeSpan.FromMinutes(5),
            Source: "user-goal:test",
            parentScope);

    [Fact]
    public void ATrustedAgentGetsAPageGrantBoundToThisDesktopAndGeneration()
    {
        var (issuer, lifecycle, target) = Arrange();

        Assert.True(issuer.TryIssue(Request(target), out var grant, out var epoch, out var reason), reason);

        Assert.NotNull(grant);
        Assert.Equal(target, grant!.Target);
        Assert.Equal("run-1", grant.Grantee.TaskId);
        Assert.True(grant.Binding.Matches(
            new BrowserPageGrantBinding(DesktopId, ProcessId, ConnectionGeneration.Require(1))));
        Assert.Single(lifecycle.Authority.ActiveGrants);
        Assert.Equal(lifecycle.Authority.ActiveGrants[0].Epoch, lifecycle.Authority.ActiveGrants[0].Epoch);
        Assert.Equal(epoch, lifecycle.Authority.Capture().GrantEpoch);
    }

    [Fact]
    public void AnUnregisteredTargetIsRefused()
    {
        var (issuer, _, _) = Arrange();

        Assert.False(issuer.TryIssue(Request(new DesktopPageTarget("ctx-other", "page-x")), out _, out _, out var reason));
        Assert.Contains("not registered", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidityBeyondTheDesktopLimitIsRefusedInsteadOfBeingClamped()
    {
        var (issuer, _, target) = Arrange(limits: new BrowserGrantIssuanceLimits(TimeSpan.FromMinutes(5)));

        // 调用方不能靠"我要一小时"把授权拉长，也**不能**被静默截断成 5 分钟（必须显式拒绝）。
        Assert.False(issuer.TryIssue(
            Request(target, validity: TimeSpan.FromHours(1)), out _, out _, out var reason));
        Assert.Contains("exceeds the desktop limit", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrNegativeValidityIsRefused()
    {
        var (issuer, _, target) = Arrange();

        Assert.False(issuer.TryIssue(Request(target, validity: TimeSpan.Zero), out _, out _, out var zeroReason));
        Assert.Contains("must be positive", zeroReason, StringComparison.Ordinal);

        Assert.False(issuer.TryIssue(
            Request(target, scope: BrowserPageGrantScope.None), out _, out _, out var scopeReason));
        Assert.Contains("no scope", scopeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubAgentCannotExceedItsParentsScope()
    {
        var (issuer, _, target) = Arrange();
        var subAgent = new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub", "run-1");

        // 父范围只有 Read ⇒ 请求 Write 必须被拒（子代理不得扩大父授权）。
        Assert.False(issuer.TryIssue(
            Request(target, subAgent, BrowserPageGrantScope.Write, parentScope: BrowserPageGrantScope.Read),
            out _, out _, out var widenReason));
        Assert.Contains("subset of its parent", widenReason, StringComparison.Ordinal);

        // 不给父范围同样拒绝（不能靠"没说"默认放宽）。
        Assert.False(issuer.TryIssue(
            Request(target, subAgent, BrowserPageGrantScope.Read), out _, out _, out var missingReason));
        Assert.Contains("subset of its parent", missingReason, StringComparison.Ordinal);

        // 子集要能签发，**前提是父任务在同一页上真的有授权**（authority 会核对实际父授权，
        // 比我这里的参数检查更强："not a subset of its parent task's grant (no_grant)"）。
        Assert.True(issuer.TryIssue(
            Request(target, scope: BrowserPageGrantScope.Read | BrowserPageGrantScope.Write),
            out _, out _, out var parentReason), parentReason);

        Assert.True(issuer.TryIssue(
            Request(target, subAgent, BrowserPageGrantScope.Read,
                parentScope: BrowserPageGrantScope.Read | BrowserPageGrantScope.Write),
            out _, out _, out var okReason), okReason);
    }

    [Fact]
    public void ADeadConnectionGenerationIsRefused()
    {
        // 连接不活时签发的授权**无法被后续校验**（generation 已换代）⇒ 拒绝，而不是先签了再说。
        var (issuer, _, target) = Arrange(generation: ConnectionGeneration.None);

        Assert.False(issuer.TryIssue(Request(target), out _, out _, out var reason));
        Assert.Contains("not live", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserCallerCannotBeUsedToIssueAnAutomationGrant()
    {
        var (issuer, _, target) = Arrange();
        var user = new BrowserCallerIdentity(BrowserCallerKind.User, "desktop-ui", "run-1");

        Assert.False(issuer.TryIssue(Request(target, user), out _, out _, out var reason));
        Assert.Contains("User caller", reason, StringComparison.Ordinal);
    }
}
