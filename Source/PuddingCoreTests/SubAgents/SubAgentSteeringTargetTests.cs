using PuddingCode.Runtime;
using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

[TestClass]
public sealed class SubAgentSteeringTargetTests
{
    [TestMethod]
    public void FromManifest_ResolvesSubSessionAndParentTurn()
    {
        var target = SubAgentSteeringTarget.FromManifest(
            CreateManifest(subSessionId: "parent-sub-abc", turnId: "turn-9"));

        Assert.IsNotNull(target);
        Assert.AreEqual("default", target.WorkspaceId);
        // 子代理 loop 的 session 是它自己的 subSessionId，不是父会话。
        Assert.AreEqual("parent-sub-abc", target.SessionId);
        // 子代理 loop 的 turn 是父 turn（SubAgentManager 用 parent.TurnId）。
        // 写错这里不会报错，只会让消息躺到 TTL 过期。
        Assert.AreEqual("turn-9", target.TargetTurnId);
    }

    [TestMethod]
    public void FromManifest_WithoutParentTurn_ReturnsNull()
    {
        // fail closed：不许退化成「投到父会话」或「投到任意 turn」，
        // 那会把消息注入错误主体，而调用方从回执上看不出来。
        Assert.IsNull(SubAgentSteeringTarget.FromManifest(
            CreateManifest(subSessionId: "parent-sub-abc", turnId: null)));
    }

    [TestMethod]
    public void FromManifest_WithBlankSubSession_ReturnsNull()
    {
        Assert.IsNull(SubAgentSteeringTarget.FromManifest(
            CreateManifest(subSessionId: "   ", turnId: "turn-9")));
    }

    [TestMethod]
    public void FromManifest_WithNullManifest_ReturnsNull()
    {
        Assert.IsNull(SubAgentSteeringTarget.FromManifest(null));
    }

    private static SubAgentRunManifest CreateManifest(string subSessionId, string? turnId)
        => new()
        {
            RunId = "run-1",
            ParentSessionId = "parent-session",
            SubSessionId = subSessionId,
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
