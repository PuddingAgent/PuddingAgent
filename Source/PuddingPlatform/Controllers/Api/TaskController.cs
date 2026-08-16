using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Tasks;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// TB-03: WorkspaceTask Control Plane API — 任务查询/CRUD + Command API。
/// 关联 ADR-072 ST-03.1/ST-03.2。ARCH-HARDEN-004：所有端点返回专用 DTO，不直接暴露 EF Entity。
/// </summary>
[ApiController]
[Route("api/workspaces/{workspaceId}/tasks")]
[Authorize]
public class TaskController : ControllerBase
{
    private readonly ITaskStore _store;
    private readonly TaskCommandService _commands;

    public TaskController(ITaskStore store, TaskCommandService commands)
    {
        _store = store;
        _commands = commands;
    }

    /// <summary>GET /api/workspaces/{workspaceId}/tasks — keyset 分页 + 筛选。</summary>
    [HttpGet]
    public async Task<ActionResult<TaskPageDto>> List(
        string workspaceId,
        [FromQuery] string? status,
        [FromQuery] string? agentId,
        [FromQuery] string? priority,
        [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 500)
        {
            return BadRequest(new { message = "limit 必须在 1-500 之间" });
        }

        try
        {
            var statusFilter = ParseStatus(status);
            var priorityFilter = ParsePriority(priority);
            var agentFilter = string.IsNullOrWhiteSpace(agentId) ? null : agentId;

            // 多取一条用于判断是否还有下一页（keyset）。
            var results = await _store.QueryTasksAsync(new TaskQuery
            {
                WorkspaceId = workspaceId,
                Status = statusFilter,
                AgentId = agentFilter,
                Priority = priorityFilter,
                Cursor = cursor,
                Limit = limit + 1,
            }, ct);

            var items = results.Take(limit).Select(ToDto).ToList();
            string? nextCursor = null;
            if (results.Count > limit)
            {
                var last = results[limit - 1];
                nextCursor = $"{last.SortOrder}|{last.TaskId}";
            }

            return Ok(new TaskPageDto { Items = items, NextCursor = nextCursor });
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>POST /api/workspaces/{workspaceId}/tasks — 创建 → Backlog。</summary>
    [HttpPost]
    public async Task<ActionResult<TaskDto>> Create(
        string workspaceId,
        [FromBody] CreateTaskDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var priority = TaskWireMaps.PriorityFromString(dto.Priority ?? "p3");
            var executionWindow = TaskWireMaps.ExecutionWindowFromString(dto.ExecutionWindow ?? "inherit");

            var created = await _store.CreateTaskAsync(new CreateTaskRequest
            {
                WorkspaceId = workspaceId,
                Title = dto.Title,
                Description = dto.Description,
                AcceptanceCriteria = dto.AcceptanceCriteria,
                Priority = priority,
                ExecutionWindow = executionWindow,
                PreferredAgentId = dto.PreferredAgentId,
                NotBeforeUtc = dto.NotBeforeUtc,
                DueAtUtc = dto.DueAtUtc,
                SortOrder = dto.SortOrder ?? 0,
            }, ct);

            return CreatedAtAction(nameof(Get), new { workspaceId, taskId = created.TaskId }, ToDto(created));
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>GET /api/workspaces/{workspaceId}/tasks/{taskId} — 详情。</summary>
    [HttpGet("{taskId}")]
    public async Task<ActionResult<TaskDto>> Get(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        try
        {
            var task = await _store.GetTaskAsync(workspaceId, taskId, ct);
            if (task is null)
            {
                throw new TaskStoreException(
                    TaskErrorCode.TaskNotFound,
                    $"Task '{taskId}' not found.",
                    taskId);
            }

            return Ok(ToDto(task));
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>PATCH /api/workspaces/{workspaceId}/tasks/{taskId} — 更新（CAS）。</summary>
    [HttpPatch("{taskId}")]
    public async Task<ActionResult<TaskDto>> Patch(
        string workspaceId,
        string taskId,
        [FromBody] PatchTaskDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var priority = string.IsNullOrWhiteSpace(dto.Priority)
                ? null
                : (TaskPriority?)TaskWireMaps.PriorityFromString(dto.Priority);
            var executionWindow = string.IsNullOrWhiteSpace(dto.ExecutionWindow)
                ? null
                : (TaskExecutionWindow?)TaskWireMaps.ExecutionWindowFromString(dto.ExecutionWindow);

            var updated = await _commands.PatchAsync(
                workspaceId,
                taskId,
                dto.ExpectedVersion,
                dto.Title,
                dto.Description,
                dto.AcceptanceCriteria,
                priority,
                executionWindow,
                dto.PreferredAgentId,
                dto.NotBeforeUtc,
                dto.DueAtUtc,
                dto.SortOrder,
                ct);

            return Ok(ToDto(updated));
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>DELETE /api/workspaces/{workspaceId}/tasks/{taskId} — 硬删（仅无历史 Backlog）。</summary>
    [HttpDelete("{taskId}")]
    public async Task<IActionResult> Delete(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        try
        {
            var task = await _store.GetTaskAsync(workspaceId, taskId, ct);
            if (task is null)
            {
                throw new TaskStoreException(
                    TaskErrorCode.TaskNotFound,
                    $"Task '{taskId}' not found.",
                    taskId);
            }

            var deleted = await _store.HardDeleteTaskAsync(workspaceId, taskId, ct);
            if (!deleted)
            {
                throw new TaskStoreException(
                    TaskErrorCode.TaskCannotHardDelete,
                    $"Task '{taskId}' cannot be hard-deleted (only history-free Backlog tasks).",
                    taskId);
            }

            return NoContent();
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>POST /tasks/{taskId}/assign — 指派（Ready→Reserved，建 Assignment）。</summary>
    [HttpPost("{taskId}/assign")]
    public Task<ActionResult<TaskDto>> Assign(
        string workspaceId,
        string taskId,
        [FromBody] AssignDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Assign, dto.ExpectedVersion, dto.AgentId, null, null, ct);

    /// <summary>POST /tasks/{taskId}/run-now — 立即执行（Ready/Deferred→Reserved，建 Assignment）。</summary>
    [HttpPost("{taskId}/run-now")]
    public Task<ActionResult<TaskDto>> RunNow(
        string workspaceId,
        string taskId,
        [FromBody] RunNowDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.RunNow, dto.ExpectedVersion, dto.AgentId, dto.WindowDecision, null, ct);

    /// <summary>POST /tasks/{taskId}/cancel — 取消。</summary>
    [HttpPost("{taskId}/cancel")]
    public Task<ActionResult<TaskDto>> Cancel(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Cancel, dto.ExpectedVersion, null, null, dto.Reason, ct);

    /// <summary>POST /tasks/{taskId}/reopen — 重开（Failed→Ready，唯一入口）。</summary>
    [HttpPost("{taskId}/reopen")]
    public Task<ActionResult<TaskDto>> Reopen(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Reopen, dto.ExpectedVersion, null, null, dto.Reason, ct);

    /// <summary>POST /tasks/{taskId}/archive — 归档（Completed/Cancelled/Failed→Archived）。</summary>
    [HttpPost("{taskId}/archive")]
    public Task<ActionResult<TaskDto>> Archive(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Archive, dto.ExpectedVersion, null, null, dto.Reason, ct);

    /// <summary>POST /tasks/{taskId}/mark-failed — 标记失败。</summary>
    [HttpPost("{taskId}/mark-failed")]
    public Task<ActionResult<TaskDto>> MarkFailed(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.MarkFailed, dto.ExpectedVersion, null, null, dto.Reason, ct);

    /// <summary>POST /tasks/{taskId}/resume — 恢复（Blocked/NeedsReview→Ready）。</summary>
    [HttpPost("{taskId}/resume")]
    public Task<ActionResult<TaskDto>> Resume(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Resume, dto.ExpectedVersion, null, null, dto.Reason, ct);

    /// <summary>POST /tasks/{taskId}/requeue — 重新排队（Deferred/Ready→Ready）。</summary>
    [HttpPost("{taskId}/requeue")]
    public Task<ActionResult<TaskDto>> Requeue(
        string workspaceId,
        string taskId,
        [FromBody] CommandDto dto,
        CancellationToken ct = default)
        => ApplyCommandAsync(workspaceId, taskId, TaskCommand.Requeue, dto.ExpectedVersion, null, null, dto.Reason, ct);

    // ── helpers ─────────────────────────────────────────────

    private async Task<ActionResult<TaskDto>> ApplyCommandAsync(
        string workspaceId,
        string taskId,
        TaskCommand command,
        int expectedVersion,
        string? agentId,
        string? windowDecision,
        string? reason,
        CancellationToken ct)
    {
        try
        {
            var result = await _commands.ApplyCommandAsync(
                workspaceId, taskId, command, expectedVersion, agentId, windowDecision, reason, ct);
            return Ok(ToDto(result));
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    private static WorkspaceTaskStatus? ParseStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? null : TaskWireMaps.StatusFromString(status);

    private static TaskPriority? ParsePriority(string? priority)
        => string.IsNullOrWhiteSpace(priority) ? null : TaskWireMaps.PriorityFromString(priority);

    private static TaskDto ToDto(WorkspaceTask t) => new()
    {
        TaskId = t.TaskId,
        WorkspaceId = t.WorkspaceId,
        Title = t.Title,
        Description = t.Description,
        AcceptanceCriteria = t.AcceptanceCriteria,
        Status = TaskWireMaps.StatusToString(t.Status),
        BoardColumn = ToBoardColumn(t.Status),
        Priority = TaskWireMaps.PriorityToString(t.Priority),
        ExecutionWindow = TaskWireMaps.ExecutionWindowToString(t.ExecutionWindow),
        PreferredAgentId = t.PreferredAgentId,
        ActiveAssignmentId = t.ActiveAssignmentId,
        NotBeforeUtc = t.NotBeforeUtc,
        DueAtUtc = t.DueAtUtc,
        NextEligibleAtUtc = t.NextEligibleAtUtc,
        SortOrder = t.SortOrder,
        ProgressPercent = t.ProgressPercent,
        ProgressSummary = t.ProgressSummary,
        BlockerKind = t.BlockerKind,
        BlockerReason = t.BlockerReason,
        FailureCode = t.FailureCode,
        FailureReason = t.FailureReason,
        Version = t.Version,
        CreatedBy = t.CreatedBy,
        UpdatedBy = t.UpdatedBy,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc,
        CompletedAtUtc = t.CompletedAtUtc,
        FailedAtUtc = t.FailedAtUtc,
        ArchivedAtUtc = t.ArchivedAtUtc,
    };

    /// <summary>Cancelled/Archived 不占五列，看板列回退为状态 wire；其余走状态机五列投影。</summary>
    private static string ToBoardColumn(WorkspaceTaskStatus status)
        => status is WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived
            ? TaskWireMaps.StatusToString(status)
            : TaskWireMaps.BoardColumnToString(TaskStateMachine.ProjectBoardColumn(status));

    private ObjectResult ToError(TaskStoreException ex)
    {
        var status = TaskWireMaps.ErrorCodeToHttpStatus(ex.ErrorCode);
        var response = new TaskErrorResponse
        {
            Code = TaskWireMaps.ErrorCodeToString(ex.ErrorCode),
            Message = ex.Message,
            TraceId = HttpContext?.TraceIdentifier ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
            Version = ex.ActualVersion,
            ExpectedVersion = ex.ExpectedVersion,
            ActualVersion = ex.ActualVersion,
        };
        return StatusCode(status, response);
    }
}
