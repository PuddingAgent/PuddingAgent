using PuddingCode.Runtime;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ExecutionWatchdogPolicyTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Evaluate_HardDeadlineWinsOverSlidingWindow()
    {
        var decision = ExecutionWatchdogPolicy.Evaluate(
            StartedAt.AddHours(24),
            StartedAt.AddHours(24),
            TimeSpan.FromHours(1),
            Snapshot(StartedAt.AddMinutes(59)));

        Assert.AreEqual(ExecutionWatchdogDecisionKind.HardTimeout, decision.Kind);
    }

    [TestMethod]
    public void Evaluate_StallsAfterMeaningfulProgressWindow()
    {
        var decision = ExecutionWatchdogPolicy.Evaluate(
            StartedAt.AddHours(2),
            StartedAt.AddHours(24),
            TimeSpan.FromHours(1),
            Snapshot(StartedAt.AddMinutes(30)));

        Assert.AreEqual(ExecutionWatchdogDecisionKind.Stalled, decision.Kind);
        Assert.AreEqual(TimeSpan.FromMinutes(90), decision.IdleFor);
        Assert.AreEqual("tool.completed:smart_plan", decision.LastStage);
    }

    [TestMethod]
    public void Evaluate_ContinuesWhenMeaningfulProgressIsRecent()
    {
        var decision = ExecutionWatchdogPolicy.Evaluate(
            StartedAt.AddHours(2),
            StartedAt.AddHours(24),
            TimeSpan.FromHours(1),
            Snapshot(StartedAt.AddMinutes(75)));

        Assert.AreEqual(ExecutionWatchdogDecisionKind.Continue, decision.Kind);
    }

    private static ExecutionProgressSnapshot Snapshot(DateTimeOffset lastMeaningfulAt)
        => new()
        {
            RootRunId = "run-1",
            ConversationId = "conversation-1",
            StartedAtUtc = StartedAt,
            LastLivenessAtUtc = lastMeaningfulAt,
            LastMeaningfulProgressAtUtc = lastMeaningfulAt,
            LivenessSequence = 2,
            MeaningfulSequence = 1,
            LastStage = "tool.completed:smart_plan",
        };
}
