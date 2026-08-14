using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// TaskManagerTool — Agent Todo 自管理工具。
/// 管理 Agent 的任务列表，支持创建、更新状态、列出、删除任务。
/// V2：per-agent JSON 文件持久化存储（agents/{agentInstanceId}/tasks.json），
/// 跨重启保留；不同 AgentInstanceId 的任务完全隔离。
/// PermissionLevel: Medium。
/// </summary>
[Tool(
    id: "manage_tasks",
    name: "任务管理",
    description: "管理 Agent 的任务列表，支持创建、更新状态、列出、删除任务。【何时用】Agent 需要自我规划/跟踪多步任务（Todo 列表）时使用：任务拆解后逐条创建、推进时更新状态、阶段结束前清点遗留。【怎么用】operation=create + title 创建任务；update_status + task_id + status（pending/in-progress/completed）更新状态；list 列出全部（默认）；delete + task_id 删除。【坑】任务持久化到 per-agent 文件 agents/{agentInstanceId}/tasks.json，跨重启保留；不同 agent 相互隔离；update_status/delete 的 task_id 不存在会报错；status 只接受 pending/in-progress/completed。Manage an Agent's task list: create, update_status, list, delete — use to plan and track multi-step work as a Todo list; create needs title, update_status needs task_id+status (pending/in-progress/completed), list is the default, delete needs task_id; tasks persist per agent to agents/{agentInstanceId}/tasks.json (survive restarts, isolated across agents), and invalid task_id/status is rejected.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class TaskManagerTool : PuddingToolBase<TaskManagerArgs>
{
    /// <summary>AgentInstanceId 为空/空白时使用的回退键（保持「匿名调用方共享一份」的旧语义）。</summary>
    private const string DefaultAgentKey = "default";

    private readonly ILogger<TaskManagerTool> _logger;
    private readonly PuddingDataPaths _paths;

    // per-agent 状态：key = agentInstanceId，value = 该 agent 的任务列表 + nextId + 加载标记 + 同步锁。
    private readonly ConcurrentDictionary<string, AgentTaskState> _states = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 磁盘文件读写用选项：camelCase + 忽略 null + 读入大小写不敏感（兼容手改/历史文件）。
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TaskManagerTool(ILogger<TaskManagerTool> logger, PuddingDataPaths paths)
    {
        _logger = logger;
        _paths = paths;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskManagerArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var operation = (args.Operation ?? "list").ToLowerInvariant();
        var agentId = ResolveAgentId(context.AgentInstanceId);

        _logger.LogInformation("[TaskManagerTool] agent={Agent} op={Op}", agentId, operation);

        try
        {
            var result = operation switch
            {
                "create"        => CreateTask(agentId, args),
                "update_status" => UpdateTaskStatus(agentId, args),
                "list"          => ListTasks(agentId),
                "delete"        => DeleteTask(agentId, args),
                _               => ToolExecutionResult.Fail(
                    $"Unknown operation: {operation}. Use create / update_status / list / delete."),
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TaskManagerTool] Operation failed agent={Agent} op={Op}",
                agentId, operation);
            return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
        }
    }

    // ── 路径 / 状态管理 ──────────────────────────────────────────────

    private static string ResolveAgentId(string? agentInstanceId) =>
        string.IsNullOrWhiteSpace(agentInstanceId) ? DefaultAgentKey : agentInstanceId;

    private string TasksFilePath(string agentId) =>
        Path.Combine(_paths.AgentInstanceRoot(agentId), "tasks.json");

    /// <summary>懒加载：首次访问某 agent 时从磁盘加载（文件缺失→空列表 + nextId=1）。</summary>
    private AgentTaskState GetOrLoadState(string agentId)
    {
        var state = _states.GetOrAdd(agentId, static _ => new AgentTaskState());
        lock (state.Sync)
        {
            if (state.Loaded)
                return state;

            state.Loaded = true;
            LoadFromDisk(agentId, state);
        }
        return state;
    }

    /// <summary>从磁盘加载。文件损坏/反序列化失败：记录 warning 并回退空列表，不抛异常、不覆盖原文件。</summary>
    private void LoadFromDisk(string agentId, AgentTaskState state)
    {
        var file = TasksFilePath(agentId);
        if (!File.Exists(file))
            return;

        try
        {
            var json = File.ReadAllText(file);
            var store = JsonSerializer.Deserialize<TaskFileStore>(json, FileJsonOptions);
            if (store is null)
                return;

            state.Tasks = store.Tasks ?? [];
            state.NextId = store.NextId > 0 ? store.NextId : 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TaskManagerTool] Failed to load tasks file {File} for agent {Agent}; falling back to empty list.",
                file, agentId);
            // 保持空列表；不覆盖损坏的原文件（除非后续发生写操作）。
        }
    }

    /// <summary>
    /// 原子写回：先写同目录唯一临时文件，再 File.Move 覆盖。调用方须持有 state.Sync。
    /// 写失败只记 warning，不破坏已加载的内存态。
    /// </summary>
    private void SaveState(string agentId, AgentTaskState state)
    {
        var file = TasksFilePath(agentId);
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(new TaskFileStore
            {
                NextId = state.NextId,
                Tasks = state.Tasks,
            }, FileJsonOptions);

            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TaskManagerTool] Failed to persist tasks for agent {Agent} to {File}; in-memory state kept.",
                agentId, file);
        }
    }

    // ── CRUD 操作 ──────────────────────────────────────────────────────

    private ToolExecutionResult CreateTask(string agentId, TaskManagerArgs args)
    {
        var title = args.Title;
        if (string.IsNullOrWhiteSpace(title))
            return ToolExecutionResult.Fail("Task title is required. Provide via 'title' parameter.");

        var state = GetOrLoadState(agentId);
        TaskItem task;
        lock (state.Sync)
        {
            task = new TaskItem
            {
                Id = state.NextId++,
                Title = title,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
            };
            state.Tasks.Add(task);
            SaveState(agentId, state);
        }

        _logger.LogInformation("[TaskManagerTool] Created task id={Id} title={Title}", task.Id, task.Title);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "created", task }, JsonOptions));
    }

    private ToolExecutionResult UpdateTaskStatus(string agentId, TaskManagerArgs args)
    {
        if (args.TaskId is not { } taskId)
            return ToolExecutionResult.Fail("task_id is required and must be an integer.");

        var status = args.Status;
        if (string.IsNullOrWhiteSpace(status))
            return ToolExecutionResult.Fail("status is required: pending / in-progress / completed.");

        var validStatuses = new[] { "pending", "in-progress", "completed" };
        if (!validStatuses.Contains(status))
            return ToolExecutionResult.Fail($"Invalid status: {status}. Use: pending / in-progress / completed.");

        var state = GetOrLoadState(agentId);
        lock (state.Sync)
        {
            var task = state.Tasks.Find(t => t.Id == taskId);
            if (task == null)
                return ToolExecutionResult.Fail($"Task not found: {taskId}");

            task.Status = status;
            SaveState(agentId, state);
        }

        _logger.LogInformation("[TaskManagerTool] Updated task id={Id} status={Status}", taskId, status);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "updated", task_id = taskId, status }, JsonOptions));
    }

    private ToolExecutionResult ListTasks(string agentId)
    {
        var state = GetOrLoadState(agentId);
        List<TaskItem> snapshot;
        lock (state.Sync)
        {
            snapshot = [.. state.Tasks];
        }

        var result = new
        {
            total = snapshot.Count,
            tasks = snapshot,
        };

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(result, JsonOptions));
    }

    private ToolExecutionResult DeleteTask(string agentId, TaskManagerArgs args)
    {
        if (args.TaskId is not { } taskId)
            return ToolExecutionResult.Fail("task_id is required and must be an integer.");

        var state = GetOrLoadState(agentId);
        lock (state.Sync)
        {
            var removed = state.Tasks.RemoveAll(t => t.Id == taskId);
            if (removed == 0)
                return ToolExecutionResult.Fail($"Task not found: {taskId}");

            SaveState(agentId, state);
            _logger.LogInformation("[TaskManagerTool] Deleted task id={Id}", taskId);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { action = "deleted", task_id = taskId }, JsonOptions));
        }
    }

    /// <summary>单个 agent 的任务状态：任务列表 + 下一个自增 id + 加载标记 + per-agent 同步锁。</summary>
    private sealed class AgentTaskState
    {
        public object Sync { get; } = new();
        public List<TaskItem> Tasks { get; set; } = [];
        public int NextId { get; set; } = 1;
        public bool Loaded { get; set; }
    }

    /// <summary>磁盘文件结构：{ "nextId": N, "tasks": [ TaskItem... ] }。</summary>
    private sealed class TaskFileStore
    {
        public int NextId { get; set; } = 1;
        public List<TaskItem> Tasks { get; set; } = [];
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
