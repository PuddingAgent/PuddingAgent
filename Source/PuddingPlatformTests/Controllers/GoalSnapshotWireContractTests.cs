using System.Text.Json;
using PuddingCode.Goals;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class GoalSnapshotWireContractTests
{
    [TestMethod]
    [DataRow(GoalPhase.Active, "active")]
    [DataRow(GoalPhase.Paused, "paused")]
    [DataRow(GoalPhase.Blocked, "blocked")]
    [DataRow(GoalPhase.BudgetExhausted, "budget_exhausted")]
    [DataRow(GoalPhase.Completed, "completed")]
    [DataRow(GoalPhase.Cancelled, "cancelled")]
    [DataRow(GoalPhase.Failed, "failed")]
    public void GoalResponse_UsesFrontendPhaseContract(GoalPhase phase, string expected)
    {
        var snapshot = new GoalSnapshot
        {
            GoalRunId = "goal-1", WorkspaceId = "default", ConversationId = "conversation-1",
            AgentInstanceId = "agent-1", Objective = "Verify goal status", ObjectiveVersion = 1,
            Phase = phase, MaxIterations = 3, IterationsStarted = 3, IterationsSettled = 3,
            ActivationEpoch = 1, AggregateVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        // Queries and command responses share this projection.
        var json = JsonSerializer.Serialize(new { goal = GoalCommandsController.ToDto(snapshot) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(expected, document.RootElement.GetProperty("goal").GetProperty("phase").GetString());
    }

    [TestMethod]
    public void GoalResponse_ExposesBlockedMessage_WhenSettlementBlocked()
    {
        var snapshot = new GoalSnapshot
        {
            GoalRunId = "goal-1", WorkspaceId = "default", ConversationId = "conversation-1",
            AgentInstanceId = "agent-1", Objective = "Verify goal status", ObjectiveVersion = 1,
            Phase = GoalPhase.Blocked,
            BlockedCode = "no_progress_circuit_open",
            BlockedMessage = "Circuit breaker opened after 3 settlements without progress " +
                             "(sameBlocker=3, infraFailures=0, threshold=3); human decision required.",
            StatusReason = "No-progress circuit breaker opened; replan is unavailable or already spent for this episode.",
            MaxIterations = 3, IterationsStarted = 3, IterationsSettled = 3,
            ActivationEpoch = 1, AggregateVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(new { goal = GoalCommandsController.ToDto(snapshot) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var goal = document.RootElement.GetProperty("goal");
        // 新增字段：结构化受阻说明原样透出。
        Assert.AreEqual(snapshot.BlockedMessage, goal.GetProperty("blockedMessage").GetString());
        // 既有字段不受影响：blockedCode / statusReason 原样保留。
        Assert.AreEqual("no_progress_circuit_open", goal.GetProperty("blockedCode").GetString());
        Assert.AreEqual(snapshot.StatusReason, goal.GetProperty("statusReason").GetString());
    }

    [TestMethod]
    public void GoalResponse_BlockedMessageIsNull_WhenNoBlockerInfo()
    {
        var snapshot = new GoalSnapshot
        {
            GoalRunId = "goal-2", WorkspaceId = "default", ConversationId = "conversation-1",
            AgentInstanceId = "agent-1", Objective = "Verify goal status", ObjectiveVersion = 1,
            Phase = GoalPhase.Active,
            MaxIterations = 3, IterationsStarted = 1, IterationsSettled = 1,
            ActivationEpoch = 1, AggregateVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(new { goal = GoalCommandsController.ToDto(snapshot) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var goal = document.RootElement.GetProperty("goal");
        // 老数据/无受阻信息：字段存在且为 null，不抛异常。
        Assert.IsTrue(goal.TryGetProperty("blockedMessage", out var blockedMessage));
        Assert.AreEqual(JsonValueKind.Null, blockedMessage.ValueKind);
    }
}
