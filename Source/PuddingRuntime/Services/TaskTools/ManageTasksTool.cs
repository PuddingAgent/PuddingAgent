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
    description: "管理者视角的任务看板交互（跨 Agent 的完整 CRUD + 命令）。【何时用】需要创建任务、查看整个看板、分配任务给 Agent、或执行状态命令（assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue）时使用；需要批量收敛（如清理长期滞留的已交付/作废卡）时用 bulk_cancel+status。<【怎么用】action 指定操作：list/create/get/update/delete/assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue/bulk_cancel；workspace_id 由运行时注入。bulk_cancel 的 status 接受逗号分隔的 wire 状态名，逐张走状态机校验，返回计数而非整卡。【坑】与 task_list/task_get/task_claim/task_update 区分：那些是执行者视角（处理自己被派发的任务），本工具是管理者视角（跨 Agent 管理整个看板）。bulk_cancel 不可逆覆盖全部匹配卡，必预写 reason。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定：task 看板元数据（delete 受 TaskCannotHardDelete 保护仅删无历史 Backlog 任务）（用户原则：仅直接损坏/泄露用户数据需门禁）
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
                        // Stage 2（D2/D5）：children_of 按母卡过滤；include_child_summary 附带只读子卡计数（D3）。
                        ParentTaskId = args.ChildrenOf,
                        IncludeChildSummary = args.IncludeChildSummary ?? false,
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
                        TaskType = args.TaskType,
                        NotBeforeUtc = ParseUtc(args.NotBeforeUtc, "not_before_utc"),
                        DueAtUtc = ParseUtc(args.DueAtUtc, "due_at_utc"),
                        SortOrder = args.SortOrder,
                        ParentTaskId = args.ParentTaskId,
                        DependsOnTaskIds = args.DependsOnTaskIds,
                        ActorId = actorId,
                    }, ct);
                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
                }
                case "get":
                {
                    var result = await _service.GetTaskAsync(workspaceId, args.TaskId!, args.IncludeChildren ?? false, ct);
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
                        TaskType = args.TaskType,
                        Status = args.Status,
                        NotBeforeUtc = ParseUtc(args.NotBeforeUtc, "not_before_utc"),
                        DueAtUtc = ParseUtc(args.DueAtUtc, "due_at_utc"),
                        SortOrder = args.SortOrder,
                        ParentTaskId = args.ParentTaskId,
                        ClearParent = args.ClearParent ?? false,
                        DependsOnTaskIds = args.DependsOnTaskIds,
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
                // 看板卡 2a92b3ed / 用户 2026-09-19 指令：批量收敛。
                // 实现要点：① 每张卡仍走 ApplyCommandAsync，状态机逐卡校验，不绕过不变量；
                // ② 不用游标分页 —— 取消会改变过滤集，keyset 游标会跳过未处理项，因此反复取
                //    「该状态的首页」直到集合为空；③ 返回紧凑计数而非整卡 JSON（否则一次批量
                //    会向上下文灌入数百 KB 无价值数据）。
                case "bulk_cancel":
                {
                    var statusWires = (args.Status ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (statusWires.Length == 0)
                    {
                        return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                            TaskErrorCode.TaskInvalidTransition,
                            "bulk_cancel requires status=<wire status list>, e.g. status=Backlog,Ready,NeedsReview."));
                    }

                    var cancelledIds = new List<string>();
                    var failures = new List<string>();

                    foreach (var statusWire in statusWires)
                    {
                        // 安全阀：单状态最多 200 轮，每轮最多 100 张。
                        for (var round = 0; round < 200; round++)
                        {
                            var page = await _service.ListTasksAsync(new TaskAdminListQuery
                            {
                                WorkspaceId = workspaceId,
                                Status = statusWire,
                                Limit = 100,
                            }, ct);

                            if (page.Items.Count == 0)
                            {
                                break;
                            }

                            var progressed = false;
                            foreach (var item in page.Items)
                            {
                                try
                                {
                                    await _service.ApplyCommandAsync(new TaskAdminCommandRequest
                                    {
                                        WorkspaceId = workspaceId,
                                        TaskId = item.TaskId,
                                        Command = "cancel",
                                        ExpectedVersion = item.Version,
                                        Reason = args.Reason,
                                        Force = args.Force ?? false,
                                        ActorId = actorId,
                                    }, ct);
                                    cancelledIds.Add(item.TaskId);
                                    progressed = true;
                                }
                                catch (TaskStoreException ex)
                                {
                                    failures.Add(TaskToolErrors.BuildErrorJson(ex));
                                }
                            }

                            // 整轮零进展即退出，避免不可取消项造成死循环。
                            if (!progressed)
                            {
                                break;
                            }
                        }
                    }

                    return ToolExecutionResult.Ok(TaskToolJson.Serialize(new
                    {
                        cancelled_count = cancelledIds.Count,
                        cancelled_task_ids = cancelledIds,
                        failed_count = failures.Count,
                        failures,
                    }));
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
                        Force = args.Force ?? false,
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
    [ToolParam("操作：list/create/get/update/delete/assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue/bulk_cancel")]
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

    // —— Stage 2：母/子层级（D1/D2/D3/D4/D5）——
    [ToolParam("母卡任务 ID（list 时等价于 children_of：只列该母卡的子卡）")]
    public string? ChildrenOf { get; init; }

    [ToolParam("list 时是否附带只读子卡摘要（child_count / completed_child_count / is_container）；默认 false")]
    public bool? IncludeChildSummary { get; init; }

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

    [ToolParam("任务类型（create/update；不传=general；小写归一，≤64 字符，超长报结构化错误）")]
    public string? TaskType { get; init; }

    [ToolParam("最早可执行时间 ISO8601")]
    public string? NotBeforeUtc { get; init; }

    [ToolParam("截止时间 ISO8601")]
    public string? DueAtUtc { get; init; }

    [ToolParam("排序序号")]
    public long? SortOrder { get; init; }

    [ToolParam("期望版本（update/命令操作 CAS，缺省用当前版本）")]
    public int? ExpectedVersion { get; init; }

    [ToolParam("父任务 ID（create/update 设置或改挂母卡；单层，仅管理者可写）")]
    public string? ParentTaskId { get; init; }

    [ToolParam("update 时显式清除父关系（脱挂为顶层；与 parent_task_id 互斥；不传 = 不变更）")]
    public bool? ClearParent { get; init; }

    [ToolParam("create/update 时建立看板卡依赖（本卡为后继，数组为前置任务 ID；幂等；自依赖/成环/前置缺失 fail-closed 报错）")]
    public IReadOnlyList<string>? DependsOnTaskIds { get; init; }

    [ToolParam("get 时是否内联直接子卡列表（children）；默认 false")]
    public bool? IncludeChildren { get; init; }

    // —— 命令操作 ——
    [ToolParam("命令：assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue（等价于 action，二选一）")]
    public string? Command { get; init; }

    [ToolParam("run_now 的窗口决策")]
    public string? WindowDecision { get; init; }

    [ToolParam("cancel/reopen/archive/mark_failed/resume/requeue 的原因")]
    public string? Reason { get; init; }

    [ToolParam("archive/cancel 时显式级联：先取消未终态子卡再归档/取消母卡（默认 false 则 fail-closed 拒结）")]
    public bool? Force { get; init; }
}
