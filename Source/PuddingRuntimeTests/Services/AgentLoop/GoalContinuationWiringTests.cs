using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 接线测试：Bootstrap（建目标）→ Gate（判定续跑）→ Ledger（记账）串起来能否工作。
/// <para>
/// 为什么要单独测：三个组件各自都有测试，但「它们是否共享同一个注册表」是只有把它们
/// 接起来才会暴露的问题 —— 若不共享，env 生效了也照样不续跑，而且日志里看不出原因。
/// </para>
/// <para>
/// 目标由 <see cref="GoalBootstrap"/> 在 <c>OnLoopCompleteAsync</c> 里建，Gate 在循环内判定，
/// 所以真实场景下是**同一会话的第二次执行**才可能续跑；这里直接按这个顺序编排。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalContinuationWiringTests
{
    [TestInitialize]
    public void Reset()
    {
        GoalContinuationGate.Reset();
        GoalContinuationGate.IsEnabled = () => false;
    }

    [TestCleanup]
    public void Cleanup() => GoalContinuationGate.IsEnabled = () => false;

    /// <summary>env 配好之后：Bootstrap 建出的目标必须能被 Gate 直接看到。</summary>
    [TestMethod]
    public void Goal_created_from_environment_is_visible_to_the_gate()
    {
        var registry = new GoalRegistry();
        GoalContinuationGate.Configure(registry);
        GoalContinuationGate.IsEnabled = () => true;

        var goal = GoalBootstrap.CreateIfConfigured(
            registry,
            "s1",
            () => "接完 goal 链路",
            () => "3");

        Assert.IsNotNull(goal, "配置了 objective 就应该建出目标");
        Assert.AreEqual(GoalPhase.Active, goal!.Phase);
        Assert.AreEqual(GoalActivation.Armed, goal.Activation, "新建目标必须是 armed，否则永远不会自动续跑");

        Assert.IsTrue(GoalContinuationGate.TryContinue("s1", 0, out var goalRound),
            "同一个注册表里的 active+armed 目标必须让 Gate 放行");
        Assert.AreEqual(1, goalRound, "第一次续跑的轮号应为 1");
    }

    /// <summary>没有 objective 时 Gate 必须不放行 —— 这是「默认关时零行为」的接线层证据。</summary>
    [TestMethod]
    public void No_objective_means_the_gate_never_continues()
    {
        var registry = new GoalRegistry();
        GoalContinuationGate.Configure(registry);
        GoalContinuationGate.IsEnabled = () => true;

        var goal = GoalBootstrap.CreateIfConfigured(
            registry,
            "s1",
            () => null,
            () => "3");

        Assert.IsNull(goal);
        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 0, out _));
    }

    /// <summary>
    /// 把 maxRounds 到顶的真实边界钉下来。
    /// <para>
    /// 这个数字很容易记错（我在 goal 里先写成「最多多跑 1 轮」），所以让测试说出实际值：
    /// <c>MaxRounds = N</c> 表示**总共允许 N 轮**，从 RoundsStarted=0 起算即最多放行 N 次。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Max_rounds_bounds_how_many_times_the_gate_admits()
    {
        var registry = new GoalRegistry();
        GoalContinuationGate.Configure(registry);
        GoalContinuationGate.IsEnabled = () => true;

        GoalBootstrap.CreateIfConfigured(registry, "s1", () => "两轮上限", () => "2");

        var admitted = 0;
        while (GoalContinuationGate.TryContinue("s1", admitted, out _))
        {
            admitted++;
            Assert.IsTrue(admitted <= 10, "防止判定失效导致死循环");
        }

        Assert.AreEqual(2, admitted, "MaxRounds=2 应恰好放行 2 次续跑");
        Assert.AreEqual(2, registry.GetLedger("s1")!.RoundsStarted);
        Assert.IsTrue(registry.GetLedger("s1")!.IsExhausted);
    }

    /// <summary>非法 maxRounds 文本必须回落到默认值，而不是把 Gate 卡死或放开。</summary>
    [TestMethod]
    public void Garbage_max_rounds_falls_back_to_the_default()
    {
        var registry = new GoalRegistry();
        GoalContinuationGate.Configure(registry);
        GoalContinuationGate.IsEnabled = () => true;

        var goal = GoalBootstrap.CreateIfConfigured(registry, "s1", () => "目标", () => "not-a-number");

        Assert.IsNotNull(goal);
        Assert.AreEqual(GoalBootstrap.DefaultMaxRounds, goal!.MaxRounds);
    }
}
