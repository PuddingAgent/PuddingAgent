using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 两传输共用的准入适配器（方案 §4.2）：入队前 `Admit`、执行前 `Revalidate`、`finally` `Release`。
/// 判据本身在唯一 authority 里，这里钉住的是**适配器不改变语义**（拒绝仍是拒绝、租约能取到也能释放）。
/// </summary>
public sealed class BrowserAdmissionAdapterTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-1");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(4);
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (BrowserAdmissionAdapter Adapter, BrowserPageLifecycle Lifecycle, DesktopPageTarget Target) Arrange()
    {
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, new FixedClock(Now));
        lifecycle.Authority.NotifyConnectionGeneration(Generation);
        var target = new DesktopPageTarget("ctx-1", "page-1");
        lifecycle.PageTargets.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        lifecycle.PageTargets.RegisterPage(target, new DesktopPageVersion(1), DesktopPageReadiness.Interactive);
        return (new BrowserAdmissionAdapter(lifecycle), lifecycle, target);
    }

    private static void IssueReadGrant(BrowserPageLifecycle lifecycle, DesktopPageTarget target)
    {
        var issuer = new BrowserGrantIssuer(
            lifecycle, DesktopId, ProcessId, () => Generation, new FixedClock(Now));
        Assert.True(
            issuer.TryIssue(
                new BrowserGrantIssuanceRequest(
                    new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"),
                    target,
                    BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
                    TimeSpan.FromMinutes(5),
                    "user-goal:test"),
                out _, out _, out var reason),
            reason);
    }

    [Fact]
    public void AdmitIsRefusedWithoutAGrantAndAcceptedWithOne()
    {
        var (adapter, lifecycle, target) = Arrange();
        var caller = new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1");

        // 没有 grant：读操作也必须被拒（"在 AgentAuthorized 上下文里" ≠ "这个页面已授权"）。
        var refused = adapter.Admit(
            caller, BrowserAutomationOperation.Snapshot, new OperationId("op-1"), Generation, target);
        Assert.False(refused.Accepted);
        Assert.NotNull(refused.Error);
        Assert.NotNull(refused.Reason);

        IssueReadGrant(lifecycle, target);

        var accepted = adapter.Admit(
            caller, BrowserAutomationOperation.Snapshot, new OperationId("op-2"), Generation, target);
        Assert.True(accepted.Accepted, accepted.Reason);
    }

    [Fact]
    public void RevalidateSeesARevokedGrantAndRefuses()
    {
        var (adapter, lifecycle, target) = Arrange();
        var caller = new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1");
        IssueReadGrant(lifecycle, target);

        // 写操作**必须**拿到租约（只读观察没有租约）—— 这本身也是一条契约。
        var admitted = adapter.Admit(
            caller, BrowserAutomationOperation.Interact, new OperationId("op-3"), Generation, target);
        Assert.True(admitted.Accepted, admitted.Reason);
        Assert.NotNull(admitted.Lease);

        // 排队期间被撤权 ⇒ 执行前复检必须拒绝（这正是 Revalidate 存在的理由）。
        lifecycle.Authority.RevokeGrant(lifecycle.Authority.ActiveGrants[0].GrantId);

        var revalidated = adapter.Revalidate(
            admitted.Lease!, caller, BrowserAutomationOperation.Interact, new OperationId("op-3"), Generation, target);
        Assert.False(revalidated.Accepted);
    }

    [Fact]
    public void ReleaseIsSafeForNullAndForRepeatedCalls()
    {
        var (adapter, lifecycle, target) = Arrange();
        var caller = new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1");

        adapter.Release(null); // 只读操作可能没有租约

        IssueReadGrant(lifecycle, target);
        var admitted = adapter.Admit(
            caller, BrowserAutomationOperation.Snapshot, new OperationId("op-4"), Generation, target);
        Assert.True(admitted.Accepted, admitted.Reason);

        adapter.Release(admitted.Lease);
        adapter.Release(admitted.Lease); // 幂等：重复释放不得抛
    }
}
