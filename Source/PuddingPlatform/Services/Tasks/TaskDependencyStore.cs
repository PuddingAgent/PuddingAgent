using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

public sealed class TaskDependencyStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TimeProvider timeProvider) : ITaskDependencyStore
{
    public async Task<TaskDependency> AddAsync(
        string workspaceId,
        string predecessorTaskId,
        string successorTaskId,
        CancellationToken ct = default)
    {
        ValidateIds(workspaceId, predecessorTaskId, successorTaskId);
        if (predecessorTaskId == successorTaskId)
            throw new InvalidOperationException("task_dependency_self_reference");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var taskIds = await db.WorkspaceTasks.AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId
                    && (item.TaskId == predecessorTaskId || item.TaskId == successorTaskId))
                .Select(item => item.TaskId)
                .ToListAsync(ct);
            if (taskIds.Count != 2)
                throw new InvalidOperationException("task_dependency_task_not_found");

            var existing = await db.TaskDependencies.AsNoTracking()
                .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId
                    && item.PredecessorTaskId == predecessorTaskId
                    && item.SuccessorTaskId == successorTaskId, ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return ToContract(existing);
            }

            var edges = await db.TaskDependencies.AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId)
                .Select(item => new { item.PredecessorTaskId, item.SuccessorTaskId })
                .ToListAsync(ct);
            if (CreatesCycle(edges.Select(edge =>
                    (edge.PredecessorTaskId, edge.SuccessorTaskId)),
                predecessorTaskId,
                successorTaskId))
            {
                throw new InvalidOperationException("task_dependency_cycle");
            }

            var entity = new TaskDependencyEntity
            {
                DependencyId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                PredecessorTaskId = predecessorTaskId,
                SuccessorTaskId = successorTaskId,
                Kind = "finish_to_start",
                CreatedAtUtc = timeProvider.GetUtcNow(),
            };
            db.TaskDependencies.Add(entity);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToContract(entity);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> RemoveAsync(
        string workspaceId,
        string dependencyId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TaskDependencies
            .Where(item => item.WorkspaceId == workspaceId
                && item.DependencyId == dependencyId)
            .ExecuteDeleteAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<TaskDependency>> ListAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entities = await db.TaskDependencies.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && (item.PredecessorTaskId == taskId || item.SuccessorTaskId == taskId))
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        return entities.Select(ToContract).ToArray();
    }

    public async Task<TaskDependencyEvaluation> EvaluateAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.WorkspaceTasks.AsNoTracking().AnyAsync(
                item => item.WorkspaceId == workspaceId && item.TaskId == taskId,
                ct))
        {
            throw new InvalidOperationException("task_dependency_task_not_found");
        }

        var predecessorIds = await db.TaskDependencies.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.SuccessorTaskId == taskId)
            .Select(item => item.PredecessorTaskId)
            .ToListAsync(ct);
        if (predecessorIds.Count == 0)
            return Evaluation(workspaceId, taskId, [], []);

        var predecessors = await db.WorkspaceTasks.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && predecessorIds.Contains(item.TaskId))
            .Select(item => new { item.TaskId, item.Status })
            .ToListAsync(ct);
        var completedEvents = await db.TaskEvents.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && predecessorIds.Contains(item.TaskId)
                && item.EventType == TaskEventType.TaskCompleted)
            .Select(item => item.TaskId)
            .Distinct()
            .ToListAsync(ct);

        var completed = completedEvents.ToHashSet(StringComparer.Ordinal);
        var waiting = new List<string>();
        var broken = new List<string>();
        foreach (var predecessorId in predecessorIds)
        {
            var predecessor = predecessors.FirstOrDefault(item => item.TaskId == predecessorId);
            if (predecessor is null)
            {
                broken.Add(predecessorId);
                continue;
            }

            if (predecessor.Status == WorkspaceTaskStatus.Completed
                || (predecessor.Status == WorkspaceTaskStatus.Archived
                    && completed.Contains(predecessorId)))
            {
                continue;
            }

            if (predecessor.Status is WorkspaceTaskStatus.Failed
                or WorkspaceTaskStatus.Cancelled
                or WorkspaceTaskStatus.Archived)
            {
                broken.Add(predecessorId);
            }
            else
            {
                waiting.Add(predecessorId);
            }
        }

        return Evaluation(workspaceId, taskId, waiting, broken);
    }

    private static TaskDependencyEvaluation Evaluation(
        string workspaceId,
        string taskId,
        IReadOnlyList<string> waiting,
        IReadOnlyList<string> broken) => new()
    {
        WorkspaceId = workspaceId,
        TaskId = taskId,
        State = broken.Count > 0
            ? TaskDependencyEvaluationState.Broken
            : waiting.Count > 0
                ? TaskDependencyEvaluationState.Waiting
                : TaskDependencyEvaluationState.Satisfied,
        WaitingOnTaskIds = waiting,
        BrokenByTaskIds = broken,
    };

    private static bool CreatesCycle(
        IEnumerable<(string Predecessor, string Successor)> existing,
        string predecessor,
        string successor)
    {
        var adjacency = existing
            .GroupBy(edge => edge.Predecessor, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.Successor).ToArray(),
                StringComparer.Ordinal);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(successor);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            if (current == predecessor)
                return true;
            if (adjacency.TryGetValue(current, out var next))
            {
                foreach (var item in next)
                    pending.Push(item);
            }
        }

        return false;
    }

    private static TaskDependency ToContract(TaskDependencyEntity entity) => new()
    {
        DependencyId = entity.DependencyId,
        WorkspaceId = entity.WorkspaceId,
        PredecessorTaskId = entity.PredecessorTaskId,
        SuccessorTaskId = entity.SuccessorTaskId,
        Kind = entity.Kind,
        CreatedAtUtc = entity.CreatedAtUtc,
    };

    private static void ValidateIds(string workspaceId, string predecessor, string successor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(predecessor);
        ArgumentException.ThrowIfNullOrWhiteSpace(successor);
    }
}
