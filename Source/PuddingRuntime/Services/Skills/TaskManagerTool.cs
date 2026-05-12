using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// TaskManagerTool — Agent Todo 自管理 Skill。
/// 管理 Agent 的任务列表，支持创建、更新状态、列出、删除任务。
/// V1 简化：使用内存 List&lt;TaskItem&gt; 存储，单例模式。
/// PermissionLevel: Medium。
/// </summary>
public sealed class TaskManagerTool : IAgentSkill
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

    public string SkillId => "manage_tasks";
    public string Name => "任务管理";
    public string Description =>
        "管理Agent的任务列表。支持创建、更新状态、列出任务。";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

    public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var operation = request.Parameters.TryGetValue("operation", out var op) ? op.ToLowerInvariant() : "list";

        _logger.LogInformation("[TaskManagerTool] agent={Agent} operation={Op}",
            request.AgentInstanceId, operation);

        try
        {
            var result = operation switch
            {
                "create"        => CreateTask(request),
                "update_status" => UpdateTaskStatus(request),
                "list"          => ListTasks(),
                "delete"        => DeleteTask(request),
                _               => Fail($"Unknown operation: {operation}. Use create / update_status / list / delete."),
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TaskManagerTool] Operation failed agent={Agent} op={Op}",
                request.AgentInstanceId, operation);
            return Task.FromResult(Fail(ex.Message));
        }
    }

    // ── CRUD 操作 ──────────────────────────────────────────────────────

    private SkillResult CreateTask(SkillInvokeRequest request)
    {
        var title = request.Parameters.TryGetValue("title", out var t) && t.Length > 0
            ? t
            : request.Input?.Trim();

        if (string.IsNullOrWhiteSpace(title))
            return Fail("Task title is required. Provide via 'title' parameter or input.");

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
        return Ok(JsonSerializer.Serialize(new { action = "created", task }, JsonOptions));
    }

    private SkillResult UpdateTaskStatus(SkillInvokeRequest request)
    {
        if (!request.Parameters.TryGetValue("task_id", out var idStr) || !int.TryParse(idStr, out var taskId))
            return Fail("task_id is required and must be an integer.");

        if (!request.Parameters.TryGetValue("status", out var status))
            return Fail("status is required: pending / in-progress / completed.");

        var validStatuses = new[] { "pending", "in-progress", "completed" };
        if (!validStatuses.Contains(status))
            return Fail($"Invalid status: {status}. Use: pending / in-progress / completed.");

        TaskItem? task;
        lock (_lock)
        {
            task = _tasks.Find(t => t.Id == taskId);
        }

        if (task == null)
            return Fail($"Task not found: {taskId}");

        task.Status = status;
        _logger.LogInformation("[TaskManagerTool] Updated task id={Id} status={Status}", task.Id, status);
        return Ok(JsonSerializer.Serialize(new { action = "updated", task }, JsonOptions));
    }

    private SkillResult ListTasks()
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

        return Ok(JsonSerializer.Serialize(result, JsonOptions));
    }

    private SkillResult DeleteTask(SkillInvokeRequest request)
    {
        if (!request.Parameters.TryGetValue("task_id", out var idStr) || !int.TryParse(idStr, out var taskId))
            return Fail("task_id is required and must be an integer.");

        lock (_lock)
        {
            var removed = _tasks.RemoveAll(t => t.Id == taskId);
            if (removed == 0)
                return Fail($"Task not found: {taskId}");

            _logger.LogInformation("[TaskManagerTool] Deleted task id={Id}", taskId);
            return Ok(JsonSerializer.Serialize(new { action = "deleted", task_id = taskId }, JsonOptions));
        }
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    private static SkillResult Ok(string output) =>
        new() { Success = true, Output = output, ExitCode = 0 };

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}

/// <summary>任务项数据模型。</summary>
public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
}
