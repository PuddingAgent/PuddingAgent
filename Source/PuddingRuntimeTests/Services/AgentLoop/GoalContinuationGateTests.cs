using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 主干侧唯一判定点：模型说完成之后要不要再来一轮。
/// <para>
/// 两个方向都必须测：**关闭时一定不续跑**（等价性），**开启时条件齐备才续跑**（功能）。
/// 只测前者等于没做功能，只测后者等于没做等价。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalContinuationGateTests
{
    [TestInitialize]
    public void Reset()
    {
        GoalContinuationGate.Reset();
        GoalContinuationGate.IsEnabled = () => false;
    }

    [TestCleanup]
    public void Cleanup() => GoalContinuationGate.IsEnabled = () => false;

    private static GoalRegistry RegistryWithGoal(int maxRounds = 5)
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "接完 goal 链路", maxRounds);
        GoalContinuationGate.Configure(registry);
        return registry;
    }

    [TestMethod]
    public void Disabled_never_continues_even_with_an_active_goal()
    {
        RegistryWithGoal();

        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 0, out _),
            "开关关着时行为必须与改动前等价");
    }

    [TestMethod]
    public void Enabled_but_not_configured_never_continues()
    {
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 0, out _));
        Assert.IsFalse(GoalContinuationGate.IsConfigured);
    }

    [TestMethod]
    public void Enabled_without_a_goal_does_not_continue()
    {
        var registry = new GoalRegistry();
        GoalContinuationGate.Configure(registry);
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 0, out _));
    }

    [TestMethod]
    public void Enabled_with_active_armed_goal_continues_and_consumes_one_round()
    {
        var registry = RegistryWithGoal();
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsTrue(GoalContinuationGate.TryContinue("s1", 0, out var goalRound));
        Assert.AreEqual(1, goalRound, "第一次续跑的轮号应为 1");
        Assert.AreEqual(1, registry.GetLedger("s1")!.RoundsStarted, "准入必须真的消耗轮次");
    }

    [TestMethod]
    public void Spent_budget_stops_continuation()
    {
        var registry = RegistryWithGoal(maxRounds: 1);
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsTrue(GoalContinuationGate.TryContinue("s1", 0, out _));
        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 1, out _), "额度用完后不得再续");
        Assert.AreEqual(1, registry.GetLedger("s1")!.RoundsStarted);
    }

    [TestMethod]
    public void Disarmed_goal_does_not_continue()
    {
        var registry = RegistryWithGoal();
        registry.ApplyRestart();
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsFalse(GoalContinuationGate.TryContinue("s1", 0, out _),
            "重启后 activation 回到 disarmed，不得自动续跑");
    }

    [TestMethod]
    public void Blank_session_does_not_continue()
    {
        RegistryWithGoal();
        GoalContinuationGate.IsEnabled = () => true;

        Assert.IsFalse(GoalContinuationGate.TryContinue("   ", 0, out _));
    }
}
