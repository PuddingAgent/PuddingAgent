using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// TaskManagerTool — Agent Todo 自管理工具。
/// 管理 Agent 的任务列表，支持创建、更新状态、列出、删除任务。
/// V1 简化：使用内存 List<TaskItem> 存储，单例模式。
/// PermissionLevel: Medium。
/// </summary>
[Tool(
    id: "manage_tasks",
    name: "任务管理",
    description: "管理 Agent 的任务列表，支持创建、更新状态、列出、删除任务。【何时用】Agent 需要自我规划/跟踪多步任务（Todo 列表）时使用：任务拆解后逐条创建、推进时更新状态、阶段结束前清点遗留。【怎么用】operation=create + title 创建任务；update_status + task_id + status（pending/in-progress/completed）更新状态；list 列出全部（默认）；delete + task_id 删除。【坑】任务存储在进程内内存（单例），重启即丢失，重要任务应同步到 goal.md 或记忆库；update_status/delete 的 task_id 不存在会报错；status 只接受 pending/in-progress/completed。Manage an Agent's task list: create, update_status, list, delete — use to plan and track multi-step work as a Todo list; create needs title, update_status needs task_id+status (pending/in-progress/completed), list is the default, delete needs task_id; tasks are in-memory only and lost on restart, and invalid task_id/status is rejected.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class TaskManagerTool : PuddingToolBase<TaskManagerArgs>
{
    private readonly ILogger<TaskManagerTool> _logger;
    private readonly List<TaskItem> _tasks = [];
    private readonly object _lock = new();
    private int _nextId = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TaskManagerTool(ILogger<TaskManagerTool> logger)
    {
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskManagerArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var operation = (args.Operation ?? "list").ToLowerInvariant();

        _logger.LogInformation("[TaskManagerTool] agent={Agent} operation={Op}",
            context.AgentInstanceId, operation);

        try
        {
            var result = operation switch
            {
                "create"        => CreateTask(args),
                "update_status" => UpdateTaskStatus(args),
                "list"          => ListTasks(),
                "delete"        => DeleteTask(args),
                _               => ToolExecutionResult.Fail(
                    $"Unknown operation: {operation}. Use create / update_status / list / delete."),
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TaskManagerTool] Operation failed agent={Agent} op={Op}",
                context.AgentInstanceId, operation);
            return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
        }
    }

    // ── CRUD 操作 ──────────────────────────────────────────────────────

    private ToolExecutionResult CreateTask(TaskManagerArgs args)
    {
        var title = args.Title;
        if (string.IsNullOrWhiteSpace(title))
            return ToolExecutionResult.Fail("Task title is required. Provide via 'title' parameter.");

        var task = new TaskItem
        {
            Id = Interlocked.Increment(ref _nextId) - 1,
            Title = title,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };

        lock (_lock)
        {
            _tasks.Add(task);
        }

        _logger.LogInformation("[TaskManagerTool] Created task id={Id} title={Title}", task.Id, task.Title);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "created", task }, JsonOptions));
    }

    private ToolExecutionResult UpdateTaskStatus(TaskManagerArgs args)
    {
        if (args.TaskId is not { } taskId)
            return ToolExecutionResult.Fail("task_id is required and must be an integer.");

        var status = args.Status;
        if (string.IsNullOrWhiteSpace(status))
            return ToolExecutionResult.Fail("status is required: pending / in-progress / completed.");

        var validStatuses = new[] { "pending", "in-progress", "completed" };
        if (!validStatuses.Contains(status))
            return ToolExecutionResult.Fail($"Invalid status: {status}. Use: pending / in-progress / completed.");

        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null)
                return ToolExecutionResult.Fail($"Task not found: {taskId}");

            task.Status = status;
        }

        _logger.LogInformation("[TaskManagerTool] Updated task id={Id} status={Status}", taskId, status);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "updated", task_id = taskId, status }, JsonOptions));
    }

    private ToolExecutionResult ListTasks()
    {
        List<TaskItem> snapshot;
        lock (_lock)
        {
            snapshot = [.. _tasks];
        }

        var result = new
        {
            total = snapshot.Count,
            tasks = snapshot,
        };

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(result, JsonOptions));
    }

    private ToolExecutionResult DeleteTask(TaskManagerArgs args)
    {
        if (args.TaskId is not { } taskId)
            return ToolExecutionResult.Fail("task_id is required and must be an integer.");

        lock (_lock)
        {
            var removed = _tasks.RemoveAll(t => t.Id == taskId);
            if (removed == 0)
                return ToolExecutionResult.Fail($"Task not found: {taskId}");

            _logger.LogInformation("[TaskManagerTool] Deleted task id={Id}", taskId);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "deleted", task_id = taskId }, JsonOptions));
        }
    }
}

/// <summary>任务项数据模型。</summary>
public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
}

public sealed record TaskManagerArgs
{
    [ToolParam("Operation to run: create, update_status, list, or delete. Default: list.")]
    public string? Operation { get; init; }

    [ToolParam("Task id for update_status and delete operations.")]
    public int? TaskId { get; init; }

    [ToolParam("Task title for create operations.")]
    public string? Title { get; init; }

    [ToolParam("Task status for update_status: pending, in-progress, or completed.")]
    public string? Status { get; init; }
}
