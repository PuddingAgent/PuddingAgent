using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingRuntimeTests.Services.TaskE2E;

/// <summary>
/// TB-08-C 独立 DbContext 只读探测：用与命令服务无关的 DbContext 实例查询
/// Task / Attempt / Event / Binding 四表，用于 E2E 断言（避免同一上下文跟踪污染断言）。
/// </summary>
public sealed class TaskDbProbe
{
    private readonly PlatformDbContextFactory _dbFactory;

    public TaskDbProbe(PlatformDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkspaceTaskEntity?> GetTaskAsync(string workspaceId, string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkspaceTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.TaskId == taskId);
    }

    public async Task<TaskAssignmentAttemptEntity?> GetAttemptAsync(string assignmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskAssignmentAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.AttemptId == assignmentId);
    }

    /// <summary>按 Sequence 升序返回该 Task 的全部事件。</summary>
    public async Task<IReadOnlyList<TaskEventEntity>> GetEventsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskEvents
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<TaskExecutionBindingEntity>> GetBindingsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskExecutionBindings
            .AsNoTracking()
            .Where(b => b.TaskId == taskId)
            .ToListAsync();
    }
}
