using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Security;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Security;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatform.Controllers.External.V1;

/// <summary>
/// ADR-075 §8: External Task API v1 — 只做协议适配（Policy、Actor/Origin、ETag/If-Match、
/// Idempotency-Key、错误码），业务全部复用 Internal 的 Task 应用服务（TaskCommandService/
/// SqliteWorkspaceTaskStore/TaskStateMachine）。不建立第二状态机；不提供 delete。
/// Actor 固定 access-token:{tokenId}，Origin 固定 external.api，客户端不可覆盖。
/// </summary>
[ApiController]
[Route("api/external/v1/workspaces/{workspaceId}/tasks")]
[ServiceFilter(typeof(ExternalApiGateFilter))]
public class ExternalTaskController(
    SqliteWorkspaceTaskStore store,
    TaskCommandService commands,
    TaskEvaluationStore evaluations,
    ExternalApiIdempotencyStore idempotency,
    ExternalTaskApiOptionsProvider optionsProvider) : ControllerBase
{
    private static readonly JsonSerializerOptions StableJson = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, TaskCommand> CommandWhitelist =
        new Dictionary<string, TaskCommand>(StringComparer.Ordinal)
        {
            ["assign"] = TaskCommand.Assign,
            ["run-now"] = TaskCommand.RunNow,
            ["cancel"] = TaskCommand.Cancel,
            ["reopen"] = TaskCommand.Reopen,
            ["archive"] = TaskCommand.Archive,
            ["mark-failed"] = TaskCommand.MarkFailed,
            ["resume"] = TaskCommand.Resume,
            ["requeue"] = TaskCommand.Requeue,
        };

    // ── read ────────────────────────────────────────────────

    /// <summary>GET /tasks — keyset 分页 + 筛选。scope: tasks.read。</summary>
    [HttpGet]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksRead)]
    public async Task<ActionResult<ExternalTaskPageDto>> List(
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
            return InvalidRequest("limit 必须在 1-500 之间。");

        try
        {
            var results = await store.QueryTasksAsync(new TaskQuery
            {
                WorkspaceId = workspaceId,
                Status = ParseStatus(status),
                AgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId,
                Priority = ParsePriority(priority),
                Cursor = cursor,
                Limit = limit + 1,
            }, ParseBoardColumn(boardColumn), ct);

            var items = results.Take(limit).Select(ToDto).ToList();
            string? nextCursor = null;
            if (results.Count > limit)
            {
                var last = results[limit - 1];
                nextCursor = $"{last.SortOrder}|{last.TaskId}";
            }

            return Ok(new ExternalTaskPageDto { Items = items, NextCursor = nextCursor });
        }
        catch (TaskStoreException ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>GET /tasks/{taskId} — 详情 + ETag。scope: tasks.read。</summary>
    [HttpGet("{taskId}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksRead)]
    public async Task<IActionResult> Get(string workspaceId, string taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(workspaceId, taskId, ct);
        if (task is null)
            return TaskNotFound(taskId);

        Response.Headers.ETag = ETag(task.Version);
        return Ok(ToDto(task));
    }

    // ── write ───────────────────────────────────────────────

    /// <summary>POST /tasks — 创建（→ Backlog）。scope: tasks.write；要求 Idempotency-Key。</summary>
    [HttpPost]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksWrite)]
    public async Task<IActionResult> Create(
        string workspaceId,
        [FromBody] ExternalCreateTaskRequest request,
        CancellationToken ct)
    {
        var gate = await TryClaimIdempotencyAsync(request, ct);
        if (gate.EarlyResult is not null)
            return gate.EarlyResult;

        if (gate.ReplayResourceId is not null)
        {
            var replayed = await store.GetTaskAsync(workspaceId, gate.ReplayResourceId, ct);
            if (replayed is not null)
            {
                Response.Headers.ETag = ETag(replayed.Version);
                return StatusCode(gate.ReplayStatus!.Value, ToDto(replayed));
            }
        }

        try
        {
            var created = await store.CreateTaskAsync(new CreateTaskRequest
            {
                WorkspaceId = workspaceId,
                Title = request.Title,
                Description = request.Description,
                AcceptanceCriteria = request.AcceptanceCriteria,
                Priority = TaskWireMaps.PriorityFromString(request.Priority ?? "p3"),
                ExecutionWindow = TaskWireMaps.ExecutionWindowFromString(request.ExecutionWindow ?? "inherit"),
                PreferredAgentId = request.PreferredAgentId,
                NotBeforeUtc = request.NotBeforeUtc,
                DueAtUtc = request.DueAtUtc,
                SortOrder = request.SortOrder ?? 0,
                Origin = TaskOrigin.ExternalApi,
                CreatedBy = ActorId,
                UpdatedBy = ActorId,
            }, ct);

            await idempotency.CompleteAsync(TokenId, "POST", CanonicalRoute, gate.Key!, 201, created.TaskId, ct);
            Response.Headers.ETag = ETag(created.Version);
            Response.Headers.Location = $"/api/external/v1/workspaces/{workspaceId}/tasks/{created.TaskId}";
            return StatusCode(StatusCodes.Status201Created, ToDto(created));
        }
        catch (TaskStoreException ex)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return ToError(ex);
        }
        catch (Exception)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            throw;
        }
    }

    /// <summary>PATCH /tasks/{taskId} — 只改元数据（CAS）。scope: tasks.write；要求 If-Match。</summary>
    [HttpPatch("{taskId}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksWrite)]
    public async Task<IActionResult> Patch(
        string workspaceId,
        string taskId,
        [FromBody] ExternalPatchTaskRequest request,
        CancellationToken ct)
    {
        if (!TryParseIfMatch(out var expectedVersion, out var parseError))
            return parseError!;

        try
        {
            var updated = await commands.PatchAsync(
                workspaceId,
                taskId,
                expectedVersion,
                request.Title,
                request.Description,
                request.AcceptanceCriteria,
                string.IsNullOrWhiteSpace(request.Priority)
                    ? null
                    : TaskWireMaps.PriorityFromString(request.Priority),
                string.IsNullOrWhiteSpace(request.ExecutionWindow)
                    ? null
                    : TaskWireMaps.ExecutionWindowFromString(request.ExecutionWindow),
                request.PreferredAgentId,
                request.NotBeforeUtc,
                request.DueAtUtc,
                request.SortOrder,
                status: null,
                updatedBy: ActorId,
                ct);

            Response.Headers.ETag = ETag(updated.Version);
            return Ok(ToDto(updated));
        }
        catch (TaskStoreException ex)
        {
            return await ToVersionConflictAsync(ex, workspaceId, taskId, ct);
        }
    }

    /// <summary>GET /tasks/{taskId}/comments。scope: tasks.read。</summary>
    [HttpGet("{taskId}/comments")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksRead)]
    public async Task<IActionResult> ListComments(string workspaceId, string taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(workspaceId, taskId, ct);
        if (task is null)
            return TaskNotFound(taskId);

        var comments = await store.ListCommentsAsync(workspaceId, taskId, ct);
        return Ok(comments.Select(ToDto).ToList());
    }

    /// <summary>POST /tasks/{taskId}/comments — 追加评论。scope: tasks.comment；要求 Idempotency-Key。</summary>
    [HttpPost("{taskId}/comments")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksComment)]
    public async Task<IActionResult> AddComment(
        string workspaceId,
        string taskId,
        [FromBody] ExternalCreateCommentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return InvalidRequest("content 必填。");

        var gate = await TryClaimIdempotencyAsync(request, ct);
        if (gate.EarlyResult is not null)
            return gate.EarlyResult;

        if (gate.ReplayResourceId is not null)
        {
            var task0 = await store.GetTaskAsync(workspaceId, taskId, ct);
            var replayedComment = task0 is null
                ? null
                : (await store.ListCommentsAsync(workspaceId, taskId, ct))
                    .FirstOrDefault(c => c.CommentId == gate.ReplayResourceId);
            if (replayedComment is not null)
                return StatusCode(gate.ReplayStatus!.Value, ToDto(replayedComment));
        }

        try
        {
            var task = await store.GetTaskAsync(workspaceId, taskId, ct);
            if (task is null)
            {
                await ReleaseIdempotencyAsync(gate.Key);
                return TaskNotFound(taskId);
            }

            var comment = await store.AddCommentAsync(
                workspaceId, taskId, TaskCommentAuthorKind.Agent, ActorId, request.Content, ct);

            await idempotency.CompleteAsync(TokenId, "POST", CanonicalRoute, gate.Key!, 201, comment.CommentId, ct);
            return StatusCode(StatusCodes.Status201Created, ToDto(comment));
        }
        catch (TaskStoreException ex)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return ToError(ex);
        }
    }

    /// <summary>GET /tasks/{taskId}/evaluations。scope: tasks.read。</summary>
    [HttpGet("{taskId}/evaluations")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksRead)]
    public async Task<IActionResult> ListEvaluations(string workspaceId, string taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(workspaceId, taskId, ct);
        if (task is null)
            return TaskNotFound(taskId);

        var rows = await evaluations.ListAsync(workspaceId, taskId, ct);
        return Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>POST /tasks/{taskId}/evaluations — 追加结构化评价（不改 Task 状态/version）。scope: tasks.evaluate；要求 Idempotency-Key。</summary>
    [HttpPost("{taskId}/evaluations")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksEvaluate)]
    public async Task<IActionResult> AddEvaluation(
        string workspaceId,
        string taskId,
        [FromBody] ExternalCreateEvaluationRequest request,
        CancellationToken ct)
    {
        TaskEvaluationVerdict verdict;
        try
        {
            verdict = TaskEvaluationStore.VerdictFromString(request.Verdict);
        }
        catch (ArgumentException)
        {
            return InvalidRequest("verdict 只接受 accepted/needs_changes/rejected。");
        }

        var gate = await TryClaimIdempotencyAsync(request, ct);
        if (gate.EarlyResult is not null)
            return gate.EarlyResult;

        if (gate.ReplayResourceId is not null)
        {
            var task0 = await store.GetTaskAsync(workspaceId, taskId, ct);
            var replayedEvaluation = task0 is null
                ? null
                : (await evaluations.ListAsync(workspaceId, taskId, ct))
                    .FirstOrDefault(e => e.EvaluationId == gate.ReplayResourceId);
            if (replayedEvaluation is not null)
                return StatusCode(gate.ReplayStatus!.Value, ToDto(replayedEvaluation));
        }

        try
        {
            var result = await evaluations.AppendAsync(new AppendTaskEvaluationRequest
            {
                WorkspaceId = workspaceId,
                TaskId = taskId,
                Verdict = verdict,
                Score = request.Score,
                Comment = request.Comment ?? string.Empty,
                TaskVersionObserved = request.TaskVersionObserved,
                SupersedesEvaluationId = request.SupersedesEvaluationId,
                EvaluatorType = ExternalAccessTokenDefaults.ActorType,
                EvaluatorId = ActorId,
                EvaluatorDisplayName = TokenName,
            }, ct);

            if (!result.IsOk)
            {
                await ReleaseIdempotencyAsync(gate.Key);
                return result.Error switch
                {
                    TaskEvaluationError.TaskNotFound => TaskNotFound(taskId),
                    TaskEvaluationError.TaskArchived => UnprocessableEntity(Error("external.task_archived",
                        "已归档 Task 不接受新评价。")),
                    TaskEvaluationError.VersionMismatch => UnprocessableEntity(Error(
                        "external.task_version_observed_mismatch",
                        $"taskVersionObserved={request.TaskVersionObserved} 与当前 Task version 不一致，请重新读取。")),
                    TaskEvaluationError.InvalidScore => InvalidRequest("score 必须在 1-5 之间。"),
                    TaskEvaluationError.InvalidComment => InvalidRequest("comment 必填且不超过 4000 字符。"),
                    TaskEvaluationError.InvalidSupersedes => InvalidRequest(
                        "supersedesEvaluationId 必须指向同一 Task 且同一评价者（token actor）的历史评价。"),
                    _ => InvalidRequest("评价请求无效。"),
                };
            }

            var evaluation = result.Value!;
            await idempotency.CompleteAsync(TokenId, "POST", CanonicalRoute, gate.Key!, 201, evaluation.EvaluationId, ct);
            return StatusCode(StatusCodes.Status201Created, ToDto(evaluation));
        }
        catch (TaskStoreException ex)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return ToError(ex);
        }
    }

    /// <summary>POST /tasks/{taskId}/commands/{command} — 状态/执行命令。scope: tasks.command；要求 If-Match + Idempotency-Key。</summary>
    [HttpPost("{taskId}/commands/{command}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalTasksCommand)]
    public async Task<IActionResult> ApplyCommand(
        string workspaceId,
        string taskId,
        string command,
        [FromBody] ExternalCommandRequest? request,
        CancellationToken ct)
    {
        if (!CommandWhitelist.TryGetValue(command, out var taskCommand))
            return InvalidRequest($"command 只接受 {string.Join('/', CommandWhitelist.Keys)}。");

        if (!TryParseIfMatch(out var expectedVersion, out var parseError))
            return parseError!;

        var gate = await TryClaimIdempotencyAsync(request, ct);
        if (gate.EarlyResult is not null)
            return gate.EarlyResult;

        // 命令重放：返回当前 Task 状态（简化重放语义，命令本身不可重复执行）。
        if (gate.ReplayResourceId is not null)
        {
            var replayed = await store.GetTaskAsync(workspaceId, gate.ReplayResourceId, ct);
            if (replayed is not null)
            {
                Response.Headers.ETag = ETag(replayed.Version);
                return Ok(ToDto(replayed));
            }
        }

        try
        {
            var updated = await commands.ApplyCommandAsync(
                workspaceId, taskId, taskCommand, expectedVersion,
                request?.AgentId, request?.WindowDecision, request?.Reason,
                ActorId, ct);

            await idempotency.CompleteAsync(TokenId, "POST", CanonicalRoute, gate.Key!, 200, taskId, ct);
            Response.Headers.ETag = ETag(updated.Version);
            return Ok(ToDto(updated));
        }
        catch (TaskStoreException ex)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return await ToVersionConflictAsync(ex, workspaceId, taskId, ct);
        }
    }

    // ── helpers ─────────────────────────────────────────────

    private string TokenId
        => User.FindFirstValue(ExternalAccessTokenClaimNames.TokenId) ?? string.Empty;

    private string ActorId
        => $"{ExternalAccessTokenDefaults.ActorIdPrefix}{TokenId}";

    private string TokenName
        => User.FindFirstValue(ClaimTypes.Name) ?? "external";

    /// <summary>幂等 key 作用域中的 canonical route。</summary>
    private string CanonicalRoute => Request.Path.Value ?? "/";

    /// <summary>ETag wire: "task-v{version}"（含引号）。</summary>
    private static string ETag(int version) => $"\"task-v{version}\"";

    private bool TryParseIfMatch(out int version, out IActionResult? error)
    {
        version = 0;
        var value = Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = StatusCode(StatusCodes.Status428PreconditionRequired,
                Error("external.precondition_required", "PATCH/Command 必须携带 If-Match: \"task-v{version}\"。"));
            return false;
        }

        var trimmed = value.Trim().Trim('"');
        if (!trimmed.StartsWith("task-v", StringComparison.Ordinal)
            || !int.TryParse(trimmed["task-v".Length..], out version))
        {
            error = InvalidRequest("If-Match 格式必须为 \"task-v{version}\"。");
            return false;
        }

        error = null;
        return true;
    }

    private sealed record IdempotencyGate(
        string? Key,
        int? ReplayStatus,
        string? ReplayResourceId,
        IActionResult? EarlyResult);

    /// <summary>
    /// 认领 Idempotency-Key。请求哈希 = 绑定后请求 DTO 的稳定序列化（同 key 同语义 body → 同哈希；
    /// 同 key 不同 body → 409）。Replay 时调用方按 ResourceId 返回原资源，不得重复执行 mutation。
    /// </summary>
    private async Task<IdempotencyGate> TryClaimIdempotencyAsync(object? requestBody, CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return new IdempotencyGate(null, null, null,
                InvalidRequest("mutation 必须携带 Idempotency-Key（≤128 字符）。"));

        if (key.Length > 128 || key.Any(char.IsControl))
            return new IdempotencyGate(null, null, null,
                InvalidRequest("Idempotency-Key 最长 128 且不允许控制字符。"));

        var requestHashSource = JsonSerializer.Serialize(requestBody ?? new { }, StableJson);
        var retention = TimeSpan.FromDays(Math.Max(1, optionsProvider.Current.IdempotencyRetentionDays));
        var claim = await idempotency.TryClaimAsync(TokenId, "POST", CanonicalRoute, key, requestHashSource, retention, ct);

        return claim.Outcome switch
        {
            ExternalIdempotencyOutcome.Conflict => new IdempotencyGate(key, null, null,
                Conflict(Error("external.idempotency_conflict", "同 Idempotency-Key 已用于不同请求体。"))),
            ExternalIdempotencyOutcome.InProgress => new IdempotencyGate(key, null, null,
                Conflict(Error("external.idempotency_in_progress", "同 Idempotency-Key 请求正在处理中。"))),
            ExternalIdempotencyOutcome.Replay => new IdempotencyGate(key, claim.ResponseStatus, claim.ResourceId, null),
            _ => new IdempotencyGate(key, null, null, null),
        };
    }

    private Task ReleaseIdempotencyAsync(string? key)
        => string.IsNullOrEmpty(key)
            ? Task.CompletedTask
            : idempotency.ReleaseAsync(TokenId, "POST", CanonicalRoute, key, CancellationToken.None);

    private static WorkspaceTaskStatus? ParseStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? null : TaskWireMaps.StatusFromString(status);

    private static TaskPriority? ParsePriority(string? priority)
        => string.IsNullOrWhiteSpace(priority) ? null : TaskWireMaps.PriorityFromString(priority);

    private static IReadOnlyList<WorkspaceTaskStatus>? ParseBoardColumn(string? boardColumn)
        => string.IsNullOrWhiteSpace(boardColumn)
            ? null
            : TaskWireMaps.BoardColumnToStatuses(TaskWireMaps.BoardColumnFromString(boardColumn));

    private static ExternalTaskDto ToDto(WorkspaceTask t) => new()
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
        BoardColumn = t.Status is WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived
            ? TaskWireMaps.StatusToString(t.Status)
            : TaskWireMaps.BoardColumnToString(TaskStateMachine.ProjectBoardColumn(t.Status)),
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

    private static ExternalTaskCommentDto ToDto(TaskComment c) => new()
    {
        CommentId = c.CommentId,
        TaskId = c.TaskId,
        WorkspaceId = c.WorkspaceId,
        AuthorKind = TaskWireMaps.CommentAuthorKindToString(c.AuthorKind),
        AuthorId = c.AuthorId,
        Content = c.Content,
        CreatedAtUtc = c.CreatedAtUtc,
    };

    private static ExternalTaskEvaluationDto ToDto(TaskEvaluation e) => new()
    {
        EvaluationId = e.EvaluationId,
        TaskId = e.TaskId,
        WorkspaceId = e.WorkspaceId,
        Verdict = TaskEvaluationStore.VerdictToString(e.Verdict),
        Score = e.Score,
        Comment = e.Comment,
        TaskVersionObserved = e.TaskVersionObserved,
        SupersedesEvaluationId = e.SupersedesEvaluationId,
        Evaluator = new ExternalEvaluatorDto
        {
            Type = e.EvaluatorType,
            Id = e.EvaluatorId,
            DisplayName = e.EvaluatorDisplayName,
        },
        CreatedAtUtc = e.CreatedAtUtc,
    };

    private NotFoundObjectResult TaskNotFound(string taskId)
        => NotFound(Error("task.not_found", $"Task '{taskId}' not found."));

    private BadRequestObjectResult InvalidRequest(string message)
        => BadRequest(Error("external.invalid_request", message));

    private static ExternalErrorResponse Error(string code, string? message = null) => new()
    {
        Code = code,
        Message = message,
        TraceId = Activity.Current?.Id,
    };

    private ObjectResult ToError(TaskStoreException ex)
    {
        // External 契约（§8.6）：版本冲突固定 412（Internal 为 409）；code 保持 task.version_conflict。
        var status = ex.ErrorCode == TaskErrorCode.TaskVersionConflict
            ? StatusCodes.Status412PreconditionFailed
            : TaskWireMaps.ErrorCodeToHttpStatus(ex.ErrorCode);
        return StatusCode(status, new ExternalErrorResponse
        {
            Code = TaskWireMaps.ErrorCodeToString(ex.ErrorCode),
            Message = ex.Message,
            TraceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            ExpectedVersion = ex.ExpectedVersion,
            ActualVersion = ex.ActualVersion,
        });
    }

    /// <summary>412 响应附当前 ETag 与最小当前 Task 快照，供调用方显式解决冲突。</summary>
    private async Task<ObjectResult> ToVersionConflictAsync(
        TaskStoreException ex,
        string workspaceId,
        string taskId,
        CancellationToken ct)
    {
        var result = ToError(ex);
        if (result.StatusCode == StatusCodes.Status412PreconditionFailed)
        {
            var current = await store.GetTaskAsync(workspaceId, taskId, ct);
            if (current is not null)
            {
                Response.Headers.ETag = ETag(current.Version);
                result = StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    code = TaskWireMaps.ErrorCodeToString(ex.ErrorCode),
                    message = ex.Message,
                    traceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
                    expectedVersion = ex.ExpectedVersion,
                    actualVersion = ex.ActualVersion,
                    currentTask = ToDto(current),
                });
            }
        }

        return result;
    }
}
