using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 目标注册表：把生命周期与轮次账本按会话绑在一起。
/// <para>
/// 这里最关键的一条是 <see cref="GoalRegistryTests.Restart_keeps_phase_but_disarms"/>
/// —— 进程内注册表"重启即丢"恰好实现了"重启后不自动续跑"，
/// 不是靠判断，是靠它压根没持久化。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalRegistryTests
{
    private static IReadOnlyList<GoalEvidence> Evidence() =>
    [
        new GoalEvidence(GoalEvidenceKind.Test, "GoalRegistryTests", "通过"),
    ];

    [TestMethod]
    public void Create_binds_state_and_ledger_to_the_session()
    {
        var registry = new GoalRegistry();

        var state = registry.Create("s1", "接完 memory 写入链路", maxRounds: 5);

        Assert.AreEqual(GoalPhase.Active, state.Phase);
        Assert.AreEqual(GoalActivation.Armed, state.Activation);
        Assert.IsNotNull(registry.Get("s1"));
        Assert.IsNotNull(registry.GetLedger("s1"));
        Assert.AreEqual(5, registry.GetLedger("s1")!.MaxRounds);
    }

    [TestMethod]
    public void One_goal_per_session()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标 A", 5);

        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Create("s1", "目标 B", 5));
    }

    [TestMethod]
    public void Unknown_session_operations_throw()
    {
        var registry = new GoalRegistry();

        Assert.IsNull(registry.Get("nope"));
        Assert.IsNull(registry.GetLedger("nope"));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Pause("nope"));
    }

    [TestMethod]
    public void Pause_and_resume_flip_activation()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标", 5);

        var paused = registry.Pause("s1");
        Assert.AreEqual(GoalPhase.Paused, paused.Phase);
        Assert.AreEqual(GoalActivation.Disarmed, paused.Activation);

        var resumed = registry.Resume("s1");
        Assert.AreEqual(GoalPhase.Active, resumed.Phase);
        Assert.AreEqual(GoalActivation.Armed, resumed.Activation);
    }

    [TestMethod]
    public void Complete_requires_evidence_and_clears_continuation()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标", 5);

        Assert.ThrowsExactly<GoalTransitionException>(() => registry.Complete("s1", []));

        var done = registry.Complete("s1", Evidence());
        Assert.AreEqual(GoalPhase.Complete, done.Phase);
        Assert.AreEqual(1, done.Evidence.Count);
        Assert.IsFalse(GoalLifecycle.CanAutoContinue(done));
    }

    [TestMethod]
    public void Clear_removes_state_and_ledger()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标", 5);

        Assert.IsTrue(registry.Clear("s1"));
        Assert.IsNull(registry.Get("s1"));
        Assert.IsNull(registry.GetLedger("s1"));
        Assert.IsFalse(registry.Clear("s1"), "重复清理返回 false 而不是抛错");
    }

    /// <summary>
    /// 核心断言：重启后 phase 保留、activation 归零；账本（已用轮次）不丢。
    /// </summary>
    [TestMethod]
    public void Restart_keeps_phase_but_disarms()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标", 5);
        registry.GetLedger("s1")!.TryReserve("r1", 1, DateTimeOffset.UnixEpoch);
        registry.GetLedger("s1")!.TryAdmit("r1", registry.Get("s1")!.GoalId, 1);

        var affected = registry.ApplyRestart();

        Assert.AreEqual(1, affected);
        var after = registry.Get("s1")!;
        Assert.AreEqual(GoalPhase.Active, after.Phase, "phase 是持久的");
        Assert.AreEqual(GoalActivation.Disarmed, after.Activation, "activation 不持久");
        Assert.IsFalse(GoalLifecycle.CanAutoContinue(after));
        Assert.AreEqual(1, registry.GetLedger("s1")!.RoundsStarted, "已用轮次不该因重启归零");
    }

    [TestMethod]
    public void Sessions_are_isolated()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标 A", 5);
        registry.Create("s2", "目标 B", 9);

        registry.Pause("s1");

        Assert.AreEqual(GoalPhase.Paused, registry.Get("s1")!.Phase);
        Assert.AreEqual(GoalPhase.Active, registry.Get("s2")!.Phase, "另一个会话不受影响");
        Assert.AreEqual(9, registry.GetLedger("s2")!.MaxRounds);
    }

    [TestMethod]
    public void Apply_restart_is_idempotent()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "目标", 5);

        registry.ApplyRestart();
        var second = registry.ApplyRestart();

        Assert.AreEqual(1, second);
        Assert.AreEqual(GoalActivation.Disarmed, registry.Get("s1")!.Activation);
    }
}
