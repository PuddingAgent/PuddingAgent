using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 观测期钩子：默认关闭时零开销；开启后只算决策、不改控制流。
/// <para>
/// 这里刻意不测"它会不会真的让 agent 继续跑" —— 因为**它现在不会**。
/// 把没有的能力测成绿的，是最坏的一种测试。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalLoopDecisionHookTests
{
    private static AgentLoopContext Ctx(string sessionId = "s1") => new()
    {
        SessionId = sessionId,
        AgentInstanceId = "a1",
        WorkspaceId = "w1",
        AgentTemplateId = "t1",
        UserMessage = "hi",
        MaxRounds = 10,
    };

    [TestMethod]
    public async Task Disabled_hook_does_nothing()
    {
        var hook = new GoalLoopDecisionHook(new GoalRegistry(), isEnabled: () => false);

        await hook.OnLoopCompleteAsync(Ctx(), "done", AgentLoopStopReason.Done);

        Assert.AreEqual(0, hook.DecisionCount);
        Assert.IsNull(hook.LastOutcome);
    }

    [TestMethod]
    public async Task Enabled_hook_records_a_decision_for_a_session_without_goal()
    {
        var hook = new GoalLoopDecisionHook(new GoalRegistry(), isEnabled: () => true);

        await hook.OnLoopCompleteAsync(Ctx(), "done", AgentLoopStopReason.Done);

        Assert.AreEqual(1, hook.DecisionCount);
        Assert.IsNotNull(hook.LastOutcome);
        Assert.AreEqual(GoalLoopActionKind.Stop, hook.LastOutcome!.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonNotActive, hook.LastOutcome.ReasonCode);
    }

    /// <summary>
    /// 观测期拿不到真实待办清单，只能上报 openItems=0；按 Decider 的顺序这会落到
    /// 「清单空但未声称完成 → Continue 去收尾」。这是**已知的口径偏差**，等接上真实
    /// 待办来源后才会给出 Stop(no-progress)。测试把这个偏差固定下来，避免以后误以为
    /// 观测期就能判空转。
    /// </summary>
    [TestMethod]
    public async Task Enabled_hook_reports_continue_for_an_active_goal_when_checklist_is_unknown()
    {
        var registry = new GoalRegistry();
        registry.Create("s1", "接完 goal 链路", maxRounds: 5);
        var hook = new GoalLoopDecisionHook(registry, isEnabled: () => true);

        await hook.OnLoopCompleteAsync(Ctx(), "done", AgentLoopStopReason.Done);

        Assert.AreEqual(GoalLoopActionKind.Continue, hook.LastOutcome!.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonNothingToDo, hook.LastOutcome.ReasonCode);
    }

    [TestMethod]
    public async Task Hook_never_throws_even_for_hostile_input()
    {
        var hook = new GoalLoopDecisionHook(new GoalRegistry(), isEnabled: () => true);

        // null context 是契约外的输入，钩子仍不得抛
        await hook.OnLoopCompleteAsync(null!, "done", AgentLoopStopReason.Done);

        Assert.AreEqual(0, hook.DecisionCount);
    }
}
