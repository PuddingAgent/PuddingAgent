using PuddingCode.Runtime;
using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

[TestClass]
public sealed class SubAgentSteerPlannerTests
{
    [TestMethod]
    public void Plan_WhenNotRunning_ReportsNotRunning_EvenWithValidManifest()
    {
        // 这条是核心护栏：目标不在跑就没有 loop 会消费消息。
        // 若不在这里拒绝，消息会静默入队并躺到 TTL 过期，而调用方从回执上看不出区别。
        var plan = SubAgentSteerPlanner.Plan(
            "sub-1", "改看第 8 轮的 cache", isRunning: false,
            CreateManifest(turnId: "turn-9"));

        Assert.AreEqual(SubAgentSteerOutcome.NotRunning, plan.Outcome);
        Assert.IsNull(plan.Target);
        Assert.IsFalse(string.IsNullOrWhiteSpace(plan.Detail));
    }

    [TestMethod]
    public void Plan_WhenRunningWithValidManifest_QueuesOnSubSessionAndParentTurn()
    {
        var plan = SubAgentSteerPlanner.Plan(
            "sub-1", "改看第 8 轮的 cache", isRunning: true,
            CreateManifest(turnId: "turn-9"));

        Assert.AreEqual(SubAgentSteerOutcome.Queued, plan.Outcome);
        Assert.IsNotNull(plan.Target);
        Assert.AreEqual("sub-1", plan.Target.SessionId, "must target the sub-agent session, not the parent");
        Assert.AreEqual("turn-9", plan.Target.TargetTurnId);
        Assert.AreEqual("default", plan.Target.WorkspaceId);
    }

    [TestMethod]
    public void Plan_WhenRunningButManifestMissing_ReportsTargetUnresolved()
    {
        // 运行中但拿不到 manifest：宁可拒绝，也不退化成「投到父会话」。
        var plan = SubAgentSteerPlanner.Plan("sub-1", "消息", isRunning: true, manifest: null);

        Assert.AreEqual(SubAgentSteerOutcome.TargetUnresolved, plan.Outcome);
        Assert.IsNull(plan.Target);
    }

    [TestMethod]
    public void Plan_WhenRunningButParentTurnMissing_ReportsTargetUnresolved()
    {
        var plan = SubAgentSteerPlanner.Plan(
            "sub-1", "消息", isRunning: true, CreateManifest(turnId: null));

        Assert.AreEqual(SubAgentSteerOutcome.TargetUnresolved, plan.Outcome);
        Assert.IsNull(plan.Target);
    }

    [TestMethod]
    public void Plan_WithBlankSubSessionId_ReportsInvalid()
    {
        var plan = SubAgentSteerPlanner.Plan("   ", "消息", isRunning: true, CreateManifest(turnId: "t"));

        Assert.AreEqual(SubAgentSteerOutcome.Invalid, plan.Outcome);
    }

    [TestMethod]
    public void Plan_WithBlankMessage_ReportsInvalid()
    {
        var plan = SubAgentSteerPlanner.Plan("sub-1", "  ", isRunning: true, CreateManifest(turnId: "t"));

        Assert.AreEqual(SubAgentSteerOutcome.Invalid, plan.Outcome);
    }

    private static SubAgentRunManifest CreateManifest(string? turnId)
        => new()
        {
            RunId = "run-1",
            ParentSessionId = "parent-session",
            SubSessionId = "sub-1",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "workspace-task-agent",
            Task = "task",
            Status = "running",
            StartedAt = DateTimeOffset.UnixEpoch,
            ParentExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = default,
                ConversationId = "parent-session",
                RunId = "parent-run",
                TraceId = null,
                TurnId = turnId,
            },
        };
}
