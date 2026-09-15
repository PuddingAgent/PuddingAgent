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
        // ADR-074 §11：clear 只清除已结束 Goal 的展示，不删除事件/Iteration/Verification。
        // 已清除的记录不得再作为"当前 Goal"回吐，否则 Banner 会永久挂着一枚用户已明确
        // 关闭的历史终态徽标（clear 写入的 ClearedAtUtc 必须被读路径尊重，否则该命令是空操作）。
        return latest is null || latest.ClearedAtUtc is not null
            ? null
            : latest.ToSnapshot();
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
