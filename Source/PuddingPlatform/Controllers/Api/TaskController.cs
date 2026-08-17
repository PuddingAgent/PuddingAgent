using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
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
    private readonly SqliteWorkspaceTaskStore _store;
    private readonly TaskCommandService _commands;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public TaskController(
        SqliteWorkspaceTaskStore store,
        TaskCommandService commands,
        IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _store = store;
        _commands = commands;
        _dbFactory = dbFactory;
    }

    /// <summary>GET /api/workspaces/{workspaceId}/tasks — keyset 分页 + 筛选（含 boardColumn 五列过滤）。</summary>
    [HttpGet]
    public async Task<ActionResult<TaskPageDto>> List(
        string workspaceId,
        [FromQuery] string? status,
        [FromQuery] string? boardColumn,
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
            var boardStatuses = string.IsNullOrWhiteSpace(boardColumn)
                ? null
                : TaskWireMaps.BoardColumnToStatuses(TaskWireMaps.BoardColumnFromString(boardColumn));

            // 多取一条用于判断是否还有下一页（keyset）。
            var results = await _store.QueryTasksAsync(new TaskQuery
            {
                WorkspaceId = workspaceId,
                Status = statusFilter,
                AgentId = agentFilter,
                Priority = priorityFilter,
                Cursor = cursor,
                Limit = limit + 1,
            }, boardStatuses, ct);

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
            var actorId = ResolveAuthorId();

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
                Origin = TaskOrigin.Manual,
                CreatedBy = actorId,
                UpdatedBy = actorId,
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

    /// <summary>PATCH /api/workspaces/{workspaceId}/tasks/{taskId} — 更新（CAS）；可选 status 字段为显式状态迁移（B1）。</summary>
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
            var targetStatus = string.IsNullOrWhiteSpace(dto.Status)
                ? null
                : (WorkspaceTaskStatus?)TaskWireMaps.StatusFromString(dto.Status);

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
                targetStatus,
                ResolveAuthorId(),
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

    /// <summary>GET /api/workspaces/{workspaceId}/tasks/{taskId}/comments — 按创建时间升序。</summary>
    [HttpGet("{taskId}/comments")]
    public async Task<ActionResult<IReadOnlyList<TaskCommentDto>>> ListComments(
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

            var comments = await _store.ListCommentsAsync(workspaceId, taskId, ct);
            return Ok(comments.Select(ToCommentDto).ToList());
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>POST /api/workspaces/{workspaceId}/tasks/{taskId}/comments — 新增评论/备注。</summary>
    [HttpPost("{taskId}/comments")]
    public async Task<ActionResult<TaskCommentDto>> AddComment(
        string workspaceId,
        string taskId,
        [FromBody] CreateTaskCommentDto dto,
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

            var authorKind = TaskWireMaps.CommentAuthorKindFromString(dto.AuthorKind);
            var authorId = ResolveAuthorId();

            var comment = await _store.AddCommentAsync(
                workspaceId, taskId, authorKind, authorId, dto.Content, ct);

            return Ok(ToCommentDto(comment));
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

    /// <summary>
    /// GET /tasks/watch — Snapshot + Cursor Watch SSE。
    /// 首帧发送当前 workspace 任务快照，后续按 task_events 全局自增 id 游标推送 task.{eventType} 事件，
    /// 支持 Last-Event-ID 请求头续传（断线重连不丢事件）；每 15s 发一次 : ping 心跳保活。
    /// </summary>
    [HttpGet("watch")]
    public async Task Watch(
        string workspaceId,
        [FromQuery] long? afterId = null,
        CancellationToken ct = default)
    {
        var cursor = ResolveCursor(afterId);
        if (cursor is null || cursor < 0)
        {
            await WriteSseJsonErrorAsync(
                StatusCodes.Status400BadRequest,
                "task.invalid_cursor",
                "afterId 和 Last-Event-ID 必须为非负整数。",
                ct);
            return;
        }

        ConfigureSseResponse(Response);

        try
        {
            // 首帧：当前 workspace 的任务快照（复用 GET /tasks 的 TaskDto 列表）。
            var snapshot = await _store.QueryTasksAsync(
                new TaskQuery { WorkspaceId = workspaceId, Limit = 500 }, ct);
            await WriteSseFrameAsync(
                Response,
                "task.snapshot",
                JsonSerializer.Serialize(snapshot.Select(ToDto).ToList()),
                id: null,
                ct);

            var lastId = cursor.Value;
            var lastHeartbeat = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                var events = await ReadEventsAfterAsync(workspaceId, lastId, ct);
                foreach (var evt in events)
                {
                    var payload = await BuildEventPayloadAsync(workspaceId, evt, ct);
                    await WriteSseFrameAsync(
                        Response,
                        $"task.{TaskWireMaps.EventTypeToString(evt.EventType)}",
                        JsonSerializer.Serialize(payload),
                        evt.Id,
                        ct);
                    lastId = evt.Id;
                }

                if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(15))
                {
                    await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"), ct);
                    await Response.Body.FlushAsync(ct);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端断开（正常退出）。
        }
        catch (IOException)
        {
            // 客户端断开导致的写失败（正常退出）。
        }
    }

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
                workspaceId, taskId, command, expectedVersion, agentId, windowDecision, reason, ResolveAuthorId(), ct);
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

    private long? ResolveCursor(long? afterId)
    {
        if (afterId.HasValue)
        {
            return afterId.Value;
        }

        var value = Request.Headers["Last-Event-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private async Task<IReadOnlyList<TaskEventEntity>> ReadEventsAfterAsync(
        string workspaceId, long afterId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TaskEvents
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.Id > afterId)
            .OrderBy(e => e.Id)
            .Take(500)
            .ToListAsync(ct);
    }

    private async Task<TaskWatchEventDto> BuildEventPayloadAsync(
        string workspaceId, TaskEventEntity evt, CancellationToken ct)
    {
        var task = await _store.GetTaskAsync(workspaceId, evt.TaskId, ct);
        return new TaskWatchEventDto
        {
            Id = evt.Id,
            EventId = evt.EventId,
            TaskId = evt.TaskId,
            WorkspaceId = evt.WorkspaceId,
            Sequence = evt.Sequence,
            EventType = TaskWireMaps.EventTypeToString(evt.EventType),
            CreatedAtUtc = evt.CreatedAtUtc,
            Task = task is null ? null : ToDto(task),
        };
    }

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSseFrameAsync(
        HttpResponse response, string eventName, string data, long? id, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (id.HasValue)
        {
            sb.Append("id: ").Append(id.Value).Append('\n');
        }

        sb.Append("event: ").Append(eventName).Append('\n');
        sb.Append("data: ").Append(data).Append("\n\n");
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await response.Body.FlushAsync(ct);
    }

    private async Task WriteSseJsonErrorAsync(int statusCode, string code, string message, CancellationToken ct)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { code, message }), ct);
    }

    private static TaskDto ToDto(WorkspaceTask t) => new()
    {
        TaskId = t.TaskId,
        WorkspaceId = t.WorkspaceId,
        Title = t.Title,
        Description = t.Description,
        AcceptanceCriteria = t.AcceptanceCriteria,
        Status = TaskWireMaps.StatusToString(t.Status),
        AllowedTransitions = TaskStateMachine
            .GetAllowedTransitions(t.Status)
            .Select(TaskWireMaps.StatusToString)
            .ToList(),
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
        Origin = t.Origin.HasValue ? TaskWireMaps.OriginToString(t.Origin.Value) : null,
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

    private static TaskCommentDto ToCommentDto(TaskComment c) => new()
    {
        CommentId = c.CommentId,
        TaskId = c.TaskId,
        WorkspaceId = c.WorkspaceId,
        AuthorKind = TaskWireMaps.CommentAuthorKindToString(c.AuthorKind),
        AuthorId = c.AuthorId,
        Content = c.Content,
        CreatedAtUtc = c.CreatedAtUtc,
    };

    private string? ResolveAuthorId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

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
