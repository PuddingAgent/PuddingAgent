namespace PuddingCode.Tasks;

/// <summary>WorkspaceTask 持久化抽象（仅接口契约，TB-02 实现 SqliteWorkspaceTaskStore，本层不实现）。</summary>
public interface ITaskStore
{
    /// <summary>创建任务。</summary>
    Task<WorkspaceTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default);

    /// <summary>按工作区与任务 ID 获取任务。</summary>
    Task<WorkspaceTask?> GetTaskAsync(string workspaceId, string taskId, CancellationToken ct = default);

    /// <summary>按查询条件分页获取任务列表。</summary>
    Task<IReadOnlyList<WorkspaceTask>> QueryTasksAsync(TaskQuery query, CancellationToken ct = default);

    /// <summary>更新任务（CAS：expectedVersion 不匹配抛/返回冲突）。</summary>
    Task<WorkspaceTask> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken ct = default);

    /// <summary>硬删除任务（仅无历史 Backlog）。</summary>
    Task<bool> HardDeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default);

    /// <summary>追加任务事件。</summary>
    Task AppendEventAsync(TaskEvent evt, CancellationToken ct = default);
}

/// <summary>创建任务请求。</summary>
public sealed record CreateTaskRequest
{
    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>任务标题。</summary>
    public required string Title { get; init; }

    /// <summary>任务描述。</summary>
    public string? Description { get; init; }

    /// <summary>验收标准。</summary>
    public string? AcceptanceCriteria { get; init; }

    /// <summary>优先级。</summary>
    public TaskPriority Priority { get; init; } = TaskPriority.P3;

    /// <summary>执行窗口。</summary>
    public TaskExecutionWindow ExecutionWindow { get; init; } = TaskExecutionWindow.Inherit;

    /// <summary>偏好 Agent ID。</summary>
    public string? PreferredAgentId { get; init; }

    /// <summary>最早可执行时间（UTC）。</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    /// <summary>截止时间（UTC）。</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>排序序号。</summary>
    public long SortOrder { get; init; }
}

/// <summary>更新任务请求（CAS：expectedVersion 不匹配抛/返回冲突）。可空字段表示“不更新”。</summary>
public sealed record UpdateTaskRequest
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>期望的当前版本号（乐观并发）。</summary>
    public required int ExpectedVersion { get; init; }

    /// <summary>任务标题。</summary>
    public string? Title { get; init; }

    /// <summary>任务描述。</summary>
    public string? Description { get; init; }

    /// <summary>验收标准。</summary>
    public string? AcceptanceCriteria { get; init; }

    /// <summary>优先级。</summary>
    public TaskPriority? Priority { get; init; }

    /// <summary>执行窗口。</summary>
    public TaskExecutionWindow? ExecutionWindow { get; init; }

    /// <summary>偏好 Agent ID。</summary>
    public string? PreferredAgentId { get; init; }

    /// <summary>最早可执行时间（UTC）。</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    /// <summary>截止时间（UTC）。</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>排序序号。</summary>
    public long? SortOrder { get; init; }
}

/// <summary>任务查询条件。</summary>
public sealed record TaskQuery
{
    /// <summary>所属工作区 ID（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>按状态过滤。</summary>
    public WorkspaceTaskStatus? Status { get; init; }

    /// <summary>按 Agent 过滤。</summary>
    public string? AgentId { get; init; }

    /// <summary>按优先级过滤。</summary>
    public TaskPriority? Priority { get; init; }

    /// <summary>分页游标（可为空，首次查询不传）。</summary>
    public string? Cursor { get; init; }

    /// <summary>分页大小。</summary>
    public int Limit { get; init; } = 100;
}
