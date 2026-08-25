using PuddingCode.Goals;

namespace PuddingCoreTests.Goals;

[TestClass]
public sealed class GoalStateMachineTests
{
    [TestMethod]
    public void Terminal_Phases_Are_Exactly_Four()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                GoalPhase.Completed,
                GoalPhase.Cancelled,
                GoalPhase.Failed,
                GoalPhase.BudgetExhausted,
            },
            Enum.GetValues<GoalPhase>().Where(GoalStateMachine.IsTerminal).ToList());
    }

    [TestMethod]
    public void Active_Can_Reach_Every_Non_Active_Phase()
    {
        foreach (var target in Enum.GetValues<GoalPhase>())
        {
            var expected = target != GoalPhase.Active;
            Assert.AreEqual(
                expected,
                GoalStateMachine.CanTransition(GoalPhase.Active, target),
                $"Active -> {target}");
        }
    }

    [TestMethod]
    public void Resume_Is_Allowed_Only_From_Paused_And_Blocked()
    {
        Assert.IsTrue(GoalStateMachine.CanResume(GoalPhase.Paused));
        Assert.IsTrue(GoalStateMachine.CanResume(GoalPhase.Blocked));
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.Active));
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.BudgetExhausted));
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.Completed));
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.Cancelled));
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.Failed));
    }

    [TestMethod]
    public void Budget_Exhausted_Is_Fully_Terminal_No_Exceptions()
    {
        // budget_exhausted 是彻底终态：不可 resume、不可 edit、无任何出边。
        // 对已终态 Goal 下 cancel 由命令层返回"已处于终态"回执，不产生状态转换。
        Assert.IsFalse(GoalStateMachine.CanResume(GoalPhase.BudgetExhausted));
        Assert.IsFalse(GoalStateMachine.CanEdit(GoalPhase.BudgetExhausted));
        Assert.IsFalse(GoalStateMachine.CanTransition(GoalPhase.BudgetExhausted, GoalPhase.Cancelled));
        Assert.AreEqual(0, GoalStateMachine.AllowedTransitions(GoalPhase.BudgetExhausted).Count);
    }

    [TestMethod]
    public void Terminal_Phases_Have_No_Outgoing_Transitions()
    {
        foreach (var terminal in Enum.GetValues<GoalPhase>().Where(GoalStateMachine.IsTerminal))
        {
            Assert.AreEqual(0, GoalStateMachine.AllowedTransitions(terminal).Count, $"{terminal}");
        }
    }

    [TestMethod]
    public void Paused_And_Blocked_Can_Only_Resume_Or_Cancel()
    {
        foreach (var phase in new[] { GoalPhase.Paused, GoalPhase.Blocked })
        {
            CollectionAssert.AreEquivalent(
                new[] { GoalPhase.Active, GoalPhase.Cancelled },
                GoalStateMachine.AllowedTransitions(phase).ToArray());
        }
    }

    [TestMethod]
    public void Self_Transition_Is_Always_Invalid()
    {
        foreach (var phase in Enum.GetValues<GoalPhase>())
            Assert.IsFalse(GoalStateMachine.CanTransition(phase, phase), $"{phase}");
    }

    [TestMethod]
    public void Counters_Respect_Budget_Bounds()
    {
        Assert.IsFalse(GoalStateMachine.AreCountersValid(0, 0, 0));
        Assert.IsFalse(GoalStateMachine.AreCountersValid(257, 0, 0));
        Assert.IsFalse(GoalStateMachine.AreCountersValid(256, 257, 0));
        Assert.IsFalse(GoalStateMachine.AreCountersValid(256, 10, 11));
        Assert.IsTrue(GoalStateMachine.AreCountersValid(256, 0, 0));
        Assert.IsTrue(GoalStateMachine.AreCountersValid(1, 1, 1));
        Assert.IsTrue(GoalStateMachine.AreCountersValid(128, 100, 99));
    }

    [TestMethod]
    public void New_Iteration_Requires_Active_And_Remaining_Budget()
    {
        Assert.IsTrue(GoalStateMachine.CanAcceptNewIteration(GoalPhase.Active, 256, 0));
        Assert.IsTrue(GoalStateMachine.CanAcceptNewIteration(GoalPhase.Active, 256, 255));
        Assert.IsFalse(GoalStateMachine.CanAcceptNewIteration(GoalPhase.Active, 256, 256));
        Assert.IsFalse(GoalStateMachine.CanAcceptNewIteration(GoalPhase.Paused, 256, 5));
        Assert.IsFalse(GoalStateMachine.CanAcceptNewIteration(GoalPhase.Blocked, 256, 5));
    }

    [TestMethod]
    public void Hard_Limits_Are_Frozen()
    {
        Assert.AreEqual(256, GoalLimits.MaxIterationsHardLimit);
        Assert.AreEqual(256, GoalLimits.DefaultMaxIterations);
        Assert.AreEqual(1, GoalLimits.MinIterations);
        Assert.AreEqual(4000, GoalLimits.ObjectiveMaxLength);
        Assert.IsFalse(GoalLimits.IsValidIterationBudget(0));
        Assert.IsFalse(GoalLimits.IsValidIterationBudget(257));
        Assert.IsTrue(GoalLimits.IsValidIterationBudget(1));
        Assert.IsTrue(GoalLimits.IsValidIterationBudget(256));
    }
}
