using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>Backlog -> Ready 的唯一自动准入写入者。</summary>
public sealed class TaskBacklogRefinementStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkspaceAgentCatalog agentCatalog,
    IOptions<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider) : ITaskBacklogRefinementStore
{
    private readonly TaskAutoDispatchOptions _options = options.Value;

    public async Task<PromoteBacklogTaskResult> TryPromoteAsync(
        PromoteBacklogTaskCommand command,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CompatibleAgentId);
        if (command.ExpectedTaskVersion < 1 || !IsSha256(command.ExpectedAgentRoutingFingerprint))
            throw new ArgumentOutOfRangeException(nameof(command));

        var agent = (await agentCatalog.ListAgentsAsync(command.WorkspaceId, ct))
            .SingleOrDefault(item => string.Equals(
                item.AgentId, command.CompatibleAgentId, StringComparison.Ordinal));
        if (agent is null)
            return new(false, TaskBacklogPromotionCodes.RouteChanged);

        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var task = await db.WorkspaceTasks.SingleOrDefaultAsync(item =>
            item.WorkspaceId == command.WorkspaceId && item.TaskId == command.TaskId, ct);
        if (task is null || task.Version != command.ExpectedTaskVersion)
            return await RejectAsync(tx, TaskBacklogPromotionCodes.TaskChanged, task?.Version, ct);
        if (task.Status != WorkspaceTaskStatus.Backlog
            || !task.AutoDispatchEnabled
            || string.IsNullOrWhiteSpace(task.Description)
            || string.IsNullOrWhiteSpace(task.AcceptanceCriteria)
            || string.Equals(task.TaskType, "general", StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(tx, TaskBacklogPromotionCodes.NotReady, task.Version, ct);

        _options.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);
        var route = TaskAgentRouteMatcher.Evaluate(task, agent, typeRoute);
        if (!route.Compatible
            || !string.Equals(route.Fingerprint, command.ExpectedAgentRoutingFingerprint, StringComparison.Ordinal))
            return await RejectAsync(tx, TaskBacklogPromotionCodes.RouteChanged, task.Version, ct);

        var sequence = (await db.TaskEvents
            .Where(item => item.TaskId == task.TaskId)
            .MaxAsync(item => (long?)item.Sequence, ct) ?? 0) + 1;
        task.Status = WorkspaceTaskStatus.Ready;
        task.Version += 1;
        task.UpdatedAtUtc = now;
        task.UpdatedBy = "task-backlog-refinement";
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = sequence,
            EventType = TaskEventType.TaskReady,
            AgentId = command.CompatibleAgentId,
            DecisionCode = "backlog_refined",
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, TaskBacklogPromotionCodes.Promoted, task.Version);
    }

    private static async Task<PromoteBacklogTaskResult> RejectAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        string code,
        int? version,
        CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        return new(false, code, version);
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
