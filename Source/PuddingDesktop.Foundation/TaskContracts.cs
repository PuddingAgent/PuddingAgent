namespace PuddingDesktop.Foundation;

/// <summary>One workspace task, as Core's task store reports it.</summary>
public sealed record WorkspaceTaskItem(
    string TaskId, string WorkspaceId, string Title, string Description, string Status, string BoardColumn,
    string Priority, string ExecutionWindow, string PreferredAgentId, string ActiveAssignmentId,
    string Origin, int Version, long SortOrder, int? ProgressPercent,
    string BlockerKind, string BlockerReason, string FailureCode, string FailureReason,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, IReadOnlyList<string> AllowedTransitions)
{
    public string DisplayTitle => Title.Length > 0 ? Title : "（无标题任务）";
    public string StatusText => TaskText.DescribeStatus(Status);
    public string PriorityText => TaskText.DescribePriority(Priority);
    public string LineText =>
        $"{DisplayTitle} · {StatusText} · {PriorityText} · 看板列 {TaskText.DescribeBoardColumn(BoardColumn)} · v{Version}";
    public string DetailText =>
        $"任务 {TaskId} · 工作区 {WorkspaceId} · 版本 {Version}\n" +
        $"状态 {StatusText}（{Status}）· 优先级 {PriorityText} · 执行窗口 {TaskText.DescribeExecutionWindow(ExecutionWindow)}\n" +
        $"偏好 Agent {(PreferredAgentId.Length == 0 ? "未指定" : PreferredAgentId)} · " +
        $"当前指派 {(ActiveAssignmentId.Length == 0 ? "无" : ActiveAssignmentId)} · " +
        $"来源 {(Origin.Length == 0 ? "未记录" : Origin)}\n" +
        (ProgressPercent is { } progress ? $"进度 {progress}%\n" : "") +
        (BlockerReason.Length > 0 ? $"阻塞 {BlockerKind}：{BlockerReason}\n" : "") +
        (FailureReason.Length > 0 ? $"失败 {FailureCode}：{FailureReason}\n" : "") +
        $"允许迁移：{(AllowedTransitions.Count == 0 ? "当前状态无可迁移目标" : string.Join(" → ", AllowedTransitions))}\n" +
        $"更新 {UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}

public sealed record TaskListPage(
    IReadOnlyList<WorkspaceTaskItem> Items, string NextCursor, int TotalCount, int Limit)
{
    public static TaskListPage Empty { get; } = new([], "", 0, 50);
    public bool CanGoForward => NextCursor.Length > 0;
    public string PageText => TotalCount == 0
        ? "没有匹配的任务"
        : $"共 {TotalCount} 个任务（本次返回 {Items.Count} 个，每页 {Limit}）" +
          (CanGoForward ? " · 还有下一页" : " · 已到末页");
}

public sealed record TaskFilter(string WorkspaceId, string Status, string Priority, string AgentId, string Cursor, int Limit)
{
    public static TaskFilter Default { get; } = new("", "", "", "", "", 50);
    public string DescribeText
    {
        get
        {
            var parts = new List<string>();
            if (WorkspaceId.Length > 0) parts.Add($"工作区={WorkspaceId}");
            if (Status.Length > 0) parts.Add($"状态={Status}");
            if (Priority.Length > 0) parts.Add($"优先级={Priority}");
            if (AgentId.Length > 0) parts.Add($"Agent={AgentId}");
            if (Cursor.Length > 0) parts.Add("已翻页（游标）");
            return parts.Count == 0 ? "未设置筛选" : string.Join(" · ", parts);
        }
    }
}

public sealed record TaskCreate(string WorkspaceId, string Title, string Description, string Priority);

/// <summary>The lifecycle commands Core supports, with the version each one must carry.</summary>
public enum TaskCommandKind { Assign, RunNow, Cancel, Reopen, Archive, MarkFailed, Resume, Requeue }

public sealed record TaskCommandRequest(
    string WorkspaceId, string TaskId, int ExpectedVersion, string AgentId, string Reason);

public interface ITaskSettings
{
    /// <summary>Keyset paging: forward only via the cursor Core returns.</summary>
    Task<TaskListPage> ListAsync(TaskFilter filter, CancellationToken cancellationToken = default);
    Task CreateAsync(TaskCreate create, CancellationToken cancellationToken = default);
    /// <summary>CAS on the version the page read; a stale one surfaces as SettingsConflictException.</summary>
    Task RunCommandAsync(TaskCommandKind kind, TaskCommandRequest request, CancellationToken cancellationToken = default);
}

public static class TaskText
{
    /// <summary>Mirrors WorkspaceTaskStatus wire values.</summary>
    public static IReadOnlyList<string> Statuses { get; } =
    [
        "Backlog", "Ready", "Deferred", "Reserved", "Assigned", "NeedsReview", "InProgress", "Blocked",
        "Completed", "Failed", "Cancelled", "Archived"
    ];

    /// <summary>Mirrors the priority wire values.</summary>
    public static IReadOnlyList<string> Priorities { get; } = ["p0", "p1", "p2", "p3"];

    public static IReadOnlyList<string> ExecutionWindows { get; } = ["inherit", "anytime", "off_peak_only"];

    public const int MinimumLimit = 1;
    public const int MaximumLimit = 500;

    public const string VersionNotice =
        "所有写操作都带 ExpectedVersion（CAS）：期间被别人改过就报冲突并阻止覆盖，不会静默覆盖。";

    public const string HierarchyNotice =
        "容器母卡保护（fail-closed）：母卡还有未终态子卡时，取消/归档会被拒绝（Core 的 " +
        "task.has_non_terminal_children）；只有显式 force 才级联，本页**不提供** force。";

    public const string CursorNotice =
        "分页是 keyset 游标（不是 offset）：TotalCount 与游标无关，翻页时恒定不变；只能按 Core 返回的游标向前翻。";

    public const string WorkPageNotice =
        "登记范围：本卡是任务管理的**入口**。完整看板/列表、任务详情编辑、评论、评价、事件流与执行命令按卡片行为说明属于**独立原生工作页**，" +
        "目前尚未实现——这里只提供列表、创建与生命周期命令，不假装已有工作页。";

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        "Backlog" => "Backlog（待办池）",
        "Ready" => "Ready（可派发）",
        "Deferred" => "Deferred（延后）",
        "Reserved" => "Reserved（已预留）",
        "Assigned" => "Assigned（已指派）",
        "NeedsReview" => "NeedsReview（待审）",
        "InProgress" => "InProgress（进行中）",
        "Blocked" => "Blocked（阻塞）",
        "Completed" => "Completed（已完成）",
        "Failed" => "Failed（失败）",
        "Cancelled" => "Cancelled（已取消）",
        "Archived" => "Archived（已归档）",
        var value => value
    };

    public static string DescribePriority(string? priority) => priority switch
    {
        null or "" => "优先级未知",
        "p0" => "p0（最高）",
        "p1" => "p1（高）",
        "p2" => "p2（中）",
        "p3" => "p3（默认）",
        var value => value
    };

    public static string DescribeBoardColumn(string? column) => column switch
    {
        null or "" => "无看板列",
        "Backlog" => "Backlog",
        "Todo" => "Todo",
        "InProgress" => "InProgress",
        "Done" => "Done",
        "Failed" => "Failed",
        var value => value
    };

    public static string DescribeExecutionWindow(string? window) => window switch
    {
        null or "" => "执行窗口未知",
        "inherit" => "inherit（继承）",
        "anytime" => "anytime（任意时间）",
        "off_peak_only" => "off_peak_only（仅非高峰）",
        var value => value
    };

    public static string DescribeCommand(TaskCommandKind kind) => kind switch
    {
        TaskCommandKind.Assign => "指派",
        TaskCommandKind.RunNow => "立即执行",
        TaskCommandKind.Cancel => "取消",
        TaskCommandKind.Reopen => "重新打开",
        TaskCommandKind.Archive => "归档",
        TaskCommandKind.MarkFailed => "标记失败",
        TaskCommandKind.Resume => "恢复",
        _ => "重新排队"
    };

    /// <summary>Assign/RunNow require an agent id; the others take only a reason.</summary>
    public static bool RequiresAgent(TaskCommandKind kind) =>
        kind is TaskCommandKind.Assign or TaskCommandKind.RunNow;

    public static int ClampLimit(int limit) => Math.Clamp(limit, MinimumLimit, MaximumLimit);

    public static TaskFilter Normalize(TaskFilter filter) => filter with
    {
        WorkspaceId = filter.WorkspaceId.Trim(),
        Status = filter.Status.Trim(),
        Priority = filter.Priority.Trim(),
        AgentId = filter.AgentId.Trim(),
        Cursor = filter.Cursor.Trim(),
        Limit = ClampLimit(filter.Limit),
    };

    public static IReadOnlyList<string> Validate(TaskFilter filter)
    {
        var errors = new List<string>();
        // 工作区是 Core 查询的必填项。
        if (filter.WorkspaceId.Length == 0) errors.Add("必须选择工作区（Core 的任务查询按工作区必填）。");
        if (filter.Limit is < MinimumLimit or > MaximumLimit)
            errors.Add($"每页条数必须在 {MinimumLimit}–{MaximumLimit} 之间。");
        if (filter.Status.Length > 0 && !Statuses.Contains(filter.Status, StringComparer.Ordinal))
            errors.Add($"状态取值不在 Core 的状态列表里：{filter.Status}。");
        if (filter.Priority.Length > 0 && !Priorities.Contains(filter.Priority, StringComparer.Ordinal))
            errors.Add($"优先级必须是 {string.Join(" / ", Priorities)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TaskCreate create)
    {
        var errors = new List<string>();
        if (create.WorkspaceId.Trim().Length == 0) errors.Add("必须选择工作区。");
        if (create.Title.Trim().Length == 0) errors.Add("任务标题不能为空。");
        if (create.Priority.Length > 0 && !Priorities.Contains(create.Priority, StringComparer.Ordinal))
            errors.Add($"优先级必须是 {string.Join(" / ", Priorities)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TaskCommandKind kind, TaskCommandRequest request)
    {
        var errors = new List<string>();
        if (request.WorkspaceId.Trim().Length == 0) errors.Add("缺少工作区。");
        if (request.TaskId.Trim().Length == 0) errors.Add("请先选择任务。");
        if (request.ExpectedVersion < 0) errors.Add("任务版本无效，请刷新后重试。");
        // 指派/立即执行必须给出 Agent；其余命令只接受原因。
        if (RequiresAgent(kind) && request.AgentId.Trim().Length == 0)
            errors.Add($"{DescribeCommand(kind)}必须指定 Agent。");
        return errors;
    }
}
