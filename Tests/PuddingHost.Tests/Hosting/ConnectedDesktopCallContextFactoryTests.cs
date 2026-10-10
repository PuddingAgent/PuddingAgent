using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.Hosting;
using PuddingBrowser.AgentTools;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 能力调用上下文工厂：工具侧只有业务参数，实例 ID / 操作 ID / 期限都必须由这里产生。
/// 关键判据是**没有活动 Desktop 时不产生上下文**——宁可让调用方明确失败，也不要猜一个实例 ID
/// 去发一条注定被会话拒绝的命令。
/// </summary>
public sealed class ConnectedDesktopCallContextFactoryTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutActiveDesktop_ReturnsNullInsteadOfGuessingAnInstanceId()
    {
        var factory = new ConnectedDesktopCallContextFactory(() => null, new FixedClock(Now));

        Assert.Null(factory.TryCreate());
    }

    [Fact]
    public void TheCallerIdentityComesFromTheCallScopeAndIsNeverFabricated()
    {
        // §3.1：Host 的 context factory 从调用级作用域取身份；没有身份就是**没有**（不生成默认 Agent/任务）。
        var probe = new RecordingAccessor();
        var factory = new ConnectedDesktopCallContextFactory(() => DesktopId, new FixedClock(Now), callerIdentity: probe);

        Assert.Null(factory.TryCreate()!.BrowserCaller);

        using (BrowserCallerIdentityScope.Push(
            new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"), null))
        {
            var withIdentity = factory.TryCreate();
            Assert.Equal("run-1", withIdentity!.BrowserCaller!.TaskId);
            Assert.Equal(BrowserCallerKind.Agent, withIdentity.BrowserCaller.Kind);
        }

        // 作用域退出后不得残留（否则下一次调用会借用上一次的身份）。
        Assert.Null(factory.TryCreate()!.BrowserCaller);
    }

    [Fact]
    public void ASubAgentCarriesItsParentTaskThroughTheContext()
    {
        var factory = new ConnectedDesktopCallContextFactory(
            () => DesktopId, new FixedClock(Now), callerIdentity: new BrowserCallerIdentityAccessor());

        using (BrowserCallerIdentityScope.Push(
            new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub", "run-parent"), null))
        {
            var call = factory.TryCreate();
            Assert.Equal("run-parent", call!.BrowserCaller!.ParentTaskId);
        }
    }

    [Fact]
    public void ARefusedProjectionLeavesTheContextWithoutACallerInsteadOfADefaultOne()
    {
        // 投影失败时作用域里只有"原因"、没有身份 ⇒ 上下文必须**没有** caller（浏览器能力因此被拒）。
        var factory = new ConnectedDesktopCallContextFactory(
            () => DesktopId, new FixedClock(Now), callerIdentity: new BrowserCallerIdentityAccessor());

        using (BrowserCallerIdentityScope.Push(null, "no trusted run identity"))
        {
            Assert.Null(factory.TryCreate()!.BrowserCaller);
        }
    }

    private sealed class RecordingAccessor : IBrowserCallerIdentityAccessor
    {
        public BrowserCallerIdentity? Current => BrowserCallerIdentityScope.Current;

        public string? CurrentFailureReason => BrowserCallerIdentityScope.CurrentFailureReason;
    }
    [Fact]
    public void WithActiveDesktop_UsesTheSessionIdAndAFreshOperationIdPerCall()
    {
        var factory = new ConnectedDesktopCallContextFactory(() => DesktopId, new FixedClock(Now));

        var first = factory.TryCreate();
        var second = factory.TryCreate();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(DesktopId, first!.DesktopId);
        // 每次调用都要新操作 ID：同 ID 不同 payload 会被 broker 判为重复请求而拒绝。
        Assert.NotEqual(first.OperationId.Value, second!.OperationId.Value);
    }

    [Fact]
    public void Deadline_UsesTheRequestedBudgetElseTheDefault()
    {
        var factory = new ConnectedDesktopCallContextFactory(() => DesktopId, new FixedClock(Now));

        var byDefault = factory.TryCreate();
        var explicitBudget = factory.TryCreate(TimeSpan.FromSeconds(5));

        Assert.Equal(Now + ConnectedDesktopCallContextFactory.DefaultCallTimeout, byDefault!.DeadlineUtc);
        Assert.Equal(Now + TimeSpan.FromSeconds(5), explicitBudget!.DeadlineUtc);
    }

    [Fact]
    public void Deadline_UsesTheConfiguredDefaultWhenProvided()
    {
        var factory = new ConnectedDesktopCallContextFactory(
            () => DesktopId, new FixedClock(Now), defaultTimeout: TimeSpan.FromSeconds(30));

        Assert.Equal(Now + TimeSpan.FromSeconds(30), factory.TryCreate()!.DeadlineUtc);
    }

    [Fact]
    public void Constructor_RejectsMissingResolver()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectedDesktopCallContextFactory(null!));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
