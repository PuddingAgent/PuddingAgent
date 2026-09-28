using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Tasks;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.Tasks;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-16 tasks card: the management entry over Core's task store and command service (the same pair the HTTP
/// controller uses). Every command carries the version the page read, so a concurrent edit conflicts instead
/// of being overwritten.
/// </summary>
internal sealed class DesktopTaskSettings(IDesktopKernel kernel) : ITaskSettings
{
    private Task<T> Tasks<T>(string operationId,
        Func<SqliteWorkspaceTaskStore, TaskCommandService, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId, (scope, token) => body(
            scope.Services.GetRequiredService<SqliteWorkspaceTaskStore>(),
            scope.Services.GetRequiredService<TaskCommandService>(), token), cancellationToken);

    public Task<TaskListPage> ListAsync(TaskFilter filter, CancellationToken cancellationToken = default)
        => Tasks("tasks.list", async (store, _, token) =>
        {
            var normalized = TaskText.Normalize(filter);
            var query = new TaskQuery
            {
                WorkspaceId = normalized.WorkspaceId,
                Status = ParseStatus(normalized.Status),
                Priority = ParsePriority(normalized.Priority),
                AgentId = Empty(normalized.AgentId),
                Cursor = Empty(normalized.Cursor),
                Limit = normalized.Limit,
            };
            var items = await store.QueryTasksAsync(query, token);
            // TotalCount 与游标无关（Core 的契约）；游标是本页最后一项的任务 ID。
            // CountTasksAsync 接受一个状态集合（为空表示不限），与查询条件保持一致。
            var total = await store.CountTasksAsync(query,
                query.Status is { } single ? [single] : null, token);
            var nextCursor = items.Count == normalized.Limit && items.Count > 0 ? items[^1].TaskId : "";
            return new TaskListPage(items.Select(Map).ToArray(), nextCursor, total, normalized.Limit);
        }, cancellationToken);

    public Task CreateAsync(TaskCreate create, CancellationToken cancellationToken = default)
        => Tasks("tasks.create", async (store, _, token) =>
        {
            await Guarded(() => store.CreateTaskAsync(new CreateTaskRequest
            {
                WorkspaceId = create.WorkspaceId.Trim(),
                Title = create.Title.Trim(),
                Description = Empty(create.Description),
                Priority = ParsePriority(create.Priority) ?? TaskPriority.P3,
            }, token));
            return true;
        }, cancellationToken);

    public Task RunCommandAsync(
        TaskCommandKind kind, TaskCommandRequest request, CancellationToken cancellationToken = default)
        => Tasks($"tasks.{kind.ToString().ToLowerInvariant()}", async (_, commands, token) =>
        {
            await Guarded(() => commands.ApplyCommandAsync(
                request.WorkspaceId.Trim(), request.TaskId.Trim(), Map(kind), request.ExpectedVersion,
                Empty(request.AgentId), null, Empty(request.Reason), LocalDesktopIdentity.UserId, token));
            return true;
        }, cancellationToken);

    /// <summary>
    /// Core reports a stale version and a forbidden transition with the same exception type, distinguished by
    /// code: a version conflict becomes SettingsConflictException so the page can say it prevented an overwrite,
    /// while the container-mother-card refusal stays a plain refusal (the page never forces it).
    /// </summary>
    private static async Task Guarded(Func<Task> action)
    {
        try { await action(); }
        catch (TaskStoreException exception)
        {
            throw exception.ErrorCode switch
            {
                TaskErrorCode.TaskVersionConflict => new SettingsConflictException(
                    $"任务版本冲突，已阻止覆盖：{exception.Message}"),
                TaskErrorCode.TaskNotFound => new InvalidOperationException($"任务不存在：{exception.Message}"),
                _ => new ArgumentException(exception.Message, nameof(action)),
            };
        }
    }

    /// <summary>
    /// Mirrors the controller's fallback: Cancelled/Archived go to history and have no board column, while
    /// TaskStateMachine.ProjectBoardColumn throws for them.
    /// </summary>
    private static string ToBoardColumn(WorkspaceTaskStatus status) =>
        status is WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived
            ? status.ToString()
            : TaskStateMachine.ProjectBoardColumn(status).ToString();

    private static TaskCommand Map(TaskCommandKind kind) => kind switch
    {
        TaskCommandKind.Assign => TaskCommand.Assign,
        TaskCommandKind.RunNow => TaskCommand.RunNow,
        TaskCommandKind.Cancel => TaskCommand.Cancel,
        TaskCommandKind.Reopen => TaskCommand.Reopen,
        TaskCommandKind.Archive => TaskCommand.Archive,
        TaskCommandKind.MarkFailed => TaskCommand.MarkFailed,
        TaskCommandKind.Resume => TaskCommand.Resume,
        _ => TaskCommand.Requeue
    };

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static WorkspaceTaskStatus? ParseStatus(string value) =>
        string.IsNullOrWhiteSpace(value)
        || !Enum.TryParse<WorkspaceTaskStatus>(value, ignoreCase: true, out var status)
            ? null
            : status;

    private static TaskPriority? ParsePriority(string value) => value.ToLowerInvariant() switch
    {
        "p0" => TaskPriority.P0,
        "p1" => TaskPriority.P1,
        "p2" => TaskPriority.P2,
        "p3" => TaskPriority.P3,
        _ => null
    };

    private static WorkspaceTaskItem Map(WorkspaceTask task) => new(
        task.TaskId, task.WorkspaceId, task.Title, task.Description ?? "",
        task.Status.ToString(),
        // 看板列由 Core 的状态机投影；Cancelled/Archived 不占五列（状态机对它们**抛异常**），
        // 与 HTTP 控制器一致地回退为状态名。
        ToBoardColumn(task.Status),
        task.Priority.ToString().ToLowerInvariant(),
        task.ExecutionWindow.ToString().ToLowerInvariant(),
        task.PreferredAgentId ?? "", task.ActiveAssignmentId ?? "",
        task.Origin?.ToString() ?? "", task.Version, task.SortOrder, task.ProgressPercent,
        task.BlockerKind ?? "", task.BlockerReason ?? "", task.FailureCode ?? "", task.FailureReason ?? "",
        task.CreatedAtUtc, task.UpdatedAtUtc,
        // 允许迁移同样来自 Core 的状态机（页面只消费，不实现状态机）。
        TaskStateMachine.GetAllowedTransitions(task.Status).Select(status => status.ToString()).ToArray());
}
