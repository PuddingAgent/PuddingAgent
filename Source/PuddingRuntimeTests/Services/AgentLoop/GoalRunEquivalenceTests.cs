using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 开关两态的等价性比对：把「关闭后必须与现状一致」变成可执行断言。
/// <para>
/// 同时要测两个方向：等价时**不能**报差异（否则每次验收都是红的），
/// 真有差异时**必须**报出来（否则这个工具等于没有）。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalRunEquivalenceTests
{
    private static GoalRunTrace Trace(
        int[] rounds,
        string[]? tools = null,
        AgentLoopStopReason stop = AgentLoopStopReason.Done) =>
        new(rounds, stop, tools ?? []);

    [TestMethod]
    public void Identical_traces_are_equivalent()
    {
        var a = Trace([1, 2, 3], ["read", "write"]);
        var b = Trace([1, 2, 3], ["read", "write"]);

        Assert.IsTrue(GoalRunEquivalence.AreEquivalent(a, b));
        Assert.AreEqual(0, GoalRunEquivalence.Compare(a, b).Count);
    }

    [TestMethod]
    public void Different_stop_reason_is_reported()
    {
        var diffs = GoalRunEquivalence.Compare(
            Trace([1], stop: AgentLoopStopReason.Done),
            Trace([1], stop: AgentLoopStopReason.MaxRoundsReached));

        Assert.AreEqual(1, diffs.Count);
        StringAssert.Contains(diffs[0], "stopReason");
    }

    [TestMethod]
    public void Extra_round_is_reported()
    {
        var diffs = GoalRunEquivalence.Compare(Trace([1, 2]), Trace([1, 2, 3]));

        Assert.AreEqual(1, diffs.Count);
        StringAssert.Contains(diffs[0], "rounds");
    }

    [TestMethod]
    public void Different_tool_calls_are_reported()
    {
        var diffs = GoalRunEquivalence.Compare(
            Trace([1], ["read"]),
            Trace([1], ["read", "read"]));

        Assert.AreEqual(1, diffs.Count);
        StringAssert.Contains(diffs[0], "tools");
    }

    [TestMethod]
    public void Multiple_differences_are_all_reported()
    {
        var diffs = GoalRunEquivalence.Compare(
            Trace([1], ["read"], AgentLoopStopReason.Done),
            Trace([1, 2], ["read", "write"], AgentLoopStopReason.Cancelled));

        Assert.AreEqual(3, diffs.Count, "三类差异都要报出来，不能只报第一条");
    }

    [TestMethod]
    public void Empty_traces_are_equivalent()
    {
        Assert.IsTrue(GoalRunEquivalence.AreEquivalent(GoalRunTrace.Empty, GoalRunTrace.Empty));
    }
}
