using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// manage_tasks — 管理者视角任务看板交互（跨 Agent 的完整 CRUD + 命令操作）。
/// <para>与执行者视角的 task_list/task_get/task_claim/task_update 互补，无 mine 范围限制。</para>
/// </summary>
[Tool(
    id: "manage_tasks",
    name: "管理工作区任务",
    description: "管理者视角的任务看板交互（跨 Agent 的完整 CRUD + 命令）。【何时用】需要创建任务、查看整个看板、分配任务给 Agent、或执行状态命令（assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue）时使用。【怎么用】action 指定操作：list/create/get/update/delete/assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue；workspace_id 由运行时注入。【坑】与 task_list/task_get/task_claim/task_update 区分：那些是执行者视角（处理自己被派发的任务），本工具是管理者视角（跨 Agent 管理整个看板）。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class ManageTasksTool : PuddingToolBase<ManageTasksArgs>
{
    private readonly IWorkspaceTaskAdminService _service;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public ManageTasksTool(
        IWorkspaceTaskAdminService service,
        IOptions<WorkspaceTaskFeatureOptions> options,
        ILogger<ManageTasksTool> logger)
    {
        _service = service;
        _options = options;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ManageTasksArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!_options.Value.Enabled)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.CapabilityMissing,
                "Workspace task tools are disabled (WorkspaceTasks.Enabled=false)."));
        }

        var action = NormalizeAction(args);

        try
        {
            var workspaceId = context.WorkspaceId;
            var actorId = context.AgentInstanceId;

            switch (action)
            {
                case "list":
                {
                    var result = await _service.ListTasksAsync(new TaskAdminListQuery
                    {
                        WorkspaceId = workspaceId,
                        Status = args.Status,
                        BoardColumn = args.BoardColumn,
                        AgentId = args.AgentId,
                        Priority = args.Priority,
                        Limit = args.Limit ?? 50,
                        Cursor = args.Cursor,
                    }, ct);
                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                case "create":
                {
                    var result = await _service.CreateTaskAsync(new TaskAdminCreateRequest
                    {
                        WorkspaceId = workspaceId,
                        Title = args.Title!,
                        Description = args.Description,
                        AcceptanceCriteria = args.AcceptanceCriteria,
                        Priority = args.Priority,
                        ExecutionWindow = args.ExecutionWindow,
                        PreferredAgentId = args.PreferredAgentId,
                        NotBeforeUtc = ParseUtc(args.NotBeforeUtc, "not_before_utc"),
                        DueAtUtc = ParseUtc(args.DueAtUtc, "due_at_utc"),
                        SortOrder = args.SortOrder,
                        ActorId = actorId,
                    }, ct);
                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                case "get":
                {
                    var result = await _service.GetTaskAsync(workspaceId, args.TaskId!, ct);
                    if (result is null)
                    {
                        return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                            TaskErrorCode.TaskNotFound,
                            $"Task '{args.TaskId}' not found.",
                            args.TaskId));
                    }

                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                case "update":
                {
                    var result = await _service.UpdateTaskAsync(new TaskAdminUpdateRequest
                    {
                        WorkspaceId = workspaceId,
                        TaskId = args.TaskId!,
                        ExpectedVersion = args.ExpectedVersion,
                        Title = args.Title,
                        Description = args.Description,
                        AcceptanceCriteria = args.AcceptanceCriteria,
                        Priority = args.Priority,
                        ExecutionWindow = args.ExecutionWindow,
                        PreferredAgentId = args.PreferredAgentId,
                        Status = args.Status,
                        NotBeforeUtc = ParseUtc(args.NotBeforeUtc, "not_before_utc"),
                        DueAtUtc = ParseUtc(args.DueAtUtc, "due_at_utc"),
                        SortOrder = args.SortOrder,
                        ActorId = actorId,
                    }, ct);
                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                case "delete":
                {
                    var deleted = await _service.DeleteTaskAsync(workspaceId, args.TaskId!, ct);
                    if (!deleted)
                    {
                        return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                            TaskErrorCode.TaskCannotHardDelete,
                            $"Task '{args.TaskId}' cannot be hard-deleted (only history-free Backlog tasks can be deleted).",
                            args.TaskId));
                    }

                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(deleted));
                }
                case "assign":
                case "run_now":
                case "cancel":
                case "reopen":
                case "archive":
                case "mark_failed":
                case "resume":
                case "requeue":
                {
                    var result = await _service.ApplyCommandAsync(new TaskAdminCommandRequest
                    {
                        WorkspaceId = workspaceId,
                        TaskId = args.TaskId!,
                        Command = action,
                        ExpectedVersion = args.ExpectedVersion,
                        AgentId = args.AgentId,
                        WindowDecision = args.WindowDecision,
                        Reason = args.Reason,
                        ActorId = actorId,
                    }, ct);
                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                default:
                    return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                        TaskErrorCode.TaskInvalidTransition,
                        $"unknown action '{action}'"));
            }
        }
        catch (TaskStoreException ex)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(ex));
        }
    }

    private static string NormalizeAction(ManageTasksArgs args)
    {
        var action = args.Action ?? args.Command;
        return string.IsNullOrWhiteSpace(action) ? string.Empty : action.Trim().ToLowerInvariant();
    }

    private static DateTimeOffset? ParseUtc(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"{field} must be a valid ISO8601 timestamp, got '{value}'.");
    }
}

/// <summary>manage_tasks 参数。</summary>
public sealed record ManageTasksArgs
{
    [ToolParam("操作：list/create/get/update/delete/assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue")]
    public string? Action { get; init; }

    // —— list ——
    [ToolParam("wire Status 过滤（Backlog/Ready/Deferred/Reserved/Assigned/NeedsReview/InProgress/Blocked/Completed/Failed/Cancelled/Archived），与 board_column 互斥；update 时表示显式目标状态")]
    public string? Status { get; init; }

    [ToolParam("wire BoardColumn 过滤（Backlog/Todo/InProgress/Done/Failed），与 status 互斥")]
    public string? BoardColumn { get; init; }

    [ToolParam("按 Agent ID 过滤（跨 Agent 视图）；assign/run_now 时表示目标 Agent")]
    public string? AgentId { get; init; }

    [ToolParam("wire Priority 过滤 / 任务优先级（p0/p1/p2/p3，create 默认 p3）")]
    public string? Priority { get; init; }

    [ToolParam("分页大小 1..100，默认 50")]
    public int? Limit { get; init; }

    [ToolParam("keyset 游标 {sortOrder}|{taskId}")]
    public string? Cursor { get; init; }

    // —— create/get/update/delete ——
    [ToolParam("任务 ID（get/update/delete/命令操作必填）")]
    public string? TaskId { get; init; }

    [ToolParam("任务标题（create 必填）")]
    public string? Title { get; init; }

    [ToolParam("任务描述")]
    public string? Description { get; init; }

    [ToolParam("验收标准")]
    public string? AcceptanceCriteria { get; init; }

    [ToolParam("wire 执行窗口 inherit/anytime/off_peak_only（create 默认 inherit）")]
    public string? ExecutionWindow { get; init; }

    [ToolParam("偏好 Agent ID")]
    public string? PreferredAgentId { get; init; }

    [ToolParam("最早可执行时间 ISO8601")]
    public string? NotBeforeUtc { get; init; }

    [ToolParam("截止时间 ISO8601")]
    public string? DueAtUtc { get; init; }

    [ToolParam("排序序号")]
    public long? SortOrder { get; init; }

    [ToolParam("期望版本（update/命令操作 CAS，缺省用当前版本）")]
    public int? ExpectedVersion { get; init; }

    // —— 命令操作 ——
    [ToolParam("命令：assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue（等价于 action，二选一）")]
    public string? Command { get; init; }

    [ToolParam("run_now 的窗口决策")]
    public string? WindowDecision { get; init; }

    [ToolParam("cancel/reopen/archive/mark_failed/resume/requeue 的原因")]
    public string? Reason { get; init; }
}
