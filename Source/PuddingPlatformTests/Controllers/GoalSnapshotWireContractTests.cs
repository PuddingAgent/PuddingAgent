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
}
