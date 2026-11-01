using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// Autodream 触发策略：该触发的一定要触发，不该触发的一个都不能触发。
/// <para>
/// 两个方向都要测：只测"会触发"会让策略退化成永远 true（后台预算被烧光）；
/// 只测"不触发"会让它退化成永远 false（等于没做）。
/// </para>
/// </summary>
[TestClass]
public sealed class MemoryDreamPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [DataTestMethod]
    [DataRow(AgentLoopStopReason.Done)]
    [DataRow(AgentLoopStopReason.MaxRoundsReached)]
    public void Productive_endings_should_dream(AgentLoopStopReason reason)
    {
        var policy = MemoryDreamPolicy.Default;
        Assert.IsTrue(policy.ShouldDream(reason, null, Now, 0), $"结束原因 {reason} 应触发整理");
    }

    [DataTestMethod]
    [DataRow(AgentLoopStopReason.Cancelled)]
    [DataRow(AgentLoopStopReason.MaxElapsedReached)]
    [DataRow(AgentLoopStopReason.BudgetExhausted)]
    [DataRow(AgentLoopStopReason.Waiting)]
    [DataRow(AgentLoopStopReason.Failed)]
    public void Non_productive_endings_should_not_dream(AgentLoopStopReason reason)
    {
        var policy = MemoryDreamPolicy.Default;
        Assert.IsFalse(policy.ShouldDream(reason, null, Now, 0), $"结束原因 {reason} 不应触发整理");
    }

    [TestMethod]
    public void Cooldown_window_blocks_second_dream()
    {
        var policy = MemoryDreamPolicy.Default;   // 冷却 30 分钟

        Assert.IsFalse(
            policy.ShouldDream(AgentLoopStopReason.Done, Now.AddMinutes(-10), Now, 1),
            "距上次仅 10 分钟，应被冷却窗口挡住");

        Assert.IsTrue(
            policy.ShouldDream(AgentLoopStopReason.Done, Now.AddMinutes(-31), Now, 1),
            "超过冷却窗口后应允许");
    }

    [TestMethod]
    public void Per_session_quota_is_enforced()
    {
        var policy = MemoryDreamPolicy.Default;   // 上限 3 次

        Assert.IsTrue(policy.ShouldDream(AgentLoopStopReason.Done, null, Now, 2));
        Assert.IsFalse(policy.ShouldDream(AgentLoopStopReason.Done, null, Now, 3),
            "达到每会话上限后必须停止，防止反复整理烧预算");
    }

    [TestMethod]
    public void Skip_on_failure_can_be_disabled()
    {
        var lenient = MemoryDreamPolicy.Default with { SkipOnFailure = false };

        Assert.IsTrue(lenient.ShouldDream(AgentLoopStopReason.Failed, null, Now, 0),
            "显式要求时，失败结束也应允许整理（用于排查场景）");
    }
}

/// <summary>
/// 钩子本身：关闭时必须完全惰性；开启时只在策略放行后投递一次。
/// </summary>
[TestClass]
public sealed class MemoryDreamSchedulerHookTests
{
    private sealed class CountingExecutor : IMemoryDreamExecutor
    {
        public int Calls { get; private set; }
        public MemoryDreamRequest? Last { get; private set; }

        public Task RunAsync(MemoryDreamRequest request, CancellationToken ct = default)
        {
            Calls++;
            Last = request;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AgentLoopContext Ctx() => new()
    {
        SessionId = "s1",
        AgentInstanceId = "a1",
        WorkspaceId = "w1",
        AgentTemplateId = "t1",
        UserMessage = "hi",
        MaxRounds = 10,
    };

    [TestMethod]
    public async Task Disabled_by_default_so_hook_is_inert()
    {
        // 环境变量未设置时 Enabled=false；这里直接断言静态开关的语义，避免依赖宿主环境。
        if (MemoryDreamSchedulerHook.Enabled)
            Assert.Inconclusive("本机设置了 PUDDING_MEMORY_DREAM=1，跳过惰性断言");

        var executor = new CountingExecutor();
        var hook = new MemoryDreamSchedulerHook(executor: executor);

        await hook.OnLoopCompleteAsync(Ctx(), "done", AgentLoopStopReason.Done);

        Assert.AreEqual(0, executor.Calls, "开关关闭时不得投递任何整理任务");
        Assert.AreEqual(0, hook.DispatchCount);
    }

    [TestMethod]
    public async Task Without_executor_nothing_happens()
    {
        var hook = new MemoryDreamSchedulerHook(executor: null);

        await hook.OnLoopCompleteAsync(Ctx(), "done", AgentLoopStopReason.Done);

        Assert.AreEqual(0, hook.DispatchCount);
    }
}
