using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>ADR-074 §10: Goal 只读投影。任意入口看到的状态都来自同一服务端查询。</summary>
public sealed class GoalQueryService(GoalRunStore store) : IGoalQueryService
{
    public async Task<GoalSnapshot?> GetActiveAsync(
        string workspaceId, string conversationId, CancellationToken ct = default)
    {
        var latest = await store.FindLatestAsync(workspaceId, conversationId, ct);
        return latest is not null && !GoalStateMachine.IsTerminal(latest.Status)
            ? latest.ToSnapshot()
            : null;
    }

    public async Task<GoalSnapshot?> GetAsync(string goalRunId, CancellationToken ct = default)
    {
        var goal = await store.FindAsync(goalRunId, ct);
        return goal?.ToSnapshot();
    }

    public async Task<GoalSnapshot?> GetLatestAsync(
        string workspaceId, string conversationId, CancellationToken ct = default)
    {
        var latest = await store.FindLatestAsync(workspaceId, conversationId, ct);
        return latest?.ToSnapshot();
    }

    public async Task<IReadOnlyList<GoalIterationSnapshot>> GetIterationsAsync(
        string goalRunId, CancellationToken ct = default)
    {
        var iterations = await store.GetIterationsAsync(goalRunId, ct);
        return iterations
            .Select(i => new GoalIterationSnapshot
            {
                GoalRunId = i.GoalRunId,
                ActivationEpoch = i.ActivationEpoch,
                IterationNo = i.IterationNo,
                Status = i.Status,
                CommandId = i.CommandId,
                TurnId = i.TurnId,
                StartedAtUtc = i.StartedAtUtc,
                SettledAtUtc = i.SettledAtUtc,
            })
            .ToList();
    }
}
